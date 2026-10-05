namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// Reads one value from the engines' shared secrets file, /home/dan/gvb-data/engines/secrets.env
/// (mode 600, outside the repository), or from the file named by GVB_ENGINE_SECRETS.
/// Why the same rules as the engine sinks' own readers: the compose files hand this file to the
/// containers with env_file, so the benchmark must read a value exactly the way Docker does
/// (KEY=VALUE, last line wins, # comments and blank lines skipped) or it would log in with a
/// different password than the container was given. A Windows carriage return is stripped.
/// Values are never logged and never put into an exception message.
/// </summary>
public static class EngineSecrets
{
   #region Data Members

   /// <summary>Environment variable that names another secrets file (tests use it).</summary>
   public const string SECRETS_ENV = "GVB_ENGINE_SECRETS";

   #endregion Data Members

   #region Public Methods

   /// <summary>The secrets file in use.</summary>
   public static string FilePath => Environment.GetEnvironmentVariable( SECRETS_ENV )
      ?? Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), "gvb-data", "engines", "secrets.env" );

   /// <summary>
   /// Reads a secret.
   /// </summary>
   /// <param name="key">Its name, e.g. "MSSQL_SA_PASSWORD".</param>
   /// <returns>The value.</returns>
   /// <exception cref="InvalidOperationException">The file or the key is missing (the message names the file and key, never a value).</exception>
   public static string Read( string key )
   {
      string path = FilePath;
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

      return string.IsNullOrEmpty( value )
         ? throw new InvalidOperationException( $"No {key} in {path}. Add a line {key}=<value> (mode 600) before starting the container that needs it." )
         : value;
   }

   #endregion Public Methods
}
