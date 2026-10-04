namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Connection and index settings for <see cref="MariaDbSink"/>. The defaults point at the local
/// gvb-mariadb container (deploy/engines/mariadb.compose.yaml) and read its root password from
/// the shared secrets file, so a parameterless sink works on this box with nothing configured.
/// Why the index numbers live here: the benchmark report prints them next to the timings, and
/// a run with different numbers is a different experiment.
/// </summary>
public sealed class MariaDbSinkOptions
{
   #region Data Members

   private const string SECRETS_ENV = "GVB_ENGINE_SECRETS";
   private const string PASSWORD_KEY = "MARIADB_ROOT_PASSWORD";

   #endregion Data Members

   #region Public Methods

   /// <summary>MariaDB host.</summary>
   public string Host { get; set; } = "127.0.0.1";

   /// <summary>MariaDB port.</summary>
   public int Port { get; set; } = 3306;

   /// <summary>
   /// Login name. The local container only has root (MARIADB_ROOT_PASSWORD is the one password
   /// its compose file sets), so that is the default.
   /// </summary>
   public string User { get; set; } = "root";

   /// <summary>Login password, or null to connect without one.</summary>
   public string? Password { get; set; }

   /// <summary>Database that holds one table per pipeline; created on first use.</summary>
   public string Database { get; set; } = "gvb";

   /// <summary>
   /// VECTOR INDEX M: links per node in the graph (MariaDB's own default comes from
   /// mhnsw_default_m, which is 6). MariaDB has no ef_construction setting to go with it: the
   /// build-time candidate list is fixed inside the server, so it is not tunable here.
   /// </summary>
   public int HnswM { get; set; } = 16;

   /// <summary>
   /// mhnsw_ef_search for each search: how many candidates the graph walk keeps (MariaDB's
   /// default is 20, which finds under half the true neighbours at 100,000 vectors; the sink
   /// documentation has the measured curve). Applied to one statement only (SET STATEMENT),
   /// never to the server. The server accepts 1 to 10000.
   /// </summary>
   public int HnswEfSearch { get; set; } = 3200;

   /// <summary>
   /// How many rows go into one INSERT. Each row also updates the HNSW graph, which costs far
   /// more than parsing the row, so a bigger batch only saves round trips.
   /// </summary>
   public int UpsertBatch { get; set; } = 500;

   /// <summary>
   /// Options for the local container, with the password read from the shared secrets file
   /// (/home/dan/gvb-data/engines/secrets.env, or the file named by GVB_ENGINE_SECRETS).
   /// </summary>
   /// <returns>Options with the password filled in when the file has one.</returns>
   public static MariaDbSinkOptions LocalDefaults()
   {
      return new MariaDbSinkOptions { Password = ReadSecret( PASSWORD_KEY ) };
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads one KEY=VALUE secret. Blank lines and # comments are skipped, a carriage return from
   /// a Windows-edited file is stripped, and the last matching line wins. The value is never
   /// logged. Falls back to the environment variable of the same name.
   /// </summary>
   /// <param name="key">Secret name.</param>
   /// <returns>The value, or null when neither the file nor the environment has it.</returns>
   private static string? ReadSecret( string key )
   {
      string path = Environment.GetEnvironmentVariable( SECRETS_ENV )
         ?? Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), "gvb-data", "engines", "secrets.env" );
      string? value = null;
      if( File.Exists( path ) )
      {
         foreach( string raw in File.ReadLines( path ) )
         {
            string line = raw.Replace( "\r", string.Empty );
            int split = line.IndexOf( '=' );
            if( split > 0 && !line.StartsWith( '#' ) && string.Equals( line[..split].Trim(), key, StringComparison.Ordinal ) )
            {
               value = line[( split + 1 )..];
            }
         }
      }

      return value ?? Environment.GetEnvironmentVariable( key );
   }

   #endregion Private Methods
}
