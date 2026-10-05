using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The v6 review's report blockers in the consolidate command: the warm-up is described from what the
/// runs recorded about it (a time based warm-up, a settle trial, one extension, a rehearsal) and never
/// as a bare count of searches; every engine's recorded setup (description, non-default settings, the
/// SHA-256 of the files it was configured from, durability) is printed, and runs whose setup differs are
/// refused for that target instead of being merged; and the headline tables' engine notes column carries
/// every flag of the engine, the unsettled flag first among the ones that matter.
/// Why synthetic folders: the numbers asserted here are chosen to differ from the real method's (12 s
/// where the real warm-up is 15 s), so a report that printed fixed text would fail.
/// </summary>
public sealed partial class ConsolidateTests
{
   #region Data Members

   private const string FILES_NOTE = "Engine files (SHA-256, first 12 hex digits): [clickhouse-config/gvb-system-logs.xml 0123456789ab; clickhouse.compose.yaml fedcba987654]. This run created the engine from these files.";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The warm-up rows carry the numbers of the runs' own notes (12 s, 40 searches, 90 s, a 4 s trial
   /// within 8%, an extension of 25 to 80 s, a 45 s rehearsal), the notes are quoted verbatim under their
   /// own heading, consolidated.json carries the parsed method, and no row calls the warm-up a count.
   /// </summary>
   [Fact]
   public void WarmupMethod_IsDescribedFromTheRunsOwnNotes_NotAsACount()
   {
      string[] notes = MethodNotes( warm: 12, searches: 40, warmCap: 90, trial: 4, tolerance: 8, extensionMin: 25, extensionCap: 80, rehearsal: 45 );
      WriteClean( "r1", "2026-10-05T10:00:00Z", Notes( notes ), Target( "sql", 2, 900, 500 ) );
      WriteClean( "r2", "2026-10-05T11:00:00Z", Notes( notes ), Target( "sql", 2, 880, 500 ) );

      JsonElement json = Consolidate( "--targets", "sql", Path.Combine( _root, "r?" ) );

      JsonElement method = json.GetProperty( "settings" ).GetProperty( "warmupMethod" );
      Assert.Equal( 12, method.GetProperty( "minSeconds" ).GetDouble() );
      Assert.Equal( 40, method.GetProperty( "minSearches" ).GetInt32() );
      Assert.Equal( 90, method.GetProperty( "capSeconds" ).GetDouble() );
      Assert.Equal( 4, method.GetProperty( "trialSeconds" ).GetDouble() );
      Assert.Equal( 8, method.GetProperty( "trialPercent" ).GetDouble() );
      Assert.Equal( 25, method.GetProperty( "extensionMinSeconds" ).GetDouble() );
      Assert.Equal( 80, method.GetProperty( "extensionCapSeconds" ).GetDouble() );
      Assert.Equal( 45, method.GetProperty( "rehearsalSeconds" ).GetDouble() );
      Assert.True( method.GetProperty( "timeBased" ).GetBoolean() );
      Assert.Equal( "warm-up at the pass's own concurrency for at least 12 s and at least 40 searches (at most 90 s); then a 4 s trial that must land within 8% of the settled figure; one extension of 25 s to 80 s if it does not; rehearsal of every pass type for 45 s before the first timed pass",
         method.GetProperty( "description" ).GetString() );
      string md = File.ReadAllText( Path.Combine( _root, "out", "consolidated.md" ) );
      Assert.Contains( "| warm-up before each timed pass | time based: the pass's own search at the pass's own concurrency for at least 12 s and at least 40 searches (at most 90 s), read in windows of at least 2 s and 100 searches |", md );
      Assert.Contains( "| settle check before each timed pass | a 4 s trial of the same pass must land within 8% of the warm-up's settled figure (the median of its last 3 windows, which must agree within 5%); if not, the warm-up is extended once (at least 25 s, at most 80 s) and a second trial is taken; a pass whose trial still disagrees flags its target as unsettled |", md );
      Assert.Contains( "| rehearsal before the first timed pass | every pass type at its own concurrency for 45 s each, untimed |", md );
      Assert.Contains( "| least searches a warm-up must run (the --warmup setting, besides the time above) | 20 |", md );
      Assert.DoesNotContain( "warm-up searches before each timed pass", md );
      string section = md[md.IndexOf( "## Warm-up method as the runs recorded it", StringComparison.Ordinal )..md.IndexOf( "## Runs used", StringComparison.Ordinal )];
      Assert.Contains( $"- {notes[0]}", section );
      Assert.Contains( $"- {notes[1]}", section );
   }

