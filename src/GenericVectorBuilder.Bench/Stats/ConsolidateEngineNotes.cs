using System.Text.RegularExpressions;
using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The per-engine notes of a consolidated report: what a reader needs to know about an engine to
/// read its speed. Three kinds.
/// cpu-cap: an engine that limits its own CPU use (Oracle Free sets cpu_count to 2 by license,
/// whatever CPUs it is given); read from the results when a run recorded a limit (a "cpuCap" on the
/// target or on its engine record, or the engine's own index-state text saying "caps itself at N
/// CPUs"), otherwise stated from the documented limit and marked "known", never as something the
/// run measured.
/// exact-by-design: an engine whose default search scans every vector (SQL Server without a vector
/// index, Qdrant with exact=true); read from its own index state or index text.
/// scans-all-vectors: an engine that was meant to use an index but built none at this size
/// (Elasticsearch builds no graph under 1,043 vectors at 1,024 dimensions), so its default search
/// equals its exact search.
/// Why these: at 524 vectors a scan over every vector costs about as much as an index walk, so the
/// exact engines sit among the indexed ones, and an engine held to 2 CPUs cannot use the 4 it was
/// given; a reader comparing speeds needs both facts beside the number.
/// </summary>
public static class ConsolidateEngineNotes
{
   #region Data Members

   /// <summary>Where a note came from: the result files say it.</summary>
   public const string SOURCE_RESULTS = "results";

   /// <summary>Where a note came from: a known product limit the run did not record.</summary>
   public const string SOURCE_KNOWN = "known";

   /// <summary>Printed with a known (not recorded) Oracle CPU cap.</summary>
   public const string ORACLE_CAP_TEXT = "Oracle Free caps itself at 2 CPUs (cpu_count 2, set by its license) whatever CPUs it is given, so pinning it to more CPUs gives it no more. Read as cpu_count 2 in the 2026-10-04 probe.";

