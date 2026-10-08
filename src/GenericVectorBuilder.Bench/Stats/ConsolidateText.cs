using System.Globalization;
using GenericVectorBuilder.Bench.Report;
using S = GenericVectorBuilder.Bench.Stats.ConsolidateSources;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// Every sentence consolidated.md and the summary page print, from the templates in this file and
/// nowhere else, each emitted with the sources its numbers and facts come from (contract:
/// sentences[] of consolidated.json). <see cref="ConsolidateAudit"/> checks every one against the
/// raw files before anything is written.
/// Template rules: no provenance, purpose, default or "only" words, no cause words (P5's
/// <see cref="BannedWords"/>), at most 34 words in the headline and 35 elsewhere, every number
/// bound to a source. Recorded texts (durability, the method notes, ClickHouse's disk note, the
/// dropped clock warnings, the dockerd accounting rule) are quoted verbatim as quote sentences,
/// checked against their raw field and outside the word rules.
/// Slots, in reading order, by the sections the summary page places (P4's BenchSlots): headline;
/// stopped.*; subtitle.* (data, machine, scope, sessions, questions or queries); rule.*; basis.* (runs, identity, the largest move,
/// the moves per metric, splits and split.N.fields, moves and conditions, the exclusions); table.&lt;metric&gt;.*; recall.*;
/// why.* (framing, costs, settings.TARGET, effort.TARGET with its labels and clauses, noeffort.TARGET); drift.* (the medians, unconfirmed, close, onLine);
/// disclosure.* (disclosure.clock.* first, the rule of the median before its use; disclosure.governor and disclosure.engines.N; busy; durability with logs, nologs,
/// TARGET with its labels and clauses, TARGET.correction; graph.TARGET; observer, observer.clock and timers for the newest session and the same with a
/// session suffix for an older one); method.* (the intro, then labels and clauses, the notices of clauses not printed and the sentence from the saved tie check);
/// notinreport.*; runs.* (runs.reuse.SESSION for every session with a blocking verdict, runs.stale.*).
/// A recorded text is printed clause by clause (see <see cref="ClauseWriter"/>): a head sentence (durability and effort), then for each group of clauses
/// a label sentence at TEXT.gN that says how the group is backed (read back, measured, documented, unverified), then each clause as a verbatim quote at
/// TEXT.gN.qM; a clause not printed is replaced by a sentence at TEXT.dropped.M or TEXT.correction.
/// A row's note (row.&lt;metric&gt;.&lt;target&gt;.note) and its flags (flag.&lt;metric&gt;.&lt;target&gt;.&lt;n&gt;) are
/// audited like every other sentence and then travel on the row itself (rows[].note with noteSources,
/// flags[].text with sources), which is where the page prints them, so no sentence is printed twice.
/// Why one file: a reader lens must be able to read every fixed word the report can print.
/// </summary>
public static class ConsolidateText
{
   #region Data Members

   /// <summary>Headline when one engine is named.</summary>
   public const string HEADLINE_ONE = "{0} had the lowest p50 latency: every other engine took at least {1} times as long, in each session{2}.";

   /// <summary>Headline when two or more engines are named.</summary>
   public const string HEADLINE_MANY = "{0} had the lowest p50 latencies, in that order: each engine further down took at least {1} times as long, in each session{2}.";

   /// <summary>Headline when the named list would not fit the word limit.</summary>
   public const string HEADLINE_COUNT = "The {0} engines at the top of the p50 table had the lowest p50 latencies, in table order: each one further down took at least {1} times as long, in each session.";

   /// <summary>Headline when no engine is ahead of every other.</summary>
   public const string HEADLINE_NONE = "No engine had a p50 latency at least {0} times lower than every other engine in each session; the p50 table shows which pairs this test separated.";

   /// <summary>Headline in one-session mode.</summary>
   public const string HEADLINE_ONE_SESSION = "One session of {0} runs: rows are not ranked, and this report names no engine as ahead of another.";

   /// <summary>The in-memory clause added to a headline that names Redis.</summary>
   public const string HEADLINE_MEMORY = "; {0} holds its data in memory";

   /// <summary>Stop sentence for G2.</summary>
   public const string STOPPED_G2 = "This report stopped: an order that held in {0} had its medians the other way round in {1}, so it names no engine as ahead of another.";

   /// <summary>One G2 pair.</summary>
   public const string STOPPED_G2_PAIR = "{0}: {1} was ahead of {2} in {3}, and in {4} the medians were {5} for {1} and {6} for {2}.";

   /// <summary>Stop sentence for G3.</summary>
   public const string STOPPED_G3 = "This report stopped: {0} targets changed their recorded setup between {1} and {2}, more than {3}, so it names no engine as ahead of another.";

   /// <summary>Subtitle: the data.</summary>
   public const string SUBTITLE_DATA = "Data: {0}, {1} vectors of {2} dimensions; {3} queries, top {4} hits each.";

   /// <summary>Subtitle: the machine.</summary>
   public const string SUBTITLE_MACHINE = "Machine: {0}, {1} logical CPUs, {2} GiB of RAM.";

   /// <summary>Subtitle: what a figure is at a small collection (design O14: per-request cost, client library included, not index scaling).</summary>
   public const string SUBTITLE_SCOPE_SMALL = "At {0} vectors each figure is the cost of one request through the benchmark's client for that engine, and does not show how an index scales.";

   /// <summary>The start of the text of a protocol fact that names the benchmark's own REST client.</summary>
   public const string HTTP_CLIENT = "HttpClient";

   /// <summary>Subtitle: the engines the benchmark reaches with its own REST code.</summary>
   public const string SUBTITLE_CLIENTS = "Of the {0} engines, {1} are reached through the benchmark's own HttpClient REST code: {2}.";

   /// <summary>Subtitle: the runs of a session that record no question-file hash.</summary>
   public const string SUBTITLE_TRUTH = "The runs of {0} record no question-file hash, and truthNdcg reads {1} in all {2} claim runs.";

   /// <summary>Subtitle: the size a larger collection's order applies to.</summary>
   public const string SUBTITLE_SCOPE_LARGE = "At {0} vectors each order applies to this size alone.";

   /// <summary>Subtitle: one session's runs.</summary>
   public const string SUBTITLE_SESSION = "Session {0}: runs {1}, started from {2} to {3}.";

