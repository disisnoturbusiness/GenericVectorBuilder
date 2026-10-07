using System.Globalization;
using System.Text.Json;
using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// Reads the observer's summary.json (written by the run tooling's observer-summary.py, schema
/// "v8-observer-summary-1") for the disclosures: the observer's own CPU, its APERF/MPERF clock
/// check, the MSR 0x620 values it saw, and the systemd timers that fired during timed passes.
/// Why read here and not cited as a file: the summary lives outside the repository, so a sentence
/// cannot cite it as a repository file; the consolidation copies it beside consolidated.json and the
/// sentences cite the values copied into consolidated.json, which a lens re-reads from the summary.
/// Why it fails loud: a summary that does not cover a run of the newest session, or has another
/// schema, would let a disclosure speak for runs the observer never saw.
/// </summary>
public static class ConsolidateObserver
{
   #region Data Members

   /// <summary>The schema this reader knows.</summary>
   public const string SCHEMA = "v8-observer-summary-1";

   /// <summary>Largest summary read.</summary>
   public const long MAX_BYTES = 64L * 1024 * 1024;

   /// <summary>Where the newest session's summary is copied beside the report; an older session's goes to observer/summary-NAME.json.</summary>
   public const string NEWEST_COPY = "observer/summary.json";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Reads the summaries given: one must cover every run of the newest claim session, and one may cover every run of an older session (the observer that ran
   /// beside v7 shows its clock and its outside load too).
   /// </summary>
   /// <param name="paths">The summary.json files, or none.</param>
   /// <param name="sessions">The claim sessions, oldest first.</param>
   /// <returns>The block of the newest session (null when no path was given) and the blocks of the older sessions a summary covers.</returns>
   /// <exception cref="ConsolidateRefusal">A file is missing, too large, not JSON or of another schema; the newest session is not fully covered by one file; or an older session is covered in part.</exception>
   public static (ObserverInfo? Newest, List<ObserverInfo> Others) Read( IReadOnlyList<string> paths, IReadOnlyList<ClaimSession> sessions )
   {
      if( paths.Count == 0 )
      {
         return ( null, new List<ObserverInfo>() );
      }

      var docs = new List<JsonDocument>();
      try
      {
         foreach( string path in paths )
         {
            docs.Add( Load( path ) );
            string? schema = ResultJson.Text( docs[^1].RootElement, "schema" );
            if( schema != SCHEMA )
            {
               throw new ConsolidateRefusal( $"{path} has schema '{schema ?? ConsolidateIdentity.NOT_RECORDED}', not '{SCHEMA}'" );
            }
         }

         ObserverInfo newest = ReadSession( paths, docs, sessions[^1], required: true )!;
         List<ObserverInfo> others = sessions.Take( sessions.Count - 1 ).Select( s => ReadSession( paths, docs, s, required: false ) ).OfType<ObserverInfo>().ToList();
         string? unused = paths.FirstOrDefault( p => newest.Path != p && others.All( o => o.Path != p ) );
         return unused == null ? ( newest, others ) : throw new ConsolidateRefusal( $"{unused} covers no run of any claim session" );
      }
      finally
      {
         docs.ForEach( d => d.Dispose() );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Parses the file with a size limit.
   /// </summary>
   /// <param name="path">The file.</param>
   /// <returns>The document; the caller disposes it.</returns>
   /// <exception cref="ConsolidateRefusal">Missing, too large or not JSON.</exception>
   private static JsonDocument Load( string path )
   {
      var file = new FileInfo( path );
      if( !file.Exists )
      {
         throw new ConsolidateRefusal( $"the observer summary {path} does not exist" );
      }

      if( file.Length > MAX_BYTES )
      {
         throw new ConsolidateRefusal( $"the observer summary {path} is {file.Length} bytes, over {MAX_BYTES}" );
      }

      try
      {
         return JsonDocument.Parse( File.ReadAllText( path ) );
      }
      catch( JsonException ex )
      {
         throw new ConsolidateRefusal( $"the observer summary {path} is not valid JSON: {ex.Message}" );
      }
   }

   /// <summary>
   /// The summary's entry for one run folder.
   /// </summary>
   /// <param name="root">The summary root.</param>
   /// <param name="folder">Run folder name.</param>
   /// <returns>The entry, or null.</returns>
   private static JsonElement? RunEntry( JsonElement root, string folder )
   {
      if( ResultJson.Child( root, "runs" ) is not { ValueKind: JsonValueKind.Array } runs )
      {
         return null;
      }

      foreach( JsonElement run in runs.EnumerateArray() )
      {
         if( run.ValueKind == JsonValueKind.Object && ResultJson.Text( run, "folder" ) == folder )
         {
            return run;
         }
      }

      return null;
   }

   /// <summary>
   /// Reads one run's checks.
   /// </summary>
   /// <param name="folder">Run folder name.</param>
   /// <param name="entry">The run's entry.</param>
   /// <returns>The run's figures; a figure the summary lacks is null.</returns>
   private static ObserverRun ReadRun( string folder, JsonElement entry )
   {
      JsonElement? checks = ResultJson.Child( entry, "checks" ) is { ValueKind: JsonValueKind.Object } c ? c : null;
      JsonElement? Check( string name ) => checks is JsonElement holder && ResultJson.Child( holder, name ) is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } found ? found : null;
      var run = new ObserverRun { Folder = folder };
      if( Check( "observerCpu" ) is { ValueKind: JsonValueKind.Array } cpu )
      {
         run.ObserverCpuMax = cpu.EnumerateArray().Where( v => v.ValueKind == JsonValueKind.Number ).Select( v => v.GetDouble() ).DefaultIfEmpty( double.NaN ).Max() is double max && double.IsFinite( max ) ? max : null;
      }

      run.AperfWorstDeviationBp = Check( "aperf" ) is JsonElement aperf ? ResultJson.Number( aperf, "worstDeviationBp" ) : null;
      run.Msr620ValuesSeen = Check( "msr620" ) is JsonElement msr ? ( ResultJson.Texts( msr, "valuesSeen" ) ?? Array.Empty<string>() ).Select( Hex ).ToList() : new List<string>();
      if( Check( "timers" ) is JsonElement timers )
      {
         run.TimersCovered = ResultJson.Bool( timers, "covered" );
         run.PassesWithTimerFired = ResultJson.Child( timers, "passesWithTimerFired" ) is { ValueKind: JsonValueKind.Array } fired ? fired.GetArrayLength() : null;
      }

      return run;
   }

   /// <summary>
   /// An MSR value as "0x" plus lower-case hex, whether the summary wrote it with the prefix or not.
   /// </summary>
   /// <param name="text">The value.</param>
   /// <returns>The normalised value, or the text unchanged when it is not hex.</returns>
   private static string Hex( string text )
   {
      string digits = text.StartsWith( "0x", StringComparison.OrdinalIgnoreCase ) ? text[2..] : text;
      return long.TryParse( digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value ) ? "0x" + value.ToString( "x", CultureInfo.InvariantCulture ) : text;
   }

   /// <summary>
   /// The block of one session from the one file that covers its runs.
   /// </summary>
   /// <param name="paths">The files.</param>
   /// <param name="docs">The files, parsed.</param>
   /// <param name="session">The claim session.</param>
   /// <param name="required">True when the session must be covered.</param>
   /// <returns>The block, or null when the session is not required and no file covers any of its runs.</returns>
   /// <exception cref="ConsolidateRefusal">The session is required and not covered, covered by more than one file, or covered in part.</exception>
   private static ObserverInfo? ReadSession( IReadOnlyList<string> paths, IReadOnlyList<JsonDocument> docs, ClaimSession session, bool required )
   {
      List<int> holders = Enumerable.Range( 0, docs.Count ).Where( d => session.Runs.Any( r => RunEntry( docs[d].RootElement, r.Name ) != null ) ).ToList();
      if( holders.Count == 0 )
      {
         return required ? throw new ConsolidateRefusal( $"{string.Join( ", ", paths )} does not cover run {session.Runs[0].Name} of session {session.Name}" ) : null;
      }

      if( holders.Count > 1 )
      {
         throw new ConsolidateRefusal( $"the runs of session {session.Name} are covered by more than one observer summary ({string.Join( ", ", holders.Select( h => paths[h] ) )}); one file must cover them all" );
      }

      string path = paths[holders[0]];
      var info = new ObserverInfo { Session = session.Name, Path = path, Copy = required ? NEWEST_COPY : $"observer/summary-{session.Name}.json", Sha256 = EngineFactSheet.FileSha256( path ), Schema = SCHEMA, TimersCovered = true };
      foreach( RunResult run in session.Runs )
      {
         JsonElement entry = RunEntry( docs[holders[0]].RootElement, run.Name ) ?? throw new ConsolidateRefusal( $"{path} does not cover run {run.Name} of session {session.Name}" );
         info.Runs.Add( ReadRun( run.Name, entry ) );
      }

      info.CpuMax = info.Runs.Select( r => r.ObserverCpuMax ).Max();
      info.AperfWorstDeviationBp = info.Runs.Select( r => r.AperfWorstDeviationBp ).Max();
      info.Msr620ValuesSeen = info.Runs.SelectMany( r => r.Msr620ValuesSeen ).Distinct( StringComparer.Ordinal ).OrderBy( v => v, StringComparer.Ordinal ).ToList();
      info.TimersCovered = info.Runs.All( r => r.TimersCovered == true );
      info.PassesWithTimerFired = info.Runs.Sum( r => r.PassesWithTimerFired ?? 0 );
      return info;
   }

   #endregion Private Methods
}
