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
   private static readonly string[] BUILD_NAMES = { "buildConfiguration", "buildConfig", "build", "configuration" };
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
   public IReadOnlyList<EngineFacts> Engines { get; init; } = Array.Empty<EngineFacts>();

   /// <summary>Which fields were derived instead of recorded, by field name, with the source ("from the command line path").</summary>
   public IReadOnlyDictionary<string, string> Derived { get; init; } = new Dictionary<string, string>();

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
         LogicalCpus = WholeNumber( root, CPU_NAMES ),
         LoadAverage = Find( root, new[] { "loadAverage" } ) is JsonElement l ? AsText( l ) : null,
         MachineControl = Find( root, new[] { "machineControl" } ) is JsonElement m ? AsText( m ) : null,
         Passes = ConditionsList( root, "passes" ).Select( ReadPass ).ToList(),
         Engines = ConditionsList( root, "engines" ).Select( ReadEngine ).ToList(),
         Derived = derived,
      };
   }

   /// <summary>
   /// True when the 1-minute load average is above the number of logical CPUs: work was queueing
   /// for a CPU, so every latency is inflated by an unknown amount.
   /// </summary>
   /// <param name="loadAverage">"1-min 5-min 15-min", or null.</param>
   /// <param name="logicalCpus">Logical CPUs of the box, or null.</param>
   /// <returns>True when the box was overloaded; false when either input is unknown.</returns>
   public static bool IsBusy( string? loadAverage, int? logicalCpus )
   {
      return logicalCpus.HasValue
         && double.TryParse( loadAverage?.Split( ' ' )[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double load )
         && load > logicalCpus.Value;
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
      return new PassFacts( ResultJson.Text( pass, "target" ) ?? string.Empty, ResultJson.Text( pass, "pass" ) ?? string.Empty, Known( ResultJson.Text( pass, "governor" ) ),
         ResultJson.Bool( pass, "busyBox" ) ?? false, ResultJson.Text( pass, "busyReason" ) );
   }

   /// <summary>
   /// One engine of the run's conditions.
   /// </summary>
   /// <param name="engine">The engine object.</param>
   /// <returns>The facts.</returns>
   private static EngineFacts ReadEngine( JsonElement engine )
   {
      string? cap = CPU_CAP_NAMES.Select( name => ResultJson.Child( engine, name ) is JsonElement found ? AsText( found ) : null ).FirstOrDefault( text => text != null );
      return new EngineFacts( ResultJson.Text( engine, "target" ) ?? string.Empty, ResultJson.Text( engine, "hosting" ), CpuSet.Normalize( ResultJson.Text( engine, "cpus" ) ),
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
/// One timed pass of a run, as its conditions recorded it.
/// </summary>
/// <param name="Target">Target name.</param>
/// <param name="Pass">Pass name, e.g. "default@8".</param>
/// <param name="Governor">CPU governor at the end of the pass, or null when not known.</param>
/// <param name="BusyBox">True when the box was busy before or during the pass.</param>
/// <param name="BusyReason">Why it was called busy, or null.</param>
public sealed record PassFacts( string Target, string Pass, string? Governor, bool BusyBox, string? BusyReason );

/// <summary>
/// One engine of a run, as its conditions recorded it.
/// </summary>
/// <param name="Target">Target name.</param>
/// <param name="Hosting">compose, always-on or embedded.</param>
/// <param name="Cpus">CPUs the engine was held to, canonical; null when it was not pinned.</param>
/// <param name="Problems">What went wrong holding the engine to its CPUs; any entry means it was not fully pinned.</param>
/// <param name="CpuCap">A CPU limit the engine puts on itself (e.g. "cpu_count 2"), as the run recorded it; null when not recorded.</param>
public sealed record EngineFacts( string Target, string? Hosting, string? Cpus, IReadOnlyList<string> Problems, string? CpuCap = null );
