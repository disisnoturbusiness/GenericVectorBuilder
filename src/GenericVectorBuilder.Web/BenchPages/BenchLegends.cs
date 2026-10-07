namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// Every fixed string the benchmark pages print themselves: headings, column labels, folder banners,
/// the legend of each flag code and the notices for a file the page cannot show.
/// Why one list: the pages must state no fact of their own. A sentence about the data comes from the
/// consolidated file's own sentences, each bound to its sources; what is left is words that name a
/// column or a state of the folder, and those sit here, in one allow-list a test walks, so nothing
/// that carries a number, an engine name, a cause, a purpose, a default or an "only" claim can be added
/// to a page template without failing it.
/// </summary>
public static class BenchLegends
{
   #region Data Members

   /// <summary>Heading of the threshold and basis section.</summary>
   public const string H_THRESHOLD = "Threshold and basis";

   /// <summary>Heading of the p50 table.</summary>
   public const string H_P50 = "p50 latency of one search, milliseconds";

   /// <summary>Heading of the one-searcher throughput table.</summary>
   public const string H_QPS1 = "Searches per second, one searcher";

   /// <summary>Heading of the eight-searcher throughput table.</summary>
   public const string H_QPS8 = "Searches per second, eight searchers at once";

   /// <summary>Heading of the exact-mode table.</summary>
   public const string H_EXACT = "p50 latency in exact mode, milliseconds";

   /// <summary>Heading of the recall table.</summary>
   public const string H_RECALL = "Recall in hits";

   /// <summary>Heading of the facts and costs table.</summary>
   public const string H_WHY = "Facts and costs side by side";

   /// <summary>Heading of the drift section.</summary>
   public const string H_DRIFT = "Drift between sessions";

   /// <summary>Heading of the disclosures section.</summary>
   public const string H_DISCLOSURES = "Disclosures";

   /// <summary>Summary line of the block that lists the clock warnings a set dropped.</summary>
   public const string H_DROPPED = "Dropped clock warnings";

   /// <summary>Heading of the container images section.</summary>
   public const string H_IMAGES = "Container images";

   /// <summary>Summary line of the block that holds the engine settings table.</summary>
   public const string H_ENGINE_SETTINGS = "Engine settings as recorded";

   /// <summary>Heading of the list of targets the report leaves out.</summary>
   public const string H_NOT_IN_REPORT = "Targets not in this report";

   /// <summary>Heading of the list of runs the set uses.</summary>
   public const string H_RUNS = "Runs used";

   /// <summary>Heading of the flags section under the tables.</summary>
   public const string H_FLAGS = "Flags";

   /// <summary>Heading of the list of sentences with no section on this page.</summary>
   public const string H_UNPLACED = "Statements with no place on this page";

   /// <summary>Heading of the list of targets whose setup differs between sessions.</summary>
   public const string H_SETUP_DIFFERS = "Setup differs between sessions";

   /// <summary>Heading of the stopped block.</summary>
   public const string H_STOPPED = "Stopped";

   /// <summary>Heading of the audit block.</summary>
   public const string H_AUDIT = "Audit";

   /// <summary>Summary line of the basis figures block.</summary>
   public const string H_BASIS_FIGURES = "Basis figures";

   /// <summary>Summary line of the block that holds the Markdown report of a consolidated folder.</summary>
   public const string H_REPORT_TEXT = "Report text";

   /// <summary>
   /// Summary line of the report block of a set that is not the current result (withdrawn, blocked, or a file in an older format): the text is kept as it
   /// was written, and a table in it is not a result of this page.
   /// </summary>
   public const string H_REPORT_RECORD = "Report text as it was written, kept for the record";

   /// <summary>Heading of the column of the facts and costs table that holds the settings that control the effort of each search.</summary>
   public const string H_SEARCH_EFFORT = "Search effort per query";

   /// <summary>Summary line of the block that holds the statements about each engine's search settings, with their sources.</summary>
   public const string H_EFFORT_STATEMENTS = "Statements behind the search effort column";

   /// <summary>Label before the first sentence of a run's own note on its warm-up.</summary>
   public const string L_WARMUP_RECORDED = "Warm-up, as this run's notes record it:";

   /// <summary>Words before the value of a run's warmupSearches field, printed when no note of the run describes the warm-up.</summary>
   public const string L_WARMUP_FIELD = "Warm-up: the run recorded the field warmupSearches with the value";

   /// <summary>Words after the value of a run's warmupSearches field.</summary>
   public const string L_WARMUP_NO_NOTE = "and no note that describes the method.";

   /// <summary>Label before the clause of a run's notes that states its clock pin.</summary>
   public const string L_CLOCK_RECORDED = "Clock, as this run's notes record it:";

   /// <summary>Statement on the page of a run whose notes say machine control was on and state no clock pin.</summary>
   public const string L_NO_CLOCK_PIN = "No CPU clock pin is recorded in this run's notes.";

   /// <summary>Label before the commit the benchmark binary of a run was built from.</summary>
   public const string L_CODE_COMMIT = "Code commit";

