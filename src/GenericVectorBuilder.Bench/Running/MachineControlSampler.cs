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
/// <param name="OutsideCpuSeconds">CPU seconds used by processes outside the benchmark in it. Signed: a counter rounding can make one interval slightly negative, and clipping those to zero would bias every mean upward and hide a real accounting mismatch.</param>
/// <param name="Start">Start of the interval (UTC).</param>
public sealed record OutsideSample( DateTime At, double Seconds, double OutsideCpuSeconds, DateTime Start );

/// <summary>
/// The outside load over the sampled intervals that lie wholly inside a window.
/// </summary>
/// <param name="Load">Mean CPUs busy outside the benchmark; negative when the counters did not add up.</param>
/// <param name="From">Start of the first interval used (UTC); at or after the window's start.</param>
/// <param name="To">End of the last interval used (UTC); at or before the window's end.</param>
/// <param name="Seconds">Wall seconds of the intervals used.</param>
/// <param name="Intervals">Number of intervals used.</param>
public sealed record OutsideWindow( double Load, DateTime From, DateTime To, double Seconds, int Intervals );

/// <summary>
/// This process's own CPU time over a window.
/// </summary>
/// <param name="Seconds">CPU seconds (user + system of this process's threads, not its finished children), or null when the window cannot be measured.</param>
/// <param name="Samples">Sampled intervals the window spans.</param>
/// <param name="Problem">Why it cannot be measured, or null.</param>
public sealed record ClientCpu( double? Seconds, int Samples, string? Problem );

/// <summary>
/// This process's cumulative CPU time at one moment.
/// </summary>
/// <param name="At">When (UTC).</param>
/// <param name="Seconds">User + system seconds of this process's threads since it started.</param>
internal readonly record struct ClientCpuSample( DateTime At, double Seconds );

/// <summary>
/// Cumulative CPU counters at one moment, in seconds.
/// </summary>
/// <param name="At">When.</param>
/// <param name="Busy">All CPUs' busy time under the kernel's <see cref="CpuAccounting"/>: user + nice + system, plus irq, softirq and steal where the cgroup totals include them.</param>
/// <param name="Self">This process's user + system time, with its finished children.</param>
/// <param name="Client">This process's user + system time, without its children.</param>
/// <param name="Groups">Each engine cgroup's usage.</param>
internal sealed record CpuReading( DateTime At, double Busy, double Self, double Client, Dictionary<string, double> Groups );

/// <summary>
/// Which /proc/stat columns make up "busy" so that it is counted the way the totals that get
/// subtracted from it are. The subtracted totals are this process's utime + stime and the
/// engine cgroups' usage_usec; both are the tasks' scheduler run time, which includes the
/// hardirq and softirq time that hit them unless the kernel was built with
/// CONFIG_IRQ_TIME_ACCOUNTING (then the run time leaves it out and /proc/stat keeps it in the
/// irq and softirq columns). Likewise steal with CONFIG_PARAVIRT_TIME_ACCOUNTING.
/// Why it matters: on linus7795 (6.8.0-142-generic, CONFIG_IRQ_TIME_ACCOUNTING not set) a loopback
/// transfer between two processes in one cgroup showed usage_usec equal to /proc/stat's
/// user + nice + system + softirq to within 0.01 s over 12 s, and 2.0 s above user + nice +
/// system alone; counting only user + nice + system left the benchmark's own network softirq
/// time in the subtracted totals and out of the total, which pushed the outside figure down by
/// up to 0.37 CPU during 8-searcher passes. Guest and guest_nice are never added: the kernel
/// already counts them in user and nice.
/// </summary>
/// <param name="CountIrq">True to add the irq and softirq columns to busy.</param>
/// <param name="CountSteal">True to add the steal column to busy.</param>
/// <param name="Description">What was decided and why, for the results.</param>
public sealed record CpuAccounting( bool CountIrq, bool CountSteal, string Description )
{
   #region Public Methods

