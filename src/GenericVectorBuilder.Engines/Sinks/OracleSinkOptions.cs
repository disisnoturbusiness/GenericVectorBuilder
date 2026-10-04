namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Connection and index settings for <see cref="OracleSink"/>. The defaults point at the local
/// gvb-oracle container (deploy/engines/oracle.compose.yaml), pluggable database FREEPDB1, and
/// read the application login from the shared secrets file, so a parameterless sink works on this
/// box with nothing configured.
/// Why the index numbers live here: the benchmark report prints them next to the timings, and
/// a run with different numbers is a different experiment.
/// </summary>
public sealed class OracleSinkOptions
{
   #region Data Members

   private const string SECRETS_ENV = "GVB_ENGINE_SECRETS";
   private const string USER_KEY = "APP_USER";
   private const string PASSWORD_KEY = "APP_USER_PASSWORD";
   private const string ADMIN_PASSWORD_KEY = "ORACLE_PASSWORD";
   private const string ADMIN_USER = "system";

   #endregion Data Members

   #region Public Methods

   /// <summary>Oracle listener host.</summary>
   public string Host { get; set; } = "127.0.0.1";

   /// <summary>Oracle listener port.</summary>
   public int Port { get; set; } = 1521;

   /// <summary>Service name of the pluggable database the tables live in.</summary>
   public string ServiceName { get; set; } = "FREEPDB1";

   /// <summary>Application login (the image creates this user from APP_USER on first start).</summary>
   public string User { get; set; } = "gvb";

   /// <summary>Application password, or null when neither the secrets file nor the environment has one.</summary>
   public string? Password { get; set; }

   /// <summary>HNSW NEIGHBORS: links per node in the graph (the usual "m").</summary>
   public int HnswNeighbors { get; set; } = 16;

   /// <summary>HNSW EFCONSTRUCTION: candidate list size while building the graph.</summary>
   public int HnswEfConstruction { get; set; } = 128;

   /// <summary>
   /// EFSEARCH: how many candidates the HNSW graph search keeps while looking for the hits, passed
   /// on every default search as WITH TARGET ACCURACY PARAMETERS ( EFSEARCH n ). Without it Oracle
   /// picks a size from a target accuracy percentage, and measured on this box that percentage
   /// did not change the recall at all.
   /// </summary>
   public int HnswEfSearch { get; set; } = 100;

   /// <summary>
   /// Login that may read the V$VECTOR_* views, used only for those reads (and the statistics the
   /// durability test reads), never for data. Why a second login: the application login has no
   /// access to them, and they are the only place where Oracle says how many vectors the HNSW graph
   /// holds and how many changes are still waiting to be merged into it. Null or empty means the
   /// application login is used, which works when SELECT on V_$VECTOR_INDEX and
   /// V_$VECTOR_CHANGE_LOG_PARTITION has been granted to it.
   /// </summary>
   public string? EvidenceUser { get; set; }

   /// <summary>Password of <see cref="EvidenceUser"/>.</summary>
   public string? EvidencePassword { get; set; }

   /// <summary>
   /// Longest <see cref="OracleSink.FinishLoadAsync"/> waits for the HNSW graph rebuild and the
   /// state check before it fails. Measured 2026-10-03: rebuilding 100,000 1024-dimension vectors
   /// took 284 s on Oracle Free's two CPU threads.
   /// </summary>
   public int IndexWaitMinutes { get; set; } = 30;

   /// <summary>How many rows go into one array-bound round trip.</summary>
   public int UpsertBatch { get; set; } = 500;

   /// <summary>
   /// Options for the local container, with the login read from the shared secrets file
   /// (/home/dan/gvb-data/engines/secrets.env, or the file named by GVB_ENGINE_SECRETS).
   /// </summary>
   /// <returns>Options with the user and password filled in when the file has them.</returns>
   public static OracleSinkOptions LocalDefaults()
   {
      string? admin = ReadSecret( ADMIN_PASSWORD_KEY );
      var options = new OracleSinkOptions { Password = ReadSecret( PASSWORD_KEY ), EvidenceUser = string.IsNullOrEmpty( admin ) ? null : ADMIN_USER, EvidencePassword = admin };
      string? user = ReadSecret( USER_KEY );
      if( !string.IsNullOrWhiteSpace( user ) )
      {
         options.User = user;
      }

      return options;
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