   /// <summary>Subtitle: the build that measured a session, by commit.</summary>
   public const string SUBTITLE_BUILD_MEASURED = "The runs of {0} were measured by build {1}.";

   /// <summary>Subtitle: the consolidating build is one that measured a session.</summary>
   public const string SUBTITLE_BUILD_SAME = "This report was consolidated by build {0}, which also measured the runs of {1}.";

   /// <summary>Subtitle: the consolidating build measured none of the sessions with a recorded build.</summary>
   public const string SUBTITLE_BUILD_OTHER = "This report was consolidated by build {0}, not by the build that measured the runs of {1}.";

   /// <summary>Subtitle: the consolidating build, when no session records a commit to compare it with.</summary>
   public const string SUBTITLE_BUILD_ONLY = "This report was consolidated by build {0}.";

   /// <summary>Subtitle: the consolidating build carries no commit.</summary>
   public const string SUBTITLE_BUILD_NONE = "The build that consolidated this report carries no commit.";

   /// <summary>Subtitle: the question file of the runs that recorded its SHA-256.</summary>
   public const string SUBTITLE_QUESTIONS = "The runs of {0} read a question file with the same SHA256 hash as {1}.";

   /// <summary>The repository file whose code line computes the question file's hash.</summary>
   public const string HASH_FILE = "src/GenericVectorBuilder.Bench/Data/GoldenQueries.cs";

   /// <summary>The token on that line.</summary>
   public const string HASH_TOKEN = "Convert.ToHexString( SHA256.HashData( bytes ) ).ToLowerInvariant()";

   /// <summary>The claim rule.</summary>
   public const string THRESHOLD_RULE = "An engine is shown ahead of another when, in each session separately, its slowest run beat the other's fastest run by at least {0} times; every other pair is not separated by this test.";

   /// <summary>How the threshold was set.</summary>
   public const string THRESHOLD_TBP = "The threshold is {0}%: the smallest multiple of {1}% at least {2}% above the largest move in the basis, {3}%, and never below {4}%.";

   /// <summary>What the basis holds.</summary>
   public const string THRESHOLD_BASIS = "The basis holds the {0} runs of sessions {1}, all with machine control on.";

   /// <summary>Which basis runs held the clock, by session.</summary>
   public const string THRESHOLD_CLOCK_BOTH = "The clock was held in the runs of {0} and not in those of {1}.";

   /// <summary>Every basis run held the clock.</summary>
   public const string THRESHOLD_CLOCK_ALL = "The clock was held in every basis run.";

   /// <summary>No basis run held the clock.</summary>
   public const string THRESHOLD_CLOCK_NONE = "The clock was held in no basis run.";

   /// <summary>Every run of some sessions held the clock, and the rest are in between.</summary>
   public const string THRESHOLD_CLOCK_EVERY_OF = "The clock was held in every basis run of {0}.";

   /// <summary>No run of some sessions held the clock, and the rest are in between.</summary>
   public const string THRESHOLD_CLOCK_NONE_OF = "The clock was held in no basis run of {0}.";

   /// <summary>A session of which only some basis runs held the clock.</summary>
   public const string THRESHOLD_CLOCK_SOME = "The clock was held in {0} of the {1} basis runs of {2}.";

   /// <summary>Moves are rounded up: the largest, unrounded and as printed.</summary>
   public const string THRESHOLD_ROUNDING = "Each move is rounded up to the next basis point before it is printed: the largest, {0}% unrounded, prints as {1}%.";

   /// <summary>The largest move.</summary>
   public const string THRESHOLD_MAX = "The largest move was {0}%, the {1} of {2} between runs {3} (seed {4}) and {5} (seed {6}).";

   /// <summary>The largest move includes a pass its run recorded as NOT HELD; what the basis gives without the engine of that pass.</summary>
   public const string THRESHOLD_MAX_WITHOUT = "The largest move includes a NOT HELD pass; with {0} left out of the basis, the largest move is {1}%, the {2} of {3}, and the threshold would be {4}%.";

   /// <summary>The differences between the two runs of the largest move.</summary>
   public const string THRESHOLD_MAX_DIFFERENCES = "Those two runs differ in {0}.";

   /// <summary>When the two runs record no difference.</summary>
   public const string THRESHOLD_MAX_SAME = "Those two runs record no difference in turbo, uncore, warm-up, rehearsal, build or pass clock.";

   /// <summary>What the basis compares: runs of one recorded setup. Why stated: a change of setup hides its moves, and the splits below list them.</summary>
   public const string THRESHOLD_IDENTITY = "Basis moves are taken between runs in which the engine recorded the same setup: its engine text, index text, search settings, durability text, engine files, engine settings, image id and hosting.";

   /// <summary>A field one run did not record. Why the exception: the engine text and the index text are compared strictly (a run that did not record one differs from a run that did), the other fields only when both runs recorded them.</summary>
   public const string THRESHOLD_IDENTITY_MISSING = "A field other than the engine text and the index text that one of two runs did not record is not compared.";

   /// <summary>Per metric, basis runs of one recorded setup.</summary>
   public const string THRESHOLD_ALL = "{0}, between basis runs of one recorded setup: largest moves {1} for one engine and {2} for a pair.";

   /// <summary>Per metric, runs at the same conditions as well.</summary>
   public const string THRESHOLD_SAME = "{0}, between those runs that also share turbo, uncore, warm-up and rehearsal: largest moves {1} for one engine and {2} for a pair.";

   /// <summary>The setup splits, counted.</summary>
   public const string THRESHOLD_SPLITS = "Changes of recorded setup left out of the basis moves: {0}, in {1}.";

   /// <summary>What each split is listed with.</summary>
   public const string THRESHOLD_SPLITS_EACH = "Each is listed with the field that changed, the move it hides and the threshold it would give.";

   /// <summary>No split.</summary>
   public const string THRESHOLD_SPLITS_NONE = "No engine recorded a different setup in some basis runs than in others.";

   /// <summary>One split: the fields that changed.</summary>
   public const string SPLIT_FIELDS = "Between its {1} runs and its {2} runs, {0} recorded a different {3}.";

   /// <summary>One split: the conditions the two runs of its one-engine move also differ in, so the move is not read as the change of setup alone.</summary>
   public const string SPLIT_CONDITIONS = "The two runs of that one-engine move also differ in {0}.";

