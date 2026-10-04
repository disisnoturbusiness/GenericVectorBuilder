using System.Data.Odbc;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Chunking;
using GenericVectorBuilder.Core.Configuration;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Mapping;
using GenericVectorBuilder.Core.Pipeline;
using GenericVectorBuilder.Core.Sources.Odbc;
using GenericVectorBuilder.Core.State;
using GenericVectorBuilder.Tests.Fakes;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Tests.Live;

/// <summary>
/// The ODBC source against a REAL SQL Server through the real ODBC driver: each test builds its
/// own throwaway GvbOdbcTest_* database (20,000 items with every common column type, a join view,
/// and small tables to drop, deny and kill), runs the catalog and the actual pipeline, and drops
/// everything again.
/// What it proves: the catalog lists tables and views with counts; previews format values and
/// suggest the right key; a full run reads every row and a re-run embeds nothing; a dropped table
/// is deleted only once it is confirmed gone; a table the login may not read is rejected and KEEPS
/// its vectors even though SQL Server hides it from the catalog; a connection killed mid-read
/// never deletes anything; a refused login is tried once per run, not once per table; and a
/// hidden view whose inner table was dropped keeps its vectors until the view itself is dropped.
/// The embedder is the deterministic fake, so no GPU time is used. Needs SQL Server, the ODBC
/// driver and (for the full run) Qdrant; FAILS when they are down.
/// </summary>
[Trait( "Category", "Live" )]
public sealed class OdbcLiveTests : IDisposable
{
   #region Data Members

   private const string DRIVER = "ODBC Driver 18 for SQL Server";
   private const string CONNECTION = "gvbodbctest";
   private const int ITEM_ROWS = 20000;
   private const int CATEGORY_ROWS = 50;
   private const int SECRET_ROWS = 25;
   private const int DOOMED_ROWS = 30;
   private const int KILL_AFTER_ROWS = 100;
   private const int WIDE_ROWS = 20000;

   private static readonly OdbcSelection ITEMS = new( "dbo", "Items" );
   private static readonly OdbcSelection CATEGORIES = new( "dbo", "Categories" );
   private static readonly OdbcSelection ITEM_CATALOG = new( "dbo", "ItemCatalog" );
   private static readonly OdbcSelection SECRET = new( "dbo", "Secret" );
   private static readonly OdbcSelection DOOMED = new( "dbo", "Doomed" );
   private static readonly OdbcSelection ODD = new( "dbo", "Odd \"Name\" Table" );
   private static readonly OdbcSelection WIDE = new( "dbo", "Wide" );

   /// <summary>Each test table's primary key, for the row id mappings.</summary>
   private static readonly Dictionary<OdbcSelection, string> PRIMARY_KEYS = new()
   {
      [ITEMS] = "ItemId",
      [CATEGORIES] = "CategoryId",
      [SECRET] = "SecretId",
      [DOOMED] = "DoomedId",
      [WIDE] = "WideId",
   };