   /// <summary>
   /// The method notes a real run wrote (the raw folder of run 601 of 5 Oct 2026, which is evidence and is
   /// never renamed) give the real numbers: a warm-up of at least 15 s and 20 searches (at most 120 s) at the
   /// pass's own concurrency, read in windows of at least 2 s and 100 searches, a 3 s trial within 10% of
   /// the median of the last 3 windows (which agree within 5%), one extension of 30 to 120 s and a 30 s
   /// rehearsal. This ties the parser to the text the runner really wrote, not to a copy of it in a test.
   /// </summary>
   [Fact]
   public void WarmupMethod_ParsesTheNotesOfARealRun()
   {
      string real = Path.Combine( RepoRoot(), "bench-results", "20261005-073329-eshoponweb", "results.json" );
      Directory.CreateDirectory( Path.Combine( _root, "real" ) );
      File.Copy( real, Path.Combine( _root, "real", "results.json" ) );

      JsonElement json = Consolidate( "--targets", "sql", Path.Combine( _root, "real" ) );

      JsonElement method = json.GetProperty( "settings" ).GetProperty( "warmupMethod" );
      Assert.Equal( new double[] { 15, 20, 120, 2, 100, 3, 10, 3, 5, 30, 120, 30 }, new[]
      {
         method.GetProperty( "minSeconds" ).GetDouble(), method.GetProperty( "minSearches" ).GetDouble(), method.GetProperty( "capSeconds" ).GetDouble(),
         method.GetProperty( "windowSeconds" ).GetDouble(), method.GetProperty( "windowSearches" ).GetDouble(), method.GetProperty( "trialSeconds" ).GetDouble(),
         method.GetProperty( "trialPercent" ).GetDouble(), method.GetProperty( "settleWindows" ).GetDouble(), method.GetProperty( "settlePercent" ).GetDouble(),
         method.GetProperty( "extensionMinSeconds" ).GetDouble(), method.GetProperty( "extensionCapSeconds" ).GetDouble(), method.GetProperty( "rehearsalSeconds" ).GetDouble(),
      } );
      Assert.True( method.GetProperty( "flagsUnsettled" ).GetBoolean() );
      Assert.DoesNotContain( "missing", method.GetProperty( "description" ).GetString()! );
      Assert.Equal( 2, method.GetProperty( "recorded" ).GetArrayLength() );
   }

   /// <summary>
   /// Runs recorded under different warm-up methods are never mixed: the odd run is dropped with the
   /// field and both descriptions, as every other difference in conditions is.
   /// </summary>
   [Fact]
   public void RunsWithDifferentWarmupMethods_AreNeverMixed()
   {
      WriteClean( "r1", "2026-10-05T10:00:00Z", Notes( MethodNotes() ), Target( "sql", 2, 900, 500 ) );
      WriteClean( "r2", "2026-10-05T11:00:00Z", Notes( MethodNotes() ), Target( "sql", 2, 890, 500 ) );
      WriteClean( "r3", "2026-10-05T12:00:00Z", Notes( MethodNotes( warm: 10 ) ), Target( "sql", 2, 880, 500 ) );

      JsonElement json = Consolidate( "--targets", "sql", Path.Combine( _root, "r?" ) );

      Assert.Equal( new[] { "r1", "r2" }, Names( json.GetProperty( "runs" ) ) );
      string reason = json.GetProperty( "dropped" )[0].GetProperty( "reason" ).GetString()!;
      Assert.Contains( "warmupMethod warm-up at the pass's own concurrency for at least 10 s", reason );
      Assert.Contains( "vs warm-up at the pass's own concurrency for at least 15 s", reason );
   }

