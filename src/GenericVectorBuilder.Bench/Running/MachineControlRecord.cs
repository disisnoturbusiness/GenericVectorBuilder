using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GenericVectorBuilder.Bench.Cli;

namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// The conditions a run was measured under, written into results.json as "conditions": build,
/// runtime, governor, CPU split and how each engine was pinned, the CPU idle settings, how each
/// target was reached, every search setting, and per timed pass the clock of every CPU and how
/// busy the box was.
/// Why in the results: two runs can only be averaged or compared when these match, and the
/// consolidation reads them from here (the names match what it accepts: buildConfiguration,
/// governor, governorAtEnd, clientCpus, engineCpus, threadSiblings, warmupSearches, exactSeconds).
/// </summary>
public sealed class MachineConditions
{
   #region Public Methods

   /// <summary>"Release" or "Debug", from the client assembly's AssemblyConfiguration attribute.</summary>
   public string BuildConfiguration { get; set; } = "unknown";

   /// <summary>True when the JIT optimiser was off for the client (a Debug build).</summary>
   public bool JitOptimizerDisabled { get; set; }

   /// <summary>.NET runtime of the client, e.g. ".NET 10.0.0".</summary>
   public string DotNet { get; set; } = RuntimeInformation.FrameworkDescription;

   /// <summary>Runtime version number.</summary>
   public string RuntimeVersion { get; set; } = Environment.Version.ToString();

   /// <summary>True for server garbage collection.</summary>
   public bool ServerGc { get; set; } = GCSettings.IsServerGC;

   /// <summary>"on", or "off" with the reason.</summary>
   public string MachineControl { get; set; } = "off";

   /// <summary>Governor every CPU ran under during the run ("performance" when machine control is on), or the mixed list.</summary>
   public string Governor { get; set; } = "unknown";

   /// <summary>Governor before the run changed it.</summary>
   public string? GovernorBefore { get; set; }

   /// <summary>Governor read at the end of the run, before it was put back.</summary>
   public string? GovernorAtEnd { get; set; }

   /// <summary>Governor read after it was put back.</summary>
   public string? GovernorAfterRestore { get; set; }

   /// <summary>Logical CPUs of the box.</summary>
   public int LogicalCpus { get; set; } = Environment.ProcessorCount;

   /// <summary>Hyperthread siblings per physical core, e.g. "0,4".</summary>
   public List<string> ThreadSiblings { get; set; } = new();

   /// <summary>CPUs the client (and any embedded engine) was held to, or null when not split.</summary>
   public string? ClientCpus { get; set; }

   /// <summary>CPUs the engine under test was held to, or null when not split.</summary>
   public string? EngineCpus { get; set; }

   /// <summary>Why the CPUs were not split, or null.</summary>
   public string? PartitionProblem { get; set; }

   /// <summary>What was read back after pinning the client.</summary>
   public string? ClientPinning { get; set; }

   /// <summary>Untimed warm-up searches before every timed pass.</summary>
   public int WarmupSearches { get; set; }

   /// <summary>Seconds each exact-mode pass was allowed.</summary>
   public int ExactSeconds { get; set; }

   /// <summary>Every search setting of the run.</summary>
   public SearchSettings Search { get; set; } = new();

   /// <summary>The busy-box rule as applied.</summary>
   public string? BusyRule { get; set; }

   /// <summary>How each target's engine was pinned.</summary>
   public List<TargetPinning> Engines { get; set; } = new();

   /// <summary>Every timed pass with its clock and load.</summary>
   public List<PassConditions> Passes { get; set; } = new();

   /// <summary>CPU idle (C-state) settings when the run started; recorded only, never changed.</summary>
   public CpuIdleSettings? CpuIdle { get; set; }

   /// <summary>CPU idle settings read at the end of the run, to show they did not change under it.</summary>
   public CpuIdleSettings? CpuIdleAtEnd { get; set; }

   /// <summary>How each target was reached: route, configured addresses and ports, connections seen.</summary>
   public List<TargetConnection> Connections { get; set; } = new();

   /// <summary>The first sampling failure, or null.</summary>
   public string? SamplingError { get; set; }

   /// <summary>Which /proc/stat columns the outside load counted as busy and why (see <see cref="CpuAccounting"/>), or null when machine control was off.</summary>
   public string? CpuAccounting { get; set; }

   /// <summary>Load average (1, 5, 15 min) at the end of the run.</summary>
   public string? LoadAverageEnd { get; set; }

   /// <summary>The state file that records changes until they are put back.</summary>
   public string? StateFile { get; set; }

   /// <summary>What was put back at the end.</summary>
   public List<string> Restored { get; set; } = new();

   /// <summary>What could not be put back.</summary>
   public List<string> RestoreProblems { get; set; } = new();

   /// <summary>Warnings and facts a reader must see next to the numbers.</summary>
   public List<string> Flags { get; set; } = new();

   /// <summary>
   /// The CPU clock as fields a program can compare (conditions.clock): pinned or not, the turbo
   /// switch and the uncore limit before, during and at the end, the thermal throttle counters at
   /// the start and the end, the tolerance and the rule each pass was judged by. Why: the notes
   /// say the same in words, but a consolidation that must refuse to pool runs at different
   /// clocks needs values, not sentences to parse.
   /// </summary>
   public ClockRecord? Clock { get; set; }

   /// <summary>
   /// Thermal throttle counter rises per target, from the target's start to its end and from its
   /// first warm-up line to its end (conditions.throttleByTarget). Read only at those moments,
   /// never in the sampler and never inside a timed pass. Why per target: a rise during one
   /// target's turn says which figures may have run under a thermal clock drop.
   /// </summary>
   public Dictionary<string, ThrottleRise> ThrottleByTarget { get; set; } = new( StringComparer.Ordinal );

   /// <summary>
   /// How the engine CPU per search of each pass was measured, with the cgroups it found
   /// (conditions.engineCpu). Why recorded: which cgroups count as the engine and which as
   /// outside work decides both figures, and the rule text is generated from what the code did.
   /// </summary>
   public EngineCpuRecord? EngineCpu { get; set; }

   /// <summary>
   /// Build facts of the client and the run's search settings.
   /// </summary>
   /// <param name="options">The command line.</param>
   /// <returns>Conditions with the build and settings filled in.</returns>
   public static MachineConditions ForRun( BenchOptions options )
   {
      Assembly client = typeof( MachineConditions ).Assembly;
      return new MachineConditions
      {
         BuildConfiguration = client.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown",
         JitOptimizerDisabled = client.GetCustomAttribute<System.Diagnostics.DebuggableAttribute>()?.IsJITOptimizerDisabled ?? false,
         WarmupSearches = options.Warmup,
         ExactSeconds = options.ExactSeconds,
         Search = SearchSettings.From( options ),
      };
   }

   #endregion Public Methods
}

/// <summary>
/// Every option that changes how the targets are searched.
/// </summary>
public sealed class SearchSettings
{
   #region Public Methods

   /// <summary>Hits per query.</summary>
   public int Top { get; set; }

   /// <summary>"golden" or "random:N".</summary>
   public string Queries { get; set; } = string.Empty;

   /// <summary>Concurrency levels.</summary>
   public List<int> Concurrency { get; set; } = new();

   /// <summary>Seconds per throughput level.</summary>
   public int SecondsPerLevel { get; set; }

   /// <summary>Warm-up searches before every pass.</summary>
   public int WarmupSearches { get; set; }

   /// <summary>Time budget of each exact pass.</summary>
   public int ExactSeconds { get; set; }

   /// <summary>Longest one search may take before it counts as an error.</summary>
   public int SearchTimeoutSeconds { get; set; }

   /// <summary>Search beam for qdrant-hnsw, or null for the server default. Other engines' search effort is in each target's index description.</summary>
   public int? HnswEf { get; set; }

   /// <summary>Rows per upsert call.</summary>
   public int Batch { get; set; }

