namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// Several benchmark runs folded into one set of numbers, written as consolidated.json and
/// consolidated.md.
/// Why it keeps the runs used, the runs dropped and every per-run value: a median is only
/// checkable when the reader can see which runs fed it and which were left out.
/// </summary>
public sealed class ConsolidatedReport
{
   #region Public Methods

   /// <summary>When the consolidation was made (UTC, ISO 8601).</summary>
   public string CreatedUtc { get; set; } = string.Empty;

   /// <summary>The consolidate command line.</summary>
   public string CommandLine { get; set; } = string.Empty;

   /// <summary>Targets asked for, in the order given.</summary>
   public List<string> Targets { get; set; } = new();

   /// <summary>Settings every run used shares (pipeline, host, data, queries, levels).</summary>
   public RunSettings Settings { get; set; } = new();

   /// <summary>Runs used, oldest first; every per-run list in this report follows this order.</summary>
   public List<RunUsed> Runs { get; set; } = new();

   /// <summary>Runs left out and why.</summary>
   public List<RunDropped> Dropped { get; set; } = new();

   /// <summary>One summary per target, in the order asked for.</summary>
   public List<TargetSummary> TargetSummaries { get; set; } = new();

   /// <summary>Two targets compared run by run (e.g. sql-diskann against sql).</summary>
   public List<PairSummary> Pairs { get; set; } = new();

   /// <summary>Each engine's exact mode against its default search, run by run.</summary>
   public List<ExactVsDefault> ExactVsDefault { get; set; } = new();

   /// <summary>Everything a reader must see before trusting a number (index not ready, durability not stated, errors, spread, machine conditions).</summary>
   public List<Flag> Flags { get; set; } = new();

   /// <summary>Rules the numbers follow that a reader needs next to them, e.g. why load rows/s is not ranked.</summary>
   public List<string> Notes { get; set; } = new();

   #endregion Public Methods
}

/// <summary>
/// The settings that must match before runs can be summarized together.
/// Why: a median over runs with different query sets or machines mixes two experiments.
/// </summary>
public sealed class RunSettings
{
   #region Public Methods

   /// <summary>Source pipeline.</summary>
   public string? Pipeline { get; set; }

   /// <summary>Machine.</summary>
   public string? Host { get; set; }

   /// <summary>Rows used.</summary>
   public int? Rows { get; set; }

   /// <summary>Vector length.</summary>
   public int? Dimension { get; set; }

   /// <summary>golden or random.</summary>
   public string? QueryKind { get; set; }

   /// <summary>Number of queries.</summary>
   public int? QueryCount { get; set; }

   /// <summary>Hits per query.</summary>
   public int? Top { get; set; }

   /// <summary>Concurrency levels.</summary>
   public List<int> Concurrency { get; set; } = new();

   /// <summary>Seconds per concurrency level.</summary>
   public int? SecondsPerLevel { get; set; }

   /// <summary>Build configuration of the benchmark client ("Release" or "Debug"); null when the runs did not record it.</summary>
   public string? BuildConfiguration { get; set; }

   /// <summary>"on", or "off" with the reason: whether the machine was held steady during the runs (governor, CPU split, quiet-box waits); null when not recorded.</summary>
   public string? MachineControl { get; set; }

   /// <summary>CPU governor during the runs; null when not recorded.</summary>
   public string? Governor { get; set; }

   /// <summary>CPU partition (which CPUs the client and the engines could use) as one text; null when not recorded.</summary>
   public string? CpuPartition { get; set; }

   /// <summary>Untimed warm-up searches before every timed pass; null when not recorded.</summary>
   public int? WarmupSearches { get; set; }

   /// <summary>Seconds each engine's exact mode was allowed; null when not recorded.</summary>
   public int? ExactSeconds { get; set; }

   /// <summary>Search-effort settings by target (e.g. "hnsw_ef=100"); null where the runs did not record them.</summary>
   public Dictionary<string, string?> SearchSettings { get; set; } = new();

   /// <summary>Fields above that were derived from the command line or notes instead of recorded, with the source.</summary>
   public Dictionary<string, string> Derived { get; set; } = new();

   #endregion Public Methods
}

