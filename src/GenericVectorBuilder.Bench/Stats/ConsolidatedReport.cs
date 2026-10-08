namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// consolidated.json of the v8 "big gaps only" report, field for field as the v8c contract
/// (v8-design/v8c/contract.md, "consolidated.json, P3") names it, serialized camelCase.
/// Why a model of its own and not the v7 shape: v7 ranked by tie bands; v8 publishes only orders
/// that hold by the claim rule in each session separately, and every sentence the page prints
/// travels here with its sources (<see cref="Sentences"/>), so the web page renders fields and
/// sentences and never writes prose of its own.
/// Fields beyond the contract (format, mode, status, stopReasons, targets, notInReport,
/// engineSettings, observer, and the counts inside drift and basis) are additions the page may read;
/// none changes the meaning of a contract field. A field the runs did not record is null, never a
/// made-up value.
/// </summary>
public sealed class ConsolidatedReport
{
   #region Public Methods

   /// <summary>Shape marker, "v8c". Why: the web page must tell this shape from the v7 and 4 Oct shapes without guessing.</summary>
   public string Format { get; set; } = "v8c";

   /// <summary>When the report was written (UTC).</summary>
   public string CreatedUtc { get; set; } = string.Empty;

   /// <summary>The consolidate command line as given.</summary>
   public string CommandLine { get; set; } = string.Empty;

   /// <summary>"two-session" when two claim sessions were given; "one-session" for one, where no row is ranked.</summary>
   public string Mode { get; set; } = string.Empty;

   /// <summary>"complete", or "stopped" when guard G2 or G3 stopped the report (no headline then).</summary>
   public string Status { get; set; } = string.Empty;

   /// <summary>Why the report stopped, one line per guard that fired; empty when complete.</summary>
   public List<string> StopReasons { get; set; } = new();

   /// <summary>The claim sessions, oldest first.</summary>
   public List<SessionInfo> Sessions { get; set; } = new();

   /// <summary>Targets with a row, in the order of the first claim run.</summary>
   public List<string> Targets { get; set; } = new();

   /// <summary>Runs each claim session holds (the rule refuses any other count).</summary>
   public int RunsPerSession { get; set; }

   /// <summary>Runs behind a ranked row's median: runs per session times sessions.</summary>
   public int RunsShown { get; set; }

   /// <summary>Claim runs in all.</summary>
   public int ClaimRunCount { get; set; }

   /// <summary>Container targets with an image id in <see cref="Images"/>.</summary>
   public int ImageTargets { get; set; }

   /// <summary>Engines the headline names (0 when it names none, or in one-session or stopped mode).</summary>
   public int HeadlineNamedCount { get; set; }

   /// <summary>The queries: as the runs recorded them, and the copied question file checked against the recorded SHA-256.</summary>
   public QueriesInfo Queries { get; set; } = new();

   /// <summary>Claim sessions whose runs an unpublished report also used, with the verdict file that names why it was not published.</summary>
   public List<ReuseNote> Reuse { get; set; } = new();

   /// <summary>Targets the claim runs hold that have no row, each with its reason.</summary>
   public List<NotInReport> NotInReport { get; set; } = new();

   /// <summary>The threshold basis: runs, left-out runs, exclusions, the moves per metric and the threshold.</summary>
   public BasisInfo Basis { get; set; } = new();

   /// <summary>The threshold the claim rule uses.</summary>
   public ThresholdInfo Threshold { get; set; } = new();

   /// <summary>One table per ranked metric, in the order p50, QPS@1, QPS@8, exact p50.</summary>
   public List<MetricTable> Metrics { get; set; } = new();

   /// <summary>Guards G2 (an order of the first session whose medians reverse in the second) and G3 (setup changed between sessions).</summary>
   public GuardsInfo Guards { get; set; } = new();

   /// <summary>Movement from the first claim session to the second; null in one-session mode.</summary>
   public DriftInfo? Drift { get; set; }

   /// <summary>Recall as whole hits per run.</summary>
   public List<RecallRow> Recall { get; set; } = new();

   /// <summary>The claim sessions whose runs record no hit count, so the report derived their hits as the recall times the queries times the hits asked for.</summary>
   public List<string> RecallDerivedSessions { get; set; } = new();

   /// <summary>Targets with a row.</summary>
   public int TargetCount { get; set; }

   /// <summary>Targets whose protocol fact names the benchmark's own HttpClient REST code.</summary>
   public int HttpClientCount { get; set; }

   /// <summary>The clock per session and the dropped mean-based warnings.</summary>
   public ClockInfo Clock { get; set; } = new();

   /// <summary>The machine the claim runs ran on, from their recorded fields.</summary>
   public MachineInfo Machine { get; set; } = new();

   /// <summary>Image ids per container target, from the newest session that recorded them.</summary>
   public List<ImageInfo> Images { get; set; } = new();

   /// <summary>Engine settings per target as the newest session recorded them (key, value, how).</summary>
   public List<EngineSettingsRow> EngineSettings { get; set; } = new();

   /// <summary>The why table: facts and measured costs per target, in p50 table order.</summary>
   public List<WhyRow> Why { get; set; } = new();

   /// <summary>Each reported target's recorded search and index settings (searchSettings of its results), in the order of the first claim run.</summary>
   public List<SearchSettingsRow> SearchSettings { get; set; } = new();

   /// <summary>The observer summary given with --observer, read for the disclosures; null when none was given.</summary>
   public ObserverInfo? Observer { get; set; }

   /// <summary>The observer summaries that cover older claim sessions, one per session; empty when none was given. Why: an observer that ran beside v7 shows the clock and the outside load of v7 too.</summary>
   public List<ObserverInfo> ObserverOthers { get; set; } = new();

   /// <summary>
   /// Every recorded text the report prints, classified clause by clause: the durability and index texts of the reported targets and the run notes of the method.
   /// Why in the result: the sweep table of how each printed clause is backed is data, so a reader and a test can read it without the page.
   /// </summary>
   public List<RecordedTextRecord> RecordedTexts { get; set; } = new();

   /// <summary>Every sentence consolidated.md or the page prints, in reading order, each with its sources.</summary>
   public List<SentenceRecord> Sentences { get; set; } = new();

   /// <summary>The timed passes the claim runs recorded as NOT HELD and the check that no order depends on them; null when no claim run recorded one.</summary>
   public NotHeldInfo? NotHeld { get; set; }

   /// <summary>The builds that measured the claim sessions and the build that made this report.</summary>
   public BuildInfo Build { get; set; } = new();

   /// <summary>The audit that ran over every sentence and fact before anything was written.</summary>
   public AuditInfo Audit { get; set; } = new();

   #endregion Public Methods
}

/// <summary>The builds behind the report: the one that measured each claim session and the one that consolidated them.</summary>
public sealed class BuildInfo
{
   #region Public Methods

   /// <summary>One entry per claim session.</summary>
   public List<SessionBuild> Measured { get; set; } = new();

   /// <summary>The build that made this report.</summary>
   public ConsolidatingBuild ConsolidatedBy { get; set; } = new();

   #endregion Public Methods
}

/// <summary>The build that measured a claim session.</summary>
public sealed class SessionBuild
{
   #region Public Methods

   /// <summary>The session.</summary>
   public string Session { get; set; } = string.Empty;

   /// <summary>The commit every run of the session records (conditions.build.commit); null when the runs record none or different ones.</summary>
   public string? Commit { get; set; }

   /// <summary>The first <see cref="ConsolidateBuild.SHORT_COMMIT"/> characters of the commit.</summary>
   public string? CommitShort { get; set; }

   #endregion Public Methods
}

/// <summary>The build that made the report.</summary>
public sealed class ConsolidatingBuild
{
   #region Public Methods

   /// <summary>The assembly's informational version; null when it carries none.</summary>
   public string? InformationalVersion { get; set; }

   /// <summary>The commit after the "+" of the version; null when it has none.</summary>
   public string? Commit { get; set; }

   /// <summary>The first characters of the commit.</summary>
   public string? CommitShort { get; set; }

