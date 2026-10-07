using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The one identity function at work: a target whose setup differs inside a session has no row and
/// is listed with the fields; a method difference between claim runs (exact seconds) refuses the
/// report instead of pooling or dropping (the v7 command kept "the largest group", the old test at
/// ConsolidateMethodAndSetupTests.cs:97-110 asserted it; this is its refusal test); an os difference
/// is printed, not refused (gap G41); a field one session did not record (engine settings, image id)
/// never makes a target differ; a basis run without machine control is refused.
/// </summary>
public sealed class ConsolidateMethodAndSetupTests : IDisposable
{
   #region Data Members

   private readonly SyntheticBench _bench = new();

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// beta's durability text differs in one run: beta has no row, and the report names the field.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task SetupDifferenceInsideASession_LeavesTheTargetOut()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string runs = _bench.WriteSession( "2026-10-06", 701, i =>
         {
            JsonObject[] pair = ConsolidateTests.Pair();
            pair[1]["durability"] = i == 2 ? "beta fsyncs every second." : "beta fsyncs every commit.";
            return pair;
         } );
         ( int exit, JsonElement? json, string? md ) = _bench.Consolidate( "--session", "v7=" + runs );
         Assert.True( exit == 0, _bench.LogText() );
         JsonElement left = Assert.Single( json!.Value.GetProperty( "notInReport" ).EnumerateArray() );
         Assert.Equal( ( "beta", "setup differs within a session" ), ( left.GetProperty( "target" ).GetString(), left.GetProperty( "code" ).GetString() ) );
         Assert.Equal( new[] { "durability" }, left.GetProperty( "fields" ).EnumerateArray().Select( f => f.GetString() ).ToArray() );
         Assert.Contains( "beta has no row: its recorded setup differs within a session, in durability.", md );
         Assert.DoesNotContain( json.Value.GetProperty( "metrics" )[0].GetProperty( "rows" ).EnumerateArray(), r => r.GetProperty( "target" ).GetString() == "beta" );
      } );
   }

   /// <summary>
   /// One claim run with another exact-mode length: the report is refused, naming the field and the runs; nothing is written.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task MethodDifference_IsRefused_NeverPooledOrDropped()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string runs = _bench.WriteSession( "2026-10-06", 701, _ => ConsolidateTests.Pair(), ( i, run ) => run["conditions"]!["exactSeconds"] = i == 1 ? 30 : 60 );
         ( int exit, JsonElement? json, _ ) = _bench.Consolidate( "--session", "v7=" + runs );
         Assert.Equal( 1, exit );
         Assert.Null( json );
         Assert.Contains( "exactSeconds differs: 60 (20261006-100000-eshoponweb, 20261006-120000-eshoponweb); 30 (20261006-110000-eshoponweb)", _bench.LogText() );
      } );
   }

   /// <summary>
   /// An os string that differs between the sessions is printed as a machine difference, not refused.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task OsDifference_IsPrinted_NotRefused()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string v7 = _bench.WriteSession( "2026-10-06", 701, _ => ConsolidateTests.Pair() );
         string v8 = _bench.WriteSession( "2026-10-08", 801, _ => ConsolidateTests.Pair(), ( _, run ) => run["machine"]!["os"] = "Test OS 2" );
         ( int exit, JsonElement? json, _ ) = _bench.Consolidate( "--session", "v7=" + v7, "--session", "v8=" + v8 );
         Assert.True( exit == 0, _bench.LogText() );
         string difference = Assert.Single( json!.Value.GetProperty( "machine" ).GetProperty( "differences" ).EnumerateArray() ).GetString()!;
         Assert.StartsWith( "os: Test OS (", difference );
      } );
   }

   /// <summary>
   /// v8 records engine settings and image ids that v7 never wrote: no target differs (G3 stays empty), and a v8 run whose
   /// engine settings differ from another v8 run is a setup difference.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task FieldsOneSessionDidNotRecord_NeverMakeATargetDiffer()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string v7 = _bench.WriteSession( "2026-10-06", 701, _ => ConsolidateTests.Pair() );
         string v8 = _bench.WriteSession( "2026-10-08", 801, _ => ConsolidateTests.Pair(), ( i, run ) => V8Fields( run, false ) );
         ( int exit, JsonElement? json, _ ) = _bench.Consolidate( "--session", "v7=" + v7, "--session", "v8=" + v8 );
         Assert.True( exit == 0, _bench.LogText() );
         Assert.Empty( json!.Value.GetProperty( "guards" ).GetProperty( "g3" ).GetProperty( "targets" ).EnumerateArray() );
         Assert.Equal( 2, json.Value.GetProperty( "images" ).GetArrayLength() );
         using var bench = new SyntheticBench();
         string w7 = bench.WriteSession( "2026-10-06", 701, _ => ConsolidateTests.Pair() );
         string w8 = bench.WriteSession( "2026-10-08", 801, _ => ConsolidateTests.Pair(), ( i, run ) => V8Fields( run, i == 2 ) );
         ( int exit2, JsonElement? json2, _ ) = bench.Consolidate( "--session", "v7=" + w7, "--session", "v8=" + w8 );
         Assert.True( exit2 == 0, bench.LogText() );
         Assert.Contains( json2!.Value.GetProperty( "notInReport" ).EnumerateArray(), n => n.GetProperty( "fields" ).EnumerateArray().Any( f => f.GetString() == "engineSettings" ) );
      } );
   }

   /// <summary>
   /// A basis session run without machine control is refused.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task BasisRunWithoutMachineControl_IsRefused()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string v6 = _bench.WriteSession( "2026-10-05", 601, _ => ConsolidateTests.Pair(), ( _, run ) => run["conditions"]!["machineControl"] = "off" );
         string v7 = _bench.WriteSession( "2026-10-06", 701, _ => ConsolidateTests.Pair() );
         ( int exit, _, _ ) = _bench.Consolidate( "--session", "v7=" + v7, "--basis-session", "v6=" + v6 );
         Assert.Equal( 1, exit );
         Assert.Contains( "has machineControl off", _bench.LogText() );
      } );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Adds the v8 engine settings and image of every target; one run may get another buffer size for alpha.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <param name="otherSetting">True to give alpha another setting value.</param>
   private static void V8Fields( JsonObject run, bool otherSetting )
   {
      foreach( JsonNode? t in run["targets"]!.AsArray() )
      {
         string name = (string)t!["name"]!;
         string value = otherSetting && name == "alpha" ? "2g" : "1g";
         t["engineSettings"] = new JsonArray( new JsonObject { ["key"] = "buffer", ["value"] = value, ["how"] = "read: test" } );
         t["image"] = new JsonObject { ["ref"] = $"{name}:1", ["id"] = "sha256:" + new string( name[0], 64 ), ["pinnedId"] = "sha256:" + new string( name[0], 64 ), ["lastTagTimeUtc"] = "2026-10-01T00:00:00Z" };
      }
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