/// <summary>
/// One run that fed the numbers.
/// Why the seed and target order are carried: they say whether the run's order was fixed or
/// shuffled, which decides whether order can explain a gap.
/// </summary>
public sealed class RunUsed
{
   #region Public Methods

   /// <summary>Folder name (the run's label).</summary>
   public string Name { get; set; } = string.Empty;

   /// <summary>Full folder path.</summary>
   public string Folder { get; set; } = string.Empty;

   /// <summary>When the run started (UTC).</summary>
   public string? StartedUtc { get; set; }

   /// <summary>Seed of the run's shuffles; null when the run did not record one.</summary>
   public int? RunSeed { get; set; }

   /// <summary>Target order actually run; null when not recorded.</summary>
   public List<string>? TargetOrder { get; set; }

   /// <summary>Load average at the start of the run.</summary>
   public string? LoadAverage { get; set; }

   #endregion Public Methods
}

/// <summary>
/// A run that was left out and the reason.
/// Why logged and written: a silently dropped run is how a median gets cherry-picked.
/// </summary>
public sealed class RunDropped
{
   #region Public Methods

   /// <summary>Folder name.</summary>
   public string Name { get; set; } = string.Empty;

   /// <summary>Full folder path.</summary>
   public string Folder { get; set; } = string.Empty;

   /// <summary>Why it was left out.</summary>
   public string Reason { get; set; } = string.Empty;

   #endregion Public Methods
}

/// <summary>
/// Everything consolidated for one target.
/// </summary>
public sealed class TargetSummary
{
   #region Public Methods

   /// <summary>Target name.</summary>
   public string Name { get; set; } = string.Empty;

   /// <summary>Distinct engine strings seen across the runs (more than one means the engine changed).</summary>
   public List<string> Engines { get; set; } = new();

   /// <summary>Distinct index descriptions seen across the runs.</summary>
   public List<string> Indexes { get; set; } = new();

   /// <summary>Distinct hosting values seen.</summary>
   public List<string> Hosting { get; set; } = new();

   /// <summary>Median latency, ms.</summary>
   public Spread P50Ms { get; set; } = new();

   /// <summary>95th percentile latency, ms.</summary>
   public Spread P95Ms { get; set; } = new();

   /// <summary>QPS by concurrency level ("1", "8").</summary>
   public Dictionary<string, Spread> Qps { get; set; } = new();

   /// <summary>Per-run QPS ratio of each higher level to the lowest ("8/1"), computed inside each run.</summary>
   public Dictionary<string, Spread> QpsRatio { get; set; } = new();

   /// <summary>Load rows per second.</summary>
   public Spread LoadRowsPerSecond { get; set; } = new();

   /// <summary>Recall@k.</summary>
   public Spread Recall { get; set; } = new();

   /// <summary>nDCG@k.</summary>
   public Spread Ndcg { get; set; } = new();

   /// <summary>Exact-mode median latency, ms.</summary>
   public Spread ExactP50Ms { get; set; } = new();

   /// <summary>Timed search errors summed over the runs (null when no run wrote the field).</summary>
   public int? Errors { get; set; }

   /// <summary>Warm-up errors summed over the runs that recorded them (null when none did).</summary>
   public int? WarmupErrors { get; set; }

   /// <summary>Rank among the listed targets in each run, by metric ("p50Ms", "qps@8", ...).</summary>
   public Dictionary<string, RankSpread> Ranks { get; set; } = new();

   /// <summary>Carry-through facts from each run used, in run order.</summary>
   public List<TargetRunFacts> PerRun { get; set; } = new();

   #endregion Public Methods
}

/// <summary>
/// A target's rank in each run and the median of those ranks.
/// Why ranks per run and not the rank of the medians: a target that is 3rd, 7th and 7th is
/// not "7th in every run", and the per-run list shows it.
/// </summary>
public sealed class RankSpread
{
   #region Public Methods

   /// <summary>1 for the best of the listed targets in that run; null when the metric was missing.</summary>
   public List<int?> PerRun { get; set; } = new();

   /// <summary>Median of the per-run ranks.</summary>
   public double? Median { get; set; }

   #endregion Public Methods
}

