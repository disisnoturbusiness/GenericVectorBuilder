using System.Text.Json;
using GenericVectorBuilder.Bench.Stats;

namespace GenericVectorBuilder.Bench.Report;

/// <summary>
/// The consolidate command of the v8 "big gaps only" report: reads the claim sessions (one or two,
/// exactly 3 runs each), the basis sessions and the other runs of the pipeline beside them, the fact
/// sheet and the exclusions, and writes consolidated.json, consolidated.md and, when an observer
/// summary is given, a copy of it at observer/summary.json, into a new folder.
/// It refuses (exit 1, nothing written) when the runs are not one experiment or a fact or a sentence
/// does not hold; a guard that stops the report (G2, G3) writes the report with status "stopped" and
/// exits 3. It touches no engine and makes no network call; it reads files and writes one folder.
/// Why every input is explicit and every default fails loud: the frozen build runs from a bin folder
/// where a wrong default would quietly read another repository's fact sheet.
/// </summary>
public static class ConsolidateCommand
{
   #region Data Members

   /// <summary>The command word.</summary>
   public const string NAME = "consolidate";

   /// <summary>Exit code: the report was written and no guard stopped it.</summary>
   public const int EXIT_WRITTEN = 0;

   /// <summary>Exit code: refused or failed; nothing was written.</summary>
   public const int EXIT_REFUSED = 1;

   /// <summary>Exit code: bad command line.</summary>
   public const int EXIT_USAGE = 2;

   /// <summary>Exit code: a guard stopped the report; it was written with status "stopped".</summary>
   public const int EXIT_STOPPED = 3;

   /// <summary>The fact sheet's file name beside the binary.</summary>
   public const string FACTS_FILE = "engine-facts.json";

   /// <summary>The exclusions' file name beside the binary.</summary>
   public const string EXCLUSIONS_FILE = "basis-exclusions.json";

   /// <summary>The file that marks the repository root.</summary>
   public const string REPO_MARKER = "GenericVectorBuilder.slnx";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Runs the command.
   /// </summary>
   /// <param name="args">Arguments after the command word.</param>
   /// <param name="log">Progress and error output.</param>
   /// <param name="ct">Cancellation (checked between folders and before writing).</param>
   /// <returns><see cref="EXIT_WRITTEN"/>, <see cref="EXIT_REFUSED"/>, <see cref="EXIT_USAGE"/> or <see cref="EXIT_STOPPED"/>.</returns>
   public static int Run( IReadOnlyList<string> args, Action<string> log, CancellationToken ct )
   {
      if( args.Any( a => a is "--help" or "-h" ) )
      {
         log( Usage() );
         return EXIT_WRITTEN;
      }

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
         return EXIT_USAGE;
      }

