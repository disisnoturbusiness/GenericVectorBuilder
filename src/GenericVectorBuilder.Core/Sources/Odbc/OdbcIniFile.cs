namespace GenericVectorBuilder.Core.Sources.Odbc;

/// <summary>
/// Reads unixODBC configuration files (odbc.ini, odbcinst.ini): one section per data source or
/// driver, "Key = Value" lines, ";" or "#" comments.
/// Why it exists: when the driver manager cannot be asked directly (the library is missing or
/// too old) the files are the next best answer, and they are also where a DSN's own settings
/// live, which tells the page whether the DSN already carries a login.
/// </summary>
internal static class OdbcIniFile
{
   #region Data Members

   /// <summary>Sections in odbc.ini that are not data sources.</summary>
   private static readonly HashSet<string> NON_DSN_SECTIONS = new( StringComparer.OrdinalIgnoreCase ) { "ODBC Data Sources", "ODBC", "Default" };

   /// <summary>Sections in odbcinst.ini that are not drivers.</summary>
   private static readonly HashSet<string> NON_DRIVER_SECTIONS = new( StringComparer.OrdinalIgnoreCase ) { "ODBC Drivers", "ODBC" };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Parses ini text into sections of key/value pairs. Keys compare case-insensitively, the
   /// first value of a repeated key wins, and section order is kept.
   /// </summary>
   /// <param name="text">File content.</param>
   /// <returns>Sections by name, in file order.</returns>
   public static List<(string Name, Dictionary<string, string> Values)> Parse( string text )
   {
      var sections = new List<(string Name, Dictionary<string, string> Values)>();
      Dictionary<string, string>? current = null;
      foreach( string raw in text.Split( '\n' ) )
      {
         string line = raw.Trim().TrimEnd( '\r' ).Trim();
         if( line.Length == 0 || line[0] is ';' or '#' )
         {
            continue;
         }

         if( line[0] == '[' && line.EndsWith( ']' ) )
         {
            current = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
            sections.Add( (line[1..^1].Trim(), current) );
            continue;
         }

         int equals = line.IndexOf( '=' );
         if( current != null && equals > 0 )
         {
            current.TryAdd( line[..equals].Trim(), line[( equals + 1 )..].Trim() );
         }
      }

      return sections;
   }

   /// <summary>
   /// Lists the data sources in one odbc.ini file with their settings.
   /// </summary>
   /// <param name="path">File path; a missing or unreadable file yields nothing.</param>
   /// <returns>DSN name and settings, in file order.</returns>
   public static List<(string Name, Dictionary<string, string> Values)> ReadDataSources( string? path )
   {
      return ReadSections( path ).Where( s => !NON_DSN_SECTIONS.Contains( s.Name ) ).ToList();
   }

   /// <summary>
   /// Lists the driver names in one odbcinst.ini file.
   /// </summary>
   /// <param name="path">File path; a missing or unreadable file yields nothing.</param>
   /// <returns>Driver names, in file order.</returns>
   public static List<string> ReadDrivers( string? path )
   {
      return ReadSections( path ).Where( s => !NON_DRIVER_SECTIONS.Contains( s.Name ) ).Select( s => s.Name ).ToList();
   }

   /// <summary>
   /// The user odbc.ini unixODBC reads: $ODBCINI when set, else ~/.odbc.ini.
   /// </summary>
   /// <returns>The path.</returns>
   public static string UserDataSourcePath()
   {
      string? fromEnv = Environment.GetEnvironmentVariable( "ODBCINI" );
      return !string.IsNullOrWhiteSpace( fromEnv )
         ? fromEnv
         : Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), ".odbc.ini" );
   }

   /// <summary>
   /// The system odbc.ini unixODBC reads: odbc.ini in $ODBCSYSINI when set, else /etc/odbc.ini.
   /// </summary>
   /// <returns>The path.</returns>
   public static string SystemDataSourcePath()
   {
      return Path.Combine( SystemFolder(), "odbc.ini" );
   }

   /// <summary>
   /// The driver list unixODBC reads: $ODBCINSTINI when set, else odbcinst.ini in $ODBCSYSINI or /etc.
   /// </summary>
   /// <returns>The path.</returns>
   public static string DriverListPath()
   {
      string? fromEnv = Environment.GetEnvironmentVariable( "ODBCINSTINI" );
      return !string.IsNullOrWhiteSpace( fromEnv ) && Path.IsPathRooted( fromEnv ) ? fromEnv : Path.Combine( SystemFolder(), "odbcinst.ini" );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads and parses a file, treating a missing or unreadable file as empty.
   /// </summary>
   /// <param name="path">File path.</param>
   /// <returns>The sections.</returns>
   private static List<(string Name, Dictionary<string, string> Values)> ReadSections( string? path )
   {
      try
      {
         return !string.IsNullOrWhiteSpace( path ) && File.Exists( path ) ? Parse( File.ReadAllText( path ) ) : new();
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
      {
         return new();
      }
   }

   /// <summary>
   /// The folder holding the system ini files: $ODBCSYSINI when set, else /etc.
   /// </summary>
   /// <returns>The folder.</returns>
   private static string SystemFolder()
   {
      string? fromEnv = Environment.GetEnvironmentVariable( "ODBCSYSINI" );
      return !string.IsNullOrWhiteSpace( fromEnv ) ? fromEnv : "/etc";
   }

   #endregion Private Methods
}