   /// <summary>
   /// A run that recorded only the older note ("untimed warm-up of N searches") is described as a fixed
   /// count of an older run, and a run with no note at all as missing: neither is described as the time
   /// based method.
   /// </summary>
   [Fact]
   public void WarmupMethod_OfOlderOrUnrecordedRuns_IsNotDressedUpAsTheCurrentMethod()
   {
      WriteRun( "r1", "2026-10-05T10:00:00Z", r => r["notes"] = new JsonArray( "Warm-up: every timed pass started with its own untimed warm-up of 30 searches." ), Target( "a", 2, 900, 500 ) );
      JsonElement older = Consolidate( "--targets", "a", Path.Combine( _root, "r1" ) );
      Assert.True( older.GetProperty( "settings" ).GetProperty( "warmupMethod" ).GetProperty( "legacy" ).GetBoolean() );

      WriteRun( "r2", "2026-10-05T11:00:00Z", Target( "a", 2, 900, 500 ) );
      JsonElement none = Consolidate( "--targets", "a", Path.Combine( _root, "r2" ), "--out", Path.Combine( _root, "out2" ) );
      Assert.Equal( JsonValueKind.Null, none.GetProperty( "settings" ).GetProperty( "warmupMethod" ).ValueKind );
      string md = File.ReadAllText( Path.Combine( _root, "out2", "consolidated.md" ) );
      Assert.Contains( "| warm-up before each timed pass | missing |", md );
      Assert.Contains( "| settle check before each timed pass | missing |", md );
      Assert.Contains( "(none: the runs recorded no warm-up or rehearsal note)", md );
   }

   /// <summary>
   /// A target whose engine description, recorded engine settings, configuration files or durability
   /// statement differs between the runs is refused for that target alone: it has no row, the other target
   /// is consolidated, and the report names the target with both values and their runs.
   /// </summary>
   [Theory]
   [InlineData( "engine description", "ClickHouse 26.3.39.7 (MergeTree)", "ClickHouse 26.3.39.7 (MergeTree, system log tables off)" )]
   [InlineData( "engine settings", "logger.level=trace", "logger.level=warning" )]
   [InlineData( "engine files", "clickhouse.compose.yaml 0123456789ab", "clickhouse.compose.yaml ba9876543210" )]
   [InlineData( "durability", "Acknowledged inserts are not fsynced.", "Acknowledged inserts are fsynced." )]
   public void EngineSetupDifference_RefusesThatTargetAlone( string label, string before, string after )
   {
      WriteRun( "r1", "2026-10-05T10:00:00Z", Setup( Target( "ch", 4, 500, 200 ), label, before ), Target( "sql", 2, 900, 500 ) );
      WriteRun( "r2", "2026-10-05T11:00:00Z", Setup( Target( "ch", 4, 490, 200 ), label, before ), Target( "sql", 2, 890, 500 ) );
      WriteRun( "r3", "2026-10-05T12:00:00Z", Setup( Target( "ch", 4, 620, 220 ), label, after ), Target( "sql", 2, 880, 500 ) );

      JsonElement json = Consolidate( "--targets", "ch,sql", Path.Combine( _root, "r?" ) );

      Assert.Equal( new[] { "sql" }, Names( json.GetProperty( "targetSummaries" ) ) );
      JsonElement withheld = json.GetProperty( "withheld" )[0];
      Assert.Equal( "ch", withheld.GetProperty( "target" ).GetString() );
      Assert.Equal( "refused", withheld.GetProperty( "kind" ).GetString() );
      string reason = withheld.GetProperty( "reason" ).GetString()!;
      Assert.Contains( $"{label} of ch: ", reason );
      Assert.Contains( $"{before} (r1, r2)", reason );
      Assert.Contains( $"{after} (r3)", reason );
      Assert.Contains( "are different experiments and are never merged", reason );
      string md = File.ReadAllText( Path.Combine( _root, "out", "consolidated.md" ) );
      Assert.Contains( "## Targets not in this report", md );
      Assert.Contains( "| ch | refused | r1, r2, r3 |", md );
   }