   /// <summary>
   /// Reads the running kernel's build options and decides the columns. A kernel whose options
   /// cannot be read is taken to count interrupt and steal time in run time (the default Linux
   /// build), and the description says it was assumed.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <returns>The accounting.</returns>
   public static CpuAccounting Detect( IMachineSystem system )
   {
      string? release = system.ReadFile( "/proc/sys/kernel/osrelease" )?.Trim();
      string? config = string.IsNullOrEmpty( release ) ? null : system.ReadFile( $"/boot/config-{release}" );
      if( config == null )
      {
         return new CpuAccounting( true, true, $"busy = user + nice + system + irq + softirq + steal (guest time is already in user); kernel build options not readable ({( string.IsNullOrEmpty( release ) ? "no kernel release" : $"/boot/config-{release}" )}), so the default Linux build was ASSUMED: task and cgroup run time include interrupt, softirq and steal time" );
      }

      bool irqTime = HasOption( config, "CONFIG_IRQ_TIME_ACCOUNTING" );
      bool stealTime = HasOption( config, "CONFIG_PARAVIRT_TIME_ACCOUNTING" );
      string columns = "user + nice + system" + ( irqTime ? string.Empty : " + irq + softirq" ) + ( stealTime ? string.Empty : " + steal" );
      string why = $"{( irqTime ? "CONFIG_IRQ_TIME_ACCOUNTING=y, so task and cgroup run time leave interrupt time out and it is not added" : "CONFIG_IRQ_TIME_ACCOUNTING is not set, so task and cgroup run time include the interrupt and softirq time that hit them and it is added" )}; "
         + $"{( stealTime ? "CONFIG_PARAVIRT_TIME_ACCOUNTING=y, so steal is not added" : "CONFIG_PARAVIRT_TIME_ACCOUNTING is not set, so steal is added" )}";
      return new CpuAccounting( !irqTime, !stealTime, $"busy = {columns} (guest time is already in user); {why} (/boot/config-{release})" );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// True when a kernel config file has an option set to y.
   /// </summary>
   /// <param name="config">The config text.</param>
   /// <param name="name">Option name.</param>
   /// <returns>True when "NAME=y" is a line of it.</returns>
   private static bool HasOption( string config, string name )
   {
      return config.Split( '\n' ).Any( l => l.Trim().Equals( name + "=y", StringComparison.Ordinal ) );
   }

   #endregion Private Methods
}

/// <summary>
/// One CPU's clock over a pass.
/// </summary>
/// <param name="Cpu">Logical CPU.</param>
/// <param name="Min">Lowest MHz seen.</param>
/// <param name="Median">Median MHz.</param>
/// <param name="Max">Highest MHz seen.</param>
public sealed record CpuMhz( int Cpu, int Min, int Median, int Max );

/// <summary>
/// Samples, in the background, every 250 ms: every CPU's clock (scaling_cur_freq) and how much
/// CPU time processes outside the benchmark used, and this process's own CPU time.
/// Why the clock: it is the proof that the governor held, per pass; the review saw a single
/// searcher run at 2.1 to 2.5 GHz while eight ran at 3.49 GHz, and nothing in the old results
/// showed it.
/// Why outside CPU time instead of the load average: the load average counts the benchmark's
/// own client and engine, which are busy by design during a run. Outside time is all CPU time
/// the kernel counted (/proc/stat, the columns <see cref="CpuAccounting"/> picks: user + nice +
/// system and, on this kernel, irq + softirq + steal) minus this process (with its finished
/// children), minus the cgroups of the engine under test. The columns are the ones the
/// subtracted totals contain, so the benchmark's own network softirq time is on both sides and
/// cancels. A negative result is kept as it is: it means the counters did not add up, and
/// clipping it to zero would hide that and bias every mean upward.
/// Why this process's own CPU time is kept for the whole run: the client's CPU per search is
/// part of every latency (for the fastest engines about as large as the latency itself), so each
/// pass's figure is the ledger's CPU time over the pass's own window divided by its searches.
/// Sampling never stops the run: a failed read is remembered in <see cref="LastError"/>.
/// </summary>
public sealed class MachineSampler : IDisposable
{
   #region Data Members

   private const double MIN_HISTORY_SECONDS = 3;
   private const int MIN_CLIENT_CPU_SAMPLES = 4;
   private static readonly TimeSpan KEEP_LOAD = TimeSpan.FromMinutes( 10 );
   private static readonly TimeSpan STOP_DEADLINE = TimeSpan.FromSeconds( 5 );

