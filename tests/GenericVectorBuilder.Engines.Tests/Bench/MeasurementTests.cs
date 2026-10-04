using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The benchmark's measurement rules, checked against the real runner code with a fake sink
/// whose latency follows a script: p50/p95/p99 and QPS@1 come from one window that holds every
/// timed default@1 search, the exact mode is a time window that cycles the queries, every pass
/// type is rehearsed for at least 5 s before any timed pass, the settle waits until latency stops
/// falling (and says so when it does not), each pass is recorded with its window, its searches
/// and the pass before it, and lopsided or thin windows are flagged.
/// Why these rules get tests: each one closes a hole the adversarial review found (a p50 from a
/// separate burst, an exact p50 from 20 searches, a client compiled during the first timed
/// pass, JVM engines timed seconds after start), and each is easy to undo by accident.
/// Why the sources are compiled here with Roslyn: the benchmark is a console project this test
/// project does not reference. Compiling the repository's Bench sources together with
/// MeasurementScenarios.cs (fakes, built only in this compilation) tests the code the benchmark runs.
/// </summary>
public class MeasurementTests
{
   #region Data Members

   private const string SCENARIOS_TYPE = "GenericVectorBuilder.Bench.MeasurementUnderTest.MeasurementScenarios";
   private const string IMPLICIT_USINGS = "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; "
      + "global using System.Net.Http; global using System.Threading; global using System.Threading.Tasks;";
   private static readonly Regex WARMUP_START = new( @"warm-up before (\S+), \d+ searches$", RegexOptions.Compiled );
   private static readonly Regex REHEARSED = new( @"(\S+) [\d,]+ searches in ([\d.]+) s", RegexOptions.Compiled );
   private static readonly Lazy<Assembly> COMPILED = new( Compile );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The benchmark's own rehearsal length is 5 s per pass type and its settle cap is 120 s.
   /// </summary>
   [Fact]
   public void Defaults_FiveSecondRehearsalAndTwoMinuteSettleCap()
   {
      double[] defaults = (double[])Invoke( "Defaults" );
      Assert.Equal( new[] { 5.0, 120.0 }, defaults );
   }

