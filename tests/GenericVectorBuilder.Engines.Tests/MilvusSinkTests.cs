using System.Diagnostics;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Sinks;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Runs the shared sink contract against the local Milvus container
/// (deploy/engines/milvus.compose.yaml must be up).
/// </summary>
[Trait( "Category", "Live" )]
public sealed class MilvusSinkTests : SinkContractTests
{
   /// <summary>
   /// Creates the Milvus sink with its local defaults.
   /// </summary>
   /// <returns>The sink under test.</returns>
   protected override ISink CreateSink()
   {
      return new MilvusSink();
   }
}

/// <summary>
/// Loads 100,000 random 1024-dim vectors into Milvus, waits for its background index build, and
/// times it. It asserts only correctness
/// (the count, well-formed hits, a stored vector finding itself); the timings are printed for
/// the benchmark write-up. Run with: dotnet test --filter "Category=Scale&amp;FullyQualifiedName~Milvus"
/// </summary>
[Trait( "Category", "Scale" )]
public sealed class MilvusSinkScaleTests
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
   public MilvusSinkScaleTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Loads 100k vectors, waits for the index, counts them, runs 20 searches and prints the
   /// timings. It then repeats the searches with other consistency and ef settings so the
   /// cost of each is visible next to the default.
   /// </summary>
   [Fact]
   public async Task Load100k_CountAndSearch()
   {
      string collection = "gvbscale_" + Guid.NewGuid().ToString( "N" )[..8];
      using var sink = new MilvusSink();
      List<VectorRecord> records = MakeRecords( COUNT, seed: 11 );
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         var clock = Stopwatch.StartNew();
         await sink.UpsertAsync( collection, records, CancellationToken.None );
         double loadSeconds = clock.Elapsed.TotalSeconds;
         clock.Restart();
         bool indexed = await sink.WaitForIndexAsync( collection, TimeSpan.FromMinutes( 15 ), CancellationToken.None );
         double indexSeconds = clock.Elapsed.TotalSeconds;
         Assert.True( indexed, "Milvus did not finish building the HNSW index within 15 minutes" );
         clock.Restart();
         long count = await sink.CountAsync( collection, CancellationToken.None );
         double countMs = clock.Elapsed.TotalMilliseconds;
         Assert.Equal( COUNT, count );
         _output.WriteLine( $"Milvus 100k x {DIMENSION}: load {loadSeconds:F1} s ({COUNT / loadSeconds:F0} vectors/s), then {indexSeconds:F1} s more until the index covers every row, count {countMs:F0} ms" );

         List<VectorRecord> queries = Enumerable.Range( 0, QUERIES ).Select( i => records[i * ( COUNT / QUERIES ) + 7] ).ToList();
         List<HashSet<Guid>> truths = queries.Select( q => BruteForceTop( records, q.Vector ) ).ToList();
         int selfFirst = await ReportAsync( "default (Strong consistency, ef 100)", sink, collection, queries, truths );
         Assert.True( selfFirst >= QUERIES * 0.8, $"only {selfFirst}/{QUERIES} stored vectors found themselves first" );
         await ReportVariantsAsync( collection, queries, truths );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Repeats the timed searches under other settings. Information only, nothing is asserted.
   /// </summary>
   /// <param name="collection">Loaded collection.</param>
   /// <param name="queries">The query records.</param>
   /// <param name="truths">Exact top hits for each query.</param>
   private async Task ReportVariantsAsync( string collection, List<VectorRecord> queries, List<HashSet<Guid>> truths )
   {
      var variants = new (string Label, MilvusSinkOptions Options)[]
      {
         ( "Bounded consistency, ef 100", new MilvusSinkOptions( SearchConsistency: "Bounded" ) ),
         ( "Strong consistency, Milvus default ef", new MilvusSinkOptions( Ef: null ) ),
         ( "Strong consistency, ef 10", new MilvusSinkOptions( Ef: 10 ) ),
         ( "Strong consistency, ef 256", new MilvusSinkOptions( Ef: 256 ) ),
      };

      foreach( ( string label, MilvusSinkOptions options ) in variants )
      {
         using var variant = new MilvusSink( options );
         await ReportAsync( label, variant, collection, queries, truths );
      }
   }

   /// <summary>
   /// Runs the timed searches and prints one result line.
   /// </summary>
   /// <param name="label">What is being measured.</param>
   /// <param name="sink">Sink under test.</param>
   /// <param name="collection">Loaded collection.</param>
   /// <param name="queries">The query records (stored vectors, so a correct engine returns the query itself first).</param>
   /// <param name="truths">Exact top hits for each query.</param>
   /// <returns>How many queries found themselves first.</returns>
   private async Task<int> ReportAsync( string label, ISink sink, string collection, List<VectorRecord> queries, List<HashSet<Guid>> truths )
   {
      var times = new List<double>();
      int selfFirst = 0;
      double recallTotal = 0;
      await sink.SearchAsync( collection, queries[0].Vector, TOP, CancellationToken.None );
      for( int i = 0; i < queries.Count; i++ )
      {
         var clock = Stopwatch.StartNew();
         IReadOnlyList<SearchHit> hits = await sink.SearchAsync( collection, queries[i].Vector, TOP, CancellationToken.None );
         times.Add( clock.Elapsed.TotalMilliseconds );
         Assert.Equal( TOP, hits.Count );
         Assert.All( hits, h => Assert.InRange( h.Score, -1.01, 1.01 ) );
         selfFirst += hits[0].ChunkId == queries[i].Chunk.ChunkId ? 1 : 0;
         recallTotal += hits.Count( h => truths[i].Contains( h.ChunkId ) ) / (double)TOP;
      }

      times.Sort();
      _output.WriteLine( $"Milvus search top-{TOP}, {label}: p50 {times[times.Count / 2]:F1} ms, p95 {times[(int)( times.Count * 0.95 ) - 1]:F1} ms, stored vector found first {selfFirst}/{QUERIES}, recall@{TOP} vs brute force {recallTotal / queries.Count:F3}" );
      return selfFirst;
   }

   /// <summary>
   /// The exact top hits for one query, by in-memory brute force.
   /// </summary>
   /// <param name="records">Everything that was loaded.</param>
   /// <param name="query">Query vector.</param>
   /// <returns>Chunk ids of the TOP nearest records.</returns>
   private static HashSet<Guid> BruteForceTop( List<VectorRecord> records, float[] query )
   {
      return records.OrderByDescending( r => Dot( r.Vector, query ) ).Take( TOP ).Select( r => r.Chunk.ChunkId ).ToHashSet();
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
