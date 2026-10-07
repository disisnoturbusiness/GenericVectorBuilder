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

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Reads the summary for the runs of the newest claim session.
   /// </summary>
   /// <param name="path">The summary.json, or null when none was given.</param>
   /// <param name="newest">The newest claim session.</param>
   /// <returns>The block, or null when no path was given.</returns>
   /// <exception cref="ConsolidateRefusal">The file is missing, too large, not JSON, of another schema, or does not cover a run.</exception>
   public static ObserverInfo? Read( string? path, ClaimSession newest )
   {
      if( path == null )
      {
         return null;
      }

      using JsonDocument doc = Load( path );
      JsonElement root = doc.RootElement;
      string? schema = ResultJson.Text( root, "schema" );
      if( schema != SCHEMA )
      {
         throw new ConsolidateRefusal( $"{path} has schema '{schema ?? ConsolidateIdentity.NOT_RECORDED}', not '{SCHEMA}'" );
      }

      var info = new ObserverInfo { Path = path, Sha256 = EngineFactSheet.FileSha256( path ), Schema = schema, TimersCovered = true };
      foreach( RunResult run in newest.Runs )
      {
         JsonElement entry = RunEntry( root, run.Name ) ?? throw new ConsolidateRefusal( $"{path} does not cover run {run.Name} of session {newest.Name}" );
         info.Runs.Add( ReadRun( run.Name, entry ) );
      }

      info.CpuMax = info.Runs.Select( r => r.ObserverCpuMax ).Max();
      info.AperfWorstDeviationBp = info.Runs.Select( r => r.AperfWorstDeviationBp ).Max();
      info.Msr620ValuesSeen = info.Runs.SelectMany( r => r.Msr620ValuesSeen ).Distinct( StringComparer.Ordinal ).OrderBy( v => v, StringComparer.Ordinal ).ToList();
      info.TimersCovered = info.Runs.All( r => r.TimersCovered == true );
      info.PassesWithTimerFired = info.Runs.Sum( r => r.PassesWithTimerFired ?? 0 );
      return info;
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

   #endregion Private Methods
}
