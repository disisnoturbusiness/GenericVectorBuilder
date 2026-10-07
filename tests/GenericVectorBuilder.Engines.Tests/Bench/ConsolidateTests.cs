using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The consolidate command end to end on synthetic result folders in the v8 shape: its command
/// line, its refusals (each writes nothing and says why), its exit codes and the one-session report.
/// Why refusals and not drops: the v7 command dropped mismatched runs and kept "the largest group",
/// so a page could say "median of 6" over fewer; v8 refuses and names the runs and fields.
/// </summary>
public sealed class ConsolidateTests : IDisposable
{
   #region Data Members

   private readonly SyntheticBench _bench = new();

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// --help prints every option and exits 0 (the run tooling greps it for --observer).
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Help_ListsEveryOption()
   {
      await ConsolidateHarness.Timed( () =>
      {
         var result = (string[])ConsolidateHarness.Call( "Consolidate", (object)new[] { "--help" } )!;
         Assert.Equal( "0", result[0] );
         string usage = string.Join( "\n", result.Skip( 1 ) );
         foreach( string option in new[] { "--session", "--basis-session", "--out", "--results-dir", "--targets", "--facts", "--exclusions", "--repo", "--observer" } )
         {
            Assert.Contains( option, usage );
         }

         Assert.DoesNotContain( "largest", usage );
      } );
   }

