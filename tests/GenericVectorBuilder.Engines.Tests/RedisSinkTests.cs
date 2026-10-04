using System.Diagnostics;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// The shared sink contract run against the local Redis container (deploy/engines/redis.compose.yaml).
/// </summary>
[Trait( "Category", "Live" )]
public sealed class RedisSinkTests : SinkContractTests
{
   #region Overrides

   /// <inheritdoc />
   protected override ISink CreateSink()
   {
      return new RedisSink();
   }

   #endregion Overrides
}

/// <summary>
/// Loads 100,000 random normalized 1024-dimension vectors into Redis and times the load, the
/// count and 20 searches. Only correctness is asserted; the timings are printed for the report.
/// Why random vectors: they are the hardest case for an approximate index (no clusters to
/// exploit), so the recall printed here is a floor for real embeddings.
/// </summary>
[Trait( "Category", "Scale" )]
public sealed class RedisSinkScaleTests
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
   public RedisSinkScaleTests( ITestOutputHelper output )
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
      using var sink = new RedisSink();
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

         await MeasureAsync( "default", random, vectors, ids, ( v, ct ) => sink.SearchAsync( collection, v, TOP, ct ), QUERIES );
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
