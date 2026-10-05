using System.Text;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// Reads the two server config files the benchmark reports durability from: Qdrant's
/// config.yaml (nested "key: value" lines) and SQL Server's mssql.conf (ini sections).
/// Why a small reader and not a YAML library: the benchmark must not take a new package for two
/// files, and both files only use plain "key: value" or "key = value" lines. A line the reader
/// cannot place (a list item, a tab-indented line) is counted in
/// <see cref="ParsedConfig.SkippedLines"/> so the report can say the reading may be incomplete
/// instead of silently dropping a setting.
/// </summary>
public static class ConfigFiles
{
   #region Data Members

   private static readonly TimeSpan PRIVILEGED_READ_LIMIT = TimeSpan.FromSeconds( 10 );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Reads a text file, or returns null when it is missing or this account may not read it.
   /// Why null and not an exception: a config file that cannot be read must leave a visible gap
   /// in the report, never stop a benchmark that is about to run for an hour.
   /// </summary>
   /// <param name="path">Absolute path.</param>
   /// <returns>The text, or null.</returns>
   public static string? TryReadFile( string path )
   {
      try
      {
         return File.Exists( path ) ? File.ReadAllText( path ) : null;
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
      {
         return null;
      }
   }

   /// <summary>
   /// Reads a file this account cannot open directly (mssql.conf sits in a folder only the
   /// mssql user may list) with "sudo -n cat", which never prompts: this box gives the benchmark
   /// user passwordless sudo, and a box that does not simply yields null here.
   /// </summary>
   /// <param name="path">Absolute path.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The text, or null when neither a direct read nor sudo could read it.</returns>
   public static async Task<string?> ReadWithSudoFallbackAsync( string path, CancellationToken ct )
   {
      string? direct = TryReadFile( path );
      if( direct != null )
      {
         return direct;
      }

      try
      {
         ShellResult result = await Shell.RunAsync( "sudo", new[] { "-n", "cat", path }, PRIVILEGED_READ_LIMIT, ct );
         return result.ExitCode == 0 ? result.Output : null;
      }
      catch( Exception ex ) when( ex is TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException )
      {
         return null;
      }
   }

   /// <summary>
   /// Whether a file exists, asked with "sudo -n test -f" when this account cannot see it (a
   /// container's data folder belongs to the container's user).
   /// </summary>
   /// <param name="path">Absolute path on the host.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True or false, or null when it could not be told.</returns>
   public static async Task<bool?> ExistsWithSudoAsync( string path, CancellationToken ct )
   {
      if( File.Exists( path ) )
      {
         return true;
      }

      try
      {
         ShellResult result = await Shell.RunAsync( "sudo", new[] { "-n", "test", "-f", path }, PRIVILEGED_READ_LIMIT, ct );
         return result.ExitCode switch { 0 => true, 1 => false, _ => null };
      }
      catch( Exception ex ) when( ex is TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException )
      {
         return null;
      }
   }

   /// <summary>
   /// Reads a file inside a running container with "sudo -n docker exec NAME cat PATH", within a
   /// time limit. Why from inside: a container's config file ships in its image, not in the data
   /// folder on the host, and the copy the server really read is the one in the container.
   /// </summary>
   /// <param name="container">Container name.</param>
   /// <param name="path">Absolute path inside the container.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The text, or null when the container or the file could not be read.</returns>
   public static async Task<string?> ReadContainerFileAsync( string container, string path, CancellationToken ct )
   {
      try
      {
         ShellResult result = await Shell.RunAsync( "sudo", new[] { "-n", "docker", "exec", container, "cat", path }, PRIVILEGED_READ_LIMIT, ct );
         return result.ExitCode == 0 ? result.Output : null;
      }
      catch( Exception ex ) when( ex is TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException )
      {
         return null;
      }
   }

   /// <summary>
   /// Flattens nested "key: value" lines to dotted paths, for example "storage.wal.wal_capacity_mb".
   /// A key with no value opens a section; a section ends at the first line indented no deeper
   /// than it. Quotes around a value are removed and "# comment" tails are cut.
   /// </summary>
   /// <param name="text">The file's text.</param>
   /// <returns>The values and the count of lines that were not understood.</returns>
   public static ParsedConfig FlattenYaml( string text )
   {
      var values = new Dictionary<string, string>( StringComparer.Ordinal );
      var sections = new List<( int Indent, string Key )>();
      int skipped = 0;
      foreach( string raw in text.Split( '\n' ) )
      {
         string line = StripComment( raw.TrimEnd( '\r' ) );
         if( string.IsNullOrWhiteSpace( line ) )
         {
            continue;
         }

         int indent = line.Length - line.TrimStart( ' ' ).Length;
         string body = line.Trim();
         int colon = body.IndexOf( ':' );
         if( body.StartsWith( '-' ) || body.StartsWith( '\t' ) || line[indent..].StartsWith( '\t' ) || colon <= 0 )
         {
            skipped++;
            continue;
         }

         while( sections.Count > 0 && sections[^1].Indent >= indent )
         {
            sections.RemoveAt( sections.Count - 1 );
         }

         string key = body[..colon].Trim();
         string value = body[( colon + 1 )..].Trim().Trim( '"', '\'' );
         if( value.Length == 0 )
         {
            sections.Add( ( indent, key ) );
         }
         else
         {
            values[string.Join( '.', sections.Select( s => s.Key ).Append( key ) )] = value;
         }
      }

      return new ParsedConfig( values, skipped );
   }

   /// <summary>
   /// Reads ini text ("[section]" headers and "key = value" lines) into "section.key" entries,
   /// the layout of SQL Server's mssql.conf.
   /// </summary>
   /// <param name="text">The file's text.</param>
   /// <returns>The values and the count of lines that were not understood.</returns>
   public static ParsedConfig ParseIni( string text )
   {
      var values = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
      string section = string.Empty;
      int skipped = 0;
      foreach( string raw in text.Split( '\n' ) )
      {
         string line = raw.Trim();
         if( line.Length == 0 || line.StartsWith( '#' ) || line.StartsWith( ';' ) )
         {
            continue;
         }

         if( line.StartsWith( '[' ) && line.EndsWith( ']' ) )
         {
            section = line[1..^1].Trim();
            continue;
         }

         int equals = line.IndexOf( '=' );
         if( equals <= 0 )
         {
            skipped++;
            continue;
         }

         values[$"{section}.{line[..equals].Trim()}"] = line[( equals + 1 )..].Trim();
      }

      return new ParsedConfig( values, skipped );
   }

   /// <summary>
   /// Lists settings as "key = value" text in key order, for the report.
   /// </summary>
   /// <param name="config">The parsed config.</param>
   /// <param name="keys">Keys to list; when null every key is listed.</param>
   /// <returns>For example "storage.storage_path = /x, service.http_port = 6333", or "nothing".</returns>
   public static string List( ParsedConfig config, IEnumerable<string>? keys = null )
   {
      IEnumerable<string> chosen = keys ?? config.Values.Keys.OrderBy( k => k, StringComparer.Ordinal );
      string[] lines = chosen.Where( config.Values.ContainsKey ).Select( k => $"{k} = {config.Values[k]}" ).ToArray();
      return lines.Length == 0 ? "nothing" : string.Join( ", ", lines );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Cuts a "#" comment from the end of a line, when the "#" starts the line or follows a space
   /// and is not inside a quoted value (so a "#" in a URL fragment or a quoted key is kept). A
   /// quote opens only at the start of a value, after a space or a colon, so an apostrophe in
   /// plain text does not hide the rest of the line.
   /// </summary>
   /// <param name="line">The line.</param>
   /// <returns>The line without its comment.</returns>
   private static string StripComment( string line )
   {
      var kept = new StringBuilder( line.Length );
      char quote = '\0';
      for( int i = 0; i < line.Length; i++ )
      {
         char c = line[i];
         if( quote != '\0' )
         {
            quote = c == quote ? '\0' : quote;
         }
         else if( c is '"' or '\'' && ( i == 0 || line[i - 1] is ' ' or ':' ) )
         {
            quote = c;
         }
         else if( c == '#' && ( i == 0 || line[i - 1] == ' ' ) )
         {
            break;
         }

         kept.Append( c );
      }

      return kept.ToString();
   }

   #endregion Private Methods
}

/// <summary>
/// A config file read into flat entries.
/// </summary>
/// <param name="Values">Entries by dotted key ("section.key").</param>
/// <param name="SkippedLines">Lines the reader did not understand; above zero means the reading may be incomplete.</param>
public sealed record ParsedConfig( IReadOnlyDictionary<string, string> Values, int SkippedLines );