   /// <summary>The SHA-256 of the assembly file; null when the assembly has no file.</summary>
   public string? AssemblySha256 { get; set; }

   #endregion Public Methods
}

/// <summary>One claim session: its name and its runs. Why named: the rule is applied in each session separately.</summary>
public sealed class SessionInfo
{
   #region Public Methods

   /// <summary>Session name as given on the command line ("v7", "v8").</summary>
   public string Name { get; set; } = string.Empty;

   /// <summary>The session's runs, oldest first.</summary>
   public List<RunRef> Runs { get; set; } = new();

   #endregion Public Methods
}

/// <summary>A run used by the report. Why the folder is relative: the page resolves it under its own results folder and never trusts an absolute path.</summary>
public sealed class RunRef
{
   #region Public Methods

   /// <summary>Folder relative to the results folder the session folders were named against (the folder name for a run inside it).</summary>
   public string Folder { get; set; } = string.Empty;

   /// <summary>runSeed.</summary>
   public int? Seed { get; set; }

   /// <summary>startedUtc.</summary>
   public string? StartedUtc { get; set; }

   /// <summary>The SHA-256 of the run's results.json as this report read it.</summary>
   public string? ResultsSha256 { get; set; }

   /// <summary>Lines of the run's results.md that hold a framing sentence this report retired (the latency split), so the page can mark the run page (hole H4); empty when none.</summary>
   public List<StaleLine> StaleLines { get; set; } = new();

   #endregion Public Methods
}

/// <summary>A line of a run's results.md that holds a retired framing sentence.</summary>
public sealed class StaleLine
{
   #region Public Methods

   /// <summary>1-based line number in results.md.</summary>
   public int Line { get; set; }

   /// <summary>The retired words found on that line, as <see cref="ConsolidateFraming.RETIRED"/> lists them.</summary>
   public string Retired { get; set; } = string.Empty;

   #endregion Public Methods
}

/// <summary>A target the claim runs hold that has no row, with its reason.</summary>
public sealed class NotInReport
{
   #region Public Methods

   /// <summary>Target name.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>Reason code: "left out by --targets" or "setup differs within a session".</summary>
   public string Code { get; set; } = string.Empty;

   /// <summary>The fields and runs behind the reason, as data; the page prints it through the target's notInReport sentence.</summary>
   public string Detail { get; set; } = string.Empty;

   /// <summary>The differing field names, for a setup difference; empty otherwise.</summary>
   public List<string> Fields { get; set; } = new();

   #endregion Public Methods
}

/// <summary>The threshold basis (design section 1).</summary>
public sealed class BasisInfo
{
   #region Public Methods

   /// <summary>Every run in the basis, folder order.</summary>
   public List<BasisRunInfo> Runs { get; set; } = new();

   /// <summary>Every other run of the pipeline found beside the session folders, with the reason it is not in the basis.</summary>
   public List<LeftOutRun> LeftOut { get; set; } = new();

   /// <summary>The rows of basis-exclusions.json, as read.</summary>
   public List<ExclusionRow> Exclusions { get; set; } = new();

   /// <summary>The moves of each metric, keyed by metric id.</summary>
   public Dictionary<string, MetricBasis> PerMetric { get; set; } = new();

   /// <summary>The largest one-engine and pair moves over every metric among the runs without machine control (disclosed, not used).</summary>
   public MoveMaxima NoMachineControl { get; set; } = new();

   /// <summary>Per exclusion kind: the largest moves with that kind kept in, and the threshold they would give.</summary>
   public List<KeptIn> ExclusionsKept { get; set; } = new();

   /// <summary>
   /// Every change of an engine's recorded setup between basis runs, with the field that changed, the move it hides and the threshold
   /// that move would give. Why: the basis compares an engine's runs only where it recorded one setup, so a change of setup hides its moves.
   /// </summary>
   public List<SetupSplit> SetupSplits { get; set; } = new();

   /// <summary>Splits in <see cref="SetupSplits"/>.</summary>
   public int SetupSplitCount { get; set; }

   /// <summary>Per basis session: its runs and how many of them held the clock (turbo off).</summary>
   public List<SessionClockHeld> ClockHeld { get; set; } = new();

   /// <summary>The largest one-engine or pair move of the basis, bp.</summary>
   public int MaxBp { get; set; }

   /// <summary>The move that set <see cref="MaxBp"/>.</summary>
   public BasisMove? MaxMove { get; set; }

   /// <summary>
   /// The timed passes behind <see cref="MaxMove"/> that their run recorded as NOT HELD (the engine was still changing when they were timed): one per run and engine of the move
   /// whose pass for the move's metric was marked. Empty when the largest move rests on none.
   /// </summary>
   public List<NotHeldCell> MaxMoveNotHeld { get; set; } = new();

   /// <summary>
   /// The largest move and the threshold of the basis with the engines of <see cref="MaxMoveNotHeld"/> left out of every run and metric; null when the largest move rests on no NOT HELD pass.
   /// Why: the threshold must not rest unseen on a pass its own run says not to quote, and the reader can then see what the line would be without it.
   /// </summary>
   public BasisWithout? MaxMoveWithout { get; set; }

   /// <summary>The threshold, bp.</summary>
   public int TBp { get; set; }

   /// <summary>The lowest threshold, bp (<see cref="ThresholdBasis.FLOOR_BP"/>).</summary>
   public int FloorBp { get; set; }

   /// <summary>Thresholds are multiples of this, bp (<see cref="ThresholdBasis.STEP_BP"/>).</summary>
   public int StepBp { get; set; }

   /// <summary>How far above the largest move the threshold sits at least, bp (<see cref="ThresholdBasis.MARGIN_BP"/>).</summary>
   public int MarginBp { get; set; }

   /// <summary>The threshold rule as code text.</summary>
   public string TBpRule { get; set; } = string.Empty;

   /// <summary>Runs in the basis.</summary>
   public int RunCount { get; set; }

   /// <summary>Runs without machine control behind <see cref="NoMachineControl"/>.</summary>
   public int NoMachineControlCount { get; set; }

   /// <summary>Runs in <see cref="LeftOut"/>.</summary>
   public int LeftOutCount { get; set; }

   /// <summary>The move rule as code text.</summary>
   public string MoveRule { get; set; } = string.Empty;

   #endregion Public Methods
}

/// <summary>How many runs of a basis session held the clock.</summary>
public sealed class SessionClockHeld
{
   #region Public Methods

   /// <summary>The session.</summary>
   public string Session { get; set; } = string.Empty;

   /// <summary>Its basis runs.</summary>
   public int Runs { get; set; }

   /// <summary>Of those, the runs that held the clock.</summary>
   public int Held { get; set; }

   #endregion Public Methods
}

/// <summary>One timed pass its run recorded as NOT HELD, as the largest move of the basis uses it.</summary>
public sealed class NotHeldCell
{
   #region Public Methods

   /// <summary>The run folder.</summary>
   public string Run { get; set; } = string.Empty;

   /// <summary>The run's seed.</summary>
   public int? Seed { get; set; }

   /// <summary>The engine.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>The pass name, such as default@8.</summary>
   public string Pass { get; set; } = string.Empty;

   /// <summary>The metric the move was taken on.</summary>
   public string Metric { get; set; } = string.Empty;

   /// <summary>The clause of the run's settle warning that records the pass, verbatim.</summary>
   public string Token { get; set; } = string.Empty;

   /// <summary>The timed figure with its unit, as the warning wrote it.</summary>
   public string Timed { get; set; } = string.Empty;

   /// <summary>How far it lay from the settled figure, percent, as written.</summary>
   public string OffPercent { get; set; } = string.Empty;

   /// <summary>The settled figure with its unit.</summary>
   public string Settled { get; set; } = string.Empty;

   /// <summary>The margin, percent, as written.</summary>
   public string LimitPercent { get; set; } = string.Empty;

   #endregion Public Methods
}

