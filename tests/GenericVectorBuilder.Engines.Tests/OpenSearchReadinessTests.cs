using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Proves the OpenSearch index step against the real container (deploy/engines/opensearch.compose.yaml
/// must be up on port 9201): after FinishLoadAsync the engine itself reports one segment searched through
/// its k-NN graph, and a collection whose settings forbid a graph is reported as such instead of as ready.
/// Why these cases: the dead benchmark draft ran searches on engines whose index was unfinished or absent
/// and called them HNSW. These tests fail if the state ever says "ready" without the engine's evidence.
/// Each test uses its own gvbbench_ collection and drops it afterwards.
/// Run: dotnet test tests/GenericVectorBuilder.Engines.Tests --filter "FullyQualifiedName~OpenSearchReadinessTests" --logger "console;verbosity=detailed"
/// </summary>
[Trait( "Category", "Live" )]
public sealed class OpenSearchReadinessTests
{
   #region Data Members

   private const int DIMENSION = 1024;
   private const int COUNT = 2000;
   private const int SMALL_COUNT = 600;
   private const string BASE_URL = "http://127.0.0.1:9201";

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xUnit's output channel.
   /// </summary>
   /// <param name="output">Where timings are printed.</param>
   public OpenSearchReadinessTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Loads 2,000 vectors as four separately refreshed batches (several segments), finishes the load and
   /// requires the engine to report every vector searched through the graph of one segment.
   /// </summary>
   [Fact]
   public async Task Load2000_FinishLoad_EveryVectorIsInTheGraph()
   {
      string collection = NewCollection();
      using var sink = new OpenSearchSink();
      using var http = new HttpClient { BaseAddress = new Uri( BASE_URL ), Timeout = TimeSpan.FromMinutes( 2 ) };
      CancellationToken ct = CancellationToken.None;
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, ct );
         foreach( VectorRecord[] part in MakeRecords( COUNT, 2026 ).Chunk( COUNT / 4 ) )
         {
            await sink.UpsertAsync( collection, part, ct );
            await PostAsync( http, $"gvb_{collection}/_refresh", null, ct );
         }

         IndexState before = await sink.GetIndexStateAsync( collection, ct );
         int segmentsBefore = await SegmentCountAsync( http, collection, ct );
         _output.WriteLine( $"before finish: {segmentsBefore} segments, ready={before.Ready}, {before.IndexedVectors}/{before.TotalVectors}: {before.Detail}" );
         Assert.True( segmentsBefore > 1, "four separately refreshed batches should leave several segments" );
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
         Assert.Equal( 1, await SegmentCountAsync( http, collection, ct ) );
         await AssertGraphLoadedAsync( http, collection, ct );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// With the graph size cut raised above the collection size the plugin builds no graph. The finish step
   /// must fail with a plain message and the state must read as not ready, never as ready.
   /// </summary>
   [Fact]
   public async Task GraphSuppressedBySettings_FinishLoadFailsPlainly_StateIsNotReady()
   {
      string collection = NewCollection();
      using var sink = new OpenSearchSink();
      using var http = new HttpClient { BaseAddress = new Uri( BASE_URL ), Timeout = TimeSpan.FromMinutes( 2 ) };
      CancellationToken ct = CancellationToken.None;
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, ct );
         await PostAsync( http, $"gvb_{collection}/_settings", """{"index.knn.advanced.approximate_threshold": 15000}""", ct, HttpMethod.Put );
         await sink.UpsertAsync( collection, MakeRecords( SMALL_COUNT, 7 ), ct );

         var failure = await Assert.ThrowsAsync<InvalidOperationException>( () => sink.FinishLoadAsync( collection, ct ) );
         _output.WriteLine( $"finish failed as expected: {failure.Message}" );
         Assert.Contains( "NO HNSW GRAPH", failure.Message );

         IndexState state = await sink.GetIndexStateAsync( collection, ct );
         Assert.False( state.Ready );
         Assert.Equal( 0, state.IndexedVectors );
         Assert.Equal( SMALL_COUNT, state.TotalVectors );
         Assert.Contains( "approximate_threshold 15000", state.Detail );
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
      using var sink = new OpenSearchSink( new OpenSearchSinkOptions( FinishTimeout: TimeSpan.FromMilliseconds( 1 ) ) );
      CancellationToken ct = CancellationToken.None;
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, ct );
         await sink.UpsertAsync( collection, MakeRecords( 50, 3 ), ct );
         var failure = await Assert.ThrowsAsync<InvalidOperationException>( () => sink.FinishLoadAsync( collection, ct ) );
         _output.WriteLine( failure.Message );
         Assert.Contains( "was not finished within", failure.Message );
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
      using var sink = new OpenSearchSink();
      string collection = NewCollection();
      IndexState state = await sink.GetIndexStateAsync( collection, CancellationToken.None );
      Assert.False( state.Ready );
      Assert.Contains( "does not exist", state.Detail );
      var failure = await Assert.ThrowsAsync<InvalidOperationException>( () => sink.FinishLoadAsync( collection, CancellationToken.None ) );
      Assert.Contains( "does not exist", failure.Message );
   }

   /// <summary>
   /// The durability text names a setting; the live index must have that setting. The sink also writes
   /// the graph size cut explicitly, and the live index must have it.
   /// </summary>
   [Fact]
   public async Task Durability_AndGraphThreshold_MatchTheLiveIndexSettings()
   {
      string collection = NewCollection();
      using var sink = new OpenSearchSink();
      using var http = new HttpClient { BaseAddress = new Uri( BASE_URL ), Timeout = TimeSpan.FromMinutes( 2 ) };
      CancellationToken ct = CancellationToken.None;
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, ct );
         string live = await LiveSettingAsync( http, collection, "index.translog.durability", ct );
         string threshold = await LiveSettingAsync( http, collection, "index.knn.advanced.approximate_threshold", ct );
         _output.WriteLine( $"live index.translog.durability = {live}; approximate_threshold = {threshold}" );
         Assert.Equal( "request", live, ignoreCase: true );
         Assert.Equal( "0", threshold );
         Assert.Contains( "index.translog.durability=request", sink.Durability );
         Assert.NotEqual( "not stated", sink.Durability );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A fresh benchmark-style collection name.
   /// </summary>
   /// <returns>The name.</returns>
   private static string NewCollection()
   {
      return "gvbbench_osready_" + Guid.NewGuid().ToString( "N" )[..10];
   }

   /// <summary>
   /// Sends one request and requires success.
   /// </summary>
   /// <param name="http">Client to the node.</param>
   /// <param name="path">Path and query.</param>
   /// <param name="json">JSON body, or null.</param>
   /// <param name="ct">Cancellation.</param>
   /// <param name="method">HTTP method; POST when null.</param>
   private static async Task PostAsync( HttpClient http, string path, string? json, CancellationToken ct, HttpMethod? method = null )
   {
      using var request = new HttpRequestMessage( method ?? HttpMethod.Post, path );
      if( json != null )
      {
         request.Content = new StringContent( json, Encoding.UTF8, "application/json" );
      }

      using HttpResponseMessage response = await http.SendAsync( request, ct );
      Assert.True( response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync( ct ) );
   }

   /// <summary>
   /// Reads one index setting as the engine reports it, explicit value or default.
   /// </summary>
   /// <param name="http">Client to the node.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="key">Flat setting name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The value as text.</returns>
   private static async Task<string> LiveSettingAsync( HttpClient http, string collection, string key, CancellationToken ct )
   {
      using JsonDocument settings = JsonDocument.Parse( await http.GetStringAsync( $"gvb_{collection}/_settings/{key}?include_defaults=true&flat_settings=true", ct ) );
      JsonElement index = settings.RootElement.EnumerateObject().First().Value;
      bool explicitValue = index.GetProperty( "settings" ).TryGetProperty( key, out JsonElement set );
      return ( explicitValue ? set : index.GetProperty( "defaults" ).GetProperty( key ) ).GetString()!;
   }

   /// <summary>
   /// Counts the segments that hold documents, from _cat/segments.
   /// </summary>
   /// <param name="http">Client to the node.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The segment count.</returns>
   private static async Task<int> SegmentCountAsync( HttpClient http, string collection, CancellationToken ct )
   {
      using JsonDocument segments = JsonDocument.Parse( await http.GetStringAsync( $"_cat/segments/gvb_{collection}?format=json&h=segment,docs.count", ct ) );
      return segments.RootElement.EnumerateArray().Count( s => int.Parse( s.GetProperty( "docs.count" ).GetString()! ) > 0 );
   }

   /// <summary>
   /// Requires the k-NN plugin's own cache statistics to list one loaded graph for the index. Why: it is a
   /// second, independent account of the graphs (the sink's state comes from a profiled search), taken
   /// from the plugin's stats API.
   /// </summary>
   /// <param name="http">Client to the node.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   private static async Task AssertGraphLoadedAsync( HttpClient http, string collection, CancellationToken ct )
   {
      using JsonDocument stats = JsonDocument.Parse( await http.GetStringAsync( "_plugins/_knn/stats/indices_in_cache", ct ) );
      JsonElement cache = stats.RootElement.GetProperty( "nodes" ).EnumerateObject().First().Value.GetProperty( "indices_in_cache" );
      JsonElement entry = cache.GetProperty( $"gvb_{collection}" );
      Assert.Equal( 1, entry.GetProperty( "graph_count" ).GetInt32() );
      Assert.True( entry.GetProperty( "graph_memory_usage" ).GetInt64() > 0 );
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
