using System.Diagnostics;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// The shared sink contract run against the local Oracle container (deploy/engines/oracle.compose.yaml).
/// </summary>
[Trait( "Category", "Live" )]
public sealed class OracleSinkTests : SinkContractTests
{
   #region Overrides

   /// <inheritdoc />
   protected override ISink CreateSink()
   {
      return new OracleSink();
   }

   #endregion Overrides
}

/// <summary>
/// Oracle-specific behaviour the shared contract does not cover: a chunk text far larger than a
/// VARCHAR2 can hold, empty strings (which Oracle stores as NULL), a re-upsert that replaces the
/// text and metadata, a pipeline name too long for an Oracle identifier, and a name that is not
/// safe to put into SQL.
/// </summary>
[Trait( "Category", "Live" )]
public sealed class OracleSinkQuirkTests : IAsyncLifetime
{
   #region Data Members

   private const int DIMENSION = 16;

   private readonly string _collection = "gvboq_" + Guid.NewGuid().ToString( "N" )[..10];
   private readonly OracleSink _sink = new();

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Nothing to prepare; each test creates its own collection.
   /// </summary>
   public Task InitializeAsync()
   {
      return Task.CompletedTask;
   }

   /// <summary>
   /// Drops the throwaway collection whatever happened.
   /// </summary>
   public async Task DisposeAsync()
   {
      await _sink.DropCollectionAsync( _collection, CancellationToken.None );
   }

   /// <summary>
   /// A 60,000 character text (over the 32,000 byte limit of the fast bind, so it goes in as a CLOB), a
   /// 10,000 character accented text (20,000 bytes, the fast bind) and an empty origin go in, and the
   /// texts come back whole.
   /// </summary>
   [Fact]
   public async Task LongText_AndEmptyStrings_RoundTrip()
   {
      await _sink.EnsureCollectionAsync( _collection, DIMENSION, CancellationToken.None );
      string longText = string.Concat( Enumerable.Range( 0, 10000 ).Select( i => $"word{i % 10} " ) ) + "end";
      VectorRecord record = MakeRecord( "doc|long", origin: string.Empty, text: longText, vector: Unit( 1 ), metadata: new Dictionary<string, string> { ["row"] = "7", ["name"] = "caf\u00e9 \"quoted\"" } );
      await _sink.UpsertAsync( _collection, new[] { record }, CancellationToken.None );

      IReadOnlyList<SearchHit> hits = await _sink.SearchExactAsync( _collection, Unit( 1 ), 1, CancellationToken.None );
      Assert.Single( hits );
      Assert.Equal( longText, hits[0].Text );
      Assert.Equal( "doc|long", hits[0].DocKey );
      Assert.Equal( "table0", hits[0].Table );

      string mediumText = new( 'é', 10000 );
      VectorRecord medium = MakeRecord( "doc|medium", "m.csv", mediumText, Unit( 3 ), new Dictionary<string, string>() );
      await _sink.UpsertAsync( _collection, new[] { medium }, CancellationToken.None );
      hits = await _sink.SearchExactAsync( _collection, Unit( 3 ), 1, CancellationToken.None );
      Assert.Equal( mediumText, hits[0].Text );

      VectorRecord empty = MakeRecord( "doc|empty", string.Empty, string.Empty, Unit( 5 ), new Dictionary<string, string>() );
      await _sink.UpsertAsync( _collection, new[] { empty }, CancellationToken.None );
      hits = await _sink.SearchExactAsync( _collection, Unit( 5 ), 1, CancellationToken.None );
      Assert.Equal( string.Empty, hits[0].Text );
   }

   /// <summary>
   /// Writing the same chunk id again replaces its text and vector instead of adding a row.
   /// </summary>
   [Fact]
   public async Task ReUpsert_ReplacesTextAndVector()
   {
      await _sink.EnsureCollectionAsync( _collection, DIMENSION, CancellationToken.None );
      VectorRecord first = MakeRecord( "doc|a", "a.csv", "first text", Unit( 0 ), new Dictionary<string, string>() );
      VectorRecord second = first with { Chunk = first.Chunk with { Text = "second text" }, Vector = Unit( 2 ) };
      await _sink.UpsertAsync( _collection, new[] { first }, CancellationToken.None );
      await _sink.UpsertAsync( _collection, new[] { second }, CancellationToken.None );

      Assert.Equal( 1, await _sink.CountAsync( _collection, CancellationToken.None ) );
      IReadOnlyList<SearchHit> hits = await _sink.SearchExactAsync( _collection, Unit( 2 ), 1, CancellationToken.None );
      Assert.Equal( "second text", hits[0].Text );
      Assert.InRange( hits[0].Score, 0.999, 1.001 );
   }

