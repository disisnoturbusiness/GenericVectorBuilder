using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The claim rule (design section 1) and what reads the same per-session ratios: an order is shown
/// only when A's worst run beats B's best run by the threshold in each session separately; rows in
/// median order, each naming the rows it is not separated from; G2 (an order of the first session
/// whose medians reverse in the second stops the report); G3 (a target whose setup changed is shown
/// with the second session alone and not ranked; more than 3 stop the report); drift, unconfirmed
/// orders and pairs close to the line; recall as whole hits; the exact table.
/// Synthetic runs: every value moves 0.2% per run, so the basis stays near zero and the threshold
/// at its 35% floor unless a test moves a value on purpose.
/// </summary>
public sealed class ConsolidateClaimTests : IDisposable
{
   #region Data Members

   private readonly SyntheticBench _bench = new();

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// alpha is ahead of beta and gamma in each session; beta and gamma are not separated; the headline names alpha alone.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Rule_OrdersAPair_WhenEverySessionClearsTheThreshold()
   {
      await ConsolidateHarness.Timed( () =>
      {
         JsonElement root = TwoSessions( v8Beta: 1.5, withDelta: false );
         Assert.Equal( 3500, root.GetProperty( "threshold" ).GetProperty( "tBp" ).GetInt32() );
         JsonElement p50 = Table( root, "p50Ms" );
         Assert.Equal( new[] { "alpha", "beta", "gamma" }, Rows( p50 ).Select( r => r.GetProperty( "target" ).GetString() ).ToArray() );
         Assert.Empty( Row( p50, "alpha" ).GetProperty( "notSeparatedFrom" ).EnumerateArray() );
         Assert.Equal( new[] { "gamma" }, Row( p50, "beta" ).GetProperty( "notSeparatedFrom" ).EnumerateArray().Select( e => e.GetString() ).ToArray() );
         Assert.Equal( 2, p50.GetProperty( "orderedPairs" ).GetInt32() );
         Assert.Equal( "ranked", Row( p50, "alpha" ).GetProperty( "status" ).GetString() );
         Assert.Equal( "alpha had the lowest p50 latency: every other engine took at least 1.35 times as long, in each session.", Sentence( root, "headline" ) );
         Assert.Equal( 6, Row( p50, "alpha" ).GetProperty( "perSession" ).EnumerateObject().Sum( s => s.Value.GetProperty( "runs" ).GetArrayLength() ) );
      } );
   }

   /// <summary>
   /// An order that held in v7 and not in v8 is unconfirmed and not published; a pair at 1.29 times in each session is close to the line.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Drift_ListsUnconfirmedOrders_AndPairsCloseToTheLine()
   {
      await ConsolidateHarness.Timed( () =>
      {
         JsonElement root = TwoSessions( v8Beta: 1.2, withDelta: true );
         Assert.Equal( 3500, root.GetProperty( "threshold" ).GetProperty( "tBp" ).GetInt32() );
         JsonElement drift = root.GetProperty( "drift" );
         Assert.Contains( drift.GetProperty( "unconfirmedOrders" ).EnumerateArray(), u => u.GetProperty( "metric" ).GetString() == "p50Ms" && u.GetProperty( "a" ).GetString() == "alpha"
            && u.GetProperty( "b" ).GetString() == "beta" && u.GetProperty( "session" ).GetString() == "v7" );
         Assert.Contains( drift.GetProperty( "closeToLine" ).EnumerateArray(), c => c.GetProperty( "metric" ).GetString() == "p50Ms" && c.GetProperty( "a" ).GetString() == "alpha"
            && c.GetProperty( "b" ).GetString() == "delta" && c.GetProperty( "minRatioBp" ).GetInt32() is >= 12500 and < 13500 );
         Assert.Equal( 2500, drift.GetProperty( "closeFromBp" ).GetInt32() );
         JsonElement beta = drift.GetProperty( "perTarget" ).EnumerateArray().First( d => d.GetProperty( "target" ).GetString() == "beta" && d.GetProperty( "metric" ).GetString() == "p50Ms" );
         Assert.Equal( -2000, beta.GetProperty( "moveBp" ).GetInt32() );
         Assert.False( root.GetProperty( "guards" ).GetProperty( "g2" ).GetProperty( "stopped" ).GetBoolean() );
         Assert.Contains( "orders held in v7 and not in v8", Sentence( root, "drift.unconfirmed" ) );
      } );
   }