/// <summary>The claim runs' timed passes recorded as NOT HELD, and the table rows they leave unranked.</summary>
public sealed class NotHeldInfo
{
   #region Public Methods

   /// <summary>Every such pass behind a ranked metric, with the settled figure its own warm-up reached.</summary>
   public List<NotHeldFigure> Cells { get; set; } = new();

   /// <summary>The table rows shown and not ranked because of those cells: one per metric and target.</summary>
   public List<NotHeldRow> Unranked { get; set; } = new();

   #endregion Public Methods
}

/// <summary>One NOT HELD pass of a claim run and the settled figure its own warm-up reached.</summary>
public sealed class NotHeldFigure
{
   #region Public Methods

   /// <summary>The run folder.</summary>
   public string Run { get; set; } = string.Empty;

   /// <summary>The run's seed.</summary>
   public int? Seed { get; set; }

   /// <summary>The engine.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>The pass name.</summary>
   public string Pass { get; set; } = string.Empty;

   /// <summary>The metric id the pass's figure stands for.</summary>
   public string Metric { get; set; } = string.Empty;

   /// <summary>The clause of the settle warning that records the pass, verbatim.</summary>
   public string Token { get; set; } = string.Empty;

   /// <summary>The timed figure with its unit.</summary>
   public string Timed { get; set; } = string.Empty;

   /// <summary>The settled figure with its unit.</summary>
   public string Settled { get; set; } = string.Empty;

   /// <summary>How far apart they were, percent, as written.</summary>
   public string OffPercent { get; set; } = string.Empty;

   /// <summary>The margin, percent, as written.</summary>
   public string LimitPercent { get; set; } = string.Empty;

   #endregion Public Methods
}

/// <summary>A table row shown and not ranked because the runs recorded its timed pass NOT HELD.</summary>
public sealed class NotHeldRow
{
   #region Public Methods

   /// <summary>Metric id of the table.</summary>
   public string Metric { get; set; } = string.Empty;

   /// <summary>The engine.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>The pass name.</summary>
   public string Pass { get; set; } = string.Empty;

   /// <summary>How many claim runs recorded the pass NOT HELD.</summary>
   public int Runs { get; set; }

   #endregion Public Methods
}

/// <summary>The basis recomputed with some engines left out: the largest move that remains and the threshold it gives.</summary>
public sealed class BasisWithout
{
   #region Public Methods

   /// <summary>The engines left out.</summary>
   public List<string> Targets { get; set; } = new();

   /// <summary>The largest move that remains, bp.</summary>
   public int MaxBp { get; set; }

   /// <summary>The move that set it.</summary>
   public BasisMove? MaxMove { get; set; }

   /// <summary>The threshold it gives, bp.</summary>
   public int TBp { get; set; }

   #endregion Public Methods
}

/// <summary>One basis run with its recorded conditions.</summary>
public sealed class BasisRunInfo
{
   #region Public Methods

   /// <summary>Folder name.</summary>
   public string Folder { get; set; } = string.Empty;

   /// <summary>runSeed.</summary>
   public int? Seed { get; set; }

   /// <summary>The session it was given in.</summary>
   public string Session { get; set; } = string.Empty;

   /// <summary>startedUtc.</summary>
   public string? StartedUtc { get; set; }

   /// <summary>The SHA-256 of the run's results.json as this report read it.</summary>
   public string? ResultsSha256 { get; set; }

   /// <summary>Its conditions.</summary>
   public BasisConditionsInfo Conditions { get; set; } = new();

   /// <summary>Lines of its results.md with a retired framing sentence.</summary>
   public List<StaleLine> StaleLines { get; set; } = new();

   #endregion Public Methods
}

/// <summary>The conditions of a basis run, as labels (the threshold-basis prototype's wording, "build" for its "binary").</summary>
public sealed class BasisConditionsInfo
{
   #region Public Methods

   /// <summary>Turbo label.</summary>
   public string Turbo { get; set; } = string.Empty;

   /// <summary>Uncore label.</summary>
   public string Uncore { get; set; } = string.Empty;

   /// <summary>Warm-up label.</summary>
   public string Warmup { get; set; } = string.Empty;

   /// <summary>Rehearsal label.</summary>
   public string Rehearsal { get; set; } = string.Empty;

   /// <summary>Build label: the binary's tree, plus the commit when recorded.</summary>
   public string Build { get; set; } = string.Empty;

   /// <summary>Machine control as recorded.</summary>
   public string MachineControl { get; set; } = string.Empty;

   #endregion Public Methods
}

/// <summary>A run of the pipeline that is not in the basis, with the reason.</summary>
public sealed class LeftOutRun
{
   #region Public Methods

   /// <summary>Folder name.</summary>
   public string Folder { get; set; } = string.Empty;

   /// <summary>Why it is not in the basis.</summary>
   public string Reason { get; set; } = string.Empty;

   #endregion Public Methods
}

/// <summary>One row of deploy/bench/basis-exclusions.json.</summary>
public sealed class ExclusionRow
{
   #region Public Methods

   /// <summary>Target.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>Seeds of the runs whose cells are removed.</summary>
   public List<int> Seeds { get; set; } = new();

   /// <summary>Metric ("*" for all, or one of p50, qps1, qps8, exact).</summary>
   public string Metric { get; set; } = string.Empty;

   /// <summary>"method defect" or "unrecorded config change".</summary>
   public string Kind { get; set; } = string.Empty;

   /// <summary>The row's own reason text, as written in the file (data; the page prints the source's evidence instead).</summary>
   public string Why { get; set; } = string.Empty;

   /// <summary>Repository path of the verdict that names the defect.</summary>
   public string Source { get; set; } = string.Empty;

   /// <summary>The verdict item, when the row names one.</summary>
   public int? Item { get; set; }

   /// <summary>Phrases the source file must hold, as the row lists them.</summary>
   public List<string> Evidence { get; set; } = new();

   /// <summary>The words of the verdict item that carry the row's seeds; the phrase a sentence cites is the evidence phrase that holds them. Empty when the row names none.</summary>
   public string SeedEvidence { get; set; } = string.Empty;

   #endregion Public Methods
}

/// <summary>The moves of one metric.</summary>
public sealed class MetricBasis
{
   #region Public Methods

   /// <summary>Largest one-engine move over the basis.</summary>
   public BasisMove? OneEngine { get; set; }

   /// <summary>Largest pair move over the basis.</summary>
   public BasisMove? Pair { get; set; }

   /// <summary>Largest one-engine move between runs at the same settings.</summary>
   public BasisMove? OneEngineSameSettings { get; set; }

   /// <summary>Largest pair move between runs at the same settings.</summary>
   public BasisMove? PairSameSettings { get; set; }

   /// <summary>Largest one-engine move with each exclusion kind kept in.</summary>
   public List<KeptIn> OneEngineExclusionsKept { get; set; } = new();

   /// <summary>Largest pair move with each exclusion kind kept in.</summary>
   public List<KeptIn> PairExclusionsKept { get; set; } = new();

   /// <summary>Largest one-engine move among the runs without machine control.</summary>
   public BasisMove? OneEngineNoMachineControl { get; set; }

   /// <summary>Largest pair move among the runs without machine control.</summary>
   public BasisMove? PairNoMachineControl { get; set; }

   #endregion Public Methods
}

/// <summary>A largest one-engine move and a largest pair move.</summary>
public sealed class MoveMaxima
{
   #region Public Methods

   /// <summary>The one-engine move.</summary>
   public BasisMove? OneEngine { get; set; }

   /// <summary>The pair move.</summary>
   public BasisMove? Pair { get; set; }

   #endregion Public Methods
}

/// <summary>
/// One change of an engine's recorded setup between two groups of basis runs: the fields that differ, the runs on each side, and the
/// moves across the change that the basis leaves out, with the threshold the largest would give.
/// </summary>
public sealed class SetupSplit
{
   #region Public Methods

   /// <summary>The engine (target name).</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>The names of the recorded fields that differ (engine, index, searchSettings, durability, engineFiles, engineSettings, imageId, hosting).</summary>
   public List<string> Fields { get; set; } = new();

