using Microsoft.Data.SqlClient;

namespace GenericVectorBuilder.Core.Configuration;

/// <summary>
/// Every setting the builder needs, bound from the "Gvb" section of appsettings.json and
/// overridable by environment variables (Gvb__SqlServer=...). Defaults match linus7795.
/// Credentials are never stored here: the SQL password comes from the GVB_SQL_PASSWORD
/// environment variable, or from the last "pass:" line of a password file.
/// </summary>
public sealed class GvbSettings
{
   #region Data Members

   private const string PASSWORD_PREFIX = "pass:";
   private const string PASSWORD_ENV = "GVB_SQL_PASSWORD";
   private const string DEFAULT_DATA_ROOT = "~/share";

   #endregion Data Members

   #region Public Methods

   /// <summary>Root URL of the GPU embedding service.</summary>
   public string EmbedUrl { get; set; } = "http://localhost:8080/";

   /// <summary>Model key used when the request does not name one.</summary>
   public string DefaultEmbedModel { get; set; } = "qwen3-emb-0.6b";

   /// <summary>SQL Server host.</summary>
   public string SqlServer { get; set; } = "localhost";

   /// <summary>Database that holds the pipeline tables; created if missing.</summary>
   public string SqlDatabase { get; set; } = "GenericVectorBuilder";

   /// <summary>SQL login.</summary>
   public string SqlUser { get; set; } = "sa";

   /// <summary>File whose last "pass:" line is the SQL password.</summary>
   public string SqlPasswordFile { get; set; } = "~/mssql-sa-password.txt";

   /// <summary>Qdrant host.</summary>
   public string QdrantHost { get; set; } = "localhost";

   /// <summary>Qdrant gRPC port.</summary>
   public int QdrantGrpcPort { get; set; } = 6334;

   /// <summary>SQLite state file.</summary>
   public string StatePath { get; set; } = "~/.local/share/GenericVectorBuilder/state.db";

   /// <summary>
   /// Folders the browser is allowed to scan. Anything outside is refused. Starts EMPTY on
   /// purpose: the .NET config binder appends to a pre-filled list instead of replacing it, so
   /// defaults here would silently stay allowed even when appsettings names other folders.
   /// <see cref="Normalize"/> fills in the default only when nothing was configured.
   /// </summary>
   public List<string> DataRoots { get; set; } = new();

   /// <summary>Where browser uploads are stored, one sub-folder per upload.</summary>
   public string UploadRoot { get; set; } = "~/gvb-data/uploads";

   /// <summary>
   /// Where the "Git repo" mode keeps its copies, one sub-folder per repository. Kept apart from
   /// the data roots because a copy is rewritten on every fetch and is never edited by hand.
   /// </summary>
   public string ReposRoot { get; set; } = "~/gvb-data/repos";

   /// <summary>
   /// Seconds the "Git repo" mode waits for a repository address to answer before giving up with
   /// a plain reason. Why short: an address that does not answer would otherwise hold the page
   /// for the minutes the network takes to give up.
   /// </summary>
   public int GitReachTimeoutSeconds { get; set; } = 20;

   /// <summary>
   /// ODBC connections set up by the admin, listed on the page beside the machine's DSNs. Each
   /// names a connection string, and optionally a user and a password file, so a run can sign in
   /// without anyone typing a password. Starts empty for the same reason as
   /// <see cref="DataRoots"/>: the config binder appends to a pre-filled list.
   /// </summary>
   public List<OdbcConnectionSetting> OdbcConnections { get; set; } = new();

   /// <summary>
   /// Applies defaults that cannot be property initializers: ~/share when no data root is
   /// configured, and the upload folder always allowed (uploads are scanned from there).
   /// </summary>
   /// <returns>This instance, for chaining.</returns>
   public GvbSettings Normalize()
   {
      if( DataRoots.Count == 0 )
      {
         DataRoots.Add( DEFAULT_DATA_ROOT );
      }

      DataRoots.Add( UploadRoot );
      DataRoots = DataRoots.Select( Expand ).Distinct( StringComparer.Ordinal ).ToList();
      return this;
   }

