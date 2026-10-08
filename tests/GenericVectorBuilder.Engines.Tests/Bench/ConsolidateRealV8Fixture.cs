using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// One consolidation of the real v7 runs (701 to 703) and the real v8 runs (801 to 803) with v5 and v6 as the basis, run once for every test of
/// <see cref="ConsolidateV8FixerTests"/>, on copies of the runs' results.json in a scratch folder, with the repository's own fact sheet, exclusions and
/// classes list and an observer summary for each session written from those runs.
/// Why the real runs: the v8 consolidation was refused on them by two defects that no synthetic run had shown, so the fixes are proven on the runs that
/// triggered them.
/// Why a synthetic observer summary: the real summaries live outside the repository; this one is written in the real schema from the runs' own pass list, with the
/// per-CPU APERF figures the real summaries hold, and puts oracle's eight-searcher pass a fixed number of MHz under the pin on every CPU, as the real ones show.
/// </summary>
public sealed class RealV8Fixture : IDisposable
{
   #region Data Members

   /// <summary>The v8 runs.</summary>
   public static readonly string[] V8 = { "20261007-205106-eshoponweb", "20261007-221429-eshoponweb", "20261007-233551-eshoponweb" };

   /// <summary>Blocked folders of the earlier reports, created empty beside the runs.</summary>
   public static readonly string[] BLOCKED = { "blocked-2026-10-05-v5", "blocked-2026-10-05-v6", "blocked-2026-10-06-v7" };

   /// <summary>MHz below the pin that the synthetic observer puts oracle's eight-searcher pass on each CPU, v7 then v8.</summary>
   public static readonly double[] ORACLE_DIP_V7 = { 14.4, 18.0, 20.0, 25.7, 22.0, 24.0, 19.5, 16.0 };

