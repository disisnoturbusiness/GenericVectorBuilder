namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// The catalogue of sentence slots the page places. A slot is "section" or "section.name"; the section (the part
/// before the first dot) decides where the page prints the sentence, and the name after it is the consolidate
/// command's own label, so the command may add names without a change here.
/// Why a catalogue: the page prints every sentence of the file once, and a slot with no section here is not dropped
/// but printed under the heading for statements with no place, so a slot the command adds and this list lacks shows
/// itself on the first page that carries it.
/// </summary>
public static class BenchSlots
{
   #region Data Members

   /// <summary>The headline; exactly one on a set that is not stopped, none on a stopped one. Printed first.</summary>
   public const string HEADLINE = "headline";

   /// <summary>The subtitle: what the numbers are of and where. Printed under the headline.</summary>
   public const string SUBTITLE = "subtitle";

   /// <summary>Why a set is stopped. Printed in the stopped block.</summary>
   public const string STOPPED = "stopped";

   /// <summary>A sentence under one metric table: "table.p50Ms", "table.qps1", "table.qps8", "table.exactP50Ms"; a "table" sentence with no metric follows the last table.</summary>
   public const string TABLE = "table";

   /// <summary>The claim rule and the threshold: printed first under the threshold heading.</summary>
   public const string RULE = "rule";

   /// <summary>The threshold basis: the largest moves, their runs and differences, the exclusions, the runs counted. Printed after the rule.</summary>
   public const string BASIS = "basis";

   /// <summary>Sentences under the recall table.</summary>
   public const string RECALL = "recall";

   /// <summary>Sentences above the facts and costs table (the framing of a cost).</summary>
   public const string WHY = "why";

   /// <summary>Sentences about the drift between sessions.</summary>
   public const string DRIFT = "drift";

   /// <summary>Disclosures: "disclosure.clock", "disclosure.images", "disclosure.durability.redis" and the like.</summary>
   public const string DISCLOSURE = "disclosure";

   /// <summary>Targets the report leaves out.</summary>
   public const string NOT_IN_REPORT = "notinreport";

   /// <summary>The runs used; "runs.reuse.NAME" says why a published set reuses the runs of the session NAME that a blocked set used, and the run pages of those runs print it.</summary>
   public const string RUNS = "runs";

   /// <summary>The slot prefix of a reuse sentence: "runs.reuse." then the session's name, or "claim." or "basis." and the session's name.</summary>
   public const string REUSE_PREFIX = RUNS + ".reuse.";

   /// <summary>The word in a reuse slot that limits the sentence to the runs of a compared session.</summary>
   public const string ROLE_CLAIM = "claim";

   /// <summary>The word in a reuse slot that limits the sentence to the runs that feed the threshold.</summary>
   public const string ROLE_BASIS = "basis";

   /// <summary>The method as the runs recorded it, quoted. Printed last.</summary>
   public const string METHOD = "method";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Reads a reuse slot: which session's runs it is about and, when the slot says so, in which part (a run of a compared session, or a run that only feeds
   /// the threshold). "runs.reuse.v7" is about the runs of v7 in either part; "runs.reuse.basis.v5" is about the runs of v5 that feed the threshold.
   /// Why in the slot name and not in the text: the page must choose the sentence for a run page from a recorded field, never from the words of a sentence.
   /// </summary>
   /// <param name="slot">The slot name.</param>
   /// <returns>The part (null for either) and the session's name; null when the slot is not a reuse slot or names no session.</returns>
   public static ( string? Role, string Session )? ReuseTarget( string slot )
   {
      if( !slot.StartsWith( REUSE_PREFIX, StringComparison.Ordinal ) )
      {
         return null;
      }

      string[] parts = slot[REUSE_PREFIX.Length..].Split( '.' );
      return parts switch
      {
         [var session] when session.Length > 0 => ( null, session ),
         [ROLE_CLAIM or ROLE_BASIS, var session] when session.Length > 0 => ( parts[0], session ),
         _ => null,
      };
   }

   /// <summary>
   /// Every section the page places, so a test can check that a sentence of each is printed in its own place.
   /// </summary>
   public static IReadOnlyList<string> Sections { get; } = new[] { HEADLINE, SUBTITLE, STOPPED, TABLE, RULE, BASIS, RECALL, WHY, DRIFT, DISCLOSURE, NOT_IN_REPORT, RUNS, METHOD };

   #endregion Public Methods
}