   /// <summary>Row limit, or null for all rows.</summary>
   public int? Limit { get; set; }

   /// <summary>--seed when given, else null (the seed used is runSeed).</summary>
   public int? Seed { get; set; }

   /// <summary>
   /// Reads the settings from the command line.
   /// </summary>
   /// <param name="options">The command line.</param>
   /// <returns>The settings.</returns>
   public static SearchSettings From( BenchOptions options )
   {
      return new SearchSettings
      {
         Top = options.Top,
         Queries = options.Queries,
         Concurrency = options.Concurrency.ToList(),
         SecondsPerLevel = options.Seconds,
         WarmupSearches = options.Warmup,
         ExactSeconds = options.ExactSeconds,
         SearchTimeoutSeconds = options.SearchTimeoutSeconds,
         HnswEf = options.HnswEf,
         Batch = options.Batch,
         Limit = options.Limit,
         Seed = options.Seed,
      };
   }

   #endregion Public Methods
}

/// <summary>
/// One timed pass: when it ran, every CPU's clock during it, and how busy the box was.
/// </summary>
public sealed class PassConditions
{
   #region Public Methods

   /// <summary>Target name.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>Pass name, e.g. "default@8".</summary>
   public string Pass { get; set; } = string.Empty;

   /// <summary>Start (UTC, after any wait for a quiet box).</summary>
   public string StartUtc { get; set; } = string.Empty;

   /// <summary>End (UTC).</summary>
   public string EndUtc { get; set; } = string.Empty;

   /// <summary>Seconds the pass took.</summary>
   public double Seconds { get; set; }

   /// <summary>"result line" when the pass's last result line marked its end; otherwise what did.</summary>
   public string EndedBy { get; set; } = string.Empty;

   /// <summary>Governor read at the end of the pass.</summary>
   public string? Governor { get; set; }

   /// <summary>Clock samples inside the pass (0 when the nearest sample stood in).</summary>
   public int FrequencySamples { get; set; }

   /// <summary>True when the pass was shorter than one sample and the nearest sample stood in.</summary>
   public bool NearestSample { get; set; }

   /// <summary>Median of the engine CPUs' median MHz.</summary>
   public int? EngineMhzMedian { get; set; }

   /// <summary>Median of the client CPUs' median MHz.</summary>
   public int? ClientMhzMedian { get; set; }

   /// <summary>Lowest, median and highest MHz of every CPU.</summary>
   public List<CpuMhz> CpuMhz { get; set; } = new();

   /// <summary>
   /// Average MHz of the engine CPUs over the pass (every reading of every engine CPU), or null
   /// when the CPUs were not split or no clock was read. Kept out of results.json on purpose:
   /// it is reported in the target's clock note (see <see cref="MachineFlags.ClockLine"/>).
   /// </summary>
   [JsonIgnore]
   public double? EngineMhzMean { get; set; }

   /// <summary>
   /// Average MHz of the client CPUs over the pass, or of every CPU when the CPUs were not split
   /// (the client then shares all of them); null when no clock was read. Kept out of results.json
   /// like <see cref="EngineMhzMean"/>.
   /// </summary>
   [JsonIgnore]
   public double? ClientMhzMean { get; set; }

   /// <summary>
   /// The clock every CPU was pinned to for the run (MHz, turbo off), or null when the clock was
   /// not pinned. Kept out of results.json; the run's summary note states it.
   /// </summary>
   [JsonIgnore]
   public int? PinnedMhz { get; set; }

   /// <summary>What the quiet check came right before: "warm-up" (the rule), or the timed pass when no warm-up line was seen.</summary>
   public string? QuietCheckBefore { get; set; }

   /// <summary>When the quiet check let the pass go on (UTC).</summary>
   public string? QuietCheckUtc { get; set; }

   /// <summary>
   /// Seconds from the quiet check (made at the pass's latest warm-up line) to the start of the
   /// timed window: the warm-up plus, in a run with a rehearsal, the settle trial that follows
   /// every pass's warm-up (about 18 s with the 15 s warm-up and 3 s trial). After a settle
   /// extension the warm-up is announced, checked and run again, so the lead is that second
   /// warm-up alone (about 15 s).
   /// </summary>
   public double? QuietCheckLeadSeconds { get; set; }

   /// <summary>Start of the window the quiet check averaged over (UTC): a full window back, or the target's or its engine's start if later.</summary>
   public string? OutsideWindowFromUtc { get; set; }

   /// <summary>CPUs busy outside the benchmark (mean over the busy window) at the quiet check before the pass's warm-up. Signed: below zero means the CPU counters did not add up (see <see cref="AccountingMismatch"/>).</summary>
   public double? OutsideLoadAtStart { get; set; }

   /// <summary>CPUs busy outside the benchmark over the last few seconds at that check. Signed like <see cref="OutsideLoadAtStart"/>.</summary>
   public double? OutsideLoadRecentAtStart { get; set; }

   /// <summary>CPUs busy outside the benchmark during the pass (mean). Signed like <see cref="OutsideLoadAtStart"/>.</summary>
   public double? OutsideLoadDuring { get; set; }

   /// <summary>
   /// True when any of the three outside loads is below <see cref="MachineFlags.MISMATCH_LIMIT"/>
   /// (-0.05 CPUs): the kernel's total busy time minus this process minus the engine's cgroups
   /// came out negative, so the counters do not add up and the pass's outside figures cannot be
   /// trusted. Computed from the recorded loads, so a pass written by older code is judged the same way.
   /// </summary>
   public bool AccountingMismatch => MismatchReason() != null;

   /// <summary>Which outside load is below the limit and by how much, or null.</summary>
   public string? AccountingMismatchReason => MismatchReason();

   /// <summary>Completed timed searches in the pass's window, read from the target's pass note, or null when no note was found.</summary>
   public int? Searches { get; set; }

   /// <summary>
   /// CPU seconds this (client) process used during the pass's timed window: user + system of its
   /// threads, not its finished children. Includes the sampler thread and the kernel network time
   /// spent on the process's behalf, as every search's cost does.
   /// </summary>
   public double? ClientCpuSeconds { get; set; }

   /// <summary>
   /// The client's CPU per completed search in the pass, in milliseconds: <see cref="ClientCpuSeconds"/>
   /// over <see cref="Searches"/>. It is CPU time summed over every thread, so it can be larger
   /// than the time per search; it is a cost, not a part of the latency.
   /// </summary>
   public double? ClientCpuMsPerSearch { get; set; }

   /// <summary>What the client CPU figure was measured over, or null when it was not.</summary>
   public string? ClientCpuBasis { get; set; }

   /// <summary>Why there is no client CPU per search for this pass, or null when there is.</summary>
   public string? ClientCpuProblem { get; set; }

   /// <summary>
   /// True when every CPU group the run's split defines had a clock reading in the pass (the
   /// engine CPUs and the client CPUs, or every CPU as one group when the CPUs were not split).
   /// A pass with false is flagged "clock not read" and does not count as at the pinned clock.
   /// Why stored: the pinned clock the pass is judged against is not in results.json on its own.
   /// </summary>
   public bool ClockRead { get; set; }

   /// <summary>
   /// True when a group's median clock was more than <see cref="ClockRule.TOLERANCE_BP"/> basis
   /// points off the pinned clock, false when every group was within it, null when the clock was
   /// not read or not pinned. Why stored: <see cref="PinnedMhz"/> is kept out of results.json, so
   /// a reader could not judge the pass again from the file.
   /// </summary>
   public bool? ClockOff { get; set; }

   /// <summary>
   /// CPU time of the engine under test's cgroups over the pass's timed window per completed
   /// search, in milliseconds (dockerd's cgroup left out), or null with
   /// <see cref="EngineCpuNullReason"/>. Why: with the client's CPU per search it shows what
   /// each search cost on each side, without claiming either is a part of the latency.
   /// </summary>
   public double? EngineCpuMsPerSearch { get; set; }

