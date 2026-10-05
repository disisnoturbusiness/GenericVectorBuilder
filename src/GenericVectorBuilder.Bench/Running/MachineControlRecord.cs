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

   /// <summary>What the quiet check came right before: "warm-up" (the rule), or the timed pass when no warm-up line was seen.</summary>
   public string? QuietCheckBefore { get; set; }

   /// <summary>When the quiet check let the pass go on (UTC).</summary>
   public string? QuietCheckUtc { get; set; }

   /// <summary>Seconds from the quiet check to the start of the timed window (the warm-up, plus the settle trial before a target's first pass).</summary>
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
   /// over <see cref="Searches"/>. For the fastest engines this is about as large as the latency
   /// itself, so the order of such engines partly reflects their .NET client libraries.
   /// </summary>
   public double? ClientCpuMsPerSearch { get; set; }

   /// <summary>What the client CPU figure was measured over, or null when it was not.</summary>
   public string? ClientCpuBasis { get; set; }

   /// <summary>Why there is no client CPU per search for this pass, or null when there is.</summary>
   public string? ClientCpuProblem { get; set; }

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
         flags.Add( $"WARNING: machine control {c.MachineControl}: the governor was {c.Governor}, nothing was pinned, client and engine shared every CPU, and no pass waited for a quiet box." );
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
      flags.AddRange( c.Passes.Where( p => p.AccountingMismatch ).Select( p => $"WARNING: accounting mismatch in {p.Target} {p.Pass}: {p.AccountingMismatchReason}; the kernel's busy time minus this process minus the engine's cgroups came out negative, so the outside-load figures of this pass cannot be trusted." ) );
      flags.AddRange( on ? c.Passes.Where( p => p.ClientCpuMsPerSearch == null ).Select( p => $"WARNING: no client CPU per search for {p.Target} {p.Pass}: {p.ClientCpuProblem ?? "it was never recorded"}." ) : Array.Empty<string>() );
      flags.AddRange( IdleFlags( c ) );
      flags.AddRange( c.Connections.SelectMany( t => t.Observed.Where( p => p.Kind.StartsWith( "docker-proxy", StringComparison.Ordinal ) )
         .Select( p => $"WARNING: {t.Target} had {p.Sockets} connection(s) open to its published port {p.Address}:{p.Port}, which goes through docker-proxy, not the container address it was meant to use." ) ) );
      List<string> unseen = on ? searchedTargets.Where( t => c.Passes.All( p => p.Target != t ) ).ToList() : new List<string>();
      if( unseen.Count > 0 )
      {
         flags.Add( $"WARNING: no timed pass of {string.Join( ", ", unseen )} was seen in the progress output, so those passes have no clock record and did not wait for a quiet box." );
      }

      List<string> tooShort = c.Passes.Where( p => p.NearestSample ).Select( p => $"{p.Target} {p.Pass}" ).ToList();
      if( tooShort.Count > 0 )
      {
         flags.Add( $"Passes shorter than one clock sample (250 ms) show the sample nearest their middle: {string.Join( ", ", tooShort )}." );
      }

      if( c.SamplingError != null )
      {
         flags.Add( $"WARNING: CPU sampling failed at least once ({c.SamplingError}); clock and outside-load figures may have gaps." );
      }

      if( c.RestoreProblems.Count > 0 )
      {
         flags.Add( $"WARNING: the machine was NOT fully put back: {string.Join( "; ", c.RestoreProblems )}. The state file {c.StateFile} keeps what is left; fix the cause, then run 'GenericVectorBuilder.Bench restore-machine'." );
      }

      return flags;
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
   /// One line per pass for a target's notes: "default@1 3491/3492 MHz".
   /// </summary>
   /// <param name="passes">The target's passes.</param>
   /// <returns>The text, or null when there are none.</returns>
   public static string? ClockLine( IEnumerable<PassConditions> passes )
   {
      List<string> parts = passes.Select( p => string.Create( CultureInfo.InvariantCulture,
         $"{p.Pass} {p.EngineMhzMedian?.ToString( CultureInfo.InvariantCulture ) ?? "?"}/{p.ClientMhzMedian?.ToString( CultureInfo.InvariantCulture ) ?? "?"} MHz, {p.Governor ?? "?"}, outside load {Load( p.OutsideLoadAtStart )} before the warm-up{( p.WaitedSeconds > 0 ? $" after waiting {p.WaitedSeconds:0} s" : string.Empty )}, {Load( p.OutsideLoadDuring )} during{( p.BusyBox ? ", BUSY BOX" : string.Empty )}{( p.ClientCpuMsPerSearch is double cpu ? $", client CPU {cpu:0.000} ms per search" : string.Empty )}" ) ).ToList();
      return parts.Count == 0 ? null : "Clock per pass (median MHz of engine CPUs / client CPUs, governor, CPUs busy outside the benchmark, client CPU per search): " + string.Join( "; ", parts ) + ".";
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
