using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The second-review rules of the consolidate command: runs measured under different build
/// configuration, CPU governor, CPU partition, warm-up count, exact-mode seconds, seconds per
/// level or search settings are never mixed; and the flags a reader needs next to a number
/// (spread, p50 against mean, unsettled engine, busy box, governor, shared cores, Debug build).
/// Each test writes synthetic result folders and reads consolidated.json and consolidated.md, as
/// a writeup would. A clean modern run produces no flag at all, so a flag in these tests is
/// always the thing under test.
/// </summary>
public sealed partial class ConsolidateTests
{
   #region Data Members

   private const string STATS_NAMESPACE = "GenericVectorBuilder.Bench.Stats";
   private const string PARTITION_TEXT = "client 0,4; engines 1-3,5-7";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// A run that records every condition and has nothing wrong with it gets no flag, carries its
   /// conditions into the settings of consolidated.json, and shows them in consolidated.md.
   /// </summary>
   [Fact]
   public void CleanModernRun_HasNoFlags_AndShowsItsConditions()
   {
      WriteClean( "r1", "2026-10-05T10:00:00Z", Target( "sql", 2, 900, 500 ) );
      WriteClean( "r2", "2026-10-05T11:00:00Z", Target( "sql", 2, 880, 500 ) );

      JsonElement json = Consolidate( "--targets", "sql", Path.Combine( _root, "r?" ) );

      Assert.Equal( 0, json.GetProperty( "flags" ).GetArrayLength() );
      JsonElement settings = json.GetProperty( "settings" );
      Assert.Equal( "Release", settings.GetProperty( "buildConfiguration" ).GetString() );
      Assert.Equal( "performance", settings.GetProperty( "governor" ).GetString() );
      Assert.Equal( PARTITION_TEXT, settings.GetProperty( "cpuPartition" ).GetString() );
      Assert.Equal( 20, settings.GetProperty( "warmupSearches" ).GetInt32() );
      Assert.Equal( 60, settings.GetProperty( "exactSeconds" ).GetInt32() );
      Assert.Equal( "ef=100", settings.GetProperty( "searchSettings" ).GetProperty( "sql" ).GetString() );
      string md = File.ReadAllText( Path.Combine( _root, "out", "consolidated.md" ) );
      Assert.Contains( "| build configuration | Release |", md );
      Assert.Contains( "| CPU governor | performance |", md );
      Assert.Contains( $"| CPU partition | {PARTITION_TEXT} |", md );
      Assert.Contains( "| least searches a warm-up must run (the --warmup setting, besides the time above) | 20 |", md );
      Assert.Contains( "| warm-up before each timed pass | time based: the pass's own search at the pass's own concurrency for at least 15 s and at least 20 searches (at most 120 s), read in windows of at least 2 s and 100 searches |", md );
      Assert.DoesNotContain( "warm-up searches before each timed pass", md );
      Assert.Contains( "| exact mode seconds | 60 |", md );
      Assert.Contains( "| sql | compose | sql 1.0 | test index | ef=100 |", md );
   }

   /// <summary>
   /// A run measured under any different condition is dropped with the field and both values,
   /// and the largest group of matching runs is used: build configuration, governor, CPU
   /// partition, warm-up count, exact-mode seconds, seconds per level and a listed engine's
   /// search settings.
   /// </summary>
   [Fact]
   public void DropsRuns_WhoseConditionsDiffer_AndSaysWhich()
   {
      var cases = new (string Name, Action<JsonObject> Change, Action<JsonObject>? ChangeTarget, string Reason)[]
      {
         ( "build", r => Machine( r )["buildConfiguration"] = "Debug", null, "buildConfiguration Debug vs Release" ),
         ( "governor", r => Machine( r )["governor"] = "schedutil", null, "governor schedutil vs performance" ),
         ( "partition", r => Conditions( r )["engineCpus"] = "2-7", null, "cpuPartition client 0,4; engines 2-7 vs " + PARTITION_TEXT ),
         ( "warmup", r => Conditions( r )["warmupSearches"] = 50, null, "warmupSearches 50 vs 20" ),
         ( "exact", r => Conditions( r )["exactSeconds"] = 5, null, "exactSeconds 5 vs 60" ),
         ( "seconds", r => r["secondsPerLevel"] = 5, null, "secondsPerLevel 5 vs 20" ),
         ( "effort", _ => { }, t => t["searchSettings"] = new JsonObject { ["ef"] = 3200 }, "searchSettings[sql] ef=3200 vs ef=100" ),
      };

      foreach( ( string name, Action<JsonObject> change, Action<JsonObject>? changeTarget, string reason ) in cases )
      {
         JsonObject odd = Target( "sql", 2, 900, 500 );
         changeTarget?.Invoke( odd );
         WriteClean( $"{name}-1", "2026-10-05T10:00:00Z", Target( "sql", 2, 900, 500 ) );
         WriteClean( $"{name}-2", "2026-10-05T11:00:00Z", Target( "sql", 2, 905, 500 ) );
         WriteClean( $"{name}-3", "2026-10-05T12:00:00Z", change, odd );

         JsonElement json = Consolidate( "--targets", "sql", Path.Combine( _root, $"{name}-?" ) );

         Assert.Equal( new[] { $"{name}-1", $"{name}-2" }, Names( json.GetProperty( "runs" ) ) );
         JsonElement dropped = Assert.Single( json.GetProperty( "dropped" ).EnumerateArray() );
         Assert.Equal( $"settings differ from the runs used: {reason}", dropped.GetProperty( "reason" ).GetString() );
         Assert.Contains( _log, l => l == $"Dropped {name}-3: settings differ from the runs used: {reason}" );
      }
   }

