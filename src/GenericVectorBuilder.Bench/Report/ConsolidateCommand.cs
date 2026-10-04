using System.IO.Enumeration;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GenericVectorBuilder.Bench.Stats;

namespace GenericVectorBuilder.Bench.Report;

/// <summary>
/// The consolidate command: reads several result folders, keeps only the runs in which every
/// listed target has a result, and writes consolidated.json and consolidated.md.
/// Why a command in the benchmark instead of a script beside it: the published numbers must
/// come from tested code with fixed rules (paired comparisons per run, dropped runs logged),
/// not from a one-off script that each writeup rewrites.
/// It touches no engine and makes no network call; it only reads files and writes two.
/// </summary>
public static class ConsolidateCommand
{
   #region Data Members

   /// <summary>The command word.</summary>
   public const string NAME = "consolidate";

   private static readonly JsonSerializerOptions JSON = new()
   {
      WriteIndented = true,
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
      DefaultIgnoreCondition = JsonIgnoreCondition.Never,
   };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Runs the command.
   /// </summary>
   /// <param name="args">Arguments after the command word.</param>
   /// <param name="log">Progress and error output.</param>
   /// <param name="ct">Cancellation (checked between folders and before writing).</param>
   /// <returns>0 when written, 1 when no run qualified or a file could not be read or written, 2 for a bad command line.</returns>
   public static int Run( IReadOnlyList<string> args, Action<string> log, CancellationToken ct )
   {
      ConsolidateArgs parsed;
      try
      {
         parsed = ConsolidateArgs.Parse( args );
      }
      catch( ArgumentException ex )
      {
         log( ex.Message );
         log( string.Empty );
         log( Usage() );
         return 2;
      }

      try
      {
         string folder = Execute( parsed, args, log, ct );
         log( $"Wrote {Path.Combine( folder, "consolidated.json" )} and consolidated.md" );
         return 0;
      }
      catch( ArgumentException ex )
      {
         log( ex.Message );
         return 2;
      }
      catch( Exception ex ) when( ex is InvalidOperationException or IOException or UnauthorizedAccessException )
      {
         log( $"Failed: {ex.Message}" );
         return 1;
      }
      catch( OperationCanceledException )
      {
         log( "Stopped; nothing was written." );
         return 1;
      }
   }

   /// <summary>
   /// The usage text.
   /// </summary>
   /// <returns>Usage text.</returns>
   public static string Usage()
   {
      return @"consolidate --targets a,b,c [--runs FOLDER|GLOB[,...]] [FOLDER|GLOB ...] [--out DIR] [--pairs a:b,c:d]

  Uses only runs in which every listed target has a result; every other run is logged with its reason.
  Runs measured under different build configuration, CPU governor, CPU partition, warm-up count, exact-mode seconds,
  seconds per level or search settings of a listed target are never mixed: the largest consistent group is used.
  GLOB may use * and ? in any path segment and {x,y} alternatives, e.g. 'bench-results/2026100{3,4}-*-eshoponweb'.
  --out defaults to a new consolidated-<UTC time> folder beside the first run used.
  --pairs a:b[,c:d] compares two targets run by run; there is no default pair, because two targets hold separate copies of the data.
  Load rows/s is reported but never ranked. Flags (spread, p50 against mean, unsettled engine, busy box, governor, shared cores) are in consolidated.md and .json.";
   }

   /// <summary>
   /// Expands one folder argument: {x,y} alternatives, then * and ? in any path segment.
   /// </summary>
   /// <param name="pattern">A folder path or pattern, absolute or relative to the current folder.</param>
   /// <returns>Matching folders, full paths, sorted.</returns>
   public static IReadOnlyList<string> ExpandFolders( string pattern )
   {
      var found = new SortedSet<string>( StringComparer.Ordinal );
      foreach( string expanded in ExpandBraces( pattern ) )
      {
         string full = Path.GetFullPath( expanded );
         if( full.IndexOfAny( new[] { '*', '?' } ) < 0 )
         {
            if( Directory.Exists( full ) )
            {
               found.Add( full.TrimEnd( Path.DirectorySeparatorChar ) );
            }

            continue;
         }

         foreach( string match in MatchSegments( full ) )
         {
            found.Add( match );
         }
      }

      return found.ToList();
   }