   private readonly ITestOutputHelper _output;
   private readonly string _suffix = Guid.NewGuid().ToString( "N" )[..8];
   private readonly string _database;
   private readonly string _saPassword;
   private readonly GvbSettings _settings;
   private readonly OdbcCatalog _catalog;
   private readonly TempFolder _stateDir = new();
   private readonly SqliteStateStore _state;
   private readonly FakeEmbedder _embedder = new();
   private readonly List<string> _logins = new();

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the throwaway database and a configured connection to it that signs in as sa with
   /// the password file, the way an admin would set it up.
   /// </summary>
   /// <param name="output">xUnit output.</param>
   public OdbcLiveTests( ITestOutputHelper output )
   {
      _output = output;
      _database = $"GvbOdbcTest_{_suffix}";
      _saPassword = GvbSettings.ReadPasswordFile( GvbSettings.Expand( "~/mssql-sa-password.txt" ) )
         ?? throw new InvalidOperationException( "No sa password in ~/mssql-sa-password.txt." );
      _settings = new GvbSettings
      {
         SqlDatabase = $"{_database}_sink",
         StatePath = Path.Combine( _stateDir.Path, "services.db" ),
         OdbcConnections =
         {
            new OdbcConnectionSetting
            {
               Name = CONNECTION,
               ConnectionString = $"Driver={{{DRIVER}}};Server=localhost;Database={_database};TrustServerCertificate=yes",
               User = "sa",
               PasswordFile = "~/mssql-sa-password.txt",
            },
         },
      };
      _catalog = new OdbcCatalog( _settings );
      _state = new SqliteStateStore( Path.Combine( _stateDir.Path, "state.db" ) );
      CreateDatabase();
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The catalog lists exactly the five tables and the view, no system objects, with metadata
   /// row counts for tables and none for the view.
   /// </summary>
   [Fact]
   public async Task Catalog_ListsTablesAndTheView_WithRowCounts()
   {
      IReadOnlyList<OdbcTableInfo> tables = await _catalog.ListTablesAsync( CONNECTION, OdbcCredentials.None, CancellationToken.None );

      Assert.Equal( new[]
      {
         new OdbcTableInfo( "dbo", "Categories", "TABLE", CATEGORY_ROWS ),
         new OdbcTableInfo( "dbo", "Doomed", "TABLE", DOOMED_ROWS ),
         new OdbcTableInfo( "dbo", "ItemCatalog", "VIEW", null ),
         new OdbcTableInfo( "dbo", "Items", "TABLE", ITEM_ROWS ),
         new OdbcTableInfo( "dbo", "Odd \"Name\" Table", "TABLE", 3 ),
         new OdbcTableInfo( "dbo", "Secret", "TABLE", SECRET_ROWS ),
      }, tables );
   }

   /// <summary>
   /// Previews read the right columns, format every type invariantly, leave binary out, and
   /// suggest the key from the strongest evidence: the primary key, a unique index, or (for the
   /// join view) the id column a COUNT query proves unique, passing over the repeated CategoryId.
   /// </summary>
   [Fact]
   public async Task Preview_FormatsValues_SuggestsKeys_AndSkipsBinary()
   {
      OdbcTablePreview items = await _catalog.PreviewAsync( CONNECTION, OdbcCredentials.None, ITEMS, CancellationToken.None );
      Assert.Equal( new[] { "ItemId", "Name", "Price", "Added", "Active", "Token", "Spec", "Note", "CategoryId" }, items.Columns );
      Assert.Equal( new[] { "Photo" }, items.SkippedColumns );
      Assert.Equal( "ItemId", items.SuggestedKey );
      Assert.Equal( "dbo_items", items.TableName );
      Assert.Equal( OdbcSource.TableId( CONNECTION, ITEMS ), items.TableId );
      Assert.Equal( 10, items.Sample.Count );

      IReadOnlyList<string?> first = items.Sample.Single( r => r[0] == "1" );
      Assert.Equal( "Café ☕ \U0001D11E item 1", first[1] );
      Assert.Equal( "0.25", first[2] );
      Assert.Equal( "2024-01-02T03:04:06.1234567", first[3] );
      Assert.Equal( "true", first[4] );
      Assert.Matches( "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", first[5] );
      Assert.Equal( "<spec size=\"1\"/>", first[6] );
      Assert.Equal( "note 1", first[7] );
      Assert.Null( items.Sample.Single( r => r[0] == "3" )[6] );
      Assert.Null( items.Sample.Single( r => r[0] == "5" )[7] );

      OdbcTablePreview view = await _catalog.PreviewAsync( CONNECTION, OdbcCredentials.None, ITEM_CATALOG, CancellationToken.None );
      Assert.Equal( new[] { "CategoryId", "ItemId", "Title", "Name" }, view.Columns );
      Assert.Equal( "ItemId", view.SuggestedKey );

      Assert.Equal( "CategoryId", ( await _catalog.PreviewAsync( CONNECTION, OdbcCredentials.None, CATEGORIES, CancellationToken.None ) ).SuggestedKey );

      OdbcTablePreview odd = await _catalog.PreviewAsync( CONNECTION, OdbcCredentials.None, ODD, CancellationToken.None );
      Assert.Equal( "Code", odd.SuggestedKey );
      Assert.Empty( odd.SkippedColumns );
      string?[] oddRow = odd.Sample.Single( r => r[0] == "A1" ).ToArray();
      Assert.Equal( new string?[] { "A1", "first", "2024-05-06T07:08:09.1234567+02:00", "12:34:56.1234567" }, oddRow );
   }

   /// <summary>
   /// The real pipeline into SQL Server and Qdrant reads every row of the table and the view, a
   /// re-run reads them all again and embeds nothing, and a changed and a deleted row cost exactly
   /// one embed and one delete each (twice, because the view shows the same item).
   /// </summary>
   [Fact]
   public async Task FullRun_ReadsEveryRow_ReRunEmbedsNothing_ChangesAreIncremental()
   {
      string pipeline = $"gvbodbctest_{_suffix}";
      using var services = new GvbServices( _settings );
      IReadOnlyList<ISink> sinks = services.GetSinks( new[] { "sql", "qdrant" } );
      try
      {
         var tables = new[] { ITEMS, CATEGORIES, ITEM_CATALOG };
         Dictionary<string, TableMapping> mappings = await MappingsFromPreviewsAsync( tables );
         int all = ITEM_ROWS + CATEGORY_ROWS + ITEM_ROWS;

         RunSnapshot first = await RunAsync( pipeline, Source( tables ), sinks, mappings );
         AssertRun( first, RunStatus.Completed, read: all, embedded: all, deleted: 0 );
         Assert.All( sinks, s => Assert.Equal( all, s.CountAsync( pipeline, CancellationToken.None ).GetAwaiter().GetResult() ) );

         RunSnapshot again = await RunAsync( pipeline, Source( tables ), sinks, mappings );
         AssertRun( again, RunStatus.Completed, read: all, embedded: 0, deleted: 0 );
         Assert.Equal( all, again.DocsUnchanged );

         Execute( "UPDATE dbo.Items SET Name = N'renamed item' WHERE ItemId = 7; DELETE dbo.Items WHERE ItemId = 8;" );
         RunSnapshot changed = await RunAsync( pipeline, Source( tables ), sinks, mappings );
         AssertRun( changed, RunStatus.Completed, read: all - 2, embedded: 2, deleted: 2 );
         Assert.All( sinks, s => Assert.Equal( all - 2, s.CountAsync( pipeline, CancellationToken.None ).GetAwaiter().GetResult() ) );
      }
      finally
      {
         await services.ResetPipelineAsync( pipeline, CancellationToken.None );
      }
   }

   /// <summary>
   /// A dropped table that is still selected is rejected ("not found") and keeps its vectors.
   /// Once it is no longer selected, the source confirms it gone (absent from the listing AND
   /// "not found" when queried) and its rows are deleted. A table that exists is never gone.
   /// </summary>
   [Fact]
   public async Task DroppedTable_IsKeptWhileSelected_ThenConfirmedGoneAndDeleted()
   {
      var sink = new MemorySink( "memory" );
      string pipeline = $"gvbodbctest_drop_{_suffix}";
      AssertRun( await RunAsync( pipeline, Source( CATEGORIES, DOOMED ), new[] { sink }, Keys( CATEGORIES, DOOMED ) ), RunStatus.Completed, read: CATEGORY_ROWS + DOOMED_ROWS, embedded: CATEGORY_ROWS + DOOMED_ROWS, deleted: 0 );

      Execute( "DROP TABLE dbo.Doomed;" );
      RunSnapshot stillSelected = await RunAsync( pipeline, Source( CATEGORIES, DOOMED ), new[] { sink }, Keys( CATEGORIES, DOOMED ) );
      RejectedOrigin rejected = Assert.Single( stillSelected.Rejected );
      Assert.Equal( $"{CONNECTION}/dbo.Doomed", rejected.Origin );
      Assert.StartsWith( "The table or view was not found", rejected.Reason );
      Assert.Equal( 0, stillSelected.DocsDeleted );
      Assert.Equal( CATEGORY_ROWS + DOOMED_ROWS, sink.Records.Count );

      OdbcSource idle = Source();
      Assert.True( await idle.ConfirmGoneAsync( $"{CONNECTION}/dbo.Doomed", CancellationToken.None ) );
      Assert.False( await idle.ConfirmGoneAsync( $"{CONNECTION}/dbo.Categories", CancellationToken.None ) );
      Assert.False( await idle.ConfirmGoneAsync( "otherconnection/dbo.Doomed", CancellationToken.None ) );

      RunSnapshot deselected = await RunAsync( pipeline, Source( CATEGORIES ), new[] { sink }, Keys( CATEGORIES ) );
      AssertRun( deselected, RunStatus.Completed, read: CATEGORY_ROWS, embedded: 0, deleted: DOOMED_ROWS );
      Assert.Equal( CATEGORY_ROWS, sink.Records.Count );
   }

   /// <summary>
   /// The trap this source is built around: SQL Server HIDES a table from a login that is denied
   /// SELECT on it (it vanishes from the catalog), so "not listed" alone would look like "dropped".
   /// The denied table must be rejected as "Permission denied" with its vectors kept, even with the
   /// mass-delete guard off, and must never be confirmed gone, selected or not.
   /// </summary>
   [Fact]
   public async Task DeniedTable_IsRejected_AndItsVectorsAreKept()
   {
      var sink = new MemorySink( "memory" );
      string pipeline = $"gvbodbctest_deny_{_suffix}";
      AssertRun( await RunAsync( pipeline, Source( CATEGORIES, SECRET ), new[] { sink }, Keys( CATEGORIES, SECRET ) ), RunStatus.Completed, read: CATEGORY_ROWS + SECRET_ROWS, embedded: CATEGORY_ROWS + SECRET_ROWS, deleted: 0 );

      OdbcCredentials reader = CreateReaderLogin();
      Execute( $"DENY SELECT ON dbo.Secret TO [{_logins[0]}];" );
      IReadOnlyList<OdbcTableInfo> visible = await _catalog.ListTablesAsync( CONNECTION, reader, CancellationToken.None );
      Assert.DoesNotContain( visible, t => t.Name == "Secret" );
      Assert.Contains( visible, t => t.Name == "Categories" );

      RunSnapshot denied = await RunAsync( pipeline, _catalog.CreateSource( CONNECTION, reader, new[] { CATEGORIES, SECRET } ), new[] { sink }, Keys( CATEGORIES, SECRET ), allowLargeDeletes: true );
      RejectedOrigin rejected = Assert.Single( denied.Rejected );
      Assert.Equal( $"{CONNECTION}/dbo.Secret", rejected.Origin );
      Assert.StartsWith( "Permission denied.", rejected.Reason );
      Assert.Equal( 0, denied.DocsDeleted );
      Assert.Equal( SECRET_ROWS, sink.Records.Values.Count( r => r.Document.Table == "dbo_secret" ) );

      OdbcSource readerSource = _catalog.CreateSource( CONNECTION, reader, Array.Empty<OdbcSelection>() );
      Assert.False( await readerSource.ConfirmGoneAsync( $"{CONNECTION}/dbo.Secret", CancellationToken.None ) );

      RunSnapshot deselected = await RunAsync( pipeline, _catalog.CreateSource( CONNECTION, reader, new[] { CATEGORIES } ), new[] { sink }, Keys( CATEGORIES ), allowLargeDeletes: true );
      Assert.Equal( RunStatus.Partial, deselected.Status );
      Assert.Equal( 0, deselected.DocsDeleted );
      Assert.Equal( SECRET_ROWS, deselected.DocsKept );
      Assert.Equal( CATEGORY_ROWS + SECRET_ROWS, sink.Records.Count );
   }

   /// <summary>
   /// The connection is killed on the server part way through reading a 20,000 row table of about
   /// 80 MB (4 KB per row, far more than the socket buffers hold, so the kill really interrupts the
   /// read; the 20,000 small Items rows arrived whole before a kill could land). The table is
   /// rejected as a lost connection, nothing is deleted even with the mass-delete guard off, and
   /// every stored vector is still there.
   /// </summary>
   [Fact]
   public async Task KilledConnection_MidRead_NeverDeletes()
   {
      Execute( $@"CREATE TABLE dbo.Wide ( WideId INT NOT NULL PRIMARY KEY, Body NVARCHAR(MAX) NOT NULL );
WITH n AS ( SELECT TOP ({WIDE_ROWS}) ROW_NUMBER() OVER ( ORDER BY ( SELECT NULL ) ) AS i FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b )
INSERT dbo.Wide SELECT i, CAST( i AS NVARCHAR(10) ) + N' ' + REPLICATE( CAST( N'wide row text ' AS NVARCHAR(MAX) ), 140 ) FROM n;" );
      var sink = new MemorySink( "memory" );
      string pipeline = $"gvbodbctest_kill_{_suffix}";
      AssertRun( await RunAsync( pipeline, Source( WIDE ), new[] { sink }, Keys( WIDE ) ), RunStatus.Completed, read: WIDE_ROWS, embedded: WIDE_ROWS, deleted: 0 );
      int stored = sink.Records.Count;

      string app = $"gvbodbc-kill-{_suffix}";
      var builder = new OdbcConnectionStringBuilder( _catalog.ResolveConnectionString( CONNECTION, OdbcCredentials.None ) ) { ["APP"] = app };
      var killing = new OdbcKillingSource( new OdbcSource( CONNECTION, builder.ConnectionString, new[] { WIDE } ), KILL_AFTER_ROWS, () => KillSessions( app ) );

      RunSnapshot killed = await RunAsync( pipeline, killing, new[] { sink }, Keys( WIDE ), allowLargeDeletes: true );
      _output.WriteLine( $"read {killed.RecordsRead} of {WIDE_ROWS} before the read failed; rejected: {string.Join( "; ", killed.Rejected )}" );

      Assert.True( killing.Killed );
      Assert.True( killed.RecordsRead < WIDE_ROWS, $"the read finished ({killed.RecordsRead} rows) despite the kill" );
      RejectedOrigin rejected = Assert.Single( killed.Rejected );
      Assert.Equal( $"{CONNECTION}/dbo.Wide", rejected.Origin );
      Assert.StartsWith( "The connection to the database", rejected.Reason );
      Assert.Equal( 0, killed.DocsDeleted );
      Assert.Equal( stored, sink.Records.Count );
   }

   /// <summary>
   /// A wrong password fails the catalog with a plain "refused the login" message and rejects every
   /// table of a run, and the password appears in none of it.
   /// </summary>
   [Fact]
   public async Task WrongPassword_IsAPlainError_AndNeverLeaks()
   {
      string wrong = $"Wrong-{_suffix}-Pw!";
      var credentials = new OdbcCredentials( "sa", wrong );
      InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>( () => _catalog.ListTablesAsync( CONNECTION, credentials, CancellationToken.None ) );
      _output.WriteLine( error.Message );
      Assert.Contains( "refused the login", error.Message );
      Assert.DoesNotContain( wrong, error.Message );
      Assert.Null( error.InnerException );

      OdbcSource source = _catalog.CreateSource( CONNECTION, credentials, new[] { ITEMS, CATEGORIES } );
      await foreach( SourceRecord _ in source.ReadAsync( CancellationToken.None ) )
      {
         Assert.Fail( "no record can be read with a wrong password" );
      }

      Assert.True( source.Report.Completed );
      Assert.Equal( 2, source.Report.Rejected.Count );
      Assert.All( source.Report.Rejected, r => Assert.DoesNotContain( wrong, r.Reason ) );
   }

   /// <summary>
   /// A login the server refuses is tried ONCE per run, not once per table and once more per
   /// stored table being checked for deletion: every refused login counts toward the lockout a
   /// server may enforce, and a server that does not answer costs the driver's full login timeout
   /// each time. The first table carries the real reason, the others say they were not tried and
   /// why, and nothing is confirmed gone. Counted in the server's own error log.
   /// </summary>
   [Fact]
   public async Task RefusedLogin_IsTriedOncePerRun_AndConfirmsNothingGone()
   {
      string user = $"GvbOdbcTest_nologin_{_suffix}";
      var credentials = new OdbcCredentials( user, $"Wrong-{_suffix}-Pw!" );
      OdbcSource source = _catalog.CreateSource( CONNECTION, credentials, new[] { ITEMS, CATEGORIES, SECRET } );
      await foreach( SourceRecord _ in source.ReadAsync( CancellationToken.None ) )
      {
         Assert.Fail( "no record can be read with a refused login" );
      }

      Assert.False( await source.ConfirmGoneAsync( $"{CONNECTION}/dbo.Doomed", CancellationToken.None ) );
      Assert.False( await source.ConfirmGoneAsync( $"{CONNECTION}/dbo.NeverExisted", CancellationToken.None ) );
      Assert.Equal( 3, source.Report.Rejected.Count );
      Assert.StartsWith( "The database refused the login.", source.Report.Rejected[0].Reason );
      Assert.All( source.Report.Rejected.Skip( 1 ), r =>
      {
         Assert.StartsWith( "Not tried, because connecting already failed for an earlier table:", r.Reason );
         Assert.Contains( "The database refused the login.", r.Reason );
      } );
      Assert.Equal( 1, FailedLogins( user ) );
   }

   /// <summary>
   /// A view whose inner table was dropped answers "not found" (42S02) about the INNER table. When
   /// the login may not read the view, SQL Server also hides it from the catalog, so both halves
   /// of the gone check looked satisfied and the view's vectors were deleted although the view
   /// still exists. The view must be kept until the view itself is dropped.
   /// </summary>
   [Fact]
   public async Task HiddenViewWithADroppedInnerTable_IsKept_UntilTheViewItselfIsDropped()
   {
      var view = new OdbcSelection( "dbo", "DoomedView" );
      Execute( "CREATE VIEW dbo.DoomedView AS SELECT DoomedId, Label FROM dbo.Doomed;" );
      var sink = new MemorySink( "memory" );
      string pipeline = $"gvbodbctest_hiddenview_{_suffix}";
      AssertRun( await RunAsync( pipeline, Source( CATEGORIES, view ), new[] { sink }, Keys( CATEGORIES ) ), RunStatus.Completed, read: CATEGORY_ROWS + DOOMED_ROWS, embedded: CATEGORY_ROWS + DOOMED_ROWS, deleted: 0 );

      OdbcCredentials reader = CreateReaderLogin();
      Execute( $"DENY SELECT ON dbo.DoomedView TO [{_logins[0]}]; DROP TABLE dbo.Doomed;" );
      IReadOnlyList<OdbcTableInfo> visible = await _catalog.ListTablesAsync( CONNECTION, reader, CancellationToken.None );
      Assert.DoesNotContain( visible, t => t.Name == view.Name );
      Assert.False( await _catalog.CreateSource( CONNECTION, reader, Array.Empty<OdbcSelection>() ).ConfirmGoneAsync( $"{CONNECTION}/dbo.DoomedView", CancellationToken.None ) );

      RunSnapshot hidden = await RunAsync( pipeline, _catalog.CreateSource( CONNECTION, reader, new[] { CATEGORIES } ), new[] { sink }, Keys( CATEGORIES ), allowLargeDeletes: true );
      Assert.Equal( 0, hidden.DocsDeleted );
      Assert.Equal( DOOMED_ROWS, hidden.DocsKept );
      Assert.Equal( CATEGORY_ROWS + DOOMED_ROWS, sink.Records.Count );

      Execute( "DROP VIEW dbo.DoomedView;" );
      RunSnapshot dropped = await RunAsync( pipeline, _catalog.CreateSource( CONNECTION, reader, new[] { CATEGORIES } ), new[] { sink }, Keys( CATEGORIES ), allowLargeDeletes: true );
      AssertRun( dropped, RunStatus.Completed, read: CATEGORY_ROWS, embedded: 0, deleted: DOOMED_ROWS );
      Assert.Equal( CATEGORY_ROWS, sink.Records.Count );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Builds the test database. Every name in it is a constant; the 20,000 items are generated
   /// set-based so setup takes about a second.
   /// </summary>
   private void CreateDatabase()
   {
      ExecuteOnMaster( $"CREATE DATABASE [{_database}];" );
      Execute( @"
CREATE TABLE dbo.Categories ( CategoryId INT NOT NULL PRIMARY KEY, Title NVARCHAR(50) NOT NULL );
INSERT dbo.Categories SELECT n, N'Category ' + CAST( n AS NVARCHAR(10) ) FROM ( SELECT TOP (50) ROW_NUMBER() OVER ( ORDER BY ( SELECT NULL ) ) AS n FROM sys.all_objects ) AS x;
CREATE TABLE dbo.Items ( ItemId INT NOT NULL PRIMARY KEY, Name NVARCHAR(100) NOT NULL, Price DECIMAL(10,2) NOT NULL, Added DATETIME2(7) NOT NULL,
   Active BIT NOT NULL, Token UNIQUEIDENTIFIER NOT NULL, Spec XML NULL, Photo VARBINARY(MAX) NULL, Note NVARCHAR(200) NULL, CategoryId INT NOT NULL );
WITH n AS ( SELECT TOP (20000) ROW_NUMBER() OVER ( ORDER BY ( SELECT NULL ) ) AS i FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b )
INSERT dbo.Items SELECT i, N'Caf" + "é ☕ \U0001D11E" + @" item ' + CAST( i AS NVARCHAR(10) ), CAST( i AS DECIMAL(10,2) ) / 4,
   DATEADD( SECOND, i, CAST( '2024-01-02T03:04:05.1234567' AS DATETIME2(7) ) ), CAST( i % 2 AS BIT ), CONVERT( UNIQUEIDENTIFIER, HASHBYTES( 'MD5', CAST( i AS VARCHAR(10) ) ) ),
   CASE WHEN i % 3 = 0 THEN NULL ELSE CAST( N'<spec size=""' + CAST( i AS NVARCHAR(10) ) + N'""/>' AS XML ) END, CAST( i AS VARBINARY(4) ),
   CASE WHEN i % 5 = 0 THEN NULL ELSE N'note ' + CAST( i AS NVARCHAR(10) ) END, ( i % 50 ) + 1 FROM n;
CREATE TABLE dbo.Secret ( SecretId INT NOT NULL PRIMARY KEY, Body NVARCHAR(100) NOT NULL );
INSERT dbo.Secret SELECT n, N'secret ' + CAST( n AS NVARCHAR(10) ) FROM ( SELECT TOP (25) ROW_NUMBER() OVER ( ORDER BY ( SELECT NULL ) ) AS n FROM sys.all_objects ) AS x;
CREATE TABLE dbo.Doomed ( DoomedId INT NOT NULL PRIMARY KEY, Label NVARCHAR(50) NOT NULL );
INSERT dbo.Doomed SELECT n, N'doomed ' + CAST( n AS NVARCHAR(10) ) FROM ( SELECT TOP (30) ROW_NUMBER() OVER ( ORDER BY ( SELECT NULL ) ) AS n FROM sys.all_objects ) AS x;
CREATE TABLE dbo.[Odd ""Name"" Table] ( Code NCHAR(5) NOT NULL, [Some Col] NVARCHAR(20) NULL, Moment DATETIMEOFFSET(7) NULL, Span TIME(7) NULL );
CREATE UNIQUE INDEX UX_Odd_Code ON dbo.[Odd ""Name"" Table] ( Code );
INSERT dbo.[Odd ""Name"" Table] VALUES ( N'A1', N'first', '2024-05-06 07:08:09.1234567 +02:00', '12:34:56.1234567' ), ( N'B2', NULL, NULL, NULL ), ( N'C3', N'third', '2025-01-01 00:00:00 +00:00', '00:00:00' );" );
      Execute( "CREATE VIEW dbo.ItemCatalog AS SELECT c.CategoryId, i.ItemId, c.Title, i.Name FROM dbo.Items AS i JOIN dbo.Categories AS c ON c.CategoryId = i.CategoryId;" );
   }

   /// <summary>
   /// Creates a low-privilege login that can read the test database (db_datareader) and nothing
   /// else on the server. Its password is random and never printed.
   /// </summary>
   /// <returns>Credentials for the login.</returns>
   private OdbcCredentials CreateReaderLogin()
   {
      string login = $"GvbOdbcTest_reader_{_suffix}";
      string password = $"Rd{Guid.NewGuid():N}!aA1";
      _logins.Add( login );
      ExecuteOnMaster( $"CREATE LOGIN [{login}] WITH PASSWORD = N'{password}', CHECK_POLICY = OFF, DEFAULT_DATABASE = [{_database}];" );
      Execute( $"CREATE USER [{login}] FOR LOGIN [{login}]; ALTER ROLE db_datareader ADD MEMBER [{login}];" );
      return new OdbcCredentials( login, password );
   }

   /// <summary>
   /// Kills every session the given application name opened, from a separate sa connection.
   /// </summary>
   /// <param name="app">The APP name in the victim's connection string.</param>
   /// <returns>How many sessions were killed.</returns>
   private int KillSessions( string app )
   {
      var sessions = new List<short>();
      using var connection = new SqlConnection( SqlConnectionString( "master" ) );
      connection.Open();
      using( var find = new SqlCommand( "SELECT session_id FROM sys.dm_exec_sessions WHERE program_name = @app AND session_id <> @@SPID", connection ) )
      {
         find.Parameters.AddWithValue( "@app", app );
         using SqlDataReader reader = find.ExecuteReader();
         while( reader.Read() )
         {
            sessions.Add( reader.GetInt16( 0 ) );
         }
      }

      foreach( short session in sessions )
      {
         using var kill = new SqlCommand( $"KILL {session.ToString( System.Globalization.CultureInfo.InvariantCulture )};", connection );
         kill.ExecuteNonQuery();
      }

      return sessions.Count;
   }

   /// <summary>
   /// How many failed logins for a user name the server's current error log holds.
   /// </summary>
   /// <param name="user">The login name that was refused.</param>
   /// <returns>The count.</returns>
   private int FailedLogins( string user )
   {
      using var connection = new SqlConnection( SqlConnectionString( "master" ) );
      connection.Open();
      using var command = new SqlCommand( "EXEC master.dbo.xp_readerrorlog 0, 1, N'Login failed for user', @user;", connection );
      command.Parameters.AddWithValue( "@user", user );
      using SqlDataReader reader = command.ExecuteReader();
      int count = 0;
      while( reader.Read() )
      {
         count++;
      }

      return count;
   }

   /// <summary>
   /// Runs the real pipeline runner over a source with the fake embedder.
   /// </summary>
   /// <param name="pipeline">Pipeline name.</param>
   /// <param name="source">Source.</param>
   /// <param name="sinks">Sinks.</param>
   /// <param name="mappings">Key columns by table id.</param>
   /// <param name="allowLargeDeletes">Turns the mass-delete guard off, so only the whitelist protects data.</param>
   /// <returns>The final snapshot.</returns>
   private async Task<RunSnapshot> RunAsync( string pipeline, ISource source, IReadOnlyList<ISink> sinks, IReadOnlyDictionary<string, TableMapping> mappings, bool allowLargeDeletes = false )
   {
      var runner = new PipelineRunner( _embedder, sinks, _state, new TextChunker() ) { AllowLargeDeletes = allowLargeDeletes };
      var progress = new RunProgress( "odbc", pipeline );
      await runner.RunAsync( pipeline, source, new RowDocumentMapper( mappings ), progress, CancellationToken.None );
      RunSnapshot snapshot = progress.Snapshot();
      _output.WriteLine( $"{pipeline}: {snapshot.Status} '{snapshot.Message}' read {snapshot.RecordsRead}, embedded {snapshot.DocsEmbedded}, deleted {snapshot.DocsDeleted}, kept {snapshot.DocsKept}; {string.Join( " | ", snapshot.Errors )}" );
      return snapshot;
   }

   /// <summary>
   /// Key mappings taken from the catalog's previews, the way the page would pre-select them.
   /// </summary>
   /// <param name="tables">Tables to preview.</param>
   /// <returns>Mappings by table id.</returns>
   private async Task<Dictionary<string, TableMapping>> MappingsFromPreviewsAsync( IEnumerable<OdbcSelection> tables )
   {
      var mappings = new Dictionary<string, TableMapping>( StringComparer.Ordinal );
      foreach( OdbcSelection table in tables )
      {
         OdbcTablePreview preview = await _catalog.PreviewAsync( CONNECTION, OdbcCredentials.None, table, CancellationToken.None );
         Assert.NotNull( preview.SuggestedKey );
         mappings[preview.TableId] = new TableMapping( preview.SuggestedKey, null );
      }

      return mappings;
   }

   /// <summary>
   /// The given tables keyed by their primary keys, the mapping the page would save.
   /// </summary>
   /// <param name="tables">Tables in the run.</param>
   /// <returns>Mappings by table id.</returns>
   private static Dictionary<string, TableMapping> Keys( params OdbcSelection[] tables )
   {
      return tables.ToDictionary( t => OdbcSource.TableId( CONNECTION, t ), t => new TableMapping( PRIMARY_KEYS[t], null ), StringComparer.Ordinal );
   }

   /// <summary>
   /// A source over the configured test connection, signed in as sa.
   /// </summary>
   /// <param name="tables">Tables to read.</param>
   /// <returns>The source.</returns>
   private OdbcSource Source( params OdbcSelection[] tables )
   {
      return _catalog.CreateSource( CONNECTION, OdbcCredentials.None, tables );
   }

   /// <summary>
   /// Asserts a run's status and counters, printing its errors when it does not match.
   /// </summary>
   /// <param name="run">The snapshot.</param>
   /// <param name="status">Expected status.</param>
   /// <param name="read">Expected records read.</param>
   /// <param name="embedded">Expected documents embedded.</param>
   /// <param name="deleted">Expected documents deleted.</param>
   private static void AssertRun( RunSnapshot run, RunStatus status, long read, long embedded, long deleted )
   {
      string detail = $"{run.Status} '{run.Message}': {string.Join( " | ", run.Errors )} {string.Join( " | ", run.Rejected )}";
      Assert.True( run.Status == status, detail );
      Assert.Equal( read, run.RecordsRead );
      Assert.Equal( embedded, run.DocsEmbedded );
      Assert.Equal( deleted, run.DocsDeleted );
   }

   /// <summary>
   /// Runs SQL in the test database as sa.
   /// </summary>
   /// <param name="sql">The batch.</param>
   private void Execute( string sql )
   {
      using var connection = new SqlConnection( SqlConnectionString( _database ) );
      connection.Open();
      using var command = new SqlCommand( sql, connection ) { CommandTimeout = 120 };
      command.ExecuteNonQuery();
   }

   /// <summary>
   /// Runs SQL in master as sa.
   /// </summary>
   /// <param name="sql">The batch.</param>
   private void ExecuteOnMaster( string sql )
   {
      using var connection = new SqlConnection( SqlConnectionString( "master" ) );
      connection.Open();
      using var command = new SqlCommand( sql, connection ) { CommandTimeout = 120 };
      command.ExecuteNonQuery();
   }

   /// <summary>
   /// The sa connection string for a database, through SqlClient (setup only; the code under test uses ODBC).
   /// </summary>
   /// <param name="database">Database name.</param>
   /// <returns>The connection string.</returns>
   private string SqlConnectionString( string database )
   {
      return new SqlConnectionStringBuilder
      {
         DataSource = "localhost",
         InitialCatalog = database,
         UserID = "sa",
         Password = _saPassword,
         TrustServerCertificate = true,
         Pooling = false,
      }.ConnectionString;
   }

   /// <summary>
   /// Releases pooled SQLite handles so the state folder can be deleted.
   /// </summary>
   private static void SqliteConnectionPoolClear()
   {
      Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Drops the throwaway database and login.
   /// </summary>
   public void Dispose()
   {
      try
      {
         ExecuteOnMaster( $"IF DB_ID( N'{_database}' ) IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END" );
         ExecuteOnMaster( $"IF DB_ID( N'{_database}_sink' ) IS NOT NULL BEGIN ALTER DATABASE [{_database}_sink] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}_sink]; END" );
         foreach( string login in _logins )
         {
            ExecuteOnMaster( $"IF SUSER_ID( N'{login}' ) IS NOT NULL DROP LOGIN [{login}];" );
         }
      }
      finally
      {
         SqliteConnectionPoolClear();
         _stateDir.Dispose();
      }
   }

   #endregion IDisposable
}

/// <summary>
/// Read-only smoke test against the machine's real AdventureWorks2025 user DSN: it is discovered
/// through the driver manager (and the same through the odbc.ini fallback), and its catalog
/// lists the 71 tables and 20 views SQL Server's INFORMATION_SCHEMA reports. Only SELECTs and
/// catalog calls are made.
/// </summary>
[Trait( "Category", "Live" )]
public sealed class OdbcAdventureWorksSmokeTests
{
   #region Data Members

   private const string DSN = "AdventureWorks2025";

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test.
   /// </summary>
   /// <param name="output">xUnit output.</param>
   public OdbcAdventureWorksSmokeTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The DSN is listed as a user DSN needing a login, identically through the driver manager
   /// and through the ini fallback, and the driver list has the SQL Server driver.
   /// </summary>
   [Fact]
   public void Dsn_IsDiscovered_ByTheDriverManagerAndTheIniFallback()
   {
      var native = new OdbcCatalog( new GvbSettings() );
      var fromFiles = new OdbcCatalog( new GvbSettings() ) { NativeDataSources = () => null };

      IReadOnlyList<OdbcConnectionInfo> listed = native.ListConnections();
      Assert.Contains( new OdbcConnectionInfo( DSN, "user DSN", "ODBC Driver 18 for SQL Server", true ), listed );
      Assert.Equal( listed, fromFiles.ListConnections() );
      Assert.Contains( "ODBC Driver 18 for SQL Server", native.ListDrivers() );
   }

   /// <summary>
   /// 71 tables and 20 views, every table with a metadata row count, plus previews that skip the
   /// photo and hierarchy columns and find the single-column primary keys.
   /// </summary>
   [Fact]
   public async Task Catalog_Lists71TablesAnd20Views()
   {
      var catalog = new OdbcCatalog( new GvbSettings() );
      string password = GvbSettings.ReadPasswordFile( GvbSettings.Expand( "~/mssql-sa-password.txt" ) )!;
      var credentials = new OdbcCredentials( "sa", password );

      IReadOnlyList<OdbcTableInfo> tables = await catalog.ListTablesAsync( DSN, credentials, CancellationToken.None );
      _output.WriteLine( $"{tables.Count( t => t.Type == "TABLE" )} tables, {tables.Count( t => t.Type == "VIEW" )} views" );

      Assert.Equal( 71, tables.Count( t => t.Type == "TABLE" ) );
      Assert.Equal( 20, tables.Count( t => t.Type == "VIEW" ) );
      Assert.All( tables.Where( t => t.Type == "TABLE" ), t => Assert.NotNull( t.RowCount ) );
      Assert.All( tables.Where( t => t.Type == "VIEW" ), t => Assert.Null( t.RowCount ) );
      Assert.DoesNotContain( tables, t => t.Schema is "sys" or "INFORMATION_SCHEMA" );
      Assert.Equal( 121317, tables.Single( t => t.Schema == "Sales" && t.Name == "SalesOrderDetail" ).RowCount );

      OdbcTablePreview photos = await catalog.PreviewAsync( DSN, credentials, new OdbcSelection( "Production", "ProductPhoto" ), CancellationToken.None );
      Assert.Equal( new[] { "ThumbNailPhoto", "LargePhoto" }, photos.SkippedColumns );
      Assert.Equal( "ProductPhotoID", photos.SuggestedKey );

      OdbcTablePreview employees = await catalog.PreviewAsync( DSN, credentials, new OdbcSelection( "HumanResources", "Employee" ), CancellationToken.None );
      Assert.Equal( new[] { "OrganizationNode" }, employees.SkippedColumns );
      Assert.Equal( "BusinessEntityID", employees.SuggestedKey );
      Assert.Equal( 10, employees.Sample.Count );
      Assert.Matches( new Regex( @"^\d{4}-\d{2}-\d{2}$" ), employees.Sample[0][employees.Columns.ToList().IndexOf( "BirthDate" )] );
   }

   /// <summary>
   /// Cross-check through a second, independent driver stack: every row of Person.Person (XML
   /// columns) and Sales.SalesOrderHeader read through ODBC must match SqlClient's COUNT(*), and
   /// one person's values must match what SqlClient reads, formatted the same way.
   /// </summary>
   [Fact]
   public async Task Source_ReadsEveryRow_AndValuesMatchSqlClient()
   {
      string password = GvbSettings.ReadPasswordFile( GvbSettings.Expand( "~/mssql-sa-password.txt" ) )!;
      var catalog = new OdbcCatalog( new GvbSettings() );
      var person = new OdbcSelection( "Person", "Person" );
      var orders = new OdbcSelection( "Sales", "SalesOrderHeader" );
      OdbcSource source = catalog.CreateSource( DSN, new OdbcCredentials( "sa", password ), new[] { person, orders } );

      var counts = new Dictionary<string, long>( StringComparer.Ordinal );
      SourceRecord? first = null;
      await foreach( SourceRecord record in source.ReadAsync( CancellationToken.None ) )
      {
         counts[record.Origin] = counts.GetValueOrDefault( record.Origin ) + 1;
         if( record.Table == "person_person" && record.Values[0] == "1" )
         {
            first = record;
         }
      }

      using var sql = new SqlConnection( new SqlConnectionStringBuilder { DataSource = "localhost", InitialCatalog = DSN, UserID = "sa", Password = password, TrustServerCertificate = true, Pooling = false }.ConnectionString );
      sql.Open();
      Assert.True( source.Report.Completed );
      Assert.Empty( source.Report.Rejected );
      Assert.Equal( Scalar( sql, "SELECT COUNT_BIG(*) FROM Person.Person" ), counts["AdventureWorks2025/Person.Person"] );
      Assert.Equal( Scalar( sql, "SELECT COUNT_BIG(*) FROM Sales.SalesOrderHeader" ), counts["AdventureWorks2025/Sales.SalesOrderHeader"] );
      Assert.True( source.Report.IsClean( "AdventureWorks2025/Person.Person" ) );

      using var command = new SqlCommand( "SELECT FirstName, ModifiedDate, rowguid, CAST( Demographics AS NVARCHAR(MAX) ) FROM Person.Person WHERE BusinessEntityID = 1", sql );
      using SqlDataReader expected = command.ExecuteReader();
      Assert.True( expected.Read() );
      Assert.NotNull( first );
      string? Value( string column ) => first!.Values[first.Columns.ToList().IndexOf( column )];
      Assert.Equal( expected.GetString( 0 ), Value( "FirstName" ) );
      Assert.Equal( expected.GetDateTime( 1 ).ToString( "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", System.Globalization.CultureInfo.InvariantCulture ), Value( "ModifiedDate" ) );
      Assert.Equal( expected.GetGuid( 2 ).ToString( "D" ), Value( "rowguid" ) );
      Assert.Equal( expected.GetString( 3 ), Value( "Demographics" ) );
      _output.WriteLine( $"Person.Person {counts["AdventureWorks2025/Person.Person"]} rows, SalesOrderHeader {counts["AdventureWorks2025/Sales.SalesOrderHeader"]} rows" );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Runs a scalar query through SqlClient.
   /// </summary>
   /// <param name="connection">Open SqlClient connection.</param>
   /// <param name="sql">The query.</param>
   /// <returns>The value as a long.</returns>
   private static long Scalar( SqlConnection connection, string sql )
   {
      using var command = new SqlCommand( sql, connection );
      return Convert.ToInt64( command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture );
   }

   #endregion Private Methods
}

/// <summary>
/// Test source wrapper: after a set number of records it kills the wrapped source's database
/// session from the server side, then keeps reading as if nothing happened, so the test sees
/// exactly what a dropped connection does to a run.
/// </summary>
internal sealed class OdbcKillingSource : ISource
{
   #region Data Members

   private readonly ISource _inner;
   private readonly int _killAfter;
   private readonly Func<int> _kill;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the wrapper.
   /// </summary>
   /// <param name="inner">The real source.</param>
   /// <param name="killAfter">Records to pass before the kill.</param>
   /// <param name="kill">Kills the sessions and returns how many.</param>
   public OdbcKillingSource( ISource inner, int killAfter, Func<int> kill )
   {
      _inner = inner;
      _killAfter = killAfter;
      _kill = kill;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>True once at least one session was killed.</summary>
   public bool Killed { get; private set; }

   /// <inheritdoc />
   public string Description => _inner.Description;

   /// <inheritdoc />
   public SourceReadReport Report => _inner.Report;

   /// <inheritdoc />
   public async IAsyncEnumerable<SourceRecord> ReadAsync( [EnumeratorCancellation] CancellationToken ct )
   {
      int seen = 0;
      await foreach( SourceRecord record in _inner.ReadAsync( ct ) )
      {
         if( ++seen == _killAfter )
         {
            Killed = _kill() > 0;
         }

         yield return record;
      }
   }

   /// <inheritdoc />
   public Task<bool> ConfirmGoneAsync( string origin, CancellationToken ct ) => _inner.ConfirmGoneAsync( origin, ct );

   #endregion Public Methods
}
