using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Configuration;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Mapping;
using GenericVectorBuilder.Core.Pipeline;
using GenericVectorBuilder.Core.Sources.Files;
using GenericVectorBuilder.Core.State;
using GenericVectorBuilder.Tests.Fakes;
using GenericVectorBuilder.Web.Runs;
using Microsoft.Extensions.Logging.Abstractions;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// Regression tests for the 2026-10-03 web gaps, driving the real <see cref="RunWorker"/> and
/// <see cref="RunRegistry"/> with fake sinks and a stub embedding service: the key columns the
/// user chose must reach the folder scan and its findings must reach the row mapper, and the
/// "Allow large deletes" choice must reach the pipeline runner. Each regression test failed on
/// the code before the fix; the guard tests next to them pin down the runs that must NOT change
/// (unique keys, no key column, deletes without the choice).
/// </summary>
public class RunWorkerGap2Tests : IDisposable
{
   #region Data Members

   private const string PIPELINE = "gap2web";
   private static readonly JsonSerializerOptions WEB_JSON = new( JsonSerializerDefaults.Web );

   private readonly TempFolder _data = new();
   private readonly TempFolder _stateDir = new();
   private readonly MemorySink _sql = new( "sql" );
   private readonly RunRegistry _registry = new();
   private readonly GvbServices _services;
   private readonly RunWorker _worker;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Starts a worker over fake sinks, a stub embedding service and a fresh state store.
   /// </summary>
   public RunWorkerGap2Tests()
   {
      var state = new SqliteStateStore( Path.Combine( _stateDir.Path, "state.db" ) );
      _services = new GvbServices( new GvbSettings(), StubHandler.Client( new StubHandler( EmbedAnswer ) ), new ISink[] { _sql }, state );
      _worker = new RunWorker( _registry, _services, NullLogger<RunWorker>.Instance );
      _worker.StartAsync( CancellationToken.None ).GetAwaiter().GetResult();
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Gap 1: two files of one table that both hold id 1. The worker did not pass the chosen key
   /// column to the scan, so the mapper never learned that id 1 repeats across files and keyed
   /// the copies by read order (1 and 1#2) instead of by file. Each copy must now carry its own
   /// file in its key, and a second run over the same folder must change nothing.
   /// </summary>
   [Fact]
   public async Task KeyValueInTwoFiles_EachCopyIsKeyedByItsFile_AndAReRunChangesNothing()
   {
      WriteSharedKeyFiles();

      RunSnapshot first = await RunAsync( KeyedRequest( "id" ) );

      List<string> keys = StoredDocKeys();
      Assert.Equal( RunStatus.Completed, first.Status );
      Assert.Equal( 4, keys.Count );
      Assert.Equal( 4, keys.Distinct().Count() );
      Assert.Equal( 2, keys.Count( k => k.Contains( '@' ) ) );
      Assert.All( keys.Where( k => k.EndsWith( "|2" ) || k.EndsWith( "|3" ) ), k => Assert.DoesNotContain( '@', k ) );

      RunSnapshot second = await RunAsync( KeyedRequest( "id" ) );

      Assert.Equal( RunStatus.Completed, second.Status );
      Assert.Equal( 0, second.DocsEmbedded );
      Assert.Equal( 0, second.DocsDeleted );
      Assert.Equal( 4, second.DocsUnchanged );
   }

   /// <summary>
   /// The key column is matched to the file header without regard to case, as the scan does, so
   /// a mapping that says "ID" still finds the repeated values of a column written "id".
   /// </summary>
   [Fact]
   public async Task KeyColumnTypedInAnotherCase_StillQualifiesTheRepeatedValue()
   {
      WriteSharedKeyFiles();

      await RunAsync( KeyedRequest( "ID" ) );

      Assert.Equal( 2, StoredDocKeys().Count( k => k.Contains( '@' ) ) );
   }

   /// <summary>
   /// Guard: a table whose key values are unique across its files keeps plain keys, with no
   /// file qualifier and no #n suffix. A pipeline built before this change must not see its
   /// keys move.
   /// </summary>
   [Fact]
   public async Task UniqueKeyValues_KeepPlainKeys()
   {
      _data.Write( "orders_a.csv", "id,name\n1,alpha\n2,beta\n" );
      _data.Write( "orders_b.csv", "id,name\n3,gamma\n4,delta\n" );

      RunSnapshot run = await RunAsync( KeyedRequest( "id" ) );

      List<string> keys = StoredDocKeys();
      Assert.Equal( RunStatus.Completed, run.Status );
      Assert.Equal( 4, keys.Count );
      Assert.All( keys, k => Assert.DoesNotMatch( "[@#]", k ) );
   }

   /// <summary>
   /// Guard: with no key column chosen the run still reads every row, as before.
   /// </summary>
   [Fact]
   public async Task NoKeyColumnChosen_RunsAsBefore()
   {
      WriteSharedKeyFiles();

      RunSnapshot run = await RunAsync( new RunRequest( PIPELINE, _data.Path, new[] { "sql" }, null, null ) );

      Assert.Equal( RunStatus.Completed, run.Status );
      Assert.Equal( 4, _sql.Records.Count );
      Assert.Equal( 4, StoredDocKeys().Distinct().Count() );
   }

   /// <summary>
   /// Gap 2: the "Allow large deletes" choice never reached the pipeline runner, so the
   /// mass-delete guard could not be turned off from the page. The request is read from JSON the
   /// way the page sends it. Without the choice a run that would remove most of the pipeline
   /// deletes nothing and says why; with it the same run removes the rows.
   /// </summary>
   [Fact]
   public async Task AllowLargeDeletes_LetsARunRemoveMostOfThePipeline()
   {
      _data.Write( "p.csv", Rows( 1, 10 ) );
      await RunAsync( KeyedRequest( "id" ) );
      _data.Write( "p.csv", Rows( 1, 2 ) );

      RunSnapshot refused = await RunAsync( FromJson( KeyedJson( "id", allowLargeDeletes: false ) ) );

      Assert.Equal( 0, refused.DocsDeleted );
      Assert.Equal( 10, _sql.Records.Count );
      Assert.Equal( RunStatus.Partial, refused.Status );
      Assert.Contains( "more than half", refused.Message );

      RunSnapshot allowed = await RunAsync( FromJson( KeyedJson( "id", allowLargeDeletes: true ) ) );

      Assert.Equal( 8, allowed.DocsDeleted );
      Assert.Equal( 2, _sql.Records.Count );
      Assert.Equal( RunStatus.Completed, allowed.Status );
   }

   /// <summary>
   /// A request that does not mention the choice (an older page, a script) has large deletes
   /// off, and the field survives the copy the endpoint makes after cleaning the name and path.
   /// </summary>
   [Fact]
   public void RunRequest_AllowLargeDeletes_IsOffUnlessAsked()
   {
      RunRequest silent = FromJson( "{\"pipeline\":\"p\",\"path\":\"/x\",\"sinks\":[\"sql\"]}" );
      RunRequest asked = FromJson( "{\"pipeline\":\"p\",\"path\":\"/x\",\"sinks\":[\"sql\"],\"allowLargeDeletes\":true}" );

      RunRequest cleaned = asked with { Pipeline = PipelineNames.Sanitize( " P One " ), Path = "/y" };

      Assert.False( silent.AllowLargeDeletes );
      Assert.True( asked.AllowLargeDeletes );
      Assert.True( cleaned.AllowLargeDeletes );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Two files of one table, both holding id 1.
   /// </summary>
   private void WriteSharedKeyFiles()
   {
      _data.Write( "orders_a.csv", "id,name\n1,alpha\n2,beta\n" );
      _data.Write( "orders_b.csv", "id,name\n1,gamma\n3,delta\n" );
   }

   /// <summary>
   /// Builds a request that keys the folder's only table by a column.
   /// </summary>
   /// <param name="keyColumn">Key column as typed.</param>
   /// <returns>The request.</returns>
   private RunRequest KeyedRequest( string keyColumn )
   {
      string table = new FolderScanner().Scan( _data.Path, CancellationToken.None ).Tables.Single().Name;
      var tables = new Dictionary<string, TableMapping> { [table] = new TableMapping( keyColumn, null ) };
      return new RunRequest( PIPELINE, _data.Path, new[] { "sql" }, null, tables );
   }

   /// <summary>
   /// The JSON body the page sends for a keyed run.
   /// </summary>
   /// <param name="keyColumn">Key column.</param>
   /// <param name="allowLargeDeletes">The checkbox.</param>
   /// <returns>The JSON text.</returns>
   private string KeyedJson( string keyColumn, bool allowLargeDeletes )
   {
      string table = new FolderScanner().Scan( _data.Path, CancellationToken.None ).Tables.Single().Name;
      return JsonSerializer.Serialize( new
      {
         pipeline = PIPELINE,
         path = _data.Path,
         sinks = new[] { "sql" },
         model = (string?)null,
         tables = new Dictionary<string, object> { [table] = new { keyColumn, template = (string?)null } },
         allowLargeDeletes,
      }, WEB_JSON );
   }

   /// <summary>
   /// Reads a run request the way the endpoint does.
   /// </summary>
   /// <param name="json">Request body.</param>
   /// <returns>The request.</returns>
   private static RunRequest FromJson( string json )
   {
      return JsonSerializer.Deserialize<RunRequest>( json, WEB_JSON ) ?? throw new InvalidOperationException( "The request did not parse." );
   }

   /// <summary>
   /// Queues a run and waits for the worker to finish it.
   /// </summary>
   /// <param name="request">The request.</param>
   /// <returns>The final snapshot.</returns>
   private async Task<RunSnapshot> RunAsync( RunRequest request )
   {
      RunJob job = _registry.Enqueue( request ).Job ?? throw new InvalidOperationException( "The run was not queued." );
      DateTime until = DateTime.UtcNow.AddSeconds( 30 );
      while( !job.Progress.IsFinished )
      {
         Assert.True( DateTime.UtcNow < until, "The run did not finish in 30 seconds." );
         await Task.Delay( 20 );
      }

      return job.Progress.Snapshot();
   }

   /// <summary>
   /// Document keys currently held by the fake sink.
   /// </summary>
   /// <returns>The keys, one per chunk.</returns>
   private List<string> StoredDocKeys()
   {
      return _sql.Records.Values.Select( r => r.Chunk.DocKey ).OrderBy( k => k, StringComparer.Ordinal ).ToList();
   }

   /// <summary>
   /// A small CSV with ids first..first+count-1.
   /// </summary>
   /// <param name="first">First id.</param>
   /// <param name="count">Number of rows.</param>
   /// <returns>The file text.</returns>
   private static string Rows( int first, int count )
   {
      return "id,name\n" + string.Concat( Enumerable.Range( first, count ).Select( i => $"{i},Tool {i}\n" ) );
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
   /// Stops the worker and removes the temp folders.
   /// </summary>
   public void Dispose()
   {
      _worker.StopAsync( CancellationToken.None ).GetAwaiter().GetResult();
      _services.Dispose();
      _data.Dispose();
      _stateDir.Dispose();
   }

   #endregion IDisposable
}

/// <summary>
/// The page itself: the Build step's "Allow large deletes" box, the per-pipeline Reset button
/// and the rule that nothing on the page opens a browser dialog (a dialog blocks automation).
/// The page's behavior is also run under a script engine outside the test suite; these tests
/// keep the parts a reviewer can break by editing the files.
/// </summary>
public class WebPageGap2Tests
{
   #region Public Methods

   /// <summary>
   /// The Build step has an "Allow large deletes" checkbox that starts unticked, with a plain
   /// warning beside it.
   /// </summary>
   [Fact]
   public void BuildStep_HasAnUntickedLargeDeletesBoxWithAWarning()
   {
      string html = File.ReadAllText( PageFile( "index.html" ) );

      Match box = Regex.Match( html, "<input[^>]*id=\"allow-large-deletes\"[^>]*>" );

      Assert.True( box.Success, "index.html has no allow-large-deletes checkbox" );
      Assert.Contains( "type=\"checkbox\"", box.Value );
      Assert.DoesNotContain( "checked", box.Value );
      Assert.Matches( "Allow large deletes", html );
      Assert.Matches( "id=\"large-deletes-note\"[^>]*>[^<]*more than half", html );
   }

   /// <summary>
   /// The page sends the box as allowLargeDeletes with the run, and the search section lists
   /// each pipeline with a Reset button that posts to the reset endpoint.
   /// </summary>
   [Fact]
   public void Page_SendsTheBox_AndListsEachPipelineWithAResetButton()
   {
      string js = File.ReadAllText( PageFile( "app.js" ) );
      string html = File.ReadAllText( PageFile( "index.html" ) );

      Assert.Matches( @"postJson\(\s*""/api/runs""[^;]*allowLargeDeletes", js );
      Assert.Contains( "/api/pipelines/${encodeURIComponent(name)}/reset", js );
      Assert.Contains( "id=\"pipeline-list\"", html );
      Assert.Contains( "id=\"reset-status\"", html );
      Assert.True( html.IndexOf( "id=\"step-search\"", StringComparison.Ordinal ) < html.IndexOf( "id=\"pipeline-list\"", StringComparison.Ordinal ), "the list belongs in the search section" );
   }

   /// <summary>
   /// Reset takes two clicks: the armed button says exactly what will be deleted, and goes back
   /// after five seconds.
   /// </summary>
   [Fact]
   public void Reset_IsATwoClickConfirmThatRevertsAfterFiveSeconds()
   {
      string js = File.ReadAllText( PageFile( "app.js" ) );

      Assert.Contains( "Click again to delete ${name} from SQL and Qdrant", js );
      Assert.Contains( "RESET_CONFIRM_MS = 5000", js );
      Assert.Contains( "setTimeout(disarmReset, RESET_CONFIRM_MS)", js );
   }

   /// <summary>
   /// No script or markup on the page calls confirm, alert or prompt, or wires them to an
   /// attribute, because a blocking dialog cannot be driven by automation.
   /// </summary>
   [Theory]
   [InlineData( "app.js" )]
   [InlineData( "index.html" )]
   public void Page_NeverOpensABrowserDialog( string file )
   {
      string text = File.ReadAllText( PageFile( file ) );

      MatchCollection calls = Regex.Matches( text, @"(?<![\w$.])(?:window\.)?(?:confirm|alert|prompt)\s*\(" );

      Assert.Empty( calls );
   }

   /// <summary>
   /// The run counters include the stored rows a run kept because their file could not be read
   /// completely, and treat a snapshot without the field as zero.
   /// </summary>
   [Fact]
   public void Counters_ShowKeptRows_AndTreatMissingAsZero()
   {
      string js = File.ReadAllText( PageFile( "app.js" ) );

      Assert.Contains( "s.docsKept ?? 0", js );
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
/// The reset endpoint as the page's Reset button uses it, against the real host with every
/// destination down: the answer is JSON with a plain-English reason the page can show beside
/// the button.
/// </summary>
[Trait( "Category", "WebE2E" )]
public class WebResetGap2Tests : IClassFixture<WebAppFixture>
{
   #region Data Members

   private readonly WebAppFixture _app;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Receives the shared host.
   /// </summary>
   /// <param name="app">The running host.</param>
   public WebResetGap2Tests( WebAppFixture app )
   {
      _app = app;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// A reset that cannot reach a destination answers 502 with a reason that names it and says
   /// to press reset again, so the button's status line has something to show.
   /// </summary>
   [Fact]
   public async Task Reset_WithDestinationsDown_Is502WithAReasonTheButtonCanShow()
   {
      HttpResponseMessage reset = await _app.Http.PostAsync( "api/pipelines/gap2_reset/reset", null );

      Assert.Equal( HttpStatusCode.BadGateway, reset.StatusCode );
      using JsonDocument body = JsonDocument.Parse( await reset.Content.ReadAsStringAsync() );
      string error = body.RootElement.GetProperty( "error" ).GetString() ?? string.Empty;
      Assert.Contains( "Could not remove 'gap2_reset'", error );
      Assert.Contains( "Press reset again", error );
   }

   #endregion Public Methods
}