   /// <summary>Why there is no engine CPU per search for this pass (embedded engine, no cgroup followed, a cgroup missing at a window edge), or null when there is.</summary>
   public string? EngineCpuNullReason { get; set; }

   /// <summary>
   /// CPU time of dockerd's cgroup over the pass's timed window per completed search, in
   /// milliseconds, or null when dockerd's cgroup was not followed during the pass. It counts
   /// everything in that cgroup for every container on the box, not this target alone.
   /// </summary>
   public double? DockerdCpuMsPerSearch { get; set; }

   /// <summary>
   /// CPUs the engine under test kept busy on average over the pass's timed window: its cgroups'
   /// CPU seconds divided by the window's length (the pass note's end minus its start), or null
   /// with <see cref="EngineCpuNullReason"/>.
   /// </summary>
   public double? EngineCpusBusy { get; set; }

   /// <summary>Seconds the pass waited for a quiet box.</summary>
   public double WaitedSeconds { get; set; }

   /// <summary>True when the box was busy (did not clear in time, or busy during the pass).</summary>
   public bool BusyBox { get; set; }

   /// <summary>Why it is flagged busy.</summary>
   public string? BusyReason { get; set; }

   /// <summary>
   /// Records the client CPU of the pass.
   /// </summary>
   /// <param name="seconds">Client CPU seconds in the pass's window, or null when it could not be measured.</param>
   /// <param name="searches">Completed searches in the window, or null when the pass's note was not found.</param>
   /// <param name="basis">What it was measured over (set when measured).</param>
   /// <param name="problem">Why there is no per-search figure, or null.</param>
   public void ApplyClientCpu( double? seconds, int? searches, string? basis, string? problem )
   {
      Searches = searches;
      ClientCpuSeconds = seconds.HasValue ? Math.Round( seconds.Value, 4 ) : null;
      ClientCpuMsPerSearch = seconds.HasValue && searches > 0 ? Math.Round( seconds.Value * 1000 / searches.Value, 4 ) : null;
      ClientCpuBasis = basis;
      ClientCpuProblem = ClientCpuMsPerSearch == null ? problem ?? "client CPU per search could not be worked out" : null;
   }

   /// <summary>
   /// Records the engine CPU of the pass. Why one method: the null and the reason are set
   /// together, so a pass never carries a figure and a reason, or neither.
   /// </summary>
   /// <param name="engineSeconds">CPU seconds of the engine's cgroups over the window, or null when not measured.</param>
   /// <param name="dockerdSeconds">CPU seconds of dockerd's cgroup over the window, or null when it was not followed.</param>
   /// <param name="searches">Completed searches in the window, or null when the pass's note was not found.</param>
   /// <param name="windowSeconds">Length of the window (the pass note's end minus its start).</param>
   /// <param name="problem">Why there is no figure, or null.</param>
   public void ApplyEngineCpu( double? engineSeconds, double? dockerdSeconds, int? searches, double windowSeconds, string? problem )
   {
      EngineCpuMsPerSearch = engineSeconds.HasValue && searches > 0 ? Math.Round( engineSeconds.Value * 1000 / searches.Value, 4 ) : null;
      DockerdCpuMsPerSearch = engineSeconds.HasValue && dockerdSeconds.HasValue && searches > 0 ? Math.Round( dockerdSeconds.Value * 1000 / searches.Value, 4 ) : null;
      EngineCpusBusy = EngineCpuMsPerSearch.HasValue && windowSeconds > 0 ? Math.Round( engineSeconds!.Value / windowSeconds, 4 ) : null;
      EngineCpuNullReason = EngineCpuMsPerSearch == null ? problem ?? "engine CPU per search could not be worked out" : null;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The reason for an accounting mismatch: the lowest outside load, if below the limit.
   /// </summary>
   /// <returns>The text, or null when none is below the limit.</returns>
   private string? MismatchReason()
   {
      ( string Name, double? Value )[] loads = { ( "before the warm-up", OutsideLoadAtStart ), ( "over the last few seconds before the warm-up", OutsideLoadRecentAtStart ), ( "during the pass", OutsideLoadDuring ) };
      ( string Name, double? Value ) worst = loads.Where( l => l.Value < MachineFlags.MISMATCH_LIMIT ).OrderBy( l => l.Value ).FirstOrDefault();
      return worst.Value.HasValue ? string.Create( CultureInfo.InvariantCulture, $"outside load {worst.Name} was {worst.Value:0.000} CPUs, below {MachineFlags.MISMATCH_LIMIT:0.00}" ) : null;
   }

   #endregion Private Methods
}

/// <summary>
/// Turns the conditions into the warnings printed with the results, and writes them into
/// results.json. Why the flags are computed in one place: the same facts feed the run-level
/// notes and the "flags" list in the JSON, and they must not disagree.
/// </summary>
public static class MachineFlags
{
   #region Data Members

   /// <summary>Outside load (CPUs) below which the counters are said not to add up: a little under zero is rounding, this much is not.</summary>
   public const double MISMATCH_LIMIT = -0.05;

   private const string DEFAULT_PASS = "default@";
   private static readonly string[] ENGINE_CPU_KINDS = { "compose", "sql", "qdrant" };

   private static readonly JsonSerializerOptions JSON = new()
   {
      WriteIndented = true,
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
   };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The flags for a run.
   /// </summary>
   /// <param name="c">The run's conditions.</param>
   /// <param name="searchedTargets">Targets that ran timed passes (to catch a target whose passes were never seen).</param>
   /// <returns>Flags; warnings start with "WARNING:".</returns>
   public static List<string> Compute( MachineConditions c, IEnumerable<string> searchedTargets )
   {
      var flags = new List<string>();
      bool on = c.MachineControl == "on";
      if( !on )
      {
         flags.Add( $"WARNING: machine control {c.MachineControl}: the governor was {c.Governor}, the CPU clock was not pinned (turbo left as found), nothing was pinned, client and engine shared every CPU, and no pass waited for a quiet box." );
      }
      else if( c.Governor != GovernorControl.TARGET || c.GovernorAtEnd is string end && end != GovernorControl.TARGET )
      {
         flags.Add( $"WARNING: the CPU governor was {c.Governor} during the run and {c.GovernorAtEnd ?? "unknown"} at its end, not {GovernorControl.TARGET} throughout." );
      }

      flags.AddRange( c.Passes.Where( p => on && p.Governor != null && p.Governor != GovernorControl.TARGET ).Select( p => $"WARNING: governor {p.Governor} during {p.Target} {p.Pass}." ) );
      if( c.BuildConfiguration != "Release" )
      {
         flags.Add( $"WARNING: {c.BuildConfiguration} build of the client{( c.JitOptimizerDisabled ? " with the JIT optimiser off" : string.Empty )}: client-side latency and throughput include slower client code than a Release build." );
      }

      if( on && c.PartitionProblem != null )
      {
         flags.Add( $"WARNING: CPUs not split between engine and client: {c.PartitionProblem}." );
      }

      flags.AddRange( c.Engines.Where( e => e.Problems.Count > 0 ).Select( e => $"WARNING: {e.Target} not fully pinned to its CPUs: {string.Join( "; ", e.Problems ).TrimEnd( '.' )}." ) );
      flags.AddRange( c.Engines.Where( e => e.Hosting == "embedded" ).Select( e => $"{e.Target} is embedded: it ran inside the client process on the client CPUs {e.Cpus ?? "(all)"}, sharing them with the client." ) );
      flags.AddRange( c.Passes.Where( p => p.BusyBox ).Select( p => $"WARNING: busy box during {p.Target} {p.Pass}: {p.BusyReason?.TrimEnd( '.' )}." ) );
      flags.AddRange( ClockWarnings( c.Passes, IsSplit( c ) ) );
      flags.AddRange( c.Passes.Where( p => p.AccountingMismatch ).Select( p => $"WARNING: accounting mismatch in {p.Target} {p.Pass}: {p.AccountingMismatchReason}; the kernel's busy time minus this process minus the engine's cgroups came out negative, so the outside-load figures of this pass cannot be trusted." ) );
      flags.AddRange( on ? c.Passes.Where( p => p.ClientCpuMsPerSearch == null ).Select( p => $"WARNING: no client CPU per search for {p.Target} {p.Pass}: {p.ClientCpuProblem ?? "it was never recorded"}." ) : Array.Empty<string>() );
      flags.AddRange( on ? EngineCpuFlags( c ) : Array.Empty<string>() );
      flags.AddRange( ThrottleFlags( c ) );
      flags.AddRange( IdleFlags( c ) );
      flags.AddRange( c.Connections.SelectMany( t => t.Observed.Where( p => p.Kind.StartsWith( "docker-proxy", StringComparison.Ordinal ) )
         .Select( p => $"WARNING: {t.Target} had {p.Sockets} connection(s) open to its published port {p.Address}:{p.Port}, which goes through docker-proxy, not the container address it was meant to use." ) ) );
      flags.AddRange( RecordFlags( c, on ? searchedTargets.Where( t => c.Passes.All( p => p.Target != t ) ).ToList() : new List<string>() ) );
      return flags;
   }