   /// <summary>Each differing field's recorded text on the two sides, as data.</summary>
   public List<FieldChange> Changes { get; set; } = new();

   /// <summary>The group of runs whose first run is the earlier.</summary>
   public SplitSide Before { get; set; } = new();

   /// <summary>The other group of runs.</summary>
   public SplitSide After { get; set; } = new();

   /// <summary>The largest one-engine move across the change, over the four metrics; null when no two runs qualify.</summary>
   public BasisMove? OneEngine { get; set; }

   /// <summary>The largest pair move across the change (the other engine at one setup in both runs), over the four metrics; null when none qualifies.</summary>
   public BasisMove? Pair { get; set; }

   /// <summary>The same moves per metric id.</summary>
   public Dictionary<string, SplitMetric> PerMetric { get; set; } = new();

   /// <summary>The threshold the basis would give with this change's moves counted, bp.</summary>
   public int TBpIfCounted { get; set; }

   #endregion Public Methods
}

/// <summary>One side of a setup split: the sessions and runs that recorded one setup.</summary>
public sealed class SplitSide
{
   #region Public Methods

   /// <summary>The sessions of the runs, in order of first run.</summary>
   public List<string> Sessions { get; set; } = new();

   /// <summary>The runs (folder names).</summary>
   public List<string> Runs { get; set; } = new();

   #endregion Public Methods
}

/// <summary>One recorded field of an engine that differs between two sides of a split.</summary>
public sealed class FieldChange
{
   #region Public Methods

   /// <summary>The field's name.</summary>
   public string Field { get; set; } = string.Empty;

   /// <summary>Its recorded text on the earlier side, or "not recorded".</summary>
   public string Before { get; set; } = string.Empty;

   /// <summary>Its recorded text on the later side, or "not recorded".</summary>
   public string After { get; set; } = string.Empty;

   #endregion Public Methods
}

/// <summary>The moves across a setup split for one metric.</summary>
public sealed class SplitMetric
{
   #region Public Methods

   /// <summary>The largest one-engine move across the change.</summary>
   public BasisMove? OneEngine { get; set; }

   /// <summary>The largest pair move across the change.</summary>
   public BasisMove? Pair { get; set; }

   #endregion Public Methods
}

/// <summary>One target's recorded search and index settings.</summary>
public sealed class SearchSettingsRow
{
   #region Public Methods

   /// <summary>The target.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>The run whose record this is: the newest claim session's first run that recorded settings for the target.</summary>
   public string Run { get; set; } = string.Empty;

   /// <summary>The settings as recorded, sorted by key.</summary>
   public List<SearchSettingEntry> Settings { get; set; } = new();

   /// <summary>The recorded clause of the target's index text that states its per-query effort, when that clause carries more than a number; null otherwise.</summary>
   public string? Clause { get; set; }

   #endregion Public Methods
}

/// <summary>One recorded search setting.</summary>
public sealed class SearchSettingEntry
{
   #region Public Methods

   /// <summary>The setting's key as recorded.</summary>
   public string Key { get; set; } = string.Empty;

   /// <summary>Its value as recorded.</summary>
   public string Value { get; set; } = string.Empty;

   #endregion Public Methods
}

/// <summary>Moves with one exclusion kind kept in.</summary>
public sealed class KeptIn
{
   #region Public Methods

   /// <summary>The exclusion kind kept in.</summary>
   public string Kind { get; set; } = string.Empty;

   /// <summary>Largest one-engine move.</summary>
   public BasisMove? OneEngine { get; set; }

   /// <summary>Largest pair move.</summary>
   public BasisMove? Pair { get; set; }

   /// <summary>The threshold the larger of the two would give (overall entries only).</summary>
   public int? TBpIfKept { get; set; }

   #endregion Public Methods
}

/// <summary>One basis move: its size, the target or pair, the two runs, their values and every recorded difference between them.</summary>
public sealed class BasisMove
{
   #region Public Methods

   /// <summary>Metric id.</summary>
   public string Metric { get; set; } = string.Empty;

   /// <summary>The move in bp, rounded up.</summary>
   public int MoveBp { get; set; }

   /// <summary>The move as max/min - 1, unrounded.</summary>
   public double Move { get; set; }

   /// <summary>The move in bp before it is rounded up to <see cref="MoveBp"/>: <see cref="Move"/> times 10000.</summary>
   public double MoveBpExact => Move * 10000.0;

   /// <summary>The target of a one-engine move.</summary>
   public string? Target { get; set; }

   /// <summary>The pair "a/b" of a pair move.</summary>
   public string? Pair { get; set; }

   /// <summary>The two runs (folder names).</summary>
   public List<string> Runs { get; set; } = new();

   /// <summary>The two seeds, in the order of <see cref="Runs"/>.</summary>
   public List<int?> Seeds { get; set; } = new();

   /// <summary>The two values (one engine) or the two same-run ratios a/b (pair).</summary>
   public List<double> Values { get; set; } = new();

   /// <summary>Runs that held both engines of a pair at their identity.</summary>
   public int? RunsWithBoth { get; set; }

   /// <summary>Every recorded difference between the two runs, in the prototype's wording.</summary>
   public List<string> Differences { get; set; } = new();

   /// <summary>The names of the differing fields (turbo, uncore, warmup, rehearsal, build, machineControl, median MHz, and "whether searchSettings was recorded" when one run recorded none).</summary>
   public List<string> DifferenceNames { get; set; } = new();

   #endregion Public Methods
}

/// <summary>The threshold.</summary>
public sealed class ThresholdInfo
{
   #region Public Methods

   /// <summary>Threshold, bp.</summary>
   public int TBp { get; set; }

   /// <summary>1 + TBp / 10000.</summary>
   public double Ratio { get; set; }

   /// <summary>The claim rule as code text.</summary>
   public string Rule { get; set; } = string.Empty;

   #endregion Public Methods
}

/// <summary>One metric's table.</summary>
public sealed class MetricTable
{
   #region Public Methods

   /// <summary>Metric id: "p50Ms", "qps1", "qps8" or "exactP50Ms".</summary>
   public string Metric { get; set; } = string.Empty;

   /// <summary>True for latency.</summary>
   public bool LowerIsBetter { get; set; }

   /// <summary>Rows in order of the median of every run shown.</summary>
   public List<MetricRow> Rows { get; set; } = new();

   /// <summary>Exact table only: the targets with no exact pass, each with its search fact.</summary>
   public List<NoExactPass> NoExactPass { get; set; } = new();

   /// <summary>Pairs of ranked rows ordered by the rule.</summary>
   public int OrderedPairs { get; set; }

   /// <summary>Pairs of ranked rows compared.</summary>
   public int ComparedPairs { get; set; }

   #endregion Public Methods
}

/// <summary>A target that has no exact pass, with the search fact that says how its default search runs.</summary>
public sealed class NoExactPass
{
   #region Public Methods

   /// <summary>Target.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>The search fact's mode (exact or approximate), or null when the sheet has none.</summary>
   public string? Mode { get; set; }

   /// <summary>The search fact's text.</summary>
   public string? FactText { get; set; }

   /// <summary>The search fact's source.</summary>
   public string? FactSource { get; set; }

   #endregion Public Methods
}

/// <summary>One row of a metric table.</summary>
public sealed class MetricRow
{
   #region Public Methods

   /// <summary>Target.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>The name shown: the target name as the runs record it.</summary>
   public string Display { get; set; } = string.Empty;

   /// <summary>"ranked", or "one-session" (shown with one session's figures, not ranked).</summary>
   public string Status { get; set; } = string.Empty;

   /// <summary>Per session: min, max and every run's value.</summary>
   public Dictionary<string, SessionFigures> PerSession { get; set; } = new();

   /// <summary>Median of every run shown (6 in two-session mode, 3 otherwise).</summary>
   public double Median { get; set; }

   /// <summary>Targets this row is not separated from, in table order.</summary>
   public List<string> NotSeparatedFrom { get; set; } = new();