/// <summary>
/// What one run recorded about how a target was run.
/// Why carried through unchanged: pass order, warm-up errors, index proof and durability decide
/// whether a target's numbers are comparable at all.
/// </summary>
public sealed class TargetRunFacts
{
   #region Public Methods

   /// <summary>Run label.</summary>
   public string Run { get; set; } = string.Empty;

   /// <summary>Position of the target in the run's target order (1 = first); null when not recorded.</summary>
   public int? OrderInRun { get; set; }

   /// <summary>Passes in the order run; null when not recorded.</summary>
   public List<string>? PassOrder { get; set; }

   /// <summary>Warm-up searches that failed; null when not recorded.</summary>
   public int? WarmupErrors { get; set; }

   /// <summary>Timed searches that failed.</summary>
   public int? Errors { get; set; }

   /// <summary>Exact-mode recall (should be 1.0; anything else means the engine's exact mode or the yardstick is wrong).</summary>
   public double? ExactRecall { get; set; }

   /// <summary>Index state after the load; null when not recorded.</summary>
   public IndexStateFacts? AfterLoad { get; set; }

   /// <summary>Index state after the searches; null when not recorded.</summary>
   public IndexStateFacts? AfterSearch { get; set; }

   /// <summary>Durability statement; null when not recorded.</summary>
   public string? Durability { get; set; }

   /// <summary>What the load's index step reported; null when the run wrote none. Why carried: for runs made before indexState existed it is the only index evidence.</summary>
   public string? LoadIndexNote { get; set; }

   /// <summary>The target's search-effort settings in this run; null when not recorded.</summary>
   public string? SearchSettings { get; set; }

   /// <summary>True when the engine was idle and warm before it was timed; null when not recorded.</summary>
   public bool? Settled { get; set; }

   /// <summary>The run's own words on settling; null when not recorded.</summary>
   public string? SettleDetail { get; set; }

   /// <summary>Mean latency of one search at a time, ms: as recorded, or 1000 / QPS at one searcher; null when neither exists.</summary>
   public double? MeanMs { get; set; }

   /// <summary>Where <see cref="MeanMs"/> came from: "recorded" or "1000 / QPS@1".</summary>
   public string? MeanSource { get; set; }

   #endregion Public Methods
}

/// <summary>
/// An engine's account of its index, as carried into the consolidated report.
/// </summary>
public sealed class IndexStateFacts
{
   #region Public Methods

   /// <summary>True when the engine said searches used the finished index; null when not written.</summary>
   public bool? Ready { get; set; }

   /// <summary>Vectors covered by the index.</summary>
   public long? IndexedVectors { get; set; }

   /// <summary>Vectors stored.</summary>
   public long? TotalVectors { get; set; }

   /// <summary>The engine's evidence text.</summary>
   public string? Detail { get; set; }

   #endregion Public Methods
}

/// <summary>
/// Target A against target B, computed inside each run and then summarized.
/// Why never a ratio of separate medians: two medians can come from different runs, and the
/// gap between them can be larger or smaller than the gap in any single run.
/// </summary>
public sealed class PairSummary
{
   #region Public Methods

   /// <summary>Target A (the numerator of every ratio).</summary>
   public string A { get; set; } = string.Empty;

   /// <summary>Target B (the denominator).</summary>
   public string B { get; set; } = string.Empty;

   /// <summary>The paired values of each run.</summary>
   public List<PairRun> Runs { get; set; } = new();

   /// <summary>A p50 / B p50, per run then summarized.</summary>
   public Spread P50Ratio { get; set; } = new();

   /// <summary>A p95 / B p95.</summary>
   public Spread P95Ratio { get; set; } = new();

   /// <summary>A QPS / B QPS by level.</summary>
   public Dictionary<string, Spread> QpsRatio { get; set; } = new();

   /// <summary>A recall minus B recall.</summary>
   public Spread RecallDifference { get; set; } = new();

   /// <summary>A nDCG minus B nDCG.</summary>
   public Spread NdcgDifference { get; set; } = new();

   /// <summary>Runs where A had the lower p50.</summary>
   public int RunsALowerP50 { get; set; }

   /// <summary>Runs where A had the higher QPS, by level.</summary>
   public Dictionary<string, int> RunsAHigherQps { get; set; } = new();

