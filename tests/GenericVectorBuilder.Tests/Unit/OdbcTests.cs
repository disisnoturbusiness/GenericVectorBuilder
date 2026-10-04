using System.Globalization;
using GenericVectorBuilder.Core.Configuration;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Sources.Odbc;
using GenericVectorBuilder.Tests.Fakes;
using Microsoft.Extensions.Configuration;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// The parts of the ODBC source that need no database: value formatting, identifier quoting,
/// origin and table naming, table id stability, connection discovery from ini files, connection
/// string building, and keeping passwords out of every message.
/// Why these are pinned: the formatted text is hashed and embedded, and the origin and table id
/// are stored in every destination. Any drift in them re-embeds or orphans every row.
/// </summary>
public class OdbcTests
{
   #region Data Members

   private const string SECRET = "Sup3r$ecret!pw";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Values come out as ISO dates, invariant numbers and lower-case GUIDs even when the
   /// machine's culture writes "1,5" and "02.01.2024".
   /// </summary>
   [Fact]
   public void Format_IsCultureInvariant()
   {
      CultureInfo before = CultureInfo.CurrentCulture;
      try
      {
         CultureInfo.CurrentCulture = new CultureInfo( "de-DE" );
         Assert.Equal( "12.3400", OdbcValues.Format( 12.3400m, OdbcValues.SQL_DECIMAL ) );
         Assert.Equal( "0.1", OdbcValues.Format( 0.1d, OdbcValues.SQL_DOUBLE ) );
         Assert.Equal( "1.5", OdbcValues.Format( 1.5f, OdbcValues.SQL_REAL ) );
         Assert.Equal( "-9223372036854775808", OdbcValues.Format( long.MinValue, OdbcValues.SQL_BIGINT ) );
         Assert.Equal( "2024-01-02", OdbcValues.Format( new DateTime( 2024, 1, 2 ), OdbcValues.SQL_TYPE_DATE ) );
         Assert.Equal( "2024-01-02T03:04:05", OdbcValues.Format( new DateTime( 2024, 1, 2, 3, 4, 5 ), OdbcValues.SQL_TYPE_TIMESTAMP ) );
         Assert.Equal( "2024-01-02T03:04:05.1234567", OdbcValues.Format( new DateTime( 2024, 1, 2, 3, 4, 5 ).AddTicks( 1234567 ), OdbcValues.SQL_TYPE_TIMESTAMP ) );
         Assert.Equal( "2024-01-02T00:00:00", OdbcValues.Format( new DateTime( 2024, 1, 2 ), OdbcValues.SQL_TYPE_TIMESTAMP ) );
         Assert.Equal( "2024-05-06T07:08:09.5+02:00", OdbcValues.Format( new DateTimeOffset( 2024, 5, 6, 7, 8, 9, 500, TimeSpan.FromHours( 2 ) ), OdbcValues.SQL_SS_TIMESTAMPOFFSET ) );
         Assert.Equal( "12:34:56", OdbcValues.Format( new TimeSpan( 12, 34, 56 ), OdbcValues.SQL_TYPE_TIME ) );
         Assert.Equal( "true", OdbcValues.Format( true, OdbcValues.SQL_BIT ) );
         Assert.Equal( "false", OdbcValues.Format( false, OdbcValues.SQL_BIT ) );
         Assert.Equal( "6f9619ff-8b86-d011-b42d-00c04fc964ff", OdbcValues.Format( Guid.Parse( "6F9619FF-8B86-D011-B42D-00C04FC964FF" ), OdbcValues.SQL_GUID ) );
      }
      finally
      {
         CultureInfo.CurrentCulture = before;
      }
   }

   /// <summary>
   /// Fixed-width CHAR and NCHAR padding is not data and is trimmed; VARCHAR keeps its spaces,
   /// and Unicode survives untouched.
   /// </summary>
   [Fact]
   public void Format_TrimsFixedWidthPaddingOnly()
   {
      Assert.Equal( "abc", OdbcValues.Format( "abc       ", OdbcValues.SQL_CHAR ) );
      Assert.Equal( "é", OdbcValues.Format( "é    ", OdbcValues.SQL_WCHAR ) );
      Assert.Equal( "abc  ", OdbcValues.Format( "abc  ", OdbcValues.SQL_VARCHAR ) );
      Assert.Equal( "Café ☕ \U0001D11E", OdbcValues.Format( "Café ☕ \U0001D11E", OdbcValues.SQL_WVARCHAR ) );
      Assert.Equal( "<a b=\"1\">x</a>", OdbcValues.Format( "<a b=\"1\">x</a>", OdbcValues.SQL_SS_XML ) );
   }