   /// <summary>
   /// Expands a leading "~/" to the user's home folder and makes the path absolute.
   /// </summary>
   /// <param name="path">Path from config.</param>
   /// <returns>Absolute path.</returns>
   public static string Expand( string path )
   {
      string home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
      string expanded = path == "~" ? home : path.StartsWith( "~/", StringComparison.Ordinal ) ? Path.Combine( home, path[2..] ) : path;
      return Path.GetFullPath( expanded );
   }

   /// <summary>
   /// True when a path is one of the allowed data roots or inside one. Stops the web page
   /// from being used to read arbitrary folders on the server. Never throws: an empty path, a
   /// path with a NUL character or one the operating system cannot parse is simply not allowed,
   /// so the endpoints answer 400 instead of 500.
   /// A symbolic link anywhere below a root also makes the path not allowed, because a link
   /// could lead out of the data folders and the string check alone cannot see that.
   /// </summary>
   /// <param name="path">Requested path.</param>
   /// <returns>True when allowed.</returns>
   public bool IsAllowedDataPath( string? path )
   {
      if( string.IsNullOrWhiteSpace( path ) || path.Contains( '\0' ) )
      {
         return false;
      }

      try
      {
         string full = Expand( path ).TrimEnd( '/' );
         foreach( string root in DataRoots.Select( r => Expand( r ).TrimEnd( '/' ) ) )
         {
            if( full == root || full.StartsWith( root + "/", StringComparison.Ordinal ) )
            {
               return !HasLinkBelow( root, full );
            }
         }

         return false;
      }
      catch( Exception ex ) when( ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException )
      {
         return false;
      }
   }

   /// <summary>
   /// The folder one browser upload lives in.
   /// </summary>
   /// <param name="uploadId">Upload id from POST /api/uploads.</param>
   /// <returns>Absolute folder path (it may not exist).</returns>
   public string UploadFolder( string uploadId )
   {
      return Path.Combine( Expand( UploadRoot ), uploadId );
   }

   /// <summary>
   /// Works out where an uploaded file goes inside its upload folder. The browser sends each
   /// file's relative path as the multipart file name so sub-folders survive. Anything that
   /// could land outside the folder, or is not a usable file name, is refused instead of
   /// throwing: ".." climbs, empty names, names ending in "/", names with a NUL character,
   /// and names that point at an existing folder.
   /// </summary>
   /// <param name="uploadFolder">The upload's folder.</param>
   /// <param name="fileName">File name as sent by the browser (may use "\" or "/").</param>
   /// <param name="target">The absolute target path when allowed.</param>
   /// <returns>True when the file may be written at <paramref name="target"/>.</returns>
   public static bool TryResolveUploadTarget( string uploadFolder, string? fileName, out string target )
   {
      target = string.Empty;
      if( string.IsNullOrWhiteSpace( fileName ) || fileName.Contains( '\0' ) )
      {
         return false;
      }

      string relative = fileName.Replace( '\\', '/' ).TrimStart( '/' );
      if( relative.Length == 0 || relative.EndsWith( '/' ) )
      {
         return false;
      }

      try
      {
         string root = Path.GetFullPath( uploadFolder ).TrimEnd( '/' );
         string full = Path.GetFullPath( Path.Combine( root, relative ) );
         if( !full.StartsWith( root + "/", StringComparison.Ordinal ) || Directory.Exists( full ) )
         {
            return false;
         }

         target = full;
         return true;
      }
      catch( Exception ex ) when( ex is ArgumentException or NotSupportedException or IOException )
      {
         return false;
      }
   }

   /// <summary>
   /// Builds the SQL connection string (no initial catalog; the sink sets it). The server's
   /// certificate is self-signed on a LAN box, so it is trusted explicitly.
   /// </summary>
   /// <returns>The connection string.</returns>
   /// <exception cref="InvalidOperationException">No password is available.</exception>
   public string BuildSqlConnectionString()
   {
      var builder = new SqlConnectionStringBuilder
      {
         DataSource = SqlServer,
         UserID = SqlUser,
         Password = ReadPassword(),
         TrustServerCertificate = true,
         Encrypt = true,
         ConnectTimeout = 15,
      };

      return builder.ConnectionString;
   }

