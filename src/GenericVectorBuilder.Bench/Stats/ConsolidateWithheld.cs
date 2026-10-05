using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// Finds the targets the runs hold that the report leaves out, and writes the reason for each, so
/// that no measured engine disappears from a published report without a line saying why.
/// Why: in the v5 report MongoDB had results in all three runs but was missing from the tables
/// (its segment layout differed between runs, and the command was run once on the other 18 targets
/// and twice more on MongoDB alone), and nothing in the main report said so.
/// Two kinds are listed: a target of the runs that was left out of --targets ("not-listed"), and a
/// listed target that was refused because its engine hosting or engine setup (description, recorded
/// settings, configuration files, durability) differs between the runs ("refused").
/// A listed target that is in the tables never appears here; it carries its flags instead. What the
/// reason names is read from the runs, not guessed: runs that failed or skipped it, a segment layout
/// that differs between runs, a hosting that differs.
/// </summary>
public static class ConsolidateWithheld
{
   #region Data Members

   /// <summary>Kind of a target that is in the runs but not in --targets.</summary>
   public const string KIND_NOT_LISTED = "not-listed";

   /// <summary>Kind of a listed target refused because its engine hosting or engine setup differs between the runs.</summary>
   public const string KIND_REFUSED = "refused";

   private const int MAX_ERROR_TEXT = 120;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Lists every target the report leaves out: the listed targets that were refused (first, in the
   /// order listed) and then every target of the runs used that is not in the listed targets.
   /// </summary>
   /// <param name="used">Runs used, oldest first.</param>
   /// <param name="listed">The targets given with --targets.</param>
   /// <param name="refused">The listed targets refused for a different hosting or engine setup between runs.</param>
   /// <returns>One entry per withheld target; empty when every target found is in the report.</returns>
   public static List<WithheldTarget> Build( IReadOnlyList<RunResult> used, IReadOnlyList<string> listed, IReadOnlyList<RefusedTarget> refused )
   {
      var withheld = refused.Select( r => Refused( r, used ) ).ToList();
      withheld.AddRange( NotListed( used, listed ) );
      return withheld;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// One refused target: the difference found and what to do about it.
   /// </summary>
   /// <param name="refused">The refused target.</param>
   /// <param name="used">Runs used.</param>
   /// <returns>The entry.</returns>
   private static WithheldTarget Refused( RefusedTarget refused, IReadOnlyList<RunResult> used )
   {
      string reason = $"refused: {refused.Difference}. {refused.Explanation}; consolidate each kind on its own by passing only the runs of one kind (--runs).";
      return new WithheldTarget { Target = refused.Target, Kind = KIND_REFUSED, Runs = used.Where( r => r.Find( refused.Target ) != null ).Select( r => r.Name ).ToList(), Reason = reason };
   }

   /// <summary>
   /// Every target of the runs used that is not in the listed targets, each with its reason.
   /// </summary>
   /// <param name="used">Runs used, oldest first.</param>
   /// <param name="listed">The targets given with --targets.</param>
   /// <returns>One entry per target, in the order the first run used lists them.</returns>
   private static List<WithheldTarget> NotListed( IReadOnlyList<RunResult> used, IReadOnlyList<string> listed )
   {
      var names = new List<string>();
      foreach( string name in used.SelectMany( r => r.Targets.Select( t => t.Name ) ) )
      {
         if( name.Length > 0 && !listed.Contains( name, StringComparer.Ordinal ) && !names.Contains( name, StringComparer.Ordinal ) )
         {
            names.Add( name );
         }
      }

      return names.Select( n => Describe( n, used ) ).ToList();
   }

   /// <summary>
   /// One withheld target: where it was found and what the runs say about it.
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <param name="used">Runs used.</param>
   /// <returns>The entry.</returns>
   private static WithheldTarget Describe( string name, IReadOnlyList<RunResult> used )
   {
      List<( RunResult Run, TargetResult Result )> found = used.Select( r => ( Run: r, Result: r.Find( name ) ) ).Where( x => x.Result != null ).Select( x => ( x.Run, x.Result! ) ).ToList();
      var parts = new List<string>
      {
         $"it is in {found.Count} of {used.Count} run(s) used but is not in --targets, so it has no row in any table of this report",
      };
      parts.AddRange( found.Where( x => x.Result.Error != null ).Select( x => $"it failed in {x.Run.Name} ({Short( x.Result.Error! )})" ) );
      string[] unsearched = found.Where( x => x.Result.Error == null && ( !x.Result.Searched || x.Result.P50Ms == null ) ).Select( x => x.Run.Name ).ToArray();
      if( unsearched.Length > 0 )
      {
         parts.Add( $"it has no search result in {string.Join( ", ", unsearched )}" );
      }

      if( ConsolidateIdentity.Difference( found.Select( x => ( x.Run.Name, x.Result.AfterLoad?.Layout ) ) ) is string layouts )
      {
         parts.Add( $"its segment layout after the load differs between runs: {layouts}" );
      }

      if( HostingDifference( found ) is string hosting )
      {
         parts.Add( $"its hosting differs between runs: {hosting}" );
      }

      if( ConsolidateIdentity.SetupDifference( found.Select( x => ( x.Run.Name, x.Result ) ).ToList() ) is string setup )
      {
         parts.Add( $"its engine setup differs between runs ({setup})" );
      }

      parts.Add( "list it in --targets to include it; a layout difference is then shown as a flag, not left out" );
      return new WithheldTarget { Target = name, Kind = KIND_NOT_LISTED, Runs = found.Select( x => x.Run.Name ).ToList(), Reason = string.Join( "; ", parts ) + "." };
   }

   /// <summary>
   /// The hosting classes a target had when they are not the same in every run.
   /// </summary>
   /// <param name="found">The runs that hold the target, with its result.</param>
   /// <returns>Each hosting class with its runs, or null when every run had the same.</returns>
   private static string? HostingDifference( IReadOnlyList<( RunResult Run, TargetResult Result )> found )
   {
      return ConsolidateIdentity.Difference( found.Select( x => ( x.Run.Name, (string?)ConsolidateIdentity.HostingClass( x.Result.Hosting ) ) ) );
   }

   /// <summary>
   /// An error text cut to a readable length, on one line.
   /// </summary>
   /// <param name="error">The error.</param>
   /// <returns>The short text.</returns>
   private static string Short( string error )
   {
      string line = error.Replace( '\n', ' ' ).Replace( '\r', ' ' );
      return line.Length > MAX_ERROR_TEXT ? line[..MAX_ERROR_TEXT] + "..." : line;
   }

   #endregion Private Methods
}