   /// <summary>
   /// NULL, DBNull and binary values are null, so the mapper leaves them out of the text.
   /// </summary>
   [Fact]
   public void Format_NullAndBinaryAreNull()
   {
      Assert.Null( OdbcValues.Format( null, OdbcValues.SQL_INTEGER ) );
      Assert.Null( OdbcValues.Format( DBNull.Value, OdbcValues.SQL_WVARCHAR ) );
      Assert.Null( OdbcValues.Format( new byte[] { 1, 2 }, OdbcValues.SQL_SS_VARIANT ) );
   }

   /// <summary>
   /// SQL Server's datetimeoffset and time(7) arrive as driver text (System.Data.Odbc cannot map
   /// them) and are normalized to the same ISO shapes as everything else.
   /// </summary>
   [Fact]
   public void FormatDriverText_NormalizesOffsetAndTime()
   {
      Assert.Equal( "2024-05-06T07:08:09.1234567+02:00", OdbcValues.FormatDriverText( "2024-05-06 07:08:09.1234567 +02:00", OdbcValues.SQL_SS_TIMESTAMPOFFSET ) );
      Assert.Equal( "2024-05-06T07:08:09-05:00", OdbcValues.FormatDriverText( "2024-05-06 07:08:09 -05:00", OdbcValues.SQL_SS_TIMESTAMPOFFSET ) );
      Assert.Equal( "12:34:56.1234567", OdbcValues.FormatDriverText( "12:34:56.1234567", OdbcValues.SQL_SS_TIME2 ) );
      Assert.Equal( "01:02:03", OdbcValues.FormatDriverText( "01:02:03", OdbcValues.SQL_SS_TIME2 ) );
      Assert.Equal( "not a date", OdbcValues.FormatDriverText( " not a date ", OdbcValues.SQL_SS_TIMESTAMPOFFSET ) );
      Assert.Null( OdbcValues.FormatDriverText( null, OdbcValues.SQL_SS_TIME2 ) );
   }

   /// <summary>
   /// Binary, rowversion and CLR types (geography, hierarchyid) are skipped; text, numbers,
   /// dates, GUIDs and XML are read.
   /// </summary>
   [Theory]
   [InlineData( OdbcValues.SQL_BINARY, false )]
   [InlineData( OdbcValues.SQL_VARBINARY, false )]
   [InlineData( OdbcValues.SQL_LONGVARBINARY, false )]
   [InlineData( OdbcValues.SQL_SS_UDT, false )]
   [InlineData( (short)101, false )]
   [InlineData( OdbcValues.SQL_WVARCHAR, true )]
   [InlineData( OdbcValues.SQL_SS_XML, true )]
   [InlineData( OdbcValues.SQL_GUID, true )]
   [InlineData( OdbcValues.SQL_SS_TIMESTAMPOFFSET, true )]
   [InlineData( OdbcValues.SQL_DECIMAL, true )]
   [InlineData( OdbcValues.SQL_BIT, true )]
   public void IsSupported_SkipsBinaryAndUnknownTypes( short sqlType, bool supported )
   {
      Assert.Equal( supported, OdbcValues.IsSupported( sqlType ) );
   }

   /// <summary>
   /// Identifiers are wrapped in the driver's quote character with embedded quotes doubled, so a
   /// name can never close the quote and inject SQL. Without a quote character only plain names pass.
   /// </summary>
   [Fact]
   public void Quote_UsesTheDriversQuoteAndRefusesUnsafeRawNames()
   {
      Assert.Equal( "\"Order Lines\"", OdbcNaming.Quote( "Order Lines", "\"" ) );
      Assert.Equal( "\"a\"\"; DROP TABLE x; --\"", OdbcNaming.Quote( "a\"; DROP TABLE x; --", "\"" ) );
      Assert.Equal( "`we``ird`", OdbcNaming.Quote( "we`ird", "`" ) );
      Assert.Equal( "Plain_Name1", OdbcNaming.Quote( "Plain_Name1", null ) );
      Assert.Equal( "Plain_Name1", OdbcNaming.Quote( "Plain_Name1", " " ) );
      Assert.Throws<InvalidOperationException>( () => OdbcNaming.Quote( "has space", null ) );
      Assert.Throws<InvalidOperationException>( () => OdbcNaming.Quote( "x;DROP", " " ) );
   }