   /// <summary>
   /// A run from before the conditions were recorded is not mixed with runs that recorded them
   /// (missing is a value that differs), and every field it lacks is named. The recorded group
   /// is the larger and wins.
   /// </summary>
   [Fact]
   public void RunWithoutConditions_IsNotMixedWithRecordedOnes()
   {
      WriteClean( "r1", "2026-10-05T10:00:00Z", Target( "sql", 2, 900, 500 ) );
      WriteClean( "r2", "2026-10-05T11:00:00Z", Target( "sql", 2, 900, 500 ) );
      WriteRun( "r3", "2026-10-05T12:00:00Z", Legacy, Old( Target( "sql", 2, 900, 500 ) ) );

      JsonElement json = Consolidate( "--targets", "sql", Path.Combine( _root, "r?" ) );

      Assert.Equal( new[] { "r1", "r2" }, Names( json.GetProperty( "runs" ) ) );
      Assert.Equal( "settings differ from the runs used: buildConfiguration missing vs Release, governor missing vs performance, cpuPartition missing vs " + PARTITION_TEXT
         + ", warmupSearches missing vs 20, warmupMethod missing vs warm-up at the pass's own concurrency for at least 15 s and at least 20 searches (at most 120 s); then a 3 s trial that must land within 10% of the settled figure; one extension of 30 s to 120 s if it does not; rehearsal of every pass type for 30 s before the first timed pass"
         + ", exactSeconds missing vs 60, searchSettings[sql] missing vs ef=100", json.GetProperty( "dropped" )[0].GetProperty( "reason" ).GetString() );
   }

   /// <summary>
   /// Runs that all predate the conditions still consolidate together, so published history
   /// stays usable; their settings read as missing, never as a made-up value.
   /// </summary>
   [Fact]
   public void RunsThatAllLackConditions_StillConsolidateTogether()
   {
      WriteRun( "r1", "2026-10-05T10:00:00Z", Legacy, Old( Target( "sql", 5.5, 800, 190 ) ) );
      WriteRun( "r2", "2026-10-05T11:00:00Z", Legacy, Old( Target( "sql", 5.5, 810, 190 ) ) );

      JsonElement json = Consolidate( "--targets", "sql", Path.Combine( _root, "r?" ) );

      Assert.Equal( 2, json.GetProperty( "runs" ).GetArrayLength() );
      Assert.Equal( 0, json.GetProperty( "dropped" ).GetArrayLength() );
      JsonElement settings = json.GetProperty( "settings" );
      Assert.Equal( JsonValueKind.Null, settings.GetProperty( "governor" ).ValueKind );
      Assert.Equal( JsonValueKind.Null, settings.GetProperty( "cpuPartition" ).ValueKind );
      Assert.Equal( JsonValueKind.Null, settings.GetProperty( "searchSettings" ).GetProperty( "sql" ).ValueKind );
   }

   /// <summary>
   /// "0,1,2,3" and "0-3" (and any order) are one CPU partition: runs that spell it differently
   /// are not split into groups.
   /// </summary>
   [Fact]
   public void CpuListSpelling_DoesNotSplitGroups()
   {
      WriteClean( "r1", "2026-10-05T10:00:00Z", r => { Machine( r )["clientCpus"] = "0,4"; Conditions( r )["engineCpus"] = "1-3,5-7"; }, Target( "sql", 2, 900, 500 ) );
      WriteClean( "r2", "2026-10-05T11:00:00Z", r => { Machine( r )["clientCpus"] = "4,0"; Conditions( r )["engineCpus"] = "1,2,3,5,6,7"; }, Target( "sql", 2, 900, 500 ) );

      JsonElement json = Consolidate( "--targets", "sql", Path.Combine( _root, "r?" ) );

      Assert.Equal( 2, json.GetProperty( "runs" ).GetArrayLength() );
      Assert.Equal( PARTITION_TEXT, json.GetProperty( "settings" ).GetProperty( "cpuPartition" ).GetString() );
   }

   /// <summary>
   /// The partition can be recorded as an object or as one text; both are read into the same
   /// client and engine lists.
   /// </summary>
   [Fact]
   public void PartitionRecordedAsObjectOrText_IsRead()
   {
      WriteClean( "obj", "2026-10-05T10:00:00Z", r => { Machine( r ).Remove( "clientCpus" ); Conditions( r ).Remove( "engineCpus" ); Conditions( r )["cpuPartition"] = new JsonObject { ["client"] = "0,4", ["engines"] = "1-3,5-7" }; }, Target( "sql", 2, 900, 500 ) );
      WriteClean( "txt", "2026-10-05T11:00:00Z", r => { Machine( r ).Remove( "clientCpus" ); Conditions( r ).Remove( "engineCpus" ); Conditions( r )["cpuPartition"] = "client 4,0; engines 1-3,5-7"; }, Target( "sql", 2, 900, 500 ) );

      JsonElement json = Consolidate( "--targets", "sql", Path.Combine( _root, "obj" ), Path.Combine( _root, "txt" ) );

      Assert.Equal( 2, json.GetProperty( "runs" ).GetArrayLength() );
      Assert.Equal( PARTITION_TEXT, json.GetProperty( "settings" ).GetProperty( "cpuPartition" ).GetString() );
      Assert.Equal( 0, json.GetProperty( "flags" ).GetArrayLength() );
   }

   /// <summary>
   /// Spread is flagged when the largest p50 or QPS across runs is more than 1.15 times the
   /// smallest, with the numbers and the run count; 1.15 exactly, a single run and a steady
   /// target are not flagged.
   /// </summary>
   [Fact]
   public void Spread_IsFlaggedAbove115_WithTheNumbers()
   {
      WriteClean( "r1", "2026-10-05T10:00:00Z", Target( "wide", 2.0, 1000, 500 ), Target( "edge", 2.0, 1000, 500 ), Target( "steady", 2.0, 1000, 500 ) );
      WriteClean( "r2", "2026-10-05T11:00:00Z", Target( "wide", 2.5, 1300, 400 ), Target( "edge", 2.0, 1150, 500 ), Target( "steady", 2.0, 1140, 500 ) );

      JsonElement json = Consolidate( "--targets", "wide,edge,steady", Path.Combine( _root, "r?" ) );

      Dictionary<string, string> wide = Flags( json, "wide" );
      Assert.Equal( "p50 ms 2.00 to 2.50 (x1.25 over 2 runs); QPS@1 400.0 to 500.0 (x1.25 over 2 runs); QPS@8 1000.0 to 1300.0 (x1.30 over 2 runs)", wide["spread"] );
      Assert.DoesNotContain( "spread", Flags( json, "edge" ).Keys );
      Assert.DoesNotContain( "spread", Flags( json, "steady" ).Keys );
      Assert.Equal( new[] { "r1", "r2" }, json.GetProperty( "flags" ).EnumerateArray().First( f => f.GetProperty( "kind" ).GetString() == "spread" ).GetProperty( "runs" ).EnumerateArray().Select( e => e.GetString() ).ToArray() );
   }