   /// <summary>The same for v8.</summary>
   public static readonly double[] ORACLE_DIP_V8 = { 9.5, 12.0, 15.0, 17.5, 23.1, 20.0, 18.0, 14.0 };

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Builds the scratch folder and runs the consolidation.
   /// </summary>
   public RealV8Fixture()
   {
      Scratch = Path.Combine( AppContext.BaseDirectory, "consolidate-tests", "realv8-" + Guid.NewGuid().ToString( "N" ) );
      Results = Path.Combine( Scratch, "results" );
      Directory.CreateDirectory( Results );
      string real = ConsolidateHarness.BenchResults();
      foreach( string folder in ConsolidateRealRunsTests.V5.Concat( ConsolidateRealRunsTests.V6 ).Concat( ConsolidateRealRunsTests.V7 ).Concat( V8 ).Concat( ConsolidateBasisTests.OTHER_RUNS ) )
      {
         Directory.CreateDirectory( Path.Combine( Results, folder ) );
         File.Copy( Path.Combine( real, folder, "results.json" ), Path.Combine( Results, folder, "results.json" ) );
      }

      foreach( string blocked in BLOCKED )
      {
         Directory.CreateDirectory( Path.Combine( Results, blocked ) );
      }

      ObserverV7 = WriteObserver( "summary-v7.json", ConsolidateRealRunsTests.V7, ORACLE_DIP_V7 );
      ObserverV8 = WriteObserver( "summary.json", V8, ORACLE_DIP_V8 );
      Both = Run( Path.Combine( Scratch, "out-both" ), "--observer", ObserverV8, "--observer", ObserverV7 );
      NoObserver = Run( Path.Combine( Scratch, "out-none" ) );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The scratch folder.</summary>
   public string Scratch { get; }

   /// <summary>The scratch results folder.</summary>
   public string Results { get; }

   /// <summary>The v7 observer summary.</summary>
   public string ObserverV7 { get; }

   /// <summary>The v8 observer summary.</summary>
   public string ObserverV8 { get; }

   /// <summary>The consolidation with both observer summaries.</summary>
   public ConsolidateOutcome Both { get; }

   /// <summary>The consolidation with no observer summary.</summary>
   public ConsolidateOutcome NoObserver { get; }

   /// <summary>
   /// Runs the command on the two real sessions with the repository's own files.
   /// </summary>
   /// <param name="output">The output folder.</param>
   /// <param name="extra">More arguments.</param>
   /// <returns>The outcome.</returns>
   public ConsolidateOutcome Run( string output, params string[] extra )
   {
      string[] args = new[] { "--session", "v7=" + string.Join( ",", ConsolidateRealRunsTests.V7 ), "--session", "v8=" + string.Join( ",", V8 ) }.Concat( extra ).ToArray();
      return DryFixFixture.Run( Results, output, args );
   }

   /// <summary>
   /// Consolidates a changed copy of the scratch results: the results.json files are copied, the change is applied to the copy, and the command runs on it.
   /// Why a copy: a variant must never touch the fixture's results, which every other test reads.
   /// </summary>
   /// <param name="tag">A short name for the variant's folders.</param>
   /// <param name="change">Applied to the copy's results folder before the command runs.</param>
   /// <param name="extra">More arguments, such as observer summaries.</param>
   /// <returns>The exit code, the log, and the outcome when something was written.</returns>
   public (int Exit, string Log, ConsolidateOutcome? Written) Attempt( string tag, Action<string> change, params string[] extra )
   {
      return AttemptInRepo( tag, change, ConsolidateHarness.RepoRoot(), extra );
   }

   /// <summary>
   /// The same, against a given repository root (a copy a test has changed).
   /// </summary>
   /// <param name="tag">A short name for the variant's folders.</param>
   /// <param name="change">Applied to the copy's results folder before the command runs.</param>
   /// <param name="repo">The repository root the command reads its sources from.</param>
   /// <param name="extra">More arguments.</param>
   /// <returns>The exit code, the log, and the outcome when something was written.</returns>
   public (int Exit, string Log, ConsolidateOutcome? Written) AttemptInRepo( string tag, Action<string> change, string repo, params string[] extra )
   {
      string results = Path.Combine( Scratch, "variant-" + tag, "results" );
      Directory.CreateDirectory( results );
      foreach( string folder in Directory.GetDirectories( Results ) )
      {
         string target = Path.Combine( results, Path.GetFileName( folder ) );
         Directory.CreateDirectory( target );
         foreach( string file in Directory.GetFiles( folder ) )
         {
            File.Copy( file, Path.Combine( target, Path.GetFileName( file ) ) );
         }
      }

      change( results );
      string output = Path.Combine( Scratch, "variant-" + tag, "out" );
      string[] args = new[]
      {
         "--session", "v7=" + string.Join( ",", ConsolidateRealRunsTests.V7 ), "--session", "v8=" + string.Join( ",", V8 ),
         "--basis-session", "v5=" + string.Join( ",", ConsolidateRealRunsTests.V5 ), "--basis-session", "v6=" + string.Join( ",", ConsolidateRealRunsTests.V6 ), "--results-dir", results, "--repo", repo,
         "--facts", Path.Combine( repo, "src", "GenericVectorBuilder.Bench", "Report", "engine-facts.json" ), "--exclusions", Path.Combine( repo, "deploy", "bench", "basis-exclusions.json" ), "--out", output,
      }.Concat( extra ).ToArray();
      var result = (string[])ConsolidateHarness.Call( "Consolidate", (object)args )!;
      string log = string.Join( Environment.NewLine, result.Skip( 1 ) );
      string json = Path.Combine( output, "consolidated.json" );
      ConsolidateOutcome? written = File.Exists( json ) ? new ConsolidateOutcome( int.Parse( result[0] ), log, JsonDocument.Parse( File.ReadAllText( json ) ).RootElement.Clone(), File.ReadAllText( Path.Combine( output, "consolidated.md" ) ) ) : null;
      return ( int.Parse( result[0] ), log, written );
   }

   /// <summary>
   /// A copy of the repository's sources, deploy files, design files and tests in the scratch folder, for a test that changes a file the command reads from the repository.
   /// </summary>
   /// <param name="tag">A short name for the copy's folder.</param>
   /// <returns>The copy's root.</returns>
   public string CopyRepo( string tag )
   {
      string root = ConsolidateHarness.RepoRoot();
      string destination = Path.Combine( Scratch, "repo-" + tag );
      Directory.CreateDirectory( destination );
      File.Copy( Path.Combine( root, "GenericVectorBuilder.slnx" ), Path.Combine( destination, "GenericVectorBuilder.slnx" ) );
      foreach( string top in new[] { "deploy", "design", "src", "tests" } )
      {
         foreach( string file in Directory.GetFiles( Path.Combine( root, top ), "*", SearchOption.AllDirectories ) )
         {
            string relative = Path.GetRelativePath( root, file );
            if( relative.Split( Path.DirectorySeparatorChar ).Any( p => p is "bin" or "obj" ) )
            {
               continue;
            }

            string target = Path.Combine( destination, relative );
            Directory.CreateDirectory( Path.GetDirectoryName( target )! );
            File.Copy( file, target );
         }
      }

      return destination;
   }

   /// <summary>
   /// Changes one target of a run in a results folder.
   /// </summary>
   /// <param name="results">The results folder.</param>
   /// <param name="run">The run folder.</param>
   /// <param name="target">The target name.</param>
   /// <param name="change">The change to the target object.</param>
   public static void Change( string results, string run, string target, Action<JsonObject> change )
   {
      string path = Path.Combine( results, run, "results.json" );
      JsonNode root = JsonNode.Parse( File.ReadAllText( path ) )!;
      change( root["targets"]!.AsArray().First( t => (string)t!["name"]! == target )!.AsObject() );
      File.WriteAllText( path, root.ToJsonString() );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Writes an observer summary for a session in the real schema: per run the checks block and per pass the clock figures, with oracle's eight-searcher pass a few MHz under the pin on every CPU.
   /// </summary>
   /// <param name="name">File name.</param>
   /// <param name="folders">The session's run folders.</param>
   /// <param name="dip">MHz under the pin on each of the 8 CPUs for oracle's eight-searcher pass.</param>
   /// <returns>The path.</returns>
   private string WriteObserver( string name, string[] folders, double[] dip )
   {
      var runs = new JsonArray();
      foreach( string folder in folders )
      {
         JsonObject run = JsonNode.Parse( File.ReadAllText( Path.Combine( Results, folder, "results.json" ) ) )!.AsObject();
         var passes = new JsonArray();
         double worst = 0;
         foreach( JsonNode? pass in run["conditions"]!["passes"]!.AsArray() )
         {
            bool oracle = (string)pass!["target"]! == "oracle" && (string)pass["pass"]! == "default@8";
            double[] cpu = Enumerable.Range( 0, 8 ).Select( c => 3500.0 - ( oracle ? dip[c] : 0.5 ) ).ToArray();
            worst = Math.Max( worst, cpu.Max( v => 3500.0 - v ) / 3500.0 * 10000 );
            passes.Add( new JsonObject
            {
               ["target"] = (string)pass["target"]!, ["pass"] = (string)pass["pass"]!,
               ["clock"] = new JsonObject { ["aperfCpuMhz"] = new JsonArray( cpu.Select( v => (JsonNode)JsonValue.Create( v )! ).ToArray() ), ["freqEngineMean"] = oracle ? 3085.5 : 3492.0, ["freqClientMean"] = oracle ? 3189.8 : 3492.0 },
            } );
         }

         runs.Add( new JsonObject
         {
            ["folder"] = folder, ["passes"] = passes,
            ["checks"] = new JsonObject
            {
               ["observerCpu"] = new JsonArray( 0.057, 0.071, 0.138 ), ["aperf"] = new JsonObject { ["worstDeviationBp"] = Math.Round( worst, 1 ) },
               ["msr620"] = new JsonObject { ["valuesSeen"] = new JsonArray( "1e1e" ) },
               ["timers"] = new JsonObject { ["covered"] = folders == V8, ["passesWithTimerFired"] = new JsonArray() },
            },
         } );
      }

      string path = Path.Combine( Scratch, name );
      File.WriteAllText( path, new JsonObject { ["schema"] = "v8-observer-summary-1", ["pinnedMhz"] = 3500.0, ["runs"] = runs }.ToJsonString() );
      return path;
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Removes the scratch folder (copies only).
   /// </summary>
   public void Dispose()
   {
      Directory.Delete( Scratch, recursive: true );
   }

   #endregion IDisposable
}