   /// <summary>
   /// The flags about the record itself: targets whose passes were never seen, passes shorter
   /// than one clock sample, a sampling failure, and what could not be put back.
   /// </summary>
   /// <param name="c">The run's conditions.</param>
   /// <param name="unseen">Searched targets with no recorded pass.</param>
   /// <returns>Flags.</returns>
   public static IEnumerable<string> RecordFlags( MachineConditions c, List<string> unseen )
   {
      if( unseen.Count > 0 )
      {
         yield return $"WARNING: no timed pass of {string.Join( ", ", unseen )} was seen in the progress output, so those passes have no clock record and did not wait for a quiet box.";
      }

      List<string> tooShort = c.Passes.Where( p => p.NearestSample ).Select( p => $"{p.Target} {p.Pass}" ).ToList();
      if( tooShort.Count > 0 )
      {
         yield return $"Passes shorter than one clock sample (250 ms) show the sample nearest their middle: {string.Join( ", ", tooShort )}.";
      }

      if( c.SamplingError != null )
      {
         yield return $"WARNING: CPU sampling failed at least once ({c.SamplingError}); clock and outside-load figures may have gaps.";
      }

      if( c.RestoreProblems.Count > 0 )
      {
         yield return $"WARNING: the machine was NOT fully put back: {string.Join( "; ", c.RestoreProblems )}. The state file {c.StateFile} keeps what is left; fix the cause, then run 'GenericVectorBuilder.Bench restore-machine'.";
      }
   }

   /// <summary>
   /// Flags about the CPU idle settings: unreadable, or changed between the start and the end of
   /// the run (someone changed them under it; this tool never does).
   /// </summary>
   /// <param name="c">The run's conditions.</param>
   /// <returns>Flags.</returns>
   public static IEnumerable<string> IdleFlags( MachineConditions c )
   {
      if( c.CpuIdle is { States.Count: 0 } idle )
      {
         yield return $"CPU idle states could not be read ({idle.Problem}); latency at one searcher depends on them.";
      }

      if( c.CpuIdle != null && c.CpuIdleAtEnd != null && c.CpuIdle.Key() != c.CpuIdleAtEnd.Key() )
      {
         yield return $"WARNING: the CPU idle settings changed during the run (start: {c.CpuIdle.Describe()}; end: {c.CpuIdleAtEnd.Describe()}). This tool only reads them, so something else changed them; passes on either side are not comparable.";
      }
   }

   /// <summary>
   /// Flags about the thermal throttle counters: unreadable, or risen over the run or during a
   /// target's turn. Why only rise against no rise: the counters count events, not their length,
   /// so the size of a rise says nothing about how much a figure moved.
   /// </summary>
   /// <param name="c">The run's conditions.</param>
   /// <returns>Flags; a rise is a warning.</returns>
   public static IEnumerable<string> ThrottleFlags( MachineConditions c )
   {
      ThrottleSnapshots? snapshots = c.Clock?.Throttle;
      foreach( ( string when, ThrottleCounts? counts ) in new[] { ( "start", snapshots?.AtStart ), ( "end", snapshots?.AtEnd ) } )
      {
         if( counts?.Unreadable is string why )
         {
            yield return $"Thermal throttle counters could not be read at the {when} of the run ({why}), so a thermal throttle event would not be seen.";
         }
      }

      if( snapshots?.AtStart != null && snapshots.AtEnd != null && ThrottleRise.Between( snapshots.AtStart, snapshots.AtEnd ) is { Rose: true } run )
      {
         yield return $"WARNING: the thermal throttle counters rose during the run ({run.Describe()} from its start to its end); only a rise against no rise is meaningful.";
      }

      foreach( KeyValuePair<string, ThrottleRise> target in c.ThrottleByTarget.OrderBy( t => t.Key, StringComparer.Ordinal ) )
      {
         if( target.Value.Rose || target.Value.FromFirstWarmup is { Rose: true } )
         {
            yield return $"WARNING: the thermal throttle counters rose during {target.Key} ({target.Value.Describe()} from the target's start to its end; {target.Value.FromFirstWarmup?.Describe() ?? "not read"} from its first warm-up line to its end).";
         }
         else if( target.Value.Problem != null )
         {
            yield return $"Thermal throttle counters of {target.Key} not compared: {target.Value.Problem}.";
         }
      }
   }

   /// <summary>
   /// Warnings for passes of a target whose engine has cgroups to follow (a compose, SQL Server or
   /// Qdrant engine) that came out with no engine CPU per search. Why: for those a missing figure
   /// is a fault to see, while for an embedded engine it is expected and its reason says why.
   /// </summary>
   /// <param name="c">The run's conditions.</param>
   /// <returns>One warning per such pass.</returns>
   public static IEnumerable<string> EngineCpuFlags( MachineConditions c )
   {
      return c.Passes.Where( p => p.EngineCpuMsPerSearch == null && c.Engines.LastOrDefault( e => e.Target == p.Target ) is { } e && ENGINE_CPU_KINDS.Contains( e.Kind ) )
         .Select( p => $"WARNING: no engine CPU per search for {p.Target} {p.Pass}: {p.EngineCpuNullReason ?? "it was never recorded"}." );
   }

   /// <summary>
   /// True when the run's conditions record a split of the CPUs into engine and client CPUs.
   /// </summary>
   /// <param name="c">The run's conditions.</param>
   /// <returns>True when both CPU lists are recorded.</returns>
   public static bool IsSplit( MachineConditions c )
   {
      return c.EngineCpus != null && c.ClientCpus != null;
   }

   /// <summary>
   /// Writes the conditions into the run's results.json as "conditions", leaving the rest of
   /// the file as the results writer made it. Written to a temporary file and renamed, so a
   /// crash never leaves half a results.json.
   /// </summary>
   /// <param name="folder">The run's folder.</param>
   /// <param name="conditions">The conditions.</param>
   /// <exception cref="InvalidDataException">results.json is not a JSON object.</exception>
   public static void WriteInto( string folder, MachineConditions conditions )
   {
      string path = Path.Combine( folder, "results.json" );
      JsonObject root = JsonNode.Parse( File.ReadAllText( path ) ) as JsonObject ?? throw new InvalidDataException( $"{path} is not a JSON object." );
      root["conditions"] = JsonSerializer.SerializeToNode( conditions, JSON );
      string temporary = path + ".tmp";
      File.WriteAllText( temporary, root.ToJsonString( JSON ) );
      File.Move( temporary, path, true );
   }