   /// <summary>What a table row order is; printed under the metric tables.</summary>
   public const string ROW_ORDER = "Rows are in the order of the median of all runs.";

   /// <summary>What a row's list of engines it is not separated from means; printed under the metric tables.</summary>
   public const string ROW_SEPARATION = "Among ranked rows, an engine is ahead of every engine below it that its row does not list, and behind every engine above it that its row does not list.";

   /// <summary>What a row marked one session is.</summary>
   public const string ONE_SESSION = "A row marked one session has figures from one session, its median is of that session's runs, and it is not ranked.";

   /// <summary>What the clock field legacyParsed means when it is true; printed after the field on the machine line of a set.</summary>
   public const string L_LEGACY_PARSED = "(the clock was read from the runs' notes, not from a clock block)";

   /// <summary>What the order of a run page's table is.</summary>
   public const string RUN_ORDER = "Engines are listed in alphabetical order. The table does not rank them.";

   /// <summary>What a dash in a table is.</summary>
   public const string DASH = "A dash means the table has no figure there.";

   /// <summary>What the flag markers are.</summary>
   public const string FLAGS_INTRO = "The small markers after an engine name are flags. Hover a marker for its evidence, or read the list below the tables.";

   /// <summary>What a flag with no description on this page is.</summary>
   public const string FLAG_UNKNOWN = "A flag this page has no description for. Its evidence is listed.";

   /// <summary>What a quoted text is.</summary>
   public const string QUOTE = "Text as the tool recorded it. Its figures were not re-derived for this report.";

   /// <summary>Banner of a candidate folder.</summary>
   public const string BANNER_CANDIDATE = "This folder is a candidate. It has not been published and it is not the page at the top of the results list.";

   /// <summary>Banner of a withdrawn folder.</summary>
   public const string BANNER_WITHDRAWN = "This set is marked withdrawn by its folder name. It is not the current result and is kept for the record.";

   /// <summary>Banner of a blocked folder.</summary>
   public const string BANNER_BLOCKED = "This set is marked blocked by its folder name. It is kept as evidence. The summary page does not use it and its numbers are not the published ones.";

   /// <summary>Notice for a consolidated file in a shape this page no longer shows as a result.</summary>
   public const string OLDER_FORMAT = "This file is in an older format. The page draws no headline and no table from it.";

   /// <summary>Notice above the sentences that have no section on this page.</summary>
   public const string UNPLACED = "These statements came with the results and have no place on this page. They are shown here so none is lost.";

   /// <summary>Label before the fields of the file that mark a set stopped.</summary>
   public const string STOPPED_IN = "Marked stopped in:";

   /// <summary>Notice that a stopped set shows no headline and no tables.</summary>
   public const string STOPPED = "This set is marked stopped. The page shows no headline and no tables.";

   /// <summary>
   /// Notice that sentences were left out of a run report's copy on a run page; the page prints the count it made after it.
   /// The word "predate" is the one the run tooling's page check looks for.
   /// </summary>
   public const string STRIPPED = "Report sentences that predate the current framing and relate client CPU to the latency are left out of this copy. The unedited report is under Raw files. Count left out:";

   /// <summary>Banner of a run page whose report carries a clock warning that a consolidated set dropped.</summary>
   public const string DROPPED_WARNING = "dropped a clock warning recorded in this run's report. The median clock figures are listed here.";

   /// <summary>Title of the list of exclusions the threshold basis applied.</summary>
   public const string L_EXCLUSIONS = "Exclusions";

   /// <summary>Title of the list of folders the threshold basis did not use.</summary>
   public const string L_LEFT_OUT = "Folders left out";

   /// <summary>Title of the list of runs the threshold basis counts.</summary>
   public const string L_BASIS_RUNS = "Basis runs";

   /// <summary>Title of the list of orders that held in one session and not in both.</summary>
   public const string L_UNCONFIRMED = "Orders that held in one session and not in both";

   /// <summary>Title of the list of pairs near the line the claim rule draws.</summary>
   public const string L_CLOSE = "Pairs close to the line";

   /// <summary>Label before a pair's smallest ratio in the lists of pairs.</summary>
   public const string L_MIN_RATIO = "minimum ratio";

   /// <summary>Summary line of the block that lists the evidence of every flag.</summary>
   public const string L_EVIDENCE = "Evidence behind the flags";

   /// <summary>Label before the text of a clock warning as the run recorded it.</summary>
   public const string L_WARNING_AS_RECORDED = "Warning as recorded:";

   /// <summary>Label before the median clock of the engine CPUs.</summary>
   public const string L_ENGINE_MHZ = "Median MHz of the engine CPUs:";

   /// <summary>Label before the median clock of the client CPUs.</summary>
   public const string L_CLIENT_MHZ = "Median MHz of the client CPUs:";

   /// <summary>Label before the folders that use a run.</summary>
   public const string USED_BY = "Used by";

   /// <summary>Message printed in place of the numbers when a file cannot be read.</summary>
   public const string UNREADABLE = "The summary numbers could not be read";