   /// <summary>
   /// G2: alpha ahead of delta in v7, delta's v8 median below alpha's. With delta's v8 cells excluded from the
   /// basis the threshold stays at 35% and the report stops (exit 3, no headline). Without the exclusion the
   /// reversal is itself a pair move larger than the v7 ratio, so the threshold rises above it and nothing stops:
   /// G2 can fire on real data only through an exclusion row.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task G2_StopsTheReport_WhenAnOrderReversesAndTheBasisDoesNotHoldTheReversal()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string v7 = _bench.WriteSession( "2026-10-06", 701, i => Four( i, 1.5, 1.5 ) );
         string v8 = _bench.WriteSession( "2026-10-08", 801, i => Four( i, 1.5, 0.9 ) );
         Directory.CreateDirectory( Path.Combine( _bench.Repo, "design", "verdicts" ) );
         File.WriteAllText( Path.Combine( _bench.Repo, "design", "verdicts", "test-verdict.txt" ), "item 1: delta ran cold in v8" + Environment.NewLine );
         string exclusions = _bench.WriteExclusions( new JsonObject
         {
            ["target"] = "delta", ["seeds"] = new JsonArray( 801, 802, 803 ), ["metric"] = "*", ["kind"] = "method defect", ["why"] = "test",
            ["source"] = "design/verdicts/test-verdict.txt", ["item"] = 1, ["evidence"] = new JsonArray( "delta ran cold" ),
         } );
         ( int exit, JsonElement? json, string? md ) = _bench.Consolidate( "--session", "v7=" + v7, "--session", "v8=" + v8, "--exclusions", exclusions );
         Assert.True( exit == 3, _bench.LogText() );
         JsonElement root = json!.Value;
         Assert.Equal( "stopped", root.GetProperty( "status" ).GetString() );
         Assert.Contains( root.GetProperty( "guards" ).GetProperty( "g2" ).GetProperty( "pairs" ).EnumerateArray(), p => p.GetProperty( "a" ).GetString() == "alpha" && p.GetProperty( "b" ).GetString() == "delta" );
         Assert.DoesNotContain( root.GetProperty( "sentences" ).EnumerateArray(), s => s.GetProperty( "slot" ).GetString() == "headline" );
         Assert.Contains( "This report stopped", md );
         Directory.Delete( Path.Combine( Path.GetDirectoryName( _bench.Results )!, "out" ), recursive: true );
         ( int free, JsonElement? json2, _ ) = _bench.Consolidate( "--session", "v7=" + v7, "--session", "v8=" + v8, "--exclusions", _bench.WriteExclusions() );
         Assert.True( free == 0, _bench.LogText() );
         Assert.True( json2!.Value.GetProperty( "threshold" ).GetProperty( "tBp" ).GetInt32() > 6000 );
      } );
   }

   /// <summary>
   /// G3: a target whose search settings changed between the sessions is shown with v8 alone, not ranked and not
   /// separated from anyone; four changed targets stop the report.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task G3_ShowsAChangedTargetWithOneSession_AndStopsAboveThree()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string v7 = _bench.WriteSession( "2026-10-06", 701, i => Four( i, 1.5, 1.5 ) );
         string v8 = _bench.WriteSession( "2026-10-08", 801, i => Four( i, 1.5, 1.5 ), ( _, run ) => Effort( run, "gamma" ) );
         ( int exit, JsonElement? json, _ ) = _bench.Consolidate( "--session", "v7=" + v7, "--session", "v8=" + v8 );
         Assert.True( exit == 0, _bench.LogText() );
         JsonElement root = json!.Value;
         JsonElement change = Assert.Single( root.GetProperty( "guards" ).GetProperty( "g3" ).GetProperty( "targets" ).EnumerateArray() );
         Assert.Equal( "gamma", change.GetProperty( "target" ).GetString() );
         Assert.Equal( new[] { "searchSettings" }, change.GetProperty( "fields" ).EnumerateArray().Select( f => f.GetString() ).ToArray() );
         JsonElement gamma = Row( Table( root, "p50Ms" ), "gamma" );
         Assert.Equal( "one-session", gamma.GetProperty( "status" ).GetString() );
         Assert.Equal( new[] { "v8" }, gamma.GetProperty( "perSession" ).EnumerateObject().Select( p => p.Name ).ToArray() );
         Assert.Equal( 3, gamma.GetProperty( "notSeparatedFrom" ).GetArrayLength() );
         Assert.Equal( "one-session", gamma.GetProperty( "flags" )[0].GetProperty( "code" ).GetString() );
         Directory.Delete( Path.Combine( Path.GetDirectoryName( _bench.Results )!, "out" ), recursive: true );
         string v8b = _bench.WriteSession( "2026-10-09", 811, i => Four( i, 1.5, 1.5 ), ( _, run ) => Effort( run, "alpha", "beta", "gamma", "delta" ) );
         ( int stopped, JsonElement? json2, _ ) = _bench.Consolidate( "--session", "v7=" + v7, "--session", "v8=" + v8b );
         Assert.True( stopped == 3, _bench.LogText() );
         Assert.True( json2!.Value.GetProperty( "guards" ).GetProperty( "g3" ).GetProperty( "stopped" ).GetBoolean() );
      } );
   }

   /// <summary>
   /// Recall is whole hits of queryCount x top per run; one hit of difference is flagged; a recorded count that
   /// disagrees with recall, or a recall that is not a whole number of hits, is refused.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Recall_IsWholeHits_AndOneHitIsADifference()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string runs = _bench.WriteSession( "2026-10-06", 701, i => Recall( i, i == 1 ? 0.995 : 1.0, i == 1 ? 199 : 200 ) );
         ( int exit, JsonElement? json, _ ) = _bench.Consolidate( "--session", "v7=" + runs );
         Assert.True( exit == 0, _bench.LogText() );
         JsonElement beta = json!.Value.GetProperty( "recall" ).EnumerateArray().First( r => r.GetProperty( "target" ).GetString() == "beta" );
         Assert.Equal( new[] { 200, 199, 200 }, beta.GetProperty( "hits" ).GetProperty( "v7" ).EnumerateArray().Select( h => h.GetInt32() ).ToArray() );
         Assert.True( beta.GetProperty( "differs" ).GetBoolean() );
         Assert.Equal( 200, beta.GetProperty( "of" ).GetInt32() );
         Assert.Equal( "beta's recall hits differ between runs: v7 200, 199, 200.", Sentence( json.Value, "recall.differs.beta" ) );
         foreach( (double recall, int hits, string words) in new[] { ( 0.995, 200, "disagrees" ), ( 0.9975, 199, "not a whole number" ) } )
         {
            using var bench = new SyntheticBench();
            string bad = bench.WriteSession( "2026-10-06", 701, i => Recall( i, i == 2 ? recall : 1.0, i == 2 ? hits : 200 ) );
            Assert.Equal( 1, bench.Consolidate( "--session", "v7=" + bad ).Exit );
            Assert.Contains( words, bench.LogText() );
         }
      } );
   }

   /// <summary>
   /// The exact table holds the targets with an exact pass in every run, lists the others with a sentence, and refuses an exact pass in some runs only.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task ExactTable_ListsTargetsWithoutAnExactPass()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string runs = _bench.WriteSession( "2026-10-06", 701, i => new[] { SyntheticBench.Target( "alpha", 1.0, 1000, 4000, 0.9 ), SyntheticBench.Target( "beta", 2.0, 500, 2000 ) } );
         ( int exit, JsonElement? json, _ ) = _bench.Consolidate( "--session", "v7=" + runs );
         Assert.True( exit == 0, _bench.LogText() );
         JsonElement exact = Table( json!.Value, "exactP50Ms" );
         Assert.Equal( new[] { "alpha" }, Rows( exact ).Select( r => r.GetProperty( "target" ).GetString() ).ToArray() );
         JsonElement absent = Assert.Single( exact.GetProperty( "noExactPass" ).EnumerateArray() );
         Assert.Equal( "beta", absent.GetProperty( "target" ).GetString() );
         Assert.Equal( "approximate", absent.GetProperty( "mode" ).GetString() );
         Assert.Equal( "beta has no exact pass in these runs.", Sentence( json.Value, "table.exactP50Ms.absent.beta" ) );
         using var bench = new SyntheticBench();
         string partial = bench.WriteSession( "2026-10-06", 701, i => new[] { SyntheticBench.Target( "alpha", 1.0, 1000, 4000, i == 0 ? 0.9 : null ) } );
         Assert.Equal( 1, bench.Consolidate( "--session", "v7=" + partial ).Exit );
         Assert.Contains( "in some claim runs and not in others", bench.LogText() );
      } );
   }

   /// <summary>
   /// Targets alpha, beta, gamma with a 0.2% move per run.
   /// </summary>
   /// <param name="i">Run index.</param>
   /// <param name="beta">beta's p50 (QPS follows it).</param>
   /// <returns>The targets.</returns>
   public static JsonObject[] Three( int i, double beta )
   {
      double f = 1 + 0.002 * i;
      return new[]
      {
         SyntheticBench.Target( "alpha", 1.0 * f, 1000 / f, 4000 / f ),
         SyntheticBench.Target( "beta", beta * f, 1000 / beta / f, 4000 / beta / f ),
         SyntheticBench.Target( "gamma", 1.6 * f, 1000 / 1.6 / f, 4000 / 1.6 / f ),
      };
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Writes v7 (beta 1.5) and v8 (beta as given) with alpha, beta, gamma and, when asked, delta (1.3 times alpha) and consolidates them.
   /// </summary>
   /// <param name="v8Beta">beta's p50 in v8.</param>
   /// <param name="withDelta">True to add delta at 1.3 times alpha.</param>
   /// <returns>consolidated.json.</returns>
   private JsonElement TwoSessions( double v8Beta, bool withDelta )
   {
      IEnumerable<JsonObject> Targets( int i, double beta ) => withDelta ? Three( i, beta ).Append( Delta( i, 1.3 ) ) : Three( i, beta );
      string v7 = _bench.WriteSession( "2026-10-06", 701, i => Targets( i, 1.5 ) );
      string v8 = _bench.WriteSession( "2026-10-08", 801, i => Targets( i, v8Beta ) );
      ( int exit, JsonElement? json, _ ) = _bench.Consolidate( "--session", "v7=" + v7, "--session", "v8=" + v8 );
      Assert.True( exit == 0, _bench.LogText() );
      return json!.Value;
   }

   /// <summary>
   /// The four targets of the guard tests: alpha, beta, gamma and delta at the given p50.
   /// </summary>
   /// <param name="i">Run index.</param>
   /// <param name="beta">beta's p50.</param>
   /// <param name="delta">delta's p50.</param>
   /// <returns>The targets.</returns>
   private static IEnumerable<JsonObject> Four( int i, double beta, double delta )
   {
      return Three( i, beta ).Append( Delta( i, delta ) );
   }

   /// <summary>
   /// Target delta at a p50 (QPS follows it).
   /// </summary>
   /// <param name="i">Run index.</param>
   /// <param name="p50">p50.</param>
   /// <returns>The target.</returns>
   private static JsonObject Delta( int i, double p50 )
   {
      double f = 1 + 0.002 * i;
      return SyntheticBench.Target( "delta", p50 * f, 1000 / p50 / f, 4000 / p50 / f );
   }

   /// <summary>
   /// Targets alpha and beta, beta with a given recall and hit count.
   /// </summary>
   /// <param name="i">Run index.</param>
   /// <param name="recall">beta's recall.</param>
   /// <param name="hits">beta's recorded recallHits.</param>
   /// <returns>The targets.</returns>
   private static JsonObject[] Recall( int i, double recall, int hits )
   {
      JsonObject[] pair = ConsolidateTests.Pair();
      pair[1]["search"]!["recall"] = recall;
      pair[1]["search"]!["recallHits"] = hits;
      return pair;
   }

   /// <summary>
   /// Changes the search settings of targets in a run.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <param name="targets">Target names.</param>
   private static void Effort( JsonObject run, params string[] targets )
   {
      foreach( JsonNode? t in run["targets"]!.AsArray().Where( t => targets.Contains( (string)t!["name"]! ) ) )
      {
         t!["searchSettings"] = new JsonObject { ["ef"] = "200" };
      }
   }

   /// <summary>
   /// A metric's table.
   /// </summary>
   /// <param name="root">consolidated.json.</param>
   /// <param name="metric">Metric id.</param>
   /// <returns>The table.</returns>
   private static JsonElement Table( JsonElement root, string metric )
   {
      return root.GetProperty( "metrics" ).EnumerateArray().First( m => m.GetProperty( "metric" ).GetString() == metric );
   }

   /// <summary>
   /// A table's rows.
   /// </summary>
   /// <param name="table">The table.</param>
   /// <returns>The rows.</returns>
   private static IEnumerable<JsonElement> Rows( JsonElement table )
   {
      return table.GetProperty( "rows" ).EnumerateArray();
   }

   /// <summary>
   /// One row of a table.
   /// </summary>
   /// <param name="table">The table.</param>
   /// <param name="target">Target.</param>
   /// <returns>The row.</returns>
   private static JsonElement Row( JsonElement table, string target )
   {
      return Rows( table ).First( r => r.GetProperty( "target" ).GetString() == target );
   }

   /// <summary>
   /// The text of the sentence in a slot.
   /// </summary>
   /// <param name="root">consolidated.json.</param>
   /// <param name="slot">Slot.</param>
   /// <returns>The text.</returns>
   private static string Sentence( JsonElement root, string slot )
   {
      return root.GetProperty( "sentences" ).EnumerateArray().First( s => s.GetProperty( "slot" ).GetString() == slot ).GetProperty( "text" ).GetString()!;
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