   /// <summary>
   /// A setup that differs between runs for the only listed target leaves nothing to report: the command
   /// stops with exit 1, writes nothing, and says which runs differ and how.
   /// </summary>
   [Fact]
   public void EngineSetupDifference_OfTheOnlyTarget_StopsTheCommand()
   {
      WriteRun( "r1", "2026-10-05T10:00:00Z", Setup( Target( "ch", 4, 500, 200 ), "engine files", "a 0123456789ab" ), Target( "other", 2, 900, 500 ) );
      WriteRun( "r2", "2026-10-05T11:00:00Z", Setup( Target( "ch", 4, 490, 200 ), "engine files", "a ba9876543210" ), Target( "other", 2, 890, 500 ) );

      int exit = Run( "--targets", "ch", Path.Combine( _root, "r?" ), "--out", Path.Combine( _root, "stopped" ) );

      Assert.Equal( 1, exit );
      Assert.Contains( _log, l => l.StartsWith( "Failed: Refusing to merge runs that are not the same kind of experiment. engine files of ch:", StringComparison.Ordinal ) );
      Assert.False( File.Exists( Path.Combine( _root, "stopped", "consolidated.json" ) ) );
   }

   /// <summary>
   /// A difference deep inside a long statement is shown where it is: the common opening is cut, the
   /// words around the first differing character are kept for each value, and the whole statement is not
   /// printed twice.
   /// </summary>
   [Fact]
   public void LongSetupDifference_IsShownWhereItDiffers()
   {
      string opening = string.Concat( Enumerable.Repeat( "Writes are journaled before the acknowledgement, as the server default says. ", 8 ) );
      WriteRun( "r1", "2026-10-05T10:00:00Z", Setup( Target( "m", 4, 500, 200 ), "durability", opening + "The write concern is majority." ), Target( "sql", 2, 900, 500 ) );
      WriteRun( "r2", "2026-10-05T11:00:00Z", Setup( Target( "m", 4, 500, 200 ), "durability", opening + "The write concern is one." ), Target( "sql", 2, 890, 500 ) );

      JsonElement json = Consolidate( "--targets", "m,sql", Path.Combine( _root, "r?" ) );

      string reason = json.GetProperty( "withheld" )[0].GetProperty( "reason" ).GetString()!;
      Assert.Contains( "The write concern is majority. (r1)", reason );
      Assert.Contains( "The write concern is one. (r2)", reason );
      Assert.Contains( "durability of m: ...", reason );
      Assert.True( reason.Length < opening.Length, $"the reason repeats the long opening: {reason.Length} characters" );
   }

