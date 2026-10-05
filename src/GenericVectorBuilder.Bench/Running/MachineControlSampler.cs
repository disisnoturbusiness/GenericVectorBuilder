using System.Globalization;

namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// Every CPU's clock at one moment.
/// </summary>
/// <param name="At">When (UTC).</param>
/// <param name="Mhz">MHz per CPU, in the sampler's CPU order; null where unreadable.</param>
public sealed record FrequencySample( DateTime At, int?[] Mhz );

/// <summary>
/// CPU time used outside the benchmark during one interval.
/// </summary>
/// <param name="At">End of the interval (UTC).</param>
/// <param name="Seconds">Wall seconds the interval lasted.</param>
/// <param name="OutsideCpuSeconds">CPU seconds used by processes outside the benchmark in it.</param>
/// <param name="Start">Start of the interval (UTC).</param>
public sealed record OutsideSample( DateTime At, double Seconds, double OutsideCpuSeconds, DateTime Start );

/// <summary>
/// Cumulative CPU counters at one moment, in seconds.
/// </summary>
/// <param name="At">When.</param>
/// <param name="Busy">All CPUs' user + nice + system time.</param>
/// <param name="Self">This process's user + system time, with its finished children.</param>
/// <param name="Groups">Each engine cgroup's usage.</param>
internal sealed record CpuReading( DateTime At, double Busy, double Self, Dictionary<string, double> Groups );

/// <summary>
/// One CPU's clock over a pass.
/// </summary>
/// <param name="Cpu">Logical CPU.</param>
/// <param name="Min">Lowest MHz seen.</param>
/// <param name="Median">Median MHz.</param>
/// <param name="Max">Highest MHz seen.</param>
public sealed record CpuMhz( int Cpu, int Min, int Median, int Max );

/// <summary>
/// Samples, in the background, every CPU's clock every 250 ms (scaling_cur_freq) and, every
/// fourth sample, how much CPU time processes outside the benchmark used.
/// Why the clock: it is the proof that the governor held, per pass; the review saw a single
/// searcher run at 2.1 to 2.5 GHz while eight ran at 3.49 GHz, and nothing in the old results
/// showed it.
/// Why outside CPU time instead of the load average: the load average counts the benchmark's
/// own client and engine, which are busy by design during a run. Outside time is all CPU time
/// the kernel counted (/proc/stat user + nice + system) minus this process (with its finished
/// children), minus the cgroups of the engine under test. Kernel interrupt time is left out on
/// both sides, because the benchmark's own network traffic causes most of it.
/// Sampling never stops the run: a failed read is remembered in <see cref="LastError"/>.
/// </summary>
public sealed class MachineSampler : IDisposable
{
   #region Data Members

   private const int LOAD_EVERY = 4;
   private const double MIN_HISTORY_SECONDS = 3;
   private static readonly TimeSpan KEEP_LOAD = TimeSpan.FromMinutes( 10 );
   private static readonly TimeSpan STOP_DEADLINE = TimeSpan.FromSeconds( 5 );