   /// <summary>
   /// A pipeline name too long for a comfortable Oracle identifier is shortened and still works.
   /// </summary>
   [Fact]
   public async Task LongPipelineName_StillWorks()
   {
      string longName = "gvboq_" + new string( 'x', 100 ) + Guid.NewGuid().ToString( "N" )[..6];
      try
      {
         Assert.True( await _sink.EnsureCollectionAsync( longName, DIMENSION, CancellationToken.None ) );
         Assert.False( await _sink.EnsureCollectionAsync( longName, DIMENSION, CancellationToken.None ) );
      }
      finally
      {
         await _sink.DropCollectionAsync( longName, CancellationToken.None );
      }
   }

   /// <summary>
   /// A name that could change the SQL is refused before anything is sent.
   /// </summary>
   [Fact]
   public async Task UnsafePipelineName_IsRefused()
   {
      await Assert.ThrowsAsync<ArgumentException>( () => _sink.CountAsync( "x; drop table y", CancellationToken.None ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Builds one record with the given payload.
   /// </summary>
   /// <param name="docKey">Document key.</param>
   /// <param name="origin">Origin file.</param>
   /// <param name="text">Chunk text.</param>
   /// <param name="vector">Embedding.</param>
   /// <param name="metadata">Document metadata.</param>
   /// <returns>The record.</returns>
   private static VectorRecord MakeRecord( string docKey, string origin, string text, float[] vector, Dictionary<string, string> metadata )
   {
      var document = new Document( docKey, "table0", origin, text, "hash", metadata );
      return new VectorRecord( document, new Chunk( Guid.NewGuid(), docKey, 0, text ), vector );
   }

   /// <summary>
   /// A unit vector with a 1 in one position, so queries and stored vectors are easy to tell apart.
   /// </summary>
   /// <param name="position">Index of the 1.</param>
   /// <returns>The vector.</returns>
   private static float[] Unit( int position )
   {
      float[] v = new float[DIMENSION];
      v[position] = 1f;
      return v;
   }

   #endregion Private Methods
}

/// <summary>
/// Loads 100,000 random normalized 1024-dimension vectors into Oracle and times the load, the
/// count, 20 searches and the exact search. Only correctness is asserted; the timings are printed
/// for the report. Oracle merges new rows into its HNSW graph in the background, so the default
/// search is timed twice: right after the load, and after an explicit index refresh (rebuild).
/// Why random vectors: they are the hardest case for an approximate index (no clusters to
/// exploit), so the recall printed here is a floor for real embeddings.
/// </summary>
[Trait( "Category", "Scale" )]
public sealed class OracleSinkScaleTests
{
   #region Data Members

   private const int DIMENSION = 1024;
   private const int TOTAL = 100000;
   private const int BATCH = 1000;
   private const int QUERIES = 20;
   private const int EXACT_QUERIES = 10;
   private const int PRE_REFRESH_QUERIES = 10;
   private const int TOP = 10;

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xunit's output sink.
   /// </summary>
   /// <param name="output">Where the timings are printed.</param>
   public OracleSinkScaleTests( ITestOutputHelper output )
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
      var sink = new OracleSink();
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

         await MeasureAsync( "default, graph not yet refreshed", random, vectors, ids, ( v, ct ) => sink.SearchAsync( collection, v, TOP, ct ), PRE_REFRESH_QUERIES );
         var refresh = Stopwatch.StartNew();
         await sink.RefreshIndexAsync( collection, CancellationToken.None );
         refresh.Stop();
         _output.WriteLine( $"index refresh (DBMS_VECTOR.REBUILD_INDEX): {refresh.Elapsed.TotalSeconds:F1} s" );
         await MeasureAsync( "default, after refresh", random, vectors, ids, ( v, ct ) => sink.SearchAsync( collection, v, TOP, ct ), QUERIES );
         await MeasureAsync( "exact", random, vectors, ids, ( v, ct ) => sink.SearchExactAsync( collection, v, TOP, ct ), EXACT_QUERIES );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Runs the stored-vector self-match check, then times fresh random queries and prints the
   /// median, 95th percentile and recall against an in-memory brute force.
   /// </summary>
   /// <param name="label">Name of the search mode.</param>
   /// <param name="random">Random source for the fresh queries.</param>
   /// <param name="vectors">Every stored vector.</param>
   /// <param name="ids">Chunk id of each stored vector.</param>
   /// <param name="search">The search under test.</param>
   /// <param name="queries">How many fresh queries to time.</param>
   private async Task MeasureAsync( string label, Random random, float[][] vectors, Guid[] ids,
      Func<float[], CancellationToken, Task<IReadOnlyList<SearchHit>>> search, int queries )
   {
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
         HashSet<Guid> truth = ExactTop( vectors, ids, query );
         recall += hits.Count( h => truth.Contains( h.ChunkId ) ) / (double)TOP;
      }

      times.Sort();
      _output.WriteLine( $"{label}: p50 {times[times.Count / 2]:F1} ms, p95 {times[(int)( times.Count * 0.95 )]:F1} ms, min {times[0]:F1} ms, max {times[^1]:F1} ms, recall@{TOP} {recall / queries:F3}, self-match {selfHits}/5" );
      Assert.True( selfHits >= 4, $"{label} search found a stored vector's own record first only {selfHits} of 5 times" );
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