   private static readonly Dictionary<string, ( string Marker, string Legend )> FLAGS = new( StringComparer.Ordinal )
   {
      ["spread"] = ( "Sp", "The runs of this engine differ from each other by more than the spread limit named in the evidence." ),
      ["p50-mean-inconsistent"] = ( "Pm", "The p50 and the mean from the one-searcher pass disagree by more than the limit named in the evidence." ),
      ["unsettled-target"] = ( "Un", "The engine did not pass its own settle check before a timed pass. The evidence names the check." ),
      ["unsettled"] = ( "Un", "The engine did not pass its own settle check before a timed pass. The evidence names the check." ),
      ["busy-box"] = ( "Bz", "CPUs outside the benchmark were busy during the timed pass. The evidence gives the figure." ),
      ["governor-not-performance"] = ( "Gv", "The CPU governor was not set to performance during the run." ),
      ["client-engine-share-cores"] = ( "Sh", "The test client and the engine ran on the same CPU cores." ),
      ["not-release-build"] = ( "Db", "The test client was not a Release build." ),
      ["index-not-ready-after-load"] = ( "Ix", "The engine did not report a finished index after the load or after the searches." ),
      ["index-not-ready-after-search"] = ( "Ix", "The engine did not report a finished index after the load or after the searches." ),
      ["index-not-ready"] = ( "Ix", "The engine did not report a finished index after the load or after the searches." ),
      ["durability-not-stated"] = ( "Du", "No statement of what a crash can lose was recorded for this engine." ),
      ["warmup-errors"] = ( "Er", "Warm-up or timed searches failed." ),
      ["search-errors"] = ( "Er", "Warm-up or timed searches failed." ),
      ["exact-recall-below-1"] = ( "Ex", "The exact mode of this engine did not return the exact answers." ),
      ["fields-missing"] = ( "Ms", "The run did not record some of the fields listed in the evidence." ),
      ["engine-or-index-text-differs"] = ( "Ch", "The engine version or the index description differs between runs." ),
      ["segment-layout-changed"] = ( "Sg", "The engine reported a different segment layout after the searches than after the load." ),
      ["segment-layout-differs-between-runs"] = ( "Sl", "The engine ended with a different segment layout in different runs." ),
      ["segment-layout-differs"] = ( "Sl", "The engine ended with a different segment layout in different runs." ),
      ["clock-off"] = ( "Ck", "The CPU clock was off its pinned value during a timed pass." ),
      ["clock-not-read"] = ( "Cn", "The CPU clock was not read during a timed pass." ),
      ["recall-differs"] = ( "Rd", "Recall differs between runs by at least one hit." ),
      ["setup-changed"] = ( "St", "The recorded setup of this engine differs between the sessions." ),
      ["one-session"] = ( "Os", "The recorded setup of this engine differs between the sessions, so one session is shown and the row is not ranked." ),
   };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Every fixed text above, flag legends included, keyed by its name, so a test can walk them all.
   /// </summary>
   public static IReadOnlyDictionary<string, string> All { get; } = BuildAll();

   /// <summary>
   /// The two-letter marker for a flag code; "??" for a code this page has no description for, so a
   /// flag the page does not know still shows.
   /// </summary>
   /// <param name="code">Flag code (the kind the consolidate command wrote).</param>
   /// <returns>The marker.</returns>
   public static string Marker( string code )
   {
      return FLAGS.TryGetValue( code, out ( string Marker, string Legend ) flag ) ? flag.Marker : "??";
   }

   /// <summary>
   /// The legend of a flag code, or <see cref="FLAG_UNKNOWN"/> for a code the page does not describe.
   /// </summary>
   /// <param name="code">Flag code.</param>
   /// <returns>One sentence.</returns>
   public static string Legend( string code )
   {
      return FLAGS.TryGetValue( code, out ( string Marker, string Legend ) flag ) ? flag.Legend : FLAG_UNKNOWN;
   }

   /// <summary>
   /// Where a flag code sits in the flags list: the order of the table above, unknown codes last.
   /// </summary>
   /// <param name="code">Flag code.</param>
   /// <returns>The position.</returns>
   public static int Order( string code )
   {
      int index = FLAGS.Keys.ToList().IndexOf( code );
      return index < 0 ? int.MaxValue : index;
   }

   /// <summary>
   /// The flag codes the page describes.
   /// </summary>
   public static IReadOnlyCollection<string> KnownFlagCodes => FLAGS.Keys;

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Collects the public constants of this class and the flag legends into one dictionary.
   /// Why by reflection: a constant added above is then in the test without anyone remembering to list it.
   /// </summary>
   /// <returns>Name to text.</returns>
   private static IReadOnlyDictionary<string, string> BuildAll()
   {
      var all = new Dictionary<string, string>( StringComparer.Ordinal );
      foreach( System.Reflection.FieldInfo field in typeof( BenchLegends ).GetFields( System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static ) )
      {
         if( field.IsLiteral && field.GetRawConstantValue() is string text )
         {
            all[field.Name] = text;
         }
      }

      foreach( KeyValuePair<string, ( string Marker, string Legend )> flag in FLAGS )
      {
         all["FLAG:" + flag.Key] = flag.Value.Legend;
      }

      return all;
   }

   #endregion Private Methods
}
