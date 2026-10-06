using System.Text.Json;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The pinned CPU clock of machine control, checked against the real code on a fake machine:
/// turbo off and the uncore limit pinned for the run (recorded before the change), every CPU's
/// ceiling the same, the clock put back exactly by the run's own restore, by the next start after
/// a crash and by restore-machine, a refusal that changes nothing when the clock cannot be
/// pinned, each pass's average clock per CPU group in the notes, and a pass more than 1% off the
/// pinned clock flagged.
/// Why these get tests: the v6 runs ran some engines about 2.9% faster than others, next to a 3%
/// tie band, because the turbo clock followed the code the cores ran (AVX2 code on any core held
/// every core at the base clock); with turbo off the uncore clock then followed the load instead.
/// A clock change left behind after a crash would skew every later run on the box.
/// </summary>
public class MachineControlClockTests
{
   #region Data Members

   private const string NO_TURBO = "/sys/devices/system/cpu/intel_pstate/no_turbo";
   private const string BOOST = "/sys/devices/system/cpu/cpufreq/boost";
   private const string WRITE = "sudo -n sh -c printf '%s\\n' \"$1\" > \"$2\" gvb-bench ";
   private const string WRMSR = "sudo -n wrmsr -a 0x620 0x";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// intel_pstate: no_turbo goes to 1 for the run (the clock record holds 0 and every CPU's
   /// 3600000 kHz ceiling before the write), every CPU's ceiling reads 3500 MHz, and afterwards
   /// every value reads exactly as before with no file left and no ceiling written by hand (the
   /// kernel moves them back). The pass timed at the base clock is not flagged; the pass timed at
   /// 3592 MHz is, in its notes and in the run's; the summary note states the pinned clock; no
   /// new field reaches results.json.
   /// </summary>
   [Fact]
   public async Task Run_PinsTheClockAndPutsItBack()
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "ClockRunAsync", folder, "intel" ) );
      Assert.Null( (string?)r.Error );
      Assert.Equal( "turbo off (intel_pstate/no_turbo 1), ceiling 3500 MHz on CPUs 0-7, floor 1200 MHz on CPUs 0-7", (string)r.DuringText );
      Assert.Equal( (string)r.Before, (string)r.After );
      Assert.True( (bool)r.ClockFileDuring );
      Assert.False( (bool)r.ClockFileAfter );
      Assert.False( (bool)r.StateFileAfter );
      string[] calls = r.Calls;
      Assert.Equal( new[] { WRITE + "1 " + NO_TURBO, WRITE + "0 " + NO_TURBO }, calls.Where( c => c.EndsWith( NO_TURBO, StringComparison.Ordinal ) ).ToArray() );
      Assert.DoesNotContain( calls, c => c.Contains( "scaling_max_freq", StringComparison.Ordinal ) || c.Contains( "scaling_min_freq", StringComparison.Ordinal ) );
      Assert.Contains( WRITE + "1 " + NO_TURBO + " => " + NO_TURBO + " was 0, max 3600000", (string[])r.ClockAtCall );
      Assert.All( (string[])r.GovernorsAfter, g => Assert.Equal( "schedutil", g ) );
      string steadyClock = ( (string[])r.SteadyNotes ).Single( n => n.StartsWith( "Clock per pass", StringComparison.Ordinal ) );
      Assert.Contains( "default@1 3491/3491 MHz, average 3491.0/3491.0, performance", steadyClock );
      Assert.Contains( "the clock was pinned at 3500 MHz (turbo off)", steadyClock );
      Assert.DoesNotContain( (string[])r.SteadyNotes, n => n.Contains( "clock off", StringComparison.Ordinal ) );
      const string TURBO_WARNING = "WARNING: clock off its pinned value during turbo default@1: the engine CPUs averaged 3592 MHz, +2.6% against the pinned 3500 MHz; the client CPUs averaged 3592 MHz, +2.6% against the pinned 3500 MHz (limit 1%)";
      Assert.Contains( (string[])r.TurboNotes, n => n.StartsWith( TURBO_WARNING, StringComparison.Ordinal ) );
      string[] runNotes = r.RunNotes;
      Assert.Contains( runNotes, n => n.StartsWith( TURBO_WARNING, StringComparison.Ordinal ) );
      Assert.DoesNotContain( runNotes, n => n.Contains( "clock off its pinned value during steady", StringComparison.Ordinal ) || n.Contains( "clock settings changed during the run", StringComparison.Ordinal ) );
      Assert.Contains( runNotes, n => n.Contains( "CPU clock pinned for the run: turbo off (intel_pstate/no_turbo 0 -> 1), so every CPU's clock is held at its ceiling of 3500 MHz", StringComparison.Ordinal )
         && n.Contains( "after putting it back: turbo on (intel_pstate/no_turbo 0), ceiling 3600 MHz on CPUs 0-7", StringComparison.Ordinal ) );
      using JsonDocument json = JsonDocument.Parse( (string)r.ConditionsJson );
      JsonElement pass = json.RootElement.GetProperty( "passes" )[0];
      Assert.False( pass.TryGetProperty( "engineMhzMean", out _ ) || pass.TryGetProperty( "clientMhzMean", out _ ) || pass.TryGetProperty( "pinnedMhz", out _ ) );
      Assert.Contains( json.RootElement.GetProperty( "restored" ).EnumerateArray(), e => e.GetString()!.StartsWith( "CPU clock back: turbo on", StringComparison.Ordinal ) );
      Assert.Contains( (string[])r.Log, l => l.Contains( "clock: turbo off (intel_pstate/no_turbo 0 -> 1)", StringComparison.Ordinal ) );
      Assert.Equal( new[] { "c1e", "1e1e", "c1e" }, new[] { (string)r.UncoreBefore, (string)r.UncoreDuring, (string)r.UncoreAfter } );
      Assert.Equal( new[] { WRMSR + "1e1e", WRMSR + "c1e" }, calls.Where( c => c.Contains( "wrmsr", StringComparison.Ordinal ) ).ToArray() );
      Assert.Contains( runNotes, n => n.Contains( "the uncore (L3 and memory) clock is held at 3000 MHz (min 3000 MHz, max 3000 MHz (MSR 0x620 = 0x1e1e); was min 1200 MHz, max 3000 MHz (MSR 0x620 = 0xc1e))", StringComparison.Ordinal )
         && n.Contains( "Uncore limit at the end: min 3000 MHz, max 3000 MHz (MSR 0x620 = 0x1e1e); after putting it back: min 1200 MHz, max 3000 MHz (MSR 0x620 = 0xc1e)", StringComparison.Ordinal ) );
      Assert.DoesNotContain( runNotes, n => n.Contains( "uncore clock limit was not as pinned", StringComparison.Ordinal ) );
      Assert.Contains( json.RootElement.GetProperty( "restored" ).EnumerateArray(), e => e.GetString()!.EndsWith( "; uncore limit back to min 1200 MHz, max 3000 MHz (MSR 0x620 = 0xc1e)", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// Something else setting the uncore limit during the run is flagged at the run level.
   /// </summary>
   [Fact]
   public async Task Run_UncoreChangedUnderTheRun_IsFlagged()
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "ClockRunAsync", folder, "uncore-changed-under" ) );
      Assert.Null( (string?)r.Error );
      Assert.Contains( (string[])r.RunNotes, n => n == "WARNING: the uncore clock limit was not as pinned at the end of the run (pinned: min 3000 MHz, max 3000 MHz (MSR 0x620 = 0x1e1e); at the end: min 1200 MHz, max 3000 MHz (MSR 0x620 = 0xc1e)); passes may not all have run at the pinned uncore clock." );
      Assert.Equal( "c1e", (string)r.UncoreAfter );
      Assert.False( (bool)r.ClockFileAfter );
   }

   /// <summary>
   /// A box with only cpufreq's boost switch (acpi-cpufreq): boost goes to 0 for the run and back to 1.
   /// </summary>
   [Fact]
   public async Task Run_BoostSwitch()
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "ClockRunAsync", folder, "boost" ) );
      Assert.Null( (string?)r.Error );
      Assert.StartsWith( "turbo off (cpufreq/boost 0), ceiling 3500 MHz", (string)r.DuringText );
      Assert.Equal( (string)r.Before, (string)r.After );
      Assert.Equal( new[] { WRITE + "0 " + BOOST, WRITE + "1 " + BOOST }, ( (string[])r.Calls ).Where( c => c.EndsWith( BOOST, StringComparison.Ordinal ) ).ToArray() );
      Assert.False( (bool)r.ClockFileAfter );
   }

   /// <summary>
   /// Turbo already off before the run: nothing is written, either way, and it stays off.
   /// </summary>
   [Fact]
   public async Task Run_TurboAlreadyOff_WritesNothing()
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "ClockRunAsync", folder, "already-off" ) );
      Assert.Null( (string?)r.Error );
      Assert.DoesNotContain( (string[])r.Calls, c => c.Contains( NO_TURBO, StringComparison.Ordinal ) );
      Assert.Equal( (string)r.Before, (string)r.After );
      Assert.Contains( "no_turbo=1", (string)r.After );
      Assert.False( (bool)r.ClockFileAfter );
   }

   /// <summary>
   /// Something else turning turbo back on during the run is flagged at the run level, and the
   /// end of the run leaves the switch as it was before the run (nothing to write back).
   /// </summary>
   [Fact]
   public async Task Run_ClockChangedUnderTheRun_IsFlagged()
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "ClockRunAsync", folder, "changed-under" ) );
      Assert.Null( (string?)r.Error );
      Assert.Contains( (string[])r.RunNotes, n => n.StartsWith( "WARNING: the CPU clock settings changed during the run (pinned: turbo off (intel_pstate/no_turbo 1)", StringComparison.Ordinal )
         && n.Contains( "at the end: turbo on (intel_pstate/no_turbo 0)", StringComparison.Ordinal ) );
      Assert.Contains( "no_turbo=0", (string)r.After );
      Assert.Single( (string[])r.Calls, c => c.EndsWith( NO_TURBO, StringComparison.Ordinal ) );
      Assert.False( (bool)r.ClockFileAfter );
   }

   /// <summary>
   /// When the clock cannot be pinned (no switch, a refused write, CPUs left with different
   /// ceilings), the start fails loudly and leaves the box exactly as it found it: governors,
   /// clock, no state file and no clock record.
   /// </summary>
   /// <param name="mode">How pinning fails.</param>
   /// <param name="message">What the error must say.</param>
   [Theory]
   [InlineData( "no-switch", "The CPU clock cannot be pinned" )]
   [InlineData( "write-fails", "permission denied" )]
   [InlineData( "uneven", "do not share one clock ceiling" )]
   [InlineData( "uncore-missing", "The uncore clock cannot be pinned: 'rdmsr -a 0x620' failed (exit 1: rdmsr: open: No such file or directory). It needs msr-tools and the msr kernel module: load it with 'sudo modprobe msr'" )]
   [InlineData( "uncore-write-fails", "Could not set the uncore limit (MSR 0x620) to 0x1e1e" )]
   public async Task Run_ClockCannotBePinned_RefusesAndPutsEverythingBack( string mode, string message )
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "ClockRunAsync", folder, mode ) );
      Assert.Contains( message, (string)r.Error );
      Assert.Equal( (string)r.Before, (string)r.After );
      Assert.Equal( (string)r.UncoreBefore, (string)r.UncoreAfter );
      Assert.All( (string[])r.GovernorsAfter, g => Assert.Equal( "schedutil", g ) );
      Assert.False( (bool)r.ClockFileAfter );
      Assert.False( (bool)r.StateFileAfter );
   }

   /// <summary>
   /// After a crash the next start (or restore-machine) puts the clock back exactly from the
   /// clock record, with or without a state file beside it, and removes both; a ceiling the
   /// kernel did not move back is written back by hand and read back.
   /// </summary>
   /// <param name="mode">What the crashed run left.</param>
   [Theory]
   [InlineData( "clock-only" )]
   [InlineData( "with-state" )]
   [InlineData( "ceiling-moved" )]
   [InlineData( "with-uncore" )]
   public async Task Restore_ClockAfterCrash( string mode )
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "ClockRestoreAfterCrashAsync", folder, mode ) );
      Assert.Null( (string?)r.Error );
      Assert.Equal( (string)r.Before, (string)r.After );
      Assert.False( (bool)r.ClockFileLeft );
      Assert.False( (bool)r.StateFileLeft );
      string[] log = r.Log;
      Assert.Contains( log, l => l.Contains( "did not finish (pid 777", StringComparison.Ordinal ) && l.Contains( (string)r.ClockPath, StringComparison.Ordinal ) );
      Assert.Contains( log, l => l.StartsWith( "  machine: CPU clock back: turbo on", StringComparison.Ordinal ) );
      Assert.Contains( WRITE + "0 " + NO_TURBO, (string[])r.Calls );
      Assert.All( (string[])r.Governors, g => Assert.Equal( "schedutil", g ) );
      if( mode == "ceiling-moved" )
      {
         Assert.Contains( WRITE + "3600000 /sys/devices/system/cpu/cpu5/cpufreq/scaling_max_freq", (string[])r.Calls );
         Assert.Contains( log, l => l.Contains( "(1 written back by hand)", StringComparison.Ordinal ) );
      }

      if( mode == "with-uncore" )
      {
         Assert.Equal( "c1e", (string)r.UncoreAfter );
         Assert.Contains( WRMSR + "c1e", (string[])r.Calls );
         Assert.Contains( log, l => l.EndsWith( "; uncore limit back to min 1200 MHz, max 3000 MHz (MSR 0x620 = 0xc1e)", StringComparison.Ordinal ) );
         Assert.Contains( log, l => l.Contains( "uncore limit MSR 0x620 back to 0xc1e", StringComparison.Ordinal ) );
      }
      else
      {
         Assert.DoesNotContain( (string[])r.Calls, c => c.Contains( "msr", StringComparison.Ordinal ) );
      }
   }

   /// <summary>
   /// The clock record of a run that is still alive is never touched: the start refuses and
   /// nothing is sent.
   /// </summary>
   [Fact]
   public async Task Restore_ClockRefusedWhileOwnerAlive()
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "ClockRestoreAfterCrashAsync", folder, "live-owner" ) );
      Assert.Contains( "Another benchmark run (pid 777", (string)r.Error );
      Assert.Empty( (string[])r.Calls );
      Assert.True( (bool)r.ClockFileLeft );
   }

   /// <summary>
   /// A clock that cannot be put back keeps its record (and the start fails loudly); a record
   /// that names any file but a turbo switch is refused without a single write.
   /// </summary>
   /// <param name="mode">"write-fails" or "tampered".</param>
   /// <param name="message">What the error must say.</param>
   [Theory]
   [InlineData( "write-fails", "permission denied" )]
   [InlineData( "tampered", "is not one this tool sets" )]
   [InlineData( "uncore-tampered", "the uncore limit 0xffff0c1e is not one this tool records or sets" )]
   public async Task Restore_ClockFailureKeepsTheRecord( string mode, string message )
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "ClockRestoreAfterCrashAsync", folder, mode ) );
      Assert.Contains( "Could not put the machine back", (string)r.Error );
      Assert.Contains( message, (string)r.Error );
      Assert.True( (bool)r.ClockFileLeft );
      Assert.DoesNotContain( (string[])r.Calls, c => c.Contains( "/etc/sudoers", StringComparison.Ordinal ) || c.Contains( "wrmsr", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// The uncore limit: its pinned form raises the lowest ratio to the highest and keeps the
   /// rest; a value with reserved bits, no highest ratio, a lowest ratio above the highest, or
   /// that is not hex, is refused.
   /// </summary>
   [Fact]
   public void UncoreLimit_PinnedAndRefused()
   {
      string[] r = (string[])MachineControlCompiler.Call( "UncoreParsing" );
      Assert.Equal( "1e1e|min 1200 MHz, max 3000 MHz (MSR 0x620 = 0xc1e)|min 3000 MHz, max 3000 MHz (MSR 0x620 = 0x1e1e)|1e1e", r[0] );
      Assert.Equal( new[] { "refused ffff0c1e", "refused 0", "refused 1e0c", "refused zz" }, r[1..] );
   }

   /// <summary>
   /// The average clock of a CPU group is the mean of every reading of its CPUs in the window; a
   /// group never sampled has none; a window shorter than one sample takes the nearest sample.
   /// </summary>
   [Fact]
   public void Sampler_MeanPerGroup()
   {
      double?[] r = (double?[])MachineControlCompiler.Call( "SamplerMeans" );
      Assert.Equal( 3525.333, r[0]!.Value, 3 );
      Assert.Equal( 3525.333, r[1]!.Value, 3 );
      Assert.Null( r[2] );
      Assert.Equal( 3592, r[3]!.Value, 3 );
   }

   /// <summary>
   /// Only a pinned pass whose average on a CPU group is more than 1% off the pinned clock, or
   /// that has no clock reading, is flagged (1.1% off is, exactly 1.0% is not); an unpinned pass
   /// never is. The clock note gives the averages and the pinned clock, or says it was not pinned.
   /// </summary>
   [Fact]
   public void Flags_OnlyPassesMoreThanOnePercentOff()
   {
      string[][] r = (string[][])MachineControlCompiler.Call( "ClockFlags" );
      string[] warnings = r[1];
      Assert.Equal( 2, warnings.Length );
      Assert.StartsWith( "WARNING: clock off its pinned value during a default@8: the engine CPUs averaged 3540 MHz, +1.1% against the pinned 3500 MHz (limit 1%)", warnings[0] );
      Assert.Equal( "WARNING: no clock was read during b exact, so it is not known whether it ran at the pinned 3500 MHz.", warnings[1] );
      Assert.Contains( warnings[0], r[0] );
      Assert.Contains( warnings[1], r[0] );
      Assert.DoesNotContain( r[0], f => f.Contains( "during c ", StringComparison.Ordinal ) || f.Contains( "during b default@1", StringComparison.Ordinal ) );
      Assert.Contains( "default@1 3492/3492 MHz, average 3491.8/3491.8, performance", r[2][0] );
      Assert.Contains( "the clock was pinned at 3500 MHz (turbo off), and a pass whose average on either group is more than 1% off it is flagged", r[2][0] );
      Assert.Contains( "the clock was not pinned", r[2][1] );
   }

   /// <summary>
   /// The clock record sits beside the state file: same folder, same name with ".clock".
   /// </summary>
   [Fact]
   public void ClockRecord_BesideTheStateFile()
   {
      Assert.Equal( new[] { "/x/bench-machine-state.clock.json", "/x/state.clock.json" }, (string[])MachineControlCompiler.Call( "ClockPaths" ) );
   }

   /// <summary>
   /// With machine control off the clock is only read: nothing is written to the turbo switch,
   /// and the summary note says the clock was not pinned and what it was.
   /// </summary>
   [Fact]
   public async Task Off_ClockReadNotChanged()
   {
      dynamic r = await MachineControlCompiler.InFolderAsync( folder => MachineControlCompiler.CallAsync( "IdleAsync", folder, false ) );
      Assert.DoesNotContain( (string[])r.Calls, c => c.Contains( "no_turbo", StringComparison.Ordinal ) || c.Contains( "scaling_m", StringComparison.Ordinal ) );
      Assert.Contains( (string[])r.RunNotes, n => n.Contains( "clock not pinned (turbo on (intel_pstate/no_turbo 0), ceiling 3600 MHz on CPUs 0-7", StringComparison.Ordinal ) );
   }

   #endregion Public Methods
}
