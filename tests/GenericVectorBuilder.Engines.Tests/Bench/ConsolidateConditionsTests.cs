using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The readers of a run's conditions: the clock from a v8 run's conditions.clock and from a v5 to v7
/// run's machine-control note (the first MSR value, the pinned and the ceiling MHz, gap G32), the
/// build object never read as the build configuration (gap G35), the dockerd rule a v8 run records,
/// the median clock rule recomputed from per-CPU medians with its all-CPU fallback, a recorded
/// clockOff that disagrees refused, and the busy flag taken from the pass's own outside load, never
/// from the run's load average.
/// </summary>
public sealed class ConsolidateConditionsTests : IDisposable
{
   #region Data Members

   /// <summary>The v7 machine-control note's clock part, verbatim from run 20261006-130619.</summary>
   private const string V7_NOTE = "Machine control on: governor performance on every CPU during the run (before: schedutil; at the end: performance; after putting it back: schedutil). CPU clock pinned for the run: turbo off (intel_pstate/no_turbo 0 -> 1), so every CPU's clock is held at its ceiling of 3500 MHz whatever the engine runs; the uncore (L3 and memory) clock is held at 3000 MHz (min 3000 MHz, max 3000 MHz (MSR 0x620 = 0x1e1e); was min 1200 MHz, max 3000 MHz (MSR 0x620 = 0xc1e)) (before: turbo on (intel_pstate/no_turbo 0), ceiling 3600 MHz on CPUs 0-7, floor 1200 MHz on CPUs 0-7). Each target's clock note gives every pass's average MHz on the engine CPUs and on the client CPUs; a pass whose average on either is more than 1% off the pinned 3500 MHz is flagged.";

   private readonly SyntheticBench _bench = new();

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// A v7 note: turbo off during the run, the first MSR value (0x1e1e, not the 0xc1e put back), 3500 MHz pinned, 3600 before, 1% tolerance.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task LegacyNote_GivesTheClockOfTheRun()
   {
      await ConsolidateHarness.Timed( () =>
      {
         var run = new JsonObject { ["notes"] = new JsonArray( "Order: x.", V7_NOTE ), ["targets"] = new JsonArray(), ["conditions"] = new JsonObject { ["machineControl"] = "on" } };
         string[] read = Read( run );
         Assert.Equal( new[] { "True", "3500", "3600", "1", "0x1e1e", "100", "True", "notes[1]" }, read.Take( 8 ).ToArray() );
      } );
   }

   /// <summary>
   /// A v8 conditions.clock is read as recorded and normalised ("0x1E1E" reads "0x1e1e"); a run with neither reads as not pinned.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task StructuredClock_IsReadAndNormalised()
   {
      await ConsolidateHarness.Timed( () =>
      {
         var clock = new JsonObject
         {
            ["pinned"] = true, ["pinnedMhz"] = 3500, ["ceilingBeforeMhz"] = 3600, ["toleranceBp"] = 100,
            ["noTurbo"] = new JsonObject { ["before"] = 0, ["during"] = 1 }, ["uncoreMsr620"] = new JsonObject { ["during"] = "0x1E1E" },
         };
         var run = new JsonObject { ["targets"] = new JsonArray(), ["conditions"] = new JsonObject { ["clock"] = clock } };
         Assert.Equal( new[] { "True", "3500", "3600", "1", "0x1e1e", "100", "False", "conditions.clock" }, Read( run ).Take( 8 ).ToArray() );
         var none = new JsonObject { ["targets"] = new JsonArray() };
         Assert.Equal( "False", Read( none )[0] );
      } );
   }

   /// <summary>
   /// conditions.build {informationalVersion, commit} gives the commit and never becomes the build configuration (gap G35);
   /// the engineCpu rule, boot id and settingsOnly are read; MissingNames stays empty when the conditions are complete.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task BuildObject_IsTheCommit_NotTheConfiguration()
   {
      await ConsolidateHarness.Timed( () =>
      {
         var conditions = new JsonObject
         {
            ["buildConfiguration"] = "Release", ["governor"] = "performance", ["clientCpus"] = "0-1,4-5", ["engineCpus"] = "2-3,6-7", ["warmupSearches"] = 20, ["exactSeconds"] = 60,
            ["build"] = new JsonObject { ["informationalVersion"] = "1.0.0+abc123", ["commit"] = "abc123" },
            ["boot"] = new JsonObject { ["bootId"] = "b-1", ["bootTimeUtc"] = "2026-10-03T15:53:39Z" },
            ["engineCpu"] = new JsonObject { ["rule"] = "dockerd is subtracted" },
         };
         var run = new JsonObject { ["settingsOnly"] = true, ["targets"] = new JsonArray(), ["conditions"] = conditions };
         string[] read = Read( run );
         Assert.Equal( new[] { "Release", "abc123", "b-1", "dockerd is subtracted", string.Empty, "True" }, read.Skip( 8 ).ToArray() );
         conditions.Remove( "buildConfiguration" );
         Assert.Equal( "null", Read( run )[8] );
      } );
   }