   /// <summary>One split: the moves it hides and the threshold they would give.</summary>
   public const string SPLIT_MOVES = "Counted across that change, the largest one-engine move is {0} and the largest pair move {1}, and the threshold would be {2}%.";

   /// <summary>One move inside a sentence.</summary>
   public const string MOVE = "{0}% ({1})";

   /// <summary>No move.</summary>
   public const string MOVE_NONE = "none";

   /// <summary>An exclusion row that covers every metric.</summary>
   public const string EVERY_METRIC = "every metric";

   /// <summary>A one-engine move's noun.</summary>
   public const string MOVE_VALUE = "{0} value";

   /// <summary>A pair move's noun.</summary>
   public const string MOVE_RATIO = "{0} ratio";

   /// <summary>The repository file whose code line names the uncore limit MSR.</summary>
   public const string MSR_FILE = "src/GenericVectorBuilder.Bench/Running/MachineControlSampler.cs";

   /// <summary>The token on that line.</summary>
   public const string MSR_TOKEN = "LIMIT_MSR = \"0x620\"";

   /// <summary>The repository file whose code line fixes the MHz one step of the core ratio is.</summary>
   public const string RATIO_STEP_FILE = "src/GenericVectorBuilder.Bench/Stats/ConsolidateClock.cs";

   /// <summary>The token on that line.</summary>
   public const string RATIO_STEP_TOKEN = "public const int RATIO_STEP_MHZ = 100;";

   /// <summary>The index state after the load.</summary>
   public const string AFTER_LOAD = "after the load";

   /// <summary>The index state after the searches.</summary>
   public const string AFTER_SEARCH = "after the searches";

   /// <summary>An exclusion row.</summary>
   public const string THRESHOLD_EXCLUSION = "{0} {1}, seed list {2}, is left out of the basis (kind: {3}); {4}, item {5}, holds the words: {6}.";

   /// <summary>An exclusion kind kept in.</summary>
   public const string THRESHOLD_KEPT = "With the {0} rows kept in, the largest moves would be {1}% for one engine and {2}% for a pair, and the threshold {3}%.";

   /// <summary>The runs without machine control.</summary>
   public const string THRESHOLD_NO_CONTROL = "In the {0} runs without machine control, one engine moved up to {1}% and a pair up to {2}%; the threshold rests on runs with machine control on.";

   /// <summary>The left-out runs.</summary>
   public const string THRESHOLD_LEFT_OUT = "{0} other runs of this pipeline are not in the basis; each is listed with its reason.";

   /// <summary>The p50 table.</summary>
   public const string TABLE_P50 = "p50 is the median latency of one searcher's searches, in ms; rows are in order of the median of {0} runs.";

   /// <summary>The QPS@1 table.</summary>
   public const string TABLE_QPS1 = "QPS@1 is searches per second of the same one-searcher pass as p50, so it is no second confirmation of the p50 order.";

   /// <summary>The QPS@8 table.</summary>
   public const string TABLE_QPS8 = "QPS@8 is searches per second with {0} searchers at once.";

   /// <summary>The exact table.</summary>
   public const string TABLE_EXACT = "Exact p50 is the median latency of each engine's own exact mode, which is a different operation per engine; the why table's CPU figures come from the other passes.";

   /// <summary>A row shown and not ranked because its timed pass was recorded NOT HELD.</summary>
   public const string TABLE_NOT_HELD = "{0} is shown and not ranked in this table: its timed {1} pass was recorded NOT HELD in {2} of the {3} runs.";

   /// <summary>A target without an exact pass whose search fact says exact.</summary>
   public const string TABLE_EXACT_ABSENT_EXACT = "{0} has no exact pass; its search fact says: {1}.";

   /// <summary>A target without an exact pass whose search fact says approximate.</summary>
   public const string TABLE_EXACT_ABSENT = "{0} has no exact pass in these runs.";

   /// <summary>The one-session caption in two-session mode.</summary>
   public const string TABLE_ONE_SESSION_ROWS = "A one-session row shows the {0} runs of {1} and is not separated from any row.";

   /// <summary>The caption in one-session mode.</summary>
   public const string TABLE_ONE_SESSION_MODE = "With one session, the lists of rows not separated hold for {0} alone, and no row is ranked.";

   /// <summary>
   /// The Redis row note: the docs quote and what its compose file sets beside it. Worded apart from <see cref="DISCLOSURE_REDIS"/> on purpose: the note is
   /// printed under every table and the disclosure once, and the page checks that each sentence of the file is printed as often as it is listed.
   /// </summary>
   public const string ROW_MEMORY = "{0} holds its data in memory: its saved docs page says '{1}', and its compose file sets {2} and {3}.";

   /// <summary>The Redis row note when the compose settings are not among the facts.</summary>
   public const string ROW_MEMORY_DOCS = "{0} holds its data in memory: its saved docs page says '{1}'.";

   /// <summary>Flag: busy box.</summary>
   public const string FLAG_BUSY = "Run {0}: busy box during the {1} pass, with {2} CPUs of outside load.";

   /// <summary>Flag: clock off.</summary>
   public const string FLAG_CLOCK_OFF = "Run {0}: the {1} pass read {2} MHz on the engine CPUs and {3} MHz on the client CPUs, against a pinned {4} MHz.";

   /// <summary>Flag: clock not read.</summary>
   public const string FLAG_CLOCK_NOT_READ = "Run {0}: no clock reading on a CPU group during the {1} pass.";

   /// <summary>Flag: governor.</summary>
   public const string FLAG_GOVERNOR = "Run {0}: the {1} pass ran under the {2} governor.";

   /// <summary>Flag: throttle counters rose.</summary>
   public const string FLAG_THROTTLE = "Run {0}: the thermal throttle counters rose during {1}, by {2} on the cores and {3} on the package.";

   /// <summary>Flag: unsettled, when the run's warning names no pass (an older run), so every pass of the target carries it.</summary>
   public const string FLAG_UNSETTLED = "Run {0}: {1} was recorded as not settled before its timed passes.";

   /// <summary>Flag: unsettled, for the pass the run's warning names.</summary>
   public const string FLAG_UNSETTLED_PASS = "Run {0}: {1} was recorded as not settled before its timed {2} pass.";

