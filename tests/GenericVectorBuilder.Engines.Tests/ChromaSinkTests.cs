using System.Diagnostics;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Sinks;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Runs the shared sink contract against the local Chroma container
/// (deploy/engines/chroma.compose.yaml must be up).
/// </summary>
[Trait( "Category", "Live" )]
public sealed class ChromaSinkTests : SinkContractTests
{
   /// <summary>
   /// Creates the Chroma sink with its local defaults.
   /// </summary>
   /// <returns>The sink under test.</returns>
   protected override ISink CreateSink()
   {
      return new ChromaSink();
   }
}

/// <summary>
/// Loads 100,000 random 1024-dim vectors into Chroma and times it. It asserts only correctness
/// (the count, well-formed hits, a stored vector finding itself); the timings are printed for
/// the benchmark write-up. Run with: dotnet test --filter "Category=Scale&amp;FullyQualifiedName~Chroma"
/// </summary>
[Trait( "Category", "Scale" )]
public sealed class ChromaSinkScaleTests
{
   #region Data Members

   private const int DIMENSION = 1024;
   private const int COUNT = 100_000;
   private const int QUERIES = 20;
   private const int TOP = 10;

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Receives the xunit output writer.
   /// </summary>
   /// <param name="output">Where timings are printed.</param>
   public ChromaSinkScaleTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Loads 100k vectors, counts them, runs 20 searches and prints the timings.
   /// </summary>
   [Fact]
   public async Task Load100k_CountAndSearch()
   {
      string collection = "gvbscale_" + Guid.NewGuid().ToString( "N" )[..8];
      using var sink = new ChromaSink();
      List<VectorRecord> records = MakeRecords( COUNT, seed: 11 );
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         var clock = Stopwatch.StartNew();
         await sink.UpsertAsync( collection, records, CancellationToken.None );
         double loadSeconds = clock.Elapsed.TotalSeconds;
         clock.Restart();
         long count = await sink.CountAsync( collection, CancellationToken.None );
         double countMs = clock.Elapsed.TotalMilliseconds;
         Assert.Equal( COUNT, count );

         ( double p50, double p95, int selfFirst, double recall ) = await MeasureSearchesAsync( sink, collection, records );
         _output.WriteLine( $"Chroma 100k x {DIMENSION}: load {loadSeconds:F1} s ({COUNT / loadSeconds:F0} vectors/s), count {countMs:F0} ms" );
         _output.WriteLine( $"Chroma search top-{TOP} over {QUERIES} queries: p50 {p50:F1} ms, p95 {p95:F1} ms, stored vector found first {selfFirst}/{QUERIES}, recall@{TOP} vs brute force {recall:F3}" );
         Assert.True( selfFirst >= QUERIES * 0.8, $"only {selfFirst}/{QUERIES} stored vectors found themselves first" );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Runs the timed searches. Queries are stored vectors (so a correct engine returns the query
   /// itself first); recall is checked against an in-memory brute force.
   /// </summary>
   /// <param name="sink">Sink under test.</param>
   /// <param name="collection">Loaded collection.</param>
   /// <param name="records">What was loaded.</param>
   /// <returns>Median and 95th percentile milliseconds, self-hit count, mean recall.</returns>
   private static async Task<( double P50, double P95, int SelfFirst, double Recall )> MeasureSearchesAsync( ISink sink, string collection, List<VectorRecord> records )
   {
      var times = new List<double>();
      int selfFirst = 0;
      double recallTotal = 0;
      await sink.SearchAsync( collection, records[0].Vector, TOP, CancellationToken.None );
      for( int i = 0; i < QUERIES; i++ )
      {
         VectorRecord query = records[i * ( COUNT / QUERIES ) + 7];
         var clock = Stopwatch.StartNew();
         IReadOnlyList<SearchHit> hits = await sink.SearchAsync( collection, query.Vector, TOP, CancellationToken.None );
         times.Add( clock.Elapsed.TotalMilliseconds );
         Assert.Equal( TOP, hits.Count );
         Assert.All( hits, h => Assert.InRange( h.Score, -1.01, 1.01 ) );
         selfFirst += hits[0].ChunkId == query.Chunk.ChunkId ? 1 : 0;
         var truth = records.OrderByDescending( r => Dot( r.Vector, query.Vector ) ).Take( TOP ).Select( r => r.Chunk.ChunkId ).ToHashSet();
         recallTotal += hits.Count( h => truth.Contains( h.ChunkId ) ) / (double)TOP;
      }

      times.Sort();
      return ( times[times.Count / 2], times[(int)( times.Count * 0.95 ) - 1], selfFirst, recallTotal / QUERIES );
   }

   /// <summary>
   /// Deterministic normalized random records.
   /// </summary>
   /// <param name="count">How many.</param>
   /// <param name="seed">Random seed.</param>
   /// <returns>The records.</returns>
   private static List<VectorRecord> MakeRecords( int count, int seed )
   {
      var random = new Random( seed );
      var records = new List<VectorRecord>( count );
      for( int i = 0; i < count; i++ )
      {
         var v = new float[DIMENSION];
         double sum = 0;
         for( int d = 0; d < DIMENSION; d++ )
         {
            v[d] = random.NextSingle() * 2 - 1;
            sum += v[d] * v[d];
         }

         float norm = (float)Math.Sqrt( sum );
         for( int d = 0; d < DIMENSION; d++ )
         {
            v[d] /= norm;
         }

         var document = new Document( $"scale|{i}", $"table{i % 3}", $"file{i % 7}.csv", $"text {i}", $"hash{i}", new Dictionary<string, string> { ["row"] = i.ToString() } );
         var chunk = new Chunk( new Guid( System.Security.Cryptography.MD5.HashData( System.Text.Encoding.UTF8.GetBytes( $"scale:{seed}:{i}" ) ) ), document.DocKey, 0, $"chunk text {i}" );
         records.Add( new VectorRecord( document, chunk, v ) );
      }

      return records;
   }

   /// <summary>
   /// Dot product (cosine for normalized vectors).
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

   #endregion Private Methods
}
