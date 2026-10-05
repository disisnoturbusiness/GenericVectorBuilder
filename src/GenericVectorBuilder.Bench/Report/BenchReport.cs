using GenericVectorBuilder.Bench.Targets;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Bench.Report;

/// <summary>
/// Everything one benchmark run measured, written as results.json and results.md.
/// Why the run carries the command line, the machine and the query rules: a number without
/// them cannot be reproduced or compared with a later run.
/// </summary>
public sealed class BenchReport
{
   #region Public Methods

   /// <summary>When the run started (UTC, ISO 8601).</summary>
   public string StartedUtc { get; set; } = string.Empty;

   /// <summary>The command: replicate, bench or run-all.</summary>
   public string Command { get; set; } = string.Empty;

   /// <summary>The exact command line.</summary>
   public string CommandLine { get; set; } = string.Empty;

   /// <summary>Hardware and software of the box.</summary>
   public MachineFacts Machine { get; set; } = new();

   /// <summary>Source pipeline.</summary>
   public string Pipeline { get; set; } = string.Empty;

   /// <summary>Collection every target was loaded under.</summary>
   public string Collection { get; set; } = string.Empty;

   /// <summary>Rows used.</summary>
   public int Rows { get; set; }

   /// <summary>Vector length.</summary>
   public int Dimension { get; set; }

   /// <summary>How the source was read and how long it took.</summary>
   public string Source { get; set; } = string.Empty;

   /// <summary>How the queries were made.</summary>
   public string Queries { get; set; } = string.Empty;

   /// <summary>Number of queries.</summary>
   public int QueryCount { get; set; }

   /// <summary>Hits per query.</summary>
   public int Top { get; set; }

   /// <summary>Concurrency levels of the throughput test.</summary>
   public IReadOnlyList<int> Concurrency { get; set; } = Array.Empty<int>();

   /// <summary>Seconds per concurrency level.</summary>
   public int SecondsPerLevel { get; set; }

   /// <summary>Seconds the in-memory brute force took for all queries.</summary>
   public double? TruthSeconds { get; set; }

   /// <summary>nDCG@k of the exact answer itself (golden only): the best any engine can score with this embedder.</summary>
   public double? TruthNdcg { get; set; }

   /// <summary>Seed that shuffled the target order and every target's pass order (--seed repeats it).</summary>
   public int RunSeed { get; set; }

   /// <summary>Targets in the order they actually ran.</summary>
   public List<string> TargetOrder { get; set; } = new();

   /// <summary>Run-level notes (method, caveats, problems).</summary>
   public List<string> Notes { get; set; } = new();

   /// <summary>One entry per target.</summary>
   public List<TargetReport> Targets { get; set; } = new();

   #endregion Public Methods
}

/// <summary>
/// What was measured for one target.
/// </summary>
public sealed class TargetReport
{
   #region Public Methods

   /// <summary>Target name.</summary>
   public string Name { get; set; } = string.Empty;

   /// <summary>Engine name and version.</summary>
   public string Engine { get; set; } = string.Empty;

   /// <summary>Index used by the default search.</summary>
   public string Index { get; set; } = string.Empty;

   /// <summary>compose, always-on or embedded.</summary>
   public string Hosting { get; set; } = string.Empty;

   /// <summary>
   /// The settings that set how the index was built and how hard the engine searched (search beam,
   /// probes, ef, M), by name, as the engine's own index description states them; null when the
   /// target was not searched. Why: two runs at another effort are different experiments, and the
   /// consolidated report refuses to average them only when the runs say what they were run at.
   /// </summary>
   public SortedDictionary<string, string>? SearchSettings { get; set; }

   /// <summary>Load measurements, when this run loaded the target.</summary>
   public LoadReport? Load { get; set; }

   /// <summary>Search measurements, when this run searched the target.</summary>
   public SearchReport? Search { get; set; }

   /// <summary>Memory reading after the load.</summary>
   public Measurement? Ram { get; set; }

   /// <summary>Disk reading after the load.</summary>
   public Measurement? Disk { get; set; }

   /// <summary>Timed passes in the order they actually ran, e.g. "default@1", "exact", "default@8"; null when the target was not searched.</summary>
   public List<string>? PassOrder { get; set; }