   /// <summary>Flags, each with its code and its sentence.</summary>
   public List<RowFlag> Flags { get; set; } = new();

   /// <summary>
   /// How the engine searched in the passes this row's figures come from: "exact" when its search fact says every vector is scanned, "approximate" when it says an index is
   /// used; the fact sheet's mode, taken from measured state where the runs hold it. Null in the exact table (every row there is the engine's exact mode) and when no search fact exists.
   /// Why on the row: a scan (Qdrant, SQL Server, sqlite-vec, Elasticsearch at 524 vectors) ranks among graph engines in the p50 and QPS tables, and a reader must see which.
   /// </summary>
   public string? SearchMode { get; set; }

   /// <summary>The confidence label of the search fact <see cref="SearchMode"/> comes from (measured, recorded, "recorded (engine's own report)" and the like), so a table shows how sure the mode is; null when there is no mode.</summary>
   public string? SearchConfidence { get; set; }

   /// <summary>The row's note sentence (the Redis in-memory note), or null.</summary>
   public string? Note { get; set; }

   /// <summary>The sources of <see cref="Note"/>, as the audit checked them; empty when there is no note.</summary>
   public List<SourceRecord> NoteSources { get; set; } = new();

   #endregion Public Methods
}

/// <summary>One session's figures for a row.</summary>
public sealed class SessionFigures
{
   #region Public Methods

   /// <summary>Lowest run value.</summary>
   public double Min { get; set; }

   /// <summary>Highest run value.</summary>
   public double Max { get; set; }

   /// <summary>Every run's value, oldest run first.</summary>
   public List<double> Runs { get; set; } = new();

   #endregion Public Methods
}

/// <summary>A flag on a row.</summary>
public sealed class RowFlag
{
   #region Public Methods

   /// <summary>Code (<see cref="ConsolidateFlags.CODES"/>).</summary>
   public string Code { get; set; } = string.Empty;

   /// <summary>The flag's sentence, audited like every sentence of sentences[]; the page prints it here, so it is not repeated there.</summary>
   public string Text { get; set; } = string.Empty;

   /// <summary>The sources of <see cref="Text"/>, as the audit checked them.</summary>
   public List<SourceRecord> Sources { get; set; } = new();

   #endregion Public Methods
}

/// <summary>The guards.</summary>
public sealed class GuardsInfo
{
   #region Public Methods

   /// <summary>G2.</summary>
   public GuardG2 G2 { get; set; } = new();

   /// <summary>G3.</summary>
   public GuardG3 G3 { get; set; } = new();

   #endregion Public Methods
}

/// <summary>Guard G2: a pair ordered in the first session alone whose medians are reversed in the second stops the report.</summary>
public sealed class GuardG2
{
   #region Public Methods

   /// <summary>True when it fired.</summary>
   public bool Stopped { get; set; }

   /// <summary>The pairs that fired it.</summary>
   public List<ReversedPair> Pairs { get; set; } = new();

   #endregion Public Methods
}

/// <summary>A pair that fired G2.</summary>
public sealed class ReversedPair
{
   #region Public Methods

   /// <summary>Metric id.</summary>
   public string Metric { get; set; } = string.Empty;

   /// <summary>The target ordered ahead in <see cref="OrderedIn"/>.</summary>
   public string A { get; set; } = string.Empty;

   /// <summary>The target it was ahead of.</summary>
   public string B { get; set; } = string.Empty;

   /// <summary>The session where A was ahead by the rule.</summary>
   public string OrderedIn { get; set; } = string.Empty;

   /// <summary>The session where B's median is better than A's.</summary>
   public string ReversedIn { get; set; } = string.Empty;

   /// <summary>A's and B's medians in <see cref="ReversedIn"/>.</summary>
   public List<double> MediansReversed { get; set; } = new();

   #endregion Public Methods
}

/// <summary>Guard G3: targets whose recorded setup differs between the sessions.</summary>
public sealed class GuardG3
{
   #region Public Methods

   /// <summary>True when more than <see cref="Consolidator.MAX_SETUP_CHANGES"/> targets changed.</summary>
   public bool Stopped { get; set; }

   /// <summary>The changed targets with their fields.</summary>
   public List<SetupChange> Targets { get; set; } = new();

   /// <summary>How many targets changed.</summary>
   public int ChangedCount { get; set; }

   /// <summary>Most changed targets before the report stops (<see cref="Consolidator.MAX_SETUP_CHANGES"/>).</summary>
   public int Limit { get; set; }

   #endregion Public Methods
}

/// <summary>A target whose setup changed between the sessions.</summary>
public sealed class SetupChange
{
   #region Public Methods

   /// <summary>Target.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>The fields that differ.</summary>
   public List<string> Fields { get; set; } = new();

   #endregion Public Methods
}

/// <summary>Drift from the first claim session to the second.</summary>
public sealed class DriftInfo
{
   #region Public Methods

   /// <summary>First session.</summary>
   public string From { get; set; } = string.Empty;

   /// <summary>Second session.</summary>
   public string To { get; set; } = string.Empty;

   /// <summary>Per ranked target and metric: the move of its session median, bp (positive = larger in the second session).</summary>
   public List<DriftMove> PerTarget { get; set; } = new();

   /// <summary>Median of the absolute moves, bp.</summary>
   public int MedianAbsMoveBp { get; set; }

   /// <summary>The largest absolute move.</summary>
   public DriftMove? Largest { get; set; }

   /// <summary>The largest absolute move, bp, without its sign.</summary>
   public int LargestAbsMoveBp { get; set; }

   /// <summary>The lower end of the close-to-line band, bp above level (2500 at the 35% threshold).</summary>
   public int CloseFromBp { get; set; }

   /// <summary>Pairs in <see cref="CloseToLine"/>.</summary>
   public int CloseCount { get; set; }

   /// <summary>Orders that held in one session and not in the other.</summary>
   public List<UnconfirmedOrder> UnconfirmedOrders { get; set; } = new();

   /// <summary>Unordered pairs whose lower ratio in every session lies within <see cref="ClaimRule.CLOSE_BELOW_BP"/> below the line, in one direction.</summary>
   public List<CloseToLine> CloseToLine { get; set; } = new();

   /// <summary>How close above the line, bp, an ordered pair still counts as on it (<see cref="ClaimRule.ON_LINE_BP"/>).</summary>
   public int OnLineBp { get; set; }

   /// <summary>Pairs in <see cref="OnLine"/>.</summary>
   public int OnLineCount { get; set; }

   /// <summary>Ordered pairs whose lowest per-session ratio clears the line by <see cref="OnLineBp"/> or less: published orders that a hair's drift would remove.</summary>
   public List<CloseToLine> OnLine { get; set; } = new();

   /// <summary>How many unconfirmed orders held in the first session alone.</summary>
   public int UnconfirmedFromFirst { get; set; }

   /// <summary>How many unconfirmed orders held in the second session alone.</summary>
   public int UnconfirmedFromSecond { get; set; }

   /// <summary>
   /// What the two sessions differ in, among the build that ran them, the day they started on and the outside load their passes saw ("build", "day", "outside load"); empty when they differ in none of them.
   /// Why: drift between sessions is only the engines' own when nothing else about the sessions differs, and the sessions differ in these.
   /// </summary>
   public List<string> SessionDifferences { get; set; } = new();

   /// <summary>Each session's outside load: the median of a run's passes' outside load during the pass, per run.</summary>
   public List<SessionOutsideLoad> OutsideLoad { get; set; } = new();

   #endregion Public Methods
}

/// <summary>The outside load a session's passes saw: for each run the median over its passes of the load outside the benchmark during the pass.</summary>
public sealed class SessionOutsideLoad
{
   #region Public Methods

   /// <summary>The session.</summary>
   public string Session { get; set; } = string.Empty;

   /// <summary>One entry per run.</summary>
   public List<RunOutsideLoad> Runs { get; set; } = new();

   /// <summary>The lowest run median, CPUs.</summary>
   public double MinCpus { get; set; }

