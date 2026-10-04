using System.Diagnostics;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Runs the shared sink contract against the real Typesense container
/// (deploy/engines/typesense.compose.yaml must be up on port 8108).
/// </summary>
[Trait( "Category", "Live" )]
public sealed class TypesenseSinkTests : SinkContractTests
{
   #region Overrides

   /// <inheritdoc />
   protected override ISink CreateSink()
   {
      return new TypesenseSink();
   }

   #endregion Overrides
}

/// <summary>
/// Scale smoke test: loads 100,000 random normalized 1024-dimension vectors into Typesense,
/// counts them, then times 20 searches (and 5 exact ones) and prints the numbers.
/// Why it asserts only correctness: speed depends on the box, so the numbers are printed for the
/// benchmark report and the test fails only when the answers are wrong.
/// Run it on its own: dotnet test tests/GenericVectorBuilder.Engines.Tests --filter "Category=Scale&amp;FullyQualifiedName~Typesense" --logger "console;verbosity=detailed"
/// </summary>
[Trait( "Category", "Scale" )]
public sealed class TypesenseScaleTests
{
   #region Data Members

   private const int DIMENSION = 1024;
   private const int COUNT = 100_000;
   private const int QUERIES = 20;
   private const int EXACT_QUERIES = 5;
   private const int TOP = 10;
   private const int WRITE_CALL = 5_000;

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xUnit's output channel.
   /// </summary>
   /// <param name="output">Where timings are printed.</param>
   public TypesenseScaleTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Loads 100,000 vectors, checks the count, and times the searches.
   /// </summary>
   [Fact]
   public async Task Load100k_Count_Search20()
   {
      string collection = "gvbsc_" + Guid.NewGuid().ToString( "N" )[..10];
      using var sink = new TypesenseSink();
      CancellationToken ct = CancellationToken.None;
      try
      {
         List<VectorRecord> records = MakeRecords( COUNT, seed: 2026 );
         await sink.EnsureCollectionAsync( collection, DIMENSION, ct );

         var load = Stopwatch.StartNew();
         foreach( VectorRecord[] part in records.Chunk( WRITE_CALL ) )
         {
            await sink.UpsertAsync( collection, part, ct );
         }

         long count = await sink.CountAsync( collection, ct );
         load.Stop();
         Assert.Equal( COUNT, count );
         _output.WriteLine( $"load+count {COUNT} vectors: {load.Elapsed.TotalSeconds:F1} s = {COUNT / load.Elapsed.TotalSeconds:F0} vectors/s" );

         await MeasureAsync( sink, collection, records, ct );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, ct );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Times the default search (and the exact search when the sink has one), checks recall against
   /// an in-memory brute force and checks that stored vectors find themselves.
   /// </summary>
   /// <param name="sink">The loaded sink.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="records">What was loaded.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task MeasureAsync( ISink sink, string collection, List<VectorRecord> records, CancellationToken ct )
   {
      List<VectorRecord> queries = MakeRecords( QUERIES, seed: 7 );
      for( int i = 0; i < 3; i++ )
      {
         await sink.SearchAsync( collection, queries[i].Vector, TOP, ct );
      }

      var times = new List<double>();
      double recall = 0;
      foreach( VectorRecord q in queries )
      {
         var clock = Stopwatch.StartNew();
         IReadOnlyList<SearchHit> hits = await sink.SearchAsync( collection, q.Vector, TOP, ct );
         times.Add( clock.Elapsed.TotalMilliseconds );
         Assert.Equal( TOP, hits.Count );
         recall += Overlap( hits, Truth( records, q.Vector ) );
      }

      _output.WriteLine( $"search x{QUERIES}: p50 {Percentile( times, 0.5 ):F1} ms, p95 {Percentile( times, 0.95 ):F1} ms, recall@{TOP} on random queries {recall / QUERIES:F3}" );

      int selfHits = 0;
      foreach( VectorRecord probe in records.Skip( 1234 ).Take( QUERIES ) )
      {
         IReadOnlyList<SearchHit> hits = await sink.SearchAsync( collection, probe.Vector, TOP, ct );
         selfHits += hits[0].ChunkId == probe.Chunk.ChunkId ? 1 : 0;
      }

      _output.WriteLine( $"stored vector finds itself first: {selfHits}/{QUERIES}" );
      Assert.True( selfHits >= QUERIES * 0.8, $"only {selfHits} of {QUERIES} stored vectors found themselves" );

      if( sink is IExactSearchSink exact )
      {
         await MeasureExactAsync( exact, collection, records, queries, ct );
      }
   }

   /// <summary>
   /// Times the engine's exact mode and requires it to agree with the in-memory brute force.
   /// </summary>
   /// <param name="exact">The sink's exact mode.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="records">What was loaded.</param>
   /// <param name="queries">Random queries.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task MeasureExactAsync( IExactSearchSink exact, string collection, List<VectorRecord> records, List<VectorRecord> queries, CancellationToken ct )
   {
      var times = new List<double>();
      double recall = 0;
      foreach( VectorRecord q in queries.Take( EXACT_QUERIES ) )
      {
         var clock = Stopwatch.StartNew();
         IReadOnlyList<SearchHit> hits = await exact.SearchExactAsync( collection, q.Vector, TOP, ct );
         times.Add( clock.Elapsed.TotalMilliseconds );
         recall += Overlap( hits, Truth( records, q.Vector ) );
      }

      _output.WriteLine( $"exact search x{EXACT_QUERIES}: p50 {Percentile( times, 0.5 ):F1} ms, recall@{TOP} {recall / EXACT_QUERIES:F3}" );
      Assert.True( recall / EXACT_QUERIES >= 0.999, "exact search disagreed with the in-memory brute force" );
   }

   /// <summary>
   /// Chunk ids of the true top hits by dot product.
   /// </summary>
   /// <param name="records">Stored records.</param>
   /// <param name="query">Query vector.</param>
   /// <returns>The ten nearest chunk ids.</returns>
   private static HashSet<Guid> Truth( List<VectorRecord> records, float[] query )
   {
      var scores = new float[records.Count];
      Parallel.For( 0, records.Count, i => scores[i] = Dot( records[i].Vector, query ) );
      return Enumerable.Range( 0, records.Count ).OrderByDescending( i => scores[i] ).Take( TOP ).Select( i => records[i].Chunk.ChunkId ).ToHashSet();
   }

   /// <summary>
   /// Share of the true top hits that a search returned.
   /// </summary>
   /// <param name="hits">Search result.</param>
   /// <param name="truth">True top ids.</param>
   /// <returns>Overlap between 0 and 1.</returns>
   private static double Overlap( IReadOnlyList<SearchHit> hits, HashSet<Guid> truth )
   {
      return hits.Take( TOP ).Count( h => truth.Contains( h.ChunkId ) ) / (double)TOP;
   }

   /// <summary>
   /// Nearest-rank percentile.
   /// </summary>
   /// <param name="values">Measurements.</param>
   /// <param name="fraction">0.5 for the median, 0.95 for p95.</param>
   /// <returns>The percentile value.</returns>
   private static double Percentile( List<double> values, double fraction )
   {
      List<double> sorted = values.OrderBy( v => v ).ToList();
      return sorted[Math.Min( sorted.Count - 1, (int)Math.Ceiling( fraction * sorted.Count ) - 1 )];
   }

   /// <summary>
   /// Dot product (cosine for normalized vectors) using SIMD.
   /// </summary>
   /// <param name="a">First vector.</param>
   /// <param name="b">Second vector.</param>
   /// <returns>The dot product.</returns>
   private static float Dot( float[] a, float[] b )
   {
      int width = System.Numerics.Vector<float>.Count;
      var sum = System.Numerics.Vector<float>.Zero;
      int i = 0;
      for( ; i <= a.Length - width; i += width )
      {
         sum += new System.Numerics.Vector<float>( a, i ) * new System.Numerics.Vector<float>( b, i );
      }

      float total = System.Numerics.Vector.Sum( sum );
      for( ; i < a.Length; i++ )
      {
         total += a[i] * b[i];
      }

      return total;
   }

   /// <summary>
   /// Deterministic random records with L2-normalized 1024-dimension vectors.
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
         double norm = 0;
         for( int d = 0; d < DIMENSION; d++ )
         {
            v[d] = (float)( random.NextDouble() * 2 - 1 );
            norm += v[d] * v[d];
         }

         float scale = (float)( 1.0 / Math.Sqrt( norm ) );
         for( int d = 0; d < DIMENSION; d++ )
         {
            v[d] *= scale;
         }

         var document = new Document( $"scale|{seed}|{i}", $"table{i % 3}", $"file{i % 7}.csv", $"text {i}", $"hash{i}", new Dictionary<string, string> { ["row"] = i.ToString() } );
         var id = new Guid( System.Security.Cryptography.MD5.HashData( System.Text.Encoding.UTF8.GetBytes( $"scale:{seed}:{i}" ) ) );
         records.Add( new VectorRecord( document, new Chunk( id, document.DocKey, 0, $"chunk text {seed}-{i}" ), v ) );
      }

      return records;
   }

   #endregion Private Methods
}
