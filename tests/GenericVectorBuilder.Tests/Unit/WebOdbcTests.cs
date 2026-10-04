using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Configuration;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Mapping;
using GenericVectorBuilder.Core.Pipeline;
using GenericVectorBuilder.Core.Sources.Odbc;
using GenericVectorBuilder.Core.State;
using GenericVectorBuilder.Tests.Fakes;
using GenericVectorBuilder.Web.Endpoints;
using GenericVectorBuilder.Web.Runs;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The database half of a run request and how the registry treats its password: it is read from
/// JSON the way the page sends it, it is absent for a folder run, it never prints, and the
/// registry keeps it only until the worker takes it, so it is not left in memory with every run
/// the service has ever seen.
/// </summary>
public class OdbcRunRequestTests
{
   #region Public Methods

   /// <summary>
   /// The page's body binds: connection, user, password and tables, with the schema optional.
   /// A request without "odbc" has none, so folder runs are unchanged.
   /// </summary>
   [Fact]
   public void RunRequest_ReadsTheOdbcMember_AndAFolderRequestHasNone()
   {
      RunRequest database = FromJson( "{\"pipeline\":\"p\",\"sinks\":[\"sql\"],\"odbc\":{\"connection\":\"AdventureWorks2025\",\"user\":\"sa\",\"password\":\"pw\",\"tables\":[{\"schema\":\"Sales\",\"name\":\"Customer\"},{\"name\":\"Loose\"}]}}" );
      RunRequest folder = FromJson( "{\"pipeline\":\"p\",\"path\":\"/x\",\"sinks\":[\"sql\"]}" );

      Assert.Equal( "AdventureWorks2025", database.Odbc!.Connection );
      Assert.Equal( "sa", database.Odbc.User );
      Assert.Equal( "pw", database.Odbc.Password );
      Assert.Equal( new OdbcSelection( "Sales", "Customer" ), database.Odbc.Tables[0] );
      Assert.Null( database.Odbc.Tables[1].Schema );
      Assert.Equal( "Loose", database.Odbc.Tables[1].Name );
      Assert.Null( folder.Odbc );
      Assert.Equal( "/x", folder.Path );
   }

   /// <summary>
   /// The member sits after every older member and is optional, so every caller written before
   /// it (the worker tests, scripts) still compiles and means a folder run. Only the git member,
   /// added later, comes after it.
   /// </summary>
   [Fact]
   public void Odbc_IsTheLastMember_AndOptional()
   {
      System.Reflection.ParameterInfo[] parameters = typeof( RunRequest ).GetConstructors().Single( c => c.GetParameters().Length > 1 ).GetParameters();
      System.Reflection.ParameterInfo odbc = parameters[^2];

      Assert.Equal( "Odbc", odbc.Name );
      Assert.Equal( "Git", parameters[^1].Name );
      Assert.True( odbc.HasDefaultValue );
      Assert.Null( new RunRequest( "p", "/x", new[] { "sql" }, null, null ).Odbc );
   }

   /// <summary>
   /// Printing a request, a run job or a secret never shows the password or a raw connection
   /// string, which can carry its own.
   /// </summary>
   [Fact]
   public void ToString_NeverShowsAPassword()
   {
      const string PASSWORD = "hunter2-unique";
      var odbc = new OdbcRunRequest( $"Driver={{x}};Server=s;PWD={PASSWORD}", "sa", PASSWORD, new[] { new OdbcSelection( "dbo", "T" ) } );
      var request = new RunRequest( "p", string.Empty, new[] { "sql" }, null, null, false, odbc );

      string[] texts =
      {
         request.ToString(),
         odbc.ToString(),
         new RunSecret( PASSWORD ).ToString(),
         new OdbcConnectRequest( odbc.Connection, "sa", PASSWORD ).ToString(),
         new OdbcPreviewRequest( odbc.Connection, "sa", PASSWORD, "dbo", "T" ).ToString(),
         new OdbcCredentials( "sa", PASSWORD ).ToString(),
      };

      Assert.All( texts, t => Assert.False( t.Contains( PASSWORD, StringComparison.Ordinal ), "a ToString leaked the password" ) );
      Assert.All( texts.Take( 3 ), t => Assert.False( t.Contains( "Server=s", StringComparison.Ordinal ), "a ToString leaked the connection string" ) );
   }

   /// <summary>
   /// Queuing a database run moves the password out of the request the registry keeps, into a
   /// secret that gives it up once. The run list shows nothing of it.
   /// </summary>
   [Fact]
   public void Enqueue_KeepsNoPasswordInTheRequest_AndTheSecretIsHandedOutOnce()
   {
      var registry = new RunRegistry();
      var odbc = new OdbcRunRequest( "dsn", "sa", "secret-pw", new[] { new OdbcSelection( "dbo", "T" ) } );

      RunJob job = registry.Enqueue( new RunRequest( "p", string.Empty, new[] { "sql" }, null, null, false, odbc ) ).Job!;

      Assert.Null( job.Request.Odbc!.Password );
      Assert.Equal( "sa", job.Request.Odbc.User );
      Assert.Equal( "dsn", job.Request.Odbc.Connection );
      Assert.Equal( "secret-pw", job.Login!.Take() );
      Assert.Null( job.Login.Take() );
      string listing = JsonSerializer.Serialize( registry.Recent() ) + JsonSerializer.Serialize( RunRegistry.ConsistentSnapshot( job.Progress ) );
      Assert.False( listing.Contains( "secret-pw", StringComparison.Ordinal ), "the run list leaked the password" );
   }

   /// <summary>
   /// A database run with no typed password, and a folder run, carry no secret at all.
   /// </summary>
   [Fact]
   public void Enqueue_WithoutAPassword_HasNoSecret()
   {
      var registry = new RunRegistry();

      RunJob folder = registry.Enqueue( new RunRequest( "p1", "/x", new[] { "sql" }, null, null ) ).Job!;
      RunJob database = registry.Enqueue( new RunRequest( "p2", string.Empty, new[] { "sql" }, null, null, false, new OdbcRunRequest( "dsn", null, null, new[] { new OdbcSelection( "", "T" ) } ) ) ).Job!;

      Assert.Null( folder.Login );
      Assert.Null( database.Login );
   }

   /// <summary>
   /// Cancelling a run that never started forgets its password at once, instead of holding it
   /// until the worker gets round to skipping the job.
   /// </summary>
   [Fact]
   public void Cancel_BeforeStart_ForgetsThePassword()
   {
      var registry = new RunRegistry();
      var odbc = new OdbcRunRequest( "dsn", "sa", "secret-pw", new[] { new OdbcSelection( "dbo", "T" ) } );
      RunJob job = registry.Enqueue( new RunRequest( "p", string.Empty, new[] { "sql" }, null, null, false, odbc ) ).Job!;

      bool cancelled = registry.Cancel( job.Progress.RunId );

      Assert.True( cancelled );
      Assert.Null( job.Login!.Take() );
      Assert.Equal( RunStatus.Cancelled, job.Progress.Snapshot().Status );
   }

   /// <summary>
   /// Scrubbing masks every secret, longest first, so a whole connection string goes as one
   /// piece before the password inside it is looked at.
   /// </summary>
   [Fact]
   public void Scrub_MasksEverySecret_LongestFirst()
   {
      string connection = "Driver={x};Server=db;PWD=abc123";

      string scrubbed = SecretScrubber.Scrub( $"Failed for '{connection}' with abc123 and again abc123", new[] { "abc123", connection, null, string.Empty } );

      Assert.Equal( "Failed for '***' with *** and again ***", scrubbed );
      Assert.Equal( string.Empty, SecretScrubber.Scrub( null, new[] { "x" } ) );
      Assert.Equal( "nothing to hide", SecretScrubber.Scrub( "nothing to hide", Array.Empty<string>() ) );
   }

   /// <summary>
   /// The secrets of a request: the typed password, and for a raw connection string the string
   /// itself and the password inside it, under either spelling.
   /// </summary>
   [Fact]
   public void SecretsOf_RawConnectionString_IncludesThePasswordInsideIt()
   {
      IReadOnlyList<string> raw = SecretScrubber.SecretsOf( "Driver={x};Server=db;UID=u;PWD=inside1", "typed1" );
      IReadOnlyList<string> spelled = SecretScrubber.SecretsOf( "Driver={x};Password=inside2", null );
      IReadOnlyList<string> named = SecretScrubber.SecretsOf( "AdventureWorks2025", null );

      Assert.Contains( "typed1", raw );
      Assert.Contains( "inside1", raw );
      Assert.Contains( "Driver={x};Server=db;UID=u;PWD=inside1", raw );
      Assert.Contains( "inside2", spelled );
      Assert.Empty( named );
   }