   /// <summary>
   /// Every timed default@1 search is a latency sample: the searches sent between "timing
   /// default@1" and the next warm-up equal the samples, QPS@1 is those samples over the same
   /// window's seconds, and no search is sent outside a warm-up or a timed window (no separate
   /// latency burst).
   /// </summary>
   [Fact]
   public async Task LatencyAndQps1_ComeFromOneWindow()
   {
      dynamic r = await ScenarioAsync( "PassesAsync", Args( "--seconds", "2", "--concurrency", "1,4", "--queries", "random:5", "--exact-seconds", "0" ), "fixed:1", null, false );
      string[] events = r.Events;
      string[] one = Record( r, "default@1" );
      string[] four = Record( r, "default@4" );
      int samples = r.LatencySamples;
      Assert.Equal( samples, TimedSearches( events, "default@1" ) );
      Assert.Equal( samples, int.Parse( one[5], CultureInfo.InvariantCulture ) );
      Assert.True( samples >= 200, $"only {samples} samples in a 2 s window of 1 ms searches" );
      double seconds = double.Parse( one[4], CultureInfo.InvariantCulture );
      Assert.True( seconds >= 2.0, $"default@1 window lasted {seconds} s" );
      Dictionary<int, double> qps = r.Qps;
      Assert.Equal( samples / seconds, qps[1], 6 );
      Assert.True( (double)r.P50 <= 1000 / qps[1] * 1.05, $"p50 {r.P50} ms against {1000 / qps[1]} ms per search in the same window" );
      Assert.Equal( 1.0, (double)r.Recall );
      int warmups = 2 * 3;
      Assert.Equal( warmups + samples + int.Parse( four[5], CultureInfo.InvariantCulture ), events.Count( e => e == "S" ) );
      Assert.DoesNotContain( (string[])r.Flags, f => f.Contains( "default@1", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// QPS@1 is only written when 1 is a concurrency level, but the latency window still runs.
   /// </summary>
   [Fact]
   public async Task Qps1_OnlyWhenOneIsALevel()
   {
      dynamic r = await ScenarioAsync( "PassesAsync", Args( "--seconds", "1", "--concurrency", "4", "--queries", "random:5", "--exact-seconds", "0" ), "fixed:1", null, false );
      Dictionary<int, double> qps = r.Qps;
      Assert.False( qps.ContainsKey( 1 ) );
      Assert.True( qps.ContainsKey( 4 ) );
      Assert.True( (int)r.LatencySamples > 0 );
   }

   /// <summary>
   /// The exact mode runs for --exact-seconds, cycling the queries: with 5 queries and 1 ms
   /// searches it times hundreds of searches, every exact search in the window is a sample, and
   /// its recall is still scored over every query.
   /// </summary>
   [Fact]
   public async Task Exact_IsATimeWindowCyclingTheQueries()
   {
      dynamic r = await ScenarioAsync( "PassesAsync", Args( "--seconds", "1", "--concurrency", "1", "--queries", "random:5", "--exact-seconds", "2" ), "fixed:1", "fixed:1", false );
      string[] exact = Record( r, "exact" );
      int samples = r.ExactQueries;
      Assert.True( samples >= 200, $"exact timed {samples} searches in 2 s of 1 ms searches" );
      Assert.Equal( samples, TimedSearches( (string[])r.Events, "exact" ) );
      Assert.True( double.Parse( exact[4], CultureInfo.InvariantCulture ) >= 2.0 );
      Assert.Equal( 1.0, (double)r.ExactRecall );
      Assert.DoesNotContain( (string[])r.Flags, f => f.Contains( "exact", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// A slow exact mode that cannot reach 200 samples, or answer every query, within its window
   /// (at most twice the exact seconds) is flagged for both.
   /// </summary>
   [Fact]
   public async Task Exact_ThinWindowFlagged()
   {
      dynamic r = await ScenarioAsync( "PassesAsync", Args( "--seconds", "1", "--concurrency", "1", "--queries", "random:20", "--exact-seconds", "1" ), "fixed:1", "fixed:150", false );
      string[] flags = r.Flags;
      Assert.Contains( flags, f => f.StartsWith( "WARNING: exact timed only", StringComparison.Ordinal ) );
      Assert.Contains( flags, f => f.StartsWith( "WARNING: exact answered", StringComparison.Ordinal ) && f.Contains( "of 20 queries", StringComparison.Ordinal ) );
      Assert.True( double.Parse( Record( r, "exact" )[4], CultureInfo.InvariantCulture ) < 3.0, "the exact window ran past twice its length" );
   }

   /// <summary>
   /// A default@1 window with two speeds (2 of 5 searches instant, the rest 20 ms) puts p50 well
   /// above the mean of the same window, and is flagged.
   /// </summary>
   [Fact]
   public async Task TwoSpeedWindow_Flagged()
   {
      dynamic r = await ScenarioAsync( "PassesAsync", Args( "--seconds", "2", "--concurrency", "1", "--queries", "random:5", "--exact-seconds", "0" ), "bimodal:20", null, false );
      Assert.Contains( (string[])r.Flags, f => f.StartsWith( "WARNING: default@1 p50", StringComparison.Ordinal ) && f.Contains( "x its mean", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// The shape rules on known samples: a clean window has no flag; fewer than 200 samples, a p50
   /// above 1.25 x the mean, and a mean above the p99 each raise their own flag.
   /// </summary>
   [Fact]
   public void ShapeFlags_Rules()
   {
      Assert.Empty( Flags( Enumerable.Repeat( 1.0, 300 ) ) );
      Assert.Single( Flags( Enumerable.Repeat( 1.0, 150 ) ), f => f.Contains( "fewer than 200", StringComparison.Ordinal ) );
      Assert.Single( Flags( Enumerable.Repeat( 1.0, 0 ) ) );
      string[] skew = Flags( Enumerable.Repeat( 10.0, 300 ).Concat( Enumerable.Repeat( 0.1, 200 ) ) );
      Assert.Single( skew );
      Assert.Contains( "x its mean", skew[0] );
      string[] stalls = Flags( Enumerable.Repeat( 1.0, 1000 ).Concat( Enumerable.Repeat( 10000.0, 5 ) ) );
      Assert.Single( stalls );
      Assert.Contains( "above its p99", stalls[0] );
      Assert.Empty( Flags( Enumerable.Repeat( 1.25, 200 ).Concat( Enumerable.Repeat( 1.0, 199 ) ) ) );
   }

   /// <summary>
   /// Settled means the p50s of the last three windows lie within 5% of the lowest; two windows
   /// are not enough, and a steady 4% drift per window is not settled.
   /// </summary>
   [Fact]
   public void IsSettled_Rules()
   {
      Assert.True( IsSettled( 5, 5.1, 5.2 ) );
      Assert.True( IsSettled( 10, 7, 5, 5.1, 5.2 ) );
      Assert.True( IsSettled( 5.2, 5.0, 5.24 ) );
      Assert.False( IsSettled( 5, 5.1 ) );
      Assert.False( IsSettled( 5, 5.2, 5.3 ) );
      Assert.False( IsSettled( 5, 5.2, 5.408 ) );
      Assert.False( IsSettled( 0, 0, 0 ) );
   }

   /// <summary>
   /// A latency that falls 6, 5, 4, 3 ms and then holds at 2 ms settles only once three windows of
   /// 100 searches sit at the floor: not before 900 searches, and with the last windows at the
   /// floor. Why the floor is checked against the first window and not as 2 ms: on a busy box a
   /// 2 ms timer wait takes nearer 3 ms, and that must not fail the test.
   /// </summary>
   [Fact]
   public async Task Settle_WaitsUntilLatencyStopsFalling()
   {
      dynamic r = await ScenarioAsync( "PrepareAsync", Args( "--concurrency", "1", "--queries", "random:5", "--exact-seconds", "1" ), "decay", 0.3, 60.0 );
      Assert.Null( (string?)r.Error );
      Assert.True( (bool)r.Settled, (string)r.SettleNote );
      Assert.True( (int)r.SettleSearches >= 900, $"settled after {r.SettleSearches} searches, before the latency stopped falling" );
      double[] p50s = r.WindowP50s;
      Assert.True( p50s[0] > 5.0, $"first window p50 {p50s[0]}" );
      Assert.All( p50s.TakeLast( 3 ), p => Assert.True( p >= 1.9 && p <= 0.6 * p50s[0], $"last windows {string.Join( ", ", p50s.TakeLast( 3 ) )} ms are not at the 2 ms floor (first window {p50s[0]} ms)" ) );
      Assert.StartsWith( "Settle: settled after", (string)r.SettleNote );
   }

   /// <summary>
   /// A latency that never holds still stops at the cap, is reported as not settled, and its note
   /// is a WARNING.
   /// </summary>
   [Fact]
   public async Task Settle_StopsAtTheCapAndWarns()
   {
      dynamic r = await ScenarioAsync( "PrepareAsync", Args( "--concurrency", "1", "--queries", "random:5", "--exact-seconds", "1" ), "alternate", 0.3, 2.0 );
      Assert.False( (bool)r.Settled );
      Assert.Contains( "cap", (string)r.StoppedBecause );
      Assert.InRange( (double)r.SettleSeconds, 2.0, 3.0 );
      Assert.StartsWith( "WARNING: latency had NOT settled", (string)r.SettleNote );
   }

   /// <summary>
   /// The rehearsal covers every pass type in the target's order, each for at least its length,
   /// all before the settle; a target whose default search fails every time is not timed at all.
   /// </summary>
   [Fact]
   public async Task Rehearsal_EveryPassTypeAndFailsLoud()
   {
      dynamic ok = await ScenarioAsync( "PrepareAsync", Args( "--concurrency", "1,4", "--queries", "random:5", "--exact-seconds", "1" ), "fixed:1", 0.4, 10.0 );
      string[] rehearsals = ok.Rehearsals;
      Assert.Equal( new[] { "default@1", "default@4", "exact" }, rehearsals.Select( x => x.Split( '|' )[0] ).OrderBy( p => p ) );
      Assert.All( rehearsals, x => Assert.True( double.Parse( x.Split( '|' )[1], CultureInfo.InvariantCulture ) >= 0.4 && int.Parse( x.Split( '|' )[2], CultureInfo.InvariantCulture ) > 0, x ) );
      string[] events = ok.Events;
      Assert.True( Array.FindLastIndex( events, e => e.Contains( ": rehearsal of ", StringComparison.Ordinal ) ) < Array.FindIndex( events, e => e.Contains( ": settling,", StringComparison.Ordinal ) ) );
      dynamic failed = await ScenarioAsync( "PrepareAsync", Args( "--concurrency", "1,4", "--queries", "random:5", "--exact-seconds", "1" ), "fail", 0.4, 10.0 );
      Assert.Contains( "every rehearsal search failed", (string)failed.Error );
      Assert.DoesNotContain( (string[])failed.Events, e => e.Contains( ": settling,", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// Pass records chain: the first timed pass ran after the settle, every later one after the
   /// pass before it in the run order, and each window starts after the previous one ended.
   /// </summary>
   [Fact]
   public async Task PassRecords_ChainInRunOrder()
   {
      dynamic r = await ScenarioAsync( "PassesAsync", Args( "--seconds", "1", "--concurrency", "1,2", "--queries", "random:5", "--exact-seconds", "1" ), "fixed:1", "fixed:1", true );
      string[] order = r.PassOrder;
      string[][] records = ( (string[])r.Passes ).Select( p => p.Split( '|' ) ).ToArray();
      Assert.Equal( order, records.Select( p => p[0] ) );
      Assert.Equal( new[] { "settle" }.Concat( order.Take( order.Length - 1 ) ), records.Select( p => p[1] ) );
      for( int i = 0; i < records.Length; i++ )
      {
         long start = long.Parse( records[i][2], CultureInfo.InvariantCulture );
         long end = long.Parse( records[i][3], CultureInfo.InvariantCulture );
         Assert.True( end > start, $"{records[i][0]} ended before it started" );
         string before = i == 0 ? "settle" : records[i - 1][0];
         Assert.True( i == 0 || start >= long.Parse( records[i - 1][3], CultureInfo.InvariantCulture ), $"{records[i][0]} started before {before} ended" );
         Assert.True( int.Parse( records[i][5], CultureInfo.InvariantCulture ) > 0, $"{records[i][0]} timed no searches" );
      }
   }

   /// <summary>
   /// The whole flow through TargetRunner with the benchmark's own 5 s rehearsal: every pass type
   /// is rehearsed for at least 5 s and the latency settles before the first timed pass, every
   /// timed pass still follows its own warm-up, and results.json carries the rehearsal, settle and
   /// per-pass notes.
   /// </summary>
   [Fact]
   public async Task Target_RehearsesSettlesThenTimes()
   {
      string folder = Path.Combine( AppContext.BaseDirectory, "measurement-tests", Guid.NewGuid().ToString( "N" ) );
      try
      {
         dynamic r = await ScenarioAsync( "MeasureAsync", "fixed:1", folder );
         Assert.Null( (string?)r.Error );
         string[] events = r.Events;
         string[] order = r.PassOrder;
         int firstTiming = Array.FindIndex( events, e => e.Contains( ": timing ", StringComparison.Ordinal ) );
         Assert.True( Array.FindLastIndex( events, e => e.Contains( ": rehearsal of ", StringComparison.Ordinal ) ) < firstTiming );
         Assert.True( Array.FindIndex( events, e => e.Contains( ": settled after", StringComparison.Ordinal ) ) < firstTiming );
         Assert.Equal( order.Length, events.Count( e => WARMUP_START.IsMatch( e ) ) );
         string[] notes = r.Notes;
         string rehearsal = Assert.Single( notes, n => n.StartsWith( "Rehearsal before any timed pass", StringComparison.Ordinal ) );
         Dictionary<string, double> rehearsed = REHEARSED.Matches( rehearsal ).ToDictionary( m => m.Groups[1].Value, m => double.Parse( m.Groups[2].Value, CultureInfo.InvariantCulture ) );
         Assert.Equal( order.OrderBy( p => p ), rehearsed.Keys.OrderBy( p => p ) );
         Assert.All( rehearsed, p => Assert.True( p.Value >= 5.0, $"{p.Key} rehearsed {p.Value} s" ) );
         Assert.Single( notes, n => n.StartsWith( "Settle: settled after", StringComparison.Ordinal ) );
         Assert.Equal( order, notes.Where( n => n.StartsWith( "Pass ", StringComparison.Ordinal ) ).Select( n => n.Split( ' ' )[1] ) );
         Assert.StartsWith( $"Pass {order[0]} after settle:", notes.First( n => n.StartsWith( "Pass ", StringComparison.Ordinal ) ) );
         Assert.Equal( 0, (int?)r.WarmupErrors );
         using JsonDocument json = JsonDocument.Parse( (string)r.Json );
         string[] written = json.RootElement.GetProperty( "targets" )[0].GetProperty( "notes" ).EnumerateArray().Select( n => n.GetString()! ).ToArray();
         Assert.Equal( notes, written );
      }
      finally
      {
         if( Directory.Exists( folder ) )
         {
            Directory.Delete( folder, true );
         }
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Arguments as an array, for the scenario calls.
   /// </summary>
   /// <param name="args">Arguments.</param>
   /// <returns>The array.</returns>
   private static string[] Args( params string[] args )
   {
      return args;
   }

   /// <summary>
   /// The searches sent between "timing PASS" and the next warm-up (or the end): the timed
   /// window's searches.
   /// </summary>
   /// <param name="events">Trace.</param>
   /// <param name="pass">Pass name.</param>
   /// <returns>Searches in the window.</returns>
   private static int TimedSearches( string[] events, string pass )
   {
      int start = Array.FindIndex( events, e => e.EndsWith( $"timing {pass}", StringComparison.Ordinal ) );
      Assert.True( start >= 0, $"no timing line for {pass}" );
      int end = Array.FindIndex( events, start + 1, e => WARMUP_START.IsMatch( e ) );
      return events.Skip( start + 1 ).Take( ( end < 0 ? events.Length : end ) - start - 1 ).Count( e => !e.StartsWith( "LOG ", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// The pass record of one pass, split into its fields.
   /// </summary>
   /// <param name="result">Scenario result.</param>
   /// <param name="pass">Pass name.</param>
   /// <returns>name|previous|startTicks|endTicks|seconds|searches|errors|p50|mean|p99|qps, split.</returns>
   private static string[] Record( dynamic result, string pass )
   {
      string[] passes = result.Passes;
      return Assert.Single( passes, p => p.StartsWith( pass + "|", StringComparison.Ordinal ) ).Split( '|' );
   }

   /// <summary>MeasurementScenarios.ShapeFlags.</summary>
   /// <param name="latencies">Latencies, ms.</param>
   /// <returns>Flags.</returns>
   private static string[] Flags( IEnumerable<double> latencies )
   {
      return (string[])Invoke( "ShapeFlags", (object)latencies.ToArray() );
   }

   /// <summary>MeasurementScenarios.IsSettled.</summary>
   /// <param name="p50s">Window p50s.</param>
   /// <returns>True when settled.</returns>
   private static bool IsSettled( params double[] p50s )
   {
      return (bool)Invoke( "IsSettled", (object)p50s );
   }

   /// <summary>
   /// Calls a synchronous scenario method.
   /// </summary>
   /// <param name="method">Method name.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>Its result.</returns>
   private static object Invoke( string method, params object[] arguments )
   {
      return COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( method )!.Invoke( null, arguments )!;
   }

   /// <summary>
   /// Calls an async scenario and returns its result, failing after 3 minutes.
   /// </summary>
   /// <param name="method">Scenario method name.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>The result record.</returns>
   private static async Task<dynamic> ScenarioAsync( string method, params object?[] arguments )
   {
      var task = (Task)COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( method )!.Invoke( null, arguments )!;
      Task finished = await Task.WhenAny( task, Task.Delay( TimeSpan.FromMinutes( 3 ) ) );
      Assert.True( finished == task, $"{method} did not finish within 3 minutes" );
      await task;
      return task.GetType().GetProperty( "Result" )!.GetValue( task )!;
   }

   /// <summary>
   /// Compiles every benchmark source file plus MeasurementScenarios.cs into one in-memory
   /// assembly. Packages only the benchmark uses are found through the benchmark's own restore
   /// output and loaded from the NuGet folder when first needed.
   /// </summary>
   /// <returns>The assembly.</returns>
   private static Assembly Compile()
   {
      string root = RepoRoot();
      string bench = Path.Combine( root, "src", "GenericVectorBuilder.Bench" );
      var parse = new CSharpParseOptions( LanguageVersion.Latest );
      List<SyntaxTree> trees = Directory.EnumerateFiles( bench, "*.cs", SearchOption.AllDirectories )
         .Where( f => Path.GetRelativePath( bench, f ).Split( Path.DirectorySeparatorChar )[0] is not ( "bin" or "obj" ) )
         .Select( f => CSharpSyntaxTree.ParseText( File.ReadAllText( f ), parse, f ) ).ToList();
      string scenarios = Path.Combine( root, "tests", "GenericVectorBuilder.Engines.Tests", "Bench", "MeasurementScenarios.cs" );
      trees.Add( CSharpSyntaxTree.ParseText( File.ReadAllText( scenarios ), parse.WithPreprocessorSymbols( "BENCH_UNDER_TEST" ), scenarios ) );
      trees.Add( CSharpSyntaxTree.ParseText( IMPLICIT_USINGS, parse ) );
      List<string> platform = ( (string)AppContext.GetData( "TRUSTED_PLATFORM_ASSEMBLIES" )! ).Split( Path.PathSeparator ).ToList();
      List<string> extra = BenchOnlyPackages( bench, platform.Select( Path.GetFileName ).ToHashSet( StringComparer.OrdinalIgnoreCase ) );
      AssemblyLoadContext.Default.Resolving += ( context, name ) =>
         extra.FirstOrDefault( p => Path.GetFileNameWithoutExtension( p ).Equals( name.Name, StringComparison.OrdinalIgnoreCase ) ) is string path ? context.LoadFromAssemblyPath( path ) : null;
      var options = new CSharpCompilationOptions( OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable );
      CSharpCompilation compilation = CSharpCompilation.Create( "BenchMeasurementUnderTest", trees, platform.Concat( extra ).Select( p => MetadataReference.CreateFromFile( p ) ), options );
      using var image = new MemoryStream();
      Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit( image );
      Assert.True( result.Success, string.Join( Environment.NewLine, result.Diagnostics.Where( d => d.Severity == DiagnosticSeverity.Error ) ) );
      return Assembly.Load( image.ToArray() );
   }

   /// <summary>
   /// Runtime files of the packages the benchmark restored that this test host does not
   /// already have, read from the benchmark's project.assets.json.
   /// </summary>
   /// <param name="bench">Benchmark project folder.</param>
   /// <param name="loaded">File names already on the test host's platform list.</param>
   /// <returns>Full paths of the extra assemblies.</returns>
   private static List<string> BenchOnlyPackages( string bench, HashSet<string?> loaded )
   {
      string assets = Path.Combine( bench, "obj", "project.assets.json" );
      Assert.True( File.Exists( assets ), $"Restore the benchmark project first (no {assets})." );
      using JsonDocument doc = JsonDocument.Parse( File.ReadAllText( assets ) );
      string packages = doc.RootElement.GetProperty( "packageFolders" ).EnumerateObject().First().Name;
      JsonElement libraries = doc.RootElement.GetProperty( "libraries" );
      var extra = new List<string>();
      foreach( JsonProperty library in doc.RootElement.GetProperty( "targets" ).EnumerateObject().First().Value.EnumerateObject() )
      {
         if( !library.Value.TryGetProperty( "runtime", out JsonElement runtime ) || library.Value.GetProperty( "type" ).GetString() != "package" )
         {
            continue;
         }

         string folder = libraries.GetProperty( library.Name ).GetProperty( "path" ).GetString()!;
         extra.AddRange( runtime.EnumerateObject().Select( r => r.Name ).Where( r => r.EndsWith( ".dll", StringComparison.OrdinalIgnoreCase ) && !loaded.Contains( Path.GetFileName( r ) ) )
            .Select( r => Path.Combine( packages, folder, r ) ) );
      }

      return extra;
   }

   /// <summary>
   /// Walks up from the test binary to the folder holding the solution file.
   /// </summary>
   /// <returns>The repository root.</returns>
   private static string RepoRoot()
   {
      var dir = new DirectoryInfo( AppContext.BaseDirectory );
      while( dir != null && !File.Exists( Path.Combine( dir.FullName, "GenericVectorBuilder.slnx" ) ) )
      {
         dir = dir.Parent;
      }

      return dir?.FullName ?? throw new InvalidOperationException( "Repository root not found." );
   }

   #endregion Private Methods
}
