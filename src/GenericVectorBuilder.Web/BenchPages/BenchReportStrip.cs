namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// Leaves out of a run report's rendered copy the sentences that tie the client CPU figure to a share of the
/// latency, and says that it did.
/// Why: client CPU per search is the time summed over every thread of the client, and it exceeds the time
/// per search for some engines, so it is not a part of the latency and a sentence that says it is a large
/// part of what is measured is wrong. The reports written before the v8 framing carry those sentences, and a
/// reader who follows a published claim to its runs would read them next to a page that denies them.
/// Why a strip at render time and not an edit of the file: a run's report is a record. The raw file keeps its
/// text and is linked on the page, and the page says that sentences were left out.
/// </summary>
public static class BenchReportStrip
{
   #region Data Members

   /// <summary>The first sentence that follows the definition of the client CPU column in the reports of runs up to the sixth of October 2026.</summary>
   public const string CLOSE_TO_LATENCY = " Where it is close to the latency, the client library is a large part of what is measured.";

   /// <summary>The sentence after it in the reports of those runs that carry it.</summary>
   public const string EMBEDDED_OVERHEAD = " For an embedded engine (DuckDB, sqlite-vec) the engine runs inside the client process, so its figure is the engine's own CPU time, not client overhead.";

   private static readonly string[] SENTENCES = { CLOSE_TO_LATENCY, EMBEDDED_OVERHEAD };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Removes each of the listed sentences from a report's text.
   /// </summary>
   /// <param name="markdown">The report text.</param>
   /// <param name="removed">How many sentences were removed.</param>
   /// <returns>The text without them.</returns>
   public static string Apply( string markdown, out int removed )
   {
      removed = 0;
      string text = markdown;
      foreach( string sentence in SENTENCES )
      {
         int count = ( text.Length - text.Replace( sentence, string.Empty, StringComparison.Ordinal ).Length ) / sentence.Length;
         removed += count;
         text = count == 0 ? text : text.Replace( sentence, string.Empty, StringComparison.Ordinal );
      }

      return text;
   }

   #endregion Public Methods
}