   private static readonly Regex EXACT_IN_DETAIL = new( @"no index used, exact scan by design(?: \([^)]*\))?", RegexOptions.Compiled | RegexOptions.IgnoreCase );
   private static readonly Regex EXACT_IN_INDEX = new( @"^\s*exact\b|brute-force scan|no vector index|\bexact search\b", RegexOptions.Compiled | RegexOptions.IgnoreCase );
   private static readonly Regex CAP_IN_TEXT = new( @"\w+(?: \w+)? caps itself at \d+ CPUs?(?: \([^)]*\))?", RegexOptions.Compiled | RegexOptions.IgnoreCase );
   private static readonly Regex SCAN_ALL = new( @"no hnsw graph, searches scan all (\d[\d,]*) vectors", RegexOptions.Compiled | RegexOptions.IgnoreCase );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The notes for one target.
   /// </summary>
   /// <param name="summary">The target's summary (engine and index texts, per-run index states).</param>
   /// <param name="used">Runs used.</param>
   /// <returns>The notes in a fixed order: cpu-cap, exact-by-design, scans-all-vectors.</returns>
   public static List<EngineNote> For( TargetSummary summary, IReadOnlyList<RunResult> used )
   {
      var notes = new List<EngineNote>();
      AddCap( notes, summary, used );
      AddExact( notes, summary );
      AddScan( notes, summary );
      return notes;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Adds the CPU-cap note: recorded by a run (a "cpuCap" on the target or on its engine record),
   /// else said by the engine's own index-state text, else the documented Oracle Free limit.
   /// </summary>
   /// <param name="notes">Receives the note.</param>
   /// <param name="summary">The target's summary.</param>
   /// <param name="used">Runs used.</param>
   private static void AddCap( List<EngineNote> notes, TargetSummary summary, IReadOnlyList<RunResult> used )
   {
      var recorded = new List<string>();
      foreach( RunResult run in used )
      {
         TargetResult? t = run.Find( summary.Name );
         string? cap = t?.CpuCap ?? run.Conditions.Engines.FirstOrDefault( e => e.Target == summary.Name )?.CpuCap;
         if( cap != null && !recorded.Contains( cap ) )
         {
            recorded.Add( cap );
         }
      }

      string? said = recorded.Count > 0 ? null : CapInResults( summary );
      if( recorded.Count > 0 )
      {
         notes.Add( new EngineNote { Kind = "cpu-cap", Source = SOURCE_RESULTS, Text = $"CPU cap recorded in the results: {string.Join( "; ", recorded )}. The engine cannot use more CPUs than that, whatever it is pinned to." } );
      }
      else if( said != null )
      {
         notes.Add( new EngineNote { Kind = "cpu-cap", Source = SOURCE_RESULTS, Text = $"The engine cannot use more CPUs than its own cap, whatever it is pinned to. The results say: {Sentence( said )}" } );
      }
      else if( summary.Engines.Any( e => e.Contains( "Oracle", StringComparison.OrdinalIgnoreCase ) && e.Contains( "Free", StringComparison.OrdinalIgnoreCase ) ) )
      {
         notes.Add( new EngineNote { Kind = "cpu-cap", Source = SOURCE_KNOWN, Text = ORACLE_CAP_TEXT } );
      }
   }

   /// <summary>
   /// The engine's own statement of a CPU cap ("Oracle Free caps itself at 2 CPUs (...)") in its
   /// index-state evidence or its index text, or null when it made none.
   /// </summary>
   /// <param name="summary">The target's summary.</param>
   /// <returns>The statement as written, or null.</returns>
   private static string? CapInResults( TargetSummary summary )
   {
      IEnumerable<string?> texts = summary.PerRun.SelectMany( r => new[] { r.AfterLoad?.Detail, r.AfterSearch?.Detail } ).Concat( summary.Indexes );
      return texts.Select( d => d == null ? null : CAP_IN_TEXT.Match( d ) ).FirstOrDefault( m => m is { Success: true } )?.Value;
   }

   /// <summary>
   /// Adds the exact-by-design note when the engine's own words say its default search scans every vector.
   /// </summary>
   /// <param name="notes">Receives the note.</param>
   /// <param name="summary">The target's summary.</param>
   private static void AddExact( List<EngineNote> notes, TargetSummary summary )
   {
      string? evidence = summary.PerRun.SelectMany( r => new[] { r.AfterLoad?.Detail, r.AfterSearch?.Detail } ).Select( d => d == null ? null : EXACT_IN_DETAIL.Match( d ) ).FirstOrDefault( m => m is { Success: true } )?.Value
         ?? summary.Indexes.FirstOrDefault( i => EXACT_IN_INDEX.IsMatch( i ) );
      if( evidence != null )
      {
         notes.Add( new EngineNote { Kind = "exact-by-design", Source = SOURCE_RESULTS, Text = $"Exact by design: every search compares the query with every stored vector, so its time grows with the collection and index quality plays no part in its speed. The results say: {Sentence( evidence )}" } );
      }
   }

   /// <summary>
   /// Adds the scans-all-vectors note when the engine reported that it built no index graph.
   /// </summary>
   /// <param name="notes">Receives the note.</param>
   /// <param name="summary">The target's summary.</param>
   private static void AddScan( List<EngineNote> notes, TargetSummary summary )
   {
      Match? scan = summary.PerRun.SelectMany( r => new[] { r.AfterLoad?.Detail, r.AfterSearch?.Detail } ).Select( d => d == null ? null : SCAN_ALL.Match( d ) ).FirstOrDefault( m => m is { Success: true } );
      if( scan != null )
      {
         notes.Add( new EngineNote { Kind = "scans-all-vectors", Source = SOURCE_RESULTS, Text = $"No index graph was built at this size: its searches scanned all {scan.Groups[1].Value} vectors, so its default search equals its exact search. The results say: {Sentence( scan.Value )}" } );
      }
   }

   /// <summary>
   /// Ends the evidence with a full stop, so the note reads as a sentence.
   /// </summary>
   /// <param name="evidence">The quoted evidence.</param>
   /// <returns>The evidence with a final period.</returns>
   private static string Sentence( string evidence )
   {
      string text = evidence.Trim().TrimEnd( '.', ';', ',' );
      return text + ".";
   }

   #endregion Private Methods
}