   /// <summary>
   /// One run has no spread to flag.
   /// </summary>
   [Fact]
   public void Spread_NeedsTwoRuns()
   {
      WriteClean( "r1", "2026-10-05T10:00:00Z", Target( "sql", 2.0, 1000, 500 ) );

      JsonElement json = Consolidate( "--targets", "sql", Path.Combine( _root, "r1" ) );

      Assert.DoesNotContain( "spread", Flags( json, "sql" ).Keys );
   }

   /// <summary>
   /// p50 above the mean (1000 / QPS at one searcher) by more than 15%, or the mean more than
   /// 1.5 times the p50, is flagged with both numbers; a recorded meanMs is used in place of the
   /// derived one; with no one-searcher level there is no mean and no flag.
   /// </summary>
   [Fact]
   public void P50AgainstMean_IsFlagged_WhenTheyDisagree()
   {
      JsonObject recorded = Target( "recorded", 6.72, 800, 191.6 );
      ( (JsonObject)recorded["search"]! )["meanMs"] = 6.5;
      WriteClean( "r1", "2026-10-05T10:00:00Z", Target( "high", 6.72, 800, 191.6 ), Target( "tail", 2.0, 800, 100 ), Target( "fine", 5.5, 800, 190 ), recorded );

      JsonElement json = Consolidate( "--targets", "high,tail,fine,recorded", Path.Combine( _root, "r1" ) );

      Assert.Equal( "p50 6.72 ms, mean 5.22 ms (1000 / QPS@1), p50/mean 1.29", Flags( json, "high" )["p50-mean-inconsistent"] );
      Assert.Equal( "p50 2.00 ms, mean 10.00 ms (1000 / QPS@1), p50/mean 0.20", Flags( json, "tail" )["p50-mean-inconsistent"] );
      Assert.DoesNotContain( "p50-mean-inconsistent", Flags( json, "fine" ).Keys );
      Assert.DoesNotContain( "p50-mean-inconsistent", Flags( json, "recorded" ).Keys );
      Assert.Equal( "recorded", json.GetProperty( "targetSummaries" )[3].GetProperty( "perRun" )[0].GetProperty( "meanSource" ).GetString() );
      Assert.Equal( "1000 / QPS@1", json.GetProperty( "targetSummaries" )[0].GetProperty( "perRun" )[0].GetProperty( "meanSource" ).GetString() );
   }

   /// <summary>
   /// Without a one-searcher level there is no mean to compare the p50 with.
   /// </summary>
   [Fact]
   public void P50AgainstMean_NeedsTheOneSearcherLevel()
   {
      JsonObject target = Target( "sql", 20, 800, 100 );
      ( (JsonObject)target["search"]! )["qps"] = new JsonObject { ["4"] = 300.0, ["8"] = 800.0 };
      WriteClean( "r1", "2026-10-05T10:00:00Z", r => r["concurrency"] = new JsonArray( 4, 8 ), target );

      JsonElement json = Consolidate( "--targets", "sql", Path.Combine( _root, "r1" ) );

      Assert.DoesNotContain( "p50-mean-inconsistent", Flags( json, "sql" ).Keys );
   }

   /// <summary>
   /// A target recorded as not settled is flagged with the run's own words, whether it wrote
   /// "settled" with a note or a "settle" object.
   /// </summary>
   [Fact]
   public void UnsettledTarget_IsFlagged_WithTheRunsWords()
   {
      JsonObject flat = Target( "flat", 2, 900, 500 );
      flat["settled"] = false;
      flat["settleNote"] = "JVM still compiling, 4 s after start";
      JsonObject nested = Target( "nested", 2, 900, 500 );
      nested.Remove( "settled" );
      nested["settle"] = new JsonObject { ["settled"] = false, ["detail"] = "compaction running" };
      WriteClean( "r1", "2026-10-05T10:00:00Z", flat, nested, Target( "calm", 2, 900, 500 ) );

      JsonElement json = Consolidate( "--targets", "flat,nested,calm", Path.Combine( _root, "r1" ) );

      Assert.Equal( "not settled: JVM still compiling, 4 s after start", Flags( json, "flat" )["unsettled-target"] );
      Assert.Equal( "not settled: compaction running", Flags( json, "nested" )["unsettled-target"] );
      Assert.DoesNotContain( "unsettled-target", Flags( json, "calm" ).Keys );
   }

   /// <summary>
   /// A busy box is a 1-minute load average above the run's own logical CPU count at the start;
   /// it applies to every target of that run, and a bigger box is not busy at the same load.
   /// </summary>
   [Fact]
   public void BusyBox_UsesTheRunsOwnCpuCount()
   {
      WriteClean( "busy", "2026-10-05T10:00:00Z", r => Machine( r )["loadAverage"] = "9.50 5.00 3.00", Target( "a", 2, 900, 500 ), Target( "b", 2, 900, 500 ) );
      WriteClean( "edge", "2026-10-05T11:00:00Z", r => Machine( r )["loadAverage"] = "8.00 5.00 3.00", Target( "a", 2, 900, 500 ), Target( "b", 2, 900, 500 ) );
      WriteClean( "big", "2026-10-05T12:00:00Z", r => { Machine( r )["loadAverage"] = "9.50 5.00 3.00"; Machine( r )["logicalCpus"] = 16; }, Target( "a", 2, 900, 500 ), Target( "b", 2, 900, 500 ) );

      JsonElement json = Consolidate( "--targets", "a,b", Path.Combine( _root, "busy" ) );
      Assert.Equal( "load average 9.50 5.00 3.00 at the start on 8 logical CPUs", Flags( json, "a" )["busy-box"] );
      Assert.Equal( "load average 9.50 5.00 3.00 at the start on 8 logical CPUs", Flags( json, "b" )["busy-box"] );

      json = Consolidate( "--targets", "a,b", Path.Combine( _root, "edge" ) );
      Assert.DoesNotContain( "busy-box", Flags( json, "a" ).Keys );

      json = Consolidate( "--targets", "a,b", Path.Combine( _root, "big" ) );
      Assert.DoesNotContain( "busy-box", Flags( json, "a" ).Keys );
   }

