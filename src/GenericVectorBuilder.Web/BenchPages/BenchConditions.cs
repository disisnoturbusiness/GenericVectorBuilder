using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// The machine conditions one run was measured under, as a run page shows them: CPU governor, CPU
/// partition (which CPUs the client and the engines could use), build configuration, warm-up and
/// exact-mode settings, the machine's own fields and the clock block the run recorded. A null field was
/// not recorded.
/// Why the page states them: the CPU governor alone moved single-searcher QPS by a factor of two, so
/// numbers shown without the conditions they were measured under mislead.
/// Why a separate reader from the Bench tool's: the web app does not reference the benchmark project, so
/// this reads the same JSON with the same accepted spellings.
/// </summary>
/// <param name="Governor">CPU frequency governor, or null.</param>
/// <param name="Partition">CPU partition as text ("client 0,4; engines 1-3,5-7"), or null.</param>
/// <param name="Build">Build configuration of the benchmark client, or null.</param>
/// <param name="WarmupMethod">The first sentence of the run's own note on its warm-up, as the run wrote it, or null when no note describes it.</param>
/// <param name="WarmupField">The value of the run's warmupSearches field, as text, or null. A field is a parameter, not a description: the warm-up of a run can be timed
/// (at least some seconds and at least some searches), so the page prints this value only beside the statement that no note describes the method.</param>
/// <param name="ExactSeconds">Seconds of exact mode, as text, or null.</param>
/// <param name="Machine">The run's machine fields (CPU, logical CPUs, RAM, OS) as label and value pairs, in that order; empty when the run recorded none.</param>
/// <param name="Clock">The run's clock block (conditions.clock) as field and value pairs in file order; empty for a run that recorded none.</param>
/// <param name="ClockNote">For a run with no clock block: the clause of its own notes that states the clock pin, or null when no note states one.</param>
/// <param name="NoClockPinRecorded">True for a run with no clock block and no clock clause in its notes whose notes say machine control was on: its page says that no pin is recorded.</param>
/// <param name="CodeCommit">The commit the benchmark binary was built from (conditions.build.commit), or null when the run did not record it.</param>
public sealed record BenchConditions( string? Governor, string? Partition, string? Build, string? WarmupMethod, string? WarmupField, string? ExactSeconds,
   IReadOnlyList<BenchPair> Machine, IReadOnlyList<BenchPair> Clock, string? ClockNote = null, bool NoClockPinRecorded = false, string? CodeCommit = null )
{
   #region Data Members

   /// <summary>The governor a timed run must be on.</summary>
   public const string GOVERNOR_TARGET = "performance";

   /// <summary>Most characters of a run's own note the page prints in one line; a longer sentence is cut and ends in an ellipsis (the whole note is in the raw file).</summary>
   public const int MAX_NOTE_CHARS = 700;

   /// <summary>The words that open the clause of a run's notes that states the clock pin.</summary>
   public const string CLOCK_CLAUSE = "CPU clock pinned for the run:";

   /// <summary>The words that open the note of a run that had machine control on.</summary>
   public const string MACHINE_CONTROL_NOTE = "Machine control on";

   private static readonly string[] CONTAINERS = { "conditions", "machine", "method", "environment", "options", "settings" };
   private static readonly Regex BUILD_IN_PATH = new( @"[/\\]bin[/\\](Debug|Release)[/\\]", RegexOptions.Compiled | RegexOptions.IgnoreCase );
   private static readonly Regex SENTENCE_END = new( @"\.(?= [A-Z])", RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>Nothing recorded: what an old result file gives.</summary>
   public static BenchConditions None { get; } = new( null, null, null, null, null, null, Array.Empty<BenchPair>(), Array.Empty<BenchPair>() );

   /// <summary>True when none of governor, partition and build was recorded.</summary>
   public bool NoneRecorded => Governor == null && Partition == null && Build == null;

   /// <summary>True when the governor is known and is not "performance".</summary>
   public bool GovernorIsWrong => Governor != null && !string.Equals( Governor.Trim(), GOVERNOR_TARGET, StringComparison.OrdinalIgnoreCase );

   /// <summary>True when the build is known and is not Release.</summary>
   public bool BuildIsWrong => Build != null && !string.Equals( Build.Trim(), "Release", StringComparison.OrdinalIgnoreCase );

   /// <summary>
   /// Reads the conditions from one run's results.json root. A field a run did not write stays
   /// null; the build is recovered from the binary path in the command line. The warm-up is the run's own note
   /// on it, never a count: a run's warm-up can be timed, and a count printed beside the word "searches" says
   /// a method the run did not use.
   /// </summary>
   /// <param name="root">The results.json root object.</param>
   /// <returns>The conditions.</returns>
   public static BenchConditions FromRun( JsonElement root )
   {
      string? commandLine = Text( root, "commandLine" );
      string? build = Known( Find( root, true, "buildConfiguration", "buildConfig", "build", "configuration" ) );
      if( build == null && commandLine != null && BUILD_IN_PATH.Match( commandLine ) is { Success: true } path )
      {
         build = path.Groups[1].Value;
      }

      string? start = Known( Find( root, "governor", "cpuGovernor", "scalingGovernor" ) );
      string? end = Known( Find( root, "governorAtEnd", "governorEnd", "governorAfter" ) );
      string? governor = start != null && end != null && !string.Equals( start, end, StringComparison.OrdinalIgnoreCase ) ? $"{start}, then {end}" : start ?? end;
      governor = Known( governor );
      IReadOnlyList<BenchPair> clock = ClockFields( root );
      string? clockNote = clock.Count == 0 ? ClockFromNotes( root ) : null;
      return new BenchConditions( governor, RunPartition( root ), build is null ? null : char.ToUpperInvariant( build[0] ) + build[1..].ToLowerInvariant(),
         WarmupFromNotes( root ), Find( root, "warmupSearches", "warmup", "warmupCount" ), Find( root, "exactSeconds" ) ?? FromCommand( commandLine, "--exact-seconds" ),
         MachineFields( root ), clock, clockNote, clock.Count == 0 && clockNote == null && MachineControlWasOn( root ), CodeCommitOf( root ) );
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
   /// The run's own note on its warm-up: the first note that opens with "Warm-up", else the first note that mentions a warm-up, cut after its first sentence.
   /// Why the run's words and not a figure: the note says whether the warm-up was a count of searches or a time with a count as the floor.
   /// </summary>
   /// <param name="root">The results.json root.</param>
   /// <returns>The sentence as the run wrote it, or null when no note describes a warm-up.</returns>
   private static string? WarmupFromNotes( JsonElement root )
   {
      List<string> notes = NoteTexts( root );
      string? note = notes.FirstOrDefault( n => n.StartsWith( "Warm-up", StringComparison.OrdinalIgnoreCase ) )
         ?? notes.FirstOrDefault( n => n.Contains( "warm-up", StringComparison.OrdinalIgnoreCase ) );
      return note == null ? null : FirstSentence( note );
   }

   /// <summary>
   /// For a run with no clock block: the clause of its notes that states the clock pin. Why: a run page for a run measured with the clock free (turbo on)
   /// and one measured with it pinned must not read alike.
   /// </summary>
   /// <param name="root">The results.json root.</param>
   /// <returns>The clause up to its first semicolon, or null when no note states a pin.</returns>
   private static string? ClockFromNotes( JsonElement root )
   {
      foreach( string note in NoteTexts( root ) )
      {
         int at = note.IndexOf( CLOCK_CLAUSE, StringComparison.Ordinal );
         if( at >= 0 )
         {
            string clause = note[at..];
            int cut = clause.IndexOf( ';' );
            return Cap( cut > 0 ? clause[..cut] : FirstSentence( clause ) );
         }
      }

      return null;
   }

   /// <summary>
   /// True when a note of the run opens with the words that say machine control was on.
   /// </summary>
   /// <param name="root">The results.json root.</param>
   /// <returns>True when the run's notes say so.</returns>
   private static bool MachineControlWasOn( JsonElement root )
   {
      return NoteTexts( root ).Any( n => n.StartsWith( MACHINE_CONTROL_NOTE, StringComparison.Ordinal ) );
   }

   /// <summary>
   /// The commit the benchmark binary was built from: conditions.build.commit when the run recorded it.
   /// </summary>
   /// <param name="root">The results.json root.</param>
   /// <returns>The commit text, or null.</returns>
   private static string? CodeCommitOf( JsonElement root )
   {
      return ChildOf( root, "conditions" ) is { ValueKind: JsonValueKind.Object } conditions && ChildOf( conditions, "build" ) is { ValueKind: JsonValueKind.Object } build ? Text( build, "commit" ) : null;
   }

   /// <summary>
   /// The run's notes that are text, in file order.
   /// </summary>
   /// <param name="root">The results.json root.</param>
   /// <returns>The notes; none when the run has no notes list.</returns>
   private static List<string> NoteTexts( JsonElement root )
   {
      return ChildOf( root, "notes" ) is { ValueKind: JsonValueKind.Array } notes
         ? notes.EnumerateArray().Where( n => n.ValueKind == JsonValueKind.String ).Select( n => n.GetString()!.Trim() ).Where( n => n.Length > 0 ).ToList()
         : new List<string>();
   }

   /// <summary>
   /// A note up to the end of its first sentence (a full stop followed by a space and a capital letter, or the end of the text).
   /// </summary>
   /// <param name="note">The note.</param>
   /// <returns>The sentence, capped at <see cref="MAX_NOTE_CHARS"/>.</returns>
   private static string FirstSentence( string note )
   {
      Match end = SENTENCE_END.Match( note );
      return Cap( end.Success ? note[..( end.Index + 1 )] : note );
   }

   /// <summary>
   /// A text cut to <see cref="MAX_NOTE_CHARS"/> characters with an ellipsis when it is longer.
   /// </summary>
   /// <param name="text">The text.</param>
   /// <returns>The text, or its start and an ellipsis.</returns>
   private static string Cap( string text )
   {
      return text.Length <= MAX_NOTE_CHARS ? text : text[..MAX_NOTE_CHARS] + "...";
   }

   /// <summary>
   /// The machine fields of a run as label and value pairs: CPU, logical CPUs, RAM, OS (those the run recorded).
   /// </summary>
   /// <param name="root">The results.json root.</param>
   /// <returns>The pairs, none when the run has no machine block.</returns>
   private static IReadOnlyList<BenchPair> MachineFields( JsonElement root )
   {
      var pairs = new List<BenchPair>();
      if( ChildOf( root, "machine" ) is not { ValueKind: JsonValueKind.Object } machine )
      {
         return pairs;
      }

      foreach( ( string field, string label ) in new[] { ( "cpu", "CPU" ), ( "logicalCpus", "Logical CPUs" ), ( "ramGiB", "RAM (GiB)" ), ( "os", "OS" ) } )
      {
         if( Scalar( machine, field ) is string value )
         {
            pairs.Add( new BenchPair( label, value ) );
         }
      }

      return pairs;
   }

   /// <summary>
   /// The clock block a v8 run recorded (conditions.clock), field by field in file order; objects inside it are
   /// written as their own members.
   /// </summary>
   /// <param name="root">The results.json root.</param>
   /// <returns>The pairs, none for a run that recorded no clock block.</returns>
   private static IReadOnlyList<BenchPair> ClockFields( JsonElement root )
   {
      return ChildOf( root, "conditions" ) is { ValueKind: JsonValueKind.Object } conditions && ChildOf( conditions, "clock" ) is { ValueKind: JsonValueKind.Object } clock
         ? clock.EnumerateObject().Select( p => new BenchPair( p.Name, AsText( p.Value ) ?? "not read" ) ).ToList()
         : Array.Empty<BenchPair>();
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
      return Find( root, false, names );
   }

   /// <summary>
   /// The first value found under any of the names, at the top of the file or inside a container.
   /// </summary>
   /// <param name="root">The results.json root.</param>
   /// <param name="stringsOnly">True to accept a JSON string only, so an object under the name (a build block with a commit in it) is not read as the value.</param>
   /// <param name="names">Accepted spellings, preferred first.</param>
   /// <returns>The value as text, or null.</returns>
   private static string? Find( JsonElement root, bool stringsOnly, params string[] names )
   {
      foreach( string name in names )
      {
         if( ValueOf( root, name, stringsOnly ) is string top )
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
            if( ValueOf( inside, name, stringsOnly ) is string found )
            {
               return found;
            }
         }
      }

      return null;
   }

   /// <summary>
   /// A property as text: any scalar, array or object when strings are not required, a non-empty JSON string otherwise.
   /// </summary>
   /// <param name="parent">The object.</param>
   /// <param name="name">Property name.</param>
   /// <param name="stringsOnly">True to accept a JSON string only.</param>
   /// <returns>The text, or null.</returns>
   private static string? ValueOf( JsonElement parent, string name, bool stringsOnly )
   {
      return stringsOnly ? Text( parent, name ) : Scalar( parent, name );
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
