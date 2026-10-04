using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GenericVectorBuilder.Core.Configuration;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Embedding;
using GenericVectorBuilder.Core.State;
using GenericVectorBuilder.Tests.Fakes;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The Origin check that keeps other websites from driving the API.
/// </summary>
public class OriginPolicyTests
{
   #region Public Methods

   /// <summary>
   /// Same-site and header-less requests pass; any other Origin (including "null", a different
   /// port or a look-alike host) is cross-site.
   /// </summary>
   [Theory]
   [InlineData( null, "linus7795.lan:5080", false )]
   [InlineData( "", "linus7795.lan:5080", false )]
   [InlineData( "http://linus7795.lan:5080", "linus7795.lan:5080", false )]
   [InlineData( "http://LINUS7795.lan:5080", "linus7795.lan:5080", false )]
   [InlineData( "http://192.168.1.50:5080", "192.168.1.50:5080", false )]
   [InlineData( "http://localhost", "localhost:80", false )]
   [InlineData( "http://evil.example", "linus7795.lan:5080", true )]
   [InlineData( "http://linus7795.lan:5081", "linus7795.lan:5080", true )]
   [InlineData( "http://linus7795.lan:5080.evil.example", "linus7795.lan:5080", true )]
   [InlineData( "null", "linus7795.lan:5080", true )]
   [InlineData( "file://", "linus7795.lan:5080", true )]
   [InlineData( "not a url", "linus7795.lan:5080", true )]
   [InlineData( "http://linus7795.lan:5080", null, true )]
   public void IsCrossSite_ComparesOriginWithHost( string? origin, string? host, bool crossSite )
   {
      Assert.Equal( crossSite, OriginPolicy.IsCrossSite( origin, host ) );
   }

   #endregion Public Methods
}

/// <summary>
/// The embedding client's handling of bad and missing answers, tested against a stub HTTP
/// handler (no GPU service needed).
/// </summary>
public class GpuServiceEmbedderFixTests
{
   #region Public Methods

   /// <summary>
   /// An empty answer to the probe gives the count message, not an index error.
   /// </summary>
   [Fact]
   public async Task PreflightAsync_EmptyAnswer_GivesTheCountMessage()
   {
      var handler = new StubHandler( _ => StubHandler.Json( "[]" ) );
      var embedder = new GpuServiceEmbedder( StubHandler.Client( handler ), EmbedderProfile.Qwen3Emb06B );

      var ex = await Assert.ThrowsAsync<InvalidOperationException>( () => embedder.PreflightAsync( CancellationToken.None ) );
      Assert.Contains( "returned 0 vectors for 1 inputs", ex.Message );
   }

   /// <summary>
   /// A good probe sets the dimension and the fingerprint carries it.
   /// </summary>
   [Fact]
   public async Task PreflightAsync_GoodAnswer_SetsDimension()
   {
      var handler = new StubHandler( _ => StubHandler.Json( "[[0.1,0.2,0.3]]" ) );
      var embedder = new GpuServiceEmbedder( StubHandler.Client( handler ), EmbedderProfile.Qwen3Emb06B );

      await embedder.PreflightAsync( CancellationToken.None );

      Assert.Equal( 3, embedder.Dimension );
      Assert.Contains( "dims=3", embedder.Fingerprint );
   }

   /// <summary>
   /// A request the service rejects (4xx) is not retried and comes back as an
   /// EmbedderUnavailableException with a plain message.
   /// </summary>
   [Fact]
   public async Task EmbedDocumentsAsync_RejectedRequest_IsNotRetried()
   {
      var handler = new StubHandler( _ => new HttpResponseMessage( HttpStatusCode.UnprocessableEntity ) );
      var embedder = new GpuServiceEmbedder( StubHandler.Client( handler ), EmbedderProfile.Qwen3Emb06B );

      var ex = await Assert.ThrowsAsync<EmbedderUnavailableException>( () => embedder.EmbedDocumentsAsync( new[] { "x" }, CancellationToken.None ) );
      Assert.Contains( "422", ex.Message );
      Assert.Equal( 1, handler.Calls );
   }

   /// <summary>
   /// A service that stays unreachable is tried three times, then reported as unavailable.
   /// </summary>
   [Fact]
   public async Task EmbedDocumentsAsync_ServiceDown_IsUnavailableAfterThreeAttempts()
   {
      var handler = new StubHandler( _ => throw new HttpRequestException( "Connection refused" ) );
      var embedder = new GpuServiceEmbedder( StubHandler.Client( handler ), EmbedderProfile.Qwen3Emb06B, TimeSpan.Zero );

      var ex = await Assert.ThrowsAsync<EmbedderUnavailableException>( () => embedder.EmbedDocumentsAsync( new[] { "x" }, CancellationToken.None ) );
      Assert.Contains( "not answering", ex.Message );
      Assert.Equal( 3, handler.Calls );
   }

   /// <summary>
   /// A service that never answers ends as unavailable when the HTTP timeout fires, but a
   /// cancel by the caller is still a plain cancellation.
   /// </summary>
   [Fact]
   public async Task EmbedQueryAsync_HttpTimeout_IsUnavailable_ButCallerCancelIsNot()
   {
      var hang = new StubHandler( _ => throw new TimeoutException() ) { Hang = true };
      HttpClient http = StubHandler.Client( hang );
      http.Timeout = TimeSpan.FromMilliseconds( 150 );
      var embedder = new GpuServiceEmbedder( http, EmbedderProfile.Qwen3Emb06B );

      await Assert.ThrowsAsync<EmbedderUnavailableException>( () => embedder.EmbedQueryAsync( "q", CancellationToken.None ) );

      using var cancelled = new CancellationTokenSource( 100 );
      HttpClient patient = StubHandler.Client( new StubHandler( _ => throw new TimeoutException() ) { Hang = true } );
      var second = new GpuServiceEmbedder( patient, EmbedderProfile.Qwen3Emb06B );
      await Assert.ThrowsAnyAsync<OperationCanceledException>( () => second.EmbedQueryAsync( "q", cancelled.Token ) );
   }

   #endregion Public Methods
}