      try
      {
         ConsolidatedReport report = Execute( parsed, args, log, ct );
         return report.Status == Consolidator.STOPPED ? EXIT_STOPPED : EXIT_WRITTEN;
      }
      catch( ArgumentException ex )
      {
         log( ex.Message );
         return EXIT_USAGE;
      }
      catch( Exception ex ) when( ex is ConsolidateRefusal or InvalidDataException or IOException or UnauthorizedAccessException or EngineFactsException or JsonException or FormatException )
      {
         log( $"Refused, nothing was written: {ex.Message}" );
         return EXIT_REFUSED;
      }
      catch( OperationCanceledException )
      {
         log( "Stopped; nothing was written." );
         return EXIT_REFUSED;
      }
   }

   /// <summary>
   /// The usage text.
   /// </summary>
   /// <returns>Usage text.</returns>
   public static string Usage()
   {
      return @"consolidate --session NAME=F1,F2,F3 [--session NAME=F1,F2,F3] [--basis-session NAME=F1,F2,F3 ...] --out DIR
            [--results-dir DIR] [--targets a,b,c] [--facts PATH] [--exclusions PATH] [--repo DIR] [--observer PATH]

  --session          a claim session: exactly 3 run folders. One session gives a one-session report (rows not ranked);
                     two give the ranked report, the earlier session first. The claim rule is applied in each session separately.
  --basis-session    a session that feeds the threshold basis alone (v5, v6). Repeat it, or give several in one value:
                     'v5=A,B,C,v6=D,E,F' (an item holding '=' starts the next session).
  --out              the folder to create; it must not exist or must be empty, and its name must not start with published- or withdrawn-.
  --results-dir      relative run folders resolve against it (default: the current folder); run references in the report are relative to it.
  --targets          targets to report (default: every target of the claim runs); every other target is listed with its reason.
  --facts            engine-facts.json (default: the copy beside this binary). Missing: refused.
  --exclusions       basis-exclusions.json (default: the copy beside this binary). Missing: refused.
  --repo             the repository the facts' file and doc sources resolve in (default: the nearest folder above this binary
                     holding GenericVectorBuilder.slnx). Missing: refused.
  --observer         the observer's summary.json of the newest session; copied to observer/summary.json in --out and cited.
  A blocked report of a session, a folder named blocked-DATE-NAME beside the runs, is named in that session's reuse note.
  The other runs of the pipeline in the folders holding the session runs are read too: those without machine control give the
  disclosed no-machine-control maxima, and every one is listed with the reason it is not in the basis.
  Refused, nothing written (exit 1): a claim session without exactly 3 runs, runs of different experiments (run-level fields,
  method, build configuration, machine control, host, governor, partition, clock, CPU, RAM), a target missing or failed in a claim run,
  a basis run without machine control, an exclusion row matching no cell, a fact or a sentence that does not hold.
  Guard G2 (an order of the first session whose medians reverse in the second) or G3 (more than 3 targets changed setup between the
  sessions) writes the report with status stopped and no headline (exit 3). Bad command line: exit 2.";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Loads the input, builds the report and writes the folder.
   /// </summary>
   /// <param name="parsed">Parsed arguments.</param>
   /// <param name="args">Raw arguments (for the command line in the report).</param>
   /// <param name="log">Progress output.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The report written.</returns>
   private static ConsolidatedReport Execute( ConsolidateArgs parsed, IReadOnlyList<string> args, Action<string> log, CancellationToken ct )
   {
      string output = CheckOut( parsed.OutFolder );
      ConsolidateInput input = ConsolidateLoader.Load( parsed, CommandLine( args ), log, ct );
      ConsolidatedReport report = Consolidator.Build( input );
      ct.ThrowIfCancellationRequested();
      Write( output, report, input.ObserverPath );
      log( $"Wrote {Path.Combine( output, "consolidated.json" )} and consolidated.md: {report.Mode}, status {report.Status}, threshold {report.Threshold.TBp} bp, {report.Audit.SentencesChecked} sentences audited ({report.Audit.RowSentencesChecked} of them on table rows), {report.Audit.FactsChecked} facts checked" );
      report.StopReasons.ForEach( r => log( "STOPPED " + r ) );
      return report;
   }

   /// <summary>
   /// Checks the output folder: a full path, absent or empty, not a published or withdrawn name.
   /// </summary>
   /// <param name="folder">The --out value.</param>
   /// <returns>The full path.</returns>
   /// <exception cref="ArgumentException">The folder is not acceptable.</exception>
   private static string CheckOut( string folder )
   {
      string full = Path.GetFullPath( folder ).TrimEnd( Path.DirectorySeparatorChar );
      string name = Path.GetFileName( full );
      if( name.StartsWith( "published-", StringComparison.Ordinal ) || name.StartsWith( "withdrawn-", StringComparison.Ordinal ) )
      {
         throw new ArgumentException( $"--out {full}: a published- or withdrawn- folder is made by renaming a checked candidate, never written by consolidate" );
      }

      if( Directory.Exists( full ) && Directory.EnumerateFileSystemEntries( full ).Any() )
      {
         throw new ArgumentException( $"--out {full} exists and is not empty" );
      }

      return full;
   }

   /// <summary>
   /// Writes the report into a temporary sibling folder, then moves it into place, so a failure never leaves half a report.
   /// </summary>
   /// <param name="output">The output folder.</param>
   /// <param name="report">The report.</param>
   /// <param name="observer">The observer summary to copy, or null.</param>
   private static void Write( string output, ConsolidatedReport report, string? observer )
   {
      string temp = output + ".tmp-" + Guid.NewGuid().ToString( "N" );
      Directory.CreateDirectory( temp );
      try
      {
         File.WriteAllText( Path.Combine( temp, "consolidated.json" ), JsonSerializer.Serialize( report, ConsolidateAudit.JSON ) );
         File.WriteAllText( Path.Combine( temp, "consolidated.md" ), ConsolidatedMarkdown.Render( report ) );
         if( observer != null )
         {
            Directory.CreateDirectory( Path.Combine( temp, "observer" ) );
            File.Copy( observer, Path.Combine( temp, "observer", "summary.json" ) );
         }

         if( Directory.Exists( output ) )
         {
            Directory.Delete( output );
         }

         Directory.Move( temp, output );
      }
      catch
      {
         if( Directory.Exists( temp ) )
         {
            Directory.Delete( temp, recursive: true );
         }

         throw;
      }
   }

   /// <summary>
   /// The command line as one text, arguments with spaces quoted.
   /// </summary>
   /// <param name="args">Arguments.</param>
   /// <returns>The text.</returns>
   private static string CommandLine( IReadOnlyList<string> args )
   {
      return string.Join( " ", new[] { NAME }.Concat( args ).Select( a => a.Contains( ' ' ) ? $"\"{a}\"" : a ) );
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

   /// <summary>Claim sessions in the order given.</summary>
   public List<(string Name, List<string> Folders)> Sessions { get; } = new();

   /// <summary>Basis sessions in the order given.</summary>
   public List<(string Name, List<string> Folders)> BasisSessions { get; } = new();

   /// <summary>Targets to report, or empty for every target.</summary>
   public List<string> Targets { get; private set; } = new();

   /// <summary>Output folder.</summary>
   public string OutFolder { get; private set; } = string.Empty;

   /// <summary>Folder relative run folders resolve against, or null for the current folder.</summary>
   public string? ResultsDir { get; private set; }

   /// <summary>Fact sheet path, or null for the default.</summary>
   public string? Facts { get; private set; }

   /// <summary>Exclusions path, or null for the default.</summary>
   public string? Exclusions { get; private set; }

   /// <summary>Repository root, or null for the default.</summary>
   public string? Repo { get; private set; }

   /// <summary>Observer summary path, or null.</summary>
   public string? Observer { get; private set; }

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
            throw new ArgumentException( $"'{args[i]}' is not an option; give run folders with --session or --basis-session." );
         }

         string name = args[i][2..];
         string value = i + 1 < args.Count ? args[++i] : throw new ArgumentException( $"--{name} needs a value." );
         parsed.Set( name, value );
      }

      if( parsed.Sessions.Count == 0 )
      {
         throw new ArgumentException( "Give at least one --session NAME=F1,F2,F3." );
      }

      if( parsed.OutFolder.Length == 0 )
      {
         throw new ArgumentException( "--out is required." );
      }

      return parsed;
   }

   /// <summary>
   /// Splits a session value: "NAME=F1,F2,F3", or several sessions in one value where an item holding '=' starts the next.
   /// </summary>
   /// <param name="value">The value.</param>
   /// <returns>The sessions.</returns>
   /// <exception cref="ArgumentException">The value does not start with NAME=, or a session has no folder.</exception>
   public static List<(string Name, List<string> Folders)> SplitSessions( string value )
   {
      var sessions = new List<(string Name, List<string> Folders)>();
      foreach( string item in value.Split( ',', StringSplitOptions.TrimEntries ) )
      {
         int eq = item.IndexOf( '=' );
         if( eq > 0 )
         {
            sessions.Add( ( item[..eq].Trim(), new List<string>() ) );
            AddFolder( sessions, item[( eq + 1 )..], value );
         }
         else if( sessions.Count == 0 )
         {
            throw new ArgumentException( $"'{value}' must start with NAME=, e.g. v7=20261006-130619-eshoponweb,..." );
         }
         else
         {
            AddFolder( sessions, item, value );
         }
      }

      if( sessions.Any( s => s.Folders.Count == 0 || s.Name.Length == 0 ) )
      {
         throw new ArgumentException( $"'{value}' holds a session with no name or no folder" );
      }

      return sessions;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Sets one option.
   /// </summary>
   /// <param name="name">Option name without dashes.</param>
   /// <param name="value">Its value.</param>
   /// <exception cref="ArgumentException">Unknown option or a repeated single option.</exception>
   private void Set( string name, string value )
   {
      switch( name )
      {
         case "session": Sessions.AddRange( SplitSessions( value ) ); break;
         case "basis-session": BasisSessions.AddRange( SplitSessions( value ) ); break;
         case "targets": Targets = value.Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ).ToList(); break;
         case "out": OutFolder = Once( OutFolder.Length == 0 ? null : OutFolder, value, name ); break;
         case "results-dir": ResultsDir = Once( ResultsDir, value, name ); break;
         case "facts": Facts = Once( Facts, value, name ); break;
         case "exclusions": Exclusions = Once( Exclusions, value, name ); break;
         case "repo": Repo = Once( Repo, value, name ); break;
         case "observer": Observer = Once( Observer, value, name ); break;
         default: throw new ArgumentException( $"Unknown option --{name} for consolidate." );
      }
   }

   /// <summary>
   /// A single-valued option's value, refusing a second one.
   /// </summary>
   /// <param name="current">The value so far.</param>
   /// <param name="value">The new value.</param>
   /// <param name="name">Option name.</param>
   /// <returns>The value.</returns>
   /// <exception cref="ArgumentException">Given twice.</exception>
   private static string Once( string? current, string value, string name )
   {
      return current == null ? value : throw new ArgumentException( $"--{name} is given twice." );
   }

   /// <summary>
   /// Adds a folder to the last session.
   /// </summary>
   /// <param name="sessions">Sessions so far.</param>
   /// <param name="folder">The folder text.</param>
   /// <param name="value">The whole value, for the message.</param>
   /// <exception cref="ArgumentException">An empty folder.</exception>
   private static void AddFolder( List<(string Name, List<string> Folders)> sessions, string folder, string value )
   {
      if( folder.Trim().Length == 0 )
      {
         throw new ArgumentException( $"'{value}' holds an empty folder name" );
      }

      sessions[^1].Folders.Add( folder.Trim() );
   }

   #endregion Private Methods
}