   /// <summary>Warm-up searches that failed, over every warm-up of this target; null when the target was not searched.</summary>
   public int? WarmupErrors { get; set; }

   /// <summary>The engine's own account of its index after the load and after the searches.</summary>
   public TargetIndexState? IndexState { get; set; }

   /// <summary>What a crash can lose with this engine's settings, or "not stated".</summary>
   public string? Durability { get; set; }

   /// <summary>Why the target failed, if it did.</summary>
   public string? Error { get; set; }

   /// <summary>Notes for this target.</summary>
   public List<string> Notes { get; set; } = new();

   #endregion Public Methods
}

/// <summary>
/// The index proof of one target: read from the engine once the load (and its index step) is
/// done, and again after the last timed pass. Why twice: an engine that kept building while it
/// was searched gave numbers from more than one index, and the two readings show it.
/// </summary>
public sealed class TargetIndexState
{
   #region Public Methods

   /// <summary>State read after the load and its index step (for bench, read before searching).</summary>
   public IndexState? AfterLoad { get; set; }

   /// <summary>State read after the last timed pass; null when the target was not searched.</summary>
   public IndexState? AfterSearch { get; set; }

   #endregion Public Methods
}

/// <summary>
/// How loading one target went.
/// </summary>
public sealed class LoadReport
{
   #region Public Methods

   /// <summary>Rows written.</summary>
   public int Rows { get; set; }

   /// <summary>Rows per upsert call.</summary>
   public int Batch { get; set; }

   /// <summary>Seconds spent inside upsert calls (one writer, batches in order).</summary>
   public double UpsertSeconds { get; set; }

   /// <summary>Rows per second of upsert time.</summary>
   public double RowsPerSecond { get; set; }

   /// <summary>Seconds after the last upsert until the target counted every row (asynchronous engines).</summary>
   public double? CountMatchSeconds { get; set; }

   /// <summary>Seconds to build or wait for the index after loading, where that is a separate step.</summary>
   public double? IndexSeconds { get; set; }

   /// <summary>What the index step reported.</summary>
   public string? IndexNote { get; set; }

   #endregion Public Methods
}

/// <summary>
/// How searching one target went.
/// </summary>
public sealed class SearchReport
{
   #region Public Methods

   /// <summary>Rows the target held when searched.</summary>
   public long? CountInTarget { get; set; }

   /// <summary>Latency samples taken at concurrency 1.</summary>
   public int LatencySamples { get; set; }

   /// <summary>Median latency, ms (client side, one query at a time).</summary>
   public double? P50Ms { get; set; }

   /// <summary>95th percentile latency, ms.</summary>
   public double? P95Ms { get; set; }

   /// <summary>99th percentile latency, ms.</summary>
   public double? P99Ms { get; set; }

   /// <summary>Queries per second at each concurrency level.</summary>
   public Dictionary<int, double> Qps { get; set; } = new();

   /// <summary>Mean recall@k against the exact answer (ties counted).</summary>
   public double? Recall { get; set; }

   /// <summary>Mean file-level nDCG@k (golden queries only).</summary>
   public double? Ndcg { get; set; }

   /// <summary>Searches that failed or timed out.</summary>
   public int Errors { get; set; }

   /// <summary>The first failure message.</summary>
   public string? FirstError { get; set; }

   /// <summary>Exact-mode searches timed (engines with an exact mode); every one is a single search, and the JSON name keeps "queries" so older readers still load it.</summary>
   public int ExactQueries { get; set; }

   /// <summary>Exact-mode median latency, ms.</summary>
   public double? ExactP50Ms { get; set; }

   /// <summary>Exact-mode 95th percentile latency, ms.</summary>
   public double? ExactP95Ms { get; set; }

   /// <summary>Exact-mode recall (should be 1.0; anything else means the engine or the yardstick is wrong).</summary>
   public double? ExactRecall { get; set; }

   /// <summary>
   /// CPU time the benchmark client itself used per search, in milliseconds, by concurrency level
   /// (the pass with that many searchers); null when the run did not measure it, which leaves it
   /// out of results.json. Why: for the fastest engines it is about as large as the latency, so the
   /// speed order partly reflects each engine's .NET client library.
   /// </summary>
   public Dictionary<int, double>? ClientCpuMsPerSearch { get; set; }

   #endregion Public Methods
}
