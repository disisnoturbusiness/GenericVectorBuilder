using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Bench.Stats;

namespace GenericVectorBuilder.Bench.Report;

/// <summary>
/// The conditions one run was measured under, read from its results.json: build configuration,
/// CPU governor, which CPUs the client and the engines were allowed to use, how many warm-up
/// searches and how many seconds of exact mode each pass had. Every field is optional: a run
/// written before a field existed loads with it null, and the report shows "missing" instead of
/// guessing.
/// Why a class of its own: these facts decide whether two runs may be averaged at all (the CPU
/// governor alone changed single-searcher QPS by a factor of two), so reading them has one
/// place, one set of accepted spellings and one test.
/// Why several accepted spellings and places: the run may carry a field at the top, under
/// "machine" or under "conditions", and the benchmark's pieces were written separately; a field
/// that exists under another accepted name must not read as missing.
/// The machine-control step of the benchmark writes these under a top-level "conditions" object
/// (buildConfiguration, governor, governorAtEnd, clientCpus, engineCpus, threadSiblings,
/// warmupSearches, exactSeconds, plus per timed pass the governor and whether the box was busy,
/// and per engine how it was pinned); older and other writers put some under "machine".
/// Facts the old harness did not write but that are recoverable are derived and labelled: the
/// build configuration from the path of the binary in the command line, the warm-up count from
/// the method note, and --warmup / --exact-seconds from the command line.
/// </summary>
public sealed class RunConditions
{
   #region Data Members

   /// <summary>The governor the CPU frequency scaling must be on for a timed run.</summary>
   public const string GOVERNOR_TARGET = "performance";