   /// <summary>
   /// A table with a schema is schema-qualified; one without is just its quoted name.
   /// </summary>
   [Fact]
   public void QualifiedName_QuotesSchemaAndName()
   {
      Assert.Equal( "\"Production\".\"Product\"", OdbcNaming.QualifiedName( new OdbcSelection( "Production", "Product" ), "\"" ) );
      Assert.Equal( "\"Product\"", OdbcNaming.QualifiedName( new OdbcSelection( "", "Product" ), "\"" ) );
   }

   /// <summary>
   /// Origins are "connection/schema.name"; '/' and '#' in a connection name, which the read
   /// report treats as container separators, become '_'.
   /// </summary>
   [Theory]
   [InlineData( "AdventureWorks2025", "Production", "Product", "AdventureWorks2025/Production.Product" )]
   [InlineData( "AdventureWorks2025", "", "Product", "AdventureWorks2025/Product" )]
   [InlineData( "sales/eu#1", "dbo", "Orders", "sales_eu_1/dbo.Orders" )]
   [InlineData( "aw", "dbo", "Order Lines", "aw/dbo.Order Lines" )]
   public void Origin_IsConnectionSlashSchemaDotName( string connection, string schema, string name, string expected )
   {
      Assert.Equal( expected, OdbcSource.Origin( connection, new OdbcSelection( schema, name ) ) );
   }

   /// <summary>
   /// Display names are lower-case schema_name with punctuation collapsed to one underscore.
   /// </summary>
   [Theory]
   [InlineData( "Production", "Product", "production_product" )]
   [InlineData( "", "Product", "product" )]
   [InlineData( "dbo", "Odd \"Name\" Table", "dbo_odd_name_table" )]
   [InlineData( "Sales", "Café Orders", "sales_café_orders" )]
   [InlineData( "", "***", "table" )]
   public void TableName_IsLowerSnakeCase( string schema, string name, string expected )
   {
      Assert.Equal( expected, OdbcSource.TableName( new OdbcSelection( schema, name ) ) );
   }

   /// <summary>
   /// The table id is pinned: it is stored in every document key, so a change to how it is
   /// computed would re-embed every ODBC pipeline. It depends on connection, schema and name only,
   /// with case kept, so two tables with identical columns never share an id.
   /// </summary>
   [Fact]
   public void TableId_IsStableAndSeparatesTables()
   {
      var product = new OdbcSelection( "Production", "Product" );
      Assert.Equal( "t0dcdad766fbb6e21", OdbcSource.TableId( "AdventureWorks2025", product ) );
      Assert.Equal( "t4e51809e3db936f8", OdbcSource.TableId( "gvbodbctest", new OdbcSelection( "dbo", "Items" ) ) );
      Assert.Equal( OdbcSource.TableId( "AdventureWorks2025", product ), OdbcSource.TableId( "AdventureWorks2025", new OdbcSelection( "Production", "Product" ) ) );
      Assert.NotEqual( OdbcSource.TableId( "AdventureWorks2025", product ), OdbcSource.TableId( "OtherDsn", product ) );
      Assert.NotEqual( OdbcSource.TableId( "AdventureWorks2025", product ), OdbcSource.TableId( "AdventureWorks2025", new OdbcSelection( "Production", "ProductCopy" ) ) );
      Assert.NotEqual( OdbcSource.TableId( "AdventureWorks2025", product ), OdbcSource.TableId( "AdventureWorks2025", new OdbcSelection( "production", "product" ) ) );
      Assert.NotEqual( OdbcSource.TableId( "c", new OdbcSelection( "a", "b.c" ) ), OdbcSource.TableId( "c", new OdbcSelection( "a.b", "c" ) ) );
   }

