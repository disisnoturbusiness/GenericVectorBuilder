using System.Text.Json;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// The machine conditions a benchmark was measured under, as the summary shows them in one line:
/// CPU governor, CPU partition (which CPUs the client and the engines could use), build
/// configuration, warm-up and exact-mode settings. A null field was not recorded.
/// Why the page states them: the CPU governor alone moved single-searcher QPS by a factor of two,
/// so numbers shown without the conditions they were measured under mislead.
/// Why a separate reader from the Bench tool's: the web app does not reference the benchmark
/// project, so this reads the same JSON with the same accepted spellings; BenchResultsSummaryTests
/// feeds both the same files.
/// </summary>
/// <param name="Governor">CPU frequency governor, or null.</param>
/// <param name="Partition">CPU partition as text ("client 0,4; engines 1-3,5-7"), or null.</param>
/// <param name="Build">Build configuration of the benchmark client, or null.</param>
/// <param name="WarmupSearches">Warm-up searches before each timed pass, as text, or null.</param>
/// <param name="ExactSeconds">Seconds of exact mode, as text, or null.</param>
public sealed record BenchConditions( string? Governor, string? Partition, string? Build, string? WarmupSearches, string? ExactSeconds )
{
   #region Data Members

   /// <summary>The governor a timed run must be on.</summary>
   public const string GOVERNOR_TARGET = "performance";