   /// <summary>
   /// The pipeline name offered for a connection: a name as it is, or for a raw string its
   /// DSN, database or file name. Never a login, and always safe as a pipeline name.
   /// </summary>
   [Theory]
   [InlineData( "AdventureWorks2025", "adventureworks2025" )]
   [InlineData( "  My Sales DSN ", "my_sales_dsn" )]
   [InlineData( "Driver={ODBC Driver 18 for SQL Server};Server=db;Database=Sales Data;UID=u;PWD=p", "sales_data" )]
   [InlineData( "DSN=Warehouse;UID=u;PWD=p", "warehouse" )]
   [InlineData( "Driver={SQLite3};DBQ=/data/Orders.2026.db", "orders_2026" )]
   [InlineData( "Driver={x};Server=db", "odbc" )]
   public void SuggestPipeline_UsesTheConnectionName_NeverALogin( string connection, string expected )
   {
      string suggested = OdbcEndpoints.SuggestPipeline( connection );

      Assert.Equal( expected, suggested );
      Assert.Equal( suggested, PipelineNames.Sanitize( suggested ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads a run request the way the endpoint does.
   /// </summary>
   /// <param name="json">Request body.</param>
   /// <returns>The request.</returns>
   private static RunRequest FromJson( string json )
   {
      return JsonSerializer.Deserialize<RunRequest>( json, new JsonSerializerOptions( JsonSerializerDefaults.Web ) ) ?? throw new InvalidOperationException( "The request did not parse." );
   }

   #endregion Private Methods
}

/// <summary>
/// The page itself, checked as files: the Folder / Database switch, the login boxes (the
/// password is a password box), no browser storage of any kind, and the request the Go button
/// builds. The page's behavior is also run under a script engine (Jint) outside the test suite;
/// these tests keep the parts a reviewer can break by editing the files.
/// </summary>
public class OdbcPageTests
{
   #region Public Methods

   /// <summary>
   /// Step 1 has a two-way switch whose Folder side starts selected, and a database panel that
   /// starts hidden next to the folder panel.
   /// </summary>
   [Fact]
   public void StepOne_HasAFolderOrDatabaseSwitch_StartingOnFolder()
   {
      string html = File.ReadAllText( PageFile( "index.html" ) );

      Match folder = Regex.Match( html, "<button[^>]*id=\"mode-folder\"[^>]*>([^<]*)</button>" );
      Match database = Regex.Match( html, "<button[^>]*id=\"mode-odbc\"[^>]*>([^<]*)</button>" );

      Assert.True( folder.Success && database.Success, "the switch buttons are missing" );
      Assert.Equal( "Folder", folder.Groups[1].Value );
      Assert.Equal( "Database (ODBC)", database.Groups[1].Value );
      Assert.Contains( "aria-pressed=\"true\"", folder.Value );
      Assert.Contains( "aria-pressed=\"false\"", database.Value );
      Assert.Matches( "<div id=\"source-odbc\" class=\"hidden\">", html );
      Assert.Matches( "<div id=\"source-folder\">", html );
      Assert.True( html.IndexOf( "id=\"source-folder\"", StringComparison.Ordinal ) < html.IndexOf( "id=\"drop\"", StringComparison.Ordinal ), "the folder controls belong in the folder panel" );
   }

   /// <summary>
   /// The login boxes: the password one is a password box that the browser is told not to
   /// remember, the user one is not autofilled either, and the connection is a dropdown plus a
   /// string box for a hand-typed connection.
   /// </summary>
   [Fact]
   public void DatabasePanel_HasAConnectionDropdown_AndAPasswordBox()
   {
      string html = File.ReadAllText( PageFile( "index.html" ) );

      Match password = Regex.Match( html, "<input[^>]*id=\"odbc-password\"[^>]*>" );
      Match user = Regex.Match( html, "<input[^>]*id=\"odbc-user\"[^>]*>" );

      Assert.True( password.Success && user.Success, "the login boxes are missing" );
      Assert.Contains( "type=\"password\"", password.Value );
      Assert.Contains( "autocomplete=\"new-password\"", password.Value );
      Assert.Contains( "autocomplete=\"off\"", user.Value );
      Assert.Matches( "<select id=\"odbc-connection\"", html );
      Assert.Matches( "<input id=\"odbc-string\" type=\"text\"", html );
      Assert.Matches( "id=\"odbc-login\" class=\"grid hidden\"", html );
      Assert.Matches( "id=\"odbc-picker\" class=\"hidden\"", html );
      Assert.Contains( "id=\"odbc-filter\"", html );
      Assert.Contains( "id=\"odbc-select-tables\"", html );
      Assert.Contains( "id=\"odbc-select-views\"", html );
   }

   /// <summary>
   /// The script never touches localStorage, sessionStorage, IndexedDB or cookies, so a typed
   /// password cannot be saved by the page.
   /// </summary>
   [Theory]
   [InlineData( "app.js" )]
   [InlineData( "index.html" )]
   public void Page_NeverStoresAnythingInTheBrowser( string file )
   {
      string text = File.ReadAllText( PageFile( file ) );

      Assert.DoesNotMatch( @"localStorage|sessionStorage|indexedDB|document\.cookie|caches\.open", text );
   }

   /// <summary>
   /// Go sends the folder as before or, in database mode, the connection, login and ticked
   /// tables under "odbc" with no path; the row id choices are sent under the stable table id;
   /// and the listing and preview calls go to the ODBC endpoints.
   /// </summary>
   [Fact]
   public void Go_SendsEitherAPathOrAnOdbcMember_AndRowIdsByTableId()
   {
      string js = File.ReadAllText( PageFile( "app.js" ) );

      Assert.Matches( @"postJson\(\s*""/api/runs""[^;]*\.\.\.source[^;]*allowLargeDeletes", js );
      Assert.Matches( @"if \(state\.mode !== ""odbc""\) return \{ path: state\.path \};", js );
      Assert.Matches( @"return \{ odbc: \{ connection: o\.connection, user: o\.user, password: o\.password, tables \} \};", js );
      Assert.Contains( "keySelect(p.tableId, p.columns, p.suggestedKey)", js );
      Assert.Contains( "postJson(\"/api/odbc/tables\"", js );
      Assert.Contains( "postJson(\"/api/odbc/preview\"", js );
      Assert.Contains( "api(\"/api/odbc/connections\")", js );
   }

   /// <summary>
   /// The login boxes are read only while they are showing, so a login typed for another
   /// connection is not sent to this one.
   /// </summary>
   [Fact]
   public void Login_IsOnlySentWhileItsBoxesAreShowing()
   {
      string js = File.ReadAllText( PageFile( "app.js" ) );

      Assert.Contains( "const loginShown = !$(\"odbc-login\").classList.contains(\"hidden\");", js );
      Assert.Contains( "const user = loginShown && ", js );
      Assert.Contains( "const password = loginShown && ", js );
   }

   /// <summary>
   /// The new controls are at least 44 pixels tall, as the rest of the page: the switch, the
   /// table rows of the picker and the checkboxes.
   /// </summary>
   [Fact]
   public void NewControls_AreBigClickTargets()
   {
      string css = File.ReadAllText( PageFile( "style.css" ) );

      Assert.Matches( @"\.seg \{[^}]*min-height: (4[4-9]|[5-9]\d)px", css );
      Assert.Matches( @"\.pick \{[^}]*min-height: (4[4-9]|[5-9]\d)px", css );
      Assert.Matches( @"\.pick input \{[^}]*width: 22px; height: 22px", css );
      Assert.Matches( @"input\[type=""password""\]", css );
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
/// The ODBC endpoints and the run validation against the real host, with no database needed:
/// the app is given a user DSN, a configured connection and a raw string that all point at a
/// port nothing listens on, so every connection fails the way a database that is down does.
/// What it proves: connections are listed with their kind; every failure is JSON with a plain
/// message that carries neither the typed password, a password inside a raw string, a stored
/// password nor the connection string; bad requests are 400; the Origin check covers the new
/// POSTs; and a run request must name exactly one of a folder and a database.
/// </summary>
[Trait( "Category", "WebE2E" )]
public class OdbcEndpointTests : IClassFixture<OdbcHermeticApp>
{
   #region Data Members

   private const string TYPED_PASSWORD = "typed-pw-7f3a";
   private const string INSIDE_PASSWORD = "inside-pw-91c2";

   private readonly OdbcHermeticApp _app;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Receives the shared host.
   /// </summary>
   /// <param name="app">The running host.</param>
   public OdbcEndpointTests( OdbcHermeticApp app )
   {
      _app = app;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The list shows the configured connection (it has a stored login, so it asks for none)
   /// and the user DSN (it has none, so the page must ask), each labelled by kind, plus the
   /// installed drivers.
   /// </summary>
   [Fact]
   public async Task Connections_ListsConfiguredAndDsn_WithKindAndWhetherALoginIsNeeded()
   {
      HttpResponseMessage response = await _app.Http.GetAsync( "api/odbc/connections" );

      Assert.Equal( HttpStatusCode.OK, response.StatusCode );
      using JsonDocument body = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
      JsonElement[] connections = body.RootElement.GetProperty( "connections" ).EnumerateArray().ToArray();
      JsonElement configured = connections.Single( c => c.GetProperty( "name" ).GetString() == OdbcHermeticApp.CONFIGURED );
      JsonElement dsn = connections.Single( c => c.GetProperty( "name" ).GetString() == OdbcHermeticApp.DSN );
      Assert.Equal( "configured", configured.GetProperty( "kind" ).GetString() );
      Assert.False( configured.GetProperty( "needsCredentials" ).GetBoolean() );
      Assert.Equal( "user DSN", dsn.GetProperty( "kind" ).GetString() );
      Assert.True( dsn.GetProperty( "needsCredentials" ).GetBoolean() );
      Assert.Equal( JsonValueKind.Array, body.RootElement.GetProperty( "drivers" ).ValueKind );
      Assert.False( ( await response.Content.ReadAsStringAsync() ).Contains( OdbcHermeticApp.STORED_PASSWORD, StringComparison.Ordinal ), "the list leaked a stored password" );
   }

   /// <summary>
   /// A database that cannot be reached is a 502 with the reason in plain words, and the typed
   /// password appears nowhere in the answer or in the service log.
   /// </summary>
   [Fact]
   public async Task Tables_WhenTheDatabaseIsDown_Is502WithAPlainReason_AndNoPassword()
   {
      HttpResponseMessage response = await _app.PostJsonAsync( "api/odbc/tables", new { connection = OdbcHermeticApp.DSN, user = "someone", password = TYPED_PASSWORD } );

      string text = await response.Content.ReadAsStringAsync();
      Assert.Equal( HttpStatusCode.BadGateway, response.StatusCode );
      Assert.StartsWith( "Could not list the tables:", ErrorOf( text ) );
      Assert.False( text.Contains( TYPED_PASSWORD, StringComparison.Ordinal ), "the answer leaked the typed password" );
      Assert.False( _app.Log.Contains( TYPED_PASSWORD, StringComparison.Ordinal ), "the service log leaked the typed password" );
   }

   /// <summary>
   /// A raw connection string with its own password: the failure names neither the string nor
   /// the password inside it, and the log does not either.
   /// </summary>
   [Fact]
   public async Task Tables_WithARawConnectionString_NeverEchoesTheStringOrItsPassword()
   {
      string raw = $"Driver={{ODBC Driver 18 for SQL Server}};Server=127.0.0.1,{_app.HangUpPort};UID=someone;PWD={INSIDE_PASSWORD};TrustServerCertificate=yes";

      HttpResponseMessage tables = await _app.PostJsonAsync( "api/odbc/tables", new { connection = raw } );
      HttpResponseMessage preview = await _app.PostJsonAsync( "api/odbc/preview", new { connection = raw, schema = "dbo", name = "T" } );

      foreach( HttpResponseMessage response in new[] { tables, preview } )
      {
         string text = await response.Content.ReadAsStringAsync();
         Assert.Equal( HttpStatusCode.BadGateway, response.StatusCode );
         Assert.False( text.Contains( INSIDE_PASSWORD, StringComparison.Ordinal ), "the answer leaked the password inside the string" );
         Assert.False( text.Contains( "Server=127.0.0.1", StringComparison.Ordinal ), "the answer leaked the connection string" );
      }

      Assert.False( _app.Log.Contains( INSIDE_PASSWORD, StringComparison.Ordinal ), "the service log leaked the password inside the string" );
   }

   /// <summary>
   /// A configured connection whose password comes from a file: the failure does not repeat
   /// that password either.
   /// </summary>
   [Fact]
   public async Task Tables_ForAConfiguredConnection_DoesNotRepeatItsStoredPassword()
   {
      HttpResponseMessage response = await _app.PostJsonAsync( "api/odbc/tables", new { connection = OdbcHermeticApp.CONFIGURED } );

      string text = await response.Content.ReadAsStringAsync();
      Assert.Equal( HttpStatusCode.BadGateway, response.StatusCode );
      Assert.StartsWith( "Could not list the tables:", ErrorOf( text ) );
      Assert.False( text.Contains( OdbcHermeticApp.STORED_PASSWORD, StringComparison.Ordinal ), "the answer leaked the stored password" );
      Assert.False( _app.Log.Contains( OdbcHermeticApp.STORED_PASSWORD, StringComparison.Ordinal ), "the service log leaked the stored password" );
   }

   /// <summary>
   /// Requests that are wrong in themselves are 400 with a plain sentence: no connection, a
   /// name that is neither configured nor a DSN, a connection string that cannot be read, no
   /// table to preview.
   /// </summary>
   [Theory]
   [InlineData( "api/odbc/tables", "{\"connection\":\"\"}", "Pick an ODBC connection." )]
   [InlineData( "api/odbc/tables", "{\"connection\":\"   \"}", "Pick an ODBC connection." )]
   [InlineData( "api/odbc/tables", "{\"connection\":\"NoSuchConnection\"}", "No configured ODBC connection or data source (DSN) is named 'NoSuchConnection'." )]
   [InlineData( "api/odbc/tables", "{\"connection\":\"Driver={unclosed;PWD=leaky-pw\"}", "That ODBC connection string could not be read. Check its key=value pairs and braces." )]
   [InlineData( "api/odbc/preview", "{\"connection\":\"\",\"name\":\"T\"}", "Pick an ODBC connection." )]
   [InlineData( "api/odbc/preview", "{\"connection\":\"GvbWebTestDsn\",\"schema\":\"dbo\",\"name\":\"\"}", "Pick a table or view to preview." )]
   [InlineData( "api/odbc/preview", "{\"connection\":\"NoSuchConnection\",\"name\":\"T\"}", "No configured ODBC connection or data source (DSN) is named 'NoSuchConnection'." )]
   public async Task BadRequests_Are400_WithAPlainSentence( string url, string json, string expected )
   {
      HttpResponseMessage response = await _app.PostWithOriginAsync( url, json, null );

      string text = await response.Content.ReadAsStringAsync();
      Assert.Equal( HttpStatusCode.BadRequest, response.StatusCode );
      Assert.Equal( expected, ErrorOf( text ) );
      Assert.DoesNotContain( "leaky-pw", text );
   }

   /// <summary>
   /// A request with no body is a 400, not a 500.
   /// </summary>
   [Fact]
   public async Task AnEmptyBody_Is400_NotA500()
   {
      HttpResponseMessage tables = await _app.PostWithOriginAsync( "api/odbc/tables", string.Empty, null );
      HttpResponseMessage preview = await _app.PostWithOriginAsync( "api/odbc/preview", string.Empty, null );

      Assert.Equal( HttpStatusCode.BadRequest, tables.StatusCode );
      Assert.Equal( HttpStatusCode.BadRequest, preview.StatusCode );
   }

   /// <summary>
   /// The Origin check covers the new POSTs: a request a browser says came from another website
   /// is refused before anything is opened, a same-site request goes on, and the list (a read)
   /// is left alone.
   /// </summary>
   [Fact]
   public async Task CrossSitePosts_AreRefused_SameSiteAndReadsAreNot()
   {
      string body = JsonSerializer.Serialize( new { connection = OdbcHermeticApp.DSN, user = "someone", password = TYPED_PASSWORD, schema = "dbo", name = "T" } );
      string same = $"http://127.0.0.1:{_app.Port}";

      HttpResponseMessage foreignTables = await _app.PostWithOriginAsync( "api/odbc/tables", body, "http://evil.example" );
      HttpResponseMessage foreignPreview = await _app.PostWithOriginAsync( "api/odbc/preview", body, "http://evil.example" );
      HttpResponseMessage foreignRun = await _app.PostWithOriginAsync( "api/runs", "{\"pipeline\":\"p\",\"sinks\":[\"sql\"],\"odbc\":{\"connection\":\"x\",\"tables\":[{\"name\":\"t\"}]}}", "null" );
      HttpResponseMessage sameSite = await _app.PostWithOriginAsync( "api/odbc/tables", body, same );
      var foreignRead = new HttpRequestMessage( HttpMethod.Get, "api/odbc/connections" );
      foreignRead.Headers.Add( "Origin", "http://evil.example" );
      HttpResponseMessage read = await _app.Http.SendAsync( foreignRead );

      Assert.Equal( HttpStatusCode.Forbidden, foreignTables.StatusCode );
      Assert.Equal( HttpStatusCode.Forbidden, foreignPreview.StatusCode );
      Assert.Equal( HttpStatusCode.Forbidden, foreignRun.StatusCode );
      Assert.Equal( "That request came from another website, so it was refused.", ErrorOf( await foreignTables.Content.ReadAsStringAsync() ) );
      Assert.Equal( HttpStatusCode.BadGateway, sameSite.StatusCode );
      Assert.Equal( HttpStatusCode.OK, read.StatusCode );
   }

   /// <summary>
   /// A run names exactly one of a folder and a database, and a database run names a connection
   /// that exists and at least one table.
   /// </summary>
   [Theory]
   [InlineData( "both", "Send only one of a folder, a database or a git repository." )]
   [InlineData( "neither", "Pick a folder, a database or a git repository first." )]
   [InlineData( "no-tables", "Pick at least one table or view." )]
   [InlineData( "blank-table", "Pick at least one table or view." )]
   [InlineData( "no-connection", "Pick an ODBC connection." )]
   [InlineData( "unknown-connection", "No configured ODBC connection or data source (DSN) is named 'NoSuchConnection'." )]
   [InlineData( "folder-outside-roots", "That folder is outside the allowed data folders." )]
   [InlineData( "no-sinks", "Pick at least one destination." )]
   public async Task RunRequest_NeedsExactlyOneSource_AndAUsableDatabaseRequest( string kind, string expected )
   {
      string tables = "[{\"schema\":\"dbo\",\"name\":\"T\"}]";
      string odbc = $"\"odbc\":{{\"connection\":\"{OdbcHermeticApp.DSN}\",\"tables\":{tables}}}";
      string json = kind switch
      {
         "both" => $"{{\"pipeline\":\"p\",\"path\":\"{_app.DataFolder}\",\"sinks\":[\"sql\"],{odbc}}}",
         "neither" => "{\"pipeline\":\"p\",\"sinks\":[\"sql\"]}",
         "no-tables" => $"{{\"pipeline\":\"p\",\"sinks\":[\"sql\"],\"odbc\":{{\"connection\":\"{OdbcHermeticApp.DSN}\",\"tables\":[]}}}}",
         "blank-table" => $"{{\"pipeline\":\"p\",\"sinks\":[\"sql\"],\"odbc\":{{\"connection\":\"{OdbcHermeticApp.DSN}\",\"tables\":[{{\"schema\":\"dbo\",\"name\":\" \"}}]}}}}",
         "no-connection" => $"{{\"pipeline\":\"p\",\"sinks\":[\"sql\"],\"odbc\":{{\"connection\":\"\",\"tables\":{tables}}}}}",
         "unknown-connection" => $"{{\"pipeline\":\"p\",\"sinks\":[\"sql\"],\"odbc\":{{\"connection\":\"NoSuchConnection\",\"tables\":{tables}}}}}",
         "folder-outside-roots" => "{\"pipeline\":\"p\",\"path\":\"/etc\",\"sinks\":[\"sql\"]}",
         _ => $"{{\"pipeline\":\"p\",\"sinks\":[],{odbc}}}",
      };

      HttpResponseMessage response = await _app.PostWithOriginAsync( "api/runs", json, null );

      Assert.Equal( HttpStatusCode.BadRequest, response.StatusCode );
      Assert.Equal( expected, ErrorOf( await response.Content.ReadAsStringAsync() ) );
   }

   /// <summary>
   /// A database run is queued with a typed password against a database that is down. It ends
   /// with a plain reason, and the password is in neither the run list, the run's snapshot, the
   /// progress stream nor the service log. A folder run still queues as before.
   /// </summary>
   [Fact]
   public async Task AcceptedDatabaseRun_NeverShowsThePasswordAnywhere()
   {
      const string PASSWORD = "run-pw-5d1e";
      string json = $"{{\"pipeline\":\"odbc_web_e2e\",\"sinks\":[\"sql\"],\"odbc\":{{\"connection\":\"{OdbcHermeticApp.DSN}\",\"user\":\"someone\",\"password\":\"{PASSWORD}\",\"tables\":[{{\"schema\":\"dbo\",\"name\":\"T\"}}]}}}}";

      HttpResponseMessage accepted = await _app.PostWithOriginAsync( "api/runs", json, null );

      Assert.Equal( HttpStatusCode.OK, accepted.StatusCode );
      using JsonDocument queued = JsonDocument.Parse( await accepted.Content.ReadAsStringAsync() );
      string runId = queued.RootElement.GetProperty( "runId" ).GetString()!;
      JsonElement last = await _app.WaitUntilFinishedAsync( runId );
      string list = await _app.Http.GetStringAsync( "api/runs" );
      string snapshot = await _app.Http.GetStringAsync( $"api/runs/{runId}" );
      string stream = await _app.ReadStreamAsync( runId );
      Assert.NotEqual( "Completed", last.GetProperty( "status" ).GetString() );
      Assert.False( string.IsNullOrWhiteSpace( last.GetProperty( "message" ).GetString() ), "a failed run needs a reason" );
      foreach( ( string name, string text ) in new[] { ("run list", list), ("snapshot", snapshot), ("stream", stream), ("service log", _app.Log) } )
      {
         Assert.False( text.Contains( PASSWORD, StringComparison.Ordinal ), $"the {name} leaked the password" );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The "error" text of a JSON error body.
   /// </summary>
   /// <param name="body">Response body.</param>
   /// <returns>The error text.</returns>
   private static string ErrorOf( string body )
   {
      using JsonDocument doc = JsonDocument.Parse( body );
      return doc.RootElement.GetProperty( "error" ).GetString() ?? string.Empty;
   }

   #endregion Private Methods
}

/// <summary>
/// Base for the two hosts that start the real web app as a process with ODBC settings: the one
/// that needs no database and the one that gets a throwaway SQL Server database.
/// </summary>
public abstract class OdbcWebHost : IAsyncLifetime, IDisposable
{
   #region Data Members

   private readonly StringBuilder _log = new();
   private Process? _process;
   private bool _disposed;

   #endregion Data Members

   #region Public Methods

   /// <summary>Scratch space for the app's data folder, state file and ODBC files.</summary>
   protected TempFolder Root { get; } = new();

   /// <summary>The stub embedding service the app talks to.</summary>
   protected StubEmbedService Embedder { get; } = new();

   /// <summary>HTTP client pointed at the app.</summary>
   public HttpClient Http { get; private set; } = new();

   /// <summary>Port the app listens on.</summary>
   public int Port { get; private set; }

   /// <summary>A folder with one small CSV, inside the app's allowed data root.</summary>
   public string DataFolder => Path.Combine( Root.Path, "data" );

   /// <summary>The app's console output so far, for leak checks and failure messages.</summary>
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

   /// <summary>
   /// Prepares what the app needs (a database, ODBC files), starts it, and waits until it answers.
   /// </summary>
   public async Task InitializeAsync()
   {
      string dll = WebBuild.Dll;
      Root.Write( "data/a.csv", "id,name\n1,alpha\n2,beta\n" );
      Port = FreePort();
      Http = new HttpClient { BaseAddress = new Uri( $"http://127.0.0.1:{Port}/" ), Timeout = TimeSpan.FromSeconds( 90 ) };
      var environment = new Dictionary<string, string>
      {
         ["GVB_SQL_PASSWORD"] = "unused",
         ["Gvb__StatePath"] = Path.Combine( Root.Path, "state.db" ),
         ["Gvb__DataRoots__0"] = DataFolder,
         ["Gvb__UploadRoot"] = Path.Combine( Root.Path, "uploads" ),
         ["Gvb__EmbedUrl"] = Embedder.Url,
         ["Gvb__SqlServer"] = "127.0.0.1,1",
         ["Gvb__QdrantGrpcPort"] = "1",
      };
      Directory.CreateDirectory( Path.Combine( Root.Path, "uploads" ) );
      Prepare( environment );

      var info = new ProcessStartInfo( "dotnet", $"\"{dll}\" --Urls=http://127.0.0.1:{Port}" )
      {
         RedirectStandardOutput = true,
         RedirectStandardError = true,
         WorkingDirectory = Path.GetDirectoryName( dll )!,
      };
      foreach( KeyValuePair<string, string> variable in environment )
      {
         info.Environment[variable.Key] = variable.Value;
      }

      _process = Process.Start( info ) ?? throw new InvalidOperationException( "Could not start the web app." );
      _process.OutputDataReceived += ( _, e ) => AppendLog( e.Data );
      _process.ErrorDataReceived += ( _, e ) => AppendLog( e.Data );
      _process.BeginOutputReadLine();
      _process.BeginErrorReadLine();
      await WaitUntilUpAsync();
   }

   /// <summary>
   /// Stops the app and everything the host created.
   /// </summary>
   public Task DisposeAsync()
   {
      Dispose();
      return Task.CompletedTask;
   }

   /// <summary>
   /// Posts a JSON body without an Origin header, as a script would.
   /// </summary>
   /// <param name="url">Relative URL.</param>
   /// <param name="body">Object to send as JSON.</param>
   /// <returns>The response.</returns>
   public Task<HttpResponseMessage> PostJsonAsync( string url, object body )
   {
      return PostWithOriginAsync( url, JsonSerializer.Serialize( body ), null );
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
   /// Waits until a run has a finish time and returns its final snapshot.
   /// </summary>
   /// <param name="runId">Run id.</param>
   /// <returns>The final snapshot.</returns>
   public async Task<JsonElement> WaitUntilFinishedAsync( string runId )
   {
      DateTime until = DateTime.UtcNow.AddSeconds( 60 );
      while( DateTime.UtcNow < until )
      {
         JsonElement snapshot = JsonDocument.Parse( await Http.GetStringAsync( $"api/runs/{runId}" ) ).RootElement.Clone();
         if( snapshot.GetProperty( "finishedUtc" ).ValueKind != JsonValueKind.Null )
         {
            return snapshot;
         }

         await Task.Delay( 100 );
      }

      throw new TimeoutException( $"Run {runId} never finished.\n{Log}" );
   }

   /// <summary>
   /// Reads a finished run's progress stream to its end.
   /// </summary>
   /// <param name="runId">Run id.</param>
   /// <returns>The stream text.</returns>
   public async Task<string> ReadStreamAsync( string runId )
   {
      using var timeout = new CancellationTokenSource( TimeSpan.FromSeconds( 30 ) );
      return await Http.GetStringAsync( $"api/runs/{runId}/events", timeout.Token );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Adds the host's ODBC settings to the app's environment, and creates whatever they point at.
   /// </summary>
   /// <param name="environment">The app's environment variables, to add to.</param>
   protected abstract void Prepare( Dictionary<string, string> environment );

   /// <summary>
   /// Removes what <see cref="Prepare"/> created outside the scratch folder.
   /// </summary>
   protected virtual void CleanUp()
   {
   }

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
   /// Kills the app, stops the stub service, cleans up and deletes the scratch files.
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
      CleanUp();
      Root.Dispose();
   }

   #endregion IDisposable
}

/// <summary>
/// A host with a user DSN, a configured connection (login from a password file) and a "database"
/// behind both that hangs up on every connection, so each attempt fails at once the way a
/// database that is down or not speaking SQL Server's protocol does. (A closed port would make
/// the driver retry for its 15 second login timeout on every attempt.) The user DSN lives in a
/// private odbc.ini named by ODBCINI, so the test neither depends on nor reads the machine's own
/// DSNs.
/// </summary>
public sealed class OdbcHermeticApp : OdbcWebHost
{
   #region Data Members

   /// <summary>Name of the user DSN.</summary>
   public const string DSN = "GvbWebTestDsn";

   /// <summary>Name of the configured connection.</summary>
   public const string CONFIGURED = "gvbwebtest_cfg";

   /// <summary>The password in the configured connection's password file.</summary>
   public const string STORED_PASSWORD = "stored-pw-c0ffee";

   private const string DRIVER = "ODBC Driver 18 for SQL Server";

   private TcpListener? _hangsUp;

   #endregion Data Members

   #region Overrides

   /// <inheritdoc />
   protected override void Prepare( Dictionary<string, string> environment )
   {
      _hangsUp = new TcpListener( IPAddress.Loopback, 0 );
      _hangsUp.Start();
      int port = ( (IPEndPoint)_hangsUp.LocalEndpoint ).Port;
      _ = Task.Run( HangUpOnEveryoneAsync );
      string ini = Root.Write( "odbc.ini", $"[{DSN}]\nDriver={DRIVER}\nServer=127.0.0.1,{port}\nDatabase=nothing\nTrustServerCertificate=yes\n" );
      string passwordFile = Root.Write( "cfg-password.txt", $"user: nobody\npass:{STORED_PASSWORD}\n" );
      environment["ODBCINI"] = ini;
      environment["Gvb__SqlServer"] = $"127.0.0.1,{port}";
      environment["Gvb__OdbcConnections__0__Name"] = CONFIGURED;
      environment["Gvb__OdbcConnections__0__ConnectionString"] = $"Driver={{{DRIVER}}};Server=127.0.0.1,{port};Database=nothing;TrustServerCertificate=yes";
      environment["Gvb__OdbcConnections__0__User"] = "nobody";
      environment["Gvb__OdbcConnections__0__PasswordFile"] = passwordFile;
      HangUpPort = port;
   }

   /// <inheritdoc />
   protected override void CleanUp()
   {
      _hangsUp?.Stop();
   }

   #endregion Overrides

   #region Public Methods

   /// <summary>The port the hanging-up "database" listens on, for raw connection strings.</summary>
   public int HangUpPort { get; private set; }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Accepts connections and closes them again until the listener is stopped.
   /// </summary>
   private async Task HangUpOnEveryoneAsync()
   {
      try
      {
         while( true )
         {
            using TcpClient client = await _hangsUp!.AcceptTcpClientAsync();
         }
      }
      catch( Exception ex ) when( ex is ObjectDisposedException or SocketException or InvalidOperationException )
      {
         // The listener was stopped; the host is shutting down.
      }
   }

   #endregion Private Methods
}

/// <summary>
/// The run request as the worker executes it, against a REAL SQL Server through the real ODBC
/// driver: a throwaway GvbOdbcTest_web_* database with a keyed table, a keyless table and a
/// view, read into fake sinks through the real <see cref="RunWorker"/> and registry.
/// What it proves: a request with an "odbc" member builds an ODBC source (the documents carry
/// the ODBC origin and table id); mappings keyed by table name and by table id both set the row
/// id; a re-run embeds nothing and a changed row embeds one; a configured connection needs no
/// typed login; the expected row total reaches the progress bar; a wrong password or an unknown
/// connection ends with a plain reason that has no password in it; and the password is gone from
/// the registry once the run has started. The embedder is a stub, so no GPU time is used.
/// Needs SQL Server and the ODBC driver; FAILS when they are down.
/// </summary>
[Trait( "Category", "Live" )]
public sealed class OdbcRunWorkerLiveTests : IDisposable
{
   #region Data Members

   private const string DRIVER = "ODBC Driver 18 for SQL Server";
   private const string CONFIGURED = "gvbwebtest_live";
   private const int ITEM_ROWS = 30;
   private const int NOTE_ROWS = 5;

   private static readonly OdbcSelection ITEMS = new( "dbo", "Items" );
   private static readonly OdbcSelection NOTES = new( "dbo", "Notes" );
   private static readonly OdbcSelection BIG_ITEMS = new( "dbo", "BigItems" );

   private readonly string _database = $"GvbOdbcTest_web_{Guid.NewGuid():N}"[..28];
   private readonly string _saPassword;
   private readonly string _raw;
   private readonly TempFolder _stateDir = new();
   private readonly MemorySink _sql = new( "sql" );
   private readonly RunRegistry _registry = new();
   private readonly GvbServices _services;
   private readonly RunWorker _worker;
   private int _pipelines;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the throwaway database and a worker over fake sinks that knows one configured
   /// connection to it.
   /// </summary>
   public OdbcRunWorkerLiveTests()
   {
      _saPassword = GvbSettings.ReadPasswordFile( GvbSettings.Expand( "~/mssql-sa-password.txt" ) )
         ?? throw new InvalidOperationException( "No sa password in ~/mssql-sa-password.txt." );
      _raw = $"Driver={{{DRIVER}}};Server=localhost;Database={_database};TrustServerCertificate=yes";
      CreateDatabase();
      var settings = new GvbSettings
      {
         OdbcConnections =
         {
            new OdbcConnectionSetting { Name = CONFIGURED, ConnectionString = _raw, User = "sa", PasswordFile = "~/mssql-sa-password.txt" },
         },
      };
      var state = new SqliteStateStore( Path.Combine( _stateDir.Path, "state.db" ) );
      _services = new GvbServices( settings, StubHandler.Client( new StubHandler( EmbedAnswer ) ), new ISink[] { _sql }, state );
      _worker = new RunWorker( _registry, _services, NullLogger<RunWorker>.Instance, new OdbcCatalog( settings ) );
      _worker.StartAsync( CancellationToken.None ).GetAwaiter().GetResult();
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// A request with a raw connection string and a typed login reads both tables: every row is
   /// embedded and stored with the ODBC origin and table name, the row total reaches the
   /// progress, and neither the snapshot nor the stored request holds the password.
   /// </summary>
   [Fact]
   public async Task OdbcRequest_ReadsTheChosenTables_ThroughAnOdbcSource()
   {
      var odbc = new OdbcRunRequest( _raw, "sa", _saPassword, new[] { ITEMS, NOTES } );

      ( RunJob job, RunSnapshot run ) = await RunAsync( Request( odbc ) );

      Assert.Equal( RunStatus.Completed, run.Status );
      Assert.Equal( ITEM_ROWS + NOTE_ROWS, run.RecordsRead );
      Assert.Equal( ITEM_ROWS + NOTE_ROWS, run.DocsEmbedded );
      Assert.Equal( ITEM_ROWS + NOTE_ROWS, run.TotalRows );
      string connectionName = OdbcCatalog.ConnectionName( _raw );
      Assert.Equal( new[] { OdbcSource.Origin( connectionName, ITEMS ), OdbcSource.Origin( connectionName, NOTES ) }.OrderBy( o => o ), _sql.Records.Values.Select( r => r.Document.Origin ).Distinct().OrderBy( o => o ) );
      Assert.All( _sql.Records.Values.Where( r => r.Document.Origin.EndsWith( "dbo.Items", StringComparison.Ordinal ) ), r =>
      {
         Assert.Equal( OdbcSource.TableName( ITEMS ), r.Document.Table );
         Assert.StartsWith( OdbcSource.TableId( connectionName, ITEMS ) + "|", r.Chunk.DocKey );
      } );
      Assert.Contains( _sql.Records.Values, r => r.Chunk.Text.Contains( "Name: item 7", StringComparison.Ordinal ) );
      Assert.Null( job.Request.Odbc!.Password );
      Assert.False( JsonSerializer.Serialize( run ).Contains( _saPassword, StringComparison.Ordinal ), "the snapshot leaked the password" );
      Assert.False( _sql.Records.Values.Any( r => r.Chunk.Text.Contains( _saPassword, StringComparison.Ordinal ) ), "a stored document holds the password" );
   }

   /// <summary>
   /// Row id choices apply whether the mapping is keyed by the table's display name or by its
   /// stable table id, both of which the preview reports: either way the keys are the table id
   /// plus the row id, and the key value is kept in the metadata.
   /// </summary>
   [Fact]
   public async Task Mappings_KeyedByTableNameOrTableId_BothSetTheRowId()
   {
      string connectionName = OdbcCatalog.ConnectionName( _raw );
      string tableId = OdbcSource.TableId( connectionName, ITEMS );
      var odbc = new OdbcRunRequest( _raw, "sa", _saPassword, new[] { ITEMS } );
      var byName = new Dictionary<string, TableMapping> { [OdbcSource.TableName( ITEMS )] = new TableMapping( "ItemId", null ) };
      var byId = new Dictionary<string, TableMapping> { [tableId] = new TableMapping( "ItemId", null ) };

      await RunAsync( Request( odbc, byName ) );
      List<string> keysByName = StoredKeys();
      _sql.Records.Clear();
      await RunAsync( Request( odbc, byId ) );
      List<string> keysById = StoredKeys();

      Assert.Equal( ITEM_ROWS, keysByName.Count );
      Assert.Equal( keysByName, keysById );
      Assert.All( keysById, k => Assert.StartsWith( tableId + "|", k ) );
      Assert.Contains( $"{tableId}|7", keysById );
      Assert.Contains( _sql.Records.Values, r => r.Document.Metadata.TryGetValue( "key", out string? key ) && key == "7" );
   }

   /// <summary>
   /// A second run over the same tables embeds nothing; one changed row embeds exactly that
   /// row and replaces it in place when the row id is chosen.
   /// </summary>
   [Fact]
   public async Task ReRun_EmbedsNothing_AndAChangedRowEmbedsOnlyThatRow()
   {
      string tableId = OdbcSource.TableId( OdbcCatalog.ConnectionName( _raw ), ITEMS );
      var odbc = new OdbcRunRequest( _raw, "sa", _saPassword, new[] { ITEMS } );
      var mappings = new Dictionary<string, TableMapping> { [tableId] = new TableMapping( "ItemId", null ) };
      string pipeline = NextPipeline();
      await RunAsync( Request( odbc, mappings, pipeline ) );

      ( _, RunSnapshot unchanged ) = await RunAsync( Request( odbc, mappings, pipeline ) );
      Execute( "UPDATE dbo.Items SET Name = N'renamed seven' WHERE ItemId = 7;" );
      ( _, RunSnapshot changed ) = await RunAsync( Request( odbc, mappings, pipeline ) );

      Assert.Equal( 0, unchanged.DocsEmbedded );
      Assert.Equal( ITEM_ROWS, unchanged.DocsUnchanged );
      Assert.Equal( 1, changed.DocsEmbedded );
      Assert.Equal( 0, changed.DocsDeleted );
      Assert.Equal( ITEM_ROWS, _sql.Records.Count );
      Assert.Contains( _sql.Records.Values, r => r.Chunk.Text.Contains( "renamed seven", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// A configured connection brings its own login, so a request with no user and no password
   /// runs; a typed login would have replaced it.
   /// </summary>
   [Fact]
   public async Task ConfiguredConnection_NeedsNoTypedLogin()
   {
      var odbc = new OdbcRunRequest( CONFIGURED, null, null, new[] { NOTES } );

      ( _, RunSnapshot run ) = await RunAsync( Request( odbc ) );

      Assert.Equal( RunStatus.Completed, run.Status );
      Assert.Equal( NOTE_ROWS, run.DocsEmbedded );
      Assert.All( _sql.Records.Values, r => Assert.StartsWith( CONFIGURED + "/", r.Document.Origin ) );
   }

   /// <summary>
   /// A view is read like a table. Its row count is not cheap to learn, so the total stays
   /// unknown (0) rather than a partial number that would push the bar past 100 percent.
   /// </summary>
   [Fact]
   public async Task AViewInTheList_IsRead_AndTheTotalStaysUnknown()
   {
      var odbc = new OdbcRunRequest( CONFIGURED, null, null, new[] { ITEMS, new OdbcSelection( "dbo", "BigItemView" ) } );

      ( _, RunSnapshot run ) = await RunAsync( Request( odbc ) );

      Assert.Equal( RunStatus.Completed, run.Status );
      Assert.Equal( 0, run.TotalRows );
      Assert.True( run.RecordsRead > ITEM_ROWS, "the view's rows were not read" );
   }

   /// <summary>
   /// A wrong password rejects every chosen table with the database's reason in plain words, as
   /// the runner does for any source it cannot read: nothing is read or written, and the reason
   /// is in the snapshot's reject list for the page to show. Neither the password nor the
   /// connection string is anywhere in the snapshot.
   /// </summary>
   [Fact]
   public async Task WrongPassword_RejectsTheTablesWithAPlainReason_AndNoSecretInIt()
   {
      string wrong = "wrong-pw-" + Guid.NewGuid().ToString( "N" )[..8];
      var odbc = new OdbcRunRequest( _raw, "sa", wrong, new[] { ITEMS, NOTES } );

      ( _, RunSnapshot run ) = await RunAsync( Request( odbc ) );

      string everything = JsonSerializer.Serialize( run );
      Assert.Equal( 2, run.Rejected.Count );
      Assert.All( run.Rejected, r => Assert.Contains( "The database refused the login.", r.Reason ) );
      Assert.Equal( 0, run.RecordsRead );
      Assert.Equal( 0, run.DocsEmbedded );
      Assert.False( everything.Contains( wrong, StringComparison.Ordinal ), "the snapshot leaked the typed password" );
      Assert.False( everything.Contains( "Server=localhost", StringComparison.Ordinal ), "the snapshot leaked the connection string" );
      Assert.Empty( _sql.Records );
   }

   /// <summary>
   /// A connection that is neither configured nor a DSN fails the run with the plain sentence
   /// the endpoints use, and does not touch the sinks.
   /// </summary>
   [Fact]
   public async Task UnknownConnection_FailsTheRunWithAPlainReason()
   {
      var odbc = new OdbcRunRequest( "NoSuchConnection", "sa", _saPassword, new[] { ITEMS } );

      ( _, RunSnapshot run ) = await RunAsync( Request( odbc ) );

      Assert.Equal( RunStatus.Failed, run.Status );
      Assert.Equal( "No configured ODBC connection or data source (DSN) is named 'NoSuchConnection'.", run.Message );
      Assert.Empty( _sql.Records );
   }

   /// <summary>
   /// A table that is not in the database is rejected with its reason and the other table is
   /// still read; the run reports it instead of failing outright.
   /// </summary>
   [Fact]
   public async Task AMissingTable_IsRejected_AndTheOthersAreStillRead()
   {
      var odbc = new OdbcRunRequest( _raw, "sa", _saPassword, new[] { new OdbcSelection( "dbo", "NoSuchTable" ), NOTES } );

      ( _, RunSnapshot run ) = await RunAsync( Request( odbc ) );

      Assert.Equal( NOTE_ROWS, _sql.Records.Count );
      Assert.Single( run.Rejected );
      Assert.Contains( "was not found", run.Rejected[0].Reason );
      Assert.NotEqual( RunStatus.Failed, run.Status );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Builds a database run request.
   /// </summary>
   /// <param name="odbc">The database request.</param>
   /// <param name="mappings">Row id choices, or null.</param>
   /// <param name="pipeline">Pipeline name, or null for a new one.</param>
   /// <returns>The request.</returns>
   private RunRequest Request( OdbcRunRequest odbc, Dictionary<string, TableMapping>? mappings = null, string? pipeline = null )
   {
      return new RunRequest( pipeline ?? NextPipeline(), string.Empty, new[] { "sql" }, null, mappings, true, odbc );
   }

   /// <summary>
   /// A pipeline name no earlier test used.
   /// </summary>
   /// <returns>The name.</returns>
   private string NextPipeline()
   {
      return $"odbc_web_{_pipelines++}";
   }

   /// <summary>
   /// Queues a run and waits for the worker to finish it.
   /// </summary>
   /// <param name="request">The request.</param>
   /// <returns>The job, and the final snapshot.</returns>
   private async Task<( RunJob Job, RunSnapshot Snapshot )> RunAsync( RunRequest request )
   {
      RunJob job = _registry.Enqueue( request ).Job ?? throw new InvalidOperationException( "The run was not queued." );
      DateTime until = DateTime.UtcNow.AddSeconds( 60 );
      while( !job.Progress.IsFinished )
      {
         Assert.True( DateTime.UtcNow < until, "The run did not finish in 60 seconds." );
         await Task.Delay( 20 );
      }

      return ( job, job.Progress.Snapshot() );
   }

   /// <summary>
   /// Document keys currently held by the fake sink.
   /// </summary>
   /// <returns>The keys in order.</returns>
   private List<string> StoredKeys()
   {
      return _sql.Records.Values.Select( r => r.Chunk.DocKey ).OrderBy( k => k, StringComparer.Ordinal ).ToList();
   }

   /// <summary>
   /// Builds the test database: a keyed table of 30 items, a keyless table of 5 notes, and a view.
   /// </summary>
   private void CreateDatabase()
   {
      ExecuteOn( "master", $"CREATE DATABASE [{_database}];" );
      Execute( @"
CREATE TABLE dbo.Items ( ItemId INT NOT NULL PRIMARY KEY, Name NVARCHAR(100) NOT NULL, Qty INT NOT NULL );
INSERT dbo.Items SELECT n, N'item ' + CAST( n AS NVARCHAR(10) ), n * 3 FROM ( SELECT TOP (30) ROW_NUMBER() OVER ( ORDER BY ( SELECT NULL ) ) AS n FROM sys.all_objects ) AS x;
CREATE TABLE dbo.Notes ( Body NVARCHAR(100) NOT NULL );
INSERT dbo.Notes VALUES ( N'first note' ), ( N'second note' ), ( N'third note' ), ( N'fourth note' ), ( N'fifth note' );" );
      Execute( "CREATE VIEW dbo.BigItemView AS SELECT ItemId, Name FROM dbo.Items WHERE Qty > 30;" );
   }

   /// <summary>
   /// Runs SQL in the test database as sa.
   /// </summary>
   /// <param name="sql">The batch.</param>
   private void Execute( string sql )
   {
      ExecuteOn( _database, sql );
   }

   /// <summary>
   /// Runs SQL in a database as sa, through SqlClient (setup only; the code under test uses ODBC).
   /// </summary>
   /// <param name="database">Database name.</param>
   /// <param name="sql">The batch.</param>
   private void ExecuteOn( string database, string sql )
   {
      using var connection = new SqlConnection( new SqlConnectionStringBuilder
      {
         DataSource = "localhost",
         InitialCatalog = database,
         UserID = "sa",
         Password = _saPassword,
         TrustServerCertificate = true,
         Pooling = false,
      }.ConnectionString );
      connection.Open();
      using var command = new SqlCommand( sql, connection ) { CommandTimeout = 120 };
      command.ExecuteNonQuery();
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
   /// Stops the worker, drops the throwaway database and removes the temp folder.
   /// </summary>
   public void Dispose()
   {
      _worker.StopAsync( CancellationToken.None ).GetAwaiter().GetResult();
      _services.Dispose();
      Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
      try
      {
         ExecuteOn( "master", $"IF DB_ID( N'{_database}' ) IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END" );
      }
      finally
      {
         _stateDir.Dispose();
      }
   }

   #endregion IDisposable
}

/// <summary>
/// A host with a throwaway SQL Server database behind a configured connection, and the
/// machine's own odbc.ini left in place so its DSNs can be read. Creating the database needs SQL
/// Server; the sa password comes from ~/mssql-sa-password.txt.
/// </summary>
public sealed class OdbcLiveApp : OdbcWebHost
{
   #region Data Members

   /// <summary>Name of the configured connection to the throwaway database.</summary>
   public const string CONFIGURED = "gvbwebapi_live";

   private const string DRIVER = "ODBC Driver 18 for SQL Server";

   private readonly string _database = $"GvbOdbcTest_webapi_{Guid.NewGuid():N}"[..31];

   #endregion Data Members

   #region Public Methods

   /// <summary>The sa password, for typing into requests the way the page would.</summary>
   public string SaPassword { get; } = GvbSettings.ReadPasswordFile( GvbSettings.Expand( "~/mssql-sa-password.txt" ) )
      ?? throw new InvalidOperationException( "No sa password in ~/mssql-sa-password.txt." );

   /// <summary>The throwaway database name.</summary>
   public string Database => _database;

   #endregion Public Methods

   #region Overrides

   /// <inheritdoc />
   protected override void Prepare( Dictionary<string, string> environment )
   {
      ExecuteOn( "master", $"CREATE DATABASE [{_database}];" );
      ExecuteOn( _database, @"
CREATE TABLE dbo.Items ( ItemId INT NOT NULL PRIMARY KEY, Name NVARCHAR(100) NOT NULL, Photo VARBINARY(MAX) NULL );
INSERT dbo.Items SELECT n, N'item ' + CAST( n AS NVARCHAR(10) ), CAST( n AS VARBINARY(4) ) FROM ( SELECT TOP (30) ROW_NUMBER() OVER ( ORDER BY ( SELECT NULL ) ) AS n FROM sys.all_objects ) AS x;
" );
      ExecuteOn( _database, "CREATE VIEW dbo.ItemNames AS SELECT ItemId, Name FROM dbo.Items;" );
      environment["Gvb__OdbcConnections__0__Name"] = CONFIGURED;
      environment["Gvb__OdbcConnections__0__ConnectionString"] = $"Driver={{{DRIVER}}};Server=localhost;Database={_database};TrustServerCertificate=yes";
      environment["Gvb__OdbcConnections__0__User"] = "sa";
      environment["Gvb__OdbcConnections__0__PasswordFile"] = "~/mssql-sa-password.txt";
   }

   /// <inheritdoc />
   protected override void CleanUp()
   {
      ExecuteOn( "master", $"IF DB_ID( N'{_database}' ) IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END" );
   }

   #endregion Overrides

   #region Private Methods

   /// <summary>
   /// Runs SQL as sa through SqlClient (setup only; the code under test uses ODBC).
   /// </summary>
   /// <param name="database">Database name.</param>
   /// <param name="sql">The batch.</param>
   private void ExecuteOn( string database, string sql )
   {
      using var connection = new SqlConnection( new SqlConnectionStringBuilder
      {
         DataSource = "localhost",
         InitialCatalog = database,
         UserID = "sa",
         Password = SaPassword,
         TrustServerCertificate = true,
         Pooling = false,
      }.ConnectionString );
      connection.Open();
      using var command = new SqlCommand( sql, connection ) { CommandTimeout = 120 };
      command.ExecuteNonQuery();
   }

   #endregion Private Methods
}

/// <summary>
/// The ODBC endpoints against a real SQL Server through the real driver: a configured
/// connection to a throwaway database, a raw connection string with a typed login, a wrong
/// password, and the machine's read-only AdventureWorks2025 DSN with a typed login.
/// What it proves: tables and views come back with row counts and the pipeline name the page
/// shows; previews have columns, ten rows, the suggested key and the skipped binary column; a
/// wrong password is a plain 502 without the password; and the machine's own DSN works with a
/// login typed the way the page types it, reading nothing but metadata and ten sample rows.
/// Needs SQL Server and the ODBC driver; FAILS when they are down.
/// </summary>
[Trait( "Category", "Live" )]
public class OdbcEndpointLiveTests : IClassFixture<OdbcLiveApp>
{
   #region Data Members

   private readonly OdbcLiveApp _app;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Receives the shared host.
   /// </summary>
   /// <param name="app">The running host.</param>
   public OdbcEndpointLiveTests( OdbcLiveApp app )
   {
      _app = app;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// A configured connection lists its table and view with metadata row counts (the view has
   /// none), the stable connection name, and a pipeline name to offer.
   /// </summary>
   [Fact]
   public async Task Tables_OfAConfiguredConnection_ListsTablesAndViewsWithCounts()
   {
      HttpResponseMessage response = await _app.PostJsonAsync( "api/odbc/tables", new { connection = OdbcLiveApp.CONFIGURED } );

      Assert.Equal( HttpStatusCode.OK, response.StatusCode );
      using JsonDocument body = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
      Assert.Equal( OdbcLiveApp.CONFIGURED, body.RootElement.GetProperty( "connection" ).GetString() );
      Assert.Equal( OdbcLiveApp.CONFIGURED, body.RootElement.GetProperty( "suggestedPipeline" ).GetString() );
      JsonElement[] tables = body.RootElement.GetProperty( "tables" ).EnumerateArray().ToArray();
      JsonElement items = tables.Single( t => t.GetProperty( "name" ).GetString() == "Items" );
      JsonElement view = tables.Single( t => t.GetProperty( "name" ).GetString() == "ItemNames" );
      Assert.Equal( "TABLE", items.GetProperty( "type" ).GetString() );
      Assert.Equal( "dbo", items.GetProperty( "schema" ).GetString() );
      Assert.Equal( 30, items.GetProperty( "rowCount" ).GetInt64() );
      Assert.Equal( "VIEW", view.GetProperty( "type" ).GetString() );
      Assert.Equal( JsonValueKind.Null, view.GetProperty( "rowCount" ).ValueKind );
   }

   /// <summary>
   /// A raw connection string with a typed login works, and the connection name reported for
   /// it is the hashed one, never the string, so no login can reach an origin.
   /// </summary>
   [Fact]
   public async Task Tables_OfARawConnectionString_WithATypedLogin_ReportsTheHashedName()
   {
      string raw = $"Driver={{ODBC Driver 18 for SQL Server}};Server=localhost;Database={_app.Database};TrustServerCertificate=yes";

      HttpResponseMessage response = await _app.PostJsonAsync( "api/odbc/tables", new { connection = raw, user = "sa", password = _app.SaPassword } );

      string text = await response.Content.ReadAsStringAsync();
      Assert.Equal( HttpStatusCode.OK, response.StatusCode );
      using JsonDocument body = JsonDocument.Parse( text );
      Assert.StartsWith( "odbc_", body.RootElement.GetProperty( "connection" ).GetString() );
      Assert.Equal( _app.Database.ToLowerInvariant(), body.RootElement.GetProperty( "suggestedPipeline" ).GetString() );
      Assert.False( text.Contains( _app.SaPassword, StringComparison.Ordinal ), "the answer leaked the password" );
   }

   /// <summary>
   /// A preview has the columns a run reads (binary left out and named), ten sample rows, the
   /// primary key as the suggested row id, and the stable table id the page keys its choice by.
   /// </summary>
   [Fact]
   public async Task Preview_HasColumnsSampleKeyAndSkippedColumns()
   {
      HttpResponseMessage response = await _app.PostJsonAsync( "api/odbc/preview", new { connection = OdbcLiveApp.CONFIGURED, schema = "dbo", name = "Items" } );

      Assert.Equal( HttpStatusCode.OK, response.StatusCode );
      using JsonDocument body = JsonDocument.Parse( await response.Content.ReadAsStringAsync() );
      JsonElement preview = body.RootElement;
      Assert.Equal( new[] { "ItemId", "Name" }, preview.GetProperty( "columns" ).EnumerateArray().Select( c => c.GetString() ) );
      Assert.Equal( 10, preview.GetProperty( "sample" ).GetArrayLength() );
      Assert.Equal( "ItemId", preview.GetProperty( "suggestedKey" ).GetString() );
      Assert.Equal( new[] { "Photo" }, preview.GetProperty( "skippedColumns" ).EnumerateArray().Select( c => c.GetString() ) );
      Assert.Equal( "dbo_items", preview.GetProperty( "tableName" ).GetString() );
      Assert.Equal( OdbcSource.TableId( OdbcLiveApp.CONFIGURED, new OdbcSelection( "dbo", "Items" ) ), preview.GetProperty( "tableId" ).GetString() );
   }

   /// <summary>
   /// A wrong password is a 502 that says the login was refused, and the password the user typed
   /// is in neither the answer nor the service log.
   /// </summary>
   [Fact]
   public async Task WrongPassword_Is502_TheLoginWasRefused_AndTheLogHasNoPassword()
   {
      string wrong = "wrong-pw-" + Guid.NewGuid().ToString( "N" )[..10];
      string raw = $"Driver={{ODBC Driver 18 for SQL Server}};Server=localhost;Database={_app.Database};TrustServerCertificate=yes";

      HttpResponseMessage tables = await _app.PostJsonAsync( "api/odbc/tables", new { connection = raw, user = "sa", password = wrong } );
      HttpResponseMessage preview = await _app.PostJsonAsync( "api/odbc/preview", new { connection = raw, user = "sa", password = wrong, schema = "dbo", name = "Items" } );

      foreach( HttpResponseMessage response in new[] { tables, preview } )
      {
         string text = await response.Content.ReadAsStringAsync();
         Assert.Equal( HttpStatusCode.BadGateway, response.StatusCode );
         Assert.Contains( "The database refused the login.", text );
         Assert.False( text.Contains( wrong, StringComparison.Ordinal ), "the answer leaked the typed password" );
         Assert.False( text.Contains( "Server=localhost", StringComparison.Ordinal ), "the answer leaked the connection string" );
      }

      Assert.False( _app.Log.Contains( wrong, StringComparison.Ordinal ), "the service log leaked the typed password" );
   }

   /// <summary>
   /// A table that does not exist previews as a 502 with a plain reason rather than a 500.
   /// </summary>
   [Fact]
   public async Task Preview_OfAMissingTable_Is502WithAPlainReason()
   {
      HttpResponseMessage response = await _app.PostJsonAsync( "api/odbc/preview", new { connection = OdbcLiveApp.CONFIGURED, schema = "dbo", name = "NoSuchTable" } );

      string text = await response.Content.ReadAsStringAsync();
      Assert.Equal( HttpStatusCode.BadGateway, response.StatusCode );
      Assert.StartsWith( "Could not preview dbo.NoSuchTable:", JsonDocument.Parse( text ).RootElement.GetProperty( "error" ).GetString() );
   }

   /// <summary>
   /// The machine's AdventureWorks2025 DSN has no login of its own, so the page asks for one;
   /// with the login typed the way the page types it, the listing and a preview work. Reads
   /// metadata and ten sample rows only.
   /// </summary>
   [Fact]
   public async Task TheMachinesAdventureWorksDsn_NeedsATypedLogin_ThenListsAndPreviews()
   {
      HttpResponseMessage listed = await _app.Http.GetAsync( "api/odbc/connections" );
      using JsonDocument connections = JsonDocument.Parse( await listed.Content.ReadAsStringAsync() );
      JsonElement dsn = connections.RootElement.GetProperty( "connections" ).EnumerateArray().Single( c => c.GetProperty( "name" ).GetString() == "AdventureWorks2025" );
      Assert.Equal( "user DSN", dsn.GetProperty( "kind" ).GetString() );
      Assert.True( dsn.GetProperty( "needsCredentials" ).GetBoolean() );

      HttpResponseMessage tables = await _app.PostJsonAsync( "api/odbc/tables", new { connection = "AdventureWorks2025", user = "sa", password = _app.SaPassword } );
      HttpResponseMessage preview = await _app.PostJsonAsync( "api/odbc/preview", new { connection = "AdventureWorks2025", user = "sa", password = _app.SaPassword, schema = "Person", name = "Person" } );

      Assert.Equal( HttpStatusCode.OK, tables.StatusCode );
      using JsonDocument tableBody = JsonDocument.Parse( await tables.Content.ReadAsStringAsync() );
      JsonElement[] all = tableBody.RootElement.GetProperty( "tables" ).EnumerateArray().ToArray();
      Assert.Equal( "adventureworks2025", tableBody.RootElement.GetProperty( "suggestedPipeline" ).GetString() );
      Assert.Contains( all, t => t.GetProperty( "schema" ).GetString() == "Person" && t.GetProperty( "name" ).GetString() == "Person" && t.GetProperty( "type" ).GetString() == "TABLE" );
      Assert.Contains( all, t => t.GetProperty( "type" ).GetString() == "VIEW" );
      Assert.Equal( HttpStatusCode.OK, preview.StatusCode );
      using JsonDocument previewBody = JsonDocument.Parse( await preview.Content.ReadAsStringAsync() );
      Assert.Equal( 10, previewBody.RootElement.GetProperty( "sample" ).GetArrayLength() );
      Assert.Equal( "BusinessEntityID", previewBody.RootElement.GetProperty( "suggestedKey" ).GetString() );
   }

   #endregion Public Methods
}
