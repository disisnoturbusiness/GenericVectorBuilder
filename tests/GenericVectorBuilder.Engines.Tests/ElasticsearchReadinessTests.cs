using System.Diagnostics;
using System.Text.Json;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Proves the Elasticsearch index step against the real container (deploy/engines/elasticsearch.compose.yaml
/// must be up on port 9200): after FinishLoadAsync the engine itself reports one segment whose HNSW graph
/// covers every vector, and a collection too small for a graph is reported as such instead of as ready.
/// Why these cases: the dead benchmark draft ran searches on engines whose index was unfinished or absent
/// and called them HNSW. These tests fail if the state ever says "ready" without the engine's evidence.
/// Each test uses its own gvbbench_ collection and drops it afterwards.
/// Run: dotnet test tests/GenericVectorBuilder.Engines.Tests --filter "FullyQualifiedName~ElasticsearchReadinessTests" --logger "console;verbosity=detailed"
/// </summary>
[Trait( "Category", "Live" )]
public sealed class ElasticsearchReadinessTests
{
   #region Data Members

   private const int DIMENSION = 1024;
   private const int COUNT = 2000;
   private const int SMALL_COUNT = 600;
   private const string BASE_URL = "http://127.0.0.1:9200";

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xUnit's output channel.
   /// </summary>
   /// <param name="output">Where timings are printed.</param>
   public ElasticsearchReadinessTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Loads 2,000 vectors as four separately refreshed batches (four segments), shows the state is not
   /// ready, then finishes the load and requires the engine to report every vector covered by the graph.
   /// </summary>
   [Fact]
   public async Task Load2000_FinishLoad_EveryVectorIsInTheGraph()
   {
      string collection = NewCollection();
      using var sink = new ElasticsearchSink();
      using var http = new HttpClient { BaseAddress = new Uri( BASE_URL ), Timeout = TimeSpan.FromMinutes( 2 ) };
      CancellationToken ct = CancellationToken.None;
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, ct );
         List<VectorRecord> records = MakeRecords( COUNT, 2026 );
         foreach( VectorRecord[] part in records.Chunk( COUNT / 4 ) )
         {
            await sink.UpsertAsync( collection, part, ct );
            using HttpResponseMessage refreshed = await http.PostAsync( $"gvb_{collection}/_refresh", null, ct );
            refreshed.EnsureSuccessStatusCode();
         }

         IndexState before = await sink.GetIndexStateAsync( collection, ct );
         _output.WriteLine( $"before finish: ready={before.Ready}, {before.IndexedVectors}/{before.TotalVectors}: {before.Detail}" );
         Assert.False( before.Ready, "four separately refreshed batches must not read as a finished index" );
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
         await AssertOneSegmentWithGraphAsync( http, collection, ct );
         _output.WriteLine( $"profiler: one kNN search compared {await VectorComparisonsAsync( http, collection, records[0].Vector, ct )} of {COUNT} vectors" );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// A collection below Lucene's graph size must fail the finish step with a plain message and read as
   /// not ready with "NO HNSW GRAPH", never as ready.
   /// </summary>
   [Fact]
   public async Task BelowGraphSize_FinishLoadFailsPlainly_StateIsNotReady()
   {
      string collection = NewCollection();
      using var sink = new ElasticsearchSink();
      CancellationToken ct = CancellationToken.None;
      try
      {
         using var http = new HttpClient { BaseAddress = new Uri( BASE_URL ), Timeout = TimeSpan.FromMinutes( 2 ) };
         await sink.EnsureCollectionAsync( collection, DIMENSION, ct );
         List<VectorRecord> records = MakeRecords( SMALL_COUNT, 7 );
         await sink.UpsertAsync( collection, records, ct );

         var failure = await Assert.ThrowsAsync<InvalidOperationException>( () => sink.FinishLoadAsync( collection, ct ) );
         _output.WriteLine( $"finish failed as expected: {failure.Message}" );
         Assert.Contains( "NO HNSW GRAPH", failure.Message );

         IndexState state = await sink.GetIndexStateAsync( collection, ct );
         Assert.False( state.Ready );
         Assert.Equal( 0, state.IndexedVectors );
         Assert.Equal( SMALL_COUNT, state.TotalVectors );
         Assert.Contains( "NO HNSW GRAPH", state.Detail );

         long compared = await VectorComparisonsAsync( http, collection, records[0].Vector, ct );
         _output.WriteLine( $"profiler: one kNN search compared {compared} of {SMALL_COUNT} vectors (a scan compares all)" );
         Assert.Equal( SMALL_COUNT, compared );
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
      using var sink = new ElasticsearchSink( new ElasticsearchSinkOptions( FinishTimeout: TimeSpan.FromMilliseconds( 1 ) ) );
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
      using var sink = new ElasticsearchSink();
      string collection = NewCollection();
      IndexState state = await sink.GetIndexStateAsync( collection, CancellationToken.None );
      Assert.False( state.Ready );
      Assert.Contains( "does not exist", state.Detail );
      var failure = await Assert.ThrowsAsync<InvalidOperationException>( () => sink.FinishLoadAsync( collection, CancellationToken.None ) );
      Assert.Contains( "does not exist", failure.Message );
   }

   /// <summary>
   /// The durability text names a setting; the live index must have that setting.
   /// </summary>
   [Fact]
   public async Task Durability_MatchesTheLiveIndexSetting()
   {
      string collection = NewCollection();
      using var sink = new ElasticsearchSink();
      using var http = new HttpClient { BaseAddress = new Uri( BASE_URL ), Timeout = TimeSpan.FromMinutes( 2 ) };
      CancellationToken ct = CancellationToken.None;
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, ct );
         string url = $"gvb_{collection}/_settings/index.translog.durability?include_defaults=true&flat_settings=true";
         using JsonDocument settings = JsonDocument.Parse( await http.GetStringAsync( url, ct ) );
         JsonElement index = settings.RootElement.EnumerateObject().First().Value;
         string live = ( index.TryGetProperty( "settings", out JsonElement set ) && set.TryGetProperty( "index.translog.durability", out JsonElement s ) ? s : index.GetProperty( "defaults" ).GetProperty( "index.translog.durability" ) ).GetString()!;
         _output.WriteLine( $"live index.translog.durability = {live}" );
         Assert.Equal( "request", live, ignoreCase: true );
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
      return "gvbbench_esready_" + Guid.NewGuid().ToString( "N" )[..10];
   }

   /// <summary>
   /// Reads the engine's own segment list and requires one segment holding every vector, with graph bytes.
   /// Why a second source beside the sink's state: the sink's state comes from the same statistics API, so
   /// this checks it against _cat/segments and the raw stats, which are separate calls.
   /// </summary>
   /// <param name="http">Client to the node.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   private static async Task AssertOneSegmentWithGraphAsync( HttpClient http, string collection, CancellationToken ct )
   {
      using JsonDocument segments = JsonDocument.Parse( await http.GetStringAsync( $"_cat/segments/gvb_{collection}?format=json&h=segment,docs.count", ct ) );
      Assert.Equal( 1, segments.RootElement.GetArrayLength() );
      Assert.Equal( COUNT, int.Parse( segments.RootElement[0].GetProperty( "docs.count" ).GetString()! ) );
      using JsonDocument stats = JsonDocument.Parse( await http.GetStringAsync( $"gvb_{collection}/_stats/dense_vector?filter_path=_all.primaries.dense_vector", ct ) );
      JsonElement dense = stats.RootElement.GetProperty( "_all" ).GetProperty( "primaries" ).GetProperty( "dense_vector" );
      Assert.Equal( COUNT, dense.GetProperty( "value_count" ).GetInt64() );
      Assert.True( dense.GetProperty( "off_heap" ).GetProperty( "total_vex_size_bytes" ).GetInt64() > 0, "no HNSW graph file (.vex) in the segment" );
   }

   /// <summary>
   /// Runs one kNN search with the engine's profiler on and returns how many vectors it compared.
   /// Why: this is a second, independent account of how the search ran. A segment with no graph is
   /// scanned, so the count equals the number of vectors; the statistics-based state must agree.
   /// </summary>
   /// <param name="http">Client to the node.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="query">Query vector.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The profiler's vector_operations_count.</returns>
   private static async Task<long> VectorComparisonsAsync( HttpClient http, string collection, float[] query, CancellationToken ct )
   {
      var body = new { size = 1, profile = true, track_total_hits = false, _source = false, knn = new { field = "vector", query_vector = query, k = 1, num_candidates = 100 } };
      using var content = new StringContent( JsonSerializer.Serialize( body ), System.Text.Encoding.UTF8, "application/json" );
      using HttpResponseMessage response = await http.PostAsync( $"gvb_{collection}/_search", content, ct );
      using JsonDocument answer = JsonDocument.Parse( await response.Content.ReadAsStringAsync( ct ) );
      return answer.RootElement.GetProperty( "profile" ).GetProperty( "shards" )[0].GetProperty( "dfs" ).GetProperty( "knn" )[0].GetProperty( "vector_operations_count" ).GetInt64();
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
         var id = new Guid( System.Security.Cryptography.MD5.HashData( System.Text.Encoding.UTF8.GetBytes( $"ready:{seed}:{i}" ) ) );
         records.Add( new VectorRecord( document, new Chunk( id, document.DocKey, 0, $"chunk text {seed}-{i}" ), v ) );
      }

      return records;
   }

   #endregion Private Methods
}