   /// <summary>
   /// One line per pass for a target's notes: "default@1 3491/3492 MHz, average 3491.8/3491.9".
   /// </summary>
   /// <param name="passes">The target's passes.</param>
   /// <returns>The text, or null when there are none.</returns>
   public static string? ClockLine( IEnumerable<PassConditions> passes )
   {
      List<PassConditions> list = passes.ToList();
      List<string> parts = list.Select( p => string.Create( CultureInfo.InvariantCulture,
         $"{p.Pass} {p.EngineMhzMedian?.ToString( CultureInfo.InvariantCulture ) ?? "?"}/{p.ClientMhzMedian?.ToString( CultureInfo.InvariantCulture ) ?? "?"} MHz, average {Mhz( p.EngineMhzMean )}/{Mhz( p.ClientMhzMean )}, {p.Governor ?? "?"}, outside load {Load( p.OutsideLoadAtStart )} before the warm-up{( p.WaitedSeconds > 0 ? $" after waiting {p.WaitedSeconds:0} s" : string.Empty )}, {Load( p.OutsideLoadDuring )} during{( p.BusyBox ? ", BUSY BOX" : string.Empty )}{( p.ClientCpuMsPerSearch is double cpu ? $", client CPU {cpu:0.000} ms per search" : string.Empty )}{( p.EngineCpuMsPerSearch is double engine ? $", engine CPU {engine:0.000} ms per search" : string.Empty )}" ) ).ToList();
      int? pinned = list.Select( p => p.PinnedMhz ).FirstOrDefault( m => m != null );
      string clock = pinned is int mhz
         ? string.Create( CultureInfo.InvariantCulture, $"; the clock was pinned at {mhz} MHz (turbo off), and a pass whose median on either group is more than {ClockRule.TOLERANCE_BP} bp ({ClockRule.TolerancePercent()}) off it, or that has no reading on a group, is flagged" )
         : "; the clock was not pinned";
      return parts.Count == 0 ? null : $"Clock per pass (median MHz of engine CPUs / client CPUs, each the median of its CPUs' medians and every CPU when the CPUs were not split, their average MHz, governor, CPUs busy outside the benchmark, client and engine CPU per search{clock}): " + string.Join( "; ", parts ) + ".";
   }

   /// <summary>
   /// Warnings for passes run under a pinned clock whose median clock on a CPU group (the median of
   /// its CPUs' medians, see <see cref="ClockRule"/>) was more than <see cref="ClockRule.TOLERANCE_BP"/>
   /// basis points off the pinned clock, or that have no reading on a group (so it is not known).
   /// Why both groups: the client's speed is part of every latency, and an embedded engine runs on
   /// the client CPUs. Why medians and not means: a mean over every 250 ms reading moves with
   /// readings of idle CPUs between bursts of work, while the clock under load stays put.
   /// </summary>
   /// <param name="passes">Passes (a target's, or the run's).</param>
   /// <param name="split">True when the CPUs were split into engine and client CPUs; false judges every CPU as one group.</param>
   /// <returns>One warning per pass that is off or unread.</returns>
   public static IEnumerable<string> ClockWarnings( IEnumerable<PassConditions> passes, bool split )
   {
      foreach( PassConditions p in passes.Where( p => p.PinnedMhz is > 0 ) )
      {
         ClockVerdict verdict = ClockRule.Judge( p, split );
         int pinned = verdict.PinnedMhz!.Value;
         if( !verdict.Read )
         {
            string groups = string.Join( " and ", verdict.Groups.Where( g => g.Median == null ).Select( g => g.Group ) );
            yield return $"WARNING: {ClockRule.NOT_READ} during {p.Target} {p.Pass}: no clock was read during it on {groups}, so it is not known whether it ran at the pinned {pinned} MHz.";
            continue;
         }

         List<string> off = verdict.Groups.Where( g => g.Off )
            .Select( g => string.Create( CultureInfo.InvariantCulture, $"the median on {g.Group} was {g.Median} MHz, {( g.Median!.Value - pinned ) / (double)pinned:+0.0%;-0.0%} against the pinned {pinned} MHz" ) ).ToList();
         if( off.Count > 0 )
         {
            yield return string.Create( CultureInfo.InvariantCulture, $"WARNING: clock off its pinned value during {p.Target} {p.Pass}: {string.Join( "; ", off )} (limit {ClockRule.TOLERANCE_BP} bp, {ClockRule.TolerancePercent()}); this pass is not comparable with passes at the pinned clock." );
         }
      }
   }

   /// <summary>
   /// The client's CPU per search by concurrency level, for the search section of a target in
   /// results.json (search.clientCpuMsPerSearch, which the consolidation reads): pass "default@8"
   /// is level 8. The exact pass has no level and a pass without a figure is left out, so a level
   /// is never shown with an empty or zero value.
   /// </summary>
   /// <param name="passes">One target's recorded passes.</param>
   /// <returns>Milliseconds by level; empty when no pass has a figure.</returns>
   public static Dictionary<int, double> ClientCpuByLevel( IEnumerable<PassConditions> passes )
   {
      var byLevel = new Dictionary<int, double>();
      foreach( PassConditions pass in passes )
      {
         if( pass.ClientCpuMsPerSearch is double ms && pass.Pass.StartsWith( DEFAULT_PASS, StringComparison.Ordinal )
            && int.TryParse( pass.Pass[DEFAULT_PASS.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int level ) )
         {
            byLevel[level] = ms;
         }
      }

      return byLevel;
   }

   /// <summary>
   /// Formats an average clock.
   /// </summary>
   /// <param name="mhz">MHz, or null.</param>
   /// <returns>"3491.8" or "?".</returns>
   public static string Mhz( double? mhz )
   {
      return mhz?.ToString( "0.0", CultureInfo.InvariantCulture ) ?? "?";
   }

   /// <summary>
   /// Formats an outside load.
   /// </summary>
   /// <param name="load">CPUs busy, or null.</param>
   /// <returns>"0.12" or "unknown".</returns>
   public static string Load( double? load )
   {
      return load?.ToString( "0.00", CultureInfo.InvariantCulture ) ?? "unknown";
   }

   #endregion Public Methods
}

/// <summary>
/// The rule every timed pass's clock is judged by, in one place, so the flag in the notes, the
/// stored clockRead and clockOff and the rule text in results.json cannot disagree.
/// A pass's clock per CPU group is the median of its CPUs' medians over the pass (the engine CPUs
/// and the client CPUs; every CPU as one group when the CPUs were not split). A group more than
/// <see cref="TOLERANCE_BP"/> basis points off the pinned clock makes the pass "clock off"; a
/// group with no reading makes it "clock not read". Either counts as not at the pinned clock.
/// Why medians: the earlier rule judged the mean of every 250 ms reading, and in the three v7
/// runs the mean of each Oracle default@8 pass came out 9.5% to 10.8% under the pinned clock
/// while every CPU's median in those passes read 3492 MHz (MachineControlV8Tests holds the v7
/// passes); a minority of low readings moves a mean but not a median. Why this does not prove
/// the clock held: see the limited-power part of <see cref="RuleText"/>.
/// </summary>
public static class ClockRule
{
   #region Data Members

   /// <summary>
   /// Most a group's median clock may differ from the pinned clock, in basis points of the pinned
   /// clock (100 = 1%). Why 1%: on linus7795 every CPU's median in the v7 runs read 3492 MHz with
   /// the clock pinned at a nominal 3500 (0.2% under), while the next step up, the 3600 MHz
   /// ceiling with turbo on, is 2.9% above it.
   /// </summary>
   public const int TOLERANCE_BP = 100;

   /// <summary>The flag word for a pass whose groups do not all have a clock reading.</summary>
   public const string NOT_READ = "clock not read";

   private const string ENGINE = "the engine CPUs";
   private const string CLIENT = "the client CPUs";
   private const string EVERY = "every CPU (the CPUs were not split)";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The tolerance as a percentage for text, from the one constant.
   /// </summary>
   /// <returns>"1%".</returns>
   public static string TolerancePercent()
   {
      return ( TOLERANCE_BP / 100.0 ).ToString( "0.##", CultureInfo.InvariantCulture ) + "%";
   }

