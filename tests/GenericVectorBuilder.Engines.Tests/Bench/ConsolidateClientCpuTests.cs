using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The measured costs of the why table: engine and client CPU per search at one and eight searchers
/// and engine CPUs busy at eight, medians over the newest session's runs, blank unless every run
/// recorded the figure; an embedded engine's client CPU includes the engine's own work and a
/// sentence bound to its hosting field says so; the range sentence names the lowest and highest.
/// </summary>
public sealed class ConsolidateClientCpuTests : IDisposable
{
   #region Data Members

   private readonly SyntheticBench _bench = new();

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Costs are the medians of the newest session's three runs; one run without engine CPU leaves engine CPU blank with its recorded reason.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Costs_AreNewestSessionMedians_AndBlankUnlessEveryRunRecordedThem()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string v7 = _bench.WriteSession( "2026-10-06", 701, _ => ConsolidateTests.Pair() );
         string v8 = _bench.WriteSession( "2026-10-08", 801, _ => ConsolidateTests.Pair(), ( i, run ) =>
         {
            foreach( JsonNode? p in run["conditions"]!["passes"]!.AsArray() )
            {
               bool alpha = (string)p!["target"]! == "alpha";
               p["clientCpuMsPerSearch"] = 0.4 + 0.1 * i;
               p["engineCpuMsPerSearch"] = alpha ? 1.0 + i : ( i == 2 ? null : 2.0 );
               if( !alpha && i == 2 )
               {
                  p["engineCpuNullReason"] = "no cgroup of the engine under test was followed";
               }
            }
         } );
         ( int exit, JsonElement? json, _ ) = _bench.Consolidate( "--session", "v7=" + v7, "--session", "v8=" + v8 );
         Assert.True( exit == 0, _bench.LogText() );
         JsonElement alpha = Costs( json!.Value, "alpha" );
         Assert.Equal( "v8", alpha.GetProperty( "session" ).GetString() );
         Assert.Equal( 2.0, alpha.GetProperty( "engineCpuMsPerSearch" ).GetProperty( "1" ).GetDouble(), 9 );
         Assert.Equal( 0.5, alpha.GetProperty( "clientCpuMsPerSearch" ).GetProperty( "8" ).GetDouble(), 9 );
         Assert.Equal( 1.5, alpha.GetProperty( "engineCpusBusyAt8" ).GetDouble(), 9 );
         JsonElement beta = Costs( json.Value, "beta" );
         Assert.Equal( JsonValueKind.Null, beta.GetProperty( "engineCpuMsPerSearch" ).GetProperty( "1" ).ValueKind );
         Assert.Equal( "no cgroup of the engine under test was followed", beta.GetProperty( "engineCpuNullReason" ).GetString() );
         Assert.Equal( "At one searcher, client CPU per search ran from 0.500 ms (alpha) to 0.500 ms (beta).", Sentence( json.Value, "why.range.client" ) );
      } );
   }

   /// <summary>
   /// An embedded target's client CPU includes the engine's work, and the sentence cites the hosting field of every claim run.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task EmbeddedTarget_ClientCpuIncludesTheEngine()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string runs = _bench.WriteSession( "2026-10-06", 701, _ =>
         {
            JsonObject[] pair = ConsolidateTests.Pair();
            pair[1]["hosting"] = "embedded";
            return pair;
         } );
         ( int exit, JsonElement? json, _ ) = _bench.Consolidate( "--session", "v7=" + runs );
         Assert.True( exit == 0, _bench.LogText() );
         Assert.True( Costs( json!.Value, "beta" ).GetProperty( "clientIncludesEngine" ).GetBoolean() );
         Assert.False( Costs( json.Value, "alpha" ).GetProperty( "clientIncludesEngine" ).GetBoolean() );
         JsonElement sentence = json.Value.GetProperty( "sentences" ).EnumerateArray().First( s => s.GetProperty( "slot" ).GetString() == "why.embedded" );
         Assert.Equal( "Hosting embedded is recorded for beta: each runs inside the test's own process, so its client CPU per search includes the engine's own work.", sentence.GetProperty( "text" ).GetString() );
         Assert.Equal( "results:targets[beta].hosting#embedded", sentence.GetProperty( "sources" )[0].GetProperty( "ref" ).GetString() );
      } );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A target's why costs.
   /// </summary>
   /// <param name="root">consolidated.json.</param>
   /// <param name="target">Target.</param>
   /// <returns>The costs.</returns>
   private static JsonElement Costs( JsonElement root, string target )
   {
      return root.GetProperty( "why" ).EnumerateArray().First( w => w.GetProperty( "target" ).GetString() == target ).GetProperty( "costs" );
   }

   /// <summary>
   /// The text of a slot's sentence.
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
