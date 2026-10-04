using System.Diagnostics;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Npgsql;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// The shared sink contract run against the local pgvector container (deploy/engines/pgvector.compose.yaml),
/// plus the checks that are specific to this engine: the default search really goes through the
/// HNSW index while the exact search does not, a replaced chunk loses its old payload, long
/// pipeline names do not collide, NUL characters are survived, and too many dimensions are refused.
/// </summary>
[Trait( "Category", "Live" )]
public sealed class PgVectorSinkTests : SinkContractTests
{
   #region Data Members

   private const int DIMENSION = 64;

   #endregion Data Members

   #region Overrides

   /// <inheritdoc />
   protected override ISink CreateSink()
   {
      return new PgVectorSink();
   }

   #endregion Overrides

   #region Public Methods

   /// <summary>
   /// The default search must use the HNSW index and the exact search must not. Read from
   /// PostgreSQL's own index-scan counter, which is flushed from the connection a moment after the
   /// query finishes, so the test waits for it. Exact searches run first, then one default search:
   /// the counter must end at exactly 1, so none of the exact searches touched the index.
   /// Why this matters: a planner that quietly picked a sequential scan for the default search
   /// would make the "approximate" numbers in the benchmark exact ones.
   /// </summary>
   [Fact]
   public async Task DefaultSearchUsesHnswIndex_ExactSearchDoesNot()
   {
      string collection = NewCollection();
      using var sink = new PgVectorSink();
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         List<VectorRecord> records = MakeRecords( 300, "idx" );
         await sink.UpsertAsync( collection, records, CancellationToken.None );
         for( int i = 0; i < 3; i++ )
         {
            await sink.SearchExactAsync( collection, records[i].Vector, 5, CancellationToken.None );
         }

         await sink.SearchAsync( collection, records[5].Vector, 5, CancellationToken.None );
         string index = $"gvb_{collection}_hnsw";
         long scans = 0;
         for( int wait = 0; wait < 60 && scans < 1; wait++ )
         {
            await Task.Delay( 500 );
            scans = await IndexScansAsync( index );
         }

         await Task.Delay( 2000 );
         Assert.Equal( 1, await IndexScansAsync( index ) );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// Upserting an id that exists replaces its text and its metadata instead of adding a row.
   /// </summary>
   [Fact]
   public async Task Upsert_SameId_ReplacesPayload()
   {
      string collection = NewCollection();
      using var sink = new PgVectorSink();
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         VectorRecord original = MakeRecords( 1, "replace" )[0];
         var changedChunk = original.Chunk with { Text = "replaced text" };
         var changed = original with { Chunk = changedChunk };
         await sink.UpsertAsync( collection, new[] { original }, CancellationToken.None );
         await sink.UpsertAsync( collection, new[] { changed }, CancellationToken.None );
         Assert.Equal( 1, await sink.CountAsync( collection, CancellationToken.None ) );
         IReadOnlyList<SearchHit> hits = await sink.SearchExactAsync( collection, original.Vector, 3, CancellationToken.None );
         Assert.Single( hits );
         Assert.Equal( "replaced text", hits[0].Text );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// Two 60-character pipeline names that differ only at the end must get separate tables, even
   /// though PostgreSQL would cut both identifiers to the same 63 bytes.
   /// </summary>
   [Fact]
   public async Task LongPipelineNames_DoNotCollide()
   {
      string stem = new( 'a', 58 );
      string first = stem + "_1";
      string second = stem + "_2";
      using var sink = new PgVectorSink();
      try
      {
         Assert.True( await sink.EnsureCollectionAsync( first, DIMENSION, CancellationToken.None ) );
         Assert.True( await sink.EnsureCollectionAsync( second, DIMENSION, CancellationToken.None ) );
         await sink.UpsertAsync( first, MakeRecords( 3, "long1" ), CancellationToken.None );
         await sink.UpsertAsync( second, MakeRecords( 5, "long2" ), CancellationToken.None );
         Assert.Equal( 3, await sink.CountAsync( first, CancellationToken.None ) );
         Assert.Equal( 5, await sink.CountAsync( second, CancellationToken.None ) );
      }
      finally
      {
         await sink.DropCollectionAsync( first, CancellationToken.None );
         await sink.DropCollectionAsync( second, CancellationToken.None );
      }
   }

   /// <summary>
   /// Text and metadata containing a NUL character are stored (without it) instead of failing the
   /// whole batch, because PostgreSQL cannot hold NUL in text or jsonb.
   /// </summary>
   [Fact]
   public async Task NulCharacters_AreStrippedNotFatal()
   {
      string collection = NewCollection();
      using var sink = new PgVectorSink();
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         VectorRecord record = MakeRecords( 1, "nul" )[0];
         var document = record.Document with { Metadata = new Dictionary<string, string> { ["k"] = "a\0b" } };
         var dirty = new VectorRecord( document, record.Chunk with { Text = "he\0llo" }, record.Vector );
         await sink.UpsertAsync( collection, new[] { dirty }, CancellationToken.None );
         IReadOnlyList<SearchHit> hits = await sink.SearchExactAsync( collection, record.Vector, 1, CancellationToken.None );
         Assert.Equal( "hello", hits[0].Text );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// More dimensions than an HNSW index can hold is refused in plain words, and no table is left behind.
   /// </summary>
   [Fact]
   public async Task Ensure_RefusesMoreThan2000Dimensions()
   {
      string collection = NewCollection();
      using var sink = new PgVectorSink();
      try
      {
         var ex = await Assert.ThrowsAsync<InvalidOperationException>( () => sink.EnsureCollectionAsync( collection, 2001, CancellationToken.None ) );
         Assert.Contains( "2000", ex.Message );
         Assert.True( await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None ), "no table should exist after the refusal" );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A throwaway collection name.
   /// </summary>
   /// <returns>A unique name.</returns>
   private static string NewCollection()
   {
      return "gvbpg_" + Guid.NewGuid().ToString( "N" )[..10];
   }

   /// <summary>
   /// Reads how many index scans PostgreSQL has counted for an index, or 0 when it has no row yet.
   /// </summary>
   /// <param name="index">Index name.</param>
   /// <returns>The scan count.</returns>
   private static async Task<long> IndexScansAsync( string index )
   {
      PgVectorSinkOptions options = PgVectorSinkOptions.LocalDefaults();
      var connectionString = new NpgsqlConnectionStringBuilder
      {
         Host = options.Host,
         Port = options.Port,
         Database = options.Database,
         Username = options.User,
         Password = options.Password,
         Pooling = false,
      };
      await using var connection = new NpgsqlConnection( connectionString.ConnectionString );
      await connection.OpenAsync();
      await using var command = new NpgsqlCommand( "SELECT coalesce( sum( idx_scan ), 0 ) FROM pg_stat_user_indexes WHERE indexrelname = @name", connection );
      command.Parameters.AddWithValue( "name", index );
      return Convert.ToInt64( await command.ExecuteScalarAsync() );
   }

   /// <summary>
   /// Deterministic normalized random records of the test dimension with distinct payloads.
   /// </summary>
   /// <param name="count">How many.</param>
   /// <param name="seedName">Text that seeds the generator, so each test gets its own data.</param>
   /// <returns>The records.</returns>
   private static List<VectorRecord> MakeRecords( int count, string seedName )
   {
      var random = new Random( seedName.GetHashCode( StringComparison.Ordinal ) );
      var records = new List<VectorRecord>( count );
      for( int i = 0; i < count; i++ )
      {
         float[] v = Enumerable.Range( 0, DIMENSION ).Select( _ => (float)( random.NextDouble() * 2 - 1 ) ).ToArray();
         float norm = MathF.Sqrt( v.Sum( x => x * x ) );
         v = v.Select( x => x / norm ).ToArray();
         var document = new Document( $"doc|{seedName}|{i}", $"table{i % 3}", $"file{i % 7}.csv", $"text {i}", $"hash{i}", new Dictionary<string, string> { ["row"] = i.ToString() } );
         records.Add( new VectorRecord( document, new Chunk( Guid.NewGuid(), document.DocKey, 0, $"chunk text {seedName}-{i}" ), v ) );
      }

      return records;
   }

   #endregion Private Methods
}

/// <summary>
/// Loads 100,000 random normalized 1024-dimension vectors into PostgreSQL with pgvector and times the load, the
/// count and 20 searches, then repeats the default search at other hnsw.ef_search values. Only correctness is asserted; the timings are printed for the report.
/// Why random vectors: they are the hardest case for an approximate index (no clusters to
/// exploit), so the recall printed here is a floor for real embeddings.
/// </summary>
[Trait( "Category", "Scale" )]
public sealed class PgVectorSinkScaleTests
{
   #region Data Members

   private const int DIMENSION = 1024;
   private const int TOTAL = 100000;
   private const int BATCH = 1000;
   private const int QUERIES = 20;
   private const int EXACT_QUERIES = 10;
   private const int TOP = 10;

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xunit's output sink.
   /// </summary>
   /// <param name="output">Where the timings are printed.</param>
   public PgVectorSinkScaleTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Load, count, then search 100,000 vectors and print the timings.
   /// </summary>
   [Fact]
   public async Task Load100k_CountAndSearch()
   {
      string collection = "gvbscale_" + Guid.NewGuid().ToString( "N" )[..10];
      using var sink = new PgVectorSink();
      try
      {
         var random = new Random( 2026 );
         float[][] vectors = new float[TOTAL][];
         var ids = new Guid[TOTAL];
         var load = new Stopwatch();
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         for( int start = 0; start < TOTAL; start += BATCH )
         {
            List<VectorRecord> batch = MakeBatch( random, start, vectors, ids );
            load.Start();
            await sink.UpsertAsync( collection, batch, CancellationToken.None );
            load.Stop();
         }

         var count = Stopwatch.StartNew();
         long stored = await sink.CountAsync( collection, CancellationToken.None );
         count.Stop();
         _output.WriteLine( $"{sink.Engine}: {sink.IndexDescription}" );
         _output.WriteLine( $"load {TOTAL} x {DIMENSION}: upsert {load.Elapsed.TotalSeconds:F1} s, count/flush {count.Elapsed.TotalSeconds:F1} s, stored {stored}" );
         Assert.Equal( TOTAL, stored );

         await MeasureAsync( "default", random, vectors, ids, ( v, ct ) => sink.SearchAsync( collection, v, TOP, ct ), QUERIES, requireSelfMatch: false );
         foreach( int efSearch in new[] { 40, 400, 1000 } )
         {
            PgVectorSinkOptions options = PgVectorSinkOptions.LocalDefaults();
            options.HnswEfSearch = efSearch;
            using var tuned = new PgVectorSink( options );
            await MeasureAsync( $"ef_search {efSearch}", random, vectors, ids, ( v, ct ) => tuned.SearchAsync( collection, v, TOP, ct ), QUERIES, requireSelfMatch: false );
         }

         await MeasureAsync( "exact", random, vectors, ids, ( v, ct ) => sink.SearchExactAsync( collection, v, TOP, ct ), EXACT_QUERIES, requireSelfMatch: true );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Times fresh random queries and prints the median, 95th percentile and recall against an
   /// in-memory brute force. What is asserted is correctness, not recall: the right number of
   /// hits, sorted best first, and every score equal to the true cosine similarity of that hit
   /// (which proves the distance-to-similarity conversion). An exact search must also find a
   /// stored vector's own record first and match the brute force exactly. An approximate search
   /// is not held to either, because on random vectors a graph index legitimately misses.
   /// </summary>
   /// <param name="label">Name of the search mode.</param>
   /// <param name="random">Random source for the fresh queries.</param>
   /// <param name="vectors">Every stored vector.</param>
   /// <param name="ids">Chunk id of each stored vector.</param>
   /// <param name="search">The search under test.</param>
   /// <param name="queries">How many fresh queries to time.</param>
   /// <param name="requireSelfMatch">True for an exact search: self-match must be 5 of 5 and recall 1.0.</param>
   private async Task MeasureAsync( string label, Random random, float[][] vectors, Guid[] ids,
      Func<float[], CancellationToken, Task<IReadOnlyList<SearchHit>>> search, int queries, bool requireSelfMatch )
   {
      Dictionary<Guid, int> position = Enumerable.Range( 0, ids.Length ).ToDictionary( i => ids[i] );
      int selfHits = 0;
      for( int i = 0; i < 5; i++ )
      {
         int probe = i * 17011 % TOTAL;
         IReadOnlyList<SearchHit> hits = await search( vectors[probe], CancellationToken.None );
         selfHits += hits.Count > 0 && hits[0].ChunkId == ids[probe] ? 1 : 0;
      }

      var times = new List<double>();
      double recall = 0;
      for( int q = 0; q < queries; q++ )
      {
         float[] query = RandomUnit( random );
         var timer = Stopwatch.StartNew();
         IReadOnlyList<SearchHit> hits = await search( query, CancellationToken.None );
         timer.Stop();
         times.Add( timer.Elapsed.TotalMilliseconds );
         Assert.Equal( TOP, hits.Count );
         Assert.True( hits.Zip( hits.Skip( 1 ), ( a, b ) => a.Score >= b.Score - 1e-6 ).All( ok => ok ), "hits are not sorted best first" );
         Assert.All( hits, h => Assert.Equal( Dot( vectors[position[h.ChunkId]], query ), h.Score, 3 ) );
         HashSet<Guid> truth = ExactTop( vectors, ids, query );
         recall += hits.Count( h => truth.Contains( h.ChunkId ) ) / (double)TOP;
      }

      times.Sort();
      _output.WriteLine( $"{label}: p50 {times[times.Count / 2]:F1} ms, p95 {times[(int)( times.Count * 0.95 )]:F1} ms, min {times[0]:F1} ms, max {times[^1]:F1} ms, recall@{TOP} {recall / queries:F3}, self-match {selfHits}/5" );
      if( requireSelfMatch )
      {
         Assert.True( selfHits == 5, $"{label} search found a stored vector's own record first only {selfHits} of 5 times" );
         Assert.Equal( 1.0, recall / queries, 3 );
      }
   }

   /// <summary>
   /// Dot product (cosine similarity for unit vectors).
   /// </summary>
   /// <param name="a">First vector.</param>
   /// <param name="b">Second vector.</param>
   /// <returns>The dot product.</returns>
   private static double Dot( float[] a, float[] b )
   {
      double sum = 0;
      for( int i = 0; i < a.Length; i++ )
      {
         sum += a[i] * b[i];
      }

      return sum;
   }

   /// <summary>
   /// Builds one batch of random records and remembers their vectors and ids.
   /// </summary>
   /// <param name="random">Random source.</param>
   /// <param name="start">Index of the first record.</param>
   /// <param name="vectors">All vectors, filled in here.</param>
   /// <param name="ids">All chunk ids, filled in here.</param>
   /// <returns>The batch.</returns>
   private static List<VectorRecord> MakeBatch( Random random, int start, float[][] vectors, Guid[] ids )
   {
      var batch = new List<VectorRecord>( BATCH );
      for( int i = start; i < start + BATCH; i++ )
      {
         vectors[i] = RandomUnit( random );
         ids[i] = Guid.NewGuid();
         var document = new Document( $"doc|{i}", $"table{i % 3}", $"file{i % 7}.csv", $"text {i}", $"hash{i}", new Dictionary<string, string> { ["row"] = i.ToString() } );
         batch.Add( new VectorRecord( document, new Chunk( ids[i], document.DocKey, 0, $"chunk text {i}" ), vectors[i] ) );
      }

      return batch;
   }

   /// <summary>
   /// A random vector scaled to length 1.
   /// </summary>
   /// <param name="random">Random source.</param>
   /// <returns>The vector.</returns>
   private static float[] RandomUnit( Random random )
   {
      float[] v = new float[DIMENSION];
      double sum = 0;
      for( int i = 0; i < DIMENSION; i++ )
      {
         v[i] = random.NextSingle() * 2f - 1f;
         sum += v[i] * v[i];
      }

      float norm = (float)Math.Sqrt( sum );
      for( int i = 0; i < DIMENSION; i++ )
      {
         v[i] /= norm;
      }

      return v;
   }

   /// <summary>
   /// The true ten nearest stored vectors by dot product, computed in memory.
   /// </summary>
   /// <param name="vectors">Every stored vector.</param>
   /// <param name="ids">Chunk id of each stored vector.</param>
   /// <param name="query">The query vector.</param>
   /// <returns>Chunk ids of the ten best.</returns>
   private static HashSet<Guid> ExactTop( float[][] vectors, Guid[] ids, float[] query )
   {
      var scores = new float[vectors.Length];
      Parallel.For( 0, vectors.Length, i =>
      {
         float sum = 0;
         float[] v = vectors[i];
         for( int d = 0; d < DIMENSION; d++ )
         {
            sum += v[d] * query[d];
         }

         scores[i] = sum;
      } );
      return Enumerable.Range( 0, vectors.Length ).OrderByDescending( i => scores[i] ).Take( TOP ).Select( i => ids[i] ).ToHashSet();
   }

   #endregion Private Methods
}