   /// <summary>Flag: the timed pass itself was recorded NOT HELD.</summary>
   public const string FLAG_NOT_HELD = "Run {0}: the timed {1} pass of {2} read {3}, {4}% from the settled {5} (limit {6}%), and the run recorded it as NOT HELD.";

   /// <summary>Flag: index not ready.</summary>
   public const string FLAG_INDEX = "Run {0}: {1}'s index state {2} read not ready, {3} of {4} vectors indexed.";

   /// <summary>Flag: segment layout.</summary>
   public const string FLAG_LAYOUT = "Run {0}: {1}'s index state after the searches reads {2}.";

   /// <summary>Flag: search errors.</summary>
   public const string FLAG_SEARCH_ERRORS = "Run {0}: {1} timed searches of {2} failed.";

   /// <summary>Flag: warm-up errors.</summary>
   public const string FLAG_WARMUP_ERRORS = "Run {0}: {1} untimed warm-up searches of {2} failed.";

   /// <summary>Flag: one session.</summary>
   public const string FLAG_ONE_SESSION = "{0}'s recorded setup differs between {1} and {2} in {3}; its row shows {2} figures and is not ranked.";

   /// <summary>Recall caption.</summary>
   public const string RECALL = "Recall counts hits of the exact top {0} over {1} queries, {2} per run; it is printed, never ranked.";

   /// <summary>Recall hits that the runs did not record and the report derived.</summary>
   public const string RECALL_DERIVED = "The recall hits of {0} are the recall of each run times {1} queries times {2} hits, rounded; those runs record no hit count.";

   /// <summary>Recall differs.</summary>
   public const string RECALL_DIFFERS = "{0}'s recall hits differ between runs: {1}.";

   /// <summary>Why framing, fixed (design section 3).</summary>
   public static readonly IReadOnlyList<string> WHY_FRAMING = new[]
   {
      "CPU per search is cost summed over every thread.",
      "It can exceed the time per search, so it is not a split of the latency.",
      "This test did not isolate causes.",
   };

   /// <summary>Where the costs come from.</summary>
   public const string WHY_COSTS = "Costs are medians over the {0} runs of {1}, and a cost is left blank unless every one of them recorded it.";

   /// <summary>A target's recorded search and index settings.</summary>
   public const string WHY_SETTINGS = "{0} recorded these search and index settings: {1}.";

   /// <summary>An approximate engine that recorded no per-query effort setting.</summary>
   public const string WHY_NO_EFFORT = "{0} recorded no setting for how hard a query searches.";

   /// <summary>The range sentence.</summary>
   public const string WHY_RANGE = "At one searcher, client CPU per search ran from {0} ms ({1}) to {2} ms ({3}).";

   /// <summary>The engine range sentence.</summary>
   public const string WHY_ENGINE_RANGE = "At one searcher, engine CPU per search ran from {0} ms ({1}) to {2} ms ({3}) where measured.";

   /// <summary>When no run recorded engine CPU.</summary>
   public const string WHY_ENGINE_NONE = "The runs of {0} recorded no engine CPU per search.";

   /// <summary>Embedded engines.</summary>
   public const string WHY_EMBEDDED = "Hosting {1} is recorded for {0}: each runs inside the test's own process, so its client CPU per search includes the engine's own work.";

   /// <summary>Drift: medians.</summary>
   public const string DRIFT_MEDIAN = "From {0} to {1}, a ranked figure's session median moved {2}% in the middle case and {3}% at most ({4}, {5}).";

   /// <summary>Drift: a cell the figures above leave out because its pass was recorded NOT HELD, with the move of its session median.</summary>
   public const string DRIFT_EXCLUDED = "The NOT HELD {0} {1} cell is left out of these figures; its session median moved {2}%, with {3} runs at {4} to {5} and {6} runs at {7} to {8}.";

   /// <summary>Drift: unconfirmed orders.</summary>
   public const string DRIFT_UNCONFIRMED = "{0} orders held in {1} and not in {2}, and {3} the other way; they are listed and not published.";

   /// <summary>Drift: close to the line.</summary>
   public const string DRIFT_CLOSE = "{0} unordered pairs sat between {1} and {2} times in each session, always in one direction; they are listed as close to the line.";

   /// <summary>Drift: ordered pairs on the line.</summary>
   public const string DRIFT_ONLINE = "{0} ordered pairs cleared {1} times by {2} bp or less in their lowest session; they are listed as on the line.";

   /// <summary>Drift: one ordered pair on the line.</summary>
   public const string DRIFT_ONLINE_ONE = "{0} ordered pair cleared {1} times by {2} bp or less in its lowest session; it is listed as on the line.";

   /// <summary>Drift: no pair on the line.</summary>
   public const string DRIFT_ONLINE_NONE = "No ordered pair cleared {0} times by {1} bp or less in its lowest session.";

   /// <summary>Drift: what the two sessions differ in besides the engines.</summary>
   public const string DRIFT_SESSIONS = "The sessions differ in {0}, and this test does not separate those from the engines' own drift.";

   /// <summary>Drift: the outside load each session saw, from the passes' own record.</summary>
   public const string DRIFT_OUTSIDE_LOAD = "The median outside load of a pass, per run, was {0} to {1} CPUs in {2} and {3} to {4} in {5}.";

   /// <summary>Drift: scope.</summary>
   public const string DRIFT_SCOPE = "Both sessions ran with the clock held; drift under other conditions is not measured here.";

   /// <summary>Clock held.</summary>
   public const string CLOCK_HELD = "In {0}, every run held the clock: turbo off, MSR {1} at {2}, top ratio {3} (nominally {4} MHz); the median of the kernel's pass medians is {5} MHz; the ceiling before was {6} MHz.";

   /// <summary>Clock held, when the pin is not a whole number of ratio steps.</summary>
   public const string CLOCK_HELD_NO_RATIO = "In {0}, every run held the clock: turbo off, MSR {1} at {2}, pinned at {3} MHz; the median of the kernel's pass medians is {4} MHz; the ceiling before was {5} MHz.";

   /// <summary>Clock not held.</summary>
   public const string CLOCK_NOT_HELD = "In {0}, the runs did not all hold the clock.";