   /// <summary>
   /// A claim session must hold exactly three runs; two are refused with the names, and nothing is written.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Session_WithoutThreeRuns_IsRefused()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string runs = _bench.WriteSession( "2026-10-06", 701, _ => Pair() );
         string two = string.Join( ",", runs.Split( ',' ).Take( 2 ) );
         ( int exit, JsonElement? json, _ ) = _bench.Consolidate( "--session", "v7=" + two );
         Assert.Equal( 1, exit );
         Assert.Null( json );
         Assert.Contains( "needs exactly 3", _bench.LogText() );
         Assert.Contains( "20261006-100000-eshoponweb", _bench.LogText() );
      } );
   }

   /// <summary>
   /// Runs whose run-level fields differ (another query count) are refused, never dropped to a group.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task MismatchedRuns_AreRefused_NeverDropped()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string runs = _bench.WriteSession( "2026-10-06", 701, _ => Pair(), ( i, run ) => run["queryCount"] = i == 2 ? 200 : 20 );
         ( int exit, JsonElement? json, _ ) = _bench.Consolidate( "--session", "v7=" + runs );
         Assert.Equal( 1, exit );
         Assert.Null( json );
         Assert.Contains( "not one experiment", _bench.LogText() );
         Assert.Contains( "queryCount: 200 vs 20", _bench.LogText() );
      } );
   }

   /// <summary>
   /// A settings-only run searched nothing and cannot be a claim run.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task SettingsOnlyRun_IsRefused()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string runs = _bench.WriteSession( "2026-10-06", 701, _ => Pair(), ( i, run ) => run["settingsOnly"] = i == 1 );
         ( int exit, _, _ ) = _bench.Consolidate( "--session", "v7=" + runs );
         Assert.Equal( 1, exit );
         Assert.Contains( "settings-only", _bench.LogText() );
      } );
   }

   /// <summary>
   /// The earlier session comes first: drift and G2 read the sessions in order.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Sessions_OutOfTimeOrder_AreRefused()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string v7 = _bench.WriteSession( "2026-10-06", 701, _ => Pair() );
         string v8 = _bench.WriteSession( "2026-10-08", 801, _ => Pair() );
         ( int exit, _, _ ) = _bench.Consolidate( "--session", "v8=" + v8, "--session", "v7=" + v7 );
         Assert.Equal( 1, exit );
         Assert.Contains( "must be the earlier one", _bench.LogText() );
      } );
   }

   /// <summary>
   /// The output folder must be new or empty and is never a published or withdrawn folder (exit 2, nothing written).
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task OutFolder_MustBeNew_AndNeverPublished()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string runs = _bench.WriteSession( "2026-10-06", 701, _ => Pair() );
         string busy = Path.Combine( _bench.Results, "..", "busy" );
         Directory.CreateDirectory( busy );
         File.WriteAllText( Path.Combine( busy, "keep.txt" ), "x" );
         Assert.Equal( 2, _bench.Consolidate( "--session", "v7=" + runs, "--out", busy ).Exit );
         Assert.Equal( "x", File.ReadAllText( Path.Combine( busy, "keep.txt" ) ) );
         string published = Path.Combine( _bench.Results, "..", "published-2026-10-09" );
         Assert.Equal( 2, _bench.Consolidate( "--session", "v7=" + runs, "--out", published ).Exit );
         Assert.False( Directory.Exists( published ) );
      } );
   }

   /// <summary>
   /// A listed target missing from a claim run is refused; a target left out of --targets is listed with its reason.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Targets_MissingIsRefused_LeftOutIsListed()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string runs = _bench.WriteSession( "2026-10-06", 701, _ => Pair() );
         Assert.Equal( 1, _bench.Consolidate( "--session", "v7=" + runs, "--targets", "alpha,omega" ).Exit );
         Assert.Contains( "has no result for omega", _bench.LogText() );
         ( int exit, JsonElement? json, string? md ) = _bench.Consolidate( "--session", "v7=" + runs, "--targets", "alpha" );
         Assert.True( exit == 0, _bench.LogText() );
         JsonElement left = Assert.Single( json!.Value.GetProperty( "notInReport" ).EnumerateArray() );
         Assert.Equal( "beta", left.GetProperty( "target" ).GetString() );
         Assert.Equal( "left out by --targets", left.GetProperty( "code" ).GetString() );
         Assert.Contains( "beta has no row: it was left out by --targets.", md );
      } );
   }

   /// <summary>
   /// --basis-session takes one session per value or several, an item holding '=' starting the next (gap G33).
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task BasisSessionGrammar_SplitsOnNames()
   {
      await ConsolidateHarness.Timed( () =>
      {
         Assert.Equal( new[] { "v5:a,b,c", "v6:d,e,f" }, (string[])ConsolidateHarness.Call( "SplitSessions", "v5=a,b,c,v6=d,e,f" )! );
         Assert.Equal( new[] { "v7:a,b,c" }, (string[])ConsolidateHarness.Call( "SplitSessions", "v7=a, b ,c" )! );
         Assert.StartsWith( "refused: ", ( (string[])ConsolidateHarness.Call( "SplitSessions", "a,b,c" )! )[0] );
         Assert.StartsWith( "refused: ", ( (string[])ConsolidateHarness.Call( "SplitSessions", "v7=a,,b" )! )[0] );
      } );
   }

   /// <summary>
   /// A missing fact sheet is refused; a fact that does not hold in the runs refuses the report and nothing is written.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Facts_MissingOrFalse_RefuseTheReport()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string runs = _bench.WriteSession( "2026-10-06", 701, _ => Pair() );
         Assert.Equal( 1, _bench.Consolidate( "--session", "v7=" + runs, "--facts", Path.Combine( _bench.Repo, "nope.json" ) ).Exit );
         Assert.Contains( "does not exist", _bench.LogText() );
         string facts = _bench.WriteFacts( new[] { "alpha", "beta" } );
         File.WriteAllText( facts, File.ReadAllText( facts ).Replace( "HNSW test index m=16", "HNSW test index m=99", StringComparison.Ordinal ) );
         ( int exit, JsonElement? json, _ ) = _bench.Consolidate( "--session", "v7=" + runs );
         Assert.Equal( 1, exit );
         Assert.Null( json );
         Assert.Contains( "engine fact sheet", _bench.LogText() );
      } );
   }

   /// <summary>
   /// One session: every row is one-session, the headline says no engine is named, and every sentence was audited.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task OneSession_IsWrittenUnranked()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string runs = _bench.WriteSession( "2026-10-06", 701, _ => Pair() );
         ( int exit, JsonElement? json, string? md ) = _bench.Consolidate( "--session", "v7=" + runs );
         Assert.True( exit == 0, _bench.LogText() );
         JsonElement root = json!.Value;
         Assert.Equal( "one-session", root.GetProperty( "mode" ).GetString() );
         Assert.All( root.GetProperty( "metrics" ).EnumerateArray().SelectMany( m => m.GetProperty( "rows" ).EnumerateArray() ), r => Assert.Equal( "one-session", r.GetProperty( "status" ).GetString() ) );
         JsonElement headline = root.GetProperty( "sentences" ).EnumerateArray().First( s => s.GetProperty( "slot" ).GetString() == "headline" );
         Assert.Equal( "One session of 3 runs: rows are not ranked, and this report names no engine as ahead of another.", headline.GetProperty( "text" ).GetString() );
         JsonElement audit = root.GetProperty( "audit" );
         Assert.Equal( root.GetProperty( "sentences" ).GetArrayLength() + audit.GetProperty( "rowSentencesChecked" ).GetInt32(), audit.GetProperty( "sentencesChecked" ).GetInt32() );
         Assert.Contains( headline.GetProperty( "text" ).GetString()!, md );
         Assert.DoesNotContain( '\u2014', md! );
      } );
   }

   /// <summary>
   /// The observer summary is copied beside the report and cited; a summary that misses a run of the newest session is refused.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Observer_IsCopiedAndCited_AndMustCoverEveryRun()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string v7 = _bench.WriteSession( "2026-10-06", 701, _ => Pair() );
         string v8 = _bench.WriteSession( "2026-10-08", 801, _ => Pair() );
         string summary = Summary( v8.Split( ',' ) );
         ( int exit, JsonElement? json, string? md ) = _bench.Consolidate( "--session", "v7=" + v7, "--session", "v8=" + v8, "--observer", summary );
         Assert.True( exit == 0, _bench.LogText() );
         string copy = Path.Combine( Path.GetDirectoryName( _bench.Results )!, "out", "observer", "summary.json" );
         Assert.Equal( File.ReadAllText( summary ), File.ReadAllText( copy ) );
         Assert.Contains( "observer/summary.json beside this report", md );
         Assert.Equal( 0.11, json!.Value.GetProperty( "observer" ).GetProperty( "cpuMax" ).GetDouble(), 9 );
         Directory.Delete( Path.GetDirectoryName( copy )!, recursive: true );
         Directory.Delete( Path.Combine( Path.GetDirectoryName( _bench.Results )!, "out" ), recursive: true );
         string partial = Summary( v8.Split( ',' ).Take( 2 ).ToArray() );
         Assert.Equal( 1, _bench.Consolidate( "--session", "v7=" + v7, "--session", "v8=" + v8, "--observer", partial ).Exit );
         Assert.Contains( "does not cover run", _bench.LogText() );
      } );
   }

   /// <summary>
   /// A recorded value that is zero, negative or not a finite number, and a missing one, are refused with the run
   /// and the target named, never skipped (the old median and spread helpers skipped null and NaN without a word).
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task MetricValue_ZeroNegativeOrMissing_IsRefusedNamingRunAndTarget()
   {
      await ConsolidateHarness.Timed( () =>
      {
         foreach( (string label, Action<JsonObject> damage, string expected) in new (string, Action<JsonObject>, string)[]
         {
            ( "zero p50", s => s["p50Ms"] = 0.0, "is 0, not a finite number above zero" ),
            ( "negative qps1", s => ( (JsonObject)s["qps"]! )["1"] = -5.0, "is -5, not a finite number above zero" ),
            ( "missing qps8", s => ( (JsonObject)s["qps"]! ).Remove( "8" ), "has no" ),
         } )
         {
            using var bench = new SyntheticBench();
            string runs = bench.WriteSession( "2026-10-06", 701, i => i == 1
               ? new[] { SyntheticBench.Target( "alpha", 1.0, 1000, 4000 ), SyntheticBench.Target( "beta", 2.0, 500, 2000 ) }.Select( t => { if( (string)t["name"]! == "alpha" ) { damage( (JsonObject)t["search"]! ); } return t; } )
               : Pair() );
            ( int exit, JsonElement? json, _ ) = bench.Consolidate( "--session", "v7=" + runs );
            Assert.True( exit == 1, label + ": " + bench.LogText() );
            Assert.Null( json );
            Assert.Contains( "20261006-110000-eshoponweb", bench.LogText() );
            Assert.Contains( "alpha", bench.LogText() );
            Assert.Contains( expected, bench.LogText() );
         }
      } );
   }

   /// <summary>
   /// Two targets, alpha well ahead of beta.
   /// </summary>
   /// <returns>The targets.</returns>
   public static JsonObject[] Pair()
   {
      return new[] { SyntheticBench.Target( "alpha", 1.0, 1000, 4000 ), SyntheticBench.Target( "beta", 2.0, 500, 2000 ) };
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Writes an observer summary for the given runs.
   /// </summary>
   /// <param name="folders">Run folders.</param>
   /// <returns>Its path.</returns>
   private string Summary( string[] folders )
   {
      var runs = new JsonArray( folders.Select( f => (JsonNode)new JsonObject
      {
         ["folder"] = f,
         ["checks"] = new JsonObject
         {
            ["observerCpu"] = new JsonArray( 0.05, 0.08, 0.11 ), ["aperf"] = new JsonObject { ["worstDeviationBp"] = 40.0 },
            ["msr620"] = new JsonObject { ["valuesSeen"] = new JsonArray( "1e1e" ) }, ["timers"] = new JsonObject { ["covered"] = true, ["passesWithTimerFired"] = new JsonArray() },
         },
      } ).ToArray() );
      string path = Path.Combine( _bench.Repo, $"summary-{folders.Length}.json" );
      File.WriteAllText( path, new JsonObject { ["schema"] = "v8-observer-summary-1", ["runs"] = runs }.ToJsonString() );
      return path;
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
