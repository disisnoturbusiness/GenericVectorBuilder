using System.Diagnostics;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// The shared sink contract run against SQLite with the sqlite-vec extension. The engine is
/// embedded, so there is no container to start: the sink writes its files under
/// ~/gvb-data/engines/sqlitevec and the contract drops its throwaway collection afterwards.
/// </summary>
[Trait( "Category", "Live" )]
public sealed class SqliteVecSinkTests : SinkContractTests
{
   #region Overrides

   /// <inheritdoc />
   protected override ISink CreateSink()
   {
      return new SqliteVecSink();
   }

   #endregion Overrides

   #region Public Methods

   /// <summary>
   /// The data lives in a file, so a brand new sink (a new process, in real use) must find the
   /// collection, its dimension and its rows exactly as the first sink left them.
   /// </summary>
   [Fact]
   public async Task Reopen_FindsTheSameDataAndDimension()
   {
      string collection = "gvbct_" + Guid.NewGuid().ToString( "N" )[..10];
      List<VectorRecord> records = Records( 20, 16 );
      try
      {
         using( var first = new SqliteVecSink() )
         {
            await first.EnsureCollectionAsync( collection, 16, CancellationToken.None );
            await first.UpsertAsync( collection, records, CancellationToken.None );
         }

         using var second = new SqliteVecSink();
         Assert.False( await second.EnsureCollectionAsync( collection, 16, CancellationToken.None ) );
         Assert.Equal( 20, await second.CountAsync( collection, CancellationToken.None ) );
         IReadOnlyList<SearchHit> hits = await second.SearchAsync( collection, records[7].Vector, 3, CancellationToken.None );
         Assert.Equal( records[7].Chunk.ChunkId, hits[0].ChunkId );
      }
      finally
      {
         using var cleanup = new SqliteVecSink();
         await cleanup.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// A vector of the wrong length is refused with a plain message that names the collection,
   /// and nothing is written.
   /// </summary>
   [Fact]
   public async Task Upsert_WrongDimension_IsRefusedAndWritesNothing()
   {
      string collection = "gvbct_" + Guid.NewGuid().ToString( "N" )[..10];
      using var sink = new SqliteVecSink();
      try
      {
         await sink.EnsureCollectionAsync( collection, 16, CancellationToken.None );
         InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>( () => sink.UpsertAsync( collection, Records( 3, 8 ), CancellationToken.None ) );
         Assert.Contains( collection, error.Message );
         Assert.Equal( 0, await sink.CountAsync( collection, CancellationToken.None ) );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// A collection name that could change the SQL or escape the data folder is refused before
   /// anything touches the disk.
   /// </summary>
   [Fact]
   public async Task BadCollectionName_IsRefused()
   {
      using var sink = new SqliteVecSink();
      await Assert.ThrowsAsync<ArgumentException>( () => sink.EnsureCollectionAsync( "x\"; DROP TABLE y; --", 16, CancellationToken.None ) );
      await Assert.ThrowsAsync<ArgumentException>( () => sink.EnsureCollectionAsync( "../escape", 16, CancellationToken.None ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Builds small deterministic records with unit-length vectors.
   /// </summary>
   /// <param name="count">How many records.</param>
   /// <param name="dimension">Vector length.</param>
   /// <returns>The records.</returns>
   private static List<VectorRecord> Records( int count, int dimension )
   {
      var random = new Random( 11 );
      var records = new List<VectorRecord>( count );
      for( int i = 0; i < count; i++ )
      {
         float[] v = Enumerable.Range( 0, dimension ).Select( _ => random.NextSingle() * 2f - 1f ).ToArray();
         float norm = MathF.Sqrt( v.Sum( x => x * x ) );
         v = v.Select( x => x / norm ).ToArray();
         var document = new Document( $"doc|{i}", "t", "f.csv", $"text {i}", $"hash{i}", new Dictionary<string, string> { ["row"] = i.ToString() } );
         records.Add( new VectorRecord( document, new Chunk( Guid.NewGuid(), document.DocKey, 0, $"chunk {i}" ), v ) );
      }

      return records;
   }

   #endregion Private Methods
}

/// <summary>
/// Loads 100,000 random normalized 1024-dimension vectors into sqlite-vec and times the load,
/// the count and 20 searches in each mode. Only correctness is asserted; the timings are
/// printed for the report. Run it by hand with --filter "Category=Scale".
/// Why random vectors: they are the hardest case for an approximate index. sqlite-vec has no
/// index, so its default search is already exact and both modes must score recall 1.0.
/// </summary>
[Trait( "Category", "Scale" )]
public sealed class SqliteVecSinkScaleTests
{
   #region Data Members

   private const int DIMENSION = 1024;
   private const int TOTAL = 100000;
   private const int BATCH = 1000;
   private const int QUERIES = 20;
   private const int TOP = 10;
   private const double MIN_EXACT_RECALL = 0.995;

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xunit's output sink.
   /// </summary>
   /// <param name="output">Where the timings are printed.</param>
   public SqliteVecSinkScaleTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Load, count, then search 100,000 vectors in both modes and print the timings.
   /// </summary>
   [Fact]
   public async Task Load100k_CountAndSearch()
   {
      string collection = "gvbscale_" + Guid.NewGuid().ToString( "N" )[..10];
      using var sink = new SqliteVecSink();
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
         _output.WriteLine( $"load {TOTAL} x {DIMENSION}: upsert {load.Elapsed.TotalSeconds:F1} s ({TOTAL / load.Elapsed.TotalSeconds:F0} vectors/s), count {count.Elapsed.TotalMilliseconds:F1} ms, stored {stored}, {SizeOnDisk( collection ) / 1048576} MB on disk" );
         Assert.Equal( TOTAL, stored );

         await MeasureAsync( "default", random, vectors, ids, ( v, ct ) => sink.SearchAsync( collection, v, TOP, ct ) );
         await MeasureAsync( "exact", random, vectors, ids, ( v, ct ) => sink.SearchExactAsync( collection, v, TOP, ct ) );
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
   /// median, 95th percentile and recall against an in-memory brute force. Both modes are
   /// brute force here, so recall must be 1.0 for each.
   /// </summary>
   /// <param name="label">Name of the search mode.</param>
   /// <param name="random">Random source for the fresh queries.</param>
   /// <param name="vectors">Every stored vector.</param>
   /// <param name="ids">Chunk id of each stored vector.</param>
   /// <param name="search">The search under test.</param>
   private async Task MeasureAsync( string label, Random random, float[][] vectors, Guid[] ids,
      Func<float[], CancellationToken, Task<IReadOnlyList<SearchHit>>> search )
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
      for( int q = 0; q < QUERIES; q++ )
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
      recall /= QUERIES;
      _output.WriteLine( $"{label}: p50 {times[times.Count / 2]:F1} ms, p95 {times[(int)( times.Count * 0.95 )]:F1} ms, min {times[0]:F1} ms, max {times[^1]:F1} ms, recall@{TOP} {recall:F3}, self-match {selfHits}/5" );
      Assert.Equal( 5, selfHits );
      Assert.True( recall >= MIN_EXACT_RECALL, $"{label} search is brute force but scored recall@{TOP} {recall:F3}" );
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
   /// Total size of the collection's database file and its WAL files.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <returns>Bytes on disk.</returns>
   private static long SizeOnDisk( string collection )
   {
      string file = Path.Combine( SqliteVecSinkOptions.DefaultDirectory(), $"gvb_{collection}.sqlite" );
      return new[] { string.Empty, "-wal", "-shm" }.Select( suffix => file + suffix ).Where( File.Exists ).Sum( f => new FileInfo( f ).Length );
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