   /// <summary>
   /// A governor other than performance is flagged for every target, including a governor that
   /// changed during the run; performance throughout is not.
   /// </summary>
   [Fact]
   public void Governor_IsFlagged_WhenNotPerformance_OrChangedMidRun()
   {
      WriteClean( "sched", "2026-10-05T10:00:00Z", r => Machine( r )["governor"] = "schedutil", Target( "a", 2, 900, 500 ) );
      WriteClean( "moved", "2026-10-05T11:00:00Z", r => Machine( r )["governorAtEnd"] = "powersave", Target( "a", 2, 900, 500 ) );
      WriteClean( "fine", "2026-10-05T12:00:00Z", r => Machine( r )["governorAtEnd"] = "Performance", Target( "a", 2, 900, 500 ) );

      Assert.Equal( "governor schedutil", Flags( Consolidate( "--targets", "a", Path.Combine( _root, "sched" ) ), "a" )["governor-not-performance"] );
      Assert.Equal( "governor performance, then powersave", Flags( Consolidate( "--targets", "a", Path.Combine( _root, "moved" ) ), "a" )["governor-not-performance"] );
      Assert.DoesNotContain( "governor-not-performance", Flags( Consolidate( "--targets", "a", Path.Combine( _root, "fine" ) ), "a" ).Keys );
   }

   /// <summary>
   /// Client and engines on the same logical CPUs, or on hyperthread siblings of the same
   /// physical core, are flagged with the evidence; a partition on separate physical cores is
   /// not; an engine list that was not recorded cannot be judged and is named as missing.
   /// </summary>
   [Fact]
   public void SharedCores_AreFlagged_PerPhysicalCore()
   {
      WriteClean( "same", "2026-10-05T10:00:00Z", r => { Machine( r )["clientCpus"] = "0-3"; Conditions( r )["engineCpus"] = "2-7"; }, Target( "a", 2, 900, 500 ) );
      WriteClean( "sibling", "2026-10-05T11:00:00Z", r => { Machine( r )["clientCpus"] = "0"; Conditions( r )["engineCpus"] = "4-7"; }, Target( "a", 2, 900, 500 ) );
      WriteClean( "apart", "2026-10-05T12:00:00Z", Target( "a", 2, 900, 500 ) );
      WriteClean( "noengine", "2026-10-05T13:00:00Z", r => Conditions( r ).Remove( "engineCpus" ), Target( "a", 2, 900, 500 ) );

      Assert.Equal( "client CPUs 0-3 and engine CPUs 2-7 share CPUs 2-3 and physical cores of CPUs 0-7 (hyperthread siblings)",
         Flags( Consolidate( "--targets", "a", Path.Combine( _root, "same" ) ), "a" )["client-engine-share-cores"] );
      Assert.Equal( "client CPUs 0 and engine CPUs 4-7 share physical cores of CPUs 0,4 (hyperthread siblings)",
         Flags( Consolidate( "--targets", "a", Path.Combine( _root, "sibling" ) ), "a" )["client-engine-share-cores"] );
      Assert.DoesNotContain( "client-engine-share-cores", Flags( Consolidate( "--targets", "a", Path.Combine( _root, "apart" ) ), "a" ).Keys );
      Dictionary<string, string> noEngine = Flags( Consolidate( "--targets", "a", Path.Combine( _root, "noengine" ) ), "a" );
      Assert.DoesNotContain( "client-engine-share-cores", noEngine.Keys );
      Assert.Equal( "conditions.engineCpus", noEngine["fields-missing"] );
   }

   /// <summary>
   /// Without thread sibling data only identical logical CPUs count as shared.
   /// </summary>
   [Fact]
   public void SharedCores_WithoutTopology_UsesLogicalCpusOnly()
   {
      WriteClean( "r1", "2026-10-05T10:00:00Z", r => { Machine( r ).Remove( "threadSiblings" ); Machine( r )["clientCpus"] = "0"; Conditions( r )["engineCpus"] = "4-7"; }, Target( "a", 2, 900, 500 ) );

      JsonElement json = Consolidate( "--targets", "a", Path.Combine( _root, "r1" ) );

      Assert.DoesNotContain( "client-engine-share-cores", Flags( json, "a" ).Keys );
   }

   /// <summary>
   /// A Debug build is flagged. An old run that recorded no build still shows it, derived from
   /// the path of the binary in its command line and labelled as derived; the warm-up count and
   /// the exact-mode seconds are likewise recovered from the method note and the command line.
   /// </summary>
   [Fact]
   public void Conditions_AreDerivedFromTheCommandLine_AndLabelled()
   {
      void OldRun( JsonObject r )
      {
         Legacy( r );
         r["commandLine"] = "/repo/src/GenericVectorBuilder.Bench/bin/Debug/net10.0/GenericVectorBuilder.Bench.dll run-all --pipeline x --exact-seconds 30";
         r["notes"] = new JsonArray( "Warm-up: every timed pass started with its own untimed warm-up of 25 searches in the same search mode." );
      }

      WriteRun( "r1", "2026-10-05T10:00:00Z", OldRun, Old( Target( "a", 5.5, 900, 190 ) ) );

      JsonElement json = Consolidate( "--targets", "a", Path.Combine( _root, "r1" ) );

      JsonElement settings = json.GetProperty( "settings" );
      Assert.Equal( "Debug", settings.GetProperty( "buildConfiguration" ).GetString() );
      Assert.Equal( 25, settings.GetProperty( "warmupSearches" ).GetInt32() );
      Assert.Equal( 30, settings.GetProperty( "exactSeconds" ).GetInt32() );
      Assert.Equal( "from the path of the binary in the command line", settings.GetProperty( "derived" ).GetProperty( "buildConfiguration" ).GetString() );
      Assert.Equal( "from the method note", settings.GetProperty( "derived" ).GetProperty( "warmupSearches" ).GetString() );
      Assert.Equal( "from the command line", settings.GetProperty( "derived" ).GetProperty( "exactSeconds" ).GetString() );
      Assert.Equal( "Debug build (from the path of the binary in the command line)", Flags( json, "a" )["not-release-build"] );
      string md = File.ReadAllText( Path.Combine( _root, "out", "consolidated.md" ) );
      Assert.Contains( "| build configuration | Debug (from the path of the binary in the command line) |", md );
      Assert.Contains( "| least searches a warm-up must run (the --warmup setting, besides the time above) | 25 (from the method note) |", md );
      Assert.Contains( "| warm-up before each timed pass | a fixed count of 25 searches before each timed pass (older run: no warm-up time, settle trial or extension recorded) |", md );
   }