   /// <summary>The median rule, defined where it is first used: per CPU group of the partition.</summary>
   public const string CLOCK_RULE = "The median rule: a pass is off when the median, over its engine CPUs or over its client CPUs, of each CPU's median MHz is more than {0}% from the pinned {1} MHz.";

   /// <summary>The median rule when the partition was not split into engine and client CPUs.</summary>
   public const string CLOCK_RULE_ALL = "The median rule: a pass is off when the median, over all CPUs, of each CPU's median MHz is more than {0}% from the pinned {1} MHz.";

   /// <summary>Clock checked.</summary>
   public const string CLOCK_CHECKED = "In {0}, by the median rule, {1} of {2} passes were off the pinned clock and {3} had a CPU group not read.";

   /// <summary>A dropped mean-based warning.</summary>
   public const string CLOCK_DROPPED = "Run {0} flagged {1}'s {2} pass as off its clock by a mean; the medians read {3} MHz on engine CPUs and {4} MHz on client CPUs, within {5}%.";

   /// <summary>Governor and the client's CPUs.</summary>
   public const string DISCLOSURE_GOVERNOR = "Every claim run used the {0} governor, with the client on CPUs {1}.";

   /// <summary>The CPUs engines of one hosting ran on, from each run's record of where each engine ran (conditions.engines). Why per hosting: an embedded engine runs inside the client process, on the client CPUs.</summary>
   public const string DISCLOSURE_ENGINES = "The {0} engines {1} ran {2}on CPUs {3}.";

   /// <summary>When the runs record only a partition and not where each engine ran.</summary>
   public const string DISCLOSURE_ENGINES_UNKNOWN = "The runs record the engine CPUs as {0} and do not record on which CPUs each engine ran.";

   /// <summary>Boot, both sessions on one boot.</summary>
   public const string DISCLOSURE_BOOT = "Every {0} run recorded boot {1} at {2}, before the first {3} run started at {4}, so {3} ran on that boot too.";

   /// <summary>Boot, a later boot.</summary>
   public const string DISCLOSURE_BOOT_LATER = "Every {0} run recorded boot {1} at {2}, after the first {3} run started at {4}, so the sessions may have run on different boots.";

   /// <summary>Images.</summary>
   public const string DISCLOSURE_IMAGES = "Each of the {0} container targets ran one image id, equal to its pin, in every {1} run; the ids are in the images table.";

   /// <summary>Images tagged before the first run of the first session.</summary>
   public const string DISCLOSURE_IMAGES_BEFORE = "Each of those images was last tagged on this box before the first {0} run started at {1}.";

   /// <summary>Images tagged later.</summary>
   public const string DISCLOSURE_IMAGES_AFTER = "{0} was last tagged on this box at {1}, after the first {2} run started at {3}.";

   /// <summary>Redis.</summary>
   public const string DISCLOSURE_REDIS = "{0} holds its data in memory: its saved docs page says '{1}'; its compose file sets {2} and {3}.";

   /// <summary>Durability intro.</summary>
   public const string DISCLOSURE_DURABILITY = "Each engine's durability text, as run {0} recorded it, follows clause by clause with how each clause is backed; the program wrote the text, and its figures were not re-derived for this report.";

   /// <summary>The head of one engine's durability clauses.</summary>
   public const string DURABILITY_HEAD = "{0}: its recorded durability text, clause by clause, with how each clause is backed.";

   /// <summary>The head of one engine's effort clause.</summary>
   public const string EFFORT_HEAD = "{0}: the clause of its recorded index text that states how hard a query searches, with how each part is backed.";

   /// <summary>A group of clauses the engine or the machine reported in these runs.</summary>
   public const string CLAUSE_READ_BACK = "Read back in these runs from the engine or the machine:";

   /// <summary>A group of clauses read from the running engine by a line of the benchmark's own code.</summary>
   public const string CLAUSE_READ_BY_CODE = "Read from the running engine or the machine by the benchmark's own code, as the cited line shows:";

   /// <summary>A group of clauses this tool measured in these runs.</summary>
   public const string CLAUSE_MEASURED = "Measured by this tool in these runs:";

   /// <summary>A group of clauses a saved documentation page backs.</summary>
   public const string CLAUSE_DOC_PAGE = "Documented on a saved page, not read back from the engine:";

   /// <summary>A group of clauses a saved log or measurement backs.</summary>
   public const string CLAUSE_DOC_LOG = "Backed by a saved log or measurement, not read back from the engine in these runs:";

   /// <summary>A clause backed by a saved log whose setting the newest session's engine settings also read from the running engine; {0} is the session and {1} the settings.</summary>
   public const string CLAUSE_DOC_LOG_AND_SETTINGS = "Backed by a saved log or measurement; the {0} engine settings also list {1}, read from the running engine:";

   /// <summary>The same for a clause set by a line of code or a compose file.</summary>
   public const string CLAUSE_DOC_CODE_AND_SETTINGS = "Set by a line of code or a compose file saved in this repository; the {0} engine settings also list {1}, read from the running engine:";

   /// <summary>The same for a clause documented on a saved page.</summary>
   public const string CLAUSE_DOC_PAGE_AND_SETTINGS = "Documented on a saved page; the {0} engine settings also list {1}, read from the running engine:";

   /// <summary>A group of clauses a code or compose line saved in the repository backs.</summary>
   public const string CLAUSE_DOC_CODE = "Set by a line of code or a compose file saved in this repository, not read back from the engine:";

   /// <summary>A group of clauses a test saved in the repository describes.</summary>
   public const string CLAUSE_DOC_TEST = "Described by a test saved in this repository, which these runs did not run:";

   /// <summary>A group of clauses nothing backs.</summary>
   public const string CLAUSE_UNVERIFIED = "Typed in the program's own text; not read back, measured or backed by a saved source:";

   /// <summary>A group of clauses nothing backs that cite a measurement: the figure or the date of a measurement, which this report does not show.</summary>
   public const string CLAUSE_UNVERIFIED_CITES = "Typed in the program's own text; it cites a measurement that these runs did not repeat, and no saved source backs this clause:";

   /// <summary>A text the list of classes does not cover, printed whole.</summary>
   public const string CLAUSE_UNLISTED = "Not classified by this report's list of recorded clauses, so printed as unverified:";

   /// <summary>A clause about targets this report does not have.</summary>
   public const string DROPPED_ABSENT = "A clause of this note names {0}, which are not targets of this report, so it is not printed.";

