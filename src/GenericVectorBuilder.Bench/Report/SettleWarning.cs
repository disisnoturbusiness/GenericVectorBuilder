using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Bench.Report;

/// <summary>
/// One timed pass the run recorded as NOT HELD: after the settle check the engine's latency was still moving, and the timed pass itself landed outside the
/// margin around the figure the warm-up had settled at. The settle warning of a target lists its passes one after another, and this is what it says of one.
/// </summary>
/// <param name="Pass">The pass name, "default@8", "default@1" or "exact".</param>
/// <param name="Token">The clause of the warning that records it, verbatim, from "the timed pass" to "NOT HELD". Why verbatim: a sentence cites it as a token of the target's notes.</param>
/// <param name="Timed">The timed figure with its unit, "336 QPS" or "p50 4.869 ms".</param>
/// <param name="OffPercent">How far the timed figure was from the settled one, percent, as written.</param>
/// <param name="Settled">The settled figure with its unit.</param>
/// <param name="LimitPercent">The margin, percent, as written.</param>
/// <param name="Metric">The figure's kind: "qps" or "p50".</param>
public sealed record NotHeldPass( string Pass, string Token, string Timed, string OffPercent, string Settled, string LimitPercent, string Metric );

/// <summary>
/// Reads the settle warning of a target (the text after "WARNING: ") for the passes it records as NOT HELD.
/// Why a reader of the text: the run records this only as prose, and the report must say which pass of which run the claim rule's threshold rests on.
/// Why it refuses a clause it cannot read: a NOT HELD clause in a form this reader does not know would otherwise be dropped, and the page would not say it.
/// </summary>
public static class SettleWarning
{
   #region Data Members

   /// <summary>The words that mark a pass as not held.</summary>
   public const string MARK = "NOT HELD";

   private static readonly TimeSpan MATCH_TIMEOUT = TimeSpan.FromSeconds( 5 );
   private static readonly Regex PASS_START = new( @"(?<![A-Za-z0-9@])(?<pass>default@\d+|exact): warm-up", RegexOptions.Compiled | RegexOptions.CultureInvariant, MATCH_TIMEOUT );
   private static readonly Regex CLAUSE = new(
      @"^the timed pass (?<timed>(?<p50>p50 )?[\d,]+(?:\.\d+)? (?:QPS|ms)), (?<off>\d+)% from the settled (?<settled>(?:p50 )?[\d,]+(?:\.\d+)? (?:QPS|ms)) \(limit (?<limit>\d+)%[^)]*\), NOT HELD$",
      RegexOptions.Compiled | RegexOptions.CultureInvariant, MATCH_TIMEOUT );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The passes a settle warning records as NOT HELD, in the order the warning lists them.
   /// </summary>
   /// <param name="warning">The warning text, or null when the target has none.</param>
   /// <returns>The passes; empty when the warning is null or marks none.</returns>
   /// <exception cref="InvalidDataException">A NOT HELD mark has no pass before it, or the clause around it is not in the form this reader knows.</exception>
   public static IReadOnlyList<NotHeldPass> NotHeld( string? warning )
   {
      var found = new List<NotHeldPass>();
      if( string.IsNullOrEmpty( warning ) )
      {
         return found;
      }

      List<Match> starts = PASS_START.Matches( warning ).ToList();
      int at = warning.IndexOf( MARK, StringComparison.Ordinal );
      while( at >= 0 )
      {
         Match? pass = starts.LastOrDefault( s => s.Index < at );
         int from = warning.LastIndexOf( "the timed pass ", at, StringComparison.Ordinal );
         if( pass == null || from < pass.Index )
         {
            throw new InvalidDataException( $"the settle warning marks a pass {MARK} where no pass or no timed-pass clause precedes it: {Excerpt( warning, at )}" );
         }

         string token = warning[from..( at + MARK.Length )];
         Match clause = CLAUSE.Match( token );
         if( !clause.Success )
         {
            throw new InvalidDataException( $"the settle warning's {MARK} clause is not in the form this report reads (the timed pass, its figure, its distance from the settled figure, the limit): {token}" );
         }

         found.Add( new NotHeldPass( pass.Groups["pass"].Value, token, clause.Groups["timed"].Value, clause.Groups["off"].Value, clause.Groups["settled"].Value, clause.Groups["limit"].Value,
            clause.Groups["timed"].Value.EndsWith( "QPS", StringComparison.Ordinal ) ? "qps" : "p50" ) );
         at = warning.IndexOf( MARK, at + MARK.Length, StringComparison.Ordinal );
      }

      return found;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A stretch of the warning around a position, for a message.
   /// </summary>
   /// <param name="text">The warning.</param>
   /// <param name="at">The position.</param>
   /// <returns>Up to 160 characters before the position and 20 after.</returns>
   private static string Excerpt( string text, int at )
   {
      int from = Math.Max( 0, at - 160 );
      return text[from..Math.Min( text.Length, at + 20 )];
   }

   #endregion Private Methods
}