   /// <summary>
   /// Load rows/s is still reported (its median, min and max) but it is not ranked, and the
   /// markdown says why: the load wrote only 524 rows.
   /// </summary>
   [Fact]
   public void LoadRowsPerSecond_IsReported_ButNeverRanked()
   {
      WriteClean( "r1", "2026-10-05T10:00:00Z", Target( "a", 2, 900, 500 ), Target( "b", 3, 800, 330 ) );
      WriteClean( "r2", "2026-10-05T11:00:00Z", Target( "a", 2, 900, 500 ), Target( "b", 3, 800, 330 ) );

      JsonElement json = Consolidate( "--targets", "a,b", Path.Combine( _root, "r?" ) );

      foreach( JsonElement target in json.GetProperty( "targetSummaries" ).EnumerateArray() )
      {
         Assert.False( target.GetProperty( "ranks" ).TryGetProperty( "loadRowsPerSecond", out _ ) );
         Assert.True( target.GetProperty( "ranks" ).TryGetProperty( "p50Ms", out _ ) );
         Assert.Equal( 1000.0, target.GetProperty( "loadRowsPerSecond" ).GetProperty( "median" ).GetDouble() );
      }

      Assert.Contains( json.GetProperty( "notes" ).EnumerateArray().Select( n => n.GetString() ), n => n!.StartsWith( "Load rows/s is reported but not ranked", StringComparison.Ordinal ) && n.Contains( "524 rows", StringComparison.Ordinal ) );
      string md = File.ReadAllText( Path.Combine( _root, "out", "consolidated.md" ) );
      Assert.Contains( "load rows/s (not ranked)", md );
      Assert.Contains( "## Notes", md );
      Assert.Contains( "- Load rows/s is reported but not ranked", md );
      Assert.DoesNotContain( "loadRowsPerSecond", md );
   }

