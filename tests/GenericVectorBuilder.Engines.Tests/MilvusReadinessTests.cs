using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Proves the Milvus sink's index readiness check against the real container
/// (deploy/engines/milvus.compose.yaml must be up, REST port 19530 and management port 9091).
/// Why: a benchmark that searches before Milvus serves its HNSW index times a scan, so the sink
/// must wait for the index and say what it saw. The proof that "Ready" means what it says does not
/// come from the sink's own readings: the query node's own search counter says which kind of
/// segment answered the searches.
/// </summary>
[Trait( "Category", "Live" )]
public sealed class MilvusReadinessTests
{
   #region Data Members

   private const int DIMENSION = 1024;
   private const int COUNT = 2000;
   private const int PROBE_SEARCHES = 20;
   private const string MANAGEMENT = "http://127.0.0.1:9091/";
   private static readonly Regex SEARCH_COUNTER = new( "^milvus_querynode_sq_segment_latency_count\\{[^}]*query_type=\"search\"[^}]*segment_state=\"(\\w+)\"[^}]*\\} (\\S+)", RegexOptions.Compiled | RegexOptions.Multiline );

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Receives the xunit output writer.
   /// </summary>
   /// <param name="output">Where timings and the engine's evidence are printed.</param>
   public MilvusReadinessTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Loads 2,000 random vectors, shows the check says "not ready" before the flush, waits with
   /// FinishLoadAsync, requires Ready with every vector indexed, and then proves with the query
   /// node's own search counter that the next searches are answered by the sealed, indexed
   /// segment and by no growing segment.
   /// </summary>
   [Fact]
   public async Task FinishLoad_Waits_UntilSearchesUseTheIndex()
   {
      string collection = EnginesAReadinessData.BenchCollection();
      using var sink = new MilvusSink { Progress = line => _output.WriteLine( line ) };
      List<VectorRecord> records = EnginesAReadinessData.Records( COUNT, DIMENSION, seed: 21 );
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         var clock = Stopwatch.StartNew();
         await sink.UpsertAsync( collection, records, CancellationToken.None );
         double loadSeconds = clock.Elapsed.TotalSeconds;

         IndexState before = await sink.GetIndexStateAsync( collection, CancellationToken.None );
         _output.WriteLine( $"Milvus before FinishLoadAsync: Ready={before.Ready}, {before.Detail}" );
         Assert.False( before.Ready, "rows are still in growing segments, the index cannot be ready yet" );

         clock.Restart();
         string note = await sink.FinishLoadAsync( collection, CancellationToken.None );
         double finishSeconds = clock.Elapsed.TotalSeconds;

         IndexState after = await sink.GetIndexStateAsync( collection, CancellationToken.None );
         _output.WriteLine( $"Milvus {COUNT} x {DIMENSION}: load {loadSeconds:F1} s, FinishLoadAsync {finishSeconds:F1} s. Note: {note}" );
         _output.WriteLine( $"Milvus after: Ready={after.Ready}, indexed {after.IndexedVectors} of {after.TotalVectors}, {after.Detail}" );
         Assert.True( after.Ready, after.Detail );
         Assert.Equal( COUNT, after.TotalVectors );
         Assert.Equal( COUNT, after.IndexedVectors );

         Dictionary<string, double> counterBefore = await SearchCountersAsync();
         for( int i = 0; i < PROBE_SEARCHES; i++ )
         {
            IReadOnlyList<SearchHit> hits = await sink.SearchAsync( collection, records[i].Vector, 10, CancellationToken.None );
            Assert.Equal( records[i].Chunk.ChunkId, hits[0].ChunkId );
         }

         Dictionary<string, double> counterAfter = await SearchCountersAsync();
         double sealedSearches = Delta( counterBefore, counterAfter, "Sealed" );
         double growingSearches = Delta( counterBefore, counterAfter, "Growing" );
         _output.WriteLine( $"Milvus query node search counter after FinishLoadAsync, {PROBE_SEARCHES} searches: Sealed +{sealedSearches}, Growing +{growingSearches}" );
         Assert.True( sealedSearches >= PROBE_SEARCHES, "the searches must be answered by the sealed, indexed segment" );
         Assert.Equal( 0, growingSearches );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// The index description of a collection that was never flushed says Finished with 0 of 0 rows,
   /// which a bare "indexed >= total and pending == 0" test accepts. The sink must still say not
   /// ready, because all rows are in a growing segment that searches scan.
   /// </summary>
   [Fact]
   public async Task GetIndexState_IsNotReady_WhenRowsAreStillGrowing()
   {
      string collection = EnginesAReadinessData.BenchCollection();
      using var sink = new MilvusSink();
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         await sink.UpsertAsync( collection, EnginesAReadinessData.Records( 100, DIMENSION, seed: 23 ), CancellationToken.None );
         using var http = new HttpClient { BaseAddress = new Uri( "http://127.0.0.1:19530/" ), Timeout = TimeSpan.FromSeconds( 30 ) };
         using var request = new StringContent( JsonSerializer.Serialize( new { collectionName = "gvb_" + collection, indexName = "vector_hnsw" } ), System.Text.Encoding.UTF8, "application/json" );
         using HttpResponseMessage response = await http.PostAsync( "v2/vectordb/indexes/describe", request );
         string raw = await response.Content.ReadAsStringAsync();
         _output.WriteLine( $"Raw index description of the unflushed collection: {raw}" );
         using JsonDocument described = JsonDocument.Parse( raw );
         JsonElement index = described.RootElement.GetProperty( "data" )[0];
         Assert.Equal( "Finished", index.GetProperty( "indexState" ).GetString() );
         Assert.Equal( 0, index.GetProperty( "pendingRows" ).GetInt64() );

         IndexState state = await sink.GetIndexStateAsync( collection, CancellationToken.None );
         _output.WriteLine( $"Sink state: Ready={state.Ready}, {state.Detail}" );
         Assert.False( state.Ready );
         Assert.Equal( 100, state.TotalVectors );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// A management port nobody listens on must fail at once with a message that names the setting,
   /// not wait out the index timeout.
   /// </summary>
   [Fact]
   public async Task FinishLoad_FailsAtOnce_WhenManagementPortIsUnreachable()
   {
      string collection = EnginesAReadinessData.BenchCollection();
      using var sink = new MilvusSink( new MilvusSinkOptions( ManagementUrl: "http://127.0.0.1:1", IndexWaitMinutes: 1 ) );
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         await sink.UpsertAsync( collection, EnginesAReadinessData.Records( 50, DIMENSION, seed: 22 ), CancellationToken.None );
         var clock = Stopwatch.StartNew();
         InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>( () => sink.FinishLoadAsync( collection, CancellationToken.None ) );
         Assert.Contains( "ManagementUrl", error.Message );
         Assert.True( clock.Elapsed < TimeSpan.FromSeconds( 50 ), $"took {clock.Elapsed.TotalSeconds:F0} s to fail" );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// The durability statement must be filled in, not the default.
   /// </summary>
   [Fact]
   public void Durability_IsStated()
   {
      using var sink = new MilvusSink();
      Assert.NotEqual( "not stated", ( (IEngineDescription)sink ).Durability );
      Assert.Contains( "fdatasync", sink.Durability );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads the query node's search counters per segment state from the management port.
   /// </summary>
   /// <returns>Search count by segment state (Growing or Sealed), summed over nodes.</returns>
   private static async Task<Dictionary<string, double>> SearchCountersAsync()
   {
      using var http = new HttpClient { Timeout = TimeSpan.FromSeconds( 30 ) };
      string metrics = await http.GetStringAsync( MANAGEMENT + "metrics" );
      var counters = new Dictionary<string, double>();
      foreach( Match match in SEARCH_COUNTER.Matches( metrics ) )
      {
         string state = match.Groups[1].Value;
         counters[state] = counters.GetValueOrDefault( state ) + double.Parse( match.Groups[2].Value, CultureInfo.InvariantCulture );
      }

      return counters;
   }

   /// <summary>
   /// How much one counter grew between two readings.
   /// </summary>
   /// <param name="before">Earlier reading.</param>
   /// <param name="after">Later reading.</param>
   /// <param name="state">Segment state.</param>
   /// <returns>The growth, 0 when the counter does not exist yet.</returns>
   private static double Delta( Dictionary<string, double> before, Dictionary<string, double> after, string state )
   {
      return after.GetValueOrDefault( state ) - before.GetValueOrDefault( state );
   }

   #endregion Private Methods
}