   /// <summary>
   /// A raw connection string is named by a hash of its settings without the login: stable across
   /// key order, case and users, different per server, and never containing the password.
   /// </summary>
   [Fact]
   public void ConnectionName_OfARawString_IgnoresTheLoginAndHidesIt()
   {
      string a = OdbcCatalog.ConnectionName( $"Driver={{ODBC Driver 18 for SQL Server}};Server=db1;Database=Sales;UID=sa;PWD={SECRET}" );
      string b = OdbcCatalog.ConnectionName( "database=Sales; SERVER=db1; Driver={ODBC Driver 18 for SQL Server}; User ID=reader; Password=other" );
      string c = OdbcCatalog.ConnectionName( "Driver={ODBC Driver 18 for SQL Server};Server=db2;Database=Sales" );
      Assert.StartsWith( "odbc_", a );
      Assert.Equal( a, b );
      Assert.NotEqual( a, c );
      Assert.DoesNotContain( SECRET, a );
      Assert.Equal( "AdventureWorks2025", OdbcCatalog.ConnectionName( " AdventureWorks2025 " ) );
      Assert.DoesNotContain( SECRET, OdbcCatalog.ConnectionName( $"Server={{unclosed;PWD={SECRET}" ) );
   }

   /// <summary>
   /// A source handed a raw connection string as its NAME still never puts the login into an
   /// origin, a table id or its description.
   /// </summary>
   [Fact]
   public void Source_NeverPutsTheLoginIntoNamesOrDescription()
   {
      string raw = $"Driver={{ODBC Driver 18 for SQL Server}};Server=db1;UID=sa;PWD={SECRET}";
      var source = new OdbcSource( raw, raw, new[] { new OdbcSelection( "dbo", "Items" ) } );
      Assert.DoesNotContain( SECRET, source.Description );
      Assert.DoesNotContain( SECRET, OdbcSource.Origin( raw, new OdbcSelection( "dbo", "Items" ) ) );
      Assert.StartsWith( "odbc_", OdbcSource.Origin( raw, new OdbcSelection( "dbo", "Items" ) ) );
      Assert.Equal( OdbcSource.TableId( OdbcCatalog.ConnectionName( raw ), new OdbcSelection( "dbo", "Items" ) ), OdbcSource.TableId( raw, new OdbcSelection( "dbo", "Items" ) ) );
   }

   /// <summary>
   /// An origin's table part is tried as every schema/name split plus the whole thing, so the gone
   /// check can require all of them to be missing.
   /// </summary>
   [Fact]
   public void SelectionsFromOrigin_ListsEveryReading()
   {
      Assert.Equal( new[] { new OdbcSelection( "dbo", "Items" ), new OdbcSelection( "", "dbo.Items" ) }, OdbcNaming.SelectionsFromOrigin( "dbo.Items" ) );
      Assert.Equal( new[] { new OdbcSelection( "", "Items" ) }, OdbcNaming.SelectionsFromOrigin( "Items" ) );
      Assert.Equal( 3, OdbcNaming.SelectionsFromOrigin( "a.b.c" ).Count() );
   }

   /// <summary>
   /// Id-like names match in every common spelling, and words that merely end in "id" or "key" do not.
   /// </summary>
   [Theory]
   [InlineData( "id", true )]
   [InlineData( "KEY", true )]
   [InlineData( "order_id", true )]
   [InlineData( "Order Key", true )]
   [InlineData( "ProductID", true )]
   [InlineData( "CustomerKey", true )]
   [InlineData( "ItemId", true )]
   [InlineData( "Paid", false )]
   [InlineData( "valid", false )]
   [InlineData( "Monkey", false )]
   [InlineData( "TURKEY", false )]
   [InlineData( "rowguid", false )]
   [InlineData( "Name", false )]
   public void LooksLikeKey_MatchesIdColumnsOnly( string column, bool expected )
   {
      Assert.Equal( expected, OdbcNaming.LooksLikeKey( column ) );
   }

   /// <summary>
   /// Printing credentials (a log line, an interpolated string) never shows the password.
   /// </summary>
   [Fact]
   public void Credentials_ToString_MasksThePassword()
   {
      var credentials = new OdbcCredentials( "sa", SECRET );
      Assert.DoesNotContain( SECRET, credentials.ToString() );
      Assert.DoesNotContain( SECRET, $"{credentials}" );
      Assert.Contains( "sa", credentials.ToString() );
   }