   /// <summary>
   /// The Targets table and consolidated.json carry what the runs recorded about each engine: the engine
   /// settings, the durability statement and the engine files (read from the target's engine-files note,
   /// without the sentence after the list, which says who created the engine and may differ), and the
   /// table says that runs that differ in them are refused.
   /// </summary>
   [Fact]
   public void TargetsTable_ShowsEngineSettings_Durability_AndEngineFiles()
   {
      JsonObject WithSetup( JsonObject t, string sentence )
      {
         t["engineSettings"] = new JsonObject { ["logger.level"] = "warning", ["system logs"] = "off" };
         t["durability"] = "Acknowledged inserts are not fsynced.";
         t["notes"] = new JsonArray( FILES_NOTE.Replace( "This run created the engine from these files.", sentence ) );
         return t;
      }

      WriteRun( "r1", "2026-10-05T10:00:00Z", WithSetup( Target( "ch", 4, 500, 200 ), "This run created the engine from these files." ) );
      WriteRun( "r2", "2026-10-05T11:00:00Z", WithSetup( Target( "ch", 4, 490, 200 ), "The engine was already running at its turn, so it may have been created from other files than these." ) );

      JsonElement json = Consolidate( "--targets", "ch", Path.Combine( _root, "r?" ) );

      JsonElement summary = json.GetProperty( "targetSummaries" )[0];
      Assert.Equal( new[] { "logger.level=warning, system logs=off" }, summary.GetProperty( "engineSettings" ).EnumerateArray().Select( e => e.GetString() ).ToArray() );
      Assert.Equal( new[] { "clickhouse-config/gvb-system-logs.xml 0123456789ab; clickhouse.compose.yaml fedcba987654" }, summary.GetProperty( "engineFiles" ).EnumerateArray().Select( e => e.GetString() ).ToArray() );
      Assert.Equal( new[] { "Acknowledged inserts are not fsynced." }, summary.GetProperty( "durabilities" ).EnumerateArray().Select( e => e.GetString() ).ToArray() );
      Assert.Equal( 0, json.GetProperty( "withheld" ).GetArrayLength() );
      string md = File.ReadAllText( Path.Combine( _root, "out", "consolidated.md" ) );
      Assert.Contains( "| target | hosting | engine | index | search settings | engine settings (recorded) | durability | engine files (SHA-256) |", md );
      Assert.Contains( "| ch | compose | ch 1.0 | test index | ef=100 | logger.level=warning, system logs=off | Acknowledged inserts are not fsynced. | clickhouse-config/gvb-system-logs.xml 0123456789ab; clickhouse.compose.yaml fedcba987654 |", md );
      Assert.Contains( "Runs that differ in any of them are refused for that target, never merged", md );
   }

   /// <summary>
   /// Runs that recorded no engine settings or files print "missing" in those columns, which says that
   /// nothing was recorded and a change there would not show.
   /// </summary>
   [Fact]
   public void TargetsTable_SaysMissing_WhenNoRunRecordedTheSetup()
   {
      WriteClean( "r1", "2026-10-05T10:00:00Z", Target( "ch", 4, 500, 200 ) );
      WriteClean( "r2", "2026-10-05T11:00:00Z", Target( "ch", 4, 490, 200 ) );

      Consolidate( "--targets", "ch", Path.Combine( _root, "r?" ) );

      string md = File.ReadAllText( Path.Combine( _root, "out", "consolidated.md" ) );
      Assert.Contains( "| ch | compose | ch 1.0 | test index | ef=100 | missing | full recovery model, log flushed on commit | missing |", md );
      Assert.Contains( "a column that reads missing was not recorded by any run, so a setting changed there would not show", md );
   }

