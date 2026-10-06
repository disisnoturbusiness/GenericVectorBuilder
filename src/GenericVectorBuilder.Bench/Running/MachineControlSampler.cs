using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Bench.Targets;

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
/// Why the clock: it is the proof that the governor and the pinned clock held, per pass; the
/// review saw a single searcher run at 2.1 to 2.5 GHz while eight ran at 3.49 GHz, and nothing in
/// the old results showed it; the v6 review then saw 3.49 GHz for most engines and 3.59 GHz for
/// a few others (turbo), which only the clock record showed.
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
      ( List<FrequencySample> inside, bool nearest ) = ClockWindow( from, to );
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
   /// The average clock of a group of CPUs between two times: the mean of every reading of
   /// those CPUs in the window (a window shorter than one sample interval gets the sample nearest
   /// its middle, as <see cref="Frequencies"/> does). Why the mean as well as the medians: a pass
   /// that spent part of its time at a higher clock (turbo) moves the mean,
   /// while a median can hide it.
   /// </summary>
   /// <param name="from">Start (UTC).</param>
   /// <param name="to">End (UTC).</param>
   /// <param name="cpus">The CPUs of the group.</param>
   /// <returns>MHz, or null when none of the group's CPUs was read in the window.</returns>
   public double? MeanMhz( DateTime from, DateTime to, IEnumerable<int> cpus )
   {
      HashSet<int> wanted = cpus.ToHashSet();
      int[] columns = Enumerable.Range( 0, _cpus.Count ).Where( i => wanted.Contains( _cpus[i] ) ).ToArray();
      ( List<FrequencySample> inside, _ ) = ClockWindow( from, to );
      List<int> values = inside.SelectMany( s => columns.Select( i => s.Mhz[i] ) ).OfType<int>().ToList();
      return values.Count == 0 ? null : values.Average();
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
   /// The clock samples between two times; when none lies inside (a window shorter than one
   /// sample interval), the sample nearest the window's middle.
   /// </summary>
   /// <param name="from">Start (UTC).</param>
   /// <param name="to">End (UTC).</param>
   /// <returns>The samples, and whether the nearest sample stood in.</returns>
   private (List<FrequencySample> Inside, bool Nearest) ClockWindow( DateTime from, DateTime to )
   {
      lock( _lock )
      {
         List<FrequencySample> inside = _frequencies.Where( s => s.At >= from && s.At <= to ).ToList();
         if( inside.Count > 0 || _frequencies.Count == 0 )
         {
            return ( inside, false );
         }

         DateTime middle = from + ( to - from ) / 2;
         inside.Add( _frequencies.MinBy( s => Math.Abs( ( s.At - middle ).Ticks ) )! );
         return ( inside, true );
      }
   }

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

/// <summary>
/// The file that turns a box's turbo on and off, and the value that turns it off.
/// </summary>
/// <param name="Path">The sysfs file.</param>
/// <param name="Off">The value that turns turbo off: "1" for intel_pstate's no_turbo, "0" for cpufreq's boost.</param>
/// <param name="Name">Short name for messages, e.g. "intel_pstate/no_turbo".</param>
public sealed record TurboSwitch( string Path, string Off, string Name )
{
   #region Public Methods

   /// <summary>
   /// What a value of the switch means for turbo.
   /// </summary>
   /// <param name="value">The value read, or null.</param>
   /// <returns>"off", "on", or "unknown (...)".</returns>
   public string Turbo( string? value )
   {
      return value == Off ? "off" : value is "0" or "1" ? "on" : $"unknown ({value ?? "unreadable"})";
   }

   #endregion Public Methods
}

/// <summary>
/// The clock settings of the box at one moment: its turbo switch and every CPU's frequency
/// ceiling (scaling_max_freq) and floor (scaling_min_freq).
/// </summary>
/// <param name="Switch">The turbo switch, or null when the box has none.</param>
/// <param name="SwitchValue">What the switch reads, or null.</param>
/// <param name="MaxKhz">Ceiling by CPU in kHz; null where unreadable.</param>
/// <param name="MinKhz">Floor by CPU in kHz; null where unreadable.</param>
public sealed record ClockSettings( TurboSwitch? Switch, string? SwitchValue, IReadOnlyDictionary<int, long?> MaxKhz, IReadOnlyDictionary<int, long?> MinKhz )
{
   #region Public Methods

   /// <summary>
   /// The settings as text for the notes and the log.
   /// </summary>
   /// <returns>e.g. "turbo on (intel_pstate/no_turbo 0), ceiling 3600 MHz on CPUs 0-7, floor 1200 MHz on CPUs 0-7".</returns>
   public string Describe()
   {
      string turbo = Switch == null
         ? $"no turbo switch ({ClockControl.NO_TURBO_PATH} and {ClockControl.BOOST_PATH} are both missing)"
         : $"turbo {Switch.Turbo( SwitchValue )} ({Switch.Name} {SwitchValue ?? "unreadable"})";
      return $"{turbo}, ceiling {PerCpu( MaxKhz )}, floor {PerCpu( MinKhz )}";
   }

   /// <summary>
   /// Every value of the settings in one text, so two readings compare exactly.
   /// </summary>
   /// <returns>The key.</returns>
   public string Key()
   {
      string Values( IReadOnlyDictionary<int, long?> khz ) => string.Join( ",", khz.OrderBy( k => k.Key ).Select( k => $"{k.Key}:{k.Value?.ToString( CultureInfo.InvariantCulture ) ?? "-"}" ) );
      return $"{Switch?.Path ?? "none"}={SwitchValue ?? "-"};max={Values( MaxKhz )};min={Values( MinKhz )}";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Per-CPU kHz values grouped as text.
   /// </summary>
   /// <param name="khz">kHz by CPU.</param>
   /// <returns>"3500 MHz on CPUs 0-7", or one part per distinct value.</returns>
   private static string PerCpu( IReadOnlyDictionary<int, long?> khz )
   {
      return khz.Count == 0 ? "not read" : string.Join( ", ", khz.GroupBy( k => k.Value ).OrderBy( g => g.Key ).Select( g =>
         $"{( g.Key is long v ? ClockControl.Mhz( v ).ToString( CultureInfo.InvariantCulture ) + " MHz" : "unreadable" )} on CPUs {CpuList.Format( g.Select( k => k.Key ) )}" ) );
   }

   #endregion Private Methods
}

/// <summary>
/// The clock a run pinned: the settings before, the settings while pinned, and the one clock
/// ceiling every CPU shares while pinned.
/// </summary>
/// <param name="Switch">The turbo switch used.</param>
/// <param name="Before">Settings before the run changed them.</param>
/// <param name="Pinned">Settings while pinned.</param>
/// <param name="PinnedMhz">Every CPU's clock ceiling while pinned; under the "performance" governor each CPU runs at it.</param>
/// <param name="Uncore">The uncore clock limit before and while pinned, or null when it was not pinned.</param>
public sealed record ClockPin( TurboSwitch Switch, ClockSettings Before, ClockSettings Pinned, int PinnedMhz, UncorePin? Uncore = null )
{
   #region Public Methods

   /// <summary>
   /// What was pinned, for the notes and the log.
   /// </summary>
   /// <returns>The text.</returns>
   public string Describe()
   {
      string uncore = Uncore == null ? "; uncore clock NOT pinned" : $"; the uncore (L3 and memory) clock is held at {Uncore.Pinned.MaxMhz} MHz ({Uncore.Pinned.Describe()}; was {Uncore.Before.Describe()})";
      return $"turbo off ({Switch.Name} {Before.SwitchValue} -> {Pinned.SwitchValue}), so every CPU's clock is held at its ceiling of {PinnedMhz} MHz whatever the engine runs{uncore}";
   }

   #endregion Public Methods
}

/// <summary>
/// The clock settings a run changed, with the values to put back: the turbo switch and every
/// CPU's ceiling and floor (which the kernel moves when turbo is switched). Written to the clock
/// record file BEFORE the switch is touched, for the same reason as <see cref="MachineState"/>.
/// </summary>
public sealed class ClockState
{
   #region Public Methods

   /// <summary>Process id of the run that owns this record.</summary>
   public int OwnerPid { get; set; }

   /// <summary>Start time of the owner in clock ticks since boot, so a reused process id is not mistaken for the owner.</summary>
   public long OwnerStartTicks { get; set; }

   /// <summary>When the owner started controlling the machine (UTC, ISO 8601).</summary>
   public string StartedUtc { get; set; } = string.Empty;

   /// <summary>The turbo switch's sysfs file; only <see cref="ClockControl.NO_TURBO_PATH"/> or <see cref="ClockControl.BOOST_PATH"/> is ever written back.</summary>
   public string SwitchPath { get; set; } = string.Empty;

   /// <summary>What the switch read before the run ("0" or "1").</summary>
   public string PreviousValue { get; set; } = string.Empty;

   /// <summary>What the run set it to (turbo off).</summary>
   public string PinnedValue { get; set; } = string.Empty;

   /// <summary>Each CPU's scaling_max_freq (kHz) before the run.</summary>
   public Dictionary<int, long> MaxKhz { get; set; } = new();

   /// <summary>Each CPU's scaling_min_freq (kHz) before the run.</summary>
   public Dictionary<int, long> MinKhz { get; set; } = new();

   /// <summary>The uncore ratio limit (MSR 0x620, hex) before the run, or null when the run did not pin the uncore clock.</summary>
   public string? PreviousUncoreLimit { get; set; }

   /// <summary>The uncore ratio limit the run set (hex), or null.</summary>
   public string? PinnedUncoreLimit { get; set; }

   /// <summary>
   /// What the record holds, for messages.
   /// </summary>
   /// <returns>e.g. "CPU clock (turbo switch /sys/.../no_turbo back to 0, ceiling and floor of 8 CPUs, uncore limit back to 0xc1e)".</returns>
   public string Describe()
   {
      return $"CPU clock (turbo switch {SwitchPath} back to {PreviousValue}, ceiling and floor of {MaxKhz.Count} CPUs{( PreviousUncoreLimit != null ? $", uncore limit MSR {UncoreControl.LIMIT_MSR} back to 0x{PreviousUncoreLimit}" : string.Empty )})";
   }

   #endregion Public Methods
}

/// <summary>
/// Reads and writes the clock record, a file next to the state file
/// (bench-machine-state.json gives bench-machine-state.clock.json).
/// Why a file of its own beside the state file: the state file's format (MachineState) has no
/// place for the clock; this file is written, owned, checked for a live owner and put back by
/// the same paths (the run's finally, the next start, restore-machine), and it is removed only
/// once the clock reads as it did before the run.
/// </summary>
public sealed class ClockStore
{
   #region Data Members

   private static readonly JsonSerializerOptions JSON = new()
   {
      WriteIndented = true,
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
   };

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the store of the clock record that belongs to a state file.
   /// </summary>
   /// <param name="stateFile">The machine state file.</param>
   public ClockStore( string stateFile )
   {
      string full = System.IO.Path.GetFullPath( stateFile );
      string folder = System.IO.Path.GetDirectoryName( full )!;
      string extension = System.IO.Path.GetExtension( full );
      Path = System.IO.Path.Combine( folder, System.IO.Path.GetFileNameWithoutExtension( full ) + ".clock" + ( extension.Length > 0 ? extension : ".json" ) );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Full path of the clock record.</summary>
   public string Path { get; }

   /// <summary>True when the record exists (something is still to be put back).</summary>
   public bool Exists => File.Exists( Path );

   /// <summary>
   /// Reads the record.
   /// </summary>
   /// <returns>The record, or null when there is none.</returns>
   /// <exception cref="InvalidDataException">The file exists but cannot be read; the message names the file and what to put back by hand.</exception>
   public ClockState? Load()
   {
      if( !File.Exists( Path ) )
      {
         return null;
      }

      try
      {
         return JsonSerializer.Deserialize<ClockState>( File.ReadAllText( Path ), JSON )
            ?? throw new InvalidDataException( $"The clock record {Path} is empty." );
      }
      catch( JsonException ex )
      {
         throw new InvalidDataException( $"The clock record {Path} is not readable ({ex.Message}). Put the CPU clock back by hand ({ClockControl.NO_TURBO_PATH} or {ClockControl.BOOST_PATH}, and each CPU's scaling_max_freq and scaling_min_freq), then delete the file.", ex );
      }
   }

   /// <summary>
   /// Writes the record so that a crash at any moment leaves the old file or the new one, never
   /// half of one: a temporary file, flushed to the disk, renamed over.
   /// </summary>
   /// <param name="state">The record.</param>
   public void Save( ClockState state )
   {
      Directory.CreateDirectory( System.IO.Path.GetDirectoryName( Path )! );
      string temporary = Path + ".tmp";
      using( var stream = new FileStream( temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough ) )
      {
         JsonSerializer.Serialize( stream, state, JSON );
         stream.Flush( true );
      }

      File.Move( temporary, Path, true );
   }

   /// <summary>
   /// Removes the record (the clock is back).
   /// </summary>
   public void Delete()
   {
      File.Delete( Path );
   }

   #endregion Public Methods
}

/// <summary>
/// Pins the CPU clock for a timed run and puts it back: turbo off, so under the "performance"
/// governor every CPU runs at its non-turbo top clock (the base clock), whatever the engine under
/// test runs.
/// Why: in the v6 runs the clock depended on the engine. Most engines ran at 3492 MHz, while
/// sqlite-vec, DuckDB, Chroma, Typesense, Weaviate and ClickHouse ran at about 3592 MHz, up to
/// 2.9% faster next to a 3% tie band. The v6 review guessed the number of busy cores. The v7
/// probes on linus7795 (Xeon E5-1620 v3, turbostat over APERF/MPERF and MSR 0x621, 2026-10-05 and
/// again 2026-10-06) say otherwise: with turbo on, an integer loop on one CPU ran at 3591 to 3600
/// MHz (and integer loops on 4 or 8 CPUs at 3560 to 3600), while a 256-bit AVX2 FMA loop on any
/// one CPU held every core at 3500 MHz, a core running an integer loop included. So the turbo
/// clock follows whether any core runs AVX2 code, not how many are busy (that the engines' own
/// code is what differs is inferred from this, not traced per instruction); the turbo-on check
/// run of 2026-10-06 (main 77a94d0, seed 731) again put pgvector at 3492 MHz and sqlite-vec and
/// Typesense at 3592 to 3596.
/// With turbo off every one of those loads ran at exactly 3500 MHz, AVX2 FMA on all 8 CPUs
/// included: the clock every core holds at full load. Turbo off also lowers the uncore (L3 and
/// memory) clock from 3.0 to 2.2 GHz and lets it follow the load, which <see cref="UncoreControl"/>
/// pins; figures are not comparable with runs made with turbo on.
/// On intel_pstate (passive, intel_cpufreq) no_turbo 1 moves every CPU's scaling_max_freq from
/// 3600000 to 3500000 kHz; cpufreq/boost does not exist on this box.
/// The switch, and every CPU's ceiling and floor, are recorded in the clock record before the
/// switch is touched; putting back writes only what differs and checks every value reads as
/// before.
/// </summary>
public static class ClockControl
{
   #region Data Members

   /// <summary>intel_pstate's turbo switch (1 = turbo off), in active and passive mode.</summary>
   public const string NO_TURBO_PATH = "/sys/devices/system/cpu/intel_pstate/no_turbo";

   /// <summary>The generic cpufreq turbo switch (0 = turbo off), e.g. acpi-cpufreq.</summary>
   public const string BOOST_PATH = "/sys/devices/system/cpu/cpufreq/boost";

   private const long MAX_KHZ = 10_000_000;
   private const int MAX_CPU = 4096;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The sysfs file holding a CPU's frequency ceiling in kHz.
   /// </summary>
   /// <param name="cpu">Logical CPU.</param>
   /// <returns>The path.</returns>
   public static string MaxPath( int cpu )
   {
      return $"/sys/devices/system/cpu/cpu{cpu}/cpufreq/scaling_max_freq";
   }

   /// <summary>
   /// The sysfs file holding a CPU's frequency floor in kHz.
   /// </summary>
   /// <param name="cpu">Logical CPU.</param>
   /// <returns>The path.</returns>
   public static string MinPath( int cpu )
   {
      return $"/sys/devices/system/cpu/cpu{cpu}/cpufreq/scaling_min_freq";
   }

   /// <summary>
   /// kHz as whole MHz.
   /// </summary>
   /// <param name="khz">kHz.</param>
   /// <returns>MHz, rounded.</returns>
   public static int Mhz( long khz )
   {
      return (int)Math.Round( khz / 1000.0, MidpointRounding.AwayFromZero );
   }

   /// <summary>
   /// The box's turbo switch: intel_pstate's no_turbo when present, else cpufreq's boost.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <returns>The switch, or null when the box has neither.</returns>
   public static TurboSwitch? FindSwitch( IMachineSystem system )
   {
      if( system.ReadFile( NO_TURBO_PATH ) != null )
      {
         return new TurboSwitch( NO_TURBO_PATH, "1", "intel_pstate/no_turbo" );
      }

      return system.ReadFile( BOOST_PATH ) != null ? new TurboSwitch( BOOST_PATH, "0", "cpufreq/boost" ) : null;
   }

   /// <summary>
   /// Reads the clock settings now.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="cpus">CPUs to read.</param>
   /// <returns>The settings.</returns>
   public static ClockSettings Read( IMachineSystem system, IReadOnlyList<int> cpus )
   {
      TurboSwitch? turbo = FindSwitch( system );
      return new ClockSettings( turbo, turbo == null ? null : system.ReadFile( turbo.Path )?.Trim(),
         cpus.ToDictionary( c => c, c => Khz( system, MaxPath( c ) ) ), cpus.ToDictionary( c => c, c => Khz( system, MinPath( c ) ) ) );
   }

   /// <summary>
   /// Turns turbo off and holds the uncore clock at its top ratio, recording the switch, every
   /// CPU's ceiling and floor and the uncore limit in the clock record first, then reads them
   /// back. Nothing is changed when any of them cannot be read.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="store">The clock record (written before the change).</param>
   /// <param name="owner">The run's state, for its owner.</param>
   /// <param name="cpus">Every online CPU.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>What was pinned.</returns>
   /// <exception cref="InvalidOperationException">No turbo switch, unreadable settings or uncore limit, a failed write, turbo still on, CPUs that do not share one ceiling, or an uncore limit that did not take.</exception>
   public static async Task<ClockPin> ApplyAsync( IMachineSystem system, ClockStore store, MachineState owner, IReadOnlyList<int> cpus, CancellationToken ct )
   {
      TurboSwitch turbo = FindSwitch( system ) ?? throw new InvalidOperationException( $"The CPU clock cannot be pinned: this box has neither {NO_TURBO_PATH} nor {BOOST_PATH}, so turbo could raise the clock when fewer cores are busy. Run with --no-machine-control to measure anyway (the results will say so)." );
      ClockSettings before = Read( system, cpus );
      RequireRecordable( turbo, before, cpus );
      UncoreLimit uncoreBefore = await UncoreControl.ReadAsync( system, ct );
      UncoreLimit uncorePinned = uncoreBefore.Pinned();
      store.Save( new ClockState
      {
         OwnerPid = owner.OwnerPid, OwnerStartTicks = owner.OwnerStartTicks, StartedUtc = owner.StartedUtc,
         SwitchPath = turbo.Path, PreviousValue = before.SwitchValue!, PinnedValue = turbo.Off,
         MaxKhz = before.MaxKhz.ToDictionary( k => k.Key, k => k.Value!.Value ), MinKhz = before.MinKhz.ToDictionary( k => k.Key, k => k.Value!.Value ),
         PreviousUncoreLimit = uncoreBefore.Hex, PinnedUncoreLimit = uncorePinned.Hex,
      } );
      if( before.SwitchValue != turbo.Off )
      {
         await ProcessInfo.WriteAsRootAsync( system, turbo.Path, turbo.Off, ct );
      }

      ClockSettings pinned = Read( system, cpus );
      if( pinned.SwitchValue != turbo.Off )
      {
         throw new InvalidOperationException( $"Turbo did not turn off: {turbo.Path} reads {pinned.SwitchValue ?? "nothing"} after writing {turbo.Off}." );
      }

      List<long?> ceilings = pinned.MaxKhz.Values.Distinct().ToList();
      if( ceilings.Count != 1 || ceilings[0] is not long khz || khz <= 0 )
      {
         throw new InvalidOperationException( $"With turbo off the CPUs do not share one clock ceiling ({pinned.Describe()}), so passes on different CPUs would run at different clocks. Set every CPU's scaling_max_freq to one value, or run with --no-machine-control." );
      }

      await UncoreControl.SetAsync( system, uncoreBefore, uncorePinned, ct );
      return new ClockPin( turbo, before, pinned, Mhz( khz ), new UncorePin( uncoreBefore, uncorePinned ) );
   }

   /// <summary>
   /// Puts the recorded clock back: the turbo switch, then each CPU's ceiling and floor where
   /// they do not read as recorded, checking every value after; removes the record only when all
   /// of them read as before. A failure keeps the record for the next attempt.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="store">The clock record.</param>
   /// <param name="state">What it holds.</param>
   /// <param name="log">Progress output (one line when the clock is back).</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Problems; empty when the clock is back.</returns>
   public static async Task<List<string>> RestoreAsync( IMachineSystem system, ClockStore store, ClockState state, Action<string> log, CancellationToken ct )
   {
      try
      {
         Validate( state );
         if( system.ReadFile( state.SwitchPath )?.Trim() != state.PreviousValue )
         {
            await ProcessInfo.WriteAsRootAsync( system, state.SwitchPath, state.PreviousValue, ct );
         }

         string? now = system.ReadFile( state.SwitchPath )?.Trim();
         if( now != state.PreviousValue )
         {
            throw new InvalidOperationException( $"{state.SwitchPath} reads {now ?? "nothing"} after writing {state.PreviousValue}" );
         }

         int written = await PutBackAsync( system, state.MaxKhz, MaxPath, ct ) + await PutBackAsync( system, state.MinKhz, MinPath, ct );
         string uncore = state.PreviousUncoreLimit == null ? string.Empty : "; uncore limit back to " + ( await UncoreControl.SetAsync( system, null, UncoreLimit.Parse( state.PreviousUncoreLimit ), ct ) ).Describe();
         store.Delete();
         string turbo = ( state.SwitchPath == NO_TURBO_PATH ) == ( state.PreviousValue == "1" ) ? "off" : "on";
         log( $"  machine: CPU clock back: turbo {turbo} ({state.SwitchPath} {state.PreviousValue}); every CPU's ceiling and floor read as before the run{( written > 0 ? $" ({written} written back by hand)" : string.Empty )}{uncore}" );
         return new List<string>();
      }
      catch( Exception ex ) when( ex is InvalidOperationException or TimeoutException or IOException or FormatException or UnauthorizedAccessException )
      {
         return new List<string> { $"CPU clock back to {state.SwitchPath} {state.PreviousValue} (record kept in {store.Path}): {ex.Message}" };
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A kHz value from a sysfs file.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="path">The file.</param>
   /// <returns>kHz, or null when missing or not a number.</returns>
   private static long? Khz( IMachineSystem system, string path )
   {
      return long.TryParse( system.ReadFile( path )?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long khz ) ? khz : null;
   }

   /// <summary>
   /// Refuses a record that would make the restore write anything but a turbo switch to 0 or 1
   /// and plausible kHz values to the cpufreq files of real CPU numbers. Why: the record is a
   /// user-writable file and the restore writes as root.
   /// </summary>
   /// <param name="state">The record.</param>
   /// <exception cref="InvalidOperationException">Something in it is not a value this tool writes.</exception>
   private static void Validate( ClockState state )
   {
      if( state.SwitchPath is not ( NO_TURBO_PATH or BOOST_PATH ) || state.PreviousValue is not ( "0" or "1" ) )
      {
         throw new InvalidOperationException( $"the recorded turbo switch '{state.SwitchPath}' = '{state.PreviousValue}' is not one this tool sets" );
      }

      if( state.MaxKhz.Concat( state.MinKhz ).Any( k => k.Key < 0 || k.Key >= MAX_CPU || k.Value <= 0 || k.Value > MAX_KHZ ) )
      {
         throw new InvalidOperationException( "the recorded CPU frequency limits hold a CPU number or kHz value this tool never records" );
      }

      if( state.PreviousUncoreLimit != null )
      {
         UncoreLimit.Parse( state.PreviousUncoreLimit );
      }
   }

   /// <summary>
   /// Refuses settings that could not be put back: a turbo switch that reads neither 0 nor 1, or
   /// a CPU whose ceiling or floor cannot be read.
   /// </summary>
   /// <param name="turbo">The switch.</param>
   /// <param name="before">The settings found.</param>
   /// <param name="cpus">Every online CPU.</param>
   /// <exception cref="InvalidOperationException">Something cannot be recorded.</exception>
   private static void RequireRecordable( TurboSwitch turbo, ClockSettings before, IReadOnlyList<int> cpus )
   {
      if( before.SwitchValue is not ( "0" or "1" ) )
      {
         throw new InvalidOperationException( $"{turbo.Path} reads '{before.SwitchValue ?? "nothing"}', not 0 or 1, so the turbo setting cannot be recorded and put back." );
      }

      List<int> unreadable = cpus.Where( c => before.MaxKhz[c] is not > 0 || before.MinKhz[c] is not > 0 ).ToList();
      if( unreadable.Count > 0 )
      {
         throw new InvalidOperationException( $"CPU(s) {CpuList.Format( unreadable )} have no readable {MaxPath( unreadable[0] )} or scaling_min_freq, so the clock cannot be recorded and put back. Run with --no-machine-control to measure anyway." );
      }
   }

   /// <summary>
   /// Writes back each CPU's recorded value where the file does not read it, then checks it does.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="recorded">kHz by CPU.</param>
   /// <param name="path">The file of a CPU.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>How many values had to be written.</returns>
   /// <exception cref="InvalidOperationException">A value does not read as recorded after the write.</exception>
   private static async Task<int> PutBackAsync( IMachineSystem system, Dictionary<int, long> recorded, Func<int, string> path, CancellationToken ct )
   {
      int written = 0;
      foreach( KeyValuePair<int, long> value in recorded.OrderBy( v => v.Key ) )
      {
         if( Khz( system, path( value.Key ) ) != value.Value )
         {
            await ProcessInfo.WriteAsRootAsync( system, path( value.Key ), value.Value.ToString( CultureInfo.InvariantCulture ), ct );
            written++;
         }

         long? now = Khz( system, path( value.Key ) );
         if( now != value.Value )
         {
            throw new InvalidOperationException( $"{path( value.Key )} reads {now?.ToString( CultureInfo.InvariantCulture ) ?? "nothing"} after writing {value.Value}" );
         }
      }

      return written;
   }

   #endregion Private Methods
}

/// <summary>
/// The uncore ratio limit of an Intel CPU (MSR 0x620): bits 6-0 the highest ratio the uncore
/// (L3 and memory controller) may run at, bits 14-8 the lowest, in steps of 100 MHz.
/// </summary>
/// <param name="Value">The register's value.</param>
public sealed record UncoreLimit( long Value )
{
   #region Data Members

   /// <summary>The bits of the two ratio fields; every other bit is reserved and must be zero.</summary>
   public const long RATIO_BITS = 0x7F7F;

   private static readonly Regex HEX = new( "^(0x)?[0-9a-fA-F]{1,16}$", RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>Highest uncore ratio.</summary>
   public int MaxRatio => (int)( Value & 0x7F );

   /// <summary>Lowest uncore ratio.</summary>
   public int MinRatio => (int)( ( Value >> 8 ) & 0x7F );

   /// <summary>Highest uncore clock in MHz.</summary>
   public int MaxMhz => MaxRatio * 100;

   /// <summary>Lowest uncore clock in MHz.</summary>
   public int MinMhz => MinRatio * 100;

   /// <summary>The value as rdmsr prints it (lower-case hex, no prefix).</summary>
   public string Hex => Value.ToString( "x", CultureInfo.InvariantCulture );

   /// <summary>
   /// The limit with its lowest ratio raised to its highest, so the uncore runs at one clock.
   /// </summary>
   /// <returns>The pinned limit.</returns>
   public UncoreLimit Pinned()
   {
      long ratio = MaxRatio;
      return new UncoreLimit( ( Value & ~RATIO_BITS ) | ( ratio << 8 ) | ratio );
   }

   /// <summary>
   /// The limit as text for the notes and the log.
   /// </summary>
   /// <returns>e.g. "min 1200 MHz, max 3000 MHz (MSR 0x620 = 0xc1e)".</returns>
   public string Describe()
   {
      return $"min {MinMhz} MHz, max {MaxMhz} MHz (MSR {UncoreControl.LIMIT_MSR} = 0x{Hex})";
   }

   /// <summary>
   /// Reads a limit from rdmsr's output or a clock record, refusing anything this tool would not
   /// write: reserved bits set, a highest ratio of zero, or a lowest ratio above the highest.
   /// Why so strict: the value comes from a user-writable file and is written back as root.
   /// </summary>
   /// <param name="hex">Hex, with or without "0x".</param>
   /// <returns>The limit.</returns>
   /// <exception cref="InvalidOperationException">Not a limit this tool records.</exception>
   public static UncoreLimit Parse( string hex )
   {
      string text = hex.Trim();
      if( !HEX.IsMatch( text ) )
      {
         throw new InvalidOperationException( $"the uncore limit '{text}' is not a hex number" );
      }

      var limit = new UncoreLimit( long.Parse( text.StartsWith( "0x", StringComparison.OrdinalIgnoreCase ) ? text[2..] : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture ) );
      if( ( limit.Value & ~RATIO_BITS ) != 0 || limit.MaxRatio == 0 || limit.MinRatio > limit.MaxRatio )
      {
         throw new InvalidOperationException( $"the uncore limit 0x{limit.Hex} is not one this tool records or sets (reserved bits, or ratios min {limit.MinRatio} / max {limit.MaxRatio})" );
      }

      return limit;
   }

   #endregion Public Methods
}

/// <summary>
/// The uncore limit a run found and the one it set.
/// </summary>
/// <param name="Before">The limit before the run.</param>
/// <param name="Pinned">The limit during the run (lowest ratio = highest ratio).</param>
public sealed record UncorePin( UncoreLimit Before, UncoreLimit Pinned );

/// <summary>
/// Reads and sets the uncore ratio limit through msr-tools (rdmsr -a / wrmsr -a 0x620, as root).
/// Why the uncore is pinned too: with turbo off its clock drops and follows the load. The probe of
/// 2026-10-06 (MSR 0x621) read it at 2.2 GHz under every load with turbo off against 3.0 GHz
/// with turbo on, and the check run of 2026-10-05 (turbo off, uncore not pinned) saw 2.2 GHz
/// through every sqlite-vec pass and pgvector at 8 searchers but 1.7 to 2.2 GHz (median 1.8) for
/// pgvector at one searcher, so engines again met different clocks, now in the L3 and memory
/// path. With the lowest ratio raised to the highest (MSR 0x620 0xc1e -> 0x1e1e) the probe read
/// 3.0 GHz idle, under one integer loop and under AVX2 FMA on 4 and on all 8 CPUs, with the cores
/// at 3500 MHz: the uncore clock every pass of a turbo-on run had (3.0 GHz) and the core clock
/// every core holds at full load. Linux has no sysfs file for this on this CPU (the
/// intel_uncore_frequency module of kernel 6.8 lists no Haswell-EP, model 63), hence the MSR.
/// The register is per package and resets at power-on; -a reads and writes it on every CPU, so
/// every package gets the same value. The kernel logs each write ("Write to unrecognized MSR
/// 0x620") and marks itself tainted 'S' until the next boot; it changes nothing else.
/// </summary>
public static class UncoreControl
{
   #region Data Members

   /// <summary>The uncore ratio limit register.</summary>
   public const string LIMIT_MSR = "0x620";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Reads the uncore limit of every CPU.
   /// Why it never loads the msr module itself: loading a kernel module is a machine change this
   /// tool would then have to record and undo, and a box where the module is not there is not
   /// set up for a pinned clock; the run refuses before changing anything and says how to fix it.
   /// (linus7795 loads the module at every boot, from fwupd's /usr/lib/modules-load.d/fwupd-msr.conf.)
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The limit every CPU reports.</returns>
   /// <exception cref="InvalidOperationException">It cannot be read, is not a limit this tool records, or differs between CPUs.</exception>
   public static async Task<UncoreLimit> ReadAsync( IMachineSystem system, CancellationToken ct )
   {
      ShellResult read = await ProcessInfo.SudoAsync( system, new[] { "rdmsr", "-a", LIMIT_MSR }, ct );
      if( read.ExitCode != 0 )
      {
         string said = ( read.Error.Trim().Length > 0 ? read.Error : read.Output ).Trim();
         throw new InvalidOperationException( $"The uncore clock cannot be pinned: 'rdmsr -a {LIMIT_MSR}' failed (exit {read.ExitCode}: {said}). It needs msr-tools and the msr kernel module: load it with 'sudo modprobe msr' and run again, or run with --no-machine-control to measure anyway (the results will say so)." );
      }

      List<string> lines = read.Output.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ).ToList();
      List<UncoreLimit> limits = lines.Select( UncoreLimit.Parse ).Distinct().ToList();
      if( limits.Count != 1 )
      {
         throw new InvalidOperationException( $"The uncore clock cannot be pinned: rdmsr -a {LIMIT_MSR} gave {( limits.Count == 0 ? "no value" : "different values on different CPUs: " + string.Join( ", ", limits.Select( l => "0x" + l.Hex ) ) )}." );
      }

      return limits[0];
   }

   /// <summary>
   /// Sets every CPU's uncore limit to a value, when it does not already read it, and checks it
   /// reads it after.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="current">The limit now, or null to read it first.</param>
   /// <param name="target">The limit wanted.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The limit, as read back.</returns>
   /// <exception cref="InvalidOperationException">The write failed or did not take.</exception>
   public static async Task<UncoreLimit> SetAsync( IMachineSystem system, UncoreLimit? current, UncoreLimit target, CancellationToken ct )
   {
      if( ( current ?? await ReadAsync( system, ct ) ) == target )
      {
         return target;
      }

      ShellResult write = await ProcessInfo.SudoAsync( system, new[] { "wrmsr", "-a", LIMIT_MSR, "0x" + target.Hex }, ct );
      ProcessInfo.Require( write, $"set the uncore limit (MSR {LIMIT_MSR}) to 0x{target.Hex}" );
      UncoreLimit now = await ReadAsync( system, ct );
      if( now != target )
      {
         throw new InvalidOperationException( $"the uncore limit reads 0x{now.Hex} after writing 0x{target.Hex}" );
      }

      return now;
   }

   #endregion Public Methods
}