   private readonly IMachineSystem _system;
   private readonly IReadOnlyList<int> _cpus;
   private readonly double _clockTicks;
   private readonly TimeSpan _interval;
   private readonly object _lock = new();
   private readonly List<FrequencySample> _frequencies = new();
   private readonly List<OutsideSample> _outside = new();
   private List<string> _groups = new();
   private CpuReading? _last;
   private int _ticks;
   private CancellationTokenSource? _stop;
   private Task? _loop;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the sampler; <see cref="Start"/> begins sampling.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="cpus">CPUs whose clock is sampled.</param>
   /// <param name="clockTicksPerSecond">USER_HZ, the unit of /proc/stat and /proc/PID/stat (getconf CLK_TCK; 100 on Linux).</param>
   /// <param name="interval">Time between samples (250 ms in a run).</param>
   public MachineSampler( IMachineSystem system, IReadOnlyList<int> cpus, double clockTicksPerSecond, TimeSpan interval )
   {
      _system = system;
      _cpus = cpus;
      _clockTicks = clockTicksPerSecond;
      _interval = interval;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>CPUs in the order of <see cref="FrequencySample.Mhz"/>.</summary>
   public IReadOnlyList<int> Cpus => _cpus;

   /// <summary>The first sampling failure, or null when every sample was read.</summary>
   public string? LastError { get; private set; }

   /// <summary>
   /// Starts sampling on a background task.
   /// </summary>
   public void Start()
   {
      _stop = new CancellationTokenSource();
      _loop = Task.Run( () => LoopAsync( _stop.Token ) );
   }

   /// <summary>
   /// Stops sampling and waits up to 5 s for the loop to end.
   /// </summary>
   public void Stop()
   {
      _stop?.Cancel();
      try
      {
         _loop?.Wait( STOP_DEADLINE );
      }
      catch( AggregateException )
      {
         // The loop only ends by cancellation; nothing to report.
      }
   }

   /// <summary>
   /// Sets the cgroup folders of the engine under test; their CPU time is benchmark time.
   /// </summary>
   /// <param name="groups">cgroup folders (empty for an embedded engine or between targets).</param>
   public void SetEngineGroups( IEnumerable<string> groups )
   {
      lock( _lock )
      {
         _groups = groups.ToList();
      }
   }

   /// <summary>
   /// Takes one sample now. Public so tests can drive the sampler without a clock.
   /// </summary>
   /// <param name="now">The sample time (UTC).</param>
   public void Tick( DateTime now )
   {
      try
      {
         int?[] mhz = _cpus.Select( ReadMhz ).ToArray();
         bool load = _ticks++ % LOAD_EVERY == 0;
         List<string> groups;
         lock( _lock )
         {
            groups = _groups;
         }

         CpuReading? reading = load ? ReadCpu( now, groups ) : null;
         lock( _lock )
         {
            _frequencies.Add( new FrequencySample( now, mhz ) );
            if( reading != null )
            {
               AddLoad( reading );
            }
         }
      }
      catch( Exception ex ) when( ex is IOException or FormatException or IndexOutOfRangeException or OverflowException )
      {
         LastError ??= $"{now:HH:mm:ss}: {ex.Message}";
      }
   }

   /// <summary>
   /// Mean CPUs busy outside the benchmark over the trailing window: 1.5 means one and a half
   /// CPUs' worth of other work.
   /// </summary>
   /// <param name="now">End of the window.</param>
   /// <param name="window">Window length (one minute for the busy-box rule).</param>
   /// <returns>The mean, or null with less than 3 s (or half the window, if shorter) of history.</returns>
   public double? OutsideLoad( DateTime now, TimeSpan window )
   {
      return OutsideLoadBetween( now - window, now );
   }

   /// <summary>
   /// Mean CPUs busy outside the benchmark between two times, over the sampled intervals that lie
   /// wholly inside. Why wholly: an interval that began before the window (before a target or
   /// its engine started, say) would bring work from before the window into it.
   /// </summary>
   /// <param name="from">Start (UTC).</param>
   /// <param name="to">End (UTC).</param>
   /// <returns>The mean, or null with less than 3 s (or half the window, if shorter) of history in it.</returns>
   public double? OutsideLoadBetween( DateTime from, DateTime to )
   {
      lock( _lock )
      {
         List<OutsideSample> inside = _outside.Where( s => s.Start >= from && s.At <= to ).ToList();
         double seconds = inside.Sum( s => s.Seconds );
         double needed = Math.Min( MIN_HISTORY_SECONDS, ( to - from ).TotalSeconds / 2 );
         return seconds <= 0 || seconds < needed ? null : inside.Sum( s => s.OutsideCpuSeconds ) / seconds;
      }
   }

   /// <summary>
   /// Each CPU's lowest, median and highest clock between two times. A window shorter than
   /// one sample interval (a 20-query exact pass) gets the sample nearest its middle.
   /// </summary>
   /// <param name="from">Start (UTC).</param>
   /// <param name="to">End (UTC).</param>
   /// <returns>Per CPU figures, the number of samples, and whether the nearest sample stood in.</returns>
   public (List<CpuMhz> Cpus, int Samples, bool Nearest) Frequencies( DateTime from, DateTime to )
   {
      List<FrequencySample> inside;
      bool nearest = false;
      lock( _lock )
      {
         inside = _frequencies.Where( s => s.At >= from && s.At <= to ).ToList();
         if( inside.Count == 0 && _frequencies.Count > 0 )
         {
            DateTime middle = from + ( to - from ) / 2;
            inside.Add( _frequencies.MinBy( s => Math.Abs( ( s.At - middle ).Ticks ) )! );
            nearest = true;
         }
      }

      var cpus = new List<CpuMhz>();
      for( int i = 0; i < _cpus.Count; i++ )
      {
         List<int> values = inside.Select( s => s.Mhz[i] ).OfType<int>().OrderBy( v => v ).ToList();
         if( values.Count > 0 )
         {
            cpus.Add( new CpuMhz( _cpus[i], values[0], Median( values ), values[^1] ) );
         }
      }

      return ( cpus, nearest ? 0 : inside.Count, nearest );
   }

   /// <summary>
   /// The median of sorted whole numbers (the mean of the middle two, rounded, for an even count).
   /// </summary>
   /// <param name="sorted">Ascending values, at least one.</param>
   /// <returns>The median.</returns>
   public static int Median( IReadOnlyList<int> sorted )
   {
      int middle = sorted.Count / 2;
      return sorted.Count % 2 == 1 ? sorted[middle] : (int)Math.Round( ( sorted[middle - 1] + sorted[middle] ) / 2.0, MidpointRounding.AwayFromZero );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Samples until cancelled.
   /// </summary>
   /// <param name="ct">Stops the loop.</param>
   private async Task LoopAsync( CancellationToken ct )
   {
      using var timer = new PeriodicTimer( _interval );
      try
      {
         do
         {
            Tick( DateTime.UtcNow );
         }
         while( await timer.WaitForNextTickAsync( ct ) );
      }
      catch( OperationCanceledException )
      {
         // Stop() was called.
      }
   }

   /// <summary>
   /// One CPU's clock in MHz.
   /// </summary>
   /// <param name="cpu">Logical CPU.</param>
   /// <returns>MHz, or null when unreadable.</returns>
   private int? ReadMhz( int cpu )
   {
      string? khz = _system.ReadFile( GovernorControl.FrequencyPath( cpu ) )?.Trim();
      return long.TryParse( khz, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value ) ? (int)Math.Round( value / 1000.0 ) : null;
   }

   /// <summary>
   /// Reads the cumulative CPU counters: the whole machine, this process with its finished
   /// children, and each engine cgroup.
   /// </summary>
   /// <param name="now">Reading time.</param>
   /// <param name="groups">Engine cgroup folders.</param>
   /// <returns>The reading.</returns>
   private CpuReading ReadCpu( DateTime now, List<string> groups )
   {
      string[] total = ( _system.ReadFile( "/proc/stat" ) ?? throw new IOException( "cannot read /proc/stat" ) ).Split( '\n' )[0].Split( ' ', StringSplitOptions.RemoveEmptyEntries );
      double busy = ( Ticks( total[1] ) + Ticks( total[2] ) + Ticks( total[3] ) ) / _clockTicks;
      string[] self = ProcessInfo.StatFields( _system, _system.ProcessId ) ?? throw new IOException( "cannot read this process's /proc stat" );
      double own = ( Ticks( self[11] ) + Ticks( self[12] ) + Ticks( self[13] ) + Ticks( self[14] ) ) / _clockTicks;
      var usage = new Dictionary<string, double>( StringComparer.Ordinal );
      foreach( string group in groups )
      {
         string? line = _system.ReadFile( group + "/cpu.stat" )?.Split( '\n' ).FirstOrDefault( l => l.StartsWith( "usage_usec ", StringComparison.Ordinal ) );
         if( line != null )
         {
            usage[group] = Ticks( line["usage_usec ".Length..] ) / 1_000_000.0;
         }
      }

      return new CpuReading( now, busy, own, usage );
   }

   /// <summary>
   /// Turns a reading into the outside CPU time since the previous one. An interval in which the
   /// engine's groups changed (a target began, its engine finished starting, or it ended) is
   /// left out: the engine's CPU time in it cannot be told apart, and counting it as outside work
   /// would hold the next pass for the engine's own start-up.
   /// </summary>
   /// <param name="reading">The new reading.</param>
   private void AddLoad( CpuReading reading )
   {
      if( _last != null && reading.At > _last.At && reading.Groups.Keys.ToHashSet( StringComparer.Ordinal ).SetEquals( _last.Groups.Keys ) )
      {
         double engine = reading.Groups.Where( g => _last.Groups.ContainsKey( g.Key ) ).Sum( g => g.Value - _last.Groups[g.Key] );
         double outside = ( reading.Busy - _last.Busy ) - ( reading.Self - _last.Self ) - engine;
         _outside.Add( new OutsideSample( reading.At, ( reading.At - _last.At ).TotalSeconds, Math.Max( 0, outside ), _last.At ) );
         _outside.RemoveAll( s => s.At < reading.At - KEEP_LOAD );
      }

      _last = reading;
   }

   /// <summary>
   /// Parses a counter.
   /// </summary>
   /// <param name="text">Digits.</param>
   /// <returns>The value.</returns>
   private static double Ticks( string text )
   {
      return long.Parse( text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture );
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Stops sampling.
   /// </summary>
   public void Dispose()
   {
      Stop();
      _stop?.Dispose();
   }

   #endregion IDisposable
}
