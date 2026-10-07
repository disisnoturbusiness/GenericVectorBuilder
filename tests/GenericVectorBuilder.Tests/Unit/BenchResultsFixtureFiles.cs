using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// Real data for the page tests: verbatim copies of the notes of a v5, a v6 and a v7 run, and of the report and file of the set published on 4 Oct 2026,
/// kept in the BenchResultsFixtures folder next to the tests (see its README for the source of each file).
/// Why files and not strings: the notes of a run are the record the run pages must describe, so a test that reads a hand-written imitation of a note
/// cannot show the page describes the real one. Why found by the source path of this file: Tests.csproj copies no data files, and a test must not depend
/// on a folder under bench-results, which comes and goes with each review.
/// </summary>
internal static class BenchResultsFixtureFiles
{
   #region Public Methods

   /// <summary>
   /// The text of a file in the fixtures folder.
   /// </summary>
   /// <param name="name">File name.</param>
   /// <param name="here">The path of the calling source file (filled in by the compiler); the fixtures folder sits beside it.</param>
   /// <returns>The file's text.</returns>
   public static string Text( string name, [CallerFilePath] string here = "" )
   {
      string path = Path.Combine( Path.GetDirectoryName( here )!, "BenchResultsFixtures", name );
      return File.Exists( path ) ? File.ReadAllText( path ) : throw new FileNotFoundException( $"The fixture {name} is not at {path}." );
   }

   /// <summary>
   /// A results.json of a run measured before the clock block existed: the shared run fixture with no clock block, and the notes, warmupSearches and
   /// machineControl of a real run of that session ("v5", "v6" or "v7").
   /// </summary>
   /// <param name="session">The session's tag.</param>
   /// <param name="here">The path of the calling source file (filled in by the compiler).</param>
   /// <returns>The results.json text.</returns>
   public static string LegacyRun( string session, [CallerFilePath] string here = "" )
   {
      JsonNode root = JsonNode.Parse( BenchResultsFixtures.RUN_RESULTS_V8_JSON )!;
      JsonObject conditions = root["conditions"]!.AsObject();
      conditions.Remove( "clock" );
      JsonNode recorded = JsonNode.Parse( Text( $"run-notes.{session}.json", here ) )!;
      root["notes"] = recorded["notes"]!.DeepClone();
      conditions["warmupSearches"] = recorded["warmupSearches"]!.DeepClone();
      conditions["machineControl"] = recorded["machineControl"]!.DeepClone();
      return root.ToJsonString();
   }

   #endregion Public Methods
}
