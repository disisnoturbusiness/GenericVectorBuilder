namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Connection and index settings for <see cref="RedisSink"/>. The defaults point at the local
/// gvb-redis container (deploy/engines/redis.compose.yaml) and read its password from the
/// shared secrets file, so a parameterless sink works on this box with nothing configured.
/// Why the index numbers live here: the benchmark report prints them next to the timings, and
/// a run with different numbers is a different experiment.
/// </summary>
public sealed class RedisSinkOptions
{
   #region Data Members

   private const string SECRETS_ENV = "GVB_ENGINE_SECRETS";
   private const string PASSWORD_KEY = "REDIS_PASSWORD";

   #endregion Data Members

   #region Public Methods

   /// <summary>Redis host.</summary>
   public string Host { get; set; } = "127.0.0.1";

   /// <summary>Redis port.</summary>
   public int Port { get; set; } = 6379;

   /// <summary>Redis password, or null for none.</summary>
   public string? Password { get; set; }

   /// <summary>HNSW M: links per node in the graph.</summary>
   public int HnswM { get; set; } = 16;

   /// <summary>HNSW EF_CONSTRUCTION: candidate list size while building the graph.</summary>
   public int HnswEfConstruction { get; set; } = 128;

   /// <summary>
   /// HNSW EF_RUNTIME: candidate list size while searching. Redis defaults to 10, which equals
   /// the number of hits the pipeline asks for and gives poor recall, so the sink sets its own.
   /// </summary>
   public int HnswEfRuntime { get; set; } = 100;

   /// <summary>How many records go into one pipelined round trip.</summary>
   public int UpsertBatch { get; set; } = 500;

   /// <summary>
   /// Options for the local container, with the password read from the shared secrets file
   /// (/home/dan/gvb-data/engines/secrets.env, or the file named by GVB_ENGINE_SECRETS).
   /// </summary>
   /// <returns>Options with the password filled in when the file has one.</returns>
   public static RedisSinkOptions LocalDefaults()
   {
      return new RedisSinkOptions { Password = ReadSecret( PASSWORD_KEY ) };
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
