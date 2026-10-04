using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Proves the Vespa index step against the real container (deploy/engines/vespa.compose.yaml must be up,
/// query port 8090 and config port 19071): after the feed the engine reports every document searchable and
/// its own query trace says the HNSW search returns all of them, and the trace tells an HNSW search from a
/// scan, which is what makes it usable as evidence.
/// Why these cases: the dead benchmark draft ran searches on engines whose index was unfinished or absent
/// and called them HNSW. These tests fail if the state ever says "ready" without the engine's evidence.
/// Each test uses its own gvbbench_ collection (a Vespa schema, deployed and removed through the sink).
/// Run: dotnet test tests/GenericVectorBuilder.Engines.Tests --filter "FullyQualifiedName~VespaReadinessTests" --logger "console;verbosity=detailed"
/// </summary>
[Trait( "Category", "Live" )]
public sealed class VespaReadinessTests
{
   #region Data Members

   private const int DIMENSION = 1024;
   private const int COUNT = 2000;
   private const string QUERY_URL = "http://127.0.0.1:8090";
   private const string CONFIG_URL = "http://127.0.0.1:19071";
   private const string NN_BLUEPRINT = "search::queryeval::NearestNeighborBlueprint";

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xUnit's output channel.
   /// </summary>
   /// <param name="output">Where timings are printed.</param>
   public VespaReadinessTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Feeds 2,000 vectors, finishes the load and requires the engine to report every vector in the HNSW
   /// graph. The state is also read straight after the feed returned: Vespa updates the graph while it
   /// applies each write, so it should already be ready, which is what makes the finish step a check.
   /// Two other accounts are compared: a visit of the document store (counts documents without the search
   /// index) and the raw trace of an approximate and an exact query.
   /// </summary>
   [Fact]
   public async Task Feed2000_FinishLoad_EveryVectorIsInTheGraph()
   {
      string collection = NewCollection();
      using var sink = new VespaSink();
      using HttpClient http = NewClient( QUERY_URL );
      CancellationToken ct = CancellationToken.None;
      try
      {
         var deploy = Stopwatch.StartNew();
         await sink.EnsureCollectionAsync( collection, DIMENSION, ct );
         _output.WriteLine( $"schema deployed in {deploy.Elapsed.TotalSeconds:F1} s" );

         var load = Stopwatch.StartNew();
         await sink.UpsertAsync( collection, MakeRecords( COUNT, 2026 ), ct );
         _output.WriteLine( $"feed of {COUNT} vectors acknowledged after {load.Elapsed.TotalSeconds:F2} s" );

         IndexState before = await sink.GetIndexStateAsync( collection, ct );
         _output.WriteLine( $"right after the feed returned: ready={before.Ready}, {before.IndexedVectors}/{before.TotalVectors}: {before.Detail}" );
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
         Assert.Equal( COUNT, await VisitCountAsync( http, collection, ct ) );
         await AssertTraceDistinguishesHnswFromScanAsync( http, collection, ct );
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
      using var sink = new VespaSink( new VespaSinkOptions( FinishTimeout: TimeSpan.FromMilliseconds( 1 ) ) );
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
   /// A collection that is not deployed reads as not ready and cannot be finished.
   /// </summary>
   [Fact]
   public async Task MissingCollection_StateIsNotReady_FinishFails()
   {
      using var sink = new VespaSink();
      string collection = NewCollection();
      IndexState state = await sink.GetIndexStateAsync( collection, CancellationToken.None );
      Assert.False( state.Ready );
      Assert.Contains( "not deployed", state.Detail );
      var failure = await Assert.ThrowsAsync<InvalidOperationException>( () => sink.FinishLoadAsync( collection, CancellationToken.None ) );
      Assert.Contains( "not deployed", failure.Message );
   }

   /// <summary>
   /// The durability text names two settings; the live config server must have both.
   /// </summary>
   [Fact]
   public async Task Durability_MatchesTheLiveConfig()
   {
      using var sink = new VespaSink();
      using HttpClient config = NewClient( CONFIG_URL );
      using JsonDocument translog = JsonDocument.Parse( await config.GetStringAsync( "config/v1/searchlib.translogserver/content/search/cluster.content/0" ) );
      using JsonDocument proton = JsonDocument.Parse( await config.GetStringAsync( "config/v1/vespa.config.search.core.proton/content/search/cluster.content/0" ) );
      bool fsync = translog.RootElement.GetProperty( "usefsync" ).GetBoolean();
      double[] delays = proton.RootElement.GetProperty( "documentdb" ).EnumerateArray().Select( d => d.GetProperty( "visibilitydelay" ).GetDouble() ).ToArray();
      _output.WriteLine( $"live usefsync = {fsync}; visibilitydelay = {string.Join( ", ", delays )}" );
      Assert.True( fsync );
      Assert.All( delays, d => Assert.Equal( 0.0, d ) );
      Assert.Contains( "usefsync=true", sink.Durability );
      Assert.Contains( "visibilitydelay=0", sink.Durability );
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
      return "gvbbench_vsready_" + Guid.NewGuid().ToString( "N" )[..10];
   }

   /// <summary>
   /// A client for one Vespa port.
   /// </summary>
   /// <param name="baseUrl">Root URL.</param>
   /// <returns>The client.</returns>
   private static HttpClient NewClient( string baseUrl )
   {
      return new HttpClient { BaseAddress = new Uri( baseUrl + "/" ), Timeout = TimeSpan.FromMinutes( 2 ) };
   }

   /// <summary>
   /// Counts documents by visiting the document store through the document API. Why: it does not use the
   /// search index, so it is a separate account of how many documents the feed stored.
   /// </summary>
   /// <param name="http">Client for the query and feed port.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The number of documents visited.</returns>
   private static async Task<long> VisitCountAsync( HttpClient http, string collection, CancellationToken ct )
   {
      long total = 0;
      string? continuation = null;
      var stop = DateTime.UtcNow + TimeSpan.FromMinutes( 2 );
      do
      {
         string url = $"document/v1/gvb/gvb_{collection}/docid?cluster=content&selection=true&fieldSet=%5Bid%5D&wantedDocumentCount=500"
            + ( continuation == null ? string.Empty : $"&continuation={Uri.EscapeDataString( continuation )}" );
         using JsonDocument page = JsonDocument.Parse( await http.GetStringAsync( url, ct ) );
         total += page.RootElement.GetProperty( "documentCount" ).GetInt64();
         continuation = page.RootElement.TryGetProperty( "continuation", out JsonElement c ) ? c.GetString() : null;
         Assert.True( DateTime.UtcNow < stop, "the visit did not finish within two minutes" );
      }
      while( continuation != null );

      return total;
   }

   /// <summary>
   /// Runs the same nearest-neighbour query as an approximate and as an exact search with the engine's trace
   /// on, and requires the trace to say "index top k" for the first and "exact" for the second. Why: the sink's
   /// state relies on that field to tell an HNSW search from a scan, so the field must really tell them apart.
   /// </summary>
   /// <param name="http">Client for the query and feed port.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   private static async Task AssertTraceDistinguishesHnswFromScanAsync( HttpClient http, string collection, CancellationToken ct )
   {
      var axis = new float[DIMENSION];
      axis[0] = 1f;
      foreach( ( string approximate, string expected ) in new[] { ( "true", "index top k" ), ( "false", "exact" ) } )
      {
         var query = new Dictionary<string, object>
         {
            ["yql"] = $"select documentid from gvb_{collection} where {{targetHits:10,approximate:{approximate}}}nearestNeighbor(embedding,q{DIMENSION})",
            [$"input.query(q{DIMENSION})"] = axis,
            ["hits"] = 0,
            ["ranking"] = "default",
            ["trace.level"] = 1,
            ["trace.explainLevel"] = 1,
         };

         using var content = new StringContent( JsonSerializer.Serialize( query ), Encoding.UTF8, "application/json" );
         using HttpResponseMessage response = await http.PostAsync( "search/", content, ct );
         using JsonDocument answer = JsonDocument.Parse( await response.Content.ReadAsStringAsync( ct ) );
         string? algorithm = FindBlueprint( answer.RootElement )?.GetProperty( "algorithm" ).GetString();
         Assert.Equal( expected, algorithm );
      }
   }

   /// <summary>
   /// Finds the first nearest-neighbour blueprint in a trace.
   /// </summary>
   /// <param name="node">A JSON value of the answer.</param>
   /// <returns>The blueprint object, or null.</returns>
   private static JsonElement? FindBlueprint( JsonElement node )
   {
      if( node.ValueKind == JsonValueKind.Object )
      {
         if( node.TryGetProperty( "[type]", out JsonElement type ) && type.ValueKind == JsonValueKind.String && type.GetString() == NN_BLUEPRINT )
         {
            return node;
         }

         foreach( JsonProperty property in node.EnumerateObject() )
         {
            JsonElement? found = FindBlueprint( property.Value );
            if( found != null )
            {
               return found;
            }
         }
      }
      else if( node.ValueKind == JsonValueKind.Array )
      {
         foreach( JsonElement item in node.EnumerateArray() )
         {
            JsonElement? found = FindBlueprint( item );
            if( found != null )
            {
               return found;
            }
         }
      }

      return null;
   }

   /// <summary>
   /// Deterministic random records with L2-normalized 1024-dimension vectors (Vespa's cosine metric needs
   /// unit length and the sink refuses anything else).
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