   /// <summary>
   /// Expands {x,y} alternatives (nested braces too), shell style.
   /// </summary>
   /// <param name="pattern">The pattern.</param>
   /// <returns>Every alternative, in order.</returns>
   public static IReadOnlyList<string> ExpandBraces( string pattern )
   {
      int open = pattern.IndexOf( '{' );
      int close = open < 0 ? -1 : MatchingBrace( pattern, open );
      if( open < 0 || close < 0 )
      {
         return new[] { pattern };
      }

      string head = pattern[..open];
      string tail = pattern[( close + 1 )..];
      return SplitTopLevel( pattern[( open + 1 )..close] )
         .SelectMany( alternative => ExpandBraces( head + alternative + tail ) )
         .ToList();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads the folders, consolidates and writes both files.
   /// </summary>
   /// <param name="parsed">Parsed arguments.</param>
   /// <param name="args">Raw arguments (for the command line in the report).</param>
   /// <param name="log">Progress output.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The output folder.</returns>
   private static string Execute( ConsolidateArgs parsed, IReadOnlyList<string> args, Action<string> log, CancellationToken ct )
   {
      var runs = new List<RunResult>();
      var unreadable = new List<RunDropped>();
      foreach( string folder in ResolveFolders( parsed.Patterns ) )
      {
         ct.ThrowIfCancellationRequested();
         try
         {
            RunResult run = RunResult.Load( folder );
            runs.Add( run );
            log( $"Read {run.Name}: {run.Targets.Count} targets, started {run.StartedUtc ?? "missing"}" );
         }
         catch( Exception ex ) when( ex is InvalidDataException or IOException or UnauthorizedAccessException )
         {
            unreadable.Add( new RunDropped { Name = Path.GetFileName( folder ), Folder = folder, Reason = ex.Message } );
         }
      }

      ConsolidatedReport report = Consolidator.Consolidate( runs, parsed.Targets, parsed.Pairs, unreadable );
      report.CreatedUtc = DateTime.UtcNow.ToString( "yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture );
      report.CommandLine = string.Join( " ", new[] { NAME }.Concat( args ).Select( a => a.Contains( ' ' ) ? $"\"{a}\"" : a ) );
      report.Dropped.ForEach( d => log( $"Dropped {d.Name}: {d.Reason}" ) );
      log( $"Used {report.Runs.Count} run(s): {string.Join( ", ", report.Runs.Select( r => r.Name ) )}" );
      log( $"{report.Flags.Count} flag(s) on {report.Flags.Select( f => f.Target ).Distinct().Count()} target(s)" );
      ct.ThrowIfCancellationRequested();
      string output = parsed.OutFolder != null ? Path.GetFullPath( parsed.OutFolder )
         : Path.Combine( Path.GetDirectoryName( report.Runs[0].Folder ) ?? ".", "consolidated-" + DateTime.UtcNow.ToString( "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture ) );
      Directory.CreateDirectory( output );
      WriteAtomically( Path.Combine( output, "consolidated.json" ), JsonSerializer.Serialize( report, JSON ) );
      WriteAtomically( Path.Combine( output, "consolidated.md" ), ConsolidatedMarkdown.Render( report ) );
      return output;
   }

   /// <summary>
   /// Expands every pattern; a pattern that matches no folder stops the command.
   /// Why stop instead of skipping: a typo in a folder name would otherwise quietly shrink the
   /// set of runs behind every median.
   /// </summary>
   /// <param name="patterns">Folder arguments.</param>
   /// <returns>Distinct folders, in argument order.</returns>
   /// <exception cref="ArgumentException">A pattern matched nothing.</exception>
   private static IReadOnlyList<string> ResolveFolders( IReadOnlyList<string> patterns )
   {
      var folders = new List<string>();
      foreach( string pattern in patterns )
      {
         IReadOnlyList<string> matched = ExpandFolders( pattern );
         if( matched.Count == 0 )
         {
            throw new ArgumentException( $"'{pattern}' matched no folder (current folder {Environment.CurrentDirectory})." );
         }

         folders.AddRange( matched.Where( m => !folders.Contains( m, StringComparer.Ordinal ) ) );
      }

      return folders;
   }

   /// <summary>
   /// Walks a full path whose segments may hold * or ?, keeping folders that exist.
   /// </summary>
   /// <param name="full">Full path with wildcards.</param>
   /// <returns>Matching folders.</returns>
   private static IEnumerable<string> MatchSegments( string full )
   {
      string root = Path.GetPathRoot( full ) ?? string.Empty;
      var current = new List<string> { root };
      foreach( string segment in full[root.Length..].Split( Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries ) )
      {
         bool wild = segment.IndexOfAny( new[] { '*', '?' } ) >= 0;
         current = current.SelectMany( dir => wild
               ? SafeDirectories( dir ).Where( d => FileSystemName.MatchesSimpleExpression( segment, Path.GetFileName( d ), ignoreCase: false ) )
               : new[] { Path.Combine( dir, segment ) }.Where( Directory.Exists ) )
            .ToList();
      }

      return current;
   }

   /// <summary>
   /// Sub-folders of a folder, or none when it cannot be listed.
   /// </summary>
   /// <param name="dir">Folder.</param>
   /// <returns>Sub-folder paths.</returns>
   private static IEnumerable<string> SafeDirectories( string dir )
   {
      try
      {
         return Directory.GetDirectories( dir );
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
      {
         return Array.Empty<string>();
      }
   }

   /// <summary>
   /// Index of the brace closing the one at <paramref name="open"/>, or -1.
   /// </summary>
   /// <param name="text">Text.</param>
   /// <param name="open">Index of an opening brace.</param>
   /// <returns>Index of its closing brace, or -1.</returns>
   private static int MatchingBrace( string text, int open )
   {
      int depth = 0;
      for( int i = open; i < text.Length; i++ )
      {
         depth += text[i] == '{' ? 1 : text[i] == '}' ? -1 : 0;
         if( depth == 0 )
         {
            return i;
         }
      }

      return -1;
   }

   /// <summary>
   /// Splits on commas that are not inside braces, so "a{1,2},b" gives "a{1,2}" and "b".
   /// </summary>
   /// <param name="text">Text.</param>
   /// <returns>The parts (empty parts kept, as the shell does inside braces).</returns>
   internal static List<string> SplitTopLevel( string text )
   {
      var parts = new List<string>();
      var part = new StringBuilder();
      int depth = 0;
      foreach( char c in text )
      {
         depth += c == '{' ? 1 : c == '}' ? -1 : 0;
         if( c == ',' && depth == 0 )
         {
            parts.Add( part.ToString() );
            part.Clear();
         }
         else
         {
            part.Append( c );
         }
      }

      parts.Add( part.ToString() );
      return parts;
   }

   /// <summary>
   /// Writes a file through a temporary name and a rename, so a stopped run never leaves half a file.
   /// </summary>
   /// <param name="path">Destination.</param>
   /// <param name="text">Content.</param>
   private static void WriteAtomically( string path, string text )
   {
      string temp = path + ".tmp";
      File.WriteAllText( temp, text );
      File.Move( temp, path, overwrite: true );
   }

   #endregion Private Methods
}

/// <summary>
/// The consolidate command line, parsed and checked.
/// Why separate from BenchOptions: consolidate needs no pipeline and takes folders, while every
/// other command needs a pipeline; one parser for both would weaken the checks of each.
/// </summary>
public sealed class ConsolidateArgs
{
   #region Public Methods

   /// <summary>Targets every used run must have, in report order.</summary>
   public IReadOnlyList<string> Targets { get; private set; } = Array.Empty<string>();

   /// <summary>Folder paths or patterns.</summary>
   public List<string> Patterns { get; } = new();

   /// <summary>Output folder, or null for the default.</summary>
   public string? OutFolder { get; private set; }

   /// <summary>Pairs to compare run by run (none unless --pairs is given).</summary>
   public IReadOnlyList<(string A, string B)> Pairs { get; private set; } = Consolidator.DEFAULT_PAIRS;

   /// <summary>
   /// Parses the arguments after the command word.
   /// </summary>
   /// <param name="args">Arguments.</param>
   /// <returns>The parsed arguments.</returns>
   /// <exception cref="ArgumentException">Something is missing or malformed; the message says what.</exception>
   public static ConsolidateArgs Parse( IReadOnlyList<string> args )
   {
      var parsed = new ConsolidateArgs();
      for( int i = 0; i < args.Count; i++ )
      {
         if( !args[i].StartsWith( "--", StringComparison.Ordinal ) )
         {
            parsed.Patterns.Add( args[i] );
            continue;
         }

         string name = args[i][2..];
         string value = i + 1 < args.Count ? args[++i] : throw new ArgumentException( $"--{name} needs a value." );
         switch( name )
         {
            case "targets": parsed.Targets = Split( value, ',' ); break;
            case "runs": parsed.Patterns.AddRange( ConsolidateCommand.SplitTopLevel( value ).Where( p => p.Trim().Length > 0 ).Select( p => p.Trim() ) ); break;
            case "out": parsed.OutFolder = value; break;
            case "pairs": parsed.Pairs = Split( value, ',' ).Select( ParsePair ).ToList(); break;
            default: throw new ArgumentException( $"Unknown option --{name} for consolidate." );
         }
      }

      if( parsed.Targets.Count == 0 )
      {
         throw new ArgumentException( "--targets is required, e.g. --targets sql,sql-diskann,qdrant." );
      }

      if( parsed.Patterns.Count == 0 )
      {
         throw new ArgumentException( "Give at least one result folder or pattern (--runs or a bare argument)." );
      }

      return parsed;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Splits and trims, dropping empty parts.
   /// </summary>
   /// <param name="value">Text.</param>
   /// <param name="separator">Separator.</param>
   /// <returns>The parts.</returns>
   private static string[] Split( string value, char separator )
   {
      return value.Split( separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
   }

   /// <summary>
   /// Parses "a:b".
   /// </summary>
   /// <param name="text">The pair text.</param>
   /// <returns>The pair.</returns>
   private static (string A, string B) ParsePair( string text )
   {
      string[] parts = Split( text, ':' );
      return parts.Length == 2 && parts[0] != parts[1] ? ( parts[0], parts[1] )
         : throw new ArgumentException( $"--pairs takes a:b items with two different targets, not '{text}'." );
   }

   #endregion Private Methods
}
