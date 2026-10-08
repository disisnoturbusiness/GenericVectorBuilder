using System.Text.Json;
using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// Loads everything the consolidate command reads: the session runs, the other runs of the pipeline
/// in the folders holding them, the exclusions, the fact sheet and the repository root.
/// Why the other runs are found by listing the session folders' parents: the basis must name every
/// run of the pipeline it did not use and why (design section 1), so a run cannot drop out of the
/// basis by being left off the command line.
/// Every default fails loud when its file is missing; nothing falls back to another location.
/// </summary>
public static class ConsolidateLoader
{
   #region Data Members

   /// <summary>Largest exclusions file read.</summary>
   public const long MAX_EXCLUSIONS_BYTES = 1024 * 1024;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Loads the input.
   /// </summary>
   /// <param name="args">Parsed arguments.</param>
   /// <param name="commandLine">The command line text.</param>
   /// <param name="log">Progress output.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The input.</returns>
   /// <exception cref="ConsolidateRefusal">A session folder cannot be read, or a file the command needs is missing or malformed.</exception>
   public static ConsolidateInput Load( ConsolidateArgs args, string commandLine, Action<string> log, CancellationToken ct )
   {
      string root = Path.GetFullPath( args.ResultsDir ?? Environment.CurrentDirectory );
      List<(string Name, List<RunResult> Runs)> sessions = args.Sessions.Select( s => ( s.Name, s.Folders.Select( f => LoadRun( root, f, s.Name, log, ct ) ).ToList() ) ).ToList();
      List<(string Name, List<RunResult> Runs)> basis = args.BasisSessions.Select( s => ( s.Name, s.Folders.Select( f => LoadRun( root, f, s.Name, log, ct ) ).ToList() ) ).ToList();
      string pipeline = sessions[0].Runs[0].Pipeline ?? throw new ConsolidateRefusal( $"{sessions[0].Runs[0].Name} records no pipeline" );
      ( List<RunResult> others, List<LeftOutRun> skipped ) = Others( sessions.Concat( basis ).SelectMany( s => s.Runs ).ToList(), pipeline, ct );
      string repo = Repo( args.Repo );
      string factsPath = Existing( args.Facts ?? Path.Combine( AppContext.BaseDirectory, ConsolidateCommand.FACTS_FILE ), "--facts" );
      string exclusionsPath = Existing( args.Exclusions ?? Path.Combine( AppContext.BaseDirectory, ConsolidateCommand.EXCLUSIONS_FILE ), "--exclusions" );
      string classesPath = Existing( Path.Combine( repo, RecordedTextClasses.FILE ), "the recorded-text classes" );
      string notesPath = Existing( Path.Combine( repo, RunPageNoteList.FILE ), "the run-page notes list" );
      log( $"Repository {repo}; facts {factsPath}; exclusions {exclusionsPath}; classes {classesPath}; run-page notes {notesPath}; {others.Count} other run(s) of {pipeline} beside the sessions" );
      return new ConsolidateInput
      {
         Sessions = sessions,
         BasisSessions = basis,
         OtherRuns = others,
         Unreadable = skipped,
         Targets = args.Targets,
         Exclusions = Exclusions( exclusionsPath ),
         Facts = EngineFactSheet.Load( factsPath ),
         FactsSha256 = EngineFactSheet.FileSha256( factsPath ),
         ExclusionsSha256 = EngineFactSheet.FileSha256( exclusionsPath ),
         RepoRoot = repo,
         ResultsRoot = root,
         ObserverPaths = args.Observers.Select( o => Existing( Path.GetFullPath( o ), "--observer" ) ).ToList(),
         Classes = RecordedTextClasses.Load( classesPath ),
         ClassesSha256 = EngineFactSheet.FileSha256( classesPath ),
         RunPageNotes = RunPageNoteList.Load( notesPath ),
         Build = ConsolidateBuild.Of( typeof( ConsolidateLoader ).Assembly ),
         CommandLine = commandLine,
         CreatedUtc = DateTime.UtcNow.ToString( "yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture ),
      };
   }

   /// <summary>
   /// Reads basis-exclusions.json.
   /// </summary>
   /// <param name="path">The file.</param>
   /// <returns>The rows.</returns>
   /// <exception cref="ConsolidateRefusal">The file is too large, not a JSON array, or a row lacks target, seeds, metric, kind, why or source.</exception>
   public static List<ExclusionRow> Exclusions( string path )
   {
      if( new FileInfo( path ).Length > MAX_EXCLUSIONS_BYTES )
      {
         throw new ConsolidateRefusal( $"{path} is over {MAX_EXCLUSIONS_BYTES} bytes" );
      }

      using JsonDocument doc = JsonDocument.Parse( File.ReadAllText( path ) );
      if( doc.RootElement.ValueKind != JsonValueKind.Array )
      {
         throw new ConsolidateRefusal( $"{path} must hold a JSON array of exclusion rows" );
      }

      var rows = new List<ExclusionRow>();
      int index = 0;
      foreach( JsonElement e in doc.RootElement.EnumerateArray() )
      {
         string Need( string name ) => ResultJson.Text( e, name ) ?? throw new ConsolidateRefusal( $"{path} row {index} has no text field '{name}'" );
         rows.Add( new ExclusionRow
         {
            Target = Need( "target" ),
            Seeds = ( ResultJson.Ints( e, "seeds" ) ?? throw new ConsolidateRefusal( $"{path} row {index} has no seeds list" ) ).ToList(),
            Metric = Need( "metric" ),
            Kind = Need( "kind" ),
            Why = Need( "why" ),
            Source = Need( "source" ),
            Item = ResultJson.Int( e, "item" ),
            Evidence = ( ResultJson.Texts( e, "evidence" ) ?? Array.Empty<string>() ).ToList(),
            SeedEvidence = ResultJson.Text( e, "seedEvidence" ) ?? string.Empty,
         } );
         index++;
      }

      return rows;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Loads one session run.
   /// </summary>
   /// <param name="root">Results root.</param>
   /// <param name="folder">Folder as given.</param>
   /// <param name="session">Session name, for messages.</param>
   /// <param name="log">Progress output.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The run.</returns>
   /// <exception cref="ConsolidateRefusal">The folder or its results.json cannot be read.</exception>
   private static RunResult LoadRun( string root, string folder, string session, Action<string> log, CancellationToken ct )
   {
      ct.ThrowIfCancellationRequested();
      string full = Path.GetFullPath( Path.Combine( root, folder ) );
      try
      {
         RunResult run = RunResult.Load( full );
         log( $"Read {run.Name} (session {session}): {run.Targets.Count} targets, started {run.StartedUtc ?? "not recorded"}" );
         return run;
      }
      catch( Exception ex ) when( ex is InvalidDataException or IOException or UnauthorizedAccessException )
      {
         throw new ConsolidateRefusal( $"session {session} folder {full}: {ex.Message}" );
      }
   }

   /// <summary>
   /// The other runs of the pipeline in the folders holding the session runs, and the folders skipped with their reason.
   /// </summary>
   /// <param name="used">Every session run.</param>
   /// <param name="pipeline">The pipeline.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The runs and the skipped folders.</returns>
   private static (List<RunResult> Runs, List<LeftOutRun> Skipped) Others( List<RunResult> used, string pipeline, CancellationToken ct )
   {
      var names = new HashSet<string>( used.Select( r => r.Folder ), StringComparer.Ordinal );
      var runs = new List<RunResult>();
      var skipped = new List<LeftOutRun>();
      foreach( string parent in used.Select( r => Path.GetDirectoryName( r.Folder )! ).Distinct( StringComparer.Ordinal ) )
      {
         foreach( string folder in Directory.GetDirectories( parent ).OrderBy( d => d, StringComparer.Ordinal ) )
         {
            ct.ThrowIfCancellationRequested();
            if( names.Contains( folder ) || !File.Exists( Path.Combine( folder, "results.json" ) ) )
            {
               continue;
            }

            Other( folder, pipeline, runs, skipped );
         }
      }

      return ( runs, skipped );
   }

   /// <summary>
   /// Reads one other folder: a run of the pipeline joins the list, a settings-only or unreadable one is skipped with its reason, another pipeline is ignored.
   /// </summary>
   /// <param name="folder">The folder.</param>
   /// <param name="pipeline">The pipeline.</param>
   /// <param name="runs">Receives a run.</param>
   /// <param name="skipped">Receives a skipped folder.</param>
   private static void Other( string folder, string pipeline, List<RunResult> runs, List<LeftOutRun> skipped )
   {
      try
      {
         RunResult run = RunResult.Load( folder );
         if( run.Pipeline != pipeline )
         {
            return;
         }

         if( run.SettingsOnly )
         {
            skipped.Add( new LeftOutRun { Folder = run.Name, Reason = "settings-only run" } );
            return;
         }

         runs.Add( run );
      }
      catch( Exception ex ) when( ex is InvalidDataException or IOException or UnauthorizedAccessException )
      {
         skipped.Add( new LeftOutRun { Folder = Path.GetFileName( folder ), Reason = $"unreadable: {ex.Message}" } );
      }
   }

   /// <summary>
   /// The repository root: the given folder, or the nearest folder above the binary holding the solution file.
   /// </summary>
   /// <param name="given">The --repo value, or null.</param>
   /// <returns>The full path.</returns>
   /// <exception cref="ConsolidateRefusal">The folder holds no solution file, or none is found above the binary.</exception>
   private static string Repo( string? given )
   {
      if( given != null )
      {
         string full = Path.GetFullPath( given );
         return File.Exists( Path.Combine( full, ConsolidateCommand.REPO_MARKER ) ) ? full : throw new ConsolidateRefusal( $"--repo {full} holds no {ConsolidateCommand.REPO_MARKER}" );
      }

      for( DirectoryInfo? dir = new( AppContext.BaseDirectory ); dir != null; dir = dir.Parent )
      {
         if( File.Exists( Path.Combine( dir.FullName, ConsolidateCommand.REPO_MARKER ) ) )
         {
            return dir.FullName;
         }
      }

      throw new ConsolidateRefusal( $"no {ConsolidateCommand.REPO_MARKER} above {AppContext.BaseDirectory}; give --repo" );
   }

   /// <summary>
   /// A file path that must exist.
   /// </summary>
   /// <param name="path">The path.</param>
   /// <param name="option">The option it came from, for the message.</param>
   /// <returns>The full path.</returns>
   /// <exception cref="ConsolidateRefusal">The file does not exist.</exception>
   private static string Existing( string path, string option )
   {
      string full = Path.GetFullPath( path );
      return File.Exists( full ) ? full : throw new ConsolidateRefusal( $"{option}: {full} does not exist" );
   }

   #endregion Private Methods
}