   /// <summary>The highest run median, CPUs.</summary>
   public double MaxCpus { get; set; }

   #endregion Public Methods
}

/// <summary>One run's median outside load.</summary>
public sealed class RunOutsideLoad
{
   #region Public Methods

   /// <summary>Run folder.</summary>
   public string Run { get; set; } = string.Empty;

   /// <summary>Median over the run's passes of outsideLoadDuring, CPUs.</summary>
   public double MedianCpus { get; set; }

   /// <summary>Passes behind the median.</summary>
   public int Passes { get; set; }

   #endregion Public Methods
}

/// <summary>One drift move.</summary>
public sealed class DriftMove
{
   #region Public Methods

   /// <summary>Target.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>Metric id.</summary>
   public string Metric { get; set; } = string.Empty;

   /// <summary>Move, bp, rounded half away from zero.</summary>
   public int MoveBp { get; set; }

   #endregion Public Methods
}

/// <summary>An order that held in one session only.</summary>
public sealed class UnconfirmedOrder
{
   #region Public Methods

   /// <summary>Metric id.</summary>
   public string Metric { get; set; } = string.Empty;

   /// <summary>The target ahead.</summary>
   public string A { get; set; } = string.Empty;

   /// <summary>The target behind.</summary>
   public string B { get; set; } = string.Empty;

   /// <summary>The session where it held.</summary>
   public string Session { get; set; } = string.Empty;

   #endregion Public Methods
}

/// <summary>A pair close below the line.</summary>
public sealed class CloseToLine
{
   #region Public Methods

   /// <summary>Metric id.</summary>
   public string Metric { get; set; } = string.Empty;

   /// <summary>The target ahead in every session.</summary>
   public string A { get; set; } = string.Empty;

   /// <summary>The target behind.</summary>
   public string B { get; set; } = string.Empty;

   /// <summary>The lowest per-session ratio, bp.</summary>
   public int MinRatioBp { get; set; }

   #endregion Public Methods
}

/// <summary>Recall of one target.</summary>
public sealed class RecallRow
{
   #region Public Methods

   /// <summary>Target.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>Hits per run, by session.</summary>
   public Dictionary<string, List<int>> Hits { get; set; } = new();

   /// <summary>queryCount x top.</summary>
   public int Of { get; set; }

   /// <summary>True when any two runs differ by one hit or more.</summary>
   public bool Differs { get; set; }

   #endregion Public Methods
}

/// <summary>The clock block.</summary>
public sealed class ClockInfo
{
   #region Public Methods

   /// <summary>Per session.</summary>
   public Dictionary<string, SessionClock> PerSession { get; set; } = new();

   /// <summary>Recorded mean-based clock warnings the median rule does not raise.</summary>
   public List<DroppedWarning> DroppedWarnings { get; set; } = new();

   #endregion Public Methods
}

/// <summary>One session's clock.</summary>
public sealed class SessionClock
{
   #region Public Methods

   /// <summary>True when every run held the clock.</summary>
   public bool Pinned { get; set; }

   /// <summary>The pinned clock, MHz, when every run agrees.</summary>
   public int? PinnedMhz { get; set; }

   /// <summary>no_turbo during the runs (1 = off), when every run agrees.</summary>
   public int? NoTurbo { get; set; }

   /// <summary>MSR 0x620 during the runs, when every run agrees.</summary>
   public string? Uncore { get; set; }

   /// <summary>Every CPU's ceiling before the runs, MHz, when every run agrees.</summary>
   public int? CeilingBeforeMhz { get; set; }

   /// <summary>The clock tolerance, bp, when every run agrees.</summary>
   public int? ToleranceBp { get; set; }

   /// <summary>
   /// The ratio the pinned clock is, MHz over <see cref="ConsolidateClock.RATIO_STEP_MHZ"/> (35 for 3500 MHz), when the pin is a whole number of steps; null otherwise.
   /// Why: the pin is the CPU's top ratio with turbo off, and the kernel's figure for that ratio is not the nominal MHz (it reads 3492 for 3500).
   /// </summary>
   public int? PinnedRatio { get; set; }

   /// <summary>The median, over the passes of the session, of the kernel's pass medians for the engine CPUs and the client CPUs together, MHz; null when no pass recorded one.</summary>
   public int? KernelMedianMhz { get; set; }

   /// <summary>The lowest kernel pass median in the session, MHz.</summary>
   public int? KernelMinMhz { get; set; }

   /// <summary>The highest kernel pass median in the session, MHz.</summary>
   public int? KernelMaxMhz { get; set; }

   /// <summary>Passes off the pinned clock by the median rule.</summary>
   public int ClockOffPasses { get; set; }

   /// <summary>Passes with a CPU group not read.</summary>
   public int ClockUnreadPasses { get; set; }

   /// <summary>Passes evaluated.</summary>
   public int PassesEvaluated { get; set; }

   /// <summary>True when the values were read from the machine-control note (v5 to v7), not conditions.clock.</summary>
   public bool LegacyParsed { get; set; }

   #endregion Public Methods
}

/// <summary>A dropped mean-based warning.</summary>
public sealed class DroppedWarning
{
   #region Public Methods

   /// <summary>Run folder.</summary>
   public string Run { get; set; } = string.Empty;

   /// <summary>The warning, verbatim.</summary>
   public string Text { get; set; } = string.Empty;

   /// <summary>Target.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>Pass.</summary>
   public string Pass { get; set; } = string.Empty;

   /// <summary>Recomputed engine-group median, MHz.</summary>
   public int? EngineMedianMhz { get; set; }

   /// <summary>Recomputed client-group median, MHz.</summary>
   public int? ClientMedianMhz { get; set; }

   #endregion Public Methods
}

/// <summary>The machine block.</summary>
public sealed class MachineInfo
{
   #region Public Methods

   /// <summary>machine.cpu.</summary>
   public string? Cpu { get; set; }

   /// <summary>machine.logicalCpus.</summary>
   public int? LogicalCpus { get; set; }

   /// <summary>machine.ramGiB.</summary>
   public double? RamGiB { get; set; }

   /// <summary>machine.os of the newest run.</summary>
   public string? Os { get; set; }

   /// <summary>machine.dotNet of the newest run.</summary>
   public string? DotNet { get; set; }

   /// <summary>conditions.governor.</summary>
   public string? Governor { get; set; }

   /// <summary>The CPU partition.</summary>
   public string? Partition { get; set; }

   /// <summary>Fields that differ between the claim runs without refusing (os, dotNet), as "field: value (runs); value (runs)".</summary>
   public List<string> Differences { get; set; } = new();

   #endregion Public Methods
}

/// <summary>One container target's image.</summary>
public sealed class ImageInfo
{
   #region Public Methods

   /// <summary>Target.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>The image reference.</summary>
   public string? Ref { get; set; }

   /// <summary>The image id.</summary>
   public string Id { get; set; } = string.Empty;

   /// <summary>When the reference was last tagged on this box.</summary>
   public string? LastTagTimeUtc { get; set; }

   /// <summary>True when that was before the first run of the first claim session started; null when unknown.</summary>
   public bool? BeforeFirstV7Start { get; set; }

   #endregion Public Methods
}

/// <summary>A target's engine settings in one session.</summary>
public sealed class EngineSettingsRow
{
   #region Public Methods

   /// <summary>Target.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>Session they were read in.</summary>
   public string Session { get; set; } = string.Empty;

   /// <summary>The settings.</summary>
   public List<EngineSettingEntry> Settings { get; set; } = new();

   #endregion Public Methods
}

/// <summary>One engine setting.</summary>
public sealed class EngineSettingEntry
{
   #region Public Methods

   /// <summary>Key.</summary>
   public string Key { get; set; } = string.Empty;

   /// <summary>Value.</summary>
   public string Value { get; set; } = string.Empty;

   /// <summary>How it was read or set.</summary>
   public string How { get; set; } = string.Empty;

   #endregion Public Methods
}

