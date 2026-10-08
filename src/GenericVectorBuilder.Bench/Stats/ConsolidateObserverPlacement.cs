using System.Globalization;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// Where the observer itself ran during a session, read from the analysis the run operator saved beside the report (design/bench-inputs/observer-placement/analysis-SEED.txt, one file per run).
/// The observer summary says how much CPU the observer used, but not whether it was pinned off the engine CPUs; the operator's analysis does, from the placement of the observer's own threads.
/// Why saved files and not the summary: the summary schema has no field for it, and a sentence about the observer's placement must cite a source a reader can open.
/// What the report does with it: it states that the observer was not pinned, what share of its resident-thread ticks fell on the engine CPUs, and the most CPU it used by cgroup, so a reader sees that
/// the observer's own load ran on the CPUs under test too.
/// When the files are absent nothing is said: the summary alone cannot support the sentence. When a file is present and is not in the form read here, the consolidation refuses, because a saved source
/// this report cites must say what the sentence says.
/// </summary>
public static class ConsolidateObserverPlacement
{
   #region Data Members

   /// <summary>The folder of the saved analyses, relative to the repository.</summary>
   public const string FOLDER = "design/bench-inputs/observer-placement";

   /// <summary>The words that say the observer was not pinned.</summary>
   public const string NOT_PINNED = "The observer is NOT pinned";

   private static readonly TimeSpan MATCH_TIMEOUT = TimeSpan.FromSeconds( 5 );
   private static readonly Regex PLACEMENT = new(
      @"Placement of the observer's resident threads \((?<seen>\d+) ticks seen[^)]*\): (?<ticks>\d+) ticks \((?<pct>\d+(?:\.\d+)?) percent\) on engine CPUs (?<cpus>\d+(?:-\d+)?(?:,\d+(?:-\d+)?)*)",
      RegexOptions.Compiled | RegexOptions.CultureInvariant, MATCH_TIMEOUT );
   private static readonly Regex CGROUP = new( @"all its processes, short-lived children included\): (?<cpu>\d+(?:\.\d+)?) CPUs", RegexOptions.Compiled | RegexOptions.CultureInvariant, MATCH_TIMEOUT );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Fills the placement of the observer of the newest session when every run of the session has a saved analysis.
   /// </summary>
   /// <param name="info">The observer block of the newest session, or null when no observer summary was given.</param>
   /// <param name="session">The newest claim session.</param>
   /// <param name="repoRoot">The repository root.</param>
   /// <exception cref="ConsolidateRefusal">A saved analysis is not in the form this reader knows.</exception>
   public static void Fill( ObserverInfo? info, ClaimSession session, string repoRoot )
   {
      if( info == null )
      {
         return;
      }

      var runs = new List<ObserverPlacementRun>();
      foreach( RunResult run in session.Runs )
      {
         string relative = $"{FOLDER}/analysis-{run.RunSeed}.txt";
         string path = Path.Combine( repoRoot, relative );
         if( run.RunSeed == null || !File.Exists( path ) )
         {
            return;
         }

         string text = Collapse( File.ReadAllText( path ) );
         Match placement = PLACEMENT.Match( text );
         Match cgroup = CGROUP.Match( text );
         if( !placement.Success || !cgroup.Success || !text.Contains( NOT_PINNED, StringComparison.Ordinal ) )
         {
            throw new ConsolidateRefusal( $"{relative} is not in the form this report reads: it must hold the observer's placement ('Placement of the observer's resident threads (...): N ticks (P percent) on engine CPUs LIST'), its cgroup CPU and '{NOT_PINNED}'" );
         }

         runs.Add( new ObserverPlacementRun
         {
            Folder = run.Name, File = relative, Percent = placement.Groups["pct"].Value, Cpus = placement.Groups["cpus"].Value, CgroupCpu = cgroup.Groups["cpu"].Value,
            PlacementQuote = $"{placement.Groups["ticks"].Value} ticks ({placement.Groups["pct"].Value} percent) on engine CPUs {placement.Groups["cpus"].Value}",
            CgroupQuote = $"children included): {cgroup.Groups["cpu"].Value} CPUs",
         } );
      }

      info.Placement = new ObserverPlacement
      {
         Runs = runs,
         MaxCgroupCpu = runs.Max( r => double.Parse( r.CgroupCpu, CultureInfo.InvariantCulture ) ).ToString( "0.####", CultureInfo.InvariantCulture ),
         NotPinnedQuote = NOT_PINNED,
      };
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// White space collapsed to single spaces, as the doc resolver reads a saved page.
   /// </summary>
   /// <param name="text">The text.</param>
   /// <returns>The collapsed text.</returns>
   private static string Collapse( string text )
   {
      return Regex.Replace( text, @"\s+", " ", RegexOptions.CultureInvariant, MATCH_TIMEOUT ).Trim();
   }

   #endregion Private Methods
}