/// <summary>
/// Search and reset as GvbServices does them, wired to stub HTTP, fake sinks and a real
/// SQLite state store.
/// </summary>
public class GvbServicesFixTests : IDisposable
{
   #region Data Members

   private const string PIPELINE = "orders";

   private readonly TempFolder _stateDir = new();
   private readonly SqliteStateStore _state;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a fresh state store per test.
   /// </summary>
   public GvbServicesFixTests()
   {
      _state = new SqliteStateStore( Path.Combine( _stateDir.Path, "state.db" ) );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// A pipeline stored under an older query format is refused before any sink is queried,
   /// instead of being searched with the wrong convention without a word.
   /// </summary>
   [Fact]
   public async Task SearchAsync_OlderFingerprint_IsRefusedBeforeAnySinkIsQueried()
   {
      var sink = new ScriptedSink( "sql" );
      var embed = new StubHandler( EmbedAnswer );
      await _state.SetFingerprintAsync( PIPELINE, "gpu-service|qwen3-emb-0.6b|dims=4|doc+|query=OLD FORMAT {0}", CancellationToken.None );
      using GvbServices services = Build( embed, sink );

      var ex = await Assert.ThrowsAsync<InvalidOperationException>( () => services.SearchAsync( PIPELINE, "hello", new[] { "sql" }, 5, CancellationToken.None ) );

      Assert.Contains( "different embedding settings", ex.Message );
      Assert.Equal( 0, sink.SearchCalls );
   }

   /// <summary>
   /// A pipeline whose stored fingerprint matches today's profile is searched normally.
   /// </summary>
   [Fact]
   public async Task SearchAsync_MatchingFingerprint_ReturnsTheSinkHits()
   {
      var sink = new ScriptedSink( "sql" );
      var embed = new StubHandler( EmbedAnswer );
      await _state.SetFingerprintAsync( PIPELINE, await CurrentFingerprintAsync( embed ), CancellationToken.None );
      using GvbServices services = Build( embed, sink );

      Dictionary<string, object> results = await services.SearchAsync( PIPELINE, "hello", new[] { "sql" }, 5, CancellationToken.None );

      Assert.Equal( 1, sink.SearchCalls );
      Assert.Single( results );
      Assert.IsAssignableFrom<IReadOnlyList<SearchHit>>( results["sql"] );
   }

   /// <summary>
   /// When the embedding service answers with an error, search ends as
   /// EmbedderUnavailableException (the web layer answers 503 JSON for that).
   /// </summary>
   [Fact]
   public async Task SearchAsync_EmbedderRejects_IsEmbedderUnavailable()
   {
      var sink = new ScriptedSink( "sql" );
      await _state.SetFingerprintAsync( PIPELINE, await CurrentFingerprintAsync( new StubHandler( EmbedAnswer ) ), CancellationToken.None );
      using GvbServices services = Build( new StubHandler( _ => new HttpResponseMessage( HttpStatusCode.UnprocessableEntity ) ), sink );

      await Assert.ThrowsAsync<EmbedderUnavailableException>( () => services.SearchAsync( PIPELINE, "hello", new[] { "sql" }, 5, CancellationToken.None ) );
      Assert.Equal( 0, sink.SearchCalls );
   }

   /// <summary>
   /// An unknown destination is refused before the GPU is asked to embed anything.
   /// </summary>
   [Fact]
   public async Task SearchAsync_UnknownSink_FailsBeforeEmbedding()
   {
      var embed = new StubHandler( EmbedAnswer );
      await _state.SetFingerprintAsync( PIPELINE, await CurrentFingerprintAsync( embed ), CancellationToken.None );
      int callsBefore = embed.Calls;
      using GvbServices services = Build( embed, new ScriptedSink( "sql" ) );

      await Assert.ThrowsAsync<ArgumentException>( () => services.SearchAsync( PIPELINE, "hello", new[] { "nope" }, 5, CancellationToken.None ) );
      Assert.Equal( callsBefore, embed.Calls );
   }

   /// <summary>
   /// One sink failing to search is reported as its message while the other sink's hits
   /// still come back.
   /// </summary>
   [Fact]
   public async Task SearchAsync_OneSinkFails_OthersStillAnswer()
   {
      var broken = new ScriptedSink( "sql" ) { FailSearch = true };
      var fine = new ScriptedSink( "qdrant" );
      var embed = new StubHandler( EmbedAnswer );
      await _state.SetFingerprintAsync( PIPELINE, await CurrentFingerprintAsync( embed ), CancellationToken.None );
      using GvbServices services = Build( embed, broken, fine );

      Dictionary<string, object> results = await services.SearchAsync( PIPELINE, "hello", new[] { "sql", "qdrant" }, 5, CancellationToken.None );

      Assert.Contains( "sql is down", JsonSerializer.Serialize( results["sql"] ) );
      Assert.IsAssignableFrom<IReadOnlyList<SearchHit>>( results["qdrant"] );
   }

   /// <summary>
   /// A reset where one destination cannot be dropped still forgets the pipeline's state
   /// (so nothing claims data that is gone), still drops the other destination, and keeps
   /// the pipeline listed so the reset can be repeated.
   /// </summary>
   [Fact]
   public async Task ResetPipelineAsync_OneSinkFails_StateIsForgottenAndTheOtherSinkDropped()
   {
      var broken = new ScriptedSink( "sql" ) { FailDrop = true };
      var fine = new ScriptedSink( "qdrant" );
      await SeedStateAsync( "sql", "qdrant" );
      using GvbServices services = Build( new StubHandler( EmbedAnswer ), broken, fine );

      var ex = await Assert.ThrowsAsync<ResetIncompleteException>( () => services.ResetPipelineAsync( PIPELINE, CancellationToken.None ) );

      Assert.Contains( "sql", ex.Message );
      Assert.Equal( 1, fine.DropCalls );
      Assert.Empty( await _state.LoadAsync( PIPELINE, "sql", CancellationToken.None ) );
      Assert.Empty( await _state.LoadAsync( PIPELINE, "qdrant", CancellationToken.None ) );
      Assert.Equal( "fp-1", await _state.GetFingerprintAsync( PIPELINE, CancellationToken.None ) );
      Assert.Contains( PIPELINE, await _state.ListPipelinesAsync( CancellationToken.None ) );
   }

   /// <summary>
   /// A reset where every destination drops leaves no state and no fingerprint.
   /// </summary>
   [Fact]
   public async Task ResetPipelineAsync_AllSinksDrop_ForgetsEverything()
   {
      var a = new ScriptedSink( "sql" );
      var b = new ScriptedSink( "qdrant" );
      await SeedStateAsync( "sql", "qdrant" );
      using GvbServices services = Build( new StubHandler( EmbedAnswer ), a, b );

      await services.ResetPipelineAsync( PIPELINE, CancellationToken.None );

      Assert.Equal( 1, a.DropCalls );
      Assert.Equal( 1, b.DropCalls );
      Assert.Null( await _state.GetFingerprintAsync( PIPELINE, CancellationToken.None ) );
      Assert.Empty( await _state.LoadAsync( PIPELINE, "sql", CancellationToken.None ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Builds services around the stub embedder and fake sinks. The order of the sinks is the
   /// order a reset drops them in.
   /// </summary>
   /// <param name="embed">Stub for the embedding service.</param>
   /// <param name="sinks">Fake sinks.</param>
   /// <returns>The services.</returns>
   private GvbServices Build( StubHandler embed, params ISink[] sinks )
   {
      return new GvbServices( new GvbSettings(), StubHandler.Client( embed ), sinks, _state );
   }

   /// <summary>
   /// Stores a fingerprint and one document state per sink, as a finished run would.
   /// </summary>
   /// <param name="sinkNames">Sinks to give a state row.</param>
   private async Task SeedStateAsync( params string[] sinkNames )
   {
      await _state.SetFingerprintAsync( PIPELINE, "fp-1", CancellationToken.None );
      foreach( string sink in sinkNames )
      {
         await _state.SaveAsync( PIPELINE, sink, new[] { new DocState( "orders|1", "hash", "o.csv", new[] { Guid.NewGuid() } ) }, CancellationToken.None );
      }
   }

   /// <summary>
   /// The fingerprint a fresh embedder reports after a probe against the stub (dimension 4).
   /// </summary>
   /// <param name="embed">Stub for the embedding service.</param>
   /// <returns>The fingerprint.</returns>
   private static async Task<string> CurrentFingerprintAsync( StubHandler embed )
   {
      var embedder = new GpuServiceEmbedder( StubHandler.Client( embed ), EmbedderProfile.Qwen3Emb06B );
      await embedder.PreflightAsync( CancellationToken.None );
      return embedder.Fingerprint;
   }

   /// <summary>
   /// Answers /embed with one 4-dimension vector per input.
   /// </summary>
   /// <param name="request">The request.</param>
   /// <returns>The response.</returns>
   private static HttpResponseMessage EmbedAnswer( HttpRequestMessage request )
   {
      using JsonDocument body = JsonDocument.Parse( request.Content!.ReadAsStringAsync().GetAwaiter().GetResult() );
      int count = body.RootElement.GetProperty( "inputs" ).GetArrayLength();
      return StubHandler.Json( JsonSerializer.Serialize( Enumerable.Range( 0, count ).Select( _ => new[] { 0.1f, 0.2f, 0.3f, 0.4f } ) ) );
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Removes the temp state folder.
   /// </summary>
   public void Dispose()
   {
      _stateDir.Dispose();
   }

   #endregion IDisposable
}

/// <summary>
/// The web host, started for real (the built GenericVectorBuilder.Web.dll) against a stub
/// embedding service and dead SQL and Qdrant ports. These pin the endpoint and queue
/// behavior that cannot be reached any other way: queued cancel, the shutdown of an open
/// progress stream, the cross-site check, upload validation and the JSON errors.
/// </summary>
[Trait( "Category", "WebE2E" )]
public class WebEndToEndTests : IClassFixture<WebAppFixture>
{
   #region Data Members

   private readonly WebAppFixture _app;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Receives the shared host.
   /// </summary>
   /// <param name="app">The running host.</param>
   public WebEndToEndTests( WebAppFixture app )
   {
      _app = app;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Cancelling a run that is still queued finishes it as Cancelled right away, instead of
   /// leaving it Queued until the running job ends.
   /// </summary>
   [Fact]
   public async Task CancelQueuedRun_FinishesItImmediately()
   {
      _app.Embedder.Mode = StubEmbedService.EmbedMode.Hang;
      string running = await _app.StartRunAsync( "cq_running" );
      try
      {
         await _app.WaitForStatusAsync( running, "Running" );
         string queued = await _app.StartRunAsync( "cq_queued" );
         Assert.Equal( "Queued", ( await _app.SnapshotAsync( queued ) ).GetProperty( "status" ).GetString() );

         HttpResponseMessage cancel = await _app.Http.PostAsync( $"api/runs/{queued}/cancel", null );
         Assert.Equal( HttpStatusCode.OK, cancel.StatusCode );

         JsonElement after = await _app.SnapshotAsync( queued );
         Assert.Equal( "Cancelled", after.GetProperty( "status" ).GetString() );
         Assert.NotEqual( JsonValueKind.Null, after.GetProperty( "finishedUtc" ).ValueKind );
         Assert.Equal( "Running", ( await _app.SnapshotAsync( running ) ).GetProperty( "status" ).GetString() );
      }
      finally
      {
         await _app.CancelAndWaitAsync( running );
         _app.Embedder.Mode = StubEmbedService.EmbedMode.Ok;
      }
   }

   /// <summary>
   /// A reset is refused with 409 and a reason while the pipeline has a run, and the run is
   /// untouched.
   /// </summary>
   [Fact]
   public async Task Reset_IsRefusedWhileThePipelineHasARun()
   {
      _app.Embedder.Mode = StubEmbedService.EmbedMode.Hang;
      string running = await _app.StartRunAsync( "rr_busy" );
      try
      {
         await _app.WaitForStatusAsync( running, "Running" );

         HttpResponseMessage reset = await _app.Http.PostAsync( "api/pipelines/rr_busy/reset", null );

         Assert.Equal( HttpStatusCode.Conflict, reset.StatusCode );
         Assert.Contains( "queued or running", await ErrorOf( reset ) );
         Assert.Equal( "Running", ( await _app.SnapshotAsync( running ) ).GetProperty( "status" ).GetString() );
      }
      finally
      {
         await _app.CancelAndWaitAsync( running );
         _app.Embedder.Mode = StubEmbedService.EmbedMode.Ok;
      }
   }

   /// <summary>
   /// A POST that a browser says came from another website is refused with 403; the same
   /// request from this site, or with no Origin at all, goes through.
   /// </summary>
   [Fact]
   public async Task CrossSitePost_IsRefused()
   {
      string body = JsonSerializer.Serialize( new { path = _app.DataFolder } );

      HttpResponseMessage foreign = await _app.PostWithOriginAsync( "api/scan", body, "http://evil.example" );
      HttpResponseMessage same = await _app.PostWithOriginAsync( "api/scan", body, $"http://127.0.0.1:{_app.Port}" );
      HttpResponseMessage none = await _app.PostWithOriginAsync( "api/scan", body, null );

      Assert.Equal( HttpStatusCode.Forbidden, foreign.StatusCode );
      Assert.Contains( "another website", await ErrorOf( foreign ) );
      Assert.Equal( HttpStatusCode.OK, same.StatusCode );
      Assert.Equal( HttpStatusCode.OK, none.StatusCode );
   }

   /// <summary>
   /// An empty, missing or NUL-containing folder is a 400 with a message, not a bare 500.
   /// </summary>
   [Theory]
   [InlineData( "{\"pipeline\":\"p\",\"sinks\":[\"sql\"]}", "api/runs" )]
   [InlineData( "{\"pipeline\":\"p\",\"path\":\"\",\"sinks\":[\"sql\"]}", "api/runs" )]
   [InlineData( "{\"pipeline\":\"p\",\"path\":\"a\\u0000b\",\"sinks\":[\"sql\"]}", "api/runs" )]
   [InlineData( "{\"path\":\"a\\u0000b\"}", "api/scan" )]
   public async Task BadFolder_IsA400WithAMessage( string json, string url )
   {
      HttpResponseMessage response = await _app.PostWithOriginAsync( url, json, null );

      Assert.Equal( HttpStatusCode.BadRequest, response.StatusCode );
      Assert.False( string.IsNullOrWhiteSpace( await ErrorOf( response ) ) );
   }

   /// <summary>
   /// An upload that is not multipart is a 400 with a message, not a 500.
   /// </summary>
   [Fact]
   public async Task Upload_NotMultipart_IsA400()
   {
      string id = await _app.NewUploadAsync();

      HttpResponseMessage response = await _app.PostWithOriginAsync( $"api/uploads/{id}/files", "{}", null );

      Assert.Equal( HttpStatusCode.BadRequest, response.StatusCode );
      Assert.Contains( "multipart", await ErrorOf( response ) );
   }

   /// <summary>
   /// A file name with a NUL character, or one that names a folder, is refused with 400.
   /// </summary>
   [Theory]
   [InlineData( "a\0b.csv" )]
   [InlineData( "folder/" )]
   [InlineData( "../../evil.csv" )]
   public async Task Upload_BadFileName_IsA400( string fileName )
   {
      string id = await _app.NewUploadAsync();

      HttpResponseMessage response = await _app.UploadAsync( id, ( fileName, "a,b\n1,2\n" ) );

      Assert.Equal( HttpStatusCode.BadRequest, response.StatusCode );
      Assert.Contains( "Refused path", await ErrorOf( response ) );
   }

   /// <summary>
   /// When one file in a batch is refused, nothing from that batch is left on disk.
   /// </summary>
   [Fact]
   public async Task Upload_OneBadNameInTheBatch_LeavesNothingBehind()
   {
      string id = await _app.NewUploadAsync();

      HttpResponseMessage response = await _app.UploadAsync( id, ( "ok1.csv", "a\n1\n" ), ( "sub/ok2.csv", "a\n2\n" ), ( "../../evil.csv", "a\n3\n" ), ( "ok4.csv", "a\n4\n" ) );

      Assert.Equal( HttpStatusCode.BadRequest, response.StatusCode );
      string folder = Path.Combine( _app.UploadFolder, id );
      Assert.Empty( Directory.GetFiles( folder, "*", SearchOption.AllDirectories ) );
   }

   /// <summary>
   /// A good batch is saved with its sub-folders.
   /// </summary>
   [Fact]
   public async Task Upload_GoodBatch_IsSavedWithSubFolders()
   {
      string id = await _app.NewUploadAsync();

      HttpResponseMessage response = await _app.UploadAsync( id, ( "ok1.csv", "a\n1\n" ), ( "sub/ok2.csv", "a\n2\n" ) );

      Assert.Equal( HttpStatusCode.OK, response.StatusCode );
      Assert.True( File.Exists( Path.Combine( _app.UploadFolder, id, "ok1.csv" ) ) );
      Assert.True( File.Exists( Path.Combine( _app.UploadFolder, id, "sub", "ok2.csv" ) ) );
   }

   /// <summary>
   /// Searching a pipeline built under an older query format is a 400 that says why, not a
   /// quiet search with the wrong convention.
   /// </summary>
   [Fact]
   public async Task Search_OlderFingerprint_Is400WithTheReason()
   {
      await _app.SeedFingerprintAsync( "se_old", "gpu-service|qwen3-emb-0.6b|dims=4|doc+|query=OLD FORMAT {0}" );

      HttpResponseMessage response = await _app.PostWithOriginAsync( "api/search", JsonSerializer.Serialize( new { pipeline = "se_old", query = "hello" } ), null );

      Assert.Equal( HttpStatusCode.BadRequest, response.StatusCode );
      Assert.Contains( "different embedding settings", await ErrorOf( response ) );
   }

   /// <summary>
   /// With the embedding service failing, search answers 503 with a JSON reason, not an empty 500.
   /// </summary>
   [Fact]
   public async Task Search_EmbedderFailing_Is503WithTheReason()
   {
      await _app.SeedFingerprintAsync( "se_down", await _app.CurrentFingerprintAsync() );
      _app.Embedder.Mode = StubEmbedService.EmbedMode.ServerError;
      try
      {
         HttpResponseMessage response = await _app.PostWithOriginAsync( "api/search", JsonSerializer.Serialize( new { pipeline = "se_down", query = "hello" } ), null );

         Assert.Equal( HttpStatusCode.ServiceUnavailable, response.StatusCode );
         Assert.Contains( "embedding service", await ErrorOf( response ) );
      }
      finally
      {
         _app.Embedder.Mode = StubEmbedService.EmbedMode.Ok;
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads the "error" text of a JSON error response ("" when there is none), so a bare
   /// 500 shows up as an empty message.
   /// </summary>
   /// <param name="response">The response.</param>
   /// <returns>The error text.</returns>
   private static async Task<string> ErrorOf( HttpResponseMessage response )
   {
      string text = await response.Content.ReadAsStringAsync();
      try
      {
         using JsonDocument doc = JsonDocument.Parse( text );
         return doc.RootElement.TryGetProperty( "error", out JsonElement error ) ? error.GetString() ?? string.Empty : string.Empty;
      }
      catch( JsonException )
      {
         return string.Empty;
      }
   }

   #endregion Private Methods
}

/// <summary>
/// Shutdown behavior, on its own host so it can be stopped.
/// </summary>
[Trait( "Category", "WebE2E" )]
public class WebShutdownTests
{
   #region Public Methods

   /// <summary>
   /// With a run going and a progress stream open, SIGTERM stops the app promptly. The stream
   /// used to run until the run ended, which held Kestrel's shutdown for the full 30 seconds.
   /// </summary>
   [Fact]
   public async Task Sigterm_WithAnOpenProgressStream_ExitsPromptly()
   {
      if( !OperatingSystem.IsLinux() )
      {
         return;
      }

      using var app = new WebAppFixture();
      await app.InitializeAsync();
      app.Embedder.Mode = StubEmbedService.EmbedMode.Hang;
      string run = await app.StartRunAsync( "sd_run" );
      await app.WaitForStatusAsync( run, "Running" );
      using HttpResponseMessage stream = await app.Http.GetAsync( $"api/runs/{run}/events", HttpCompletionOption.ResponseHeadersRead );
      await using Stream body = await stream.Content.ReadAsStreamAsync();
      var first = new byte[16];
      Assert.True( await body.ReadAsync( first ) > 0 );

      TimeSpan took = await app.StopAsync( TimeSpan.FromSeconds( 20 ) );

      Assert.True( took < TimeSpan.FromSeconds( 10 ), $"the app took {took.TotalSeconds:0.0}s to stop with a progress stream open" );
   }

   #endregion Public Methods
}

/// <summary>
/// Builds and starts the web app for the end-to-end tests, and talks to it. One instance is
/// shared by a test class (xUnit fixture); tests that stop the app make their own.
/// </summary>
public sealed class WebAppFixture : IAsyncLifetime, IDisposable
{
   #region Data Members

   private readonly TempFolder _root = new();
   private readonly StringBuilder _log = new();
   private Process? _process;
   private bool _disposed;

   #endregion Data Members

   #region Public Methods

   /// <summary>The stub embedding service the app talks to.</summary>
   public StubEmbedService Embedder { get; } = new();

   /// <summary>HTTP client pointed at the app.</summary>
   public HttpClient Http { get; private set; } = new();

   /// <summary>Port the app listens on.</summary>
   public int Port { get; private set; }

   /// <summary>A folder with one small CSV, inside the app's allowed data root.</summary>
   public string DataFolder => Path.Combine( _root.Path, "data" );

   /// <summary>The app's upload root.</summary>
   public string UploadFolder => Path.Combine( _root.Path, "uploads" );

   /// <summary>The app's SQLite state file.</summary>
   public string StatePath => Path.Combine( _root.Path, "state.db" );

   /// <summary>The app's repos folder, where the "Git repo" mode keeps its copies.</summary>
   public string ReposFolder => Path.Combine( _root.Path, "repos" );

   /// <summary>A data folder for git repositories the tests create, kept apart from <see cref="DataFolder"/>.</summary>
   public string GitDataFolder => Path.Combine( _root.Path, "gitsrc" );

   /// <summary>
   /// Starts the stub service and the app, and waits until the app answers.
   /// </summary>
   public async Task InitializeAsync()
   {
      string dll = WebBuild.Dll;
      _root.Write( "data/a.csv", "id,name\n1,alpha\n2,beta\n" );
      Directory.CreateDirectory( UploadFolder );
      Directory.CreateDirectory( GitDataFolder );
      Port = FreePort();
      Http = new HttpClient { BaseAddress = new Uri( $"http://127.0.0.1:{Port}/" ), Timeout = TimeSpan.FromSeconds( 60 ) };

      var info = new ProcessStartInfo( "dotnet", $"\"{dll}\" --Urls=http://127.0.0.1:{Port}" )
      {
         RedirectStandardOutput = true,
         RedirectStandardError = true,
         WorkingDirectory = Path.GetDirectoryName( dll )!,
      };
      info.Environment["GVB_SQL_PASSWORD"] = "unused";
      info.Environment["Gvb__StatePath"] = StatePath;
      info.Environment["Gvb__DataRoots__0"] = DataFolder;
      info.Environment["Gvb__DataRoots__1"] = UploadFolder;
      info.Environment["Gvb__DataRoots__2"] = GitDataFolder;
      info.Environment["Gvb__UploadRoot"] = UploadFolder;
      info.Environment["Gvb__ReposRoot"] = ReposFolder;
      info.Environment["Gvb__GitReachTimeoutSeconds"] = "3";
      info.Environment["Gvb__EmbedUrl"] = Embedder.Url;
      info.Environment["Gvb__SqlServer"] = "127.0.0.1,1";
      info.Environment["Gvb__QdrantGrpcPort"] = "1";
      _process = Process.Start( info ) ?? throw new InvalidOperationException( "Could not start the web app." );
      _process.OutputDataReceived += ( _, e ) => AppendLog( e.Data );
      _process.ErrorDataReceived += ( _, e ) => AppendLog( e.Data );
      _process.BeginOutputReadLine();
      _process.BeginErrorReadLine();
      await WaitUntilUpAsync();
   }

   /// <summary>
   /// Stops the app and the stub service.
   /// </summary>
   public Task DisposeAsync()
   {
      Dispose();
      return Task.CompletedTask;
   }

   /// <summary>
   /// Sends SIGTERM and waits for the app to exit.
   /// </summary>
   /// <param name="limit">Longest to wait.</param>
   /// <returns>How long the app took to exit.</returns>
   public async Task<TimeSpan> StopAsync( TimeSpan limit )
   {
      Process app = _process ?? throw new InvalidOperationException( "The app is not running." );
      var clock = Stopwatch.StartNew();
      using( Process kill = Process.Start( "kill", $"-TERM {app.Id}" )! )
      {
         await kill.WaitForExitAsync();
      }

      using var timeout = new CancellationTokenSource( limit );
      try
      {
         await app.WaitForExitAsync( timeout.Token );
      }
      catch( OperationCanceledException )
      {
         // Reported through the elapsed time; Dispose kills the process.
      }

      return clock.Elapsed;
   }

   /// <summary>
   /// Queues a run over the sample CSV into the SQL destination (never reached while the stub
   /// embedder hangs).
   /// </summary>
   /// <param name="pipeline">Pipeline name.</param>
   /// <returns>The run id.</returns>
   public async Task<string> StartRunAsync( string pipeline )
   {
      var request = new { pipeline, path = DataFolder, sinks = new[] { "sql" }, model = (string?)null, tables = new Dictionary<string, object>() };
      HttpResponseMessage response = await Http.PostAsJsonAsync( "api/runs", request );
      response.EnsureSuccessStatusCode();
      using JsonDocument doc = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
      return doc.RootElement.GetProperty( "runId" ).GetString()!;
   }

   /// <summary>
   /// Fetches a run's current snapshot.
   /// </summary>
   /// <param name="runId">Run id.</param>
   /// <returns>The snapshot JSON.</returns>
   public async Task<JsonElement> SnapshotAsync( string runId )
   {
      string text = await Http.GetStringAsync( $"api/runs/{runId}" );
      return JsonDocument.Parse( text ).RootElement.Clone();
   }

   /// <summary>
   /// Waits until a run reaches a status.
   /// </summary>
   /// <param name="runId">Run id.</param>
   /// <param name="status">Status name, e.g. "Running".</param>
   public async Task WaitForStatusAsync( string runId, string status )
   {
      DateTime until = DateTime.UtcNow.AddSeconds( 20 );
      while( DateTime.UtcNow < until )
      {
         if( ( await SnapshotAsync( runId ) ).GetProperty( "status" ).GetString() == status )
         {
            return;
         }

         await Task.Delay( 50 );
      }

      throw new TimeoutException( $"Run {runId} never reached {status}.\n{Log}" );
   }

   /// <summary>
   /// Cancels a run and waits until it has finished, so the single worker is free for the next test.
   /// </summary>
   /// <param name="runId">Run id.</param>
   public async Task CancelAndWaitAsync( string runId )
   {
      await Http.PostAsync( $"api/runs/{runId}/cancel", null );
      DateTime until = DateTime.UtcNow.AddSeconds( 20 );
      while( DateTime.UtcNow < until )
      {
         if( ( await SnapshotAsync( runId ) ).GetProperty( "finishedUtc" ).ValueKind != JsonValueKind.Null )
         {
            return;
         }

         await Task.Delay( 50 );
      }
   }

   /// <summary>
   /// Posts a JSON body, optionally with an Origin header.
   /// </summary>
   /// <param name="url">Relative URL.</param>
   /// <param name="json">Body.</param>
   /// <param name="origin">Origin header value, or null for none.</param>
   /// <returns>The response.</returns>
   public Task<HttpResponseMessage> PostWithOriginAsync( string url, string json, string? origin )
   {
      var request = new HttpRequestMessage( HttpMethod.Post, url ) { Content = new StringContent( json, Encoding.UTF8, "application/json" ) };
      if( origin != null )
      {
         request.Headers.Add( "Origin", origin );
      }

      return Http.SendAsync( request );
   }

   /// <summary>
   /// Starts an upload.
   /// </summary>
   /// <returns>The upload id.</returns>
   public async Task<string> NewUploadAsync()
   {
      HttpResponseMessage response = await Http.PostAsync( "api/uploads", null );
      response.EnsureSuccessStatusCode();
      using JsonDocument doc = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
      return doc.RootElement.GetProperty( "id" ).GetString()!;
   }

   /// <summary>
   /// Posts files to an upload as a hand-built multipart body, so file names the .NET client
   /// would refuse or rewrite (NUL characters, "..") are sent exactly as given.
   /// </summary>
   /// <param name="id">Upload id.</param>
   /// <param name="files">File name and content pairs.</param>
   /// <returns>The response.</returns>
   public Task<HttpResponseMessage> UploadAsync( string id, params (string Name, string Content)[] files )
   {
      const string BOUNDARY = "----gvbtest";
      var body = new StringBuilder();
      foreach( ( string name, string content ) in files )
      {
         body.Append( $"--{BOUNDARY}\r\nContent-Disposition: form-data; name=\"files\"; filename=\"{name}\"\r\nContent-Type: text/csv\r\n\r\n{content}\r\n" );
      }

      body.Append( $"--{BOUNDARY}--\r\n" );
      var content2 = new ByteArrayContent( Encoding.UTF8.GetBytes( body.ToString() ) );
      content2.Headers.TryAddWithoutValidation( "Content-Type", $"multipart/form-data; boundary={BOUNDARY}" );
      return Http.PostAsync( $"api/uploads/{id}/files", content2 );
   }

   /// <summary>
   /// Stores a fingerprint for a pipeline in the app's state file, as a finished run would.
   /// </summary>
   /// <param name="pipeline">Pipeline name.</param>
   /// <param name="fingerprint">Fingerprint to store.</param>
   public async Task SeedFingerprintAsync( string pipeline, string fingerprint )
   {
      await new SqliteStateStore( StatePath ).SetFingerprintAsync( pipeline, fingerprint, CancellationToken.None );
   }

   /// <summary>
   /// The fingerprint the app computes for the default model against the stub (dimension 4).
   /// </summary>
   /// <returns>The fingerprint.</returns>
   public async Task<string> CurrentFingerprintAsync()
   {
      using var http = new HttpClient { BaseAddress = new Uri( Embedder.Url ) };
      var embedder = new GpuServiceEmbedder( http, EmbedderProfile.Qwen3Emb06B );
      await embedder.PreflightAsync( CancellationToken.None );
      return embedder.Fingerprint;
   }

   /// <summary>The app's console output so far, for failure messages.</summary>
   public string Log
   {
      get
      {
         lock( _log )
         {
            return _log.ToString();
         }
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Appends a line of app output.
   /// </summary>
   /// <param name="line">The line, or null at end of stream.</param>
   private void AppendLog( string? line )
   {
      if( line != null )
      {
         lock( _log )
         {
            _log.AppendLine( line );
         }
      }
   }

   /// <summary>
   /// Waits until the app answers on its port.
   /// </summary>
   private async Task WaitUntilUpAsync()
   {
      DateTime until = DateTime.UtcNow.AddSeconds( 30 );
      while( DateTime.UtcNow < until )
      {
         try
         {
            if( ( await Http.GetAsync( "api/config" ) ).IsSuccessStatusCode )
            {
               return;
            }
         }
         catch( HttpRequestException )
         {
            // Not listening yet.
         }

         await Task.Delay( 100 );
      }

      throw new TimeoutException( $"The web app did not start.\n{Log}" );
   }

   /// <summary>
   /// Picks a free loopback port.
   /// </summary>
   /// <returns>The port.</returns>
   private static int FreePort()
   {
      var listener = new TcpListener( IPAddress.Loopback, 0 );
      listener.Start();
      int port = ( (IPEndPoint)listener.LocalEndpoint ).Port;
      listener.Stop();
      return port;
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Kills the app, stops the stub service and deletes the temp files.
   /// </summary>
   public void Dispose()
   {
      if( _disposed )
      {
         return;
      }

      _disposed = true;
      if( _process is { HasExited: false } )
      {
         _process.Kill( entireProcessTree: true );
         _process.WaitForExit( 5000 );
      }

      Http.Dispose();
      Embedder.Dispose();
      _root.Dispose();
   }

   #endregion IDisposable
}

/// <summary>
/// Finds the web project and builds it once per test run, into a temp folder so the build
/// never touches the project's own bin folders.
/// </summary>
public static class WebBuild
{
   #region Data Members

   private static readonly TimeSpan BUILD_LIMIT = TimeSpan.FromMinutes( 5 );
   private static readonly Lazy<string> _dll = new( BuildOnce );

   #endregion Data Members

   #region Public Methods

   /// <summary>Path of the freshly built GenericVectorBuilder.Web.dll.</summary>
   public static string Dll => _dll.Value;

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Runs dotnet build on the web project, with build servers off and a time limit.
   /// Why build servers are off: a build that starts a reusable MSBuild node or compiler server
   /// hands it this build's output pipes, the node outlives the build, and reading the output to
   /// its end then waits forever. That hung the whole test run once.
   /// </summary>
   /// <returns>The path of the built dll.</returns>
   private static string BuildOnce()
   {
      string repo = AppContext.BaseDirectory;
      while( !File.Exists( Path.Combine( repo, "GenericVectorBuilder.slnx" ) ) )
      {
         repo = Path.GetDirectoryName( repo ) ?? throw new InvalidOperationException( "Could not find the solution folder." );
      }

      string project = Path.Combine( repo, "src", "GenericVectorBuilder.Web", "GenericVectorBuilder.Web.csproj" );
      string hash = Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( repo ) ) )[..10];
      string output = Path.Combine( Path.GetTempPath(), "gvb-web-e2e-" + hash );
      var info = new ProcessStartInfo( "dotnet", $"build \"{project}\" -c Debug -o \"{output}\" --nologo -v q --disable-build-servers" )
      {
         RedirectStandardOutput = true,
         RedirectStandardError = true,
      };
      using Process build = Process.Start( info ) ?? throw new InvalidOperationException( "Could not start dotnet build." );
      Task<string> stdout = build.StandardOutput.ReadToEndAsync();
      Task<string> stderr = build.StandardError.ReadToEndAsync();
      if( !build.WaitForExit( BUILD_LIMIT ) )
      {
         build.Kill( entireProcessTree: true );
         throw new TimeoutException( $"Building the web project took longer than {BUILD_LIMIT.TotalMinutes:0} minutes and was stopped." );
      }

      string text = stdout.Wait( TimeSpan.FromSeconds( 10 ) ) && stderr.Wait( TimeSpan.FromSeconds( 10 ) ) ? stdout.Result + stderr.Result : "(the build output could not be read)";
      return build.ExitCode == 0 ? Path.Combine( output, "GenericVectorBuilder.Web.dll" ) : throw new InvalidOperationException( "Building the web project failed:\n" + text );
   }

   #endregion Private Methods
}

/// <summary>
/// A stand-in for the GPU embedding service: answers /embed with small vectors, or hangs, or
/// fails, as the test decides.
/// </summary>
public sealed class StubEmbedService : IDisposable
{
   #region Data Members

   private readonly HttpListener _listener = new();
   private readonly CancellationTokenSource _stop = new();

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Starts listening on a free loopback port.
   /// </summary>
   public StubEmbedService()
   {
      var probe = new TcpListener( IPAddress.Loopback, 0 );
      probe.Start();
      int port = ( (IPEndPoint)probe.LocalEndpoint ).Port;
      probe.Stop();
      Url = $"http://127.0.0.1:{port}/";
      _listener.Prefixes.Add( Url );
      _listener.Start();
      _ = Task.Run( ServeAsync );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>What the stub does with /embed requests.</summary>
   public enum EmbedMode
   {
      /// <summary>Answer with a 4-dimension vector per input.</summary>
      Ok,

      /// <summary>Never answer.</summary>
      Hang,

      /// <summary>Answer 500.</summary>
      ServerError,
   }

   /// <summary>Base URL of the stub, with a trailing slash.</summary>
   public string Url { get; }

   /// <summary>Current behavior of /embed.</summary>
   public volatile EmbedMode Mode = EmbedMode.Ok;

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Accepts requests until disposed.
   /// </summary>
   private async Task ServeAsync()
   {
      while( !_stop.IsCancellationRequested )
      {
         HttpListenerContext context;
         try
         {
            context = await _listener.GetContextAsync();
         }
         catch( Exception ex ) when( ex is HttpListenerException or ObjectDisposedException or InvalidOperationException )
         {
            return;
         }

         _ = Task.Run( () => HandleAsync( context ) );
      }
   }

   /// <summary>
   /// Answers one request.
   /// </summary>
   /// <param name="context">The request context.</param>
   private async Task HandleAsync( HttpListenerContext context )
   {
      try
      {
         if( context.Request.HttpMethod != "POST" )
         {
            await Reply( context, 200, "{\"status\":\"ok\"}" );
            return;
         }

         string body = await new StreamReader( context.Request.InputStream ).ReadToEndAsync();
         switch( Mode )
         {
            case EmbedMode.Hang:
               await Task.Delay( Timeout.Infinite, _stop.Token );
               break;
            case EmbedMode.ServerError:
               await Reply( context, 500, "{}" );
               break;
            default:
               int count = JsonDocument.Parse( body ).RootElement.GetProperty( "inputs" ).GetArrayLength();
               await Reply( context, 200, JsonSerializer.Serialize( Enumerable.Range( 0, count ).Select( _ => new[] { 0.1f, 0.2f, 0.3f, 0.4f } ) ) );
               break;
         }
      }
      catch( Exception ex ) when( ex is OperationCanceledException or HttpListenerException or ObjectDisposedException or InvalidOperationException or IOException )
      {
         // The test ended or the client went away.
      }
   }

   /// <summary>
   /// Writes a JSON reply.
   /// </summary>
   /// <param name="context">The request context.</param>
   /// <param name="status">HTTP status.</param>
   /// <param name="json">Body.</param>
   private static async Task Reply( HttpListenerContext context, int status, string json )
   {
      byte[] bytes = Encoding.UTF8.GetBytes( json );
      context.Response.StatusCode = status;
      context.Response.ContentType = "application/json";
      context.Response.ContentLength64 = bytes.Length;
      await context.Response.OutputStream.WriteAsync( bytes );
      context.Response.Close();
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Stops listening and releases any hung requests.
   /// </summary>
   public void Dispose()
   {
      _stop.Cancel();
      _listener.Close();
   }

   #endregion IDisposable
}

/// <summary>
/// An HTTP handler whose answer a test scripts, and which counts how often it was called.
/// </summary>
public sealed class StubHandler : HttpMessageHandler
{
   #region Data Members

   private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
   private int _calls;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the handler.
   /// </summary>
   /// <param name="respond">Builds the response for a request; may throw to simulate a failure.</param>
   public StubHandler( Func<HttpRequestMessage, HttpResponseMessage> respond )
   {
      _respond = respond;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>How many requests arrived.</summary>
   public int Calls => Volatile.Read( ref _calls );

   /// <summary>When true the handler never answers (until the request is cancelled).</summary>
   public bool Hang { get; set; }

   /// <summary>
   /// A 200 response with a JSON body.
   /// </summary>
   /// <param name="json">Body.</param>
   /// <returns>The response.</returns>
   public static HttpResponseMessage Json( string json )
   {
      return new HttpResponseMessage( HttpStatusCode.OK ) { Content = new StringContent( json, Encoding.UTF8, "application/json" ) };
   }

   /// <summary>
   /// An HTTP client over a handler, with a base address like the real service's.
   /// </summary>
   /// <param name="handler">The handler.</param>
   /// <returns>The client.</returns>
   public static HttpClient Client( StubHandler handler )
   {
      return new HttpClient( handler ) { BaseAddress = new Uri( "http://stub.test/" ) };
   }

   #endregion Public Methods

   #region Overrides

   /// <inheritdoc />
   protected override async Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken )
   {
      Interlocked.Increment( ref _calls );
      if( Hang )
      {
         await Task.Delay( Timeout.Infinite, cancellationToken );
      }

      return _respond( request );
   }

   #endregion Overrides
}

/// <summary>
/// A sink whose search and drop calls are counted and can be made to fail.
/// </summary>
public sealed class ScriptedSink : ISink
{
   #region Constructor

   /// <summary>
   /// Creates the sink.
   /// </summary>
   /// <param name="name">Sink name.</param>
   public ScriptedSink( string name )
   {
      Name = name;
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name { get; }

   /// <summary>How many searches reached the sink.</summary>
   public int SearchCalls { get; private set; }

   /// <summary>How many drops reached the sink.</summary>
   public int DropCalls { get; private set; }

   /// <summary>When true, searches throw.</summary>
   public bool FailSearch { get; set; }

   /// <summary>When true, drops throw.</summary>
   public bool FailDrop { get; set; }

   /// <inheritdoc />
   public Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct ) => Task.FromResult( false );

   /// <inheritdoc />
   public Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct ) => Task.CompletedTask;

   /// <inheritdoc />
   public Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct ) => Task.CompletedTask;

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      SearchCalls++;
      if( FailSearch )
      {
         throw new InvalidOperationException( $"{Name} is down" );
      }

      return Task.FromResult<IReadOnlyList<SearchHit>>( new[] { new SearchHit( Guid.NewGuid(), "orders|1", "orders", "text", 0.9 ) } );
   }

   /// <inheritdoc />
   public Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      DropCalls++;
      if( FailDrop )
      {
         throw new InvalidOperationException( $"{Name} is down" );
      }

      return Task.CompletedTask;
   }

   #endregion Public Methods
}
