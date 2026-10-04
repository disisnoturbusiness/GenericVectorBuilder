namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Connection and index settings for <see cref="MongoDbSink"/>. The defaults point at the local
/// gvb-mongodb container (deploy/engines/mongodb.compose.yaml) and read its password from the
/// shared secrets file, so a parameterless sink works on this box with nothing configured.
/// Why the index numbers live here: the benchmark report prints them next to the timings, and
/// a run with different numbers is a different experiment.
/// </summary>
public sealed class MongoDbSinkOptions
{
   #region Data Members

   private const string SECRETS_ENV = "GVB_ENGINE_SECRETS";
   private const string PASSWORD_KEY = "MONGODB_INITDB_ROOT_PASSWORD";

   #endregion Data Members

   #region Public Methods

   /// <summary>MongoDB host.</summary>
   public string Host { get; set; } = "127.0.0.1";

   /// <summary>MongoDB port.</summary>
   public int Port { get; set; } = 27017;

   /// <summary>Login name (the compose file creates this root user).</summary>
   public string User { get; set; } = "gvb";

   /// <summary>Login password, or null to connect without authentication.</summary>
   public string? Password { get; set; }

   /// <summary>Database that holds one collection per pipeline; created on first write.</summary>
   public string Database { get; set; } = "gvb";

   /// <summary>HNSW maxEdges: links per node in the graph (Atlas default 16).</summary>
   public int HnswMaxEdges { get; set; } = 16;

   /// <summary>HNSW numEdgeCandidates: candidate list size while building (Atlas default 100).</summary>
   public int HnswNumEdgeCandidates { get; set; } = 128;

   /// <summary>
   /// How many candidates $vectorSearch examines per hit wanted. MongoDB asks for 10 to 20 times
   /// the limit; there is no engine default, the field is required.
   /// </summary>
   public int NumCandidatesFactor { get; set; } = 20;

   /// <summary>Smallest numCandidates ever sent, so a search for 1 hit still looks wide.</summary>
   public int MinNumCandidates { get; set; } = 100;

   /// <summary>
   /// Longest <see cref="MongoDbSink.FinishLoadAsync"/> waits for mongot to index every stored
   /// vector before it fails with a plain message.
   /// </summary>
   public int IndexWaitSeconds { get; set; } = 1800;

   /// <summary>How many documents go into one bulk write.</summary>
   public int UpsertBatch { get; set; } = 500;

   /// <summary>
   /// Options for the local container, with the password read from the shared secrets file
   /// (/home/dan/gvb-data/engines/secrets.env, or the file named by GVB_ENGINE_SECRETS).
   /// </summary>
   /// <returns>Options with the password filled in when the file has one.</returns>
   public static MongoDbSinkOptions LocalDefaults()
   {
      return new MongoDbSinkOptions { Password = ReadSecret( PASSWORD_KEY ) };
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
