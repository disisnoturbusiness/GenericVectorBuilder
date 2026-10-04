using System.Data.Odbc;
using GenericVectorBuilder.Core.Configuration;

namespace GenericVectorBuilder.Core.Sources.Odbc;

/// <summary>
/// What the page asks before an ODBC run: which connections exist, which tables and views a
/// connection has, and what a table will look like when read.
/// A "connection" is one of three things, told apart in this order: a configured connection
/// name, a DSN name, or a raw ODBC connection string (anything containing '=').
/// Why credentials are a separate argument on every call: the page asks for a login only when
/// the connection lacks one, and the login must live for that request only. It is merged into
/// the connection string in memory, never written anywhere, and masked out of every error.
/// Every call that talks to a database runs on the thread pool, because System.Data.Odbc has no
/// truly asynchronous calls and the web server's threads must not wait on a slow database.
/// </summary>
public sealed class OdbcCatalog
{
   #region Data Members

   private const string KIND_CONFIGURED = "configured";
   private const string KIND_USER_DSN = "user DSN";
   private const string KIND_SYSTEM_DSN = "system DSN";
   private const int PREVIEW_ROWS = 10;

   private static readonly string[] USER_KEYS = { "UID", "User ID", "User", "Username" };
   private static readonly string[] PASSWORD_KEYS = { "PWD", "Password" };
   private static readonly string[] TRUE_VALUES = { "yes", "true", "1", "sspi" };

   /// <summary>Drivers over local files, which need no login.</summary>
   private static readonly string[] FILE_DRIVER_HINTS = { "sqlite", "text", "excel", "access", "csv", "dbase", "parquet" };

   /// <summary>SQL Server sign-in methods that use no password.</summary>
   private static readonly string[] PASSWORDLESS_AUTHENTICATION = { "ActiveDirectoryIntegrated", "ActiveDirectoryMsi", "ActiveDirectoryManagedIdentity", "ActiveDirectoryDefault", "ActiveDirectoryInteractive" };