   /// <summary>A clause whose figures nothing saved backs.</summary>
   public const string DROPPED_UNBACKED = "A clause of the {0} text is not printed: it cites figures that no saved source backs.";

   /// <summary>A run note's clock claim the observer's readings contradict; {0} is the target, {1} the pass and {2} and {3} how far under the pin the observer found it.</summary>
   public const string DROPPED_CLOCK_HELD = "A clause of the machine control note, that every CPU's clock is held at its ceiling for any engine, is not printed: the observer found {0}'s {1} pass {2}% to {3}% under the pin.";

   /// <summary>The same when no observer reading was given to check the claim.</summary>
   public const string DROPPED_CLOCK_HELD_UNCHECKED = "A clause of the machine control note, that every CPU's clock is held at its ceiling for any engine, is not printed: no clock reading by APERF and MPERF was given to check it.";

   /// <summary>The recall of one collection size in the saved re-run of the effort test.</summary>
   public const string RECALL_LOG = "In the saved re-run of the {0} effort test, recall@{1} at ef {2} on {3} random {4}-dim vectors read {5} to {6}.";

   /// <summary>No saved re-run matches the recorded effort.</summary>
   public const string RECALL_LOG_NONE = "No saved re-run of the {0} effort test matches the ef {1} its runs recorded, so no recall figure is given.";

   /// <summary>What the saved check of the tie rule found in this data.</summary>
   public const string TIE_CHECK = "A saved check of this data found {0} pairs of rows within 1e-5 of identical, and {1} of {2} queries with a row outside the exact top {3} that the tie rule would count.";

   /// <summary>No saved check of the tie rule matches this data.</summary>
   public const string TIE_CHECK_NONE = "No saved check of the tie rule matches this data, so the reason given for the rule is not shown to apply to it.";

   /// <summary>The saved logs behind the measurements in those texts.</summary>
   public const string DISCLOSURE_DURABILITY_LOGS = "Strace logs of measurements cited in those texts are saved for {0}, in {1}, each listed with its hash in {2}.";

   /// <summary>Measurements cited with no saved log.</summary>
   public const string DISCLOSURE_DURABILITY_NOLOGS = "No log is saved for the measurements cited in the durability texts of {0}.";

   /// <summary>A recorded clause a repository file contradicts: it is not printed, and this says why.</summary>
   public const string DISCLOSURE_CORRECTION = "The recorded {0} text says '{3}'; {1} sets {2}. That clause is not printed.";

   /// <summary>A search fact that rests on the engine's own report of segments smaller than another engine's graph point.</summary>
   public const string DISCLOSURE_GRAPH = "{0}'s search fact is the engine's own report; its segments held at most {1} vectors, below the {2} at which {3}'s recorded text says a segment gets a graph, and no graph was checked.";

   /// <summary>How outside load treats dockerd, read from the code.</summary>
   public const string DISCLOSURE_BUSY = "Outside load is busy CPU less this client's and the followed engine cgroups'; dockerd's cgroup is one of them for a compose engine, so its CPU counts as benchmark work, not outside load.";

   /// <summary>Settings.</summary>
   public const string DISCLOSURE_SETTINGS = "The engine settings table lists what run {0} read from each running engine or set from a repository file, as each row's how column says.";

   /// <summary>A cap fact.</summary>
   public const string DISCLOSURE_CAP = "{0}'s recorded index text says: {1}.";

   /// <summary>A measured exact search fact.</summary>
   public const string DISCLOSURE_SCAN = "{0}'s measured search fact says: {1}.";

   /// <summary>ClickHouse data folder, v8: the size at the start, the size read after the system log tables were truncated and the folder stopped changing, and the size at the end.</summary>
   public const string DISCLOSURE_DATA_FOLDER = "Run {0}: {1}'s data folder held {2} bytes at its start, {3} bytes after its system log tables were truncated and it stopped changing, and {4} bytes at its end.";

   /// <summary>The same when the recorded text says the folder was still changing at the deadline of the wait.</summary>
   public const string DISCLOSURE_DATA_FOLDER_STILL = "Run {0}: {1}'s data folder held {2} bytes at its start, {3} bytes after its system log tables were truncated and while it was still changing at the deadline, and {4} bytes at its end.";

   /// <summary>What the reset truncated and what ClickHouse itself counted in those tables.</summary>
   public const string DISCLOSURE_DATA_FOLDER_TABLES = "Run {0}: the reset truncated {1} MergeTree log tables, whose active bytes by {2}'s own count were {3} before and {4} after.";

   /// <summary>Data folder without a reset.</summary>
   public const string DISCLOSURE_DATA_FOLDER_PLAIN = "Run {0}: {1}'s data folder held {2} bytes at its start and {3} bytes at its end.";

   /// <summary>The recorded disk note of a run (no structured data-folder fields) that the run-page notes list marks: the sizes the line records, and the pointer to the run page's correction.</summary>
   public const string DISCLOSURE_DISK_CORRECTED = "Run {0}: {1}'s whole engine data folder was recorded as {2}, against {3} before; the run's page prints a correction to its recorded \"added by this load\" figure.";

   /// <summary>The same when the note is not in the form that gives the two sizes.</summary>
   public const string DISCLOSURE_DISK_WITHHELD = "Run {0}: {1}'s recorded disk note is not printed here; the run's page prints a correction to its \"added by this load\" figure.";

   /// <summary>Segment layouts.</summary>
   public const string DISCLOSURE_LAYOUTS = "Segment layouts differed between runs for {0}; each run's layout is in its row flags.";

   /// <summary>Observer present.</summary>
   public const string DISCLOSURE_OBSERVER = "An observer process ran beside the {0} runs; its own CPU, at most {1} CPUs in any one pass, counts as outside load, and its summary is {2} beside this report.";

   /// <summary>Observer clock check, when the summary holds no per-CPU figures: the figure is the largest of a CPU group's mean.</summary>
   public const string DISCLOSURE_OBSERVER_CLOCK = "By APERF and MPERF, the largest deviation of a CPU group's mean in a pass was {0}%, and the observer read MSR {1} as {2}.";

