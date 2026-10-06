using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// Machine control against the real box: the governor of every CPU is set to performance, turbo
/// is turned off and the uncore limit pinned through sudo, and all are put back, by the run's own
/// restore and, after a simulated crash, by the restore every start does first. Uses the real state file
/// (/home/dan/gvb-work/bench-machine-state.json), so if this test is killed half-way the next
/// benchmark start, or "restore-machine", still puts the governor back. Fails, rather than
/// waits, when a real benchmark run owns the machine.
/// Why live: the unit tests prove the rules on a fake machine; only the real box shows that
/// sudo, the sysfs files and the read-back really behave as the code assumes.
/// </summary>
[Trait( "Category", "Live" )]
public class MachineControlLiveTests
{
   #region Data Members

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xunit's output sink.
   /// </summary>
   /// <param name="output">Where the evidence is printed.</param>
   public MachineControlLiveTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Every CPU reads "performance" while controlled, the state file holds the governors from
   /// before, and afterwards every CPU reads exactly what it read before, with no state file
   /// left; the same after a run that "dies" and is restored by the next start.
   /// </summary>
   [Fact]
   public async Task Governor_SetAndPutBack()
   {
      dynamic r = await MachineControlCompiler.CallAsync( "LiveGovernorAsync" );
      string[] before = r.Before;
      _output.WriteLine( "before:      " + string.Join( " ", before ) );
      _output.WriteLine( "during:      " + string.Join( " ", (string[])r.During ) );
      _output.WriteLine( "recorded:    " + string.Join( " ", (string[])r.Recorded ) );
      _output.WriteLine( "after:       " + string.Join( " ", (string[])r.After ) );
      _output.WriteLine( "crash during:" + string.Join( " ", (string[])r.CrashDuring ) );
      _output.WriteLine( "crash after: " + string.Join( " ", (string[])r.CrashAfter ) );
      ( (string[])r.Log ).ToList().ForEach( _output.WriteLine );
      Assert.All( (string[])r.During, g => Assert.Equal( "performance", g ) );
      Assert.Equal( before, (string[])r.Recorded );
      Assert.Equal( before, (string[])r.After );
      Assert.False( (bool)r.FileAfter );
      Assert.All( (string[])r.CrashDuring, g => Assert.Equal( "performance", g ) );
      Assert.Equal( before, (string[])r.CrashAfter );
      Assert.False( (bool)r.CrashFileAfter );
      Assert.Contains( (string[])r.Log, l => l.Contains( "did not finish", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// The real clock: while machine control holds the box, turbo is off, every CPU shares one
   /// ceiling and the uncore limit's lowest ratio equals its highest, and the clock record holds
   /// the values from before; afterwards the turbo switch, every CPU's ceiling and floor and the
   /// uncore limit read exactly as before, with no file left. The same after a run that "dies"
   /// with the clock pinned and is put back by the next start.
   /// </summary>
   [Fact]
   public async Task Clock_PinnedAndPutBack()
   {
      dynamic r = await MachineControlCompiler.CallAsync( "LiveClockAsync" );
      _output.WriteLine( "before:       " + (string)r.Before );
      _output.WriteLine( "during:       " + (string)r.During );
      _output.WriteLine( "during text:  " + (string)r.DuringText );
      _output.WriteLine( "recorded:     " + (string)r.Recorded );
      _output.WriteLine( "after:        " + (string)r.After );
      _output.WriteLine( "crash during: " + (string)r.CrashDuring );
      _output.WriteLine( "crash after:  " + (string)r.CrashAfter );
      ( (string[])r.Log ).ToList().ForEach( _output.WriteLine );
      Assert.StartsWith( "turbo off (", (string)r.DuringText );
      Assert.Matches( "ceiling [0-9]+ MHz on CPUs [0-9,-]+, floor", (string)r.DuringText );
      Assert.Contains( "-> 1, 8 CPUs, uncore 0x", (string)r.Recorded );
      long during = long.Parse( ( (string)r.During ).Split( ";uncore=" )[1], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture );
      Assert.Equal( during & 0x7F, ( during >> 8 ) & 0x7F );
      Assert.Equal( (string)r.Before, (string)r.After );
      Assert.False( (bool)r.FilesAfter );
      Assert.Equal( (string)r.During, (string)r.CrashDuring );
      Assert.Equal( (string)r.Before, (string)r.CrashAfter );
      Assert.False( (bool)r.CrashFilesAfter );
      Assert.Contains( (string[])r.Log, l => l.Contains( "did not finish", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// The busy-box rule on the real machine with a synthetic outside load (limit 2 CPUs above
   /// which the shared box's background stays, windows 10 s and 5 s, deadline 20 s, so it runs
   /// in about a minute and a half): a warm-up announced while 4
   /// busy loops run is held until they end and the box is quiet, and only then does the pass
   /// open (the check is at the warm-up line); a load that outlasts the deadline holds the
   /// warm-up for the deadline and the pass is flagged busy. The governor is back afterwards and
   /// no state file is left.
   /// </summary>
   [Fact]
   public async Task BusyBox_HoldsTheWarmupOnTheRealMachine()
   {
      dynamic r = await MachineControlCompiler.CallAsync( "LiveBusyBoxAsync" );
      _output.WriteLine( $"background {r.Background} CPUs, limit {r.Limit} CPUs" );
      ( (string[])r.Passes ).ToList().ForEach( _output.WriteLine );
      ( (string[])r.Log ).ToList().ForEach( _output.WriteLine );
      string[] passes = r.Passes;
      Assert.Equal( 2, passes.Length );
      Assert.Contains( "before=warm-up", passes[0] );
      Assert.Contains( "busy=False", passes[0] );
      Assert.DoesNotContain( "waited=0.0 ", passes[0] );
      Assert.Contains( "before=warm-up", passes[1] );
      Assert.Contains( "busy=True", passes[1] );
      Assert.Contains( (string[])r.Log, l => l.Contains( "box busy before live default@1 (warm-up)", StringComparison.Ordinal ) );
      Assert.Contains( (string[])r.Log, l => l.Contains( "box quiet after", StringComparison.Ordinal ) );
      Assert.Contains( (string[])r.Log, l => l.Contains( "BUSY BOX", StringComparison.Ordinal ) );
      Assert.Equal( (string[])r.GovernorsBefore, (string[])r.GovernorsAfter );
      Assert.False( (bool)r.StateFileLeft );
   }

   /// <summary>
   /// The outside-load figure tracks a synthetic outside load during an 8-searcher pass, on the
   /// real box with the real sampler: a loopback echo engine in its own cgroup, 8 searcher threads
   /// in this process, and a CPU-quota-held busy loop (0.2 CPU, then 0.5 CPU, six times each, in its
   /// own cgroup so its CPU is exact) between quiet phases. Two checks, both over exactly the span of
   /// sampler intervals the figure covers. (1) Against the stress's own CPU: each stress phase's
   /// figure, less the mean of the quiet phases either side, less the stress's CPU, is that phase's
   /// error; the median error over the six phases of each level is within 0.05 CPUs. (The box's
   /// background drifts by hundredths of a CPU between phases and bursts by whole CPUs now and then,
   /// which is why the median of six is judged, not one phase.) (2) Against an observer that never
   /// reads /proc/stat (the top-level cgroups' CPU, less this process and the engine): the figure
   /// less the observer's, in each stress phase against the mean of its neighbours, is within 0.05
   /// CPUs in every phase; the background cancels out of this one, so it holds on a busy box. Takes
   /// about five minutes; changes nothing on the machine; every scope has a timeout and is stopped
   /// at the end.
   /// </summary>
   [Fact]
   public async Task OutsideLoad_TracksASyntheticLoadDuringAnEightSearcherPass()
   {
      string folder = Path.Combine( AppContext.BaseDirectory, "machine-control-live", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( folder );
      try
      {
         dynamic r = await MachineControlCompiler.CallAsync( TimeSpan.FromMinutes( 8 ), "LiveOutsideLoadAsync", folder );
         _output.WriteLine( "phase        tool   oldFormula  cgroupObserver  stressTruth  client  engine  QPS    clientMs/search  window(s) intervals" );
         dynamic[] phases = ( (IEnumerable<dynamic>)r.Phases ).ToArray();
         foreach( dynamic p in phases )
         {
            _output.WriteLine( string.Create( System.Globalization.CultureInfo.InvariantCulture,
               $"{(string)p.Name,-12} {(double)p.Tool,6:0.000} {(double)p.OldFormula,10:0.000} {(double)p.Observer,15:0.000} {(double)p.Truth,12:0.000} {(double)p.OwnCpu,7:0.000} {(double)p.EngineCpu,7:0.000} {(double)p.Qps,6:0} {(double)p.ClientMsPerSearch,15:0.0000} {(double)p.Seconds,9:0.0} {(int)p.Intervals,9}" ) );
         }

         _output.WriteLine( string.Create( System.Globalization.CultureInfo.InvariantCulture, $"sampler alone, no searches: client {(double)r.IdleClientCpu:0.0000} CPUs (includes this test's own 20 Hz ledger)" ) );
         _output.WriteLine( "accounting: " + (string)r.Accounting );
         Assert.Null( (string?)r.SamplingError );
         var errors = new Dictionary<double, List<double>> { [0.2] = new(), [0.5] = new() };
         for( int i = 1; i < phases.Length - 1; i++ )
         {
            if( phases[i].Quota == null )
            {
               continue;
            }

            double truth = phases[i].Truth;
            double before = phases[i - 1].Tool;
            double after = phases[i + 1].Tool;
            double tracked = (double)phases[i].Tool - ( before + after ) / 2 - truth;
            double relative = ( (double)phases[i].Tool - (double)phases[i].Observer ) - ( ( before - (double)phases[i - 1].Observer ) + ( after - (double)phases[i + 1].Observer ) ) / 2;
            _output.WriteLine( string.Create( System.Globalization.CultureInfo.InvariantCulture, $"{(string)phases[i].Name}: stress {truth:0.000} CPUs; error against the stress's CPU {tracked:+0.000;-0.000}; against the cgroup observer {relative:+0.000;-0.000}" ) );
            Assert.InRange( relative, -0.05, 0.05 );
            errors[(double)phases[i].Quota!].Add( tracked );
         }

         foreach( ( double level, List<double> list ) in errors )
         {
            double median = list.OrderBy( e => e ).ElementAt( list.Count / 2 );
            _output.WriteLine( string.Create( System.Globalization.CultureInfo.InvariantCulture, $"stress {level:0.0} CPUs: median error {median:+0.000;-0.000} over {list.Count} phases (mean {list.Average():+0.000;-0.000}, worst {list.OrderByDescending( Math.Abs ).First():+0.000;-0.000})" ) );
            Assert.InRange( median, -0.05, 0.05 );
         }
      }
      finally
      {
         Directory.Delete( folder, true );
      }
   }

   /// <summary>
   /// The real host's answer about a start on the engine CPUs matches Docker: when it returns a
   /// read-back line, the throwaway container has cpuset 2-3,6-7, every thread of its main process
   /// is allowed only those CPUs and the process inside sees 4 CPUs (so an engine sizes its pools
   /// for them); when it returns nothing (a compose runner that cannot create containers on a CPU
   /// set), the container has no cpuset, so machine control must move it and say so. Either way
   /// it is removed afterwards.
   /// </summary>
   [Fact]
   public async Task EngineHost_ReadBackMatchesDocker()
   {
      string folder = Path.Combine( AppContext.BaseDirectory, "machine-control-live", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( folder );
      try
      {
         string[] r = await MachineControlCompiler.CallAsync( "LivePinnedStartAsync", folder );
         _output.WriteLine( "host said: " + r[0] );
         _output.WriteLine( "cpuset: '" + r[1] + "'" );
         _output.WriteLine( "main process threads: " + r[2] );
         _output.WriteLine( "nproc inside: " + r[3] );
         _output.WriteLine( "left after down: " + r[4] );
         if( r[0] == "none" )
         {
            Assert.Equal( string.Empty, r[1] );
            Assert.DoesNotMatch( "^2-3,6-7 x[0-9]+$", r[2] );
         }
         else
         {
            Assert.Contains( "2-3,6-7", r[0] );
            Assert.Equal( "2-3,6-7", r[1] );
            Assert.Matches( "^2-3,6-7 x[0-9]+$", r[2] );
            Assert.Equal( "4", r[3] );
         }

         Assert.Equal( "none", r[4] );
      }
      finally
      {
         Directory.Delete( folder, true );
      }
   }

   #endregion Public Methods
}