   private static readonly string[] CONTAINERS = { "conditions", "machine", "method", "environment", "options", "settings" };
   private static readonly Regex BUILD_IN_PATH = new( @"[/\\]bin[/\\](Debug|Release)[/\\]", RegexOptions.Compiled | RegexOptions.IgnoreCase );
   private static readonly Regex WARMUP_IN_NOTE = new( @"untimed warm-up of (\d+) searches", RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>Nothing recorded: what an old result file gives.</summary>
   public static BenchConditions None { get; } = new( null, null, null, null, null );

   /// <summary>True when none of governor, partition and build was recorded.</summary>
   public bool NoneRecorded => Governor == null && Partition == null && Build == null;

   /// <summary>True when the governor is known and is not "performance".</summary>
   public bool GovernorIsWrong => Governor != null && !string.Equals( Governor.Trim(), GOVERNOR_TARGET, StringComparison.OrdinalIgnoreCase );

   /// <summary>True when the build is known and is not Release.</summary>
   public bool BuildIsWrong => Build != null && !string.Equals( Build.Trim(), "Release", StringComparison.OrdinalIgnoreCase );

   /// <summary>
   /// Reads the conditions from one run's results.json root. A field a run did not write stays
   /// null; the build is recovered from the binary path in the command line, the warm-up count
   /// from the method note, as the Bench tool's consolidate command does.
   /// </summary>
   /// <param name="root">The results.json root object.</param>
   /// <returns>The conditions.</returns>
   public static BenchConditions FromRun( JsonElement root )
   {
      string? commandLine = Text( root, "commandLine" );
      string? build = Known( Find( root, "buildConfiguration", "buildConfig", "build", "configuration" ) );
      if( build == null && commandLine != null && BUILD_IN_PATH.Match( commandLine ) is { Success: true } path )
      {
         build = path.Groups[1].Value;
      }

      string? start = Known( Find( root, "governor", "cpuGovernor", "scalingGovernor" ) );
      string? end = Known( Find( root, "governorAtEnd", "governorEnd", "governorAfter" ) );
      string? governor = start != null && end != null && !string.Equals( start, end, StringComparison.OrdinalIgnoreCase ) ? $"{start}, then {end}" : start ?? end;
      governor = Known( governor );
      return new BenchConditions( governor, RunPartition( root ), build is null ? null : char.ToUpperInvariant( build[0] ) + build[1..].ToLowerInvariant(),
         Find( root, "warmupSearches", "warmup", "warmupCount" ) ?? WarmupFromNotes( root ), Find( root, "exactSeconds" ) ?? FromCommand( commandLine, "--exact-seconds" ) );
   }

   /// <summary>
   /// Reads the conditions from the "settings" object of a consolidated.json the consolidate
   /// command wrote.
   /// </summary>
   /// <param name="settings">The settings object.</param>
   /// <returns>The conditions.</returns>
   public static BenchConditions FromSettings( JsonElement settings )
   {
      return new BenchConditions( Text( settings, "governor" ), Text( settings, "cpuPartition" ), Text( settings, "buildConfiguration" ),
         Scalar( settings, "warmupSearches" ), Scalar( settings, "exactSeconds" ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The CPU partition of a run: client and engine lists from their own fields, or from a
   /// "cpuPartition" value, written as "client A; engines B".
   /// </summary>
   /// <param name="root">The results.json root.</param>
   /// <returns>The text, or null when nothing was recorded.</returns>
   private static string? RunPartition( JsonElement root )
   {
      string? client = Find( root, "clientCpus", "clientCpuList", "clientAffinity", "benchCpus", "benchmarkCpus" );
      string? engine = Find( root, "engineCpus", "engineCpuList", "engineAffinity", "enginesCpus", "serverCpus" );
      if( client != null || engine != null )
      {
         return $"client {client ?? "not recorded"}; engines {engine ?? "not recorded"}";
      }

      return Find( root, "cpuPartition", "partition", "cpuAffinity", "affinity", "pinning" );
   }

   /// <summary>
   /// Null for a value the writer marked as not known.
   /// </summary>
   /// <param name="value">The text, or null.</param>
   /// <returns>The text, or null when it is null or "unknown".</returns>
   private static string? Known( string? value )
   {
      return value == null || value.Trim().Equals( "unknown", StringComparison.OrdinalIgnoreCase ) ? null : value;
   }

   /// <summary>
   /// The warm-up count in the method note.
   /// </summary>
   /// <param name="root">The results.json root.</param>
   /// <returns>The number as text, or null.</returns>
   private static string? WarmupFromNotes( JsonElement root )
   {
      if( ChildOf( root, "notes" ) is not { ValueKind: JsonValueKind.Array } notes )
      {
         return null;
      }

      foreach( JsonElement note in notes.EnumerateArray().Where( n => n.ValueKind == JsonValueKind.String ) )
      {
         if( WARMUP_IN_NOTE.Match( note.GetString()! ) is { Success: true } m )
         {
            return m.Groups[1].Value;
         }
      }

      return null;
   }

   /// <summary>
   /// The number after a command-line option such as "--exact-seconds 60".
   /// </summary>
   /// <param name="commandLine">The command line, or null.</param>
   /// <param name="option">The option name.</param>
   /// <returns>The number as text, or null.</returns>
   private static string? FromCommand( string? commandLine, string option )
   {
      return commandLine != null && Regex.Match( commandLine, Regex.Escape( option ) + @"[ =](\d+)" ) is { Success: true } m ? m.Groups[1].Value : null;
   }

   /// <summary>
   /// The first value found under any of the names, at the top of the file or inside a container.
   /// </summary>
   /// <param name="root">The results.json root.</param>
   /// <param name="names">Accepted spellings, preferred first.</param>
   /// <returns>The value as text, or null.</returns>
   private static string? Find( JsonElement root, params string[] names )
   {
      foreach( string name in names )
      {
         if( Scalar( root, name ) is string top )
         {
            return top;
         }
      }

      foreach( string container in CONTAINERS )
      {
         if( ChildOf( root, container ) is not { ValueKind: JsonValueKind.Object } inside )
         {
            continue;
         }

         foreach( string name in names )
         {
            if( Scalar( inside, name ) is string found )
            {
               return found;
            }
         }
      }

      return null;
   }

   /// <summary>
   /// A property of an object, matched ignoring case; null when absent or JSON null.
   /// </summary>
   /// <param name="parent">The object.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The value, or null.</returns>
   private static JsonElement? ChildOf( JsonElement parent, string name )
   {
      if( parent.ValueKind != JsonValueKind.Object )
      {
         return null;
      }

      foreach( JsonProperty property in parent.EnumerateObject() )
      {
         if( string.Equals( property.Name, name, StringComparison.OrdinalIgnoreCase ) )
         {
            return property.Value.ValueKind == JsonValueKind.Null ? null : property.Value;
         }
      }

      return null;
   }

   /// <summary>
   /// A string property, or null.
   /// </summary>
   /// <param name="parent">The object.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The text, or null.</returns>
   private static string? Text( JsonElement parent, string name )
   {
      return ChildOf( parent, name ) is { ValueKind: JsonValueKind.String } v && !string.IsNullOrWhiteSpace( v.GetString() ) ? v.GetString()!.Trim() : null;
   }

   /// <summary>
   /// A string, number, boolean, array or object property as short text, or null.
   /// </summary>
   /// <param name="parent">The object.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The text, or null.</returns>
   private static string? Scalar( JsonElement parent, string name )
   {
      return ChildOf( parent, name ) is JsonElement v ? AsText( v ) : null;
   }

   /// <summary>
   /// Any JSON value as short comparable text: scalars as written, arrays joined by commas,
   /// objects as sorted "key=value" pairs.
   /// </summary>
   /// <param name="value">The value.</param>
   /// <returns>The text, or null when empty.</returns>
   private static string? AsText( JsonElement value )
   {
      string? text = value.ValueKind switch
      {
         JsonValueKind.String => value.GetString(),
         JsonValueKind.Number => value.GetRawText(),
         JsonValueKind.True => "true",
         JsonValueKind.False => "false",
         JsonValueKind.Array => string.Join( ",", value.EnumerateArray().Select( AsText ).OfType<string>() ),
         JsonValueKind.Object => string.Join( ", ", value.EnumerateObject().OrderBy( p => p.Name, StringComparer.Ordinal ).Select( p => AsText( p.Value ) is string t ? $"{p.Name}={t}" : null ).OfType<string>() ),
         _ => null,
      };
      return string.IsNullOrWhiteSpace( text ) ? null : text.Trim();
   }

   #endregion Private Methods
}
