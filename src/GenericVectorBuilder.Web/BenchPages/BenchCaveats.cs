using System.Globalization;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// The list of known problems under the summary table, written from the data of the summary being
/// shown: its run count, collection size, machine conditions, the flags the consolidate command computed
/// for each engine, the runs it dropped, the engines it left out, and the durability statement of an engine
/// that holds everything in memory.
/// Why from the data and not from a table keyed by the published folder's name: the 5 Oct list was
/// attached to a folder name, the folder was renamed after the independent review blocked it, and the page
/// then showed the general list under numbers the list had been written against. A caveat that is a fact
/// about a folder belongs in that folder's own consolidated.json, where it travels with the numbers.
/// Nothing here states a number the summary does not carry, and every sentence names the engines it is about.
/// </summary>
public static class BenchCaveats
{
   #region Data Members

   /// <summary>Longest durability statement quoted whole; a longer one is cut at a sentence end or at this length.</summary>
   public const int MAX_QUOTED = 320;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The caveats of a summary read from a consolidate output.
   /// </summary>
   /// <param name="summary">The summary.</param>
   /// <returns>One plain sentence each, in the order a reader needs them: what the table is, the machine, the warnings, the runs and engines left out, the engines that lose data.</returns>
   public static IReadOnlyList<string> For( BenchSummary summary )
   {
      var list = new List<string> { Guide( summary ) };
      int? rows = summary.Rows;
      if( rows is > 0 and <= BenchSummaryHtml.SMALL_COLLECTION_MAX_ROWS )
      {
         list.Add( $"{rows.Value.ToString( "N0", CultureInfo.InvariantCulture )} vectors measures the cost of each call, through each engine's .NET client library, more than how an engine scales." );
      }

      if( summary.Conditions?.Partition is string partition )
      {
         list.Add( $"The test client and the engines ran on one machine, on separate CPUs where the partition says so ({partition})." );
      }

      list.AddRange( Warnings( summary ) );
      list.AddRange( Left( summary ) );
      list.AddRange( InMemory( summary ) );
      return list;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The first line: what the numbers are, and where the evidence for each marker is.
   /// </summary>
   /// <param name="summary">The summary.</param>
   /// <returns>The sentence.</returns>
   private static string Guide( BenchSummary summary )
   {
      string what = summary.HasRanges ? $"These are medians of {summary.Runs} runs." : "This is one run, so no spread is known.";
      return $"{what} The small markers after an engine name carry the warnings the consolidate command computed for them; the report's Flags and Notes sections have the evidence.";
   }

   /// <summary>
   /// One sentence for each meaning a flag can have that any engine in the table carries (two kinds that mean the same, such as an index
   /// not ready after the load and after the searches, share one sentence): what it means and which engines have it.
   /// </summary>
   /// <param name="summary">The summary.</param>
   /// <returns>The sentences, in the order of the legend.</returns>
   private static IEnumerable<string> Warnings( BenchSummary summary )
   {
      return summary.Ranked.Where( r => r.Flags is { Count: > 0 } )
         .SelectMany( r => r.Flags!.Select( f => ( Row: r, Flag: f ) ) )
         .GroupBy( x => BenchFlagInfo.Meaning( x.Flag.Kind ) ).OrderBy( g => g.Min( x => BenchFlagInfo.Order( x.Flag.Kind ) ) )
         .Select( g => $"{g.Key} Engines: {Names( g.Select( x => x.Row.Name ).Distinct( StringComparer.Ordinal ) )}." );
   }

   /// <summary>
   /// The runs the report left out of the medians and the engines it left out of the table.
   /// </summary>
   /// <param name="summary">The summary.</param>
   /// <returns>The sentences, none when nothing was left out.</returns>
   private static IEnumerable<string> Left( BenchSummary summary )
   {
      if( summary.Dropped is { Count: > 0 } dropped )
      {
         yield return $"{dropped.Count} run{( dropped.Count == 1 ? " was" : "s were" )} left out of the medians: {string.Join( "; ", dropped )}.";
      }

      if( summary.Withheld is { Count: > 0 } withheld )
      {
         yield return $"{Names( withheld.Select( w => w.Name ) )} {( withheld.Count == 1 ? "was" : "were" )} measured but {( withheld.Count == 1 ? "is" : "are" )} not in the table; the reason is listed under it.";
      }
   }

   /// <summary>
   /// For each engine that holds everything in memory, what its recorded durability statement says.
   /// </summary>
   /// <param name="summary">The summary.</param>
   /// <returns>The sentences, none when no in-memory engine is in the table.</returns>
   private static IEnumerable<string> InMemory( BenchSummary summary )
   {
      foreach( BenchEngineRow row in summary.Ranked.Where( r => r.InMemory ) )
      {
         string? durability = summary.Configs?.FirstOrDefault( c => c.Key == row.Key )?.Durability;
         yield return durability == null ? $"{row.Name} holds everything in memory; these results record no durability statement for it." : $"{row.Name} holds everything in memory. As recorded: {Quote( durability )}";
      }
   }

   /// <summary>
   /// A recorded statement cut at its first sentence end past a sentence's length, or at <see cref="MAX_QUOTED"/> characters, with an ellipsis when cut.
   /// </summary>
   /// <param name="text">The statement.</param>
   /// <returns>The text to quote, ending in a full stop or an ellipsis.</returns>
   private static string Quote( string text )
   {
      string line = text.Trim().Replace( '\n', ' ' ).Replace( '\r', ' ' );
      if( line.Length <= MAX_QUOTED )
      {
         return line.EndsWith( '.' ) ? line : line + ".";
      }

      int end = line.IndexOf( ". ", StringComparison.Ordinal );
      return end > 0 && end < MAX_QUOTED ? line[..( end + 1 )] : line[..MAX_QUOTED].TrimEnd() + "...";
   }

   /// <summary>
   /// "a", "a and b" or "a, b and c".
   /// </summary>
   /// <param name="names">Names.</param>
   /// <returns>The joined text.</returns>
   private static string Names( IEnumerable<string> names )
   {
      List<string> list = names.ToList();
      return list.Count <= 1 ? string.Join( string.Empty, list ) : string.Join( ", ", list.Take( list.Count - 1 ) ) + " and " + list[^1];
   }

   #endregion Private Methods
}