   /// <summary>
   /// The median of the per-CPU medians of a set of CPUs.
   /// </summary>
   /// <param name="cpus">Per CPU figures of a pass.</param>
   /// <param name="set">The CPUs of the group.</param>
   /// <returns>MHz, or null when none of the group's CPUs was sampled.</returns>
   public static int? GroupMedian( IEnumerable<CpuMhz> cpus, IEnumerable<int> set )
   {
      HashSet<int> wanted = set.ToHashSet();
      List<int> medians = cpus.Where( c => wanted.Contains( c.Cpu ) ).Select( c => c.Median ).OrderBy( m => m ).ToList();
      return medians.Count == 0 ? null : MachineSampler.Median( medians );
   }

   /// <summary>
   /// True when a median is more than the tolerance off the pinned clock, in whole numbers so a
   /// value exactly at the limit is never decided by floating-point rounding.
   /// </summary>
   /// <param name="medianMhz">The group's median MHz.</param>
   /// <param name="pinnedMhz">The pinned MHz.</param>
   /// <returns>True when off.</returns>
   public static bool IsOff( int medianMhz, int pinnedMhz )
   {
      return (long)Math.Abs( medianMhz - pinnedMhz ) * 10_000 > (long)pinnedMhz * TOLERANCE_BP;
   }

   /// <summary>
   /// Judges one pass from its recorded group medians and its pinned clock.
   /// </summary>
   /// <param name="pass">The pass (EngineMhzMedian, ClientMhzMedian and PinnedMhz are read).</param>
   /// <param name="split">True when the CPUs were split; false judges ClientMhzMedian as every CPU.</param>
   /// <returns>The verdict; not evaluated when the pass has no pinned clock.</returns>
   public static ClockVerdict Judge( PassConditions pass, bool split )
   {
      (string Group, int? Median)[] groups = split
         ? new[] { ( ENGINE, pass.EngineMhzMedian ), ( CLIENT, pass.ClientMhzMedian ) }
         : new[] { ( EVERY, pass.ClientMhzMedian ) };
      int? pinned = pass.PinnedMhz is > 0 ? pass.PinnedMhz : null;
      List<GroupClock> judged = groups.Select( g => new GroupClock( g.Group, g.Median, pinned is int p && g.Median is int m && IsOff( m, p ) ) ).ToList();
      bool read = judged.All( g => g.Median != null );
      return new ClockVerdict( pinned != null, read, pinned != null && read ? judged.Any( g => g.Off ) : null, pinned, judged );
   }

   /// <summary>
   /// The rule as recorded in conditions.clock.rule, built from the constants and the run's own
   /// CPU groups, so the text says what the code does on this run.
   /// </summary>
   /// <param name="partition">The CPU split, or null when machine control is off.</param>
   /// <param name="interval">Time between clock samples.</param>
   /// <returns>The text.</returns>
   public static string RuleText( CpuPartition? partition, TimeSpan interval )
   {
      string sampling = string.Create( CultureInfo.InvariantCulture, $"every CPU's clock is read from {GovernorControl.FrequencyPath( 0 ).Replace( "cpu0", "cpuN", StringComparison.Ordinal )} every {interval.TotalMilliseconds:0} ms" );
      string judging = partition == null
         ? "machine control was off, so the clock was not pinned and no pass was judged against it"
         : JudgingText( partition );
      return $"{sampling}; {judging}. {ThrottleText()}";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The judging part of the rule text for a run that pinned the clock.
   /// </summary>
   /// <param name="partition">The CPU split.</param>
   /// <returns>The text.</returns>
   private static string JudgingText( CpuPartition partition )
   {
      List<IReadOnlyList<int>> sets = partition.IsSplit ? new List<IReadOnlyList<int>> { partition.EngineCpus, partition.ClientCpus } : new List<IReadOnlyList<int>> { partition.OnlineCpus };
      string groups = partition.IsSplit
         ? $"the engine CPUs ({CpuList.Format( partition.EngineCpus )}) and the client CPUs ({CpuList.Format( partition.ClientCpus )}) are judged as two groups"
         : $"the CPUs were not split, so every CPU ({CpuList.Format( partition.OnlineCpus )}) is judged as one group (the all-CPU fallback)";
      string hidden = string.Join( " and ", sets.Select( s => s.Count ).Distinct().Select( n => string.Create( CultureInfo.InvariantCulture, $"the median of {n} CPU medians hides {( n - 1 ) / 2} CPU(s) off the clock for the whole pass" ) ) );
      return $"each CPU's median over a pass is taken, then the median of those medians per CPU group: {groups}; "
         + $"a pass is 'clock off' (clockOff) when a group's median differs from the pinned clock by more than {TOLERANCE_BP} bp ({TolerancePercent()}), "
         + $"and '{NOT_READ}' (clockRead false) when a group has no reading; either counts as not at the pinned clock. "
         + $"Limited power: a CPU's median hides off-clock readings while they are fewer than half of its readings, and {hidden}. "
         + "This rule reads the kernel's figures only; the independent check of the clock is APERF/MPERF, read by the run's observer outside this tool";
   }

   /// <summary>
   /// The thermal throttle part of the rule text.
   /// </summary>
   /// <returns>The text.</returns>
   private static string ThrottleText()
   {
      return "Thermal throttle counters (thermal_throttle/core_throttle_count and package_throttle_count of every CPU) are read at the start and the end of the run and at each target's start, first warm-up line and end, "
         + "never by the clock sampler and never inside a timed pass; the core counter is counted once per physical core (thread_siblings_list) and the package counter once per package (physical_package_id), "
         + "a rise on any CPU counts, a counter that went down counts as a rise, and only a rise against no rise means anything. "
         + "They count thermal events only: no power-limit counters are read, so power capping (RAPL) and AVX clock drops are not seen.";
   }

   #endregion Private Methods
}

/// <summary>
/// One CPU group's clock in a pass.
/// </summary>
/// <param name="Group">The group, as text ("the engine CPUs").</param>
/// <param name="Median">Median of its CPUs' medians, or null when none was read.</param>
/// <param name="Off">True when more than the tolerance off the pinned clock.</param>
public sealed record GroupClock( string Group, int? Median, bool Off );

/// <summary>
/// What <see cref="ClockRule.Judge"/> found for one pass.
/// </summary>
/// <param name="Evaluated">True when the pass had a pinned clock to be judged against.</param>
/// <param name="Read">True when every group had a reading.</param>
/// <param name="Off">True when a group was off, false when none was, null when not evaluated or not read.</param>
/// <param name="PinnedMhz">The pinned clock, or null.</param>
/// <param name="Groups">Each group's figure.</param>
public sealed record ClockVerdict( bool Evaluated, bool Read, bool? Off, int? PinnedMhz, IReadOnlyList<GroupClock> Groups );

/// <summary>
/// The CPU clock of a run as structured fields (conditions.clock). A value that was not read is
/// null, and results.json leaves nulls out, so a reader treats an absent field as not recorded.
/// </summary>
public sealed class ClockRecord
{
   #region Public Methods

   /// <summary>True when the run pinned the clock (turbo off, uncore limit held).</summary>
   public bool Pinned { get; set; }

   /// <summary>The clock every CPU was pinned to, in MHz, or null when not pinned.</summary>
   public int? PinnedMhz { get; set; }

   /// <summary>Every CPU's frequency ceiling before the run, in MHz, when all CPUs shared one; null when they differed or were not read.</summary>
   public int? CeilingBeforeMhz { get; set; }

   /// <summary>The turbo switch normalised so 1 means turbo off (intel_pstate's no_turbo as read; cpufreq's boost inverted).</summary>
   public TurboValues NoTurbo { get; set; } = new();