   /// <summary>
   /// The engine notes column of the headline tables lists every flag of the engine after "flags:", the
   /// unsettled flag included, and the line under the tables says what the column holds; an engine with
   /// no flag and no note keeps "-".
   /// </summary>
   [Fact]
   public void EngineNotesColumn_ShowsTheUnsettledFlag_AndEveryOtherFlag()
   {
      JsonObject unsettled = Target( "redis", 1.0, 5000, 1000 );
      unsettled["settled"] = false;
      unsettled["settleNote"] = "latency had NOT settled when timing began for default@8";
      JsonObject spread = Target( "spready", 2.0, 1000, 500 );
      WriteClean( "r1", "2026-10-05T10:00:00Z", unsettled, spread, Target( "calm", 3, 800, 1000.0 / 3 ) );
      JsonObject unsettled2 = Target( "redis", 1.0, 5100, 1000 );
      unsettled2["settled"] = false;
      WriteClean( "r2", "2026-10-05T11:00:00Z", unsettled2, Target( "spready", 2.0, 1500, 500 ), Target( "calm", 3, 805, 1000.0 / 3 ) );

      Consolidate( "--targets", "redis,spready,calm", Path.Combine( _root, "r?" ) );

      string md = File.ReadAllText( Path.Combine( _root, "out", "consolidated.md" ) );
      string ranking = md[md.IndexOf( "## Request speed", StringComparison.Ordinal )..md.IndexOf( "## Per-engine notes", StringComparison.Ordinal )];
      Assert.Contains( "redis | ", ranking );
      Assert.Matches( @"\| redis \| [^|]+ \| 2 \| flags: unsettled-target \|", ranking );
      Assert.Matches( @"\| spready \| [^|]+ \| 2 \| flags: spread \|", ranking );
      Assert.Matches( @"\| calm \| [^|]+ \| 2 \| - \|", ranking );
      Assert.Equal( 1, ranking.Split( "The engine notes column lists the notes under Per-engine notes and, after \"flags:\", every flag the engine carries" ).Length - 1 );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The two method notes a current run records (SearchRunner.DescribeMethod writes them from the
   /// constants it runs with), built from the numbers given so a test can pick numbers the real method
   /// does not use. The defaults are the real method's.
   /// </summary>
   /// <param name="warm">Shortest warm-up, seconds.</param>
   /// <param name="searches">Fewest searches in a warm-up.</param>
   /// <param name="warmCap">Longest warm-up, seconds.</param>
   /// <param name="trial">Settle trial, seconds.</param>
   /// <param name="tolerance">How far the trial may lie from the settled figure, percent.</param>
   /// <param name="extensionMin">Shortest extension, seconds.</param>
   /// <param name="extensionCap">Longest extension, seconds.</param>
   /// <param name="rehearsal">Rehearsal of each pass type, seconds.</param>
   /// <returns>The rehearsal note and the warm-up note.</returns>
   private static string[] MethodNotes( double warm = 15, int searches = 20, double warmCap = 120, double trial = 3, double tolerance = 10, double extensionMin = 30, double extensionCap = 120, double rehearsal = 30 )
   {
      return new[]
      {
         $"Preparation, untimed, before any timed pass: a rehearsal of every pass type at its own concurrency for {rehearsal} s each, through the same code the passes use.",
         $"Warm-up and settle check, untimed, right before every timed pass: the pass's own search at the pass's own number of searchers for at least {warm} s and at least {searches} searches (at most {warmCap} s), "
            + $"read in windows of at least 2 s and 100 searches; then a {trial} s trial of the same pass. The trial's figure (p50 with one searcher, QPS with several) must lie within {tolerance}% "
            + $"of the warm-up's settled figure (the median of its last 3 windows, which must agree within 5%); if not, the warm-up is extended once (at least {extensionMin} s, until its windows agree, at most {extensionCap} s), "
            + "a second trial is taken and the warm-up runs again before the pass. Each target's notes give every check, and a pass that still disagrees flags its target as unsettled. "
            + "With machine control on, the check for a quiet box is made when the warm-up is announced, before it starts.",
      };
   }

   /// <summary>
   /// A run-level change that sets the run's notes.
   /// </summary>
   /// <param name="notes">The notes.</param>
   /// <returns>The change.</returns>
   private static Action<JsonObject> Notes( string[] notes )
   {
      return run => run["notes"] = new JsonArray( notes.Select( n => (JsonNode)JsonValue.Create( n )! ).ToArray() );
   }

   /// <summary>
   /// Writes one part of a target's engine setup.
   /// </summary>
   /// <param name="target">The target object.</param>
   /// <param name="label">"engine description", "engine settings", "engine files" or "durability".</param>
   /// <param name="value">The text.</param>
   /// <returns>The same target.</returns>
   private static JsonObject Setup( JsonObject target, string label, string value )
   {
      switch( label )
      {
         case "engine description": target["engine"] = value; break;
         case "engine settings": target["engineSettings"] = new JsonObject { [value.Split( '=' )[0]] = value.Split( '=' )[1] }; break;
         case "engine files": target["notes"] = new JsonArray( $"Engine files (SHA-256, first 12 hex digits): [{value}]. This run created the engine from these files." ); break;
         default: target["durability"] = value; break;
      }

      return target;
   }

   #endregion Private Methods
}
