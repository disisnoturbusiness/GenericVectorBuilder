using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The engine fact sheet against the real v7 and v8 runs together. The v8 runs record, for the embedded engines, that a version was read "on an
/// in-memory connection"; the storage rows of sqlitevec and duckdb said "in-memory" is absent from everything the target recorded, so the whole
/// consolidation was refused. The rows now read the index, engine and durability text, where a storage claim lives.
/// </summary>
public sealed class EngineFactsV8Tests : IDisposable
{
   #region Data Members

   private const string FACTS_PATH = "src/GenericVectorBuilder.Bench/Report/engine-facts.json";
   private static readonly string[] V8 = { "20261007-205106-eshoponweb", "20261007-221429-eshoponweb", "20261007-233551-eshoponweb" };

   private readonly string _root;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a private folder under the test binaries for the planted runs.
   /// </summary>
   public EngineFactsV8Tests()
   {
      _root = Path.Combine( AppContext.BaseDirectory, "engine-facts-v8-tests", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( _root );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Every row of the real fact sheet resolves against the three real v7 runs and the three real v8 runs, the six together.
   /// </summary>
   [Fact]
   public Task EveryRealFactRow_ResolvesAgainstTheV7AndV8Runs()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string[] outcome = Facts( Path.Combine( ReportSourcesHarness.RepoRoot(), FACTS_PATH ), SixRuns() );
         Assert.Single( outcome );
         string[] parts = outcome[0].Split( ' ' );
         Assert.Equal( parts[0][5..], parts[1][9..] );
      } );
   }

   /// <summary>
   /// The scoped absence check keeps its power: an "in-memory" in the durability text, the index text or the engine text of an embedded engine still fails the
   /// row, and the word is still in the pattern.
   /// </summary>
   [Fact]
   public Task ScopedAbsenceRow_StillFailsWhenTheIndexEngineOrDurabilityTextSaysInMemory()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string facts = Path.Combine( ReportSourcesHarness.RepoRoot(), FACTS_PATH );
         Assert.Contains( "in memory|in-memory|memory pool|RAM", File.ReadAllLines( facts ).Single( l => l.Contains( "\"target\": \"sqlitevec\"" ) && l.Contains( "\"kind\": \"storage\"" ) ) );
         foreach( string field in new[] { "durability", "index", "engine" } )
         {
            string[] runs = SixRuns().Take( 5 ).Append( Planted( V8[2], t => t["name"]!.ToString() == "sqlitevec", t => t[field] = t[field] + " The vectors are held in-memory." ) ).ToArray();
            string[] outcome = Facts( facts, runs );
            Assert.True( outcome.Any( p => p.StartsWith( "sqlitevec/storage: the source does not resolve" ) && p.Contains( "'in-memory' occurs in targets[sqlitevec]." + field ) ), $"{field}: {string.Join( " | ", outcome )}" );
         }

         string[] settingsOnly = SixRuns().Take( 5 ).Append( Planted( V8[2], t => t["name"]!.ToString() == "duckdb", t => t["engineSettings"] = new JsonArray( new JsonObject { ["key"] = "version", ["value"] = "1", ["how"] = "read: on an in-memory connection" } ) ) ).ToArray();
         Assert.Single( Facts( facts, settingsOnly ) );
      } );
   }

   /// <summary>
   /// A scoped row needs the fields it names: a run that records no durability text cannot back a claim about it, and the row fails loud instead of passing.
   /// </summary>
   [Fact]
   public Task ScopedAbsenceRow_FailsLoudWhenARunRecordsNoDurabilityText()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string[] runs = SixRuns().Take( 5 ).Append( Planted( V8[2], t => t["name"]!.ToString() == "duckdb", t => t.AsObject().Remove( "durability" ) ) ).ToArray();
         string[] outcome = Facts( Path.Combine( ReportSourcesHarness.RepoRoot(), FACTS_PATH ), runs );
         Assert.Contains( outcome, p => p.StartsWith( "duckdb/storage: the source does not resolve" ) && p.Contains( "no value at 'durability'" ) );
      } );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The facade's check of a fact sheet against runs and the repository tree.
   /// </summary>
   /// <param name="facts">The sheet.</param>
   /// <param name="runs">The results.json files.</param>
   /// <returns>The counts first, then the problems.</returns>
   private static string[] Facts( string facts, string[] runs )
   {
      return (string[])ReportSourcesHarness.Call( "Facts", facts, runs, ReportSourcesHarness.RepoRoot() )!;
   }

   /// <summary>
   /// The three real v7 runs then the three real v8 runs, as results.json paths.
   /// </summary>
   /// <returns>Six paths.</returns>
   private static string[] SixRuns()
   {
      string results = Path.Combine( ReportSourcesHarness.RepoRoot(), "bench-results" );
      return ReportSourcesHarness.V7Runs().Concat( V8.Select( r => Path.Combine( results, r, "results.json" ) ) ).ToArray();
   }

   /// <summary>
   /// A copy of a real run in this test's folder with one target changed.
   /// </summary>
   /// <param name="run">The run folder name.</param>
   /// <param name="pick">Which target to change.</param>
   /// <param name="change">The change.</param>
   /// <returns>The path of the changed copy's results.json; the folder keeps the run's name with a suffix.</returns>
   private string Planted( string run, Func<JsonNode, bool> pick, Action<JsonNode> change )
   {
      string source = Path.Combine( ReportSourcesHarness.RepoRoot(), "bench-results", run, "results.json" );
      JsonNode root = JsonNode.Parse( File.ReadAllText( source ) )!;
      change( root["targets"]!.AsArray().First( t => pick( t! ) )! );
      string folder = Path.Combine( _root, run + "-planted" + Guid.NewGuid().ToString( "N" )[..6] );
      Directory.CreateDirectory( folder );
      string path = Path.Combine( folder, "results.json" );
      File.WriteAllText( path, root.ToJsonString() );
      return path;
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Removes the planted copies.
   /// </summary>
   public void Dispose()
   {
      Directory.Delete( _root, recursive: true );
   }

   #endregion IDisposable
}