   private readonly IMachineSystem _system;
   private readonly IReadOnlyList<int> _cpus;
   private readonly double _clockTicks;
   private readonly TimeSpan _interval;
   private readonly object _lock = new();
   private readonly List<FrequencySample> _frequencies = new();
   private readonly List<OutsideSample> _outside = new();
   private readonly List<ClientCpuSample> _client = new();
   private List<string> _groups = new();
   private CpuReading? _last;
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
      Accounting = CpuAccounting.Detect( system );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>CPUs in the order of <see cref="FrequencySample.Mhz"/>.</summary>
   public IReadOnlyList<int> Cpus => _cpus;

   /// <summary>The first sampling failure, or null when every sample was read.</summary>
   public string? LastError { get; private set; }

   /// <summary>Which /proc/stat columns count as busy, and why; written into the results.</summary>
   public CpuAccounting Accounting { get; }

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
   /// Takes one sample now: every CPU's clock, the outside CPU time since the last sample, and
   /// this process's CPU time. Public so tests can drive the sampler without a clock.
   /// </summary>
   /// <param name="now">The sample time (UTC).</param>
   public void Tick( DateTime now )
   {
      try
      {
         int?[] mhz = _cpus.Select( ReadMhz ).ToArray();
         List<string> groups;
         lock( _lock )
         {
            groups = _groups;
         }

         CpuReading reading = ReadCpu( now, groups );
         lock( _lock )
         {
            _frequencies.Add( new FrequencySample( now, mhz ) );
            AddLoad( reading );
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
   /// <returns>The mean (negative when the counters did not add up, never clipped), or null with less than 3 s (or half the window, if shorter) of history in it.</returns>
   public double? OutsideLoadBetween( DateTime from, DateTime to )
   {
      return OutsideWindowBetween( from, to )?.Load;
   }

   /// <summary>
   /// The outside load between two times with the span of intervals it really covers.
   /// </summary>
   /// <param name="from">Start (UTC).</param>
   /// <param name="to">End (UTC).</param>
   /// <returns>The window, or null with less than 3 s (or half the window, if shorter) of history in it.</returns>
   public OutsideWindow? OutsideWindowBetween( DateTime from, DateTime to )
   {
      lock( _lock )
      {
         List<OutsideSample> inside = _outside.Where( s => s.Start >= from && s.At <= to ).ToList();
         double seconds = inside.Sum( s => s.Seconds );
         double needed = Math.Min( MIN_HISTORY_SECONDS, ( to - from ).TotalSeconds / 2 );
         return seconds <= 0 || seconds < needed
            ? null
            : new OutsideWindow( inside.Sum( s => s.OutsideCpuSeconds ) / seconds, inside.Min( s => s.Start ), inside.Max( s => s.At ), seconds, inside.Count );
      }
   }

   /// <summary>
   /// This process's own CPU time between two times, by linear interpolation between the two
   /// samples either side of each end. Why interpolation is enough: a pass lasts tens of seconds
   /// and the client's CPU rate is steady inside it, so an end that falls between two 250 ms
   /// samples is off by well under 1% of the pass.
   /// </summary>
   /// <param name="from">Start (UTC).</param>
   /// <param name="to">End (UTC).</param>
   /// <returns>The CPU seconds, or the reason it cannot be measured (a window shorter than four samples, or outside what was sampled).</returns>
   public ClientCpu ClientCpuBetween( DateTime from, DateTime to )
   {
      lock( _lock )
      {
         if( _client.Count < 2 || from < _client[0].At || to > _client[^1].At )
         {
            return new ClientCpu( null, 0, _client.Count < 2 ? "this process's CPU time was not sampled" : $"the window {from:HH:mm:ss.fff} to {to:HH:mm:ss.fff} lies outside the sampled span {_client[0].At:HH:mm:ss.fff} to {_client[^1].At:HH:mm:ss.fff}" );
         }

         int samples = _client.Count( c => c.At >= from && c.At <= to );
         if( to - from < _interval * MIN_CLIENT_CPU_SAMPLES )
         {
            return new ClientCpu( null, samples, $"the window of {( to - from ).TotalSeconds:0.###} s is shorter than {MIN_CLIENT_CPU_SAMPLES} samples of {_interval.TotalMilliseconds:0} ms, too short for CPU time read in 10 ms ticks" );
         }

         return new ClientCpu( ClientAt( to ) - ClientAt( from ), samples, null );
      }
   }

   /// <summary>
   /// Fills each pass's client CPU time and CPU per search. The searches and the exact window of
   /// each pass come from the target's "Pass NAME after ...: START to END (...), N searches" notes
   /// (see <see cref="PassNoteReader"/>), because only the search runner knows how many searches
   /// the window held; a pass with no such note, no searches, or a window that cannot be measured
   /// says why in <see cref="PassConditions.ClientCpuProblem"/>, never a silent blank.
   /// </summary>
   /// <param name="passes">One target's recorded passes.</param>
   /// <param name="notes">That target's notes.</param>
   /// <param name="now">The time of this call, so the samples reach the end of the last pass.</param>
   public void AttachClientCpu( IEnumerable<PassConditions> passes, IEnumerable<string> notes, DateTime now )
   {
      Tick( now );
      List<PassNote> found = PassNoteReader.Read( notes );
      foreach( PassConditions pass in passes )
      {
         DateTime start = DateTime.TryParse( pass.StartUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed ) ? parsed : DateTime.MinValue;
         PassNote? note = found.Where( n => n.Pass == pass.Pass ).OrderBy( n => Math.Abs( ( n.Start - start ).Ticks ) ).FirstOrDefault();
         if( note == null )
         {
            pass.ApplyClientCpu( null, null, null, $"no 'Pass {pass.Pass} after ...' note with a search count was found for this pass" );
            continue;
         }

         ClientCpu cpu = ClientCpuBetween( note.Start, note.End );
         pass.ApplyClientCpu( cpu.Seconds, note.Searches, cpu.Problem == null ? PassNoteReader.Basis( note, cpu ) : null, cpu.Problem ?? ( note.Searches == 0 ? "no search completed in the pass" : null ) );
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
      string[] self = ProcessInfo.StatFields( _system, _system.ProcessId ) ?? throw new IOException( "cannot read this process's /proc stat" );
      double client = ( Ticks( self[11] ) + Ticks( self[12] ) ) / _clockTicks;
      double own = client + ( Ticks( self[13] ) + Ticks( self[14] ) ) / _clockTicks;
      var usage = new Dictionary<string, double>( StringComparer.Ordinal );
      foreach( string group in groups )
      {
         string? line = _system.ReadFile( group + "/cpu.stat" )?.Split( '\n' ).FirstOrDefault( l => l.StartsWith( "usage_usec ", StringComparison.Ordinal ) );
         if( line != null )
         {
            usage[group] = Ticks( line["usage_usec ".Length..] ) / 1_000_000.0;
         }
      }

      return new CpuReading( now, Busy( total ), own, client, usage );
   }

   /// <summary>
   /// All CPUs' busy time from the /proc/stat total line, in the columns <see cref="Accounting"/>
   /// picks. Columns an old kernel does not have count as zero.
   /// </summary>
   /// <param name="total">The line's words: "cpu", user, nice, system, idle, iowait, irq, softirq, steal, guest, guest_nice.</param>
   /// <returns>Busy seconds.</returns>
   private double Busy( string[] total )
   {
      double Column( int index ) => index < total.Length ? Ticks( total[index] ) : 0;
      double busy = Column( 1 ) + Column( 2 ) + Column( 3 );
      busy += Accounting.CountIrq ? Column( 6 ) + Column( 7 ) : 0;
      busy += Accounting.CountSteal ? Column( 8 ) : 0;
      return busy / _clockTicks;
   }

   /// <summary>
   /// Turns a reading into the outside CPU time since the previous one, and files this
   /// process's CPU time in the ledger. An interval in which the engine's groups changed (a
   /// target began, its engine finished starting, or it ended) is left out: the engine's CPU time
   /// in it cannot be told apart, and counting it as outside work would hold the next pass for the
   /// engine's own start-up. The interval's value is kept signed.
   /// </summary>
   /// <param name="reading">The new reading.</param>
   private void AddLoad( CpuReading reading )
   {
      if( _last != null && reading.At <= _last.At )
      {
         return;
      }

      if( _last != null && reading.Groups.Keys.ToHashSet( StringComparer.Ordinal ).SetEquals( _last.Groups.Keys ) )
      {
         double engine = reading.Groups.Where( g => _last.Groups.ContainsKey( g.Key ) ).Sum( g => g.Value - _last.Groups[g.Key] );
         double outside = ( reading.Busy - _last.Busy ) - ( reading.Self - _last.Self ) - engine;
         _outside.Add( new OutsideSample( reading.At, ( reading.At - _last.At ).TotalSeconds, outside, _last.At ) );
         _outside.RemoveAll( s => s.At < reading.At - KEEP_LOAD );
      }

      _client.Add( new ClientCpuSample( reading.At, reading.Client ) );
      _last = reading;
   }

   /// <summary>
   /// This process's CPU seconds at a moment between the first and last ledger samples.
   /// </summary>
   /// <param name="at">The moment.</param>
   /// <returns>Seconds, interpolated between the samples either side.</returns>
   private double ClientAt( DateTime at )
   {
      int low = 0;
      int high = _client.Count - 1;
      while( high - low > 1 )
      {
         int middle = ( low + high ) / 2;
         if( _client[middle].At <= at )
         {
            low = middle;
         }
         else
         {
            high = middle;
         }
      }

      ClientCpuSample a = _client[low];
      ClientCpuSample b = _client[high];
      double span = ( b.At - a.At ).TotalSeconds;
      return span <= 0 ? a.Seconds : a.Seconds + ( b.Seconds - a.Seconds ) * ( at - a.At ).TotalSeconds / span;
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

/// <summary>
/// One timed pass as the target's notes describe it.
/// </summary>
/// <param name="Pass">Pass name, e.g. "default@8".</param>
/// <param name="Start">Start of the pass's timed window (UTC).</param>
/// <param name="End">End of the window (UTC).</param>
/// <param name="Searches">Completed timed searches in the window.</param>
public sealed record PassNote( string Pass, DateTime Start, DateTime End, int Searches );

/// <summary>
/// Reads the "Pass NAME after PREVIOUS: START to END (N s), N searches, N failed, ..." notes the
/// target runner writes for each timed pass (TargetRunner.DescribePass).
/// Why the text: the search runner's pass records reach the results only as these notes, and
/// they are the one place that pairs a pass's exact window with its search count; a test builds
/// a note with DescribePass and reads it back here, so a change to either side fails loudly.
/// </summary>
public static class PassNoteReader
{
   #region Data Members

   private static readonly System.Text.RegularExpressions.Regex PASS = new(
      @"^Pass (?<pass>\S+) after .+?: (?<start>\d{4}-\d\d-\d\dT[\d:.]+Z) to (?<end>\d{4}-\d\d-\d\dT[\d:.]+Z) \([^)]*\), (?<searches>[^ ]+(?: [^ ]+)*?) searches, \d+ failed",
      System.Text.RegularExpressions.RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The passes the notes describe, in note order.
   /// </summary>
   /// <param name="notes">A target's notes.</param>
   /// <returns>The passes; notes of other kinds are skipped.</returns>
   public static List<PassNote> Read( IEnumerable<string> notes )
   {
      var found = new List<PassNote>();
      foreach( string note in notes )
      {
         System.Text.RegularExpressions.Match m = PASS.Match( note );
         string digits = m.Success ? new string( m.Groups["searches"].Value.Where( char.IsDigit ).ToArray() ) : string.Empty;
         if( m.Success && digits.Length > 0 && int.TryParse( digits, NumberStyles.None, CultureInfo.InvariantCulture, out int searches ) )
         {
            found.Add( new PassNote( m.Groups["pass"].Value, Utc( m.Groups["start"].Value ), Utc( m.Groups["end"].Value ), searches ) );
         }
      }

      return found;
   }

   /// <summary>
   /// What a pass's client CPU figure was measured over, for the results.
   /// </summary>
   /// <param name="note">The pass's note.</param>
   /// <param name="cpu">Its CPU time.</param>
   /// <returns>The text.</returns>
   public static string Basis( PassNote note, ClientCpu cpu )
   {
      return string.Create( CultureInfo.InvariantCulture,
         $"user + system CPU time of this process's threads (not its finished children; includes the 4 Hz sampler thread and the kernel network time spent on this process's behalf) from {note.Start:yyyy-MM-ddTHH:mm:ss.fffZ} to {note.End:yyyy-MM-ddTHH:mm:ss.fffZ}, {cpu.Samples} samples, divided by the pass's {note.Searches} completed searches" );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Parses a UTC timestamp as the notes print it.
   /// </summary>
   /// <param name="text">"2026-10-05T00:45:47.020Z".</param>
   /// <returns>The time.</returns>
   private static DateTime Utc( string text )
   {
      return DateTime.Parse( text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal );
   }

   #endregion Private Methods
}