   /// <summary>Observer clock check, per CPU: the worst single CPU of any pass.</summary>
   public const string DISCLOSURE_OBSERVER_CPU = "By APERF and MPERF, the largest deviation of one CPU in a pass was {0}%: {1}'s {2} pass on CPU {3}, at {4} MHz against the pin of {5} MHz.";

   /// <summary>The MSR the observer read.</summary>
   public const string DISCLOSURE_OBSERVER_MSR = "The observer read MSR {0} as {1}.";

   /// <summary>The pass that ran under the pin on every CPU.</summary>
   public const string DISCLOSURE_OBSERVER_DIP = "{0}'s {1} pass ran {2}% to {3}% under the pin on every CPU in all {4} runs of {5}; no other pass was more than {6}% from it, and why is not known.";

   /// <summary>The pass that ran under the pin on some CPUs.</summary>
   public const string DISCLOSURE_OBSERVER_DIP_SOME = "{0}'s {1} pass ran under the pin on {2} of {3} CPU readings in {4} runs of {5}, by up to {6}%; no other pass was more than {7}% from it, and why is not known.";

   /// <summary>A pass whose sampled mean lay outside the tolerance: what a mean rule flags and the median rule does not.</summary>
   public const string DISCLOSURE_MEAN_DIP = "Run {0}: {1}'s {2} pass averaged {3} MHz on the engine CPUs and {4} MHz on the client CPUs; a mean rule flags it and the median rule does not.";

   /// <summary>The same, when an earlier session's recorded warnings flag the same pass by a mean.</summary>
   public const string DISCLOSURE_MEAN_DIP_AS = "Run {0}: {1}'s {2} pass averaged {3} MHz on the engine CPUs and {4} MHz on the client CPUs, as in {5}; a mean rule flags it and the median rule does not.";

   /// <summary>The observer was not pinned; where its threads ran and what it used.</summary>
   public const string DISCLOSURE_OBSERVER_PLACEMENT = "The observer was not pinned: in the {0} runs {1} percent of its resident-thread ticks were seen on engine CPUs {2}.";

   /// <summary>The observer's CPU by cgroup: the largest of the runs' averages over a whole run, not a pass maximum.</summary>
   public const string DISCLOSURE_OBSERVER_CGROUP = "Averaged over a whole run, the observer's CPU by cgroup was at most {1} CPUs in any of the {0} runs.";

   /// <summary>Timers.</summary>
   public const string DISCLOSURE_TIMERS = "The observer saw a systemd timer fire during {0} timed passes of the {1} runs.";

   /// <summary>Timers not covered.</summary>
   public const string DISCLOSURE_TIMERS_NOT_COVERED = "The observer summary does not cover systemd timers for every {0} run.";

   /// <summary>When no run of the session is covered.</summary>
   public const string DISCLOSURE_TIMERS_NONE = "The observer summary covers systemd timers for no {0} run.";

   /// <summary>No observer.</summary>
   public const string DISCLOSURE_NO_OBSERVER = "No observer summary was given, so this report holds no APERF and MPERF check of the clock.";

   /// <summary>Search settings scope.</summary>
   public const string DISCLOSURE_SEARCH_SETTINGS = "Every order here was measured at the search settings each run recorded for the engine, listed in the why section; this report makes no claim at other settings.";

   /// <summary>A recorded route.</summary>
   public const string DISCLOSURE_ROUTE = "A route each run recorded in conditions.connections reads: {0}.";

   /// <summary>C-states.</summary>
   public const string DISCLOSURE_CSTATES = "CPU idle states were recorded and left as found: driver {0}, governor {1}.";

   /// <summary>dockerd test.</summary>
   public const string DISCLOSURE_DOCKERD_TEST = "The test {0} holds the formula that turns CPU counters into outside load equal to the formula of v7, on fixed counters; it does not make the measured load of two sessions equal.";

   /// <summary>dockerd not recorded.</summary>
   public const string DISCLOSURE_DOCKERD_NONE = "The runs of {0} did not record how dockerd's CPU was counted.";

   /// <summary>Method intro.</summary>
   public const string METHOD = "The method, quoted from the notes of run {0}:";

   /// <summary>Target left out by --targets.</summary>
   public const string NOT_IN_REPORT_TARGETS = "{0} has no row: it was left out by --targets.";

   /// <summary>Target whose setup differs inside a session.</summary>
   public const string NOT_IN_REPORT_SETUP = "{0} has no row: its recorded setup differs within a session, in {1}.";

   /// <summary>Runs used.</summary>
   public const string RUNS = "Runs used: {0} claim runs, which are among the {1} runs of the basis; each is listed below with its seed and start time.";

   /// <summary>Why the page may show runs an unpublished report also used: its verdict's word.</summary>
   public const string RUNS_REUSE = "The runs of {0} were also used by a report that was not published; its verdict, {1}, holds the word {2}.";

   /// <summary>The same, naming the blocked set found beside the runs.</summary>
   public const string RUNS_REUSE_SET = "The runs of {0} were also used by the set {3}, which was not published; its verdict, {1}, holds the word {2}.";

   /// <summary>The word a verdict file holds when it blocked the report it judged.</summary>
   public const string REUSE_TOKEN = "BLOCK";

   /// <summary>A run page with a retired framing sentence (hole H4).</summary>
   public const string RUNS_STALE = "The results.md of run {0} holds a framing sentence this report retired, on line {1}.";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Writes every sentence of the report, fills the row notes and flag texts, and returns the
   /// sentences in reading order.
   /// </summary>
   /// <param name="report">The report, figures and why rows filled.</param>
   /// <param name="sessions">Claim sessions.</param>
   /// <param name="input">The input.</param>
   /// <returns>The sentences.</returns>
   /// <exception cref="ConsolidateRefusal">A fact a sentence needs is missing (the Redis storage fact for a named Redis).</exception>
   public static List<Sentence> Write( ConsolidatedReport report, IReadOnlyList<ClaimSession> sessions, ConsolidateInput input )
   {
      var w = new Writer( report, sessions, input );
      ConsolidateTextParts.Head( w );
      ConsolidateTextParts.Threshold( w );
      ConsolidateTextParts.Tables( w );
      ConsolidateTextParts.Recall( w );
      ConsolidateTextParts.Why( w );
      ConsolidateTextParts.Drift( w );
      ConsolidateTextParts.Clock( w );
      ConsolidateTextDisclosures.Write( w );
      ConsolidateTextParts.Tail( w );
      return w.Sentences;
   }