   private readonly GvbSettings _settings;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the catalog over the configured connections and the machine's DSNs.
   /// </summary>
   /// <param name="settings">Settings, for <see cref="GvbSettings.OdbcConnections"/>.</param>
   public OdbcCatalog( GvbSettings settings )
   {
      _settings = settings;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Where DSNs come from. Tests replace it to read only their own ini files; null from it means
   /// "the driver manager could not be asked", which falls back to the ini files.
   /// </summary>
   internal Func<IReadOnlyList<OdbcDsnEntry>?> NativeDataSources { get; init; } = OdbcDriverManager.ListDataSources;

   /// <summary>The user odbc.ini read for DSN settings (and for DSNs when the driver manager cannot be asked).</summary>
   internal string UserIniPath { get; init; } = OdbcIniFile.UserDataSourcePath();

   /// <summary>The system odbc.ini read for DSN settings (and for DSNs when the driver manager cannot be asked).</summary>
   internal string SystemIniPath { get; init; } = OdbcIniFile.SystemDataSourcePath();

   /// <summary>
   /// The name a connection is known by in origins and table ids: the configured or DSN name
   /// itself, or for a raw connection string "odbc_" plus a hash of its settings without the
   /// login. Pass this, never a raw connection string, as the connection name of an
   /// <see cref="OdbcSource"/>, so the preview's table ids match the run's.
   /// </summary>
   /// <param name="connection">Configured name, DSN name, or raw connection string.</param>
   /// <returns>The stable connection name.</returns>
   public static string ConnectionName( string connection )
   {
      return OdbcNaming.ConnectionName( connection );
   }

   /// <summary>
   /// Lists every connection the user can pick: configured connections first, then user DSNs,
   /// then system DSNs. A name is listed once; a configured connection hides a DSN of the same
   /// name, and a user DSN hides a system DSN, which matches what opening it would use.
   /// </summary>
   /// <returns>The connections.</returns>
   public IReadOnlyList<OdbcConnectionInfo> ListConnections()
   {
      var result = new List<OdbcConnectionInfo>();
      var names = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
      foreach( OdbcConnectionSetting configured in _settings.OdbcConnections.Where( c => !string.IsNullOrWhiteSpace( c.Name ) ) )
      {
         if( names.Add( configured.Name.Trim() ) )
         {
            Dictionary<string, string> values = ParseConnectionString( configured.ConnectionString );
            bool stored = !string.IsNullOrWhiteSpace( configured.User ) && !string.IsNullOrWhiteSpace( configured.PasswordFile );
            string? driver = values.TryGetValue( "Driver", out string? d ) ? d.Trim( '{', '}' ) : null;
            result.Add( new OdbcConnectionInfo( configured.Name.Trim(), KIND_CONFIGURED, driver, !stored && !CarriesLogin( values, driver ) ) );
         }
      }

      foreach( OdbcDsnEntry dsn in DataSources() )
      {
         if( names.Add( dsn.Name ) )
         {
            IReadOnlyDictionary<string, string> values = DsnSettings( dsn );
            result.Add( new OdbcConnectionInfo( dsn.Name, dsn.IsUser ? KIND_USER_DSN : KIND_SYSTEM_DSN, dsn.Driver, !CarriesLogin( values, dsn.Driver ) ) );
         }
      }

      return result;
   }

   /// <summary>
   /// Lists the ODBC drivers installed on the machine, for building a raw connection string.
   /// </summary>
   /// <returns>Driver names; empty when none can be found.</returns>
   public IReadOnlyList<string> ListDrivers()
   {
      return OdbcDriverManager.ListDrivers()
         ?? ( OperatingSystem.IsWindows() ? Array.Empty<string>() : OdbcIniFile.ReadDrivers( OdbcIniFile.DriverListPath() ) );
   }

   /// <summary>
   /// Builds the connection string to open. A configured connection adds its user and the
   /// password from its password file; a DSN becomes "DSN=name"; a raw string is used as given.
   /// Credentials typed on the page then override the login (UID and PWD, with other spellings
   /// of those keys removed so the driver never sees two logins).
   /// </summary>
   /// <param name="connection">Configured name, DSN name, or raw connection string.</param>
   /// <param name="credentials">Login for this request, or <see cref="OdbcCredentials.None"/>.</param>
   /// <returns>The connection string, password included. Never log or show it.</returns>
   /// <exception cref="ArgumentException">The connection is empty, unknown, or not a readable connection string.</exception>
   /// <exception cref="InvalidOperationException">A configured password file has no password.</exception>
   public string ResolveConnectionString( string connection, OdbcCredentials credentials )
   {
      if( string.IsNullOrWhiteSpace( connection ) )
      {
         throw new ArgumentException( "Pick an ODBC connection." );
      }

      string name = connection.Trim();
      OdbcConnectionSetting? configured = _settings.OdbcConnections.FirstOrDefault( c => string.Equals( c.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase ) );
      var builder = new OdbcConnectionStringBuilder();
      if( configured != null )
      {
         Parse( builder, configured.ConnectionString, $"The connection string configured for '{configured.Name}'" );
         SetLogin( builder, configured.User, configured.ReadPassword() );
      }
      else if( name.Contains( '=' ) )
      {
         Parse( builder, name, "That ODBC connection string" );
      }
      else if( DataSources().Any( d => d.Name.Equals( name, StringComparison.OrdinalIgnoreCase ) ) )
      {
         builder.Dsn = name;
      }
      else
      {
         throw new ArgumentException( $"No configured ODBC connection or data source (DSN) is named '{name}'." );
      }

      SetLogin( builder, credentials.User, credentials.Password );
      return builder.ConnectionString;
   }

   /// <summary>
   /// Lists the user tables and views of a connection, with row counts where they are cheap.
   /// </summary>
   /// <param name="connection">Configured name, DSN name, or raw connection string.</param>
   /// <param name="credentials">Login for this request.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Tables and views sorted by schema and name.</returns>
   /// <exception cref="InvalidOperationException">The connection failed or the catalog could not be read; the message is plain and has no password.</exception>
   public Task<IReadOnlyList<OdbcTableInfo>> ListTablesAsync( string connection, OdbcCredentials credentials, CancellationToken ct )
   {
      return WithConnectionAsync<IReadOnlyList<OdbcTableInfo>>( connection, credentials, "Could not list the tables",
         open => OdbcMetadata.ListTables( open, withCounts: true, ct ), ct );
   }

   /// <summary>
   /// Previews one table or view: the columns a run reads, ten sample rows formatted as a run
   /// formats them, the suggested row id, and the columns that are skipped.
   /// </summary>
   /// <param name="connection">Configured name, DSN name, or raw connection string.</param>
   /// <param name="credentials">Login for this request.</param>
   /// <param name="table">The table or view.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The preview.</returns>
   /// <exception cref="InvalidOperationException">The connection failed or the table could not be read; the message is plain and has no password.</exception>
   public Task<OdbcTablePreview> PreviewAsync( string connection, OdbcCredentials credentials, OdbcSelection table, CancellationToken ct )
   {
      string label = table.Schema.Length > 0 ? $"{table.Schema}.{table.Name}" : table.Name;
      string connectionName = ConnectionName( connection );
      return WithConnectionAsync( connection, credentials, $"Could not preview {label}", open =>
      {
         string? quote = OdbcNaming.QuoteCharOf( open );
         OdbcTableShape shape = OdbcMetadata.LoadShape( open, table, quote, ct );
         List<IReadOnlyList<string?>> sample = OdbcMetadata.Sample( open, shape, table, quote, PREVIEW_ROWS, ct );
         bool isView = OdbcMetadata.IsView( open, table );
         long? rowCount = isView ? null : OdbcMetadata.CheapRowCount( open, table );
         string? key = OdbcKeyFinder.Suggest( open, table, shape, quote, isView, rowCount, ct );
         return new OdbcTablePreview( table.Schema, table.Name, OdbcNaming.TableName( table ), OdbcNaming.TableId( connectionName, table ),
            shape.Columns.Select( c => c.Name ).ToList(), sample, key, shape.Skipped );
      }, ct );
   }

   /// <summary>
   /// Creates the run's source for a connection and the chosen tables, with the same connection
   /// name the preview used, so table ids and mappings line up.
   /// </summary>
   /// <param name="connection">Configured name, DSN name, or raw connection string.</param>
   /// <param name="credentials">Login for this run.</param>
   /// <param name="tables">Tables and views to read.</param>
   /// <returns>The source.</returns>
   public OdbcSource CreateSource( string connection, OdbcCredentials credentials, IReadOnlyList<OdbcSelection> tables )
   {
      return new OdbcSource( ConnectionName( connection ), ResolveConnectionString( connection, credentials ), tables );
   }

   /// <summary>
   /// True when connection settings already carry a login: a user and password, an integrated
   /// sign-in, or a driver over local files that needs none.
   /// </summary>
   /// <param name="values">Connection string or DSN settings.</param>
   /// <param name="driver">Driver name, if known.</param>
   /// <returns>True when no login has to be typed.</returns>
   internal static bool CarriesLogin( IReadOnlyDictionary<string, string> values, string? driver )
   {
      bool hasUser = USER_KEYS.Any( k => values.TryGetValue( k, out string? v ) && v.Length > 0 );
      bool hasPassword = PASSWORD_KEYS.Any( k => values.TryGetValue( k, out string? v ) && v.Length > 0 );
      bool integrated = ( values.TryGetValue( "Trusted_Connection", out string? trusted ) && TRUE_VALUES.Contains( trusted, StringComparer.OrdinalIgnoreCase ) )
         || ( values.TryGetValue( "Integrated Security", out string? integratedSecurity ) && TRUE_VALUES.Contains( integratedSecurity, StringComparer.OrdinalIgnoreCase ) )
         || ( values.TryGetValue( "Authentication", out string? method ) && PASSWORDLESS_AUTHENTICATION.Contains( method, StringComparer.OrdinalIgnoreCase ) );
      bool fileDriver = driver != null && FILE_DRIVER_HINTS.Any( h => driver.Contains( h, StringComparison.OrdinalIgnoreCase ) );
      return ( hasUser && hasPassword ) || integrated || fileDriver;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// DSNs from the driver manager, or from the ini files when it cannot be asked (Linux and
   /// macOS only; on Windows the driver manager is part of the system).
   /// </summary>
   /// <returns>User DSNs, then system DSNs.</returns>
   private IReadOnlyList<OdbcDsnEntry> DataSources()
   {
      IReadOnlyList<OdbcDsnEntry>? native = NativeDataSources();
      if( native != null || OperatingSystem.IsWindows() )
      {
         return native ?? Array.Empty<OdbcDsnEntry>();
      }

      return OdbcIniFile.ReadDataSources( UserIniPath ).Select( s => new OdbcDsnEntry( s.Name, DriverOf( s.Values ), true ) )
         .Concat( OdbcIniFile.ReadDataSources( SystemIniPath ).Select( s => new OdbcDsnEntry( s.Name, DriverOf( s.Values ), false ) ) )
         .ToList();
   }

   /// <summary>
   /// A DSN's own settings: its odbc.ini section on Linux and macOS, its registry key on Windows.
   /// </summary>
   /// <param name="dsn">The DSN.</param>
   /// <returns>The settings; empty when they cannot be read.</returns>
   private IReadOnlyDictionary<string, string> DsnSettings( OdbcDsnEntry dsn )
   {
      if( OperatingSystem.IsWindows() )
      {
         return WindowsDsnSettings( dsn );
      }

      return OdbcIniFile.ReadDataSources( dsn.IsUser ? UserIniPath : SystemIniPath )
         .FirstOrDefault( s => s.Name.Equals( dsn.Name, StringComparison.OrdinalIgnoreCase ) ).Values
         ?? new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
   }

   /// <summary>
   /// Reads a DSN's settings from HKCU (user) or HKLM (system) Software\ODBC\ODBC.INI\name.
   /// </summary>
   /// <param name="dsn">The DSN.</param>
   /// <returns>The settings; empty when the key cannot be read.</returns>
   [System.Runtime.Versioning.SupportedOSPlatform( "windows" )]
   private static IReadOnlyDictionary<string, string> WindowsDsnSettings( OdbcDsnEntry dsn )
   {
      var values = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
      try
      {
         Microsoft.Win32.RegistryKey root = dsn.IsUser ? Microsoft.Win32.Registry.CurrentUser : Microsoft.Win32.Registry.LocalMachine;
         using Microsoft.Win32.RegistryKey? key = root.OpenSubKey( $@"Software\ODBC\ODBC.INI\{dsn.Name}" );
         foreach( string valueName in key?.GetValueNames() ?? Array.Empty<string>() )
         {
            values[valueName] = key!.GetValue( valueName )?.ToString() ?? string.Empty;
         }
      }
      catch( Exception ex ) when( ex is System.Security.SecurityException or UnauthorizedAccessException or IOException )
      {
         values.Clear();
      }

      return values;
   }

   /// <summary>
   /// Opens the connection on the thread pool, runs the work, and turns any database failure
   /// into an InvalidOperationException with a plain message and the password masked. The
   /// original exception is deliberately not attached, since its text is not under our control.
   /// </summary>
   /// <typeparam name="T">Result type.</typeparam>
   /// <param name="connection">Configured name, DSN name, or raw connection string.</param>
   /// <param name="credentials">Login for this request.</param>
   /// <param name="action">Start of the error message, e.g. "Could not list the tables".</param>
   /// <param name="work">What to do with the open connection.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The work's result.</returns>
   private async Task<T> WithConnectionAsync<T>( string connection, OdbcCredentials credentials, string action, Func<OdbcConnection, T> work, CancellationToken ct )
   {
      string connectionString = ResolveConnectionString( connection, credentials );
      string? secret = OdbcErrors.SecretOf( connectionString ) ?? credentials.Password;
      return await Task.Run( () =>
      {
         try
         {
            using var open = new OdbcConnection( connectionString );
            open.Open();
            return work( open );
         }
         catch( Exception ex ) when( ex is OdbcException or InvalidOperationException or ArgumentException )
         {
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException( $"{action}: {OdbcErrors.Describe( ex, secret )}" );
         }
      }, ct );
   }

   /// <summary>
   /// Loads a connection string into a builder, with a plain error that never repeats the string.
   /// </summary>
   /// <param name="builder">The builder.</param>
   /// <param name="connectionString">The connection string.</param>
   /// <param name="what">What the string is, for the error message.</param>
   /// <exception cref="ArgumentException">The string is not a valid connection string.</exception>
   private static void Parse( OdbcConnectionStringBuilder builder, string connectionString, string what )
   {
      try
      {
         builder.ConnectionString = connectionString;
      }
      catch( ArgumentException )
      {
         throw new ArgumentException( $"{what} could not be read. Check its key=value pairs and braces." );
      }
   }

   /// <summary>
   /// Sets the login on a connection string: a non-empty user replaces every user key with UID,
   /// and a non-null password replaces every password key with PWD.
   /// </summary>
   /// <param name="builder">The builder.</param>
   /// <param name="user">User, or null or empty to keep the current one.</param>
   /// <param name="password">Password, or null to keep the current one.</param>
   private static void SetLogin( OdbcConnectionStringBuilder builder, string? user, string? password )
   {
      if( !string.IsNullOrEmpty( user ) )
      {
         Array.ForEach( USER_KEYS, k => builder.Remove( k ) );
         builder["UID"] = user;
      }

      if( password != null )
      {
         Array.ForEach( PASSWORD_KEYS, k => builder.Remove( k ) );
         builder["PWD"] = password;
      }
   }

   /// <summary>
   /// Parses a connection string into its settings, for the login check. An unreadable string
   /// has no settings.
   /// </summary>
   /// <param name="connectionString">The connection string.</param>
   /// <returns>Settings by key, case-insensitive.</returns>
   private static Dictionary<string, string> ParseConnectionString( string connectionString )
   {
      var values = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
      try
      {
         var builder = new OdbcConnectionStringBuilder( connectionString );
         foreach( string key in builder.Keys.Cast<string>() )
         {
            values[key] = Convert.ToString( builder[key], System.Globalization.CultureInfo.InvariantCulture ) ?? string.Empty;
         }
      }
      catch( ArgumentException )
      {
         values.Clear();
      }

      return values;
   }

   /// <summary>
   /// The driver an odbc.ini section names: its Driver setting, a driver name or a library path.
   /// </summary>
   /// <param name="values">The section's settings.</param>
   /// <returns>The driver, or null.</returns>
   private static string? DriverOf( Dictionary<string, string> values )
   {
      return values.TryGetValue( "Driver", out string? driver ) && driver.Length > 0 ? driver : null;
   }

   #endregion Private Methods
}