/// <summary>A row of the why table.</summary>
public sealed class WhyRow
{
   #region Public Methods

   /// <summary>Target.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>Facts from engine-facts.json, validated.</summary>
   public List<WhyFact> Facts { get; set; } = new();

   /// <summary>Measured costs.</summary>
   public WhyCosts Costs { get; set; } = new();

   #endregion Public Methods
}

/// <summary>One fact.</summary>
public sealed class WhyFact
{
   #region Public Methods

   /// <summary>index, search, storage, protocol, cap or set-by-setup.</summary>
   public string Kind { get; set; } = string.Empty;

   /// <summary>The fact's text.</summary>
   public string Text { get; set; } = string.Empty;

   /// <summary>Its source.</summary>
   public string Source { get; set; } = string.Empty;

   /// <summary>
   /// The fact sheet's confidence label: recorded, measured, documented, set-by-code, checked or "recorded (engine's own report)"; a recorded row also names the class
   /// of the clause of the recorded text it rests on, as "recorded, unverified", "recorded, documented" or "recorded, read-back".
   /// </summary>
   public string Confidence { get; set; } = string.Empty;

   /// <summary>How the fact is backed: read-back, measured, documented or unverified (see <see cref="GenericVectorBuilder.Bench.Report.TextClass"/>).</summary>
   public string Class { get; set; } = string.Empty;

   /// <summary>Search rows: exact or approximate.</summary>
   public string? Mode { get; set; }

   /// <summary>Where the source resolved ("path:line", the runs, the saved page).</summary>
   public string Where { get; set; } = string.Empty;

   #endregion Public Methods
}

/// <summary>Measured costs of one target: medians over the runs of <see cref="Session"/>.</summary>
public sealed class WhyCosts
{
   #region Public Methods

   /// <summary>The session the costs come from (the newest).</summary>
   public string Session { get; set; } = string.Empty;

   /// <summary>Engine cgroup CPU per search, ms, by searchers ("1", "8"); null when not measured.</summary>
   public Dictionary<string, double?> EngineCpuMsPerSearch { get; set; } = new();

   /// <summary>Why engine CPU is null, as recorded.</summary>
   public string? EngineCpuNullReason { get; set; }

   /// <summary>The client's CPU per search, ms, by searchers.</summary>
   public Dictionary<string, double?> ClientCpuMsPerSearch { get; set; } = new();

   /// <summary>True for an embedded target, whose client CPU includes the engine's own work.</summary>
   public bool ClientIncludesEngine { get; set; }

   /// <summary>Engine CPUs busy during the eight-searcher pass.</summary>
   public double? EngineCpusBusyAt8 { get; set; }

   #endregion Public Methods
}

/// <summary>The observer summary given with --observer.</summary>
public sealed class ObserverInfo
{
   #region Public Methods

   /// <summary>The claim session the summary covers.</summary>
   public string Session { get; set; } = string.Empty;

   /// <summary>Where the summary is copied beside the report, such as observer/summary.json.</summary>
   public string Copy { get; set; } = string.Empty;

   /// <summary>The path as given.</summary>
   public string Path { get; set; } = string.Empty;

   /// <summary>Its SHA-256.</summary>
   public string Sha256 { get; set; } = string.Empty;

   /// <summary>Its schema field.</summary>
   public string? Schema { get; set; }

   /// <summary>Per run of the newest session the summary covers.</summary>
   public List<ObserverRun> Runs { get; set; } = new();

   /// <summary>Highest observer CPU over every pass of those runs, CPUs.</summary>
   public double? CpuMax { get; set; }

   /// <summary>Largest APERF/MPERF deviation from the pinned clock over those runs, bp.</summary>
   public double? AperfWorstDeviationBp { get; set; }

   /// <summary>Every MSR 0x620 value the observer saw in those runs.</summary>
   public List<string> Msr620ValuesSeen { get; set; } = new();

   /// <summary>True when the summary says timers were covered for every run.</summary>
   public bool TimersCovered { get; set; }

   /// <summary>Passes during which a systemd timer fired, over those runs.</summary>
   public int PassesWithTimerFired { get; set; }

   /// <summary>The clock the summary's per-CPU figures are measured against, MHz, from the session's runs; null when the summary holds no per-CPU figures.</summary>
   public int? PinnedMhz { get; set; }

   /// <summary>The CPU in a pass that lay furthest from the pinned clock over those runs; null when the summary holds no per-CPU figures.</summary>
   public ObserverCpuDeviation? CpuWorst { get; set; }

   /// <summary>The pass that ran under the pin on the CPUs more than any other, over those runs; null when none ran under it or the summary holds no per-CPU figures.</summary>
   public ObserverDip? Dip { get; set; }

   /// <summary>Where the observer's own threads ran, from the saved analysis of each run; null when the session has no saved analysis.</summary>
   public ObserverPlacement? Placement { get; set; }

   /// <summary>Passes whose mean clock the observer sampled outside the clock tolerance (what a mean-based rule flags and the median rule does not); empty when none or no figures.</summary>
   public List<ObserverMeanDip> MeanDips { get; set; } = new();

   #endregion Public Methods
}

/// <summary>Where the observer ran, read from the analysis the run operator saved for each run.</summary>
public sealed class ObserverPlacement
{
   #region Public Methods

   /// <summary>One entry per run of the session.</summary>
   public List<ObserverPlacementRun> Runs { get; set; } = new();

   /// <summary>The most CPU the observer's cgroup used in any run, CPUs, as written.</summary>
   public string MaxCgroupCpu { get; set; } = string.Empty;

   /// <summary>The words of the saved analyses that say the observer was not pinned.</summary>
   public string NotPinnedQuote { get; set; } = string.Empty;

   #endregion Public Methods
}

/// <summary>The placement of the observer in one run.</summary>
public sealed class ObserverPlacementRun
{
   #region Public Methods

   /// <summary>Run folder.</summary>
   public string Folder { get; set; } = string.Empty;

   /// <summary>The saved analysis, relative to the repository.</summary>
   public string File { get; set; } = string.Empty;

   /// <summary>The share of the observer's resident-thread ticks seen on the engine CPUs, percent, as written.</summary>
   public string Percent { get; set; } = string.Empty;

   /// <summary>The engine CPUs the analysis names.</summary>
   public string Cpus { get; set; } = string.Empty;

   /// <summary>The observer's CPU use over the run by cgroup, CPUs, as written.</summary>
   public string CgroupCpu { get; set; } = string.Empty;

   /// <summary>The words of the analysis that carry the share.</summary>
   public string PlacementQuote { get; set; } = string.Empty;

   /// <summary>The words of the analysis that carry the CPU use.</summary>
   public string CgroupQuote { get; set; } = string.Empty;

   #endregion Public Methods
}

/// <summary>One CPU's APERF/MPERF clock in one pass against the pinned clock.</summary>
public sealed class ObserverCpuDeviation
{
   #region Public Methods

   /// <summary>Run folder.</summary>
   public string Run { get; set; } = string.Empty;

   /// <summary>Target.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>Pass.</summary>
   public string Pass { get; set; } = string.Empty;

   /// <summary>CPU number.</summary>
   public int Cpu { get; set; }

   /// <summary>The CPU's mean clock over the pass, MHz.</summary>
   public double Mhz { get; set; }

   /// <summary>How far from the pinned clock, bp, without its sign.</summary>
   public double DeviationBp { get; set; }

   /// <summary>True when the CPU ran under the pinned clock, false when over it.</summary>
   public bool Under { get; set; }

   #endregion Public Methods
}

/// <summary>The pass type that ran under the pinned clock on its CPUs, over the runs of a session.</summary>
public sealed class ObserverDip
{
   #region Public Methods

   /// <summary>Target.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>Pass.</summary>
   public string Pass { get; set; } = string.Empty;

   /// <summary>Runs of the session that hold this pass.</summary>
   public int Runs { get; set; }

   /// <summary>CPU readings of this pass over those runs (one per CPU per run).</summary>
   public int CpuReadings { get; set; }