   /// <summary>
   /// Driver errors become a short plain summary plus the driver's own words without the vendor
   /// tags, and the password is masked even when a driver echoes it.
   /// </summary>
   [Fact]
   public void Describe_IsPlainAndMasksThePassword()
   {
      Assert.Equal( "The table or view was not found; it may have been dropped or renamed. (Invalid object name 'dbo.Gone'.)",
         OdbcErrors.Describe( "42S02", "[Microsoft][ODBC Driver 18 for SQL Server][SQL Server]Invalid object name 'dbo.Gone'.", null ) );
      Assert.StartsWith( "Permission denied.", OdbcErrors.Describe( "42000", "ERROR [42000] [Microsoft][ODBC Driver 18 for SQL Server][SQL Server]The SELECT permission was denied on the object 'Secret'", null ) );
      Assert.StartsWith( "The connection to the database was lost.", OdbcErrors.Describe( "08S01", "[Microsoft][ODBC Driver 18 for SQL Server]Communication link failure", null ) );
      Assert.StartsWith( "The database refused the login.", OdbcErrors.Describe( "28000", "Login failed for user 'sa'.", null ) );
      Assert.StartsWith( "The database reported an error.", OdbcErrors.Describe( "42000", "Incorrect syntax near 'x'.", null ) );

      string echoed = OdbcErrors.Describe( "08001", $"cannot connect with PWD={SECRET};", SECRET );
      Assert.DoesNotContain( SECRET, echoed );
      Assert.Contains( "***", echoed );
      Assert.DoesNotContain( SECRET, OdbcErrors.Describe( new InvalidOperationException( $"boom {SECRET}" ), SECRET ) );
   }

   /// <summary>
   /// A login timeout that hides the real cause is passed over for the last "connection failed"
   /// record after it, the order the SQL Server driver reports a server name that does not
   /// resolve or a port nobody listens on. Any other first record is kept.
   /// </summary>
   [Fact]
   public void MostSpecific_PrefersTheConnectionFailureOverALoginTimeout()
   {
      Assert.Equal( 2, OdbcErrors.MostSpecific( new[] { "HYT00", "08001", "08001" } ) );
      Assert.Equal( 1, OdbcErrors.MostSpecific( new[] { "S1T00", "08S01" } ) );
      Assert.Equal( 0, OdbcErrors.MostSpecific( new[] { "HYT00" } ) );
      Assert.Equal( 0, OdbcErrors.MostSpecific( new[] { "HYT00", "01000" } ) );
      Assert.Equal( 0, OdbcErrors.MostSpecific( new[] { "28000", "08001" } ) );
      Assert.Equal( 0, OdbcErrors.MostSpecific( new string?[] { null, "08001" } ) );
      Assert.Equal( 0, OdbcErrors.MostSpecific( Array.Empty<string?>() ) );
      Assert.StartsWith( "Could not connect to the database. (A network-related",
         OdbcErrors.Describe( "08001", "[Microsoft][ODBC Driver 18 for SQL Server]A network-related or instance-specific error has occurred. Server is not found or not accessible.", null ) );
   }

   /// <summary>
   /// System.Data.Odbc repeats the earlier records at the front of each later one; only the
   /// record's own words are kept. A message that does not start with the one before is left whole.
   /// </summary>
   [Fact]
   public void OwnText_CutsTheEarlierRecordsOff()
   {
      const string FIRST = "[Microsoft][ODBC Driver 18 for SQL Server]Login timeout expired";
      const string OWN = "[Microsoft][ODBC Driver 18 for SQL Server]TCP Provider: Error code 0x2749";
      Assert.Equal( OWN, OdbcErrors.OwnText( FIRST + OWN, FIRST ) );
      Assert.Equal( FIRST, OdbcErrors.OwnText( FIRST, null ) );
      Assert.Equal( FIRST, OdbcErrors.OwnText( FIRST, FIRST ) );
      Assert.Equal( "Invalid object name 'dbo.Base'.", OdbcErrors.OwnText( "Invalid object name 'dbo.Base'.", "Statement(s) could not be prepared." ) );
      Assert.Equal( string.Empty, OdbcErrors.OwnText( null, FIRST ) );
   }