   /// <summary>
   /// Reads the password from a password file: the LAST line starting with "pass:", with the
   /// prefix, surrounding spaces and any Windows carriage return removed (a CRLF copy once made
   /// the password silently wrong on this box). Shared by the SQL sink login and the ODBC
   /// connections so both read the file the same way.
   /// </summary>
   /// <param name="file">Absolute path of the password file.</param>
   /// <returns>The password, or null when the file is missing or has no "pass:" line.</returns>
   public static string? ReadPasswordFile( string file )
   {
      string? line = File.Exists( file ) ? File.ReadAllLines( file ).LastOrDefault( l => l.StartsWith( PASSWORD_PREFIX, StringComparison.Ordinal ) ) : null;
      return line?[PASSWORD_PREFIX.Length..].Trim().TrimEnd( '\r' );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// True when any folder or file between a data root (exclusive) and the requested path
   /// (inclusive) is a symbolic link. Paths that do not exist yet have no link to find.
   /// </summary>
   /// <param name="root">Expanded data root, no trailing slash.</param>
   /// <param name="full">Expanded requested path inside the root, no trailing slash.</param>
   /// <returns>True when a link was found.</returns>
   private static bool HasLinkBelow( string root, string full )
   {
      string current = root;
      foreach( string part in full[root.Length..].Split( '/', StringSplitOptions.RemoveEmptyEntries ) )
      {
         current = current + "/" + part;
         if( new FileInfo( current ).LinkTarget != null )
         {
            return true;
         }
      }

      return false;
   }

   /// <summary>
   /// Reads the SQL password: the environment variable first, else the LAST "pass:" line of
   /// the password file with any Windows carriage return stripped (a CRLF copy once made the
   /// password silently wrong on this box).
   /// </summary>
   /// <returns>The password.</returns>
   private string ReadPassword()
   {
      string? fromEnv = Environment.GetEnvironmentVariable( PASSWORD_ENV );
      if( !string.IsNullOrEmpty( fromEnv ) )
      {
         return fromEnv;
      }

      string file = Expand( SqlPasswordFile );
      return ReadPasswordFile( file )
         ?? throw new InvalidOperationException( $"No SQL password: set {PASSWORD_ENV} or put a 'pass:' line in {file}." );
   }

   #endregion Private Methods
}

/// <summary>
/// One admin-configured ODBC connection from the "Gvb:OdbcConnections" settings list.
/// Why a password FILE and never a password: appsettings.json is copied, committed and shown
/// in diffs. The password stays in a file only the service account can read, as the last
/// "pass:" line, the same convention as the SQL sink's login. A connection string here should
/// not hold a PWD either; use <see cref="PasswordFile"/>.
/// </summary>
public sealed class OdbcConnectionSetting
{
   #region Public Methods

   /// <summary>Name shown on the page and passed back as the connection to use.</summary>
   public string Name { get; set; } = string.Empty;

   /// <summary>ODBC connection string without a password, e.g. "Driver={ODBC Driver 18 for SQL Server};Server=db1;Database=Sales".</summary>
   public string ConnectionString { get; set; } = string.Empty;

   /// <summary>Login name, or null when the connection string or the driver supplies the login.</summary>
   public string? User { get; set; }

   /// <summary>File whose last "pass:" line is the password ("~/" allowed), or null for no stored password.</summary>
   public string? PasswordFile { get; set; }

   /// <summary>
   /// Reads this connection's password from <see cref="PasswordFile"/>.
   /// </summary>
   /// <returns>The password, or null when no password file is configured.</returns>
   /// <exception cref="InvalidOperationException">A password file is configured but has no "pass:" line.</exception>
   public string? ReadPassword()
   {
      if( string.IsNullOrWhiteSpace( PasswordFile ) )
      {
         return null;
      }

      string file = GvbSettings.Expand( PasswordFile );
      return GvbSettings.ReadPasswordFile( file )
         ?? throw new InvalidOperationException( $"No password for the ODBC connection '{Name}': put a 'pass:' line in {file}." );
   }

   #endregion Public Methods
}