   /// <summary>The uncore ratio limit register (MSR 0x620) as "0x" plus lower-case hex.</summary>
   public UncoreValues UncoreMsr620 { get; set; } = new();

   /// <summary>The thermal throttle counters at the start and the end of the run.</summary>
   public ThrottleSnapshots Throttle { get; set; } = new();

   /// <summary>The clock tolerance in basis points (<see cref="ClockRule.TOLERANCE_BP"/>).</summary>
   public int ToleranceBp { get; set; } = ClockRule.TOLERANCE_BP;

   /// <summary>The rule each pass was judged by, and what the counters can and cannot see (<see cref="ClockRule.RuleText"/>).</summary>
   public string Rule { get; set; } = string.Empty;

   /// <summary>
   /// The record of a run that pinned the clock.
   /// </summary>
   /// <param name="pin">What was pinned (with the settings before).</param>
   /// <param name="atStart">The throttle counters at the start.</param>
   /// <param name="rule">The rule text.</param>
   /// <returns>The record (the values at the end are filled in at the end of the run).</returns>
   public static ClockRecord ForPinned( ClockPin pin, ThrottleCounts atStart, string rule )
   {
      return new ClockRecord
      {
         Pinned = true,
         PinnedMhz = pin.PinnedMhz,
         CeilingBeforeMhz = SharedCeilingMhz( pin.Before ),
         NoTurbo = new TurboValues { Before = NoTurboValue( pin.Before ), During = NoTurboValue( pin.Pinned ) },
         UncoreMsr620 = new UncoreValues { Before = Msr( pin.Uncore?.Before ), During = Msr( pin.Uncore?.Pinned ) },
         Throttle = new ThrottleSnapshots { AtStart = atStart },
         Rule = rule,
      };
   }

   /// <summary>
   /// The record of a run that did not pin the clock (machine control off): only what it was.
   /// </summary>
   /// <param name="found">The clock settings read at the start.</param>
   /// <param name="atStart">The throttle counters at the start.</param>
   /// <param name="rule">The rule text.</param>
   /// <returns>The record.</returns>
   public static ClockRecord ForUnpinned( ClockSettings found, ThrottleCounts atStart, string rule )
   {
      return new ClockRecord
      {
         CeilingBeforeMhz = SharedCeilingMhz( found ),
         NoTurbo = new TurboValues { Before = NoTurboValue( found ) },
         Throttle = new ThrottleSnapshots { AtStart = atStart },
         Rule = rule,
      };
   }

   /// <summary>
   /// The turbo switch normalised so 1 means turbo off.
   /// </summary>
   /// <param name="settings">Clock settings read, or null.</param>
   /// <returns>1 (turbo off), 0 (turbo on), or null when there is no switch or it read something else.</returns>
   public static int? NoTurboValue( ClockSettings? settings )
   {
      if( settings?.Switch is not TurboSwitch turbo || settings.SwitchValue is not ( "0" or "1" ) )
      {
         return null;
      }

      return settings.SwitchValue == turbo.Off ? 1 : 0;
   }

   /// <summary>
   /// An uncore limit as the record writes it.
   /// </summary>
   /// <param name="limit">The limit, or null.</param>
   /// <returns>"0x1e1e", or null.</returns>
   public static string? Msr( UncoreLimit? limit )
   {
      return limit == null ? null : "0x" + limit.Hex;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The one ceiling every CPU shared, in MHz.
   /// </summary>
   /// <param name="settings">Clock settings.</param>
   /// <returns>MHz, or null when they differ, one is unreadable, or none was read.</returns>
   private static int? SharedCeilingMhz( ClockSettings settings )
   {
      List<long?> ceilings = settings.MaxKhz.Values.Distinct().ToList();
      return ceilings.Count == 1 && ceilings[0] is long khz ? ClockControl.Mhz( khz ) : null;
   }

   #endregion Private Methods
}

/// <summary>
/// The turbo switch before, during and at the end of a run, 1 meaning turbo off.
/// </summary>
public sealed class TurboValues
{
   #region Public Methods

   /// <summary>Before the run changed anything.</summary>
   public int? Before { get; set; }

   /// <summary>While the run held it (null when it did not).</summary>
   public int? During { get; set; }

   /// <summary>At the end, before it was put back; written only into the final results.</summary>
   public int? AtEnd { get; set; }

   #endregion Public Methods
}

/// <summary>
/// The uncore limit register before, during and at the end of a run.
/// </summary>
public sealed class UncoreValues
{
   #region Public Methods

   /// <summary>Before the run changed it.</summary>
   public string? Before { get; set; }

   /// <summary>While the run held it (as read back after the write).</summary>
   public string? During { get; set; }

   /// <summary>At the end, before it was put back; written only into the final results.</summary>
   public string? AtEnd { get; set; }

   #endregion Public Methods
}

/// <summary>
/// The thermal throttle counters at the start and the end of a run.
/// </summary>
public sealed class ThrottleSnapshots
{
   #region Public Methods

   /// <summary>When the run started.</summary>
   public ThrottleCounts? AtStart { get; set; }

   /// <summary>At the end of the run; written only into the final results.</summary>
   public ThrottleCounts? AtEnd { get; set; }

   #endregion Public Methods
}

/// <summary>
/// One CPU's thermal throttle counters and where it sits.
/// </summary>
/// <param name="Cpu">Logical CPU.</param>
/// <param name="CoreCount">core_throttle_count, or null when unreadable.</param>
/// <param name="PackageCount">package_throttle_count, or null when unreadable.</param>
/// <param name="Siblings">thread_siblings_list (its physical core), or "cpuN" when unreadable (then it is counted alone).</param>
/// <param name="PackageId">physical_package_id, or "cpuN" when unreadable.</param>
public sealed record CpuThrottle( int Cpu, long? CoreCount, long? PackageCount, string Siblings, string PackageId );

/// <summary>
/// The thermal throttle counters of every CPU at one moment, with the totals counted once per
/// physical core and once per package.
/// </summary>
public sealed class ThrottleCounts
{
   #region Public Methods

   /// <summary>When they were read (UTC).</summary>
   public string ReadUtc { get; set; } = string.Empty;

   /// <summary>Core throttle events: per physical core the highest count of its CPUs, summed; null when a counter was unreadable.</summary>
   public long? CoreEvents { get; set; }

   /// <summary>Package throttle events: per package the highest count of its CPUs, summed; null when a counter was unreadable.</summary>
   public long? PackageEvents { get; set; }

   /// <summary>Every CPU's counters, so a rise on any one CPU is seen.</summary>
   public List<CpuThrottle> PerCpu { get; set; } = new();

   /// <summary>Which counters could not be read, or null when all were (the totals are then null).</summary>
   public string? Unreadable { get; set; }

   /// <summary>A note about the reading (a CPU whose topology could not be read is counted alone), or null.</summary>
   public string? Note { get; set; }

   #endregion Public Methods
}

/// <summary>
/// How much the thermal throttle counters rose between two readings. Why per CPU and then once
/// per core and package: siblings and the CPUs of a package each report the shared counter, so a
/// plain sum counts one event several times, while taking one CPU per core could miss a rise
/// another sibling shows. A counter that went down (a reset) counts as a rise.
/// </summary>
public sealed class ThrottleRise
{
   #region Public Methods

   /// <summary>Core events added: per physical core the largest rise of its CPUs, summed; null when not compared.</summary>
   public long? CoreRise { get; set; }

   /// <summary>Package events added: per package the largest rise of its CPUs, summed; null when not compared.</summary>
   public long? PackageRise { get; set; }

   /// <summary>Why the readings were not compared (a counter unreadable on either side), or null.</summary>
   public string? Problem { get; set; }

   /// <summary>A counter that went down, or null.</summary>
   public string? Note { get; set; }