   /// <summary>
   /// "Not found" confirms a table gone only when the message names that table, so a view whose
   /// inner table was dropped ("Invalid object name 'dbo.Base'" for dbo.BaseView) is never taken
   /// for a dropped view. Case does not matter, and a longer name does not count.
   /// </summary>
   [Theory]
   [InlineData( "[Microsoft][ODBC Driver 18 for SQL Server][SQL Server]Invalid object name 'dbo.Gone'.", "dbo", "Gone", true )]
   [InlineData( "Invalid object name 'DBO.GONE'.", "dbo", "Gone", true )]
   [InlineData( "Invalid object name 'dbo.Gone'.", "", "dbo.Gone", true )]
   [InlineData( "Invalid object name 'My Schema.Odd]Bracket [x]'.", "My Schema", "Odd]Bracket [x]", true )]
   [InlineData( "ERROR: relation \"public.gone\" does not exist;", "public", "gone", true )]
   [InlineData( "Table 'shop.gone' doesn't exist", "", "gone", true )]
   [InlineData( "no such table: gone", "", "gone", true )]
   [InlineData( "Invalid object name 'dbo.Base'.", "dbo", "BaseView", false )]
   [InlineData( "Invalid object name 'dbo.Base2'.", "dbo", "Base", false )]
   [InlineData( "Invalid object name 'dbo.MyBase'.", "", "Base", false )]
   [InlineData( "Invalid object name 'stage.Orders'.", "sales", "Orders", false )]
   [InlineData( "table or view does not exist", "dbo", "Gone", false )]
   [InlineData( null, "dbo", "Gone", false )]
   public void NamesTable_MatchesOnlyTheQueriedTable( string? message, string schema, string name, bool expected )
   {
      Assert.Equal( expected, OdbcErrors.NamesTable( message, new OdbcSelection( schema, name ) ) );
   }

   /// <summary>
   /// The password is found under either key and masked in a displayable connection string.
   /// </summary>
   [Fact]
   public void ConnectionStringSecrets_AreFoundAndMasked()
   {
      Assert.Equal( SECRET, OdbcErrors.SecretOf( $"DSN=x;UID=sa;PWD={SECRET}" ) );
      Assert.Equal( SECRET, OdbcErrors.SecretOf( $"DSN=x;User=sa;Password={SECRET}" ) );
      Assert.Null( OdbcErrors.SecretOf( "DSN=x;Trusted_Connection=yes" ) );
      string shown = OdbcErrors.RedactConnectionString( $"DSN=x;UID=sa;PWD={SECRET}" );
      Assert.DoesNotContain( SECRET, shown );
      Assert.Contains( "***", shown );
   }

   /// <summary>
   /// A configured connection signs in with its user and the LAST "pass:" line of its password
   /// file (CRLF stripped); credentials typed on the page override it; other spellings of the
   /// login keys are removed so the driver never sees two logins.
   /// </summary>
   [Fact]
   public void ResolveConnectionString_BuildsTheLoginFromSettingsAndCredentials()
   {
      using var folder = new TempFolder();
      string passwordFile = folder.Write( "pw.txt", "pass:old-one\r\nuser: sa\r\npass:" + SECRET + "\r\n" );
      var settings = new GvbSettings
      {
         OdbcConnections = { new OdbcConnectionSetting { Name = "Sales", ConnectionString = "Driver={ODBC Driver 18 for SQL Server};Server=db1;User ID=old;Password=old", User = "svc", PasswordFile = passwordFile } },
      };
      var catalog = new OdbcCatalog( settings ) { NativeDataSources = () => new[] { new OdbcDsnEntry( "AdventureWorks2025", "ODBC Driver 18 for SQL Server", true ) } };

      var built = new System.Data.Odbc.OdbcConnectionStringBuilder( catalog.ResolveConnectionString( "sales", OdbcCredentials.None ) );
      Assert.Equal( "svc", built["UID"] );
      Assert.Equal( SECRET, built["PWD"] );
      Assert.False( built.ContainsKey( "User ID" ) );
      Assert.False( built.ContainsKey( "Password" ) );

      var typed = new System.Data.Odbc.OdbcConnectionStringBuilder( catalog.ResolveConnectionString( "Sales", new OdbcCredentials( "me", "typed" ) ) );
      Assert.Equal( "me", typed["UID"] );
      Assert.Equal( "typed", typed["PWD"] );

      var dsn = new System.Data.Odbc.OdbcConnectionStringBuilder( catalog.ResolveConnectionString( "AdventureWorks2025", new OdbcCredentials( "sa", SECRET ) ) );
      Assert.Equal( "AdventureWorks2025", dsn.Dsn );
      Assert.Equal( SECRET, dsn["PWD"] );

      var raw = new System.Data.Odbc.OdbcConnectionStringBuilder( catalog.ResolveConnectionString( "Driver={X};Server=s;UID=a", new OdbcCredentials( null, SECRET ) ) );
      Assert.Equal( "a", raw["UID"] );
      Assert.Equal( SECRET, raw["PWD"] );
   }

