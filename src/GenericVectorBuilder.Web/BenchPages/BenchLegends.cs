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

   /// <summary>Heading of the column of the metric tables that holds the mode of each engine's search.</summary>
   public const string H_SEARCH_MODE = "Search";

   /// <summary>What the search column of the metric tables is, when the file has a facts table to carry the facts behind it.</summary>
   public const string SEARCH_COLUMN = "The search column gives the mode the report's search fact states for the engine, with the label that fact carries. The facts table below lists each fact with its source.";

   /// <summary>What the search column of the metric tables is, when the file has no facts table.</summary>
   public const string SEARCH_COLUMN_BARE = "The search column gives the mode the report states for the engine's search.";

   /// <summary>Column heading of the engine's CPU per search with one searcher.</summary>
   public const string H_ENGINE_CPU_1 = "Engine CPU per search, one searcher (ms)";

   /// <summary>Column heading of the engine's CPU per search with eight searchers.</summary>
   public const string H_ENGINE_CPU_8 = "Engine CPU per search, eight searchers (ms)";

   /// <summary>Column heading of the client's CPU per search with one searcher.</summary>
   public const string H_CLIENT_CPU_1 = "Client CPU per search, one searcher (ms)";

   /// <summary>Column heading of the client's CPU per search with eight searchers.</summary>
   public const string H_CLIENT_CPU_8 = "Client CPU per search, eight searchers (ms)";

   /// <summary>Column heading of the number of engine CPUs busy with eight searchers.</summary>
   public const string H_ENGINE_CPUS_BUSY = "Engine CPUs busy, eight searchers";

   /// <summary>Heading of the table of changes of recorded setup that the threshold basis does not compare across.</summary>
   public const string H_SETUP_SPLITS = "Changes of recorded setup the threshold basis does not compare across";

   /// <summary>Label before the text the earlier runs recorded, in the table of changes of recorded setup.</summary>
   public const string L_BEFORE = "Before:";

   /// <summary>Label before the text the later runs recorded, in the table of changes of recorded setup.</summary>
   public const string L_AFTER = "After:";

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

   /// <summary>Banner of a folder named published whose file is in an older format or cannot be read: the name says more than the page does.</summary>
   public const string BANNER_PUBLISHED_RECORD = "This folder is named published, but its file is in an older format or cannot be read. The page does not show it as a result.";

   /// <summary>Banner of a blocked folder.</summary>
   public const string BANNER_BLOCKED = "This set is marked blocked by its folder name. It is kept as evidence. The summary page does not use it and its numbers are not the published ones.";

   /// <summary>The cell of a ranked row that is separated from every other engine; the Markdown report says the same word.</summary>
   public const string L_NONE = "none";

   /// <summary>The text of the link at the end of the blocked set's pointer line.</summary>
   public const string L_BLOCKED_CORRECTIONS_LINK = "Summary and all runs";

   /// <summary>The line on the page of a blocked set that points to the corrections: its report is kept as written, with the statements the run pages correct left unmarked.</summary>
   public const string BLOCKED_CORRECTIONS = "This report is kept as written. Statements the run pages mark as false, misleading or unbacked are printed here unmarked; the corrections are on those run pages and in the summary's list.";

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

   /// <summary>Words after a report's file name, above the count of retired sentences it still holds, in the raw files list of a page that left them out of its copy.</summary>
   public const string RAW_UNEDITED = "is kept unedited. It still holds the sentences this page leaves out of its copy. Count:";

   /// <summary>First sentence above the copy of a run's report: the copy is the run's text.</summary>
   public const string RUN_REPORT_AS_WRITTEN = "This report is printed as the run wrote it, except for any sentence a note above it says was left out.";

   /// <summary>Second sentence above the copy of a run's report: what the page did not check.</summary>
   public const string RUN_REPORT_UNCHECKED = "This page does not check the engine texts in it. The summary page's facts table gives the label of each fact it uses.";

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

   /// <summary>Title of the list of ordered pairs that clear the line by a hair.</summary>
   public const string L_ON_LINE = "Ordered pairs on the line";

   /// <summary>Between the two engines of an ordered pair: the first is ahead of the second.</summary>
   public const string L_AHEAD_OF = "ahead of";

   /// <summary>Before the session an unconfirmed order held in.</summary>
   public const string L_HELD_IN = "held in";

   /// <summary>Heading of the list of fields of the file the page does not read.</summary>
   public const string H_UNREAD = "Fields this page does not read";

   /// <summary>Notice above the list of fields of the file the page does not read.</summary>
   public const string UNREAD = "The file holds these fields, and this page does not read them. Anything they say is not on this page.";

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

   /// <summary>Heading of the block on a run page that lists the recorded statements a set marks as false, misleading or not backed by a saved source.</summary>
   public const string H_RUN_NOTES = "Corrections to statements recorded in this run";

   /// <summary>What the corrections block of a run page is: the run's own text stays as written, and the markers show where each statement sits in it.</summary>
   public const string RUN_NOTES_INTRO = "This run's report is kept as it was written. A set that uses this run states that the recorded statements below are false, misleading or unbacked. Each is marked in the report text.";

   /// <summary>Printed when the list of corrections cannot be read; the page never takes an unreadable list for an empty one.</summary>
   public const string RUN_NOTES_UNREADABLE = "The list of corrections could not be read, so this page may print statements that a set marks as false, misleading or not backed by a saved source:";

   /// <summary>Printed on the summary when the set lists no corrections to recorded statements and the list file is absent: the run pages then print every recorded statement as written, and a reader is told so.</summary>
   public const string NO_RUN_NOTES = "This set lists no corrections to recorded statements and the list file is absent, so the run pages print every recorded statement as written.";

   /// <summary>Label of a correction of a statement the set contradicts.</summary>
   public const string L_NOTE_FALSE = "False:";

   /// <summary>Label of a correction of a statement that gives a wrong picture without being contradicted.</summary>
   public const string L_NOTE_MISLEADING = "Misleading:";

   /// <summary>Label of a correction of a statement that cites a figure or a reading no saved source backs; it is not shown to be wrong.</summary>
   public const string L_NOTE_UNBACKED = "Not backed by a saved source:";

   /// <summary>Label before the statement as the run recorded it.</summary>
   public const string L_NOTE_RECORDED = "Recorded:";

   /// <summary>Label before the place the list of corrections came from.</summary>
   public const string L_NOTE_LISTED_IN = "Listed in";

   /// <summary>Words before the count of lines of the report text that carry the marker of a correction.</summary>
   public const string L_NOTE_PLACES = "Marked in the report text, lines:";

   /// <summary>Printed when a listed statement is not in the report text of the run.</summary>
   public const string L_NOTE_NOT_FOUND = "This statement was not found in the report text below.";

   /// <summary>Printed above the raw files list of a run page that carries corrections: the raw files hold the recorded statements as written.</summary>
   public const string RAW_NOTES_UNEDITED = "The raw files below hold the recorded statements that the corrections above refer to, as written.";

   /// <summary>What a row of a metric table marked not held is.</summary>
   public const string NOT_HELD = "A row marked not held has figures from a timed pass the tool recorded as not held: the engine was still changing when it was timed. The row is shown and is not ranked.";

   /// <summary>Label of the group of rows of a metric table that are shown and not ranked.</summary>
   public const string H_NOT_RANKED = "Not ranked";

   /// <summary>Text of the separation cell of a row that is not ranked because its pass was not held.</summary>
   public const string L_NOT_HELD = "not held, not ranked";

   /// <summary>First words of the header of the Vector search benchmark page; the data set's name follows.</summary>
   public const string L_HEADER_FROM = "These benchmarks were taken from the";

   /// <summary>Word after the data set's name in the header.</summary>
   public const string L_HEADER_DATASET = "data set";

   /// <summary>Words between the row count and the dimension count in the header's description of the data.</summary>
   public const string L_HEADER_VECTORS_OF = "vectors of";

   /// <summary>Word after the dimension count in the header's description of the data.</summary>
   public const string L_HEADER_DIMENSIONS = "dimensions";

   /// <summary>Words before the queries in the header's first sentence.</summary>
   public const string L_HEADER_AND_FROM = "and from the";

   /// <summary>End of the header's first sentence: what the data set and the queries were used for.</summary>
   public const string L_HEADER_RAN = "which ran the speed test.";

   /// <summary>Name of a query set the file records as "golden".</summary>
   public const string L_HEADER_GOLDEN = "golden questions";

   /// <summary>Name of a query set the file records as "random".</summary>
   public const string L_HEADER_RANDOM = "random stored vectors";

   /// <summary>Name of a query set the file records under any other kind.</summary>
   public const string L_HEADER_QUERIES = "queries";

   /// <summary>Words before the path of the file the queries were read from.</summary>
   public const string L_HEADER_QUESTIONS_FROM = "The queries are read from";

   /// <summary>Printed in the header when the file holds no data line, so the data set and the queries are not named.</summary>
   public const string L_HEADER_NO_DATA = "The file records no data line, so the data set and the queries are not named here.";

   /// <summary>First words of the heading over the order of the engines; the number of searchers follows.</summary>
   public const string L_HEADER_MULTI_A = "Multi search,";

   /// <summary>Rest of the heading over the order of the engines.</summary>
   public const string L_HEADER_MULTI_B = "searchers at once: database engines in order";

   /// <summary>What the order of the engines is, as the header states it.</summary>
   public const string L_HEADER_ORDER = "Most searches per second first.";

   /// <summary>Words before the number of runs the searches per second are the median of.</summary>
   public const string L_HEADER_MEDIAN_A = "Searches per second is the median of the";

   /// <summary>Word after the number of runs.</summary>
   public const string L_HEADER_MEDIAN_B = "runs.";

   /// <summary>How the ms per search of the header is made, and that it is not a measured latency.</summary>
   public const string L_HEADER_MS = "The ms per search is worked out as one second divided by the searches per second, so it is not a timed latency.";

   /// <summary>What each difference of the header is taken from.</summary>
   public const string L_HEADER_DIFF = "Each difference is from the row above it.";

   /// <summary>Column heading of the engine's name.</summary>
   public const string L_HEADER_COL_ENGINE = "Engine";

   /// <summary>Column heading of the searches per second.</summary>
   public const string L_HEADER_COL_QPS = "Searches per second";

   /// <summary>Column heading of the ms per search worked out from the searches per second.</summary>
   public const string L_HEADER_COL_MS = "ms per search";

   /// <summary>Column heading of the difference in ms per search from the row above.</summary>
   public const string L_HEADER_COL_DIFF = "Difference from the row above, ms per search";

   /// <summary>Printed in the difference cell of the first row, which has no row above it.</summary>
   public const string L_HEADER_FIRST = "first in the list";

   /// <summary>Marker on a row that the report's test does not separate from the row above it.</summary>
   public const string L_HEADER_WITHIN = "within run-to-run spread of the row above";

   /// <summary>What the marker on a row means, and what the order of such rows is.</summary>
   public const string L_HEADER_WITHIN_LEGEND = "A row marked within run-to-run spread is not separated from the row above by the report's test. The order shown is the order of the medians.";

   /// <summary>Printed after a note that the file binds to an engine's row, such as that it holds its data in memory.</summary>
   public const string L_HEADER_NOTE_KEEP = "Read this row with that in mind.";

   /// <summary>Label of a row that has one session of figures and is not ranked.</summary>
   public const string L_HEADER_ONE_SESSION = "one session, not ranked";

   /// <summary>Where the figures of the rows that are not ranked are.</summary>
   public const string L_HEADER_FIGURES_IN_FULL = "The figures of a row that is not ranked are in the full results.";

   /// <summary>Text of the link from the header to the full results page.</summary>
   public const string L_HEADER_FULL = "Full results: every table, the flags and every run";

   /// <summary>Words before the builds that measured the runs, in the small text of the header.</summary>
   public const string L_HEADER_BUILD = "Build that measured the runs:";

   /// <summary>Printed for a session whose runs record no build.</summary>
   public const string L_HEADER_NOT_RECORDED = "not recorded";

   /// <summary>Printed when the file records no build.</summary>
   public const string L_HEADER_NO_BUILD = "No build is recorded in this file.";

   /// <summary>Word before the name of the set in the small text of the header.</summary>
   public const string L_HEADER_SET = "Set:";

   /// <summary>Printed in the header of a set that is marked stopped.</summary>
   public const string L_HEADER_STOPPED = "This set is marked stopped, so no order is shown. The stopped block in the full results gives the reason.";

   /// <summary>Words before the name of the table the header needs and the file does not hold.</summary>
   public const string L_HEADER_NO_TABLE_A = "The file holds no table named";

   /// <summary>Words after the name of the table the header needs and the file does not hold.</summary>
   public const string L_HEADER_NO_TABLE_B = "so no order is shown.";

   /// <summary>Printed when the table of the header holds no ranked row.</summary>
   public const string L_HEADER_NO_RANKED = "The table holds no ranked row, so no order is shown.";

   /// <summary>Words before the reason the header could not be built.</summary>
   public const string L_HEADER_ERROR = "The order of the engines could not be built";

   /// <summary>Printed on the Vector search benchmark page when no set has been published.</summary>
   public const string L_BENCHMARK_NONE = "No published benchmark results yet.";

   /// <summary>First words of the sentence on the Golden Questions page that says what the questions are; the number of questions follows.</summary>
   public const string L_GOLDEN_INTRO_A = "These are the";

   /// <summary>Rest of that sentence, after the number of questions.</summary>
   public const string L_GOLDEN_INTRO_B = "questions every engine answered in the speed test.";

   /// <summary>Column heading of the question's number on the Golden Questions page.</summary>
   public const string L_GOLDEN_COL_NUMBER = "#";

   /// <summary>Column heading of the question's text.</summary>
   public const string L_GOLDEN_COL_QUESTION = "Question";

   /// <summary>Column heading of the question's type, the category the questions file gives it.</summary>
   public const string L_GOLDEN_COL_TYPE = "Type";

   /// <summary>Column heading of the files a correct answer should return, the relevant files the questions file lists.</summary>
   public const string L_GOLDEN_COL_FILES = "Files a correct answer should return";

   /// <summary>Words before the path of the file the Golden Questions page read the questions from.</summary>
   public const string L_GOLDEN_FROM = "The questions are read from";

   /// <summary>Printed after the path when the file's hash is the one the runs recorded.</summary>
   public const string L_GOLDEN_HASH_MATCH = "Its hash is the one the runs recorded.";

   /// <summary>Words before the name of the benchmark page, in the link back to it from the Golden Questions page.</summary>
   public const string L_GOLDEN_BACK = "Back to the";

   /// <summary>Words before the reason the Golden Questions page could not show the questions.</summary>
   public const string L_GOLDEN_UNREADABLE = "The questions could not be shown";

   /// <summary>Printed on the Golden Questions page when the published set records no file of questions.</summary>
   public const string L_GOLDEN_NO_FILE = "The published set records no file of questions, so there are no questions to show.";

   /// <summary>First words of the notice that the questions file is not the file the runs read; the file's hash follows.</summary>
   public const string L_GOLDEN_CHANGED_A = "The questions file is not the file the runs read, so no questions are shown. Its hash starts";

   /// <summary>Words between the file's hash and the hash the runs recorded.</summary>
   public const string L_GOLDEN_CHANGED_B = "and the runs recorded";

   /// <summary>First words of the notice that the questions file holds another number of questions than the runs ran; the file's number follows.</summary>
   public const string L_GOLDEN_COUNT_A = "The questions file holds";

   /// <summary>Words between the number of questions the file holds and the number the runs recorded.</summary>
   public const string L_GOLDEN_COUNT_B = "questions and the runs recorded";

   /// <summary>End of that notice, after the number the runs recorded.</summary>
   public const string L_GOLDEN_COUNT_C = "queries, so no questions are shown.";

   /// <summary>Printed on the Golden Questions page when the questions file holds no question.</summary>
   public const string L_GOLDEN_EMPTY = "The questions file holds no question, so there are none to show.";

   /// <summary>Summary line of the block that lists the clauses of the engine texts that no sentence of the page prints.</summary>
   public const string H_RECORDED_TEXTS = "Recorded engine texts, clause by clause";

   /// <summary>What the clause table is: every clause of a recorded text that a sentence above does not already print, with its class and basis.</summary>
   public const string RECORDED_TEXTS = "Each clause of a recorded text that no sentence on this page prints, with the class the report gives it and what it is bound to.";

   /// <summary>Summary line of the block on the summary page that lists the corrections the run pages print; the count follows it.</summary>
   public const string H_RUN_NOTES_SUMMARY = "Corrections printed on the run pages, count:";

   /// <summary>What the block of corrections on the summary page is.</summary>
   public const string RUN_NOTES_SUMMARY = "Each recorded statement below is false, misleading or unbacked, as the correction says. The page of the run it was recorded in marks it and prints this correction with its sources.";

   private static readonly Dictionary<string, ( string Marker, string Legend )> FLAGS = new( StringComparer.Ordinal )
   {
      ["spread"] = ( "Sp", "The runs of this engine differ from each other by more than the spread limit named in the evidence." ),
      ["p50-mean-inconsistent"] = ( "Pm", "The p50 and the mean from the one-searcher pass disagree by more than the limit named in the evidence." ),
      ["unsettled-target"] = ( "Un", "The engine did not pass its own settle check before a timed pass. The evidence names the check." ),
      ["unsettled"] = ( "Un", "The run recorded the engine as not settled before its timed passes. The evidence names the run." ),
      ["busy-box"] = ( "Bz", "CPUs outside the benchmark were busy during the timed pass. The evidence gives the figure." ),
      ["governor-not-performance"] = ( "Gv", "The CPU governor was not performance during a timed pass. The evidence gives the governor." ),
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
      ["throttle-rise"] = ( "Th", "The CPU's thermal throttle counters rose between the start and the end of this engine's turn in the run. The evidence gives the rise." ),
      ["setup-changed"] = ( "St", "The recorded setup of this engine differs between the sessions." ),
      ["one-session"] = ( "Os", "The recorded setup of this engine differs between the sessions, so one session is shown and the row is not ranked." ),
      ["not-held"] = ( "Nh", "The run recorded a timed pass of this engine as NOT HELD: the engine was still changing when it was timed. The evidence gives the figures. The row is shown, not ranked." ),
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