   /// <summary>
   /// Fills a template.
   /// </summary>
   /// <param name="template">The template.</param>
   /// <param name="args">Its arguments.</param>
   /// <returns>The text.</returns>
   public static string Fill( string template, params object[] args )
   {
      return string.Format( CultureInfo.InvariantCulture, template, args );
   }

   /// <summary>
   /// The label of a metric in text.
   /// </summary>
   /// <param name="metric">Metric id.</param>
   /// <returns>"p50", "QPS@1", "QPS@8" or "exact p50".</returns>
   public static string Label( string metric )
   {
      return ClaimMetrics.Label( metric );
   }

   /// <summary>
   /// The label of a pass in text: "one-searcher", "eight-searcher" or "exact".
   /// Why words and not pass names: the pass names hold the word default, which templates do not use.
   /// </summary>
   /// <param name="pass">Pass name.</param>
   /// <returns>The label.</returns>
   public static string PassLabel( string pass )
   {
      return pass switch
      {
         "default@1" => "one-searcher",
         "default@8" => "eight-searcher",
         "exact" => "exact",
         _ => pass.Replace( "default", "standard", StringComparison.Ordinal ),
      };
   }

   /// <summary>
   /// The MSR number as it appears in <see cref="MSR_TOKEN"/>.
   /// </summary>
   /// <returns>"0x620".</returns>
   public static string Msr()
   {
      return MSR_TOKEN[( MSR_TOKEN.IndexOf( '"' ) + 1 )..MSR_TOKEN.LastIndexOf( '"' )];
   }

   /// <summary>
   /// The decimals a metric's values are printed with: 3 for latency in ms, 0 for searches per second.
   /// </summary>
   /// <param name="metric">Metric id.</param>
   /// <returns>Decimals.</returns>
   public static int Decimals( string metric )
   {
      return ClaimMetrics.LowerIsBetter( metric ) ? 3 : 0;
   }

   #endregion Public Methods
}

/// <summary>
/// The state of one writing pass: the report, the sessions and the sentences so far.
/// </summary>
public sealed class Writer
{
   #region Constructor

   /// <summary>
   /// Creates the writer.
   /// </summary>
   /// <param name="report">The report.</param>
   /// <param name="sessions">Claim sessions.</param>
   /// <param name="input">The input: the repository root where cited files are looked up, the basis sessions and the results folder.</param>
   public Writer( ConsolidatedReport report, IReadOnlyList<ClaimSession> sessions, ConsolidateInput input )
   {
      Report = report;
      Sessions = sessions;
      Input = input;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The report.</summary>
   public ConsolidatedReport Report { get; }

   /// <summary>Claim sessions, oldest first.</summary>
   public IReadOnlyList<ClaimSession> Sessions { get; }

   /// <summary>The sentences, in reading order.</summary>
   public List<Sentence> Sentences { get; } = new();

   /// <summary>The input of this consolidation.</summary>
   public ConsolidateInput Input { get; }

   /// <summary>Repository root.</summary>
   public string RepoRoot => Input.RepoRoot;

   /// <summary>The newest claim session.</summary>
   public ClaimSession Newest => Sessions[^1];

   /// <summary>True in two-session mode.</summary>
   public bool TwoSessions => Sessions.Count == 2;

   /// <summary>
   /// Adds a sentence.
   /// </summary>
   /// <param name="slot">Its slot.</param>
   /// <param name="text">Its text.</param>
   /// <param name="sources">Its sources.</param>
   /// <returns>The text, so a caller can also store it in a field.</returns>
   public string Add( string slot, string text, params SentenceSource[] sources )
   {
      Sentences.Add( new Sentence( slot, text, sources ) );
      return text;
   }

   /// <summary>
   /// Adds a sentence with a list of sources.
   /// </summary>
   /// <param name="slot">Its slot.</param>
   /// <param name="text">Its text.</param>
   /// <param name="sources">Its sources.</param>
   /// <returns>The text.</returns>
   public string Add( string slot, string text, IEnumerable<SentenceSource> sources )
   {
      return Add( slot, text, sources.ToArray() );
   }

   /// <summary>
   /// The why row of a target.
   /// </summary>
   /// <param name="target">Target.</param>
   /// <returns>The row, or null.</returns>
   public WhyRow? Why( string target )
   {
      return Report.Why.FirstOrDefault( w => w.Target == target );
   }

   /// <summary>
   /// The first fact of a kind for a target.
   /// </summary>
   /// <param name="target">Target.</param>
   /// <param name="kind">Fact kind.</param>
   /// <returns>The fact, or null.</returns>
   public WhyFact? Fact( string target, string kind )
   {
      return Why( target )?.Facts.FirstOrDefault( f => f.Kind == kind );
   }

   /// <summary>
   /// The source that binds a run's folder name in a sentence: the run's entry in the report's sessions.
   /// </summary>
   /// <param name="folder">Run folder name.</param>
   /// <returns>The source.</returns>
   public SentenceSource RunSource( string folder )
   {
      foreach( SessionInfo session in Report.Sessions )
      {
         RunRef? run = session.Runs.FirstOrDefault( r => Path.GetFileName( r.Folder ) == folder );
         if( run != null )
         {
            return S.Consolidated( $"sessions[name={session.Name}].runs[folder={run.Folder}].folder", run.Folder );
         }
      }

      throw new InvalidOperationException( $"{folder} is not a claim run of this report" );
   }

   /// <summary>
   /// A move as "29.08% (mongodb/oracle)" with its source, or "none".
   /// </summary>
   /// <param name="move">The move, or null.</param>
   /// <param name="path">Its path in consolidated.json.</param>
   /// <param name="sources">Receives the sources.</param>
   /// <returns>The text.</returns>
   public static string Move( BasisMove? move, string path, List<SentenceSource> sources )
   {
      if( move == null )
      {
         return ConsolidateText.MOVE_NONE;
      }

      string pct = S.Percent( move.MoveBp );
      sources.Add( S.Consolidated( path + ".moveBp", pct, "bp-pct" ) );
      return ConsolidateText.Fill( ConsolidateText.MOVE, pct, move.Target ?? move.Pair ?? string.Empty );
   }

   #endregion Public Methods
}