   /// <summary>
   /// Unknown names, empty names and unreadable strings are refused with a plain message that
   /// never repeats the password.
   /// </summary>
   [Fact]
   public void ResolveConnectionString_RefusesUnknownConnectionsWithoutLeaking()
   {
      var catalog = new OdbcCatalog( new GvbSettings() ) { NativeDataSources = () => Array.Empty<OdbcDsnEntry>() };
      ArgumentException unknown = Assert.Throws<ArgumentException>( () => catalog.ResolveConnectionString( "NoSuchDsn", new OdbcCredentials( "sa", SECRET ) ) );
      Assert.Contains( "NoSuchDsn", unknown.Message );
      Assert.Throws<ArgumentException>( () => catalog.ResolveConnectionString( " ", OdbcCredentials.None ) );
      ArgumentException broken = Assert.Throws<ArgumentException>( () => catalog.ResolveConnectionString( $"Server={{unclosed;PWD={SECRET}", OdbcCredentials.None ) );
      Assert.DoesNotContain( SECRET, broken.Message );
   }

   /// <summary>
   /// When the driver manager cannot be asked, DSNs come from the user and system odbc.ini files,
   /// with the right kind and driver. Configured connections come first and hide a DSN of the
   /// same name. A login is asked for only when the connection carries none.
   /// </summary>
   [Fact]
   public void ListConnections_FallsBackToIniFiles()
   {
      using var folder = new TempFolder();
      string user = folder.Write( "user.ini", "[ODBC Data Sources]\nAdventureWorks2025 = ODBC Driver 18 for SQL Server\n\n[AdventureWorks2025]\nDriver=ODBC Driver 18 for SQL Server\nServer=localhost\n\n; comment\n[Stored]\nDriver = ODBC Driver 18 for SQL Server\nUID = sa\nPWD = x\n[Shadowed]\nDriver=X\n" );
      string system = folder.Write( "system.ini", "[Trusted]\r\nDriver=ODBC Driver 18 for SQL Server\r\nTrusted_Connection=Yes\r\n[Lite]\r\nDriver=SQLite3\r\nDatabase=/tmp/x.db\r\n[Stored]\r\nDriver=Y\r\n" );
      var settings = new GvbSettings
      {
         OdbcConnections =
         {
            new OdbcConnectionSetting { Name = "Shadowed", ConnectionString = "Driver={ODBC Driver 18 for SQL Server};Server=db1", User = "svc", PasswordFile = "/nowhere" },
            new OdbcConnectionSetting { Name = "Open", ConnectionString = "Driver={ODBC Driver 18 for SQL Server};Server=db2" },
         },
      };
      var catalog = new OdbcCatalog( settings ) { NativeDataSources = () => null, UserIniPath = user, SystemIniPath = system };

      IReadOnlyList<OdbcConnectionInfo> connections = catalog.ListConnections();

      Assert.Equal( new[]
      {
         new OdbcConnectionInfo( "Shadowed", "configured", "ODBC Driver 18 for SQL Server", false ),
         new OdbcConnectionInfo( "Open", "configured", "ODBC Driver 18 for SQL Server", true ),
         new OdbcConnectionInfo( "AdventureWorks2025", "user DSN", "ODBC Driver 18 for SQL Server", true ),
         new OdbcConnectionInfo( "Stored", "user DSN", "ODBC Driver 18 for SQL Server", false ),
         new OdbcConnectionInfo( "Trusted", "system DSN", "ODBC Driver 18 for SQL Server", false ),
         new OdbcConnectionInfo( "Lite", "system DSN", "SQLite3", false ),
      }, connections );
   }

   /// <summary>
   /// The ini parser keeps section order, ignores comments and blank lines, compares keys without
   /// case and keeps the first value of a repeated key.
   /// </summary>
   [Fact]
   public void IniParse_ReadsSectionsAndKeys()
   {
      var sections = OdbcIniFile.Parse( "# top\n[A]\nKey = 1\nkey = 2\n\n[B]\r\n; c\r\nx=y=z\r\n" );
      Assert.Equal( new[] { "A", "B" }, sections.Select( s => s.Name ) );
      Assert.Equal( "1", sections[0].Values["KEY"] );
      Assert.Equal( "y=z", sections[1].Values["x"] );
   }