   /// <summary>
   /// There is no default pair (sql and sql-diskann live in different databases); a pair given
   /// with --pairs is computed and the notes say the two targets hold separate copies.
   /// </summary>
   [Fact]
   public void Pairs_AreOnlyThoseAskedFor_AndSaySeparateCopies()
   {
      WriteClean( "r1", "2026-10-05T10:00:00Z", Target( "sql", 2, 900, 500 ), Target( "sql-diskann", 2, 900, 500 ), Target( "qdrant", 2, 900, 500 ), Target( "qdrant-hnsw", 2, 900, 500 ) );

      JsonElement none = Consolidate( "--targets", "sql,sql-diskann,qdrant,qdrant-hnsw", Path.Combine( _root, "r1" ) );
      Assert.Equal( 0, none.GetProperty( "pairs" ).GetArrayLength() );
      Assert.DoesNotContain( none.GetProperty( "notes" ).EnumerateArray().Select( n => n.GetString() ), n => n!.Contains( "their own copy of the data", StringComparison.Ordinal ) );

      JsonElement asked = Consolidate( "--targets", "sql,sql-diskann", "--pairs", "sql-diskann:sql", Path.Combine( _root, "r1" ) );
      Assert.Equal( 1, asked.GetProperty( "pairs" ).GetArrayLength() );
      Assert.Contains( asked.GetProperty( "notes" ).EnumerateArray().Select( n => n.GetString() ), n => n!.Contains( "their own copy of the data", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// The consolidated.md carries every new flag in its Flags table, and no em-dash anywhere.
   /// </summary>
   [Fact]
   public void Markdown_ListsTheNewFlags_WithoutEmDashes()
   {
      JsonObject unsettled = Target( "a", 6.72, 800, 191.6 );
      unsettled["settled"] = false;
      WriteClean( "r1", "2026-10-05T10:00:00Z", r => { Machine( r )["governor"] = "schedutil"; Machine( r )["loadAverage"] = "12.0 5.0 3.0"; }, unsettled );
      WriteClean( "r2", "2026-10-05T11:00:00Z", r => { Machine( r )["governor"] = "schedutil"; Machine( r )["loadAverage"] = "12.0 5.0 3.0"; }, Target( "a", 3.0, 1400, 191.6 ) );

      Consolidate( "--targets", "a", Path.Combine( _root, "r?" ) );

      string md = File.ReadAllText( Path.Combine( _root, "out", "consolidated.md" ) );
      foreach( string kind in new[] { "spread", "p50-mean-inconsistent", "unsettled-target", "busy-box", "governor-not-performance" } )
      {
         Assert.Contains( $"| a | {kind} |", md );
      }

      Assert.DoesNotContain( "\u2014", md );
      Assert.DoesNotContain( "\u2013", md );
   }

   /// <summary>
   /// CPU lists are normalized (runs written as ranges, sorted, no duplicates), text that is not
   /// a list is kept as written, and overlap is reported per CPU and per physical core.
   /// </summary>
   [Fact]
   public void CpuSet_NormalizesAndOverlaps()
   {
      Assert.Equal( "0-3,8", CpuSet( "Normalize", "8,3,2,1,0,1" ) );
      Assert.Equal( "5", CpuSet( "Normalize", " 5 " ) );
      Assert.Equal( "3-0", CpuSet( "Normalize", "3-0" ) );
      Assert.Equal( "all of them", CpuSet( "Normalize", "all of them" ) );
      Assert.Null( CpuSet( "Normalize", "  " ) );
      Assert.Null( CpuSet( "Normalize", null ) );

      Type type = COMPILED.Value.GetType( $"{STATS_NAMESPACE}.CpuSet" )!;
      var overlap = (ITuple)type.GetMethod( "Overlap" )!.Invoke( null, new object[] { "0-1", "1-3", new[] { "0,4", "1,5" } } )!;
      Assert.Equal( "1", overlap[0] );
      Assert.Equal( "1,5", overlap[1] );
      var none = (ITuple)type.GetMethod( "Overlap" )!.Invoke( null, new object[] { "0,4", "1-3,5-7", new[] { "0,4", "1,5", "2,6", "3,7" } } )!;
      Assert.Null( none[0] );
      Assert.Null( none[1] );
      var unreadable = (ITuple)type.GetMethod( "Overlap" )!.Invoke( null, new object[] { "all", "1-3", Array.Empty<string>() } )!;
      Assert.Null( unreadable[0] );
   }

   /// <summary>
   /// The conditions the machine-control step writes (a top-level "conditions" object with the
   /// build, governor, CPU split, warm-up and exact-mode settings, per-pass and per-engine
   /// records) are read in place of the "machine" fields, and a run that holds the machine steady
   /// and passed every check has no flag.
   /// </summary>
   [Fact]
   public void MachineControlConditions_AreRead_AndAreClean()
   {
      WriteClean( "r1", "2026-10-05T10:00:00Z", MachineControlStyle, Target( "a", 2, 900, 500 ) );
      WriteClean( "r2", "2026-10-05T11:00:00Z", MachineControlStyle, Target( "a", 2, 905, 500 ) );

      JsonElement json = Consolidate( "--targets", "a", Path.Combine( _root, "r?" ) );

      Assert.Equal( 0, json.GetProperty( "flags" ).GetArrayLength() );
      JsonElement settings = json.GetProperty( "settings" );
      Assert.Equal( "Release", settings.GetProperty( "buildConfiguration" ).GetString() );
      Assert.Equal( "on", settings.GetProperty( "machineControl" ).GetString() );
      Assert.Equal( "performance", settings.GetProperty( "governor" ).GetString() );
      Assert.Equal( PARTITION_TEXT, settings.GetProperty( "cpuPartition" ).GetString() );
      Assert.Equal( 20, settings.GetProperty( "warmupSearches" ).GetInt32() );
      Assert.Equal( 60, settings.GetProperty( "exactSeconds" ).GetInt32() );
      Assert.Contains( "| machine control | on |", File.ReadAllText( Path.Combine( _root, "out", "consolidated.md" ) ) );
   }

   /// <summary>
   /// Machine control "on" and "off" are different experiments and are not mixed, whatever else
   /// matches.
   /// </summary>
   [Fact]
   public void MachineControlOnAndOff_AreNotMixed()
   {
      WriteClean( "r1", "2026-10-05T10:00:00Z", MachineControlStyle, Target( "a", 2, 900, 500 ) );
      WriteClean( "r2", "2026-10-05T11:00:00Z", MachineControlStyle, Target( "a", 2, 905, 500 ) );
      WriteClean( "r3", "2026-10-05T12:00:00Z", r => { MachineControlStyle( r ); Conditions( r )["machineControl"] = "off: --no-machine-control"; }, Target( "a", 2, 905, 500 ) );

      JsonElement json = Consolidate( "--targets", "a", Path.Combine( _root, "r?" ) );

      Assert.Equal( new[] { "r1", "r2" }, Names( json.GetProperty( "runs" ) ) );
      Assert.Equal( "settings differ from the runs used: machineControl off vs on", json.GetProperty( "dropped" )[0].GetProperty( "reason" ).GetString() );
   }

   /// <summary>
   /// A governor, build or other value the writer marked "unknown" counts as not recorded: it is
   /// named in fields-missing and is null in the settings, never a recorded value.
   /// </summary>
   [Fact]
   public void UnknownGovernorAndBuild_AreNotRecorded()
   {
      WriteClean( "r1", "2026-10-05T10:00:00Z", r => { MachineControlStyle( r ); Conditions( r )["governor"] = "unknown"; Conditions( r )["governorAtEnd"] = "unknown"; Conditions( r )["buildConfiguration"] = "unknown"; }, Target( "a", 2, 900, 500 ) );

      JsonElement json = Consolidate( "--targets", "a", Path.Combine( _root, "r1" ) );

      Assert.Equal( JsonValueKind.Null, json.GetProperty( "settings" ).GetProperty( "governor" ).ValueKind );
      Assert.Equal( JsonValueKind.Null, json.GetProperty( "settings" ).GetProperty( "buildConfiguration" ).ValueKind );
      Assert.Equal( "conditions.buildConfiguration, conditions.governor", Flags( json, "a" )["fields-missing"] );
      Assert.DoesNotContain( "governor-not-performance", Flags( json, "a" ).Keys );
   }

   /// <summary>
   /// Per-pass records speak for their own target: a pass recorded as busy flags that target's
   /// busy box with the recorded reason, and a pass under another governor flags that target's
   /// governor; the other targets of the run are not flagged.
   /// </summary>
   [Fact]
   public void PassRecords_FlagOnlyTheirOwnTarget()
   {
      void Passes( JsonObject r )
      {
         MachineControlStyle( r );
         Conditions( r )["passes"] = new JsonArray(
            new JsonObject { ["target"] = "a", ["pass"] = "default@1", ["governor"] = "performance", ["busyBox"] = false },
            new JsonObject { ["target"] = "a", ["pass"] = "default@8", ["governor"] = "performance", ["busyBox"] = true, ["busyReason"] = "outside load 3.20 CPUs during the pass" },
            new JsonObject { ["target"] = "b", ["pass"] = "default@8", ["governor"] = "schedutil", ["busyBox"] = false },
            new JsonObject { ["target"] = "c", ["pass"] = "default@8", ["governor"] = "performance", ["busyBox"] = false } );
      }

      WriteClean( "r1", "2026-10-05T10:00:00Z", Passes, Target( "a", 2, 900, 500 ), Target( "b", 2, 900, 500 ), Target( "c", 2, 900, 500 ) );

      JsonElement json = Consolidate( "--targets", "a,b,c", Path.Combine( _root, "r1" ) );

      Assert.Equal( "busy during default@8: outside load 3.20 CPUs during the pass", Flags( json, "a" )["busy-box"] );
      Assert.DoesNotContain( "governor-not-performance", Flags( json, "a" ).Keys );
      Assert.Equal( "governor schedutil during default@8", Flags( json, "b" )["governor-not-performance"] );
      Assert.DoesNotContain( "busy-box", Flags( json, "b" ).Keys );
      Assert.Empty( Flags( json, "c" ) );
   }

   /// <summary>
   /// Per-engine records decide whether that engine shared cores with the client: an embedded
   /// engine always does, an engine that could not be fully pinned may, an engine held to CPUs that
   /// overlap the client's does, and an engine on separate cores does not.
   /// </summary>
   [Fact]
   public void EngineRecords_DecideSharedCores_PerTarget()
   {
      void Engines( JsonObject r )
      {
         MachineControlStyle( r );
         Conditions( r )["engines"] = new JsonArray(
            new JsonObject { ["target"] = "a", ["hosting"] = "embedded", ["problems"] = new JsonArray() },
            new JsonObject { ["target"] = "b", ["hosting"] = "always-on", ["cpus"] = "0-2", ["problems"] = new JsonArray() },
            new JsonObject { ["target"] = "c", ["hosting"] = "compose", ["cpus"] = "1-3,5-7", ["problems"] = new JsonArray( "docker update failed" ) },
            new JsonObject { ["target"] = "d", ["hosting"] = "compose", ["cpus"] = "1-3,5-7", ["problems"] = new JsonArray() } );
      }

      WriteClean( "r1", "2026-10-05T10:00:00Z", Engines, Target( "a", 2, 900, 500 ), Target( "b", 2, 900, 500 ), Target( "c", 2, 900, 500 ), Target( "d", 2, 900, 500 ) );

      JsonElement json = Consolidate( "--targets", "a,b,c,d", Path.Combine( _root, "r1" ) );

      Assert.Equal( "embedded engine runs inside the client process on the client CPUs 0,4", Flags( json, "a" )["client-engine-share-cores"] );
      Assert.Equal( "client CPUs 0,4 and engine CPUs 0-2 share CPUs 0 and physical cores of CPUs 0,4 (hyperthread siblings)", Flags( json, "b" )["client-engine-share-cores"] );
      Assert.Equal( "engine not fully pinned to its CPUs: docker update failed", Flags( json, "c" )["client-engine-share-cores"] );
      Assert.Empty( Flags( json, "d" ) );
   }

   /// <summary>
   /// A listed target's index description (which carries its search effort: ef, probes, scan
   /// thresholds) is part of the settings: a run whose description differs is dropped and the
   /// reason shows both texts.
   /// </summary>
   [Fact]
   public void DropsRuns_WhoseIndexDescriptionDiffers()
   {
      JsonObject changed = Target( "sql", 2, 900, 500 );
      changed["index"] = "HNSW m=16, mhnsw_ef_search=100";
      JsonObject original = Target( "sql", 2, 900, 500 );
      original["index"] = "HNSW m=16, mhnsw_ef_search=3200";
      WriteClean( "r1", "2026-10-05T10:00:00Z", original );
      WriteClean( "r2", "2026-10-05T11:00:00Z", Target( "sql", 2, 905, 500 ) );
      WriteClean( "r3", "2026-10-05T12:00:00Z", Target( "sql", 2, 910, 500 ) );
      WriteClean( "r4", "2026-10-05T13:00:00Z", changed );

      JsonElement json = Consolidate( "--targets", "sql", Path.Combine( _root, "r?" ) );

      Assert.Equal( new[] { "r2", "r3" }, Names( json.GetProperty( "runs" ) ) );
      Assert.Equal( 2, json.GetProperty( "dropped" ).GetArrayLength() );
      Assert.Contains( json.GetProperty( "dropped" ).EnumerateArray().Select( d => d.GetProperty( "reason" ).GetString() ), r => r == "settings differ from the runs used: index[sql] HNSW m=16, mhnsw_ef_search=100 vs test index" );
   }

   /// <summary>
   /// A target that declares its fair default pair (pairHint) has it carried into the
   /// exact-versus-default comparison with the target's own note, in the JSON and in the markdown.
   /// </summary>
   [Fact]
   public void DeclaredPairHint_IsCarriedIntoTheExactComparison()
   {
      JsonObject diskann = Target( "sql-diskann", 4.0, 900, 250, exactP50: 8.0 );
      diskann["pairHint"] = new JsonObject { ["target"] = "sql-diskann", ["passA"] = "default@1", ["passB"] = "exact", ["note"] = "same table, only the search method differs" };
      WriteClean( "r1", "2026-10-05T10:00:00Z", diskann, Target( "sql", 4, 900, 250 ) );

      JsonElement json = Consolidate( "--targets", "sql,sql-diskann", Path.Combine( _root, "r1" ) );

      Assert.Equal( 0, json.GetProperty( "pairs" ).GetArrayLength() );
      JsonElement exact = Assert.Single( json.GetProperty( "exactVsDefault" ).EnumerateArray() );
      Assert.Equal( "default@1 vs exact", exact.GetProperty( "declaredPair" ).GetString() );
      Assert.Equal( "same table, only the search method differs", exact.GetProperty( "note" ).GetString() );
      Assert.Contains( "- sql-diskann, default pair default@1 vs exact: same table, only the search method differs", File.ReadAllText( Path.Combine( _root, "out", "consolidated.md" ) ) );
   }

   /// <summary>
   /// The measurement step's own settle notes are read when no structured field says: "WARNING:
   /// latency had NOT settled ..." flags the target with that text, "Settle: settled after ..."
   /// does not, and a structured settled value wins over the notes.
   /// </summary>
   [Fact]
   public void SettleNotes_AreReadWhenNoFieldSaysSo()
   {
      JsonObject bad = Target( "bad", 2, 900, 500 );
      bad.Remove( "settled" );
      bad["notes"] = new JsonArray( "Settle: unavailable", "WARNING: latency had NOT settled when timing began (the 90 s cap ran out) after 90.0 s and 4,000 searches (0 failed)." );
      JsonObject good = Target( "good", 2, 900, 500 );
      good.Remove( "settled" );
      good["notes"] = new JsonArray( "Settle: settled after 3.1 s and 900 searches (0 failed); p50 of the last windows of 100: 1.9, 2.0 ms, within 10% across 3 windows." );
      JsonObject wins = Target( "wins", 2, 900, 500 );
      wins["notes"] = new JsonArray( "WARNING: latency had NOT settled when timing began (x)." );
      WriteClean( "r1", "2026-10-05T10:00:00Z", bad, good, wins );

      JsonElement json = Consolidate( "--targets", "bad,good,wins", Path.Combine( _root, "r1" ) );

      Assert.Equal( "not settled: latency had NOT settled when timing began (the 90 s cap ran out) after 90.0 s and 4,000 searches (0 failed).", Flags( json, "bad" )["unsettled-target"] );
      Assert.Empty( Flags( json, "good" ) );
      Assert.Empty( Flags( json, "wins" ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Writes a synthetic run that records everything the current harness does, so it gets no
   /// "fields-missing" flag: run seed and target order, and for each target its pass order,
   /// warm-up errors, a ready index state and a durability statement.
   /// </summary>
   /// <param name="name">Folder name.</param>
   /// <param name="started">startedUtc.</param>
   /// <param name="targets">Target objects.</param>
   private void WriteClean( string name, string started, params JsonObject[] targets )
   {
      WriteClean( name, started, _ => { }, targets );
   }

   /// <summary>
   /// Writes a complete synthetic run, letting the caller change run-level fields.
   /// </summary>
   /// <param name="name">Folder name.</param>
   /// <param name="started">startedUtc.</param>
   /// <param name="change">Changes to the run object.</param>
   /// <param name="targets">Target objects.</param>
   private void WriteClean( string name, string started, Action<JsonObject> change, params JsonObject[] targets )
   {
      foreach( JsonObject target in targets )
      {
         target["passOrder"] = new JsonArray( "default@1", "default@8" );
         target["warmupErrors"] = 0;
         target["indexState"] = new JsonObject { ["afterLoad"] = State( true, 524, 524, "ok" ), ["afterSearch"] = State( true, 524, 524, "ok" ) };
         target["durability"] = "full recovery model, log flushed on commit";
      }

      WriteRun( name, started, run =>
      {
         run["notes"] = new JsonArray( MethodNotes().Select( n => (JsonNode)JsonValue.Create( n )! ).ToArray() );
         run["runSeed"] = 1;
         run["targetOrder"] = new JsonArray( targets.Select( t => JsonValue.Create( t["name"]!.GetValue<string>() )! ).ToArray<JsonNode>() );
         change( run );
      }, targets );
   }

   /// <summary>
   /// Adds every condition the current harness records to a synthetic run: governor, build,
   /// client CPUs, thread siblings and CPU count under "machine"; engine CPUs, warm-up and
   /// exact-mode seconds under "conditions". Two containers, as the real files may use.
   /// </summary>
   /// <param name="run">The run object.</param>
   private static void Conditioned( JsonObject run )
   {
      JsonObject machine = Machine( run );
      machine["logicalCpus"] = 8;
      machine["governor"] = "performance";
      machine["buildConfiguration"] = "Release";
      machine["clientCpus"] = "0,4";
      machine["threadSiblings"] = new JsonArray( "0,4", "1,5", "2,6", "3,7" );
      run["conditions"] = new JsonObject { ["engineCpus"] = "1-3,5-7", ["warmupSearches"] = 20, ["exactSeconds"] = 60 };
   }

   /// <summary>
   /// Replaces the conditions of a synthetic run with the shape the machine-control step writes:
   /// everything under a top-level "conditions" object, with per-pass and per-engine records, and
   /// only the box's CPU count and load under "machine".
   /// </summary>
   /// <param name="run">The run object.</param>
   private static void MachineControlStyle( JsonObject run )
   {
      Legacy( run );
      run["conditions"] = new JsonObject
      {
         ["buildConfiguration"] = "Release",
         ["jitOptimizerDisabled"] = false,
         ["machineControl"] = "on",
         ["governor"] = "performance",
         ["governorBefore"] = "schedutil",
         ["governorAtEnd"] = "performance",
         ["logicalCpus"] = 8,
         ["threadSiblings"] = new JsonArray( "0,4", "1,5", "2,6", "3,7" ),
         ["clientCpus"] = "0,4",
         ["engineCpus"] = "1-3,5-7",
         ["warmupSearches"] = 20,
         ["exactSeconds"] = 60,
         ["search"] = new JsonObject { ["top"] = 10, ["warmupSearches"] = 20, ["exactSeconds"] = 60, ["seed"] = 11 },
         ["passes"] = new JsonArray(),
         ["engines"] = new JsonArray(),
      };
   }

   /// <summary>
   /// Removes the conditions a run written before they existed would not have (the old harness
   /// did write the CPU count).
   /// </summary>
   /// <param name="run">The run object.</param>
   private static void Legacy( JsonObject run )
   {
      JsonObject machine = Machine( run );
      foreach( string key in new[] { "governor", "buildConfiguration", "clientCpus", "threadSiblings" } )
      {
         machine.Remove( key );
      }

      run.Remove( "conditions" );
   }

   /// <summary>
   /// Removes the target fields a run written before they existed would not have.
   /// </summary>
   /// <param name="target">The target object.</param>
   /// <returns>The same object.</returns>
   private static JsonObject Old( JsonObject target )
   {
      target.Remove( "settled" );
      target.Remove( "searchSettings" );
      return target;
   }

   /// <summary>
   /// A run's "machine" object.
   /// </summary>
   /// <param name="run">The run object.</param>
   /// <returns>The machine object.</returns>
   private static JsonObject Machine( JsonObject run )
   {
      return (JsonObject)run["machine"]!;
   }

   /// <summary>
   /// A run's "conditions" object.
   /// </summary>
   /// <param name="run">The run object.</param>
   /// <returns>The conditions object.</returns>
   private static JsonObject Conditions( JsonObject run )
   {
      return (JsonObject)run["conditions"]!;
   }

   /// <summary>
   /// The flags of one target in consolidated.json, by kind, with their detail text.
   /// </summary>
   /// <param name="json">consolidated.json root.</param>
   /// <param name="target">Target name.</param>
   /// <returns>Detail by kind.</returns>
   private static Dictionary<string, string> Flags( JsonElement json, string target )
   {
      return json.GetProperty( "flags" ).EnumerateArray().Where( f => f.GetProperty( "target" ).GetString() == target )
         .ToDictionary( f => f.GetProperty( "kind" ).GetString()!, f => f.GetProperty( "detail" ).GetString()! );
   }

   /// <summary>
   /// Calls a static string method of the compiled CpuSet class.
   /// </summary>
   /// <param name="method">Method name.</param>
   /// <param name="argument">The argument.</param>
   /// <returns>The result.</returns>
   private static string? CpuSet( string method, string? argument )
   {
      return (string?)COMPILED.Value.GetType( $"{STATS_NAMESPACE}.CpuSet" )!.GetMethod( method )!.Invoke( null, new object?[] { argument } );
   }

   #endregion Private Methods
}