   /// <summary>Runs where A ran before B (from the target order); null when no run recorded its order.</summary>
   public int? RunsARanFirst { get; set; }

   #endregion Public Methods
}

/// <summary>
/// One run's values for a pair.
/// </summary>
public sealed class PairRun
{
   #region Public Methods

   /// <summary>Run label.</summary>
   public string Run { get; set; } = string.Empty;

   /// <summary>A p50, ms.</summary>
   public double? AP50Ms { get; set; }

   /// <summary>B p50, ms.</summary>
   public double? BP50Ms { get; set; }

   /// <summary>A p50 / B p50.</summary>
   public double? P50Ratio { get; set; }

   /// <summary>A p95 / B p95.</summary>
   public double? P95Ratio { get; set; }

   /// <summary>A QPS by level.</summary>
   public Dictionary<string, double?> AQps { get; set; } = new();

   /// <summary>B QPS by level.</summary>
   public Dictionary<string, double?> BQps { get; set; } = new();

   /// <summary>A QPS / B QPS by level.</summary>
   public Dictionary<string, double?> QpsRatio { get; set; } = new();

   /// <summary>A recall minus B recall.</summary>
   public double? RecallDifference { get; set; }

   /// <summary>A nDCG minus B nDCG.</summary>
   public double? NdcgDifference { get; set; }

   /// <summary>A's position in the run's target order (1 = first); null when not recorded.</summary>
   public int? AOrder { get; set; }

   /// <summary>B's position in the run's target order; null when not recorded.</summary>
   public int? BOrder { get; set; }

   #endregion Public Methods
}

/// <summary>
/// One target's exact mode against its default search, run by run.
/// Why per run: the dead draft printed a median of each and the direction it implied was wrong
/// for most runs.
/// </summary>
public sealed class ExactVsDefault
{
   #region Public Methods

   /// <summary>Target name.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>The paired values of each run that timed the exact mode.</summary>
   public List<ExactRun> Runs { get; set; } = new();

   /// <summary>Exact p50 / default p50.</summary>
   public Spread Ratio { get; set; } = new();

   /// <summary>Exact p50 minus default p50, ms.</summary>
   public Spread DifferenceMs { get; set; } = new();

   /// <summary>Runs where the exact p50 was higher than the default p50.</summary>
   public int RunsExactSlower { get; set; }

   /// <summary>The pair of passes this target declared as its fair default comparison, e.g. "default@1 vs exact"; null when it declared none.</summary>
   public string? DeclaredPair { get; set; }

   /// <summary>What makes the declared pair fair, in the target's words (e.g. "same table, only the search method differs"); null when it declared none.</summary>
   public string? Note { get; set; }

   #endregion Public Methods
}

/// <summary>
/// One run's exact and default p50 for a target.
/// </summary>
public sealed class ExactRun
{
   #region Public Methods

   /// <summary>Run label.</summary>
   public string Run { get; set; } = string.Empty;

   /// <summary>Default search p50, ms.</summary>
   public double? DefaultP50Ms { get; set; }

   /// <summary>Exact mode p50, ms.</summary>
   public double? ExactP50Ms { get; set; }

   /// <summary>Exact minus default, ms.</summary>
   public double? DifferenceMs { get; set; }

   /// <summary>Exact / default.</summary>
   public double? Ratio { get; set; }

   /// <summary>Exact-mode recall.</summary>
   public double? ExactRecall { get; set; }

   /// <summary>True when the exact pass ran before the default@1 latency pass; null when the pass order was not recorded.</summary>
   public bool? ExactRanFirst { get; set; }

   #endregion Public Methods
}

/// <summary>
/// Something a reader must see before trusting a target's numbers.
/// </summary>
public sealed class Flag
{
   #region Public Methods

   /// <summary>Target name.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>Kind, e.g. "index-not-ready-after-load", "spread" or "governor-not-performance".</summary>
   public string Kind { get; set; } = string.Empty;

   /// <summary>Runs it applies to.</summary>
   public List<string> Runs { get; set; } = new();

   /// <summary>Evidence from the result files (the engine's detail text, counts), never a cause.</summary>
   public string Detail { get; set; } = string.Empty;

   #endregion Public Methods
}