   /// <summary>For a target: the rise from its first warm-up line to its end (its timed passes and teardown), or null when no warm-up line was seen.</summary>
   public ThrottleRise? FromFirstWarmup { get; set; }

   /// <summary>True when either counter rose.</summary>
   [System.Text.Json.Serialization.JsonIgnore]
   public bool Rose => CoreRise > 0 || PackageRise > 0;

   /// <summary>
   /// The rise between two readings.
   /// </summary>
   /// <param name="earlier">The first reading.</param>
   /// <param name="later">The second reading.</param>
   /// <returns>The rise, or a problem when either reading lacks a counter.</returns>
   public static ThrottleRise Between( ThrottleCounts? earlier, ThrottleCounts? later )
   {
      if( earlier == null || later == null || earlier.Unreadable != null || later.Unreadable != null )
      {
         return new ThrottleRise { Problem = $"the counters were not read on both sides (first reading: {Side( earlier )}; second reading: {Side( later )})" };
      }

      var notes = new List<string>();
      var core = new Dictionary<string, long>( StringComparer.Ordinal );
      var package = new Dictionary<string, long>( StringComparer.Ordinal );
      foreach( CpuThrottle now in later.PerCpu )
      {
         CpuThrottle? before = earlier.PerCpu.FirstOrDefault( c => c.Cpu == now.Cpu );
         Keep( core, now.Siblings, Delta( "core", now.Cpu, before?.CoreCount, now.CoreCount, notes ) );
         Keep( package, now.PackageId, Delta( "package", now.Cpu, before?.PackageCount, now.PackageCount, notes ) );
      }

      return new ThrottleRise { CoreRise = core.Values.Sum(), PackageRise = package.Values.Sum(), Note = notes.Count > 0 ? string.Join( "; ", notes ) : null };
   }

   /// <summary>
   /// A target's record: its start to its end, and its first warm-up line to its end.
   /// </summary>
   /// <param name="start">At the target's start.</param>
   /// <param name="firstWarmup">At its first warm-up line, or null.</param>
   /// <param name="end">At its end.</param>
   /// <returns>The record.</returns>
   public static ThrottleRise ForTarget( ThrottleCounts? start, ThrottleCounts? firstWarmup, ThrottleCounts end )
   {
      ThrottleRise whole = Between( start, end );
      whole.FromFirstWarmup = firstWarmup == null ? null : Between( firstWarmup, end );
      return whole;
   }

   /// <summary>
   /// The rise as text.
   /// </summary>
   /// <returns>"core events +1, package events +0", or the problem.</returns>
   public string Describe()
   {
      return Problem ?? string.Create( CultureInfo.InvariantCulture, $"core events +{CoreRise}, package events +{PackageRise}{( Note != null ? " (" + Note + ")" : string.Empty )}" );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// One CPU's rise of one counter; a CPU missing from the first reading, or a counter that went
   /// down, counts its whole later value as a rise (at least 1 for a counter that went down).
   /// </summary>
   /// <param name="name">"core" or "package".</param>
   /// <param name="cpu">Logical CPU.</param>
   /// <param name="before">The earlier count, or null.</param>
   /// <param name="after">The later count, or null.</param>
   /// <param name="notes">Receives a note for a counter that went down.</param>
   /// <returns>The rise.</returns>
   private static long Delta( string name, int cpu, long? before, long? after, List<string> notes )
   {
      long now = after ?? 0;
      if( before is not long then )
      {
         return now;
      }

      if( now >= then )
      {
         return now - then;
      }

      notes.Add( string.Create( CultureInfo.InvariantCulture, $"the {name} counter of CPU {cpu} went down from {then} to {now}, counted as a rise" ) );
      return Math.Max( 1, now );
   }

   /// <summary>
   /// How one side of a comparison was read, for a problem text.
   /// </summary>
   /// <param name="counts">The reading, or null.</param>
   /// <returns>"not taken", the unreadable counters, or "read".</returns>
   private static string Side( ThrottleCounts? counts )
   {
      return counts == null ? "not taken" : counts.Unreadable ?? "read";
   }

   /// <summary>
   /// Keeps the largest rise per core or package.
   /// </summary>
   /// <param name="largest">Largest rise per key.</param>
   /// <param name="key">The core's siblings or the package id.</param>
   /// <param name="rise">This CPU's rise.</param>
   private static void Keep( Dictionary<string, long> largest, string key, long rise )
   {
      largest[key] = Math.Max( largest.GetValueOrDefault( key ), rise );
   }

   #endregion Private Methods
}

/// <summary>
/// How the engine CPU per search was measured on this run, with the cgroups it found
/// (conditions.engineCpu). The rule text is generated from these values and from which cgroups
/// each target's turn really subtracted from outside load (conditions.engines[].cgroups).
/// </summary>
public sealed class EngineCpuRecord
{
   #region Public Methods

   /// <summary>dockerd's cgroup folder, or null when no dockerd process was found.</summary>
   public string? DockerdCgroup { get; set; }

   /// <summary>Command names of the processes in dockerd's cgroup when it was found (dockerd, docker-proxy, ...).</summary>
   public List<string> DockerdCgroupProcesses { get; set; } = new();

   /// <summary>cgroup folders of the containerd-shim processes seen (at the start and after each compose engine started).</summary>
   public List<string> ContainerdShimCgroups { get; set; } = new();

   /// <summary>The rule, generated by <see cref="Describe"/>.</summary>
   public string Rule { get; set; } = string.Empty;

   /// <summary>
   /// Generates the rule text from the record and the targets' cgroups.
   /// </summary>
   /// <param name="engines">Each target's pinning, with the cgroups its turn subtracted from outside load.</param>
   /// <param name="interval">Time between samples.</param>
   /// <returns>The text.</returns>
   public string Describe( IEnumerable<TargetPinning> engines, TimeSpan interval )
   {
      List<TargetPinning> list = engines.ToList();
      List<string> with = DockerdCgroup == null ? new List<string>() : list.Where( e => e.Cgroups.Contains( DockerdCgroup, StringComparer.Ordinal ) ).Select( e => e.Target ).Distinct().ToList();
      List<string> without = list.Select( e => e.Target ).Distinct().Where( t => !with.Contains( t ) ).ToList();
      string dockerd = DockerdCgroup == null
         ? "no dockerd process was found, so no cgroup of it was followed"
         : $"dockerd's cgroup ({DockerdCgroup}, holding {( DockerdCgroupProcesses.Count > 0 ? string.Join( ", ", DockerdCgroupProcesses ) : "processes not read" )}, for every container on the box) is left out of engine CPU per search and given as dockerdCpuMsPerSearch; "
            + $"it is subtracted from outside load as benchmark work during the turns of {List( with )}, and counts as outside load during the turns of {List( without )}";
      string shim = ContainerdShimCgroups.Count == 0
         ? "no containerd-shim process was seen"
         : $"containerd-shim runs in {string.Join( ", ", ContainerdShimCgroups )}, which is in neither engine figure and counts as outside load";
      return string.Create( CultureInfo.InvariantCulture, $"engine CPU per search is the CPU time (cpu.stat usage_usec) of the cgroups each target's turn followed (conditions.engines[].cgroups, dockerd's left out), read with every {interval.TotalMilliseconds:0} ms sample, interpolated to the pass's timed window and divided by its completed searches; engineCpusBusy is that CPU time over the window's length. A followed cgroup counts every process in it. " )
         + $"{dockerd}. {shim}. An embedded engine runs in this client process, so its CPU is in clientCpuMsPerSearch and its engine figure is null with the reason; a target whose engine has no cgroup followed, or whose containers could not all be pinned, also gets null with the reason, never 0.";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Target names as text.
   /// </summary>
   /// <param name="targets">Names.</param>
   /// <returns>"a, b", or "no target".</returns>
   private static string List( List<string> targets )
   {
      return targets.Count == 0 ? "no target" : string.Join( ", ", targets );
   }

   #endregion Private Methods
}