   /// <summary>Of those, the readings under the pinned clock.</summary>
   public int CpusUnder { get; set; }

   /// <summary>True when every reading was under the pinned clock.</summary>
   public bool EveryCpuUnder { get; set; }

   /// <summary>The smallest deviation under the pin among those readings, bp.</summary>
   public double MinBp { get; set; }

   /// <summary>The largest deviation under the pin among those readings, bp.</summary>
   public double MaxBp { get; set; }

   /// <summary>The largest deviation, either way, of any CPU in any other pass of those runs, bp.</summary>
   public double OtherWorstBp { get; set; }

   #endregion Public Methods
}

/// <summary>A pass whose sampled mean clock lay outside the clock tolerance of the pin.</summary>
public sealed class ObserverMeanDip
{
   #region Public Methods

   /// <summary>Run folder.</summary>
   public string Run { get; set; } = string.Empty;

   /// <summary>Target.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>Pass.</summary>
   public string Pass { get; set; } = string.Empty;

   /// <summary>Mean of the engine CPUs' sampled clock over the pass, MHz.</summary>
   public double EngineMeanMhz { get; set; }

   /// <summary>Mean of the client CPUs' sampled clock over the pass, MHz.</summary>
   public double ClientMeanMhz { get; set; }

   #endregion Public Methods
}

/// <summary>What the observer summary says about one run.</summary>
public sealed class ObserverRun
{
   #region Public Methods

   /// <summary>Run folder.</summary>
   public string Folder { get; set; } = string.Empty;

   /// <summary>Highest observer CPU over the run's passes, CPUs.</summary>
   public double? ObserverCpuMax { get; set; }

   /// <summary>Largest APERF/MPERF deviation from the pinned clock over the passes, bp.</summary>
   public double? AperfWorstDeviationBp { get; set; }

   /// <summary>MSR 0x620 values the observer saw.</summary>
   public List<string> Msr620ValuesSeen { get; set; } = new();

   /// <summary>Passes during which a systemd timer fired, as the summary lists them.</summary>
   public int? PassesWithTimerFired { get; set; }

   /// <summary>True when the summary says timers were covered.</summary>
   public bool? TimersCovered { get; set; }

   /// <summary>The CPU in a pass that lay furthest from the pinned clock in this run; null when the summary holds no per-CPU figures.</summary>
   public ObserverCpuDeviation? CpuWorst { get; set; }

   #endregion Public Methods
}

/// <summary>A sentence and its sources, as the contract writes it.</summary>
public sealed class SentenceRecord
{
   #region Public Methods

   /// <summary>Where the sentence is printed.</summary>
   public string Slot { get; set; } = string.Empty;

   /// <summary>The sentence.</summary>
   public string Text { get; set; } = string.Empty;

   /// <summary>Its sources.</summary>
   public List<SourceRecord> Sources { get; set; } = new();

   #endregion Public Methods
}

/// <summary>A source of a sentence.</summary>
public sealed class SourceRecord
{
   #region Public Methods

   /// <summary>results, file, doc, consolidated, quote or absent.</summary>
   public string Kind { get; set; } = string.Empty;

   /// <summary>The reference.</summary>
   public string Ref { get; set; } = string.Empty;

   /// <summary>The value the sentence carries for this source.</summary>
   public string Value { get; set; } = string.Empty;

   #endregion Public Methods
}

/// <summary>The audit.</summary>
public sealed class AuditInfo
{
   #region Public Methods

   /// <summary>Sentences checked: every one of sentences[] and every row note and flag text.</summary>
   public int SentencesChecked { get; set; }

   /// <summary>Of those, the row notes and flag texts, which travel on their rows and not in sentences[].</summary>
   public int RowSentencesChecked { get; set; }

   /// <summary>Fact rows validated.</summary>
   public int FactsChecked { get; set; }

   /// <summary>Failures (always empty in a written report: a failure writes nothing).</summary>
   public List<string> Failures { get; set; } = new();

   /// <summary>SHA-256 of engine-facts.json.</summary>
   public string FactsSha256 { get; set; } = string.Empty;

   /// <summary>SHA-256 of basis-exclusions.json.</summary>
   public string ExclusionsSha256 { get; set; } = string.Empty;

   /// <summary>SHA-256 of the observer summary given, or null.</summary>
   public string? ObserverSha256 { get; set; }

   /// <summary>SHA-256 of the observer summary of each older session that has one, by session name.</summary>
   public Dictionary<string, string> ObserverSha256Others { get; set; } = new();

   /// <summary>SHA-256 of recorded-text-classes.json.</summary>
   public string ClassesSha256 { get; set; } = string.Empty;

   /// <summary>The recorded clauses the list did not classify and the report printed as unverified, each as "where: clause"; empty when every clause was classified.</summary>
   public List<string> UnlistedClauses { get; set; } = new();

   #endregion Public Methods
}

/// <summary>The queries block.</summary>
public sealed class QueriesInfo
{
   #region Public Methods

   /// <summary>The queries line of the first claim run, verbatim.</summary>
   public string? Recorded { get; set; }

   /// <summary>Repository path of the copied question file.</summary>
   public string CopyPath { get; set; } = string.Empty;

   /// <summary>SHA-256 of the copy, or null when the repository has none.</summary>
   public string? CopySha256 { get; set; }

   /// <summary>Claim runs whose recorded queriesFileSha256 equals the copy's.</summary>
   public List<string> MatchingRuns { get; set; } = new();

   /// <summary>Claim runs that recorded no queriesFileSha256.</summary>
   public List<string> UnrecordedRuns { get; set; } = new();

   #endregion Public Methods
}

/// <summary>A session (claim or basis) whose runs an unpublished report also used.</summary>
public sealed class ReuseNote
{
   #region Public Methods

   /// <summary>Session name.</summary>
   public string Session { get; set; } = string.Empty;

   /// <summary>Repository path of the verdict that blocked that report.</summary>
   public string Verdict { get; set; } = string.Empty;

   /// <summary>The blocked report's folder names found beside the runs (blocked-DATE-session); empty when none was found.</summary>
   public List<string> BlockedFolders { get; set; } = new();

   /// <summary>The session's runs (folder names), so a page can say it on the run pages of exactly these runs.</summary>
   public List<string> Runs { get; set; } = new();

   #endregion Public Methods
}

/// <summary>One recorded text as the report prints it, split into the clauses its classes cover.</summary>
public sealed class RecordedTextRecord
{
   #region Public Methods

   /// <summary>The target, or empty for a run note.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>durability, index or notes.</summary>
   public string Field { get; set; } = string.Empty;

   /// <summary>The run the text was read from.</summary>
   public string Run { get; set; } = string.Empty;

   /// <summary>For a run note, its position in the run's notes; null otherwise.</summary>
   public int? Note { get; set; }

   /// <summary>The clauses in text order.</summary>
   public List<RecordedClause> Clauses { get; set; } = new();

   #endregion Public Methods
}

/// <summary>One clause of a recorded text with its class and what backs it.</summary>
public sealed class RecordedClause
{
   #region Public Methods

   /// <summary>The clause, verbatim from the recorded text; for a clause the report does not print, only its first characters, so the table does not print the clause the report dropped.</summary>
   public string Text { get; set; } = string.Empty;

   /// <summary>The length of the whole clause in the white-space-collapsed recorded text, so the clauses together can be checked against the recorded text.</summary>
   public int Length { get; set; }

   /// <summary>read-back, measured, documented or unverified.</summary>
   public string Class { get; set; } = string.Empty;

   /// <summary>The sources that back it; empty when unverified.</summary>
   public List<string> Basis { get; set; } = new();

   /// <summary>True when the report prints the clause; false when it drops it (and says so).</summary>
   public bool Printed { get; set; } = true;

   /// <summary>Why the report does not print the clause, or null when it does.</summary>
   public string? NotPrinted { get; set; }

   /// <summary>True when no entry of the list covered the text and the clause stands for all of it.</summary>
   public bool Unlisted { get; set; }

   #endregion Public Methods
}