   /// <summary>
   /// The list binds from the "Gvb" configuration section like every other setting.
   /// </summary>
   [Fact]
   public void Settings_BindOdbcConnections()
   {
      IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection( new Dictionary<string, string?>
      {
         ["Gvb:OdbcConnections:0:Name"] = "Sales",
         ["Gvb:OdbcConnections:0:ConnectionString"] = "Driver={ODBC Driver 18 for SQL Server};Server=db1",
         ["Gvb:OdbcConnections:0:User"] = "svc",
         ["Gvb:OdbcConnections:0:PasswordFile"] = "~/sales-password.txt",
      } ).Build();

      GvbSettings settings = config.GetSection( "Gvb" ).Get<GvbSettings>()!;

      OdbcConnectionSetting sales = Assert.Single( settings.OdbcConnections );
      Assert.Equal( "Sales", sales.Name );
      Assert.Equal( "svc", sales.User );
      Assert.Equal( "~/sales-password.txt", sales.PasswordFile );
   }

   /// <summary>
   /// A configured password file without a "pass:" line is a plain error naming the file.
   /// </summary>
   [Fact]
   public void ConfiguredPasswordFile_WithoutPassLine_IsAPlainError()
   {
      using var folder = new TempFolder();
      string file = folder.Write( "empty.txt", "user: sa\n" );
      var setting = new OdbcConnectionSetting { Name = "Sales", PasswordFile = file };
      InvalidOperationException error = Assert.Throws<InvalidOperationException>( () => setting.ReadPassword() );
      Assert.Contains( file, error.Message );
      Assert.Null( new OdbcConnectionSetting { Name = "Open" }.ReadPassword() );
   }

   /// <summary>
   /// A connection that cannot even be opened rejects every chosen table with a plain reason (no
   /// password), still counts as completed (every table was attempted), and never confirms
   /// anything gone. Origins of other connections are never confirmed either.
   /// </summary>
   [Fact]
   public async Task UnreachableConnection_RejectsEveryTable_AndConfirmsNothingGone()
   {
      string connectionString = $"Driver={{GvbNoSuchDriver}};Server=nowhere;UID=sa;PWD={SECRET}";
      var tables = new[] { new OdbcSelection( "dbo", "A" ), new OdbcSelection( "dbo", "B" ), new OdbcSelection( "dbo", "A" ) };
      var source = new OdbcSource( "offline", connectionString, tables );

      var records = new List<SourceRecord>();
      await foreach( SourceRecord record in source.ReadAsync( CancellationToken.None ) )
      {
         records.Add( record );
      }

      Assert.Empty( records );
      Assert.True( source.Report.Completed );
      Assert.Equal( new[] { "offline/dbo.A", "offline/dbo.B" }, source.Report.Rejected.Select( r => r.Origin ) );
      Assert.All( source.Report.Rejected, r => Assert.DoesNotContain( SECRET, r.Reason ) );
      Assert.False( await source.ConfirmGoneAsync( "offline/dbo.C", CancellationToken.None ) );
      Assert.False( await source.ConfirmGoneAsync( "elsewhere/dbo.C", CancellationToken.None ) );
      Assert.False( await source.ConfirmGoneAsync( "offline/", CancellationToken.None ) );
   }

   /// <summary>
   /// The login check: user plus password, integrated sign-in, or a file driver carry a login;
   /// a server alone or a user alone do not.
   /// </summary>
   [Fact]
   public void CarriesLogin_RecognizesStoredAndIntegratedLogins()
   {
      static Dictionary<string, string> Values( params string[] pairs ) => pairs.Select( p => p.Split( '=' ) ).ToDictionary( p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase );

      Assert.True( OdbcCatalog.CarriesLogin( Values( "UID=sa", "PWD=x" ), null ) );
      Assert.True( OdbcCatalog.CarriesLogin( Values( "Trusted_Connection=Yes" ), null ) );
      Assert.True( OdbcCatalog.CarriesLogin( Values( "Authentication=ActiveDirectoryMsi" ), null ) );
      Assert.True( OdbcCatalog.CarriesLogin( Values( "Database=/tmp/x.db" ), "SQLite3 ODBC Driver" ) );
      Assert.False( OdbcCatalog.CarriesLogin( Values( "Server=db1" ), "ODBC Driver 18 for SQL Server" ) );
      Assert.False( OdbcCatalog.CarriesLogin( Values( "UID=sa" ), "ODBC Driver 18 for SQL Server" ) );
   }

   #endregion Public Methods
}
