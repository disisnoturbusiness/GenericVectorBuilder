using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// Decides whether runs are the same kind of experiment, and refuses to merge them when they
/// are not: the same benchmark command, the same engine hosting for every listed target (a
/// container against a native service) and the same segment layout wherever an engine reports one.
/// Why a refusal and not "use the largest group": the settings check drops the odd run and says
/// so, which is right for a stray Debug build; but a run-all and a bench run, or a container and a
/// native engine, are both legitimate sets of numbers, and quietly choosing one by count would
/// pick the answer. The caller must say which kind it wants by passing only those runs.
/// Why these three: a bench run searches a copy that an earlier replicate loaded while a run-all
/// loads its own, and Qdrant ended the first with 3 or 4 segments and the second with 2; a
/// container is cut off from the host by its network and CPU limits, a native service is not;
/// Milvus compacted two sealed segments covering 1,048 rows for 524 stored during the passes.
/// A field a run did not record counts as a value of its own, so a run from before the field
/// existed is not mixed with runs that have it.
/// </summary>
public static class ConsolidateIdentity
{
   #region Data Members

   /// <summary>How many run names are listed for each side of a difference before "and N more".</summary>
   public const int MAX_RUNS_LISTED = 3;

   private const string NOT_RECORDED = "not recorded";
   private const string NOT_REPORTED = "not reported";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Checks that the runs are one kind of experiment.
   /// </summary>
   /// <param name="runs">Runs that would be merged.</param>
   /// <param name="targets">The listed targets (hosting and segment layout are compared for these only).</param>
   /// <returns>Null when they agree; otherwise a message that names every difference, the runs on each side, and what to do.</returns>
   public static string? Refusal( IReadOnlyList<RunResult> runs, IReadOnlyList<string> targets )
   {
      if( runs.Count < 2 )
      {
         return null;
      }

      var differences = new List<string>();
      foreach( string field in Fields( runs[0], targets ).Select( f => f.Name ) )
      {
         IEnumerable<IGrouping<string, string>> groups = runs
            .Select( r => ( Run: r.Name, Value: Fields( r, targets ).First( f => f.Name == field ).Value ) )
            .GroupBy( x => x.Value, x => x.Run );
         List<IGrouping<string, string>> list = groups.ToList();
         if( list.Count > 1 )
         {
            differences.Add( $"{field}: {string.Join( " vs ", list.Select( g => $"{g.Key} ({RunList( g )})" ) )}" );
         }
      }

      return differences.Count == 0 ? null
         : "Refusing to merge runs that are not the same kind of experiment. " + string.Join( "; ", differences )
            + ". Consolidate each kind on its own: pass only the runs of one kind (--runs).";
   }

   /// <summary>
   /// The hosting class of a target: container, native or embedded; anything else lower-cased as written.
   /// Why classes and not the raw text: "compose" and "container" are the same hosting, "always-on" and
   /// "systemd" are the same hosting, and the difference that matters is container against native.
   /// </summary>
   /// <param name="hosting">The hosting text a run recorded, or null.</param>
   /// <returns>The class, or "not recorded".</returns>
   public static string HostingClass( string? hosting )
   {
      if( string.IsNullOrWhiteSpace( hosting ) )
      {
         return NOT_RECORDED;
      }

      string text = hosting.Trim().ToLowerInvariant();
      if( text.Contains( "embedded" ) || text.Contains( "in-process" ) || text.Contains( "in process" ) )
      {
         return "embedded";
      }

      if( text.Contains( "compose" ) || text.Contains( "container" ) || text.Contains( "docker" ) || text.Contains( "podman" ) )
      {
         return "container";
      }

      return text.Contains( "always-on" ) || text.Contains( "native" ) || text.Contains( "systemd" ) || text.Contains( "service" ) ? "native" : text;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The facts that must agree between runs, as (name, value) pairs in a fixed order.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <param name="targets">The listed targets.</param>
   /// <returns>The pairs.</returns>
   private static IEnumerable<(string Name, string Value)> Fields( RunResult run, IReadOnlyList<string> targets )
   {
      yield return ( "command", run.Command ?? NOT_RECORDED );
      foreach( string target in targets )
      {
         TargetResult? t = run.Find( target );
         string? hosting = t?.Hosting ?? run.Conditions.Engines.FirstOrDefault( e => e.Target == target )?.Hosting;
         yield return ( $"hosting of {target}", HostingClass( hosting ) );
         yield return ( $"segment layout of {target} after the load", t?.AfterLoad?.Layout ?? NOT_REPORTED );
      }
   }

   /// <summary>
   /// The first few run names of a group, then how many more.
   /// </summary>
   /// <param name="names">Run names.</param>
   /// <returns>E.g. "r1, r2, r3 and 2 more".</returns>
   private static string RunList( IEnumerable<string> names )
   {
      List<string> list = names.ToList();
      string shown = string.Join( ", ", list.Take( MAX_RUNS_LISTED ) );
      return list.Count > MAX_RUNS_LISTED ? $"{shown} and {list.Count - MAX_RUNS_LISTED} more" : shown;
   }

   #endregion Private Methods
}
