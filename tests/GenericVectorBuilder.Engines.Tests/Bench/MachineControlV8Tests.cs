using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The v8 machine-state records, checked against the real benchmark code (compiled with the
/// scenarios by <see cref="MachineControlCompiler"/>) on a fake machine, and the median clock rule
/// checked on every timed pass of the three v7 runs: the median rule replaces the mean-based clock
/// warning, conditions.clock and the thermal throttle counters are recorded as fields, and each
/// pass gets the engine's CPU per search from a per-sample ledger with dockerd's cgroup left out
/// of the engine figure while the outside load keeps the v7 accounting.
/// Why these get tests: the consolidation and the published page read these fields, and a field
/// that is wrong, missing or zero where it should be null would reach a published sentence
/// unseen. Every test has a deadline, since a scenario that hangs must fail, not stall the suite.
/// </summary>
[Collection( TimingCollection.NAME )]
public class MachineControlV8Tests
{
   #region Data Members

   private static readonly TimeSpan DEADLINE = TimeSpan.FromMinutes( 4 );
   private const string ENGINE_GROUP = "/sys/fs/cgroup/system.slice/docker-beef01.scope";
   private const string DOCKERD_GROUP = "/sys/fs/cgroup/system.slice/docker.service";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The fixture is the three v7 runs' passes exactly: each results.json still has the SHA-256 the
   /// fixture was copied from, and the run, target, pass, recorded group medians and per-CPU medians
   /// of all 156 passes, in file order, equal the fixture's lines. Why: proof (a) judges the copy,
   /// so the copy must be the record.
   /// </summary>
   [Fact]
   public async Task V7Fixture_IsTheThreeV7RunsPassForPass()
   {
      await Within( () =>
      {
         var lines = new List<string>();
         foreach( ( string folder, string sha256 ) in MachineControlV7ClockFixture.SOURCES )
         {
            string path = Path.Combine( RepoRoot(), "bench-results", folder, "results.json" );
            Assert.True( File.Exists( path ), $"{path} is missing" );
            Assert.Equal( sha256, Convert.ToHexStringLower( SHA256.HashData( File.ReadAllBytes( path ) ) ) );
            using JsonDocument doc = JsonDocument.Parse( File.ReadAllText( path ) );
            JsonElement conditions = doc.RootElement.GetProperty( "conditions" );
            Assert.Equal( MachineControlV7ClockFixture.ENGINE_CPUS, conditions.GetProperty( "engineCpus" ).GetString() );
            Assert.Equal( MachineControlV7ClockFixture.CLIENT_CPUS, conditions.GetProperty( "clientCpus" ).GetString() );
            lines.AddRange( conditions.GetProperty( "passes" ).EnumerateArray().Select( p => FixtureLine( folder[9..15], p ) ) );
         }

         string[] fixture = MachineControlV7ClockFixture.PASSES.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
         Assert.Equal( 156, lines.Count );
         Assert.Equal( fixture, lines.ToArray() );
         return true;
      } );
   }

   /// <summary>
   /// Proof (a): with the pinned clock injected as 3500 MHz (results.json leaves PinnedMhz out, so
   /// without it nothing would be judged), all 156 v7 passes are evaluated, every group has a
   /// reading, none is off, and no warning is raised, including the three Oracle default@8 passes
   /// the mean rule flagged. The group medians the code computes from the per-CPU medians equal
   /// the recorded ones. The same passes judged against 3300 MHz are all off, so the rule is not
   /// vacuous.
   /// </summary>
   [Fact]
   public async Task V7Passes_MedianRuleEvaluatesAll156AndFlagsNone()
   {
      dynamic r = await Within( () => MachineControlCompiler.Call( "V7ClockJudge", MachineControlV7ClockFixture.PASSES, MachineControlV7ClockFixture.ENGINE_CPUS, MachineControlV7ClockFixture.CLIENT_CPUS, MachineControlV7ClockFixture.PINNED_MHZ ) );
      Assert.Equal( 156, (int)r.Passes );
      Assert.Equal( 156, (int)r.Evaluated );
      Assert.Equal( 0, (int)r.Off );
      Assert.Equal( 0, (int)r.Unread );
      Assert.Empty( (string[])r.Warnings );
      Assert.Empty( (string[])r.Differ );
      Assert.Equal( new[]
      {
         "130619 oracle default@8 evaluated=True read=True off=False",
         "142724 oracle default@8 evaluated=True read=True off=False",
         "154837 oracle default@8 evaluated=True read=True off=False",
      }, (string[])r.Oracle );
      dynamic low = await Within( () => MachineControlCompiler.Call( "V7ClockJudge", MachineControlV7ClockFixture.PASSES, MachineControlV7ClockFixture.ENGINE_CPUS, MachineControlV7ClockFixture.CLIENT_CPUS, 3300 ) );
      Assert.Equal( 156, (int)low.Evaluated );
      Assert.Equal( 156, (int)low.Off );
      Assert.Equal( 156, ( (string[])low.Warnings ).Length );
   }

   /// <summary>
   /// The passes proof (a) clears are the ones the mean rule flagged: the v7 notes carry exactly
   /// three mean-based clock warnings, one per run, each for oracle default@8, at -10.8%, -10.6% and
   /// -9.5% on the engine CPUs.
   /// </summary>
   [Fact]
   public async Task V7Notes_TheMeanRuleFlaggedOnlyTheThreeOracleDefault8Passes()
   {
      string[] warnings = await Within( () => MachineControlV7ClockFixture.SOURCES
         .SelectMany( s => TargetNotes( Path.Combine( RepoRoot(), "bench-results", s.Folder, "results.json" ) ) )
         .Where( n => n.StartsWith( "WARNING: clock off its pinned value during ", StringComparison.Ordinal ) ).ToArray() );
      Assert.Equal( 3, warnings.Length );
      Assert.All( warnings, w => Assert.StartsWith( "WARNING: clock off its pinned value during oracle default@8: the engine CPUs averaged ", w ) );
      Assert.Contains( "3121 MHz, -10.8% against the pinned 3500 MHz", warnings[0] );
      Assert.Contains( "3129 MHz, -10.6% against the pinned 3500 MHz", warnings[1] );
      Assert.Contains( "3168 MHz, -9.5% against the pinned 3500 MHz", warnings[2] );
   }

   /// <summary>
   /// Proof (b): a group median of 3300 MHz against 3500 is flagged off; a pass with no reading on
   /// a group is flagged "clock not read" (clockRead false, clockOff null); with the CPUs not split
   /// every CPU is one group (the all-CPU fallback), judged in a whole run too, where the rule text
   /// names the fallback.
   /// </summary>
   [Fact]
   public async Task Synthetic_MedianFlags_UnreadFlags_UnsplitUsesTheFallback()
   {
      string[] cases = await Within( () => (string[])MachineControlCompiler.Call( "SyntheticClock" ) );
      Assert.Equal( new[]
      {
         "low read=True off=True warning=WARNING: clock off its pinned value during low default@1: the median on the engine CPUs was 3300 MHz, -5.7% against the pinned 3500 MHz (limit 100 bp, 1%); this pass is not comparable with passes at the pinned clock.",
         "unread read=False off= warning=WARNING: clock not read during unread default@1: no clock was read during it on the engine CPUs, so it is not known whether it ran at the pinned 3500 MHz.",
         "fallback read=True off=False warning=none",
         "fallbacklow read=True off=True warning=WARNING: clock off its pinned value during fallbacklow default@1: the median on every CPU (the CPUs were not split) was 3300 MHz, -5.7% against the pinned 3500 MHz (limit 100 bp, 1%); this pass is not comparable with passes at the pinned clock.",
      }, cases );
      string[][] run = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "UnsplitRunAsync", folder ) );
      Assert.Equal( new[] { "default@1 engine=null client=3491 read=True off=False", "default@8 engine=null client=3300 read=True off=True" }, run[0] );
      Assert.Contains( "the CPUs were not split, so every CPU (0-1) is judged as one group (the all-CPU fallback)", run[1][0] );
      Assert.Equal( new[] { "WARNING: clock off its pinned value during t default@8: the median on every CPU (the CPUs were not split) was 3300 MHz, -5.7% against the pinned 3500 MHz (limit 100 bp, 1%); this pass is not comparable with passes at the pinned clock." }, run[2] );
   }

   /// <summary>
   /// The rule text carries what the brief requires and no v7 history: the median of per-CPU
   /// medians per group, the fallback, its limited power, thermal counters only (no power-limit
   /// files), and the observer's APERF/MPERF reading as the independent check.
   /// </summary>
   [Fact]
   public async Task RuleText_SaysTheRuleAndItsLimits()
   {
      string[][] run = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "UnsplitRunAsync", folder ) );
      string rule = run[1][0];
      Assert.Contains( "each CPU's median over a pass is taken, then the median of those medians per CPU group", rule );
      Assert.Contains( "Limited power: a CPU's median hides off-clock readings while they are fewer than half of its readings", rule );
      Assert.Contains( "the independent check of the clock is APERF/MPERF, read by the run's observer outside this tool", rule );
      Assert.Contains( "They count thermal events only: no power-limit counters are read, so power capping (RAPL) and AVX clock drops are not seen.", rule );
      Assert.Contains( "only a rise against no rise means anything", rule );
      Assert.DoesNotContain( "v7", rule, StringComparison.Ordinal );
      Assert.DoesNotContain( "312", rule, StringComparison.Ordinal );
   }

   /// <summary>
   /// Proof (c): the core counter is counted once per physical core and the package counter once
   /// per package; a rise on one sibling only is seen; a counter that went down counts as a rise
   /// and is named; an unreadable counter leaves the totals null with the reason and the rise not
   /// compared (null, never 0); a CPU whose siblings cannot be read is counted as its own core.
   /// </summary>
   [Fact]
   public async Task Throttle_OncePerCoreAndPackage_UnreadableAndDecreasing()
   {
      string[] cases = await Within( () => (string[])MachineControlCompiler.Call( "ThrottleCases" ) );
      Assert.Equal( new[]
      {
         "first core=5 package=7 unreadable=none",
         "sibling rise=core events +1, package events +0 rose=True",
         "none rise=core events +0, package events +0 rose=False",
         "down rise=core events +0, package events +3 (the package counter of CPU 1 went down from 7 to 3, counted as a rise) rose=True",
         "unreadable core=null why=no readable core_throttle_count or package_throttle_count under /sys/devices/system/cpu/cpu3/thermal_throttle on CPU(s) 3 rise=the counters were not read on both sides (first reading: read; second reading: no readable core_throttle_count or package_throttle_count under /sys/devices/system/cpu/cpu3/thermal_throttle on CPU(s) 3) risecore=null",
         "notopology core=6 note=the thread siblings of CPU(s) 2 could not be read, so each is counted as its own core",
      }, cases );
   }

   /// <summary>
   /// In a run the counters are read at each target's start, first warm-up line and end, and at the
   /// run's start and end, never while a pass is timed; a second warm-up line of a target does not
   /// replace its first snapshot (target b's rise after its first warm-up line shows from it); a
   /// rise during a target's load (before its first warm-up line) shows only in the whole-target
   /// figure. Every rise is a warning in the run's notes.
   /// </summary>
   [Fact]
   public async Task Throttle_ReadAtTargetEdgesAndFirstWarmupOnly()
   {
      string[] r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "ThrottleRunAsync", folder ) );
      Assert.Equal( "{\"a\":{\"coreRise\":1,\"packageRise\":0,\"fromFirstWarmup\":{\"coreRise\":0,\"packageRise\":0}},\"b\":{\"coreRise\":1,\"packageRise\":2,\"fromFirstWarmup\":{\"coreRise\":1,\"packageRise\":2}}}", r[0] );
      using JsonDocument run = JsonDocument.Parse( r[1] );
      Assert.Equal( 0, run.RootElement.GetProperty( "atStart" ).GetProperty( "coreEvents" ).GetInt64() );
      Assert.Equal( 2, run.RootElement.GetProperty( "atEnd" ).GetProperty( "coreEvents" ).GetInt64() );
      Assert.Equal( 2, run.RootElement.GetProperty( "atEnd" ).GetProperty( "packageEvents" ).GetInt64() );
      string[] notes = r[2].Split( '\n' );
      Assert.Contains( "WARNING: the thermal throttle counters rose during the run (core events +2, package events +2 from its start to its end); only a rise against no rise is meaningful.", notes );
      Assert.Contains( "WARNING: the thermal throttle counters rose during a (core events +1, package events +0 from the target's start to its end; core events +0, package events +0 from its first warm-up line to its end).", notes );
      Assert.Contains( "WARNING: the thermal throttle counters rose during b (core events +1, package events +2 from the target's start to its end; core events +1, package events +2 from its first warm-up line to its end).", notes );
      Assert.Equal( string.Empty, r[3] );
   }

   /// <summary>
   /// Proof (d), the ledger: with the engine's cgroup at 2.0 CPUs and dockerd's at 0.08, the
   /// engine figure over a window is the engine's alone (dockerd's is its own figure), windows whose
   /// ends fall between samples are interpolated exactly, and the outside load still subtracts
   /// both cgroups (0.1 CPU of outside work). A window outside the samples, shorter than four
   /// samples, across a change of the followed cgroups, with a cgroup unread at an edge, with no
   /// engine cgroup, or with a counter that went down gives null with the reason; an idle engine
   /// gives a real 0.
   /// </summary>
   [Fact]
   public async Task Ledger_DockerdOut_EdgesInterpolated_NullNotZero()
   {
      string[] r = await Within( () => (string[])MachineControlCompiler.Call( "LedgerCases" ) );
      Assert.Equal( new[]
      {
         "window engine=4.400000 dockerd=0.176000 problem=none",
         "edges engine=9.480000 dockerd=0.379200 problem=none",
         "outside=0.100000",
         "past engine=null dockerd=null problem=the window 12:00:04.000 to 12:00:06.000 lies outside the sampled span 12:00:00.000 to 12:00:05.000",
         "before engine=null dockerd=null problem=the window 11:59:59.000 to 12:00:02.000 lies outside the sampled span 12:00:00.000 to 12:00:05.000",
         "short engine=null dockerd=null problem=the window of 0.5 s is shorter than 4 samples of 250 ms",
         "changed engine=null dockerd=null problem=the followed cgroups changed inside the pass window (at or before 12:00:03.250)",
         "beforechange engine=4.200000 dockerd=0.168000 problem=none",
         "missing engine=null dockerd=null problem=1 followed cgroup(s) could not be read at 12:00:03.250 (no usage_usec in cpu.stat)",
         "missingelsewhere engine=4.200000 dockerd=0.168000 problem=none",
         "nogroup engine=null dockerd=null problem=no cgroup of the engine under test was followed at 12:00:01.000",
         "down engine=null dockerd=null problem=the engine cgroups' CPU counter went down by 15.4 s inside the window (a cgroup was made again)",
         "idle engine=0.000000 dockerd=0.000000 problem=none",
         "usage_usec 1300000",
      }, r );
   }

   /// <summary>
   /// Each pass's figures come from the ledger over the window of its own pass note (written by the
   /// real TargetRunner.DescribePass): 4.4 CPU seconds over 4,400 searches in 2.2 s is 1 ms per
   /// search and 2 CPUs busy, dockerd's 0.176 s is 0.04 ms per search; a pass with no note and an
   /// embedded engine get null with the reason.
   /// </summary>
   [Fact]
   public async Task Ledger_PassFiguresFromTheirNotes()
   {
      string[] r = await Within( () => (string[])MachineControlCompiler.Call( "LedgerPasses" ) );
      Assert.Equal( new[]
      {
         "t default@8 engine=1.0000 dockerd=0.0400 busy=2.0000 reason=none",
         "t exact engine=null dockerd=null busy=null reason=no 'Pass exact after ...' note with a search count was found for this pass",
         "duckdb default@8 engine=null dockerd=null busy=null reason=embedded engine: test",
      }, r );
   }

   /// <summary>
   /// Proof (d), allocation: 10,000 ledger appends allocate nothing (the array is preallocated),
   /// and 10,000 kept samples allocate under 320 bytes each above a control that does every read
   /// and keeps nothing (FakeMachine allocates on every read, so only the delta means anything).
   /// Why it matters: the sampler runs in the client process, so its CPU is part of every pass's
   /// client CPU per search. The ledger answers an idle engine with 0, not null.
   /// </summary>
   [Fact]
   public async Task Ledger_TenThousandSamplesAllocateUnderTheBound()
   {
      double[] r = await Within( () => (double[])MachineControlCompiler.Call( "LedgerAllocation" ) );
      Assert.True( r[0] < 320, string.Create( CultureInfo.InvariantCulture, $"{r[0]:0.0} bytes per kept sample above the control" ) );
      Assert.Equal( 0, r[1] );
      Assert.Equal( 10_000, r[2] );
      Assert.Equal( 0, r[3] );
   }

   /// <summary>
   /// The outside load is the v7 accounting, bit for bit: 2,000 samples of seeded random counters,
   /// with dockerd's cgroup and the engine's followed (and only the engine's every other 300
   /// samples) and dockerd's file unreadable now and then, give the same double for every interval
   /// as the v7 code's arithmetic (copied from f662f82 as V7Outside), and no interval v7 left out.
   /// Why: dockerd's CPU is subtracted from outside load as benchmark work during a compose turn,
   /// as in v7, so the busy flags and the 0.3 limit mean what they meant in v7.
   /// </summary>
   [Fact]
   public async Task OutsideLoad_IsTheV7AccountingBitForBit()
   {
      string[] r = await Within( () => (string[])MachineControlCompiler.Call( "OutsideParity" ) );
      Assert.True( int.Parse( r[0], CultureInfo.InvariantCulture ) > 1900, $"only {r[0]} intervals compared" );
      Assert.Equal( "0", r[1] );
      Assert.Equal( "none", r[2] );
      Assert.Equal( "0", r[3] );
   }

   /// <summary>
   /// Proof (e): results.json carries the contract's names (contract.md, "results.json, P1"):
   /// conditions.clock {pinned, pinnedMhz, ceilingBeforeMhz, noTurbo {before, during, atEnd},
   /// uncoreMsr620 {before, during, atEnd}, throttle {atStart, atEnd} with coreEvents and
   /// packageEvents, toleranceBp, rule}, conditions.throttleByTarget per target with coreRise and
   /// packageRise, and per pass clockRead, clockOff, engineCpuMsPerSearch, engineCpuNullReason,
   /// dockerdCpuMsPerSearch, engineCpusBusy; atEnd only in the write after the end of the run. The
   /// values: turbo 0 then 1, uncore "0xc1e" then "0x1e1e", ceiling 3600 before, pinned 3500.
   /// </summary>
   [Fact]
   public async Task Contract_FieldNamesAndValuesInResultsJson()
   {
      string[] r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "ContractRunAsync", folder ) );
      using JsonDocument before = JsonDocument.Parse( r[0] );
      using JsonDocument after = JsonDocument.Parse( r[1] );
      JsonElement clockBefore = before.RootElement.GetProperty( "conditions" ).GetProperty( "clock" );
      Assert.False( clockBefore.GetProperty( "noTurbo" ).TryGetProperty( "atEnd", out _ ) );
      Assert.False( clockBefore.GetProperty( "uncoreMsr620" ).TryGetProperty( "atEnd", out _ ) );
      Assert.False( clockBefore.GetProperty( "throttle" ).TryGetProperty( "atEnd", out _ ) );
      JsonElement c = after.RootElement.GetProperty( "conditions" );
      JsonElement clock = c.GetProperty( "clock" );
      Assert.Equal( new[] { "pinned", "pinnedMhz", "ceilingBeforeMhz", "noTurbo", "uncoreMsr620", "throttle", "toleranceBp", "rule" }, clock.EnumerateObject().Select( p => p.Name ).ToArray() );
      Assert.True( clock.GetProperty( "pinned" ).GetBoolean() );
      Assert.Equal( 3500, clock.GetProperty( "pinnedMhz" ).GetInt32() );
      Assert.Equal( 3600, clock.GetProperty( "ceilingBeforeMhz" ).GetInt32() );
      Assert.Equal( new[] { 0, 1, 1 }, new[] { "before", "during", "atEnd" }.Select( k => clock.GetProperty( "noTurbo" ).GetProperty( k ).GetInt32() ).ToArray() );
      Assert.Equal( new[] { "0xc1e", "0x1e1e", "0x1e1e" }, new[] { "before", "during", "atEnd" }.Select( k => clock.GetProperty( "uncoreMsr620" ).GetProperty( k ).GetString() ).ToArray() );
      Assert.All( new[] { "atStart", "atEnd" }, k => Assert.Equal( 0, clock.GetProperty( "throttle" ).GetProperty( k ).GetProperty( "coreEvents" ).GetInt64() + clock.GetProperty( "throttle" ).GetProperty( k ).GetProperty( "packageEvents" ).GetInt64() ) );
      Assert.Equal( 100, clock.GetProperty( "toleranceBp" ).GetInt32() );
      Assert.Equal( new[] { "redis", "duckdb", "other" }, c.GetProperty( "throttleByTarget" ).EnumerateObject().Select( p => p.Name ).ToArray() );
      Assert.All( c.GetProperty( "throttleByTarget" ).EnumerateObject(), t => Assert.Equal( 0, t.Value.GetProperty( "coreRise" ).GetInt64() + t.Value.GetProperty( "packageRise" ).GetInt64() ) );
      AssertPasses( c.GetProperty( "passes" ) );
   }

   /// <summary>
   /// The engine CPU rule in conditions.engineCpu is generated from what the run did: dockerd's
   /// cgroup and the processes in it, the turns it was subtracted from outside load as benchmark
   /// work (the compose target) and the turns it counted as outside load (the others), and
   /// containerd-shim's cgroup, which is in neither engine figure and counts as outside load.
   /// The flags do not warn about the embedded and the no-lever target's null figures.
   /// </summary>
   [Fact]
   public async Task Contract_EngineCpuRuleSaysWhatTheCodeDid()
   {
      string[] r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "ContractRunAsync", folder ) );
      Assert.Contains( "dockerd's cgroup (" + DOCKERD_GROUP + ", holding docker-proxy, dockerd, for every container on the box) is left out of engine CPU per search and given as dockerdCpuMsPerSearch", r[2] );
      Assert.Contains( "it is subtracted from outside load as benchmark work during the turns of redis, and counts as outside load during the turns of duckdb, other.", r[2] );
      Assert.Contains( "containerd-shim runs in /sys/fs/cgroup/system.slice/containerd.service, which is in neither engine figure and counts as outside load.", r[2] );
      Assert.DoesNotContain( "no engine CPU per search", r[3], StringComparison.Ordinal );
      using JsonDocument after = JsonDocument.Parse( r[1] );
      JsonElement engines = after.RootElement.GetProperty( "conditions" ).GetProperty( "engines" );
      Assert.Equal( new[] { "compose", "embedded", "none" }, engines.EnumerateArray().Select( e => e.GetProperty( "kind" ).GetString() ).ToArray() );
      Assert.Equal( new[] { DOCKERD_GROUP, ENGINE_GROUP }, engines[0].GetProperty( "cgroups" ).EnumerateArray().Select( g => g.GetString() ).ToArray() );
      Assert.Equal( r[2], after.RootElement.GetProperty( "conditions" ).GetProperty( "engineCpu" ).GetProperty( "rule" ).GetString() );
   }

   /// <summary>
   /// A compose target whose containers cannot be listed when it is pinned gets no engine figure:
   /// null with the pinning problem as the reason, and a warning in the run's flags, never 0.
   /// </summary>
   [Fact]
   public async Task PinFailure_GivesNullWithTheReason()
   {
      string[] r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "PinFailsRunAsync", folder ) );
      Assert.Equal( "null", r[0] );
      Assert.Equal( "the engine's containers could not all be found and pinned (Could not list the running containers of redis.compose.yaml: exit 1: compose ps failed), so its followed cgroups may be incomplete", r[1] );
      Assert.Contains( "WARNING: no engine CPU per search for redis default@8: the engine's containers could not all be found and pinned (Could not list the running containers of redis.compose.yaml: exit 1: compose ps failed), so its followed cgroups may be incomplete.", r[2].Split( '\n' ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The per-pass checks of <see cref="Contract_FieldNamesAndValuesInResultsJson"/>: the compose
   /// pass has the engine at about 1.5 CPUs (1.5 ms per search at 1,000 searches per second over its 0.4 s window, the search count following the measured window so timer jitter cannot move it), dockerd
   /// at about 0.1 ms per search, outside load near zero; the embedded and the no-lever passes have
   /// null figures with reasons, and dockerd's 0.1 CPU is outside load during their turns.
   /// </summary>
   /// <param name="passes">conditions.passes.</param>
   private static void AssertPasses( JsonElement passes )
   {
      Assert.Equal( 3, passes.GetArrayLength() );
      Assert.All( passes.EnumerateArray(), p =>
      {
         Assert.True( p.GetProperty( "clockRead" ).GetBoolean() );
         Assert.False( p.GetProperty( "clockOff" ).GetBoolean() );
      } );
      JsonElement redis = passes[0];
      Assert.InRange( redis.GetProperty( "engineCpuMsPerSearch" ).GetDouble(), 1.35, 1.65 );
      Assert.InRange( redis.GetProperty( "engineCpusBusy" ).GetDouble(), 1.35, 1.65 );
      Assert.InRange( redis.GetProperty( "dockerdCpuMsPerSearch" ).GetDouble(), 0.07, 0.13 );
      Assert.InRange( redis.GetProperty( "outsideLoadDuring" ).GetDouble(), -0.05, 0.05 );
      Assert.False( redis.TryGetProperty( "engineCpuNullReason", out _ ) );
      foreach( ( int index, string reason ) in new[] { ( 1, "embedded engine: it runs inside this client process (its CPU is part of clientCpuMsPerSearch), so it has no cgroup of its own" ), ( 2, "no cgroup of this target's engine was followed (none)" ) } )
      {
         JsonElement pass = passes[index];
         Assert.Equal( reason, pass.GetProperty( "engineCpuNullReason" ).GetString() );
         Assert.False( pass.TryGetProperty( "engineCpuMsPerSearch", out _ ) || pass.TryGetProperty( "engineCpusBusy", out _ ) || pass.TryGetProperty( "dockerdCpuMsPerSearch", out _ ) );
         Assert.InRange( pass.GetProperty( "outsideLoadDuring" ).GetDouble(), 0.05, 0.15 );
      }
   }

   /// <summary>
   /// One fixture line from a v7 pass record.
   /// </summary>
   /// <param name="run">The run's time, e.g. "130619".</param>
   /// <param name="pass">conditions.passes[i].</param>
   /// <returns>"run|target|pass|engineMhzMedian|clientMhzMedian|cpu0,...,cpu7 medians".</returns>
   private static string FixtureLine( string run, JsonElement pass )
   {
      Dictionary<int, int> medians = pass.GetProperty( "cpuMhz" ).EnumerateArray().ToDictionary( m => m.GetProperty( "cpu" ).GetInt32(), m => m.GetProperty( "median" ).GetInt32() );
      string perCpu = string.Join( ",", Enumerable.Range( 0, 8 ).Select( i => medians[i].ToString( CultureInfo.InvariantCulture ) ) );
      return string.Create( CultureInfo.InvariantCulture, $"{run}|{pass.GetProperty( "target" ).GetString()}|{pass.GetProperty( "pass" ).GetString()}|{pass.GetProperty( "engineMhzMedian" ).GetInt32()}|{pass.GetProperty( "clientMhzMedian" ).GetInt32()}|{perCpu}" );
   }

   /// <summary>
   /// Every target note of a results.json, in file order.
   /// </summary>
   /// <param name="path">The file.</param>
   /// <returns>The notes.</returns>
   private static List<string> TargetNotes( string path )
   {
      using JsonDocument doc = JsonDocument.Parse( File.ReadAllText( path ) );
      return doc.RootElement.GetProperty( "targets" ).EnumerateArray()
         .SelectMany( t => t.TryGetProperty( "notes", out JsonElement notes ) ? notes.EnumerateArray().Select( n => n.GetString()! ) : Enumerable.Empty<string>() ).ToList();
   }

   /// <summary>
   /// Runs synchronous work on the thread pool with the test deadline, so a hang fails the test.
   /// </summary>
   /// <typeparam name="T">Result type.</typeparam>
   /// <param name="work">The work.</param>
   /// <returns>Its result.</returns>
   private static async Task<T> Within<T>( Func<T> work )
   {
      return await Task.Run( work ).WaitAsync( DEADLINE );
   }

   /// <summary>
   /// Walks up from the test binary to the folder holding the solution file (the v7 results are
   /// committed under its bench-results).
   /// </summary>
   /// <returns>The repository root.</returns>
   private static string RepoRoot()
   {
      var dir = new DirectoryInfo( AppContext.BaseDirectory );
      while( dir != null && !File.Exists( Path.Combine( dir.FullName, "GenericVectorBuilder.slnx" ) ) )
      {
         dir = dir.Parent;
      }

      return dir?.FullName ?? throw new InvalidOperationException( "Repository root not found from " + AppContext.BaseDirectory );
   }

   #endregion Private Methods
}
