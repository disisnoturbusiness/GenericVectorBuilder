using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Proves the Typesense index step against the real container (deploy/engines/typesense.compose.yaml must
/// be up on port 8108): after the load the engine reports an empty write queue and an unfiltered vector
/// query returns every stored vector, and the premise that such a query is graph search, not a scan, holds.
/// Why these cases: the dead benchmark draft ran searches on engines whose index was unfinished or absent
/// and called them HNSW. Typesense cannot say which algorithm ran, so the premise is tested, not assumed.
/// Each test uses its own gvbbench_ collection and drops it afterwards.
/// Run: dotnet test tests/GenericVectorBuilder.Engines.Tests --filter "FullyQualifiedName~TypesenseReadinessTests" --logger "console;verbosity=detailed"
/// </summary>
[Trait( "Category", "Live" )]
public sealed class TypesenseReadinessTests
{
   #region Data Members

   private const int DIMENSION = 1024;
   private const int COUNT = 2000;
   private const string BASE_URL = "http://127.0.0.1:8108";

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xUnit's output channel.
   /// </summary>
   /// <param name="output">Where timings are printed.</param>
   public TypesenseReadinessTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Loads 2,000 vectors, finishes the load and requires the engine to report every vector in the graph.
   /// The state is also read before the finish step: Typesense builds the graph while writing, so it should
   /// already be ready, which is what makes the finish step a check and not a build.
   /// </summary>
   [Fact]
   public async Task Load2000_FinishLoad_EveryVectorIsInTheGraph()
   {
      string collection = NewCollection();
      using var sink = new TypesenseSink();
      using HttpClient http = NewClient();
      CancellationToken ct = CancellationToken.None;
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, ct );
         var load = Stopwatch.StartNew();
         await sink.UpsertAsync( collection, MakeRecords( COUNT, 2026 ), ct );
         _output.WriteLine( $"upsert of {COUNT} vectors returned after {load.Elapsed.TotalSeconds:F2} s" );

         IndexState before = await sink.GetIndexStateAsync( collection, ct );
         _output.WriteLine( $"right after the writes returned: ready={before.Ready}, {before.IndexedVectors}/{before.TotalVectors}: {before.Detail}" );
         Assert.Equal( COUNT, before.TotalVectors );

         var clock = Stopwatch.StartNew();
         string note = await sink.FinishLoadAsync( collection, ct );
         _output.WriteLine( $"FinishLoadAsync for {COUNT} vectors: {clock.Elapsed.TotalSeconds:F2} s: {note}" );

         IndexState after = await sink.GetIndexStateAsync( collection, ct );
         _output.WriteLine( $"after finish: ready={after.Ready}, {after.IndexedVectors}/{after.TotalVectors}: {after.Detail}" );
         Assert.True( after.Ready, after.Detail );
         Assert.Equal( COUNT, after.TotalVectors );
         Assert.Equal( after.TotalVectors, after.IndexedVectors );
         Assert.Equal( COUNT, await sink.CountAsync( collection, ct ) );
         await AssertRawCountsAsync( http, collection, ct );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// The premise behind the state: a vector query with no filter is answered from the HNSW graph. A scan
   /// would return exactly the exact search's hits; the graph with ef 10 does not. Fails if Typesense ever
   /// answers unfiltered queries by scanning, which would make "graph returns every vector" mean nothing.
   /// </summary>
   [Fact]
   public async Task UnfilteredVectorQuery_IsGraphSearch_NotAScan()
   {
      string collection = NewCollection();
      using var sink = new TypesenseSink( new TypesenseSinkOptions( Ef: 10 ) );
      CancellationToken ct = CancellationToken.None;
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, ct );
         await sink.UpsertAsync( collection, MakeRecords( COUNT, 11 ), ct );
         await sink.FinishLoadAsync( collection, ct );

         List<VectorRecord> queries = MakeRecords( 20, 99 );
         double recall = 0;
         foreach( VectorRecord q in queries )
         {
            HashSet<Guid> exact = ( await sink.SearchExactAsync( collection, q.Vector, 10, ct ) ).Select( h => h.ChunkId ).ToHashSet();
            IReadOnlyList<SearchHit> graph = await sink.SearchAsync( collection, q.Vector, 10, ct );
            recall += graph.Count( h => exact.Contains( h.ChunkId ) ) / 10.0;
         }

         recall /= queries.Count;
         _output.WriteLine( $"recall@10 of the unfiltered search (ef 10) against the exact search: {recall:F3}" );
         Assert.True( recall < 0.9, $"recall {recall:F3} is too close to exact: the unfiltered query may be a scan" );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// The finish step must end with a plain message when its time limit passes, not wait on.
   /// </summary>
   [Fact]
   public async Task FinishLoad_PastItsTimeLimit_FailsWithAPlainMessage()
   {
      string collection = NewCollection();
      using var sink = new TypesenseSink( new TypesenseSinkOptions( FinishTimeout: TimeSpan.FromMilliseconds( 1 ) ) );
      CancellationToken ct = CancellationToken.None;
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, ct );
         await sink.UpsertAsync( collection, MakeRecords( 50, 3 ), ct );
         var failure = await Assert.ThrowsAsync<InvalidOperationException>( () => sink.FinishLoadAsync( collection, ct ) );
         _output.WriteLine( failure.Message );
         Assert.Contains( "was not ready within", failure.Message );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// A collection that does not exist reads as not ready and cannot be finished.
   /// </summary>
   [Fact]
   public async Task MissingCollection_StateIsNotReady_FinishFails()
   {
      using var sink = new TypesenseSink();
      string collection = NewCollection();
      IndexState state = await sink.GetIndexStateAsync( collection, CancellationToken.None );
      Assert.False( state.Ready );
      Assert.Contains( "does not exist", state.Detail );
      var failure = await Assert.ThrowsAsync<InvalidOperationException>( () => sink.FinishLoadAsync( collection, CancellationToken.None ) );
      Assert.Contains( "does not exist", failure.Message );
   }

   /// <summary>
   /// The durability text cites the snapshot interval from the compose file; the compose file must say so.
   /// The raft fsync setting it also cites is read from the container's brpc /flags page, which is not
   /// reachable from the host, so it is not checked here (it was read by hand on 2026-10-04).
   /// </summary>
   [Fact]
   public void Durability_CitesTheComposeSnapshotInterval()
   {
      using var sink = new TypesenseSink();
      string compose = File.ReadAllText( Path.Combine( RepoRoot(), "deploy", "engines", "typesense.compose.yaml" ) );
      Assert.Contains( "TYPESENSE_SNAPSHOT_INTERVAL_SECONDS: \"300\"", compose );
      Assert.Contains( "TYPESENSE_SNAPSHOT_INTERVAL_SECONDS=300", sink.Durability );
      Assert.Contains( "raft_sync=true", sink.Durability );
      Assert.NotEqual( "not stated", sink.Durability );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A fresh benchmark-style collection name.
   /// </summary>
   /// <returns>The name.</returns>
   private static string NewCollection()
   {
      return "gvbbench_tsready_" + Guid.NewGuid().ToString( "N" )[..10];
   }

   /// <summary>
   /// Finds the repository root by walking up from the test binary until the solution file appears.
   /// </summary>
   /// <returns>The folder holding GenericVectorBuilder.slnx.</returns>
   private static string RepoRoot()
   {
      string? folder = AppContext.BaseDirectory;
      while( folder != null && !File.Exists( Path.Combine( folder, "GenericVectorBuilder.slnx" ) ) )
      {
         folder = Path.GetDirectoryName( folder );
      }

      return folder ?? throw new InvalidOperationException( "GenericVectorBuilder.slnx was not found above the test binary" );
   }

   /// <summary>
   /// A client with the API key from the shared secrets file.
   /// </summary>
   /// <returns>The client.</returns>
   private static HttpClient NewClient()
   {
      string key = File.ReadLines( new TypesenseSinkOptions().SecretsFile ).Last( l => l.StartsWith( "TYPESENSE_API_KEY=", StringComparison.Ordinal ) )["TYPESENSE_API_KEY=".Length..].TrimEnd( '\r' );
      var http = new HttpClient { BaseAddress = new Uri( BASE_URL ), Timeout = TimeSpan.FromMinutes( 2 ) };
      http.DefaultRequestHeaders.Add( "X-TYPESENSE-API-KEY", key );
      return http;
   }

   /// <summary>
   /// Reads the document count and a k-above-count graph query straight from the engine, without the sink,
   /// as a second source for the sink's state.
   /// </summary>
   /// <param name="http">Client with the API key.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   private static async Task AssertRawCountsAsync( HttpClient http, string collection, CancellationToken ct )
   {
      using JsonDocument info = JsonDocument.Parse( await http.GetStringAsync( $"collections/gvb_{collection}", ct ) );
      Assert.Equal( COUNT, info.RootElement.GetProperty( "num_documents" ).GetInt64() );
      string axis = string.Join( ",", Enumerable.Range( 0, DIMENSION ).Select( i => i == 0 ? "1" : "0" ) );
      string body = JsonSerializer.Serialize( new { searches = new[] { new { collection = $"gvb_{collection}", q = "*", vector_query = $"vec:([{axis}], k:{( COUNT * 2 ).ToString( CultureInfo.InvariantCulture )})", per_page = 1 } } } );
      using var content = new StringContent( body, Encoding.UTF8, "application/json" );
      using HttpResponseMessage response = await http.PostAsync( "multi_search", content, ct );
      using JsonDocument answer = JsonDocument.Parse( await response.Content.ReadAsStringAsync( ct ) );
      Assert.Equal( COUNT, answer.RootElement.GetProperty( "results" )[0].GetProperty( "found" ).GetInt64() );
      using JsonDocument stats = JsonDocument.Parse( await http.GetStringAsync( "stats.json", ct ) );
      Assert.Equal( 0, stats.RootElement.GetProperty( "pending_write_batches" ).GetInt64() );
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

         var document = new Document( $"ready|{seed}|{i}", $"table{i % 3}", $"file{i % 7}.csv", $"text {i}", $"hash{i}", new Dictionary<string, string> { ["row"] = i.ToString() } );
         var id = new Guid( System.Security.Cryptography.MD5.HashData( Encoding.UTF8.GetBytes( $"ready:{seed}:{i}" ) ) );
         records.Add( new VectorRecord( document, new Chunk( id, document.DocKey, 0, $"chunk text {seed}-{i}" ), v ) );
      }

      return records;
   }

   #endregion Private Methods
}