   /// <summary>
   /// The median rule: a group's median of per-CPU medians more than 1% off the pinned clock is off; with the CPUs not split, every CPU is one group.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task MedianRule_JudgesGroups_AndFallsBackToAllCpus()
   {
      await ConsolidateHarness.Timed( () =>
      {
         int[] engineLow = { 3492, 3492, 3300, 3300, 3492, 3492, 3300, 3300 };
         Assert.Equal( new[] { "3300|3492|True|True" }, Clocks( engineLow, split: true ) );
         Assert.Equal( new[] { "3396|3396|True|True" }, Clocks( engineLow, split: false ) );
         int[] steady = Enumerable.Repeat( 3492, 8 ).ToArray();
         Assert.Equal( new[] { "3492|3492|True|False" }, Clocks( steady, split: true ) );
      } );
   }

   /// <summary>
   /// A v8 run whose recorded clockOff disagrees with the medians it recorded is refused: the recorder and the reader must implement one rule.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task RecordedClockOff_ThatDisagrees_IsRefused()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string runs = _bench.WriteSession( "2026-10-06", 701, _ => ConsolidateTests.Pair(), ( i, run ) =>
         {
            if( i == 1 )
            {
               run["conditions"]!["passes"]![0]!["clockOff"] = true;
            }
         } );
         string[] folders = runs.Split( ',' ).Select( f => Path.Combine( _bench.Results, f ) ).ToArray();
         string result = Assert.Single( (string[])ConsolidateHarness.Call( "SessionClock", (object)folders )! );
         Assert.StartsWith( "refused: ", result );
         Assert.Contains( "recorded clockOff True, recomputed False", result );
         Assert.Equal( 1, _bench.Consolidate( "--session", "v7=" + runs ).Exit );
      } );
   }

   /// <summary>
   /// A load average above the CPU count raises no flag; a pass the run flagged busy from its own outside load does, with the load in its sentence.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task BusyFlag_ComesFromThePass_NotTheLoadAverage()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string runs = _bench.WriteSession( "2026-10-06", 701, _ => ConsolidateTests.Pair(), ( i, run ) =>
         {
            run["machine"]!["loadAverage"] = "9.50 9.00 8.00";
            if( i == 2 )
            {
               JsonNode pass = run["conditions"]!["passes"]!.AsArray().First( p => (string)p!["target"]! == "beta" && (string)p["pass"]! == "default@1" )!;
               pass["busyBox"] = true;
               pass["outsideLoadDuring"] = 0.41;
            }
         } );
         ( int exit, JsonElement? json, _ ) = _bench.Consolidate( "--session", "v7=" + runs );
         Assert.True( exit == 0, _bench.LogText() );
         JsonElement p50 = json!.Value.GetProperty( "metrics" )[0];
         Assert.Empty( p50.GetProperty( "rows" ).EnumerateArray().First( r => r.GetProperty( "target" ).GetString() == "alpha" ).GetProperty( "flags" ).EnumerateArray() );
         JsonElement flag = Assert.Single( p50.GetProperty( "rows" ).EnumerateArray().First( r => r.GetProperty( "target" ).GetString() == "beta" ).GetProperty( "flags" ).EnumerateArray() );
         Assert.Equal( "busy-box", flag.GetProperty( "code" ).GetString() );
         Assert.Equal( "Run 20261006-120000-eshoponweb: busy box during the one-searcher pass, with 0.410 CPUs of outside load.", flag.GetProperty( "text" ).GetString() );
      } );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The facade's reading of a run's conditions, split on '|'.
   /// </summary>
   /// <param name="run">The run object.</param>
   /// <returns>Pinned, pinned MHz, ceiling, no_turbo during, uncore, tolerance, legacy, source, build configuration, commit, boot id, engine CPU rule, missing names, settings-only.</returns>
   private static string[] Read( JsonObject run )
   {
      return ( (string)ConsolidateHarness.Call( "Conditions", run.ToJsonString() )! ).Split( '|' );
   }

   /// <summary>
   /// The recomputed clock of one pass with the given per-CPU medians, pinned at 3500 MHz.
   /// </summary>
   /// <param name="medians">Median per CPU 0 to 7.</param>
   /// <param name="split">True to record the engine and client CPUs.</param>
   /// <returns>"engine|client|read|off" per pass.</returns>
   private static string[] Clocks( int[] medians, bool split )
   {
      var cpus = new JsonArray( medians.Select( ( m, i ) => (JsonNode)new JsonObject { ["cpu"] = i, ["median"] = m } ).ToArray() );
      var conditions = new JsonObject
      {
         ["clock"] = new JsonObject { ["pinned"] = true, ["pinnedMhz"] = 3500, ["toleranceBp"] = 100, ["noTurbo"] = new JsonObject { ["during"] = 1 } },
         ["passes"] = new JsonArray( new JsonObject { ["target"] = "alpha", ["pass"] = "default@1", ["cpuMhz"] = cpus } ),
      };
      if( split )
      {
         conditions["engineCpus"] = "2-3,6-7";
         conditions["clientCpus"] = "0-1,4-5";
      }

      return (string[])ConsolidateHarness.Call( "PassClocks", new JsonObject { ["targets"] = new JsonArray(), ["conditions"] = conditions }.ToJsonString() )!;
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Removes the scratch folders.
   /// </summary>
   public void Dispose()
   {
      _bench.Dispose();
   }

   #endregion IDisposable
}
