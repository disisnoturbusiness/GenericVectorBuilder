namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Connection and index settings for <see cref="PgVectorSink"/>. The defaults point at the
/// local gvb-pgvector container (deploy/engines/pgvector.compose.yaml) and read its password
/// from the shared secrets file, so a parameterless sink works on this box with nothing
/// configured.
/// Why the index numbers live here: the benchmark report prints them next to the timings, and
/// a run with different numbers is a different experiment.
/// </summary>
public sealed class PgVectorSinkOptions
{
   #region Data Members

   private const string SECRETS_ENV = "GVB_ENGINE_SECRETS";
   private const string PASSWORD_KEY = "POSTGRES_PASSWORD";

   #endregion Data Members

   #region Public Methods

   /// <summary>PostgreSQL host.</summary>
   public string Host { get; set; } = "127.0.0.1";

   /// <summary>PostgreSQL port.</summary>
   public int Port { get; set; } = 5432;

   /// <summary>Database that holds one table per pipeline (the compose file creates it).</summary>
   public string Database { get; set; } = "gvb";

   /// <summary>Login name. The postgres superuser, because the sink creates the vector extension.</summary>
   public string User { get; set; } = "postgres";

   /// <summary>Login password, or null to connect without one.</summary>
   public string? Password { get; set; }

   /// <summary>HNSW m: links per node in the graph (pgvector default 16, allowed 2 to 100).</summary>
   public int HnswM { get; set; } = 16;

   /// <summary>HNSW ef_construction: candidate list size while building (pgvector default 64, allowed 4 to 1000).</summary>
   public int HnswEfConstruction { get; set; } = 128;

   /// <summary>
   /// hnsw.ef_search: candidate list size while searching (pgvector default 40, allowed 1 to
   /// 1000). It is also the most rows an index scan can return, so the sink never sets it below
   /// the number of hits asked for.
   /// </summary>
   public int HnswEfSearch { get; set; } = 100;

   /// <summary>How many records go into one transaction and one round trip.</summary>
   public int UpsertBatch { get; set; } = 500;

   /// <summary>Seconds before a command is cut off. Generous because an exact scan or an index build can be slow.</summary>
   public int CommandTimeoutSeconds { get; set; } = 600;

   /// <summary>
   /// Options for the local container, with the password read from the shared secrets file
   /// (/home/dan/gvb-data/engines/secrets.env, or the file named by GVB_ENGINE_SECRETS).
   /// </summary>
   /// <returns>Options with the password filled in when the file has one.</returns>
   public static PgVectorSinkOptions LocalDefaults()
   {
      return new PgVectorSinkOptions { Password = ReadSecret( PASSWORD_KEY ) };
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