   private static readonly string[] CONTAINERS = { "conditions", "machine", "method", "environment", "options", "settings" };
   private static readonly string[] BUILD_NAMES = { "buildConfiguration", "buildConfig", "configuration" };
   private static readonly string[] GOVERNOR_NAMES = { "governor", "cpuGovernor", "scalingGovernor", "cpuFrequencyGovernor" };
   private static readonly string[] GOVERNOR_END_NAMES = { "governorAtEnd", "governorEnd", "governorAfter" };
   private static readonly string[] GOVERNOR_PICK = { "during", "applied", "set", "value", "current", "now", "run" };
   private static readonly string[] CLIENT_NAMES = { "clientCpus", "clientCpuList", "clientAffinity", "benchCpus", "benchmarkCpus" };
   private static readonly string[] ENGINE_NAMES = { "engineCpus", "engineCpuList", "engineAffinity", "enginesCpus", "serverCpus" };
   private static readonly string[] PARTITION_NAMES = { "cpuPartition", "partition", "cpuAffinity", "affinity", "pinning" };
   private static readonly string[] SIBLING_NAMES = { "threadSiblings", "siblings", "cpuSiblings", "hyperthreadSiblings" };
   private static readonly string[] WARMUP_NAMES = { "warmup", "warmupSearches", "warmupCount" };
   private static readonly string[] EXACT_NAMES = { "exactSeconds", "exactSecondsBudget" };
   private static readonly string[] CPU_NAMES = { "logicalCpus", "logicalCPUs", "cpus" };
   private static readonly string[] CPU_CAP_NAMES = { "cpuCap", "cpuLimit", "engineCpuCap" };
   private static readonly Regex BUILD_IN_PATH = new( @"[/\\]bin[/\\](Debug|Release)[/\\]", RegexOptions.Compiled | RegexOptions.IgnoreCase );
   private static readonly Regex WARMUP_IN_NOTE = new( @"untimed warm-up of (\d+) searches", RegexOptions.Compiled );
   private static readonly Regex WARMUP_IN_COMMAND = new( @"--warmup[ =](\d+)", RegexOptions.Compiled );
   private static readonly Regex EXACT_IN_COMMAND = new( @"--exact-seconds[ =](\d+)", RegexOptions.Compiled );
   private static readonly Regex CLIENT_IN_TEXT = new( @"(?:client|bench)\w*\W+([0-9][0-9,\-]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase );
   private static readonly Regex ENGINE_IN_TEXT = new( @"(?:engine|server)\w*\W+([0-9][0-9,\-]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase );

   #endregion Data Members

   #region Public Methods

   /// <summary>Build configuration of the benchmark client ("Release" or "Debug"), or null.</summary>
   public string? BuildConfiguration { get; init; }

   /// <summary>CPU frequency governor during the run ("performance", "schedutil", ...), or null. When it changed during the run both values are in the text.</summary>
   public string? Governor { get; init; }

   /// <summary>CPUs the benchmark client could run on, canonical ("0,4" or "0-3"), or null when not recorded.</summary>
   public string? ClientCpus { get; init; }

   /// <summary>CPUs the engines could run on, canonical, or null when not recorded.</summary>
   public string? EngineCpus { get; init; }

   /// <summary>The partition as free text when it was recorded in a form that could not be split into client and engine CPUs.</summary>
   public string? PartitionText { get; init; }

   /// <summary>Thread siblings as the kernel lists them ("0,4"), one entry per physical core; empty when not recorded.</summary>
   public IReadOnlyList<string> ThreadSiblings { get; init; } = Array.Empty<string>();

   /// <summary>Untimed warm-up searches before every timed pass, or null.</summary>
   public int? WarmupSearches { get; init; }

   /// <summary>Seconds each engine's exact mode was allowed, or null.</summary>
   public int? ExactSeconds { get; init; }

   /// <summary>
   /// How every timed pass was warmed up and checked (time based warm-up, settle trial, extension, rehearsal), as the run's own method notes
   /// state it; null when the run recorded no such note. Why beside <see cref="WarmupSearches"/>: that count is only the minimum number of
   /// searches in a warm-up, and printing it alone described a time based method as "20 searches".
   /// </summary>
   public WarmupMethod? WarmupMethod { get; init; }

   /// <summary>Logical CPUs of the box, or null.</summary>
   public int? LogicalCpus { get; init; }

   /// <summary>Load average (1, 5 and 15 minutes) when the run started, or null.</summary>
   public string? LoadAverage { get; init; }

   /// <summary>"on", or "off" with the reason, when the run recorded whether machine control was in force; null otherwise.</summary>
   public string? MachineControl { get; init; }

   /// <summary>"on" or "off" (the first word of <see cref="MachineControl"/>), or null when not recorded. Why: a run with the machine held steady and one without are different experiments.</summary>
   public string? MachineControlState => MachineControl == null ? null : new string( MachineControl.TakeWhile( char.IsLetter ).ToArray() ).ToLowerInvariant();

   /// <summary>One entry per timed pass the run recorded (target, pass, governor during it, whether the box was busy); empty when not recorded.</summary>
   public IReadOnlyList<PassFacts> Passes { get; init; } = Array.Empty<PassFacts>();

   /// <summary>One entry per engine the run pinned or inspected (hosting, CPUs, problems); empty when not recorded.</summary>
   public IReadOnlyList<EnginePinFacts> Engines { get; init; } = Array.Empty<EnginePinFacts>();

   /// <summary>Which fields were derived instead of recorded, by field name, with the source ("from the command line path").</summary>
   public IReadOnlyDictionary<string, string> Derived { get; init; } = new Dictionary<string, string>();

   /// <summary>
   /// How the CPU clock was held: conditions.clock of a v8 run, or the machine-control note of a v5 to v7 run
   /// (<see cref="RunClock.LegacyParsed"/>). Never null: a run that recorded neither reads as not pinned.
   /// </summary>
   public RunClock Clock { get; init; } = new();

   /// <summary>conditions.build.informationalVersion of a v8 run; null when not recorded. Why: a v8 run names its build by commit, a v7 run only by the path of its binary.</summary>
   public string? BuildVersion { get; init; }

   /// <summary>conditions.build.commit of a v8 run; null when not recorded.</summary>
   public string? BuildCommit { get; init; }

   /// <summary>conditions.boot.bootId of a v8 run; null when not recorded.</summary>
   public string? BootId { get; init; }

   /// <summary>conditions.boot.bootTimeUtc of a v8 run; null when not recorded. Why: a boot time before the first v7 start shows both sessions ran on one boot.</summary>
   public string? BootTimeUtc { get; init; }

   /// <summary>The distinct routes of conditions.connections[].route, in first-seen order; empty when not recorded. Why: the report quotes how the targets were reached.</summary>
   public List<string> Routes { get; init; } = new();

   /// <summary>conditions.engineCpu.rule of a v8 run: how engine CPU per search and dockerd's CPU were counted, generated by the run from what its code did; null when not recorded.</summary>
   public string? EngineCpuRule { get; init; }

   /// <summary>conditions.cpuIdle as comparable text (driver, governor, states); null when not recorded.</summary>
   public string? CpuIdle { get; init; }

   /// <summary>conditions.cpuIdle.driver (the idle driver, e.g. "intel_idle"); null when not recorded.</summary>
   public string? CpuIdleDriver { get; init; }

   /// <summary>conditions.cpuIdle.governor (the idle governor, e.g. "menu"); null when not recorded.</summary>
   public string? CpuIdleGovernor { get; init; }

   /// <summary>conditions.flags verbatim, in order; empty when not recorded.</summary>
   public IReadOnlyList<string> Flags { get; init; } = Array.Empty<string>();

   /// <summary>conditions.throttleByTarget per target: the thermal throttle counters' rise over the target (core, package), null where not recorded; empty when the run recorded none.</summary>
   public IReadOnlyDictionary<string, RecordedThrottle> ThrottleByTarget { get; init; } = new Dictionary<string, RecordedThrottle>();

   /// <summary>
   /// The partition as one comparable text: "client 0,4; engines 1-3,5-7", the free text, or null
   /// when nothing was recorded. Why one text: it is part of the settings two runs must share.
   /// </summary>
   public string? Partition
   {
      get
      {
         if( ClientCpus == null && EngineCpus == null )
         {
            return PartitionText;
         }

         return $"client {ClientCpus ?? "not recorded"}; engines {EngineCpus ?? "not recorded"}";
      }
   }

   /// <summary>
   /// Reads the conditions from a results.json root.
   /// </summary>
   /// <param name="root">The results.json root object.</param>
   /// <param name="commandLine">The run's command line, or null.</param>
   /// <param name="notes">The run's notes, or null.</param>
   /// <returns>The conditions; every field the file did not carry is null.</returns>
   public static RunConditions Parse( JsonElement root, string? commandLine, IReadOnlyList<string>? notes )
   {
      var derived = new Dictionary<string, string>();
      string? build = Known( Find( root, BUILD_NAMES ) is JsonElement b ? AsText( b ) : null );
      if( build == null && commandLine != null && BUILD_IN_PATH.Match( commandLine ) is { Success: true } path )
      {
         build = path.Groups[1].Value;
         derived["buildConfiguration"] = "from the path of the binary in the command line";
      }

      ( string? client, string? engine, string? text ) = Cpus( root );
      return new RunConditions
      {
         BuildConfiguration = build == null ? null : char.ToUpperInvariant( build[0] ) + build[1..].ToLowerInvariant(),
         Governor = Known( ReadGovernor( root ) ),
         ClientCpus = client,
         EngineCpus = engine,
         PartitionText = text,
         ThreadSiblings = Find( root, SIBLING_NAMES ) is { ValueKind: JsonValueKind.Array } s ? s.EnumerateArray().Select( i => AsText( i ) ).OfType<string>().ToList() : Array.Empty<string>(),
         WarmupSearches = WholeNumber( root, WARMUP_NAMES ) ?? FromText( commandLine, WARMUP_IN_COMMAND, "warmupSearches", "from the command line", derived ) ?? FromNotes( notes, derived ),
         ExactSeconds = WholeNumber( root, EXACT_NAMES ) ?? FromText( commandLine, EXACT_IN_COMMAND, "exactSeconds", "from the command line", derived ),
         WarmupMethod = WarmupMethod.From( notes ),
         LogicalCpus = WholeNumber( root, CPU_NAMES ),
         LoadAverage = Find( root, new[] { "loadAverage" } ) is JsonElement l ? AsText( l ) : null,
         MachineControl = Find( root, new[] { "machineControl" } ) is JsonElement m ? AsText( m ) : null,
         Passes = ConditionsList( root, "passes" ).Select( ReadPass ).ToList(),
         Engines = ConditionsList( root, "engines" ).Select( ReadEngine ).ToList(),
         Derived = derived,
         Clock = RunClock.Read( root, notes ),
         BuildVersion = Nested( root, "build", "informationalVersion" ),
         BuildCommit = Nested( root, "build", "commit" ),
         BootId = Nested( root, "boot", "bootId" ),
         BootTimeUtc = Nested( root, "boot", "bootTimeUtc" ),
         EngineCpuRule = Nested( root, "engineCpu", "rule" ),
         Routes = ConditionsList( root, "connections" ).Select( c => ResultJson.Text( c, "route" ) ).OfType<string>().Distinct( StringComparer.Ordinal ).ToList(),
         CpuIdle = InConditions( root, "cpuIdle" ) is JsonElement idle ? AsText( idle ) : null,
         CpuIdleDriver = Nested( root, "cpuIdle", "driver" ),
         CpuIdleGovernor = Nested( root, "cpuIdle", "governor" ),
         Flags = InConditions( root, "flags" ) is { ValueKind: JsonValueKind.Array } flags ? flags.EnumerateArray().Select( f => AsText( f ) ).OfType<string>().ToList() : Array.Empty<string>(),
         ThrottleByTarget = Throttles( root ),
      };
   }

   /// <summary>
   /// True when the governor text is exactly the target governor (ignoring case).
   /// </summary>
   /// <param name="governor">The governor text.</param>
   /// <returns>True for "performance".</returns>
   public static bool IsPerformance( string governor )
   {
      return string.Equals( governor.Trim(), GOVERNOR_TARGET, StringComparison.OrdinalIgnoreCase );
   }

   /// <summary>
   /// Names of the conditions the run did not record, as shown in the fields-missing flag.
   /// </summary>
   /// <returns>Names like "conditions.governor"; empty when everything was recorded.</returns>
   public IReadOnlyList<string> MissingNames()
   {
      var missing = new List<string>();
      Add( missing, BuildConfiguration, "conditions.buildConfiguration" );
      Add( missing, Governor, "conditions.governor" );
      if( PartitionText == null )
      {
         Add( missing, ClientCpus, "conditions.clientCpus" );
         Add( missing, EngineCpus, "conditions.engineCpus" );
      }

      Add( missing, WarmupSearches?.ToString( CultureInfo.InvariantCulture ), "conditions.warmupSearches" );
      Add( missing, ExactSeconds?.ToString( CultureInfo.InvariantCulture ), "conditions.exactSeconds" );
      return missing;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Adds a name to the list when the value is null.
   /// </summary>
   /// <param name="missing">Receives the name.</param>
   /// <param name="value">The value.</param>
   /// <param name="name">The name to add.</param>
   private static void Add( List<string> missing, string? value, string name )
   {
      if( value == null )
      {
         missing.Add( name );
      }
   }

   /// <summary>
   /// The first element found under any of the names: first at the top of the file, then inside
   /// each container object, then inside conditions.search.
   /// </summary>
   /// <param name="root">The results.json root.</param>
   /// <param name="names">Accepted spellings, preferred first.</param>
   /// <returns>The element, or null.</returns>
   private static JsonElement? Find( JsonElement root, string[] names )
   {
      foreach( JsonElement holder in Holders( root ) )
      {
         foreach( string name in names )
         {
            if( ResultJson.Child( holder, name ) is JsonElement found )
            {
               return found;
            }
         }
      }

      return null;
   }

   /// <summary>
   /// The objects a condition may be recorded in, most specific first: the file's top level, each
   /// container object, then the search settings inside "conditions".
   /// </summary>
   /// <param name="root">The results.json root.</param>
   /// <returns>The objects.</returns>
   private static IEnumerable<JsonElement> Holders( JsonElement root )
   {
      yield return root;
      foreach( string container in CONTAINERS )
      {
         if( ResultJson.Child( root, container ) is { ValueKind: JsonValueKind.Object } inside )
         {
            yield return inside;
         }
      }

      if( ResultJson.Child( root, "conditions" ) is JsonElement conditions && ResultJson.Child( conditions, "search" ) is { ValueKind: JsonValueKind.Object } search )
      {
         yield return search;
      }
   }

   /// <summary>
   /// The objects of an array inside the top-level "conditions" object (passes, engines).
   /// </summary>
   /// <param name="root">The results.json root.</param>
   /// <param name="name">Array name.</param>
   /// <returns>The objects; none when absent.</returns>
   private static IEnumerable<JsonElement> ConditionsList( JsonElement root, string name )
   {
      return ResultJson.Child( root, "conditions" ) is JsonElement conditions && ResultJson.Child( conditions, name ) is { ValueKind: JsonValueKind.Array } array
         ? array.EnumerateArray().Where( i => i.ValueKind == JsonValueKind.Object ).ToList()
         : Enumerable.Empty<JsonElement>();
   }

   /// <summary>
   /// One timed pass of the run's conditions.
   /// </summary>
   /// <param name="pass">The pass object.</param>
   /// <returns>The facts.</returns>
   private static PassFacts ReadPass( JsonElement pass )
   {
      return new PassFacts
      {
         Target = ResultJson.Text( pass, "target" ) ?? string.Empty,
         Pass = ResultJson.Text( pass, "pass" ) ?? string.Empty,
         Governor = Known( ResultJson.Text( pass, "governor" ) ),
         BusyBox = ResultJson.Bool( pass, "busyBox" ) ?? false,
         BusyReason = ResultJson.Text( pass, "busyReason" ),
         StartUtc = ResultJson.Text( pass, "startUtc" ),
         EndUtc = ResultJson.Text( pass, "endUtc" ),
         EngineMhzMedian = ResultJson.Int( pass, "engineMhzMedian" ),
         ClientMhzMedian = ResultJson.Int( pass, "clientMhzMedian" ),
         CpuMedians = CpuMedians( pass ),
         ClockRead = ResultJson.Bool( pass, "clockRead" ),
         ClockOff = ResultJson.Bool( pass, "clockOff" ),
         OutsideLoadDuring = ResultJson.Number( pass, "outsideLoadDuring" ),
         ClientCpuMsPerSearch = ResultJson.Number( pass, "clientCpuMsPerSearch" ),
         EngineCpuMsPerSearch = ResultJson.Number( pass, "engineCpuMsPerSearch" ),
         EngineCpuNullReason = ResultJson.Text( pass, "engineCpuNullReason" ),
         DockerdCpuMsPerSearch = ResultJson.Number( pass, "dockerdCpuMsPerSearch" ),
         EngineCpusBusy = ResultJson.Number( pass, "engineCpusBusy" ),
         Searches = ResultJson.Int( pass, "searches" ),
      };
   }

   /// <summary>
   /// The per-CPU median clock of one pass (conditions.passes[].cpuMhz[].median), by CPU number.
   /// </summary>
   /// <param name="pass">The pass object.</param>
   /// <returns>Median MHz by CPU; empty when the pass recorded none.</returns>
   /// <exception cref="InvalidDataException">An entry lacks its cpu or median number.</exception>
   private static IReadOnlyDictionary<int, int> CpuMedians( JsonElement pass )
   {
      var medians = new SortedDictionary<int, int>();
      if( ResultJson.Child( pass, "cpuMhz" ) is not { ValueKind: JsonValueKind.Array } list )
      {
         return medians;
      }

      foreach( JsonElement cpu in list.EnumerateArray() )
      {
         int number = ResultJson.Int( cpu, "cpu" ) ?? throw new InvalidDataException( "a conditions.passes[].cpuMhz entry has no cpu number" );
         medians[number] = ResultJson.Int( cpu, "median" ) ?? throw new InvalidDataException( $"conditions.passes[].cpuMhz entry for CPU {number} has no median" );
      }

      return medians;
   }

   /// <summary>
   /// A text field of an object inside the top-level "conditions" object (conditions.build.commit).
   /// </summary>
   /// <param name="root">The results.json root.</param>
   /// <param name="block">Object name inside conditions.</param>
   /// <param name="name">Field name.</param>
   /// <returns>The text, or null when any part is absent.</returns>
   private static string? Nested( JsonElement root, string block, string name )
   {
      return InConditions( root, block ) is { ValueKind: JsonValueKind.Object } holder ? ResultJson.Text( holder, name ) : null;
   }

   /// <summary>
   /// A child of the top-level "conditions" object.
   /// </summary>
   /// <param name="root">The results.json root.</param>
   /// <param name="name">Child name.</param>
   /// <returns>The child, or null.</returns>
   private static JsonElement? InConditions( JsonElement root, string name )
   {
      return ResultJson.Child( root, "conditions" ) is { ValueKind: JsonValueKind.Object } conditions ? ResultJson.Child( conditions, name ) : null;
   }

   /// <summary>
   /// conditions.throttleByTarget per target.
   /// </summary>
   /// <param name="root">The results.json root.</param>
   /// <returns>The rises per target; empty when not recorded.</returns>
   private static IReadOnlyDictionary<string, RecordedThrottle> Throttles( JsonElement root )
   {
      var found = new Dictionary<string, RecordedThrottle>( StringComparer.Ordinal );
      if( InConditions( root, "throttleByTarget" ) is { ValueKind: JsonValueKind.Object } all )
      {
         foreach( JsonProperty target in all.EnumerateObject() )
         {
            found[target.Name] = target.Value.ValueKind == JsonValueKind.Object
               ? new RecordedThrottle( ResultJson.Long( target.Value, "coreRise" ), ResultJson.Long( target.Value, "packageRise" ) )
               : new RecordedThrottle( null, null );
         }
      }

      return found;
   }

   /// <summary>
   /// One engine of the run's conditions.
   /// </summary>
   /// <param name="engine">The engine object.</param>
   /// <returns>The facts.</returns>
   private static EnginePinFacts ReadEngine( JsonElement engine )
   {
      string? cap = CPU_CAP_NAMES.Select( name => ResultJson.Child( engine, name ) is JsonElement found ? AsText( found ) : null ).FirstOrDefault( text => text != null );
      return new EnginePinFacts( ResultJson.Text( engine, "target" ) ?? string.Empty, ResultJson.Text( engine, "hosting" ), CpuSet.Normalize( ResultJson.Text( engine, "cpus" ) ),
         ResultJson.Texts( engine, "problems" ) ?? Array.Empty<string>(), cap );
   }

   /// <summary>
   /// Null for a value the writer marked as not known.
   /// </summary>
   /// <param name="value">The text, or null.</param>
   /// <returns>The text, or null when it is null, empty or "unknown".</returns>
   private static string? Known( string? value )
   {
      return string.IsNullOrWhiteSpace( value ) || value.Trim().Equals( "unknown", StringComparison.OrdinalIgnoreCase ) ? null : value;
   }

   /// <summary>
   /// A whole number found under any of the names, or null.
   /// </summary>
   /// <param name="root">The results.json root.</param>
   /// <param name="names">Accepted spellings.</param>
   /// <returns>The number, or null.</returns>
   private static int? WholeNumber( JsonElement root, string[] names )
   {
      return Find( root, names ) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32( out int value ) ? value : null;
   }

   /// <summary>
   /// A number captured from the command line, recording where it came from.
   /// </summary>
   /// <param name="text">The command line, or null.</param>
   /// <param name="pattern">Pattern with the number in group 1.</param>
   /// <param name="field">Field name for the derived map.</param>
   /// <param name="source">Where the number came from.</param>
   /// <param name="derived">Receives the source.</param>
   /// <returns>The number, or null when the pattern did not match.</returns>
   private static int? FromText( string? text, Regex pattern, string field, string source, Dictionary<string, string> derived )
   {
      if( text != null && pattern.Match( text ) is { Success: true } m && int.TryParse( m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value ) )
      {
         derived[field] = source;
         return value;
      }

      return null;
   }

   /// <summary>
   /// The warm-up count from the method note ("untimed warm-up of 20 searches").
   /// </summary>
   /// <param name="notes">The run's notes, or null.</param>
   /// <param name="derived">Receives the source.</param>
   /// <returns>The number, or null.</returns>
   private static int? FromNotes( IReadOnlyList<string>? notes, Dictionary<string, string> derived )
   {
      foreach( string note in notes ?? Array.Empty<string>() )
      {
         if( WARMUP_IN_NOTE.Match( note ) is { Success: true } m && int.TryParse( m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value ) )
         {
            derived["warmupSearches"] = "from the method note";
            return value;
         }
      }

      return null;
   }

   /// <summary>
   /// The governor text: the recorded value, followed by the value at the end when it differs.
   /// </summary>
   /// <param name="root">The results.json root.</param>
   /// <returns>The text, or null when not recorded.</returns>
   private static string? ReadGovernor( JsonElement root )
   {
      string? start = Known( Find( root, GOVERNOR_NAMES ) is JsonElement g ? GovernorText( g ) : null );
      string? end = Known( Find( root, GOVERNOR_END_NAMES ) is JsonElement e ? GovernorText( e ) : null );
      return start != null && end != null && !string.Equals( start, end, StringComparison.OrdinalIgnoreCase ) ? $"{start}, then {end}" : start ?? end;
   }

   /// <summary>
   /// A governor element as text: a string as is, an object by its "during"-style property (else
   /// flattened), an array as its distinct values.
   /// </summary>
   /// <param name="element">The element.</param>
   /// <returns>The text, or null.</returns>
   private static string? GovernorText( JsonElement element )
   {
      if( element.ValueKind == JsonValueKind.Object )
      {
         foreach( string pick in GOVERNOR_PICK )
         {
            if( ResultJson.Child( element, pick ) is JsonElement chosen && AsText( chosen ) is string text )
            {
               return text;
            }
         }
      }

      return AsText( element );
   }

   /// <summary>
   /// The client CPUs, the engine CPUs and, when the partition was only free text, that text.
   /// </summary>
   /// <param name="root">The results.json root.</param>
   /// <returns>Canonical client list, canonical engine list, free text.</returns>
   private static (string? Client, string? Engine, string? Text) Cpus( JsonElement root )
   {
      string? client = Find( root, CLIENT_NAMES ) is JsonElement c ? CpuSet.Normalize( AsText( c ) ) : null;
      string? engine = Find( root, ENGINE_NAMES ) is JsonElement e ? CpuSet.Normalize( AsText( e ) ) : null;
      string? text = null;
      if( Find( root, PARTITION_NAMES ) is JsonElement partition )
      {
         ( string? pc, string? pe, string? pt ) = SplitPartition( partition );
         client ??= pc;
         engine ??= pe;
         text = client == null && engine == null ? pt : null;
      }

      return ( client, engine, text );
   }

   /// <summary>
   /// Splits a recorded partition into client and engine CPU lists. An object is read by key
   /// ("client", "engines", ...); a string by the words "client" and "engine" followed by a list.
   /// </summary>
   /// <param name="partition">The recorded partition.</param>
   /// <returns>Client list, engine list, free text (set when neither list could be read).</returns>
   private static (string? Client, string? Engine, string? Text) SplitPartition( JsonElement partition )
   {
      string? client = null;
      string? engine = null;
      if( partition.ValueKind == JsonValueKind.Object )
      {
         foreach( JsonProperty property in partition.EnumerateObject() )
         {
            string key = property.Name.ToLowerInvariant();
            string? value = CpuSet.Normalize( AsText( property.Value ) );
            client = key.Contains( "client" ) || key.Contains( "bench" ) ? value : client;
            engine = key.Contains( "engine" ) || key.Contains( "server" ) ? value : engine;
         }
      }
      else if( AsText( partition ) is string text )
      {
         client = CLIENT_IN_TEXT.Match( text ) is { Success: true } c ? CpuSet.Normalize( c.Groups[1].Value ) : null;
         engine = ENGINE_IN_TEXT.Match( text ) is { Success: true } e ? CpuSet.Normalize( e.Groups[1].Value ) : null;
      }

      return ( client, engine, client == null && engine == null ? AsText( partition ) : null );
   }

   /// <summary>
   /// Any scalar, array or object as short comparable text: strings, numbers and booleans as
   /// written, arrays as their items joined by commas, objects as sorted "key=value" pairs.
   /// </summary>
   /// <param name="element">The element.</param>
   /// <returns>The text, or null for null or empty.</returns>
   internal static string? AsText( JsonElement element )
   {
      string? text = element.ValueKind switch
      {
         JsonValueKind.String => element.GetString(),
         JsonValueKind.Number => element.GetRawText(),
         JsonValueKind.True => "true",
         JsonValueKind.False => "false",
         JsonValueKind.Array => string.Join( ",", element.EnumerateArray().Select( i => AsText( i ) ).OfType<string>() ),
         JsonValueKind.Object => ObjectText( element ),
         _ => null,
      };
      return string.IsNullOrWhiteSpace( text ) ? null : text.Trim();
   }

   /// <summary>
   /// An object as sorted "key=value" pairs joined by ", ".
   /// </summary>
   /// <param name="element">The object.</param>
   /// <returns>The text.</returns>
   private static string ObjectText( JsonElement element )
   {
      var text = new StringBuilder();
      foreach( JsonProperty property in element.EnumerateObject().OrderBy( p => p.Name, StringComparer.Ordinal ) )
      {
         if( AsText( property.Value ) is string value )
         {
            text.Append( text.Length == 0 ? string.Empty : ", " ).Append( property.Name ).Append( '=' ).Append( value );
         }
      }

      return text.ToString();
   }

   #endregion Private Methods
}

/// <summary>
/// How every timed pass was warmed up and checked, as the run's own method notes state it: how long and
/// how many searches the warm-up lasts at the pass's own concurrency, the settle trial and how closely it
/// must agree, the one extension, and the rehearsal before the first timed pass.
/// Why read from the run's recorded notes and never filled in by this program: the report once printed
/// "warm-up searches before each timed pass | 20" for a method that is time based (at least 15 s, a settle
/// trial, one extension of up to 120 s), because the only recorded number was the minimum count. The
/// numbers here are the ones the run wrote into its own notes (SearchRunner.DescribeMethod writes them
/// from the constants it runs with), so a change of method changes the report with no edit here. A part
/// the notes do not state is null and prints as "missing"; the notes themselves are carried verbatim
/// (<see cref="Recorded"/>) so the report can quote them whatever the wording.
/// An older run that recorded only "untimed warm-up of N searches" loads as <see cref="Legacy"/>: a count,
/// not a time based method.
/// </summary>
public sealed class WarmupMethod
{
   #region Data Members

   /// <summary>Start of the run's note about the rehearsal before the first timed pass.</summary>
   public const string REHEARSAL_NOTE_START = "Preparation, untimed";

   /// <summary>Start of the run's note about the warm-up and the settle check before every timed pass.</summary>
   public const string WARMUP_NOTE_START = "Warm-up and settle check";

   private const string MISSING = "missing";
   private const string NUMBER = @"\d+(?:\.\d+)?";

   private static readonly Regex WARMUP = new( $@"for at least (?<s>{NUMBER}) s and at least (?<n>\d+) searches \(at most (?<cap>{NUMBER}) s\)", RegexOptions.Compiled );
   private static readonly Regex WINDOW = new( $@"windows of at least (?<s>{NUMBER}) s and (?<n>\d+) searches", RegexOptions.Compiled );
   private static readonly Regex TRIAL = new( $@"then a (?<s>{NUMBER}) s trial of the same pass", RegexOptions.Compiled );
   private static readonly Regex TOLERANCE = new( $@"must lie within (?<p>{NUMBER})% of the warm-up's settled figure", RegexOptions.Compiled );
   private static readonly Regex SETTLE = new( $@"median of its last (?<n>\d+) windows, which must agree within (?<p>{NUMBER})%", RegexOptions.Compiled );
   private static readonly Regex EXTENSION = new( $@"extended once \(at least (?<min>{NUMBER}) s, until its windows agree, at most (?<cap>{NUMBER}) s\)", RegexOptions.Compiled );
   private static readonly Regex REHEARSAL = new( $@"a rehearsal of every pass type at its own concurrency for (?<s>{NUMBER}) s each", RegexOptions.Compiled );
   private static readonly Regex LEVEL = new( $@"its level test passes: the older and the newer half of its latest windows, (?<min>\d+) to (?<max>\d+) windows a half, each half read as the pass reads it \(the p50 of all its searches with one searcher, its searches per second with several\), agree within (?<p>{NUMBER})%", RegexOptions.Compiled );
   private static readonly Regex RANGE = new( $@"a second trial is taken, which must lie inside the range of the newer half's windows or within (?<p>{NUMBER})% of the newer half's figure", RegexOptions.Compiled );
   private static readonly Regex HOLD = new( $@"After the pass its own figure is held against the settled figure, within (?<p>{NUMBER})%", RegexOptions.Compiled );
   private static readonly Regex FLOOR = new( $@"two one-searcher p50s no more than (?<ms>{NUMBER}) ms apart agree whatever their percentage", RegexOptions.Compiled );
   private static readonly Regex LEGACY = new( @"untimed warm-up of (?<n>\d+) searches", RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>Shortest warm-up, seconds; null when the notes do not say.</summary>
   public double? MinSeconds { get; init; }

   /// <summary>Fewest searches in a warm-up; null when the notes do not say.</summary>
   public int? MinSearches { get; init; }

   /// <summary>Longest warm-up, seconds, before it stops waiting for the readings to agree; null when the notes do not say.</summary>
   public double? CapSeconds { get; init; }

   /// <summary>Shortest reading window, seconds; null when the notes do not say.</summary>
   public double? WindowSeconds { get; init; }

   /// <summary>Fewest searches in a reading window; null when the notes do not say.</summary>
   public int? WindowSearches { get; init; }

   /// <summary>Length of the settle trial, seconds; null when the notes do not say.</summary>
   public double? TrialSeconds { get; init; }

   /// <summary>How far, in percent, the trial's figure may lie from the warm-up's settled figure; null when the notes do not say.</summary>
   public double? TrialPercent { get; init; }

   /// <summary>How many of the last warm-up windows give the settled figure; null when the notes do not say.</summary>
   public int? SettleWindows { get; init; }

   /// <summary>How closely those windows must agree, in percent; null when the notes do not say.</summary>
   public double? SettlePercent { get; init; }

   /// <summary>Shortest extension of the warm-up, seconds; null when the notes do not say.</summary>
   public double? ExtensionMinSeconds { get; init; }

   /// <summary>Longest extension of the warm-up, seconds; null when the notes do not say.</summary>
   public double? ExtensionCapSeconds { get; init; }

   /// <summary>Length of the rehearsal of each pass type before the first timed pass, seconds; null when the notes do not say.</summary>
   public double? RehearsalSeconds { get; init; }

   /// <summary>True for an older run whose only note was "untimed warm-up of N searches": a count with no time, no trial and no extension.</summary>
   public bool Legacy { get; init; }

   /// <summary>True when the notes say a pass whose trial still disagrees flags its target as unsettled.</summary>
   public bool FlagsUnsettled { get; init; }

   /// <summary>Fewest windows in each half of the extension's level test; null when the notes do not say (runs before the level test).</summary>
   public int? LevelHalfMin { get; init; }

   /// <summary>Most windows in each half of the extension's level test; null when the notes do not say.</summary>
   public int? LevelHalfMax { get; init; }

   /// <summary>How closely the two halves of the level test must agree, in percent; null when the notes do not say.</summary>
   public double? LevelPercent { get; init; }

   /// <summary>How far, in percent, a second trial outside the newer half's window range may lie from the newer half's figure; null when the notes do not say.</summary>
   public double? RangePercent { get; init; }

   /// <summary>How far, in percent, a timed pass's own figure may lie from its settled figure; null when the notes do not say (runs before the hold check).</summary>
   public double? HoldPercent { get; init; }

   /// <summary>Difference in ms under which two one-searcher p50s agree in the level test, the second trial and the hold; null when the notes do not say (runs before the floor).</summary>
   public double? FloorMs { get; init; }

   /// <summary>The run's own method notes about the rehearsal and the warm-up, verbatim, in the order recorded.</summary>
   public List<string> Recorded { get; init; } = new();

   /// <summary>True when the warm-up is time based (the notes state a shortest warm-up in seconds).</summary>
   public bool TimeBased => MinSeconds.HasValue;

   /// <summary>
   /// One line that states the method in the run's own numbers, with "missing" for a part the notes did
   /// not state. It is the text consolidated.json carries for the summary page, and the text two runs must
   /// share to be merged.
   /// </summary>
   public string Description
   {
      get
      {
         if( Legacy )
         {
            return $"a fixed count of {Count( MinSearches )} searches before each timed pass (older run: no warm-up time, settle trial or extension recorded)";
         }

         return $"warm-up at the pass's own concurrency for at least {Seconds( MinSeconds )} and at least {Count( MinSearches )} searches (at most {Seconds( CapSeconds )}); "
            + $"then a {Seconds( TrialSeconds )} trial that must land within {Percent( TrialPercent )} of the settled figure; "
            + $"one extension of {Seconds( ExtensionMinSeconds )} to {Seconds( ExtensionCapSeconds )} if it does not{LevelDescription()}; "
            + $"rehearsal of every pass type for {Seconds( RehearsalSeconds )} before the first timed pass";
      }
   }

   /// <summary>
   /// The method as (item, value) rows for consolidated.md, every number from the run's notes and
   /// "missing" for a part the notes did not state.
   /// </summary>
   /// <returns>The rows.</returns>
   public IReadOnlyList<(string Item, string Value)> Rows()
   {
      if( Legacy )
      {
         return new[] { ( "warm-up before each timed pass", Description ) };
      }

      string unsettled = !FlagsUnsettled ? string.Empty
         : HoldPercent.HasValue ? "; a pass whose trial still disagrees, or whose own figure did not hold, flags its target as unsettled"
         : "; a pass whose trial still disagrees flags its target as unsettled";
      string level = LevelHalfMin.HasValue
         ? $"; the extension stops once the older and the newer half of its latest windows ({Count( LevelHalfMin )} to {Count( LevelHalfMax )} windows a half, each half read as the pass reads it) agree within {Percent( LevelPercent )}, judged on the windows that stopped it, "
            + $"and the second trial must lie inside the range of the newer half's windows or within {Percent( RangePercent )} of the newer half's figure"
         : string.Empty;
      string hold = HoldPercent.HasValue ? $"; after the pass its own figure must lie within {Percent( HoldPercent )} of the settled figure" : string.Empty;
      hold += FloorMs.HasValue ? $"; in the level test, the second trial and the hold, one-searcher p50s within {FloorMs.Value.ToString( "0.0##", CultureInfo.InvariantCulture )} ms of each other agree" : string.Empty;
      return new[]
      {
         ( "warm-up before each timed pass", $"time based: the pass's own search at the pass's own concurrency for at least {Seconds( MinSeconds )} and at least {Count( MinSearches )} searches (at most {Seconds( CapSeconds )}), read in windows of at least {Seconds( WindowSeconds )} and {Count( WindowSearches )} searches" ),
         ( "settle check before each timed pass", $"a {Seconds( TrialSeconds )} trial of the same pass must land within {Percent( TrialPercent )} of the warm-up's settled figure (the median of its last {Count( SettleWindows )} windows, which must agree within {Percent( SettlePercent )}); "
            + $"if not, the warm-up is extended once (at least {Seconds( ExtensionMinSeconds )}, at most {Seconds( ExtensionCapSeconds )}) and a second trial is taken{level}{hold}{unsettled}" ),
         ( "rehearsal before the first timed pass", $"every pass type at its own concurrency for {Seconds( RehearsalSeconds )} each, untimed" ),
      };
   }

   /// <summary>
   /// Reads the method from the run's notes.
   /// </summary>
   /// <param name="notes">The run's notes, or null.</param>
   /// <returns>The method, or null when the notes say nothing about a warm-up.</returns>
   public static WarmupMethod? From( IReadOnlyList<string>? notes )
   {
      IReadOnlyList<string> all = notes ?? Array.Empty<string>();
      List<string> mine = all.Where( n => n.StartsWith( REHEARSAL_NOTE_START, StringComparison.Ordinal ) || n.StartsWith( WARMUP_NOTE_START, StringComparison.Ordinal ) ).ToList();
      string text = string.Join( " ", mine );
      if( mine.Count > 0 && WARMUP.IsMatch( text ) )
      {
         Match warm = WARMUP.Match( text );
         Match window = WINDOW.Match( text );
         Match settle = SETTLE.Match( text );
         Match extension = EXTENSION.Match( text );
         return new WarmupMethod
         {
            MinSeconds = Number( warm, "s" ),
            MinSearches = Whole( warm, "n" ),
            CapSeconds = Number( warm, "cap" ),
            WindowSeconds = Number( window, "s" ),
            WindowSearches = Whole( window, "n" ),
            TrialSeconds = Number( TRIAL.Match( text ), "s" ),
            TrialPercent = Number( TOLERANCE.Match( text ), "p" ),
            SettleWindows = Whole( settle, "n" ),
            SettlePercent = Number( settle, "p" ),
            ExtensionMinSeconds = Number( extension, "min" ),
            ExtensionCapSeconds = Number( extension, "cap" ),
            RehearsalSeconds = Number( REHEARSAL.Match( text ), "s" ),
            FlagsUnsettled = text.Contains( "flags its target as unsettled", StringComparison.Ordinal ),
            LevelHalfMin = Whole( LEVEL.Match( text ), "min" ),
            LevelHalfMax = Whole( LEVEL.Match( text ), "max" ),
            LevelPercent = Number( LEVEL.Match( text ), "p" ),
            RangePercent = Number( RANGE.Match( text ), "p" ),
            HoldPercent = Number( HOLD.Match( text ), "p" ),
            FloorMs = Number( FLOOR.Match( text ), "ms" ),
            Recorded = mine,
         };
      }

      string? legacy = all.FirstOrDefault( n => LEGACY.IsMatch( n ) );
      return legacy == null ? null : new WarmupMethod { Legacy = true, MinSearches = Whole( LEGACY.Match( legacy ), "n" ), Recorded = new List<string> { legacy } };
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The level test, the second trial's range and the hold check for the one-line description,
   /// or nothing for a run that recorded none of them (so older runs keep their description, and
   /// runs judged by different rules do not share one and are not merged).
   /// </summary>
   /// <returns>"; the extension judged by its level test (5 to 10 windows a half, within 5%), its second trial inside the newer windows' range or within 10%; each timed pass held within 10% of its settled figure", or less.</returns>
   private string LevelDescription()
   {
      string level = LevelHalfMin.HasValue
         ? $"; the extension judged by its level test ({Count( LevelHalfMin )} to {Count( LevelHalfMax )} windows a half, within {Percent( LevelPercent )}), its second trial inside the newer windows' range or within {Percent( RangePercent )}"
         : string.Empty;
      string hold = HoldPercent.HasValue ? $"; each timed pass held within {Percent( HoldPercent )} of its settled figure" : string.Empty;
      string floor = FloorMs.HasValue ? $"; one-searcher p50s within {FloorMs.Value.ToString( "0.0##", CultureInfo.InvariantCulture )} ms agree in those checks" : string.Empty;
      return level + hold + floor;
   }

   /// <summary>
   /// A number captured by a group, or null when the pattern did not match.
   /// </summary>
   /// <param name="match">The match.</param>
   /// <param name="group">Group name.</param>
   /// <returns>The number, or null.</returns>
   private static double? Number( Match match, string group )
   {
      return match.Success && double.TryParse( match.Groups[group].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value ) ? value : null;
   }

   /// <summary>
   /// A whole number captured by a group, or null when the pattern did not match.
   /// </summary>
   /// <param name="match">The match.</param>
   /// <param name="group">Group name.</param>
   /// <returns>The number, or null.</returns>
   private static int? Whole( Match match, string group )
   {
      return match.Success && int.TryParse( match.Groups[group].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value ) ? value : null;
   }

   /// <summary>
   /// Seconds as "15 s", or "missing".
   /// </summary>
   /// <param name="seconds">Seconds, or null.</param>
   /// <returns>The text.</returns>
   private static string Seconds( double? seconds )
   {
      return seconds.HasValue ? seconds.Value.ToString( "0.##", CultureInfo.InvariantCulture ) + " s" : MISSING;
   }

   /// <summary>
   /// A percentage as "10%", or "missing".
   /// </summary>
   /// <param name="percent">Percent, or null.</param>
   /// <returns>The text.</returns>
   private static string Percent( double? percent )
   {
      return percent.HasValue ? percent.Value.ToString( "0.##", CultureInfo.InvariantCulture ) + "%" : MISSING;
   }

   /// <summary>
   /// A count as text, or "missing".
   /// </summary>
   /// <param name="count">The count, or null.</param>
   /// <returns>The text.</returns>
   private static string Count( int? count )
   {
      return count.HasValue ? count.Value.ToString( CultureInfo.InvariantCulture ) : MISSING;
   }

   #endregion Private Methods
}

/// <summary>
/// One timed pass of a run, as its conditions recorded it (conditions.passes[]). Null means the run did not record the field.
/// Why so many fields: the clock check is recomputed from the per-CPU medians, the busy flag comes from the pass's own outside load,
/// and the why table's measured costs (engine and client CPU per search) are read here.
/// </summary>
public sealed class PassFacts
{
   #region Public Methods

   /// <summary>Target name.</summary>
   public string Target { get; init; } = string.Empty;

   /// <summary>Pass name, e.g. "default@8".</summary>
   public string Pass { get; init; } = string.Empty;

   /// <summary>CPU governor at the end of the pass, or null when not known.</summary>
   public string? Governor { get; init; }

   /// <summary>True when the run flagged the box busy before or during the pass.</summary>
   public bool BusyBox { get; init; }

   /// <summary>Why it was called busy, or null.</summary>
   public string? BusyReason { get; init; }

   /// <summary>When the timed window opened (UTC text).</summary>
   public string? StartUtc { get; init; }

   /// <summary>When the timed window closed (UTC text).</summary>
   public string? EndUtc { get; init; }

   /// <summary>The run's own median of the engine CPUs' per-CPU medians, MHz.</summary>
   public int? EngineMhzMedian { get; init; }

   /// <summary>The run's own median of the client CPUs' per-CPU medians, MHz.</summary>
   public int? ClientMhzMedian { get; init; }

   /// <summary>Median MHz per CPU over the pass; empty when not recorded.</summary>
   public IReadOnlyDictionary<int, int> CpuMedians { get; init; } = new Dictionary<int, int>();

   /// <summary>v8: true when every CPU group of the partition had a clock median during the pass.</summary>
   public bool? ClockRead { get; init; }

   /// <summary>v8: true when the pass's clock was off the pinned clock by the run's own rule; null when not read.</summary>
   public bool? ClockOff { get; init; }

   /// <summary>CPUs used by processes outside the client and the engine under test during the pass.</summary>
   public double? OutsideLoadDuring { get; init; }

   /// <summary>The client's own CPU per search, ms.</summary>
   public double? ClientCpuMsPerSearch { get; init; }

   /// <summary>v8: the engine cgroups' CPU per search, ms; null for an embedded target or when not measured.</summary>
   public double? EngineCpuMsPerSearch { get; init; }

   /// <summary>v8: why <see cref="EngineCpuMsPerSearch"/> is null.</summary>
   public string? EngineCpuNullReason { get; init; }

   /// <summary>v8: dockerd's CPU per search, ms, recorded apart from the engine's.</summary>
   public double? DockerdCpuMsPerSearch { get; init; }

   /// <summary>v8: engine cgroup CPU seconds divided by the pass's seconds.</summary>
   public double? EngineCpusBusy { get; init; }

   /// <summary>Completed searches of the pass.</summary>
   public int? Searches { get; init; }

   #endregion Public Methods
}

/// <summary>
/// One engine of a run, as its conditions recorded it.
/// </summary>
/// <param name="Target">Target name.</param>
/// <param name="Hosting">compose, always-on or embedded.</param>
/// <param name="Cpus">CPUs the engine was held to, canonical; null when it was not pinned.</param>
/// <param name="Problems">What went wrong holding the engine to its CPUs; any entry means it was not fully pinned.</param>
/// <param name="CpuCap">A CPU limit the engine puts on itself (e.g. "cpu_count 2"), as the run recorded it; null when not recorded.</param>
public sealed record EnginePinFacts( string Target, string? Hosting, string? Cpus, IReadOnlyList<string> Problems, string? CpuCap = null );

/// <summary>
/// The thermal throttle counters' rise over one target, as the run recorded it (conditions.throttleByTarget).
/// Why kept: the counters only say rise or no rise, and a rise during a target is a flag on its rows.
/// </summary>
/// <param name="CoreRise">Rise of the core counters, or null when not recorded.</param>
/// <param name="PackageRise">Rise of the package counters, or null when not recorded.</param>
public sealed record RecordedThrottle( long? CoreRise, long? PackageRise );
