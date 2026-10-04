using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GenericVectorBuilder.Core.Configuration;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Embedding;
using GenericVectorBuilder.Core.Sources.Odbc;
using GenericVectorBuilder.Core.State;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Tests.Fakes;
using GenericVectorBuilder.Web.Destinations;
using GenericVectorBuilder.Web.Endpoints;
using GenericVectorBuilder.Web.Git;
using GenericVectorBuilder.Web.Runs;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The destination list in process: SQL Server and Qdrant then every engine with its display
/// name and a probe; probes that answer within two seconds and start nothing; a run or a search
/// naming an engine that is not running (a fake one on a port nothing listens on) refused with
/// its name; and a reset that leaves stopped engines alone unless they hold the pipeline.
/// </summary>
public class DestinationCatalogTests : IDisposable
{
   #region Data Members

   private const string PIPELINE = "orders";
   private const string FAKE = "fakeengine";
   private const string FAKE_DISPLAY = "Fake Engine 1.0";

   private readonly TempFolder _root = new();
   private readonly SqliteStateStore _state;
   private readonly int _deadPort = DeadPort();

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a fresh state store per test.
   /// </summary>
   public DestinationCatalogTests()
   {
      _state = new SqliteStateStore( Path.Combine( _root.Path, "state.db" ) );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The catalog lists SQL Server and Qdrant first (ticked), then every engine sink of the
   /// engine catalog by its engine name, each with a probe. An engine added without a probe
   /// fails here rather than appearing on the page as something nobody can check.
   /// </summary>
   [Fact]
   public void Build_ListsSqlQdrantThenEveryEngine_EachWithAProbe()
   {
      IReadOnlyDictionary<string, ISink> engines = EngineCatalog.CreateAll();
      try
      {
         DestinationCatalog catalog = DestinationCatalog.Build( engines.Values );

         Assert.Equal( new[] { "sql", "qdrant" }, catalog.All.Take( 2 ).Select( d => d.Name ) );
         Assert.Equal( new[] { "sql", "qdrant" }, catalog.DefaultNames );
         Assert.Equal( 2 + engines.Count, catalog.All.Count );
         Assert.True( engines.Count >= 14, $"only {engines.Count} engines were found" );
         foreach( Destination d in catalog.All.Skip( 2 ) )
         {
            Assert.True( d.Optional );
            Assert.False( d.DefaultOn );
            Assert.True( d.Probe != null, $"{d.Name} has no probe in EngineProbes" );
            Assert.Equal( ( (IEngineDescription)engines[d.Name] ).Engine, d.DisplayName );
         }
      }
      finally
      {
         foreach( IDisposable disposable in engines.Values.OfType<IDisposable>() )
         {
            disposable.Dispose();
         }
      }
   }

   /// <summary>
   /// A probe of a port nothing listens on says "not running" with the address; a listening
   /// port is "ok"; a destination without a probe is never "ok".
   /// </summary>
   [Fact]
   public async Task Probe_DeadPortIsNotRunning_ListeningPortIsOk()
   {
      var listener = new TcpListener( IPAddress.Loopback, 0 );
      listener.Start();
      int live = ( (IPEndPoint)listener.LocalEndpoint ).Port;
      try
      {
         var catalog = new DestinationCatalog( new[]
         {
            new Destination( "dead", "Dead", true, false, EngineProbes.Tcp( "127.0.0.1", _deadPort ) ),
            new Destination( "live", "Live", true, false, EngineProbes.Tcp( "127.0.0.1", live ) ),
            new Destination( "unknown", "Unknown", true, false, null ),
         } );

         IReadOnlyDictionary<string, string> health = await catalog.ProbeOptionalAsync( CancellationToken.None );

         Assert.StartsWith( $"not running: nothing answered at 127.0.0.1:{_deadPort}", health["dead"] );
         Assert.Equal( "ok", health["live"] );
         Assert.StartsWith( "not running: no health check", health["unknown"] );
      }
      finally
      {
         listener.Stop();
      }
   }

   /// <summary>
   /// A probe that never returns is cut off at two seconds, and fourteen of them together still
   /// take about two seconds, because they run side by side.
   /// </summary>
   [Fact]
   public async Task Probe_ThatHangs_IsCutOffAtTwoSeconds_AndProbesRunSideBySide()
   {
      var hanging = Enumerable.Range( 0, 14 ).Select( i => new Destination( $"hang{i}", "Hang", true, false, _ => new TaskCompletionSource().Task ) );
      var catalog = new DestinationCatalog( hanging );
      var clock = Stopwatch.StartNew();

      IReadOnlyDictionary<string, string> health = await catalog.ProbeOptionalAsync( CancellationToken.None );

      Assert.True( clock.Elapsed < TimeSpan.FromSeconds( 4 ), $"took {clock.Elapsed.TotalSeconds:0.0}s" );
      Assert.All( health.Values, v => Assert.Equal( "not running: no answer within 2 seconds", v ) );
   }

   /// <summary>
   /// A run naming an engine that is not running is refused with a 400 that names it, and
   /// nothing is queued. SQL Server is never probed here: when it fails mid-run the pipeline
   /// finishes the other destinations, as before.
   /// </summary>
   [Fact]
   public async Task StartRun_UnreachableEngine_IsRefusedNamingIt()
   {
      using Fixture f = NewFixture( EngineProbes.Tcp( "127.0.0.1", _deadPort ) );
      var request = new RunRequest( "p", f.Data, new[] { "sql", FAKE }, null, null );

      ( int status, JsonElement body ) = GitStartRunTests.Read( await f.StartAsync( request ) );

      Assert.Equal( StatusCodes.Status400BadRequest, status );
      Assert.Equal( $"Not running: {FAKE} ({FAKE_DISPLAY}). Start it, or untick it, and press Go again.", body.GetProperty( "error" ).GetString() );
      Assert.Empty( f.Registry.Recent() );
   }

   /// <summary>
   /// The same run is queued once the engine answers, and a run to SQL Server alone is queued
   /// without any probe.
   /// </summary>
   [Fact]
   public async Task StartRun_RunningEngine_AndSqlOnly_AreQueued()
   {
      var listener = new TcpListener( IPAddress.Loopback, 0 );
      listener.Start();
      try
      {
         using Fixture f = NewFixture( EngineProbes.Tcp( "127.0.0.1", ( (IPEndPoint)listener.LocalEndpoint ).Port ) );

         ( int withEngine, _ ) = GitStartRunTests.Read( await f.StartAsync( new RunRequest( "p1", f.Data, new[] { "sql", FAKE }, null, null ) ) );
         ( int sqlOnly, _ ) = GitStartRunTests.Read( await f.StartAsync( new RunRequest( "p2", f.Data, new[] { "sql" }, null, null ) ) );

         Assert.Equal( StatusCodes.Status200OK, withEngine );
         Assert.Equal( StatusCodes.Status200OK, sqlOnly );
         Assert.Equal( 2, f.Registry.Recent().Count );
      }
      finally
      {
         listener.Stop();
      }
   }

   /// <summary>
   /// A search over SQL Server and a stopped engine searches SQL Server, does not touch the
   /// engine, and says the engine is not running in its place; with only stopped engines chosen
   /// the search is a 400.
   /// </summary>
   [Fact]
   public async Task Search_SkipsAStoppedEngine_AndSaysWhy()
   {
      using Fixture f = NewFixture( EngineProbes.Tcp( "127.0.0.1", _deadPort ) );
      await _state.SetFingerprintAsync( PIPELINE, await CurrentFingerprintAsync(), CancellationToken.None );

      ( int status, JsonElement body ) = GitStartRunTests.Read( await f.SearchAsync( new SearchRequest( PIPELINE, "hello", new[] { "sql", FAKE }, 5 ) ) );
      ( int onlyDown, JsonElement refused ) = GitStartRunTests.Read( await f.SearchAsync( new SearchRequest( PIPELINE, "hello", new[] { FAKE }, 5 ) ) );

      Assert.Equal( StatusCodes.Status200OK, status );
      Assert.Equal( new[] { "sql", FAKE }, body.EnumerateObject().Select( p => p.Name ) );
      Assert.Equal( JsonValueKind.Array, body.GetProperty( "sql" ).ValueKind );
      Assert.StartsWith( $"{FAKE_DISPLAY} is not running: nothing answered", body.GetProperty( FAKE ).GetProperty( "error" ).GetString() );
      Assert.Equal( 1, f.Sql.SearchCalls );
      Assert.Equal( 0, f.Engine.SearchCalls );
      Assert.Equal( StatusCodes.Status400BadRequest, onlyDown );
      Assert.StartsWith( "None of the chosen destinations is running.", refused.GetProperty( "error" ).GetString() );
   }

   /// <summary>
   /// A reset never touches an engine that does not hold the pipeline, so a stopped engine
   /// cannot block it; an engine that holds it is dropped.
   /// </summary>
   [Fact]
   public async Task Reset_DropsOnlyEnginesThatHoldThePipeline()
   {
      var sql = new ScriptedSink( "sql" );
      var stopped = new ScriptedSink( "stopped" ) { FailDrop = true };
      var holding = new ScriptedSink( "holding" );
      await SeedStateAsync( "sql", "holding" );
      using GvbServices services = new( new GvbSettings(), StubHandler.Client( new StubHandler( EmbedAnswer ) ), new ISink[] { sql }, _state, new ISink[] { stopped, holding } );

      await services.ResetPipelineAsync( PIPELINE, CancellationToken.None );

      Assert.Equal( 1, sql.DropCalls );
      Assert.Equal( 0, stopped.DropCalls );
      Assert.Equal( 1, holding.DropCalls );
      Assert.Empty( await _state.LoadAsync( PIPELINE, "holding", CancellationToken.None ) );
      Assert.Null( await _state.GetFingerprintAsync( PIPELINE, CancellationToken.None ) );
      Assert.True( services.IsOptional( "holding" ) );
      Assert.False( services.IsOptional( "sql" ) );
   }

   /// <summary>
   /// When an engine that holds the pipeline cannot be dropped, the reset says so, and that
   /// engine's state and the fingerprint are put back, so pressing reset again (with the engine
   /// started) drops it instead of forgetting it holds anything.
   /// </summary>
   [Fact]
   public async Task Reset_EngineHoldingThePipelineFails_KeepsItsStateForTheNextReset()
   {
      var sql = new ScriptedSink( "sql" );
      var engine = new ScriptedSink( "pgfake" ) { FailDrop = true };
      await SeedStateAsync( "sql", "pgfake" );
      using GvbServices services = new( new GvbSettings(), StubHandler.Client( new StubHandler( EmbedAnswer ) ), new ISink[] { sql }, _state, new ISink[] { engine } );

      var ex = await Assert.ThrowsAsync<ResetIncompleteException>( () => services.ResetPipelineAsync( PIPELINE, CancellationToken.None ) );

      Assert.Contains( "pgfake", ex.Message );
      Assert.Single( await _state.LoadAsync( PIPELINE, "pgfake", CancellationToken.None ) );
      Assert.Empty( await _state.LoadAsync( PIPELINE, "sql", CancellationToken.None ) );
      Assert.Equal( "fp-1", await _state.GetFingerprintAsync( PIPELINE, CancellationToken.None ) );

      engine.FailDrop = false;
      await services.ResetPipelineAsync( PIPELINE, CancellationToken.None );

      Assert.Equal( 2, engine.DropCalls );
      Assert.Empty( await _state.LoadAsync( PIPELINE, "pgfake", CancellationToken.None ) );
   }

   /// <summary>
   /// Two destinations with one name are refused when the services are built.
   /// </summary>
   [Fact]
   public void Services_DuplicateDestinationName_IsRefused()
   {
      var ex = Assert.Throws<ArgumentException>( () => new GvbServices( new GvbSettings(), StubHandler.Client( new StubHandler( EmbedAnswer ) ), new ISink[] { new ScriptedSink( "sql" ) }, _state, new ISink[] { new ScriptedSink( "SQL" ) } ) );

      Assert.Contains( "Two destinations are named 'SQL'", ex.Message );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Builds the run and search dependencies with SQL Server (a scripted sink) and one fake
   /// optional engine checked by the given probe.
   /// </summary>
   /// <param name="probe">The engine's probe.</param>
   /// <returns>The fixture.</returns>
   private Fixture NewFixture( Func<CancellationToken, Task> probe )
   {
      string data = Path.Combine( _root.Path, "data" );
      Directory.CreateDirectory( data );
      var settings = new GvbSettings { DataRoots = { data }, UploadRoot = Path.Combine( _root.Path, "uploads" ), ReposRoot = Path.Combine( _root.Path, "repos" ) }.Normalize();
      var sql = new ScriptedSink( "sql" );
      var engine = new ScriptedSink( FAKE );
      var services = new GvbServices( settings, StubHandler.Client( new StubHandler( EmbedAnswer ) ), new ISink[] { sql }, _state, new ISink[] { engine } );
      var catalog = new DestinationCatalog( new[]
      {
         new Destination( "sql", "SQL Server", false, true, null ),
         new Destination( FAKE, FAKE_DISPLAY, true, false, probe ),
      } );
      return new Fixture( data, services, catalog, sql, engine );
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
   /// The fingerprint a fresh embedder reports against the stub (dimension 4).
   /// </summary>
   /// <returns>The fingerprint.</returns>
   private static async Task<string> CurrentFingerprintAsync()
   {
      var embedder = new GpuServiceEmbedder( StubHandler.Client( new StubHandler( EmbedAnswer ) ), EmbedderProfile.Qwen3Emb06B );
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

   /// <summary>
   /// A loopback port that nothing listens on: one the system handed out and then took back.
   /// </summary>
   /// <returns>The port.</returns>
   private static int DeadPort()
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
   /// Removes the temp folder.
   /// </summary>
   public void Dispose()
   {
      _root.Dispose();
   }

   #endregion IDisposable

   /// <summary>
   /// The endpoint dependencies for one test, disposed with it.
   /// </summary>
   private sealed class Fixture : IDisposable
   {
      #region Constructor

      /// <summary>
      /// Holds the pieces.
      /// </summary>
      /// <param name="data">The allowed data folder.</param>
      /// <param name="services">Services.</param>
      /// <param name="catalog">Destination catalog.</param>
      /// <param name="sql">The SQL Server stand-in.</param>
      /// <param name="engine">The fake engine.</param>
      public Fixture( string data, GvbServices services, DestinationCatalog catalog, ScriptedSink sql, ScriptedSink engine )
      {
         Data = data;
         Services = services;
         Catalog = catalog;
         Sql = sql;
         Engine = engine;
      }

      #endregion Constructor

      #region Public Methods

      /// <summary>The allowed data folder.</summary>
      public string Data { get; }

      /// <summary>Services.</summary>
      public GvbServices Services { get; }

      /// <summary>Destination catalog.</summary>
      public DestinationCatalog Catalog { get; }

      /// <summary>The SQL Server stand-in.</summary>
      public ScriptedSink Sql { get; }

      /// <summary>The fake engine.</summary>
      public ScriptedSink Engine { get; }

      /// <summary>The run registry.</summary>
      public RunRegistry Registry { get; } = new();

      /// <summary>
      /// Calls the run endpoint.
      /// </summary>
      /// <param name="request">The request.</param>
      /// <returns>The endpoint's result.</returns>
      public Task<IResult> StartAsync( RunRequest request )
      {
         return RunEndpoints.StartRunAsync( request, Registry, Services, new OdbcCatalog( Services.Settings ), new GitWorkspace( Services.Settings ), Catalog, CancellationToken.None );
      }

      /// <summary>
      /// Calls the search endpoint.
      /// </summary>
      /// <param name="request">The request.</param>
      /// <returns>The endpoint's result.</returns>
      public Task<IResult> SearchAsync( SearchRequest request )
      {
         return RunEndpoints.SearchAsync( request, Services, Catalog, NullLoggerFactory.Instance, CancellationToken.None );
      }

      #endregion Public Methods

      #region IDisposable

      /// <summary>
      /// Disposes the services.
      /// </summary>
      public void Dispose()
      {
         Services.Dispose();
      }

      #endregion IDisposable
   }
}

/// <summary>
/// The page's destination lists, checked as files: built from the configured destinations,
/// engines greyed out until the health check finds them running, a button to check again, and a
/// search that sends the ticked destinations.
/// </summary>
public class DestinationPageTests
{
   #region Public Methods

   /// <summary>
   /// The Build step and the Search step each have a destination list and a check button.
   /// </summary>
   [Fact]
   public void Page_HasWriteToAndSearchInLists_WithCheckButtons()
   {
      string html = File.ReadAllText( PageFile( "index.html" ) );

      Assert.Contains( "<div id=\"sinks\" class=\"dest-list\"></div>", html );
      Assert.Contains( "<div id=\"search-sinks\" class=\"dest-list\"></div>", html );
      Assert.Matches( "<button[^>]*id=\"check-destinations\"", html );
      Assert.Matches( "<button[^>]*id=\"check-search-destinations\"", html );
      Assert.True( html.IndexOf( "id=\"step-search\"", StringComparison.Ordinal ) < html.IndexOf( "id=\"search-sinks\"", StringComparison.Ordinal ), "the search list belongs in the search section" );
   }

   /// <summary>
   /// The lists come from the config's destinations with SQL Server and Qdrant ticked; an engine
   /// starts disabled and is only enabled when health says "ok", and is unticked when it is not;
   /// search sends the ticked destinations.
   /// </summary>
   [Fact]
   public void Script_GreysOutStoppedEngines_AndSearchSendsTheTickedOnes()
   {
      string js = File.ReadAllText( PageFile( "app.js" ) );

      Assert.Contains( "state.config.destinations.map((d) =>", js );
      Assert.Contains( "input.checked = d.defaultOn;", js );
      Assert.Contains( "input.disabled = d.optional;", js );
      Assert.Contains( "input.disabled = !up;", js );
      Assert.Contains( "if (!up) input.checked = false;", js );
      Assert.Contains( "\"not running\"", js );
      Assert.Contains( "document.querySelectorAll(\"#search-sinks input:checked\")", js );
      Assert.Matches( @"postJson\(""/api/search"", \{ pipeline, query: \$\(""query""\)\.value, sinks, top: 5 \}\)", js );
   }

   /// <summary>
   /// Greyed-out destinations look and feel unavailable, and every box is a big target.
   /// </summary>
   [Fact]
   public void Style_GreysOutStoppedEngines()
   {
      string css = File.ReadAllText( PageFile( "style.css" ) );

      Assert.Matches( @"\.dest\.off \{[^}]*opacity", css );
      Assert.Matches( @"\.sinks label \{[^}]*min-height: 44px", css );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Finds a file of the web project's static page by walking up to the solution folder.
   /// </summary>
   /// <param name="name">File name inside wwwroot.</param>
   /// <returns>The full path.</returns>
   private static string PageFile( string name )
   {
      string folder = AppContext.BaseDirectory;
      while( !File.Exists( Path.Combine( folder, "GenericVectorBuilder.slnx" ) ) )
      {
         folder = Path.GetDirectoryName( folder ) ?? throw new InvalidOperationException( "Could not find the solution folder." );
      }

      return Path.Combine( folder, "src", "GenericVectorBuilder.Web", "wwwroot", name );
   }

   #endregion Private Methods
}

/// <summary>
/// The destination endpoints against the real host: /api/config lists SQL Server, Qdrant and
/// every engine by its engine name; /api/health has an entry for each, and starts nothing; a
/// run naming an engine that health reports as not running is refused with its name.
/// </summary>
[Trait( "Category", "WebE2E" )]
public class DestinationEndpointTests : IClassFixture<WebAppFixture>
{
   #region Data Members

   private readonly WebAppFixture _app;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Receives the shared host.
   /// </summary>
   /// <param name="app">The running host.</param>
   public DestinationEndpointTests( WebAppFixture app )
   {
      _app = app;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Config lists every destination with its display name, SQL Server and Qdrant first and the
   /// only ones ticked; health answers for every one of them.
   /// </summary>
   [Fact]
   public async Task ConfigAndHealth_CoverEveryDestination()
   {
      IReadOnlyDictionary<string, ISink> engines = EngineCatalog.CreateAll();
      Dictionary<string, string> expected = engines.Values.ToDictionary( e => e.Name, e => ( (IEngineDescription)e ).Engine );
      foreach( IDisposable disposable in engines.Values.OfType<IDisposable>() )
      {
         disposable.Dispose();
      }

      using JsonDocument config = JsonDocument.Parse( await _app.Http.GetStringAsync( "api/config" ) );
      using JsonDocument health = JsonDocument.Parse( await _app.Http.GetStringAsync( "api/health" ) );

      List<JsonElement> listed = config.RootElement.GetProperty( "destinations" ).EnumerateArray().ToList();
      Assert.Equal( new[] { "sql", "qdrant" }, listed.Take( 2 ).Select( d => d.GetProperty( "name" ).GetString() ) );
      Assert.All( listed.Take( 2 ), d => Assert.True( d.GetProperty( "defaultOn" ).GetBoolean() ) );
      Assert.Equal( expected.Count, listed.Count - 2 );
      foreach( JsonElement d in listed.Skip( 2 ) )
      {
         string name = d.GetProperty( "name" ).GetString()!;
         Assert.Equal( expected[name], d.GetProperty( "displayName" ).GetString() );
         Assert.True( d.GetProperty( "optional" ).GetBoolean() );
         Assert.False( d.GetProperty( "defaultOn" ).GetBoolean() );
         string status = health.RootElement.GetProperty( name ).GetString()!;
         Assert.True( status == "ok" || status.StartsWith( "not running", StringComparison.Ordinal ), $"{name}: {status}" );
      }

      Assert.True( health.RootElement.TryGetProperty( "embedder", out _ ) );
      Assert.True( health.RootElement.TryGetProperty( "sql", out _ ) );
   }

   /// <summary>
   /// A run naming an engine that health reports as not running is a 400 naming it. The
   /// engines are containers that are normally stopped on the test box; if every one of them is
   /// running the test says so instead of passing.
   /// </summary>
   [Fact]
   public async Task Run_WithAnEngineThatIsNotRunning_IsRefusedNamingIt()
   {
      using JsonDocument health = JsonDocument.Parse( await _app.Http.GetStringAsync( "api/health" ) );
      using JsonDocument config = JsonDocument.Parse( await _app.Http.GetStringAsync( "api/config" ) );
      JsonElement? down = config.RootElement.GetProperty( "destinations" ).EnumerateArray()
         .Where( d => d.GetProperty( "optional" ).GetBoolean() )
         .Cast<JsonElement?>()
         .FirstOrDefault( d => health.RootElement.GetProperty( d!.Value.GetProperty( "name" ).GetString()! ).GetString() != "ok" );
      Assert.True( down.HasValue, "Every engine container is running, so there is nothing to refuse. Stop one and run this again." );
      string name = down.Value.GetProperty( "name" ).GetString()!;
      string json = JsonSerializer.Serialize( new { pipeline = "refused", path = _app.DataFolder, sinks = new[] { "sql", name } } );

      HttpResponseMessage response = await _app.PostWithOriginAsync( "api/runs", json, null );

      Assert.Equal( HttpStatusCode.BadRequest, response.StatusCode );
      using JsonDocument body = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
      Assert.StartsWith( $"Not running: {name} ({down.Value.GetProperty( "displayName" ).GetString()})", body.RootElement.GetProperty( "error" ).GetString() );
   }

   #endregion Public Methods
}
