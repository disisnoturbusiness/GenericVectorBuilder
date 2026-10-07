using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// Compiles the consolidation's real sources (every file under src/GenericVectorBuilder.Bench/Stats,
/// the Report readers, the command and P5's fact and sentence files) once with Roslyn, together with
/// a small facade of string and number calls, and runs the consolidate command on folders the tests
/// write. Shared by every Consolidate*Tests class.
/// Why Roslyn: the benchmark is a console project this test project does not reference, so the real
/// files are compiled and run, not copies. Why a facade: the tests then call plain methods, not
/// reflection over the report model.
/// </summary>
public static class ConsolidateHarness
{
   #region Data Members

   /// <summary>Longest any one test body may run before it is reported as hung.</summary>
   public static readonly TimeSpan TEST_DEADLINE = TimeSpan.FromSeconds( 180 );

   /// <summary>The Report files the consolidation compiles with every Stats file.</summary>
   public static readonly string[] REPORT_SOURCES =
   {
      "Report/RunResult.cs", "Report/RunConditions.cs", "Report/ResultJson.cs", "Report/ConsolidateCommand.cs", "Report/ConsolidatedMarkdown.cs",
      "Report/EngineFacts.cs", "Report/Sentence.cs", "Report/SentenceAudit.cs", "Report/BannedWords.cs",
   };

   private static readonly Lazy<Assembly> COMPILED = new( Compile );

   private const string FACADE = @"
global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; global using System.Threading; global using System.Threading.Tasks;
using System.Text.Json;
using GenericVectorBuilder.Bench.Report;
using GenericVectorBuilder.Bench.Stats;

namespace Facade;

public static class Api
{
   public static string[] Consolidate( string[] args )
   {
      var log = new List<string>();
      int exit = ConsolidateCommand.Run( args, log.Add, CancellationToken.None );
      return new[] { exit.ToString() }.Concat( log ).ToArray();
   }

   public static int TBpFor( int maxBp ) { return ThresholdBasis.TBpFor( maxBp ); }

   public static int MoveBp( double hi, double lo ) { return ThresholdBasis.MoveBp( hi, lo ); }

   public static int RatioBp( double num, double den ) { return ClaimRule.RatioBp( num, den ); }

   public static string Basis( string[] basisFolders, string[] otherFolders, string exclusionsPath )
   {
      List<BasisRun> basis = basisFolders.Select( f => new BasisRun( RunResult.Load( f ), ""basis"" ) ).ToList();
      List<BasisRun> other = otherFolders.Select( f => new BasisRun( RunResult.Load( f ), ""other"" ) ).ToList();
      BasisInfo info = ThresholdBasis.Compute( basis, other, ConsolidateLoader.Exclusions( exclusionsPath ) );
      return JsonSerializer.Serialize( info, ConsolidateAudit.JSON );
   }

   public static string Conditions( string resultsJson )
   {
      RunResult run = RunResult.Parse( ""/x/run"", resultsJson );
      RunConditions c = run.Conditions;
      RunClock k = c.Clock;
      return string.Join( ""|"", k.Pinned, k.PinnedMhz, k.CeilingBeforeMhz, k.NoTurboDuring, k.UncoreDuring ?? ""null"", k.ToleranceBp, k.LegacyParsed, k.Source ?? ""null"",
         c.BuildConfiguration ?? ""null"", c.BuildCommit ?? ""null"", c.BootId ?? ""null"", c.EngineCpuRule ?? ""null"", string.Join( "";"", c.MissingNames() ), run.SettingsOnly );
   }

   public static string[] PassClocks( string resultsJson )
   {
      RunResult run = RunResult.Parse( ""/x/run"", resultsJson );
      return run.Conditions.Passes.Select( p => ConsolidateClock.Recompute( run, p ) ).Select( c => $""{c.EngineMedian}|{c.ClientMedian}|{c.Read}|{c.Off}"" ).ToArray();
   }

   public static string[] SessionClock( string[] folders )
   {
      try
      {
         SessionClock s = ConsolidateClock.ForSession( folders.Select( RunResult.Load ).ToList() );
         return new[] { $""{s.Pinned}|{s.PinnedMhz}|{s.Uncore}|{s.CeilingBeforeMhz}|{s.PassesEvaluated}|{s.ClockOffPasses}|{s.ClockUnreadPasses}"" };
      }
      catch( InvalidDataException ex )
      {
         return new[] { ""refused: "" + ex.Message };
      }
   }

   public static string[] Banned( string text ) { return BannedWords.Find( text ).ToArray(); }

   public static int Words( string text ) { return SentenceAudit.WordCount( text ); }

   public static string[] Templates()
   {
      return typeof( ConsolidateText ).GetFields( System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static )
         .Where( f => f.FieldType == typeof( string ) && f.IsLiteral && f.Name != ""MSR_FILE"" && f.Name != ""MSR_TOKEN"" && f.Name != ""HASH_FILE"" && f.Name != ""HASH_TOKEN"" )
         .Select( f => f.Name + ""="" + (string)f.GetRawConstantValue()! ).Concat( ConsolidateText.WHY_FRAMING.Select( t => ""WHY_FRAMING="" + t ) ).ToArray();
   }

   public static string Framing( string name )
   {
      return (string)typeof( ConsolidateFraming ).GetField( name )!.GetValue( null )!;
   }

   public static string[] SplitSessions( string value )
   {
      try
      {
         return ConsolidateArgs.SplitSessions( value ).Select( s => s.Name + "":"" + string.Join( "","", s.Folders ) ).ToArray();
      }
      catch( ArgumentException ex )
      {
         return new[] { ""refused: "" + ex.Message };
      }
   }
}";

   #endregion Data Members

   #region Public Methods

   /// <summary>The compiled assembly.</summary>
   public static Assembly Compiled => COMPILED.Value;

   /// <summary>
   /// Calls a facade method, unwrapping the exception a call throws.
   /// </summary>
   /// <param name="name">Method name.</param>
   /// <param name="args">Arguments.</param>
   /// <returns>Its result.</returns>
   public static object? Call( string name, params object?[] args )
   {
      MethodInfo info = Compiled.GetType( "Facade.Api" )!.GetMethod( name ) ?? throw new MissingMethodException( "Facade.Api", name );
      try
      {
         return info.Invoke( null, args );
      }
      catch( TargetInvocationException ex ) when( ex.InnerException is not null )
      {
         System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture( ex.InnerException ).Throw();
         throw;
      }
   }

   /// <summary>
   /// Runs a synchronous test body on another thread and fails the test when it does not finish in time.
   /// Why: xunit's own Timeout is honoured for async tests only.
   /// </summary>
   /// <param name="body">The test body.</param>
   /// <returns>A task that completes with the body, or throws a timeout.</returns>
   public static async Task Timed( Action body )
   {
      await Task.Run( body ).WaitAsync( TEST_DEADLINE );
   }

   /// <summary>
   /// Walks up from the test binary to the folder holding the solution file.
   /// </summary>
   /// <returns>The repository root.</returns>
   public static string RepoRoot()
   {
      var dir = new DirectoryInfo( AppContext.BaseDirectory );
      while( dir != null && !File.Exists( Path.Combine( dir.FullName, "GenericVectorBuilder.slnx" ) ) )
      {
         dir = dir.Parent;
      }

      return dir?.FullName ?? throw new InvalidOperationException( "Repository root not found." );
   }

   /// <summary>
   /// The repository's bench-results folder (read-only for these tests).
   /// </summary>
   /// <returns>The path.</returns>
   public static string BenchResults()
   {
      return Path.Combine( RepoRoot(), "bench-results" );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Compiles every Stats file, the Report files and the facade into an in-memory assembly.
   /// </summary>
   /// <returns>The assembly.</returns>
   private static Assembly Compile()
   {
      string bench = Path.Combine( RepoRoot(), "src", "GenericVectorBuilder.Bench" );
      IEnumerable<string> files = Directory.GetFiles( Path.Combine( bench, "Stats" ), "*.cs" ).OrderBy( f => f, StringComparer.Ordinal )
         .Concat( REPORT_SOURCES.Select( s => Path.Combine( bench, s ) ) );
      var trees = files.Select( f => CSharpSyntaxTree.ParseText( File.ReadAllText( f ), path: f ) ).ToList();
      trees.Add( CSharpSyntaxTree.ParseText( FACADE ) );
      IEnumerable<MetadataReference> references = ( (string)AppContext.GetData( "TRUSTED_PLATFORM_ASSEMBLIES" )! )
         .Split( Path.PathSeparator ).Select( p => MetadataReference.CreateFromFile( p ) );
      var options = new CSharpCompilationOptions( OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable );
      CSharpCompilation compilation = CSharpCompilation.Create( "ConsolidateUnderTest", trees, references, options );
      using var image = new MemoryStream();
      Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit( image );
      Assert.True( result.Success, string.Join( Environment.NewLine, result.Diagnostics.Where( d => d.Severity == DiagnosticSeverity.Error ) ) );
      return Assembly.Load( image.ToArray() );
   }

   #endregion Private Methods
}

/// <summary>
/// A scratch results folder and a scratch repository for one test, under the test binaries (never
/// the system temp folder or the real bench-results), with builders for synthetic runs in the shape
/// the v8 run tooling writes, a fact sheet and an exclusions file that hold for them, and a call that
/// consolidates them. Removed again on dispose.
/// Why a scratch repository: the facts' file sources and the sentences' file sources resolve under
/// --repo, so the test copies the three real files the templates cite and writes one sink file per
/// synthetic target.
/// </summary>
public sealed class SyntheticBench : IDisposable
{
   #region Data Members

   /// <summary>Real repository files the templates cite, copied into the scratch repository.</summary>
   public static readonly string[] CITED_FILES =
   {
      "src/GenericVectorBuilder.Bench/Running/MachineControlSampler.cs", "src/GenericVectorBuilder.Bench/Running/MachineControlPinning.cs", "src/GenericVectorBuilder.Bench/Data/GoldenQueries.cs",
      "tests/GenericVectorBuilder.Engines.Tests/Bench/MachineControlV8Tests.cs",
   };

   /// <summary>The in-memory quote of the synthetic Redis docs page.</summary>
   public const string MEMORY_QUOTE = "Redis is an in-memory database";

   private readonly List<string> _sinks = new();

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the scratch folders.
   /// </summary>
   public SyntheticBench()
   {
      string root = Path.Combine( AppContext.BaseDirectory, "consolidate-tests", Guid.NewGuid().ToString( "N" ) );
      Results = Path.Combine( root, "results" );
      Repo = Path.Combine( root, "repo" );
      Directory.CreateDirectory( Results );
      Directory.CreateDirectory( Repo );
      File.WriteAllText( Path.Combine( Repo, "GenericVectorBuilder.slnx" ), "<Solution />" );
      foreach( string file in CITED_FILES )
      {
         Directory.CreateDirectory( Path.GetDirectoryName( Path.Combine( Repo, file ) )! );
         File.Copy( Path.Combine( ConsolidateHarness.RepoRoot(), file ), Path.Combine( Repo, file ) );
      }

      Directory.CreateDirectory( Path.Combine( Repo, "design", "engine-docs" ) );
      File.WriteAllText( Path.Combine( Repo, "design", "engine-docs", "redis.html" ), $"<html><body><p>{MEMORY_QUOTE} with snapshots.</p></body></html>" );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The scratch results folder.</summary>
   public string Results { get; }

   /// <summary>The scratch repository.</summary>
   public string Repo { get; }

   /// <summary>The log of the last consolidation.</summary>
   public List<string> Log { get; } = new();

   /// <summary>
   /// A synthetic target with every field the v8 tooling writes for a searched target.
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <param name="p50">p50 ms.</param>
   /// <param name="qps1">QPS at 1.</param>
   /// <param name="qps8">QPS at 8.</param>
   /// <param name="exact">Exact p50, or null for no exact pass.</param>
   /// <returns>The target object.</returns>
   public static JsonObject Target( string name, double p50, double qps1, double qps8, double? exact = null )
   {
      var search = new JsonObject { ["p50Ms"] = p50, ["qps"] = new JsonObject { ["1"] = qps1, ["8"] = qps8 }, ["recall"] = 1.0, ["recallHits"] = 200, ["ndcg"] = 0.5, ["errors"] = 0 };
      if( exact.HasValue )
      {
         search["exactP50Ms"] = exact.Value;
      }

      return new JsonObject
      {
         ["name"] = name,
         ["engine"] = name + " 1.0",
         ["index"] = "HNSW test index m=16",
         ["hosting"] = "compose",
         ["searchSettings"] = new JsonObject { ["ef"] = "100" },
         ["load"] = new JsonObject { ["rows"] = 524, ["rowsPerSecond"] = 1000.0 },
         ["search"] = search,
         ["settled"] = true,
         ["durability"] = $"{name} fsyncs every commit.",
         ["notes"] = new JsonArray( $"Engine files (SHA-256, first 12 hex digits): [{name}.compose.yaml 0123456789ab]. This run created the engine from these files." ),
      };
   }

   /// <summary>
   /// Writes a synthetic run in the v8 shape: machine control on, the clock pinned and recorded, a pass per target per metric with per-CPU medians.
   /// </summary>
   /// <param name="name">Folder name.</param>
   /// <param name="started">startedUtc.</param>
   /// <param name="seed">runSeed.</param>
   /// <param name="targets">Target objects.</param>
   /// <param name="change">Changes to the run object after it is built, or null.</param>
   /// <returns>The folder path.</returns>
   public string WriteRun( string name, string started, int seed, IEnumerable<JsonObject> targets, Action<JsonObject>? change = null )
   {
      List<JsonObject> list = targets.ToList();
      foreach( JsonObject t in list )
      {
         Sink( (string)t["name"]! );
      }

      var run = new JsonObject
      {
         ["startedUtc"] = started,
         ["command"] = "run-all",
         ["commandLine"] = $"/x/src/GenericVectorBuilder.Bench/bin/Release/net10.0/GenericVectorBuilder.Bench.dll run-all --seed {seed}",
         ["machine"] = new JsonObject { ["host"] = "testbox", ["cpu"] = "Test CPU 3000", ["logicalCpus"] = 8, ["ramGiB"] = 62.7, ["os"] = "Test OS", ["dotNet"] = ".NET 10" },
         ["pipeline"] = "eshoponweb",
         ["rows"] = 524,
         ["dimension"] = 1024,
         ["queries"] = "golden: 20 labelled questions",
         ["queryCount"] = 20,
         ["top"] = 10,
         ["concurrency"] = new JsonArray( 1, 8 ),
         ["secondsPerLevel"] = 20,
         ["truthNdcg"] = 0.5,
         ["runSeed"] = seed,
         ["notes"] = new JsonArray( "Order: targets ran one at a time in a random order." ),
         ["targets"] = new JsonArray( list.Select( t => (JsonNode)t ).ToArray() ),
         ["conditions"] = Conditions( list ),
      };
      change?.Invoke( run );
      string folder = Path.Combine( Results, name );
      Directory.CreateDirectory( folder );
      File.WriteAllText( Path.Combine( folder, "results.json" ), run.ToJsonString() );
      return folder;
   }

   /// <summary>
   /// Writes a session of three runs on one day, an hour apart, seeds from a base.
   /// </summary>
   /// <param name="day">Day, "2026-10-06".</param>
   /// <param name="seedBase">Seed of the first run (701).</param>
   /// <param name="targets">The targets of run 0, 1 or 2.</param>
   /// <param name="change">Changes to run i after it is built, or null.</param>
   /// <returns>The session argument value without its name: "f1,f2,f3".</returns>
   public string WriteSession( string day, int seedBase, Func<int, IEnumerable<JsonObject>> targets, Action<int, JsonObject>? change = null )
   {
      var folders = new List<string>();
      for( int i = 0; i < 3; i++ )
      {
         int run = i;
         string name = $"{day.Replace( "-", string.Empty, StringComparison.Ordinal )}-1{i}0000-eshoponweb";
         WriteRun( name, $"{day}T1{i}:00:00Z", seedBase + i, targets( i ), change == null ? null : o => change( run, o ) );
         folders.Add( name );
      }

      return string.Join( ",", folders );
   }

   /// <summary>
   /// Writes a fact sheet that holds for the synthetic targets: index and search from the index text,
   /// storage "not recorded" (or the saved Redis page for a target named redis), protocol from the target's sink file.
   /// </summary>
   /// <param name="targets">Target names.</param>
   /// <returns>The path.</returns>
   public string WriteFacts( IEnumerable<string> targets )
   {
      var rows = new JsonArray();
      foreach( string t in targets )
      {
         rows.Add( Fact( t, "index", "HNSW test index m=16", $"results:targets[{t}].index#HNSW test index m=16", "recorded" ) );
         JsonObject search = Fact( t, "search", "HNSW test index", $"results:targets[{t}].index#HNSW test index", "recorded" );
         search["mode"] = "approximate";
         rows.Add( search );
         rows.Add( t == "redis"
            ? Fact( t, "storage", MEMORY_QUOTE, $"doc:design/engine-docs/redis.html#{MEMORY_QUOTE}", "documented" )
            : Fact( t, "storage", "not recorded", $"absent:results:targets[{t}]#in memory|in-memory|memory pool|RAM", "checked" ) );
         rows.Add( Fact( t, "protocol", $"{Stem( t )}.Client", $"src/{Stem( t )}Sink.cs#using {Stem( t )}.Client;", "set-by-code" ) );
      }

      string path = Path.Combine( Repo, "engine-facts.json" );
      File.WriteAllText( path, rows.ToJsonString() );
      return path;
   }

   /// <summary>
   /// Writes an exclusions file.
   /// </summary>
   /// <param name="rows">Rows, or none.</param>
   /// <returns>The path.</returns>
   public string WriteExclusions( params JsonObject[] rows )
   {
      string path = Path.Combine( Repo, "basis-exclusions.json" );
      File.WriteAllText( path, new JsonArray( rows.Select( r => (JsonNode)r ).ToArray() ).ToJsonString() );
      return path;
   }

   /// <summary>
   /// Consolidates with --results-dir, --repo, --facts, --exclusions and --out filled in (an empty exclusions file and facts for every target written when absent).
   /// </summary>
   /// <param name="args">The other arguments.</param>
   /// <returns>The exit code, consolidated.json (null when not written) and consolidated.md.</returns>
   public (int Exit, JsonElement? Json, string? Md) Consolidate( params string[] args )
   {
      string output = Path.Combine( Path.GetDirectoryName( Results )!, "out" );
      var full = new List<string>( args ) { "--results-dir", Results, "--repo", Repo };
      if( !args.Contains( "--facts" ) )
      {
         full.AddRange( new[] { "--facts", File.Exists( Path.Combine( Repo, "engine-facts.json" ) ) ? Path.Combine( Repo, "engine-facts.json" ) : WriteFacts( _sinks ) } );
      }

      if( !args.Contains( "--exclusions" ) )
      {
         full.AddRange( new[] { "--exclusions", File.Exists( Path.Combine( Repo, "basis-exclusions.json" ) ) ? Path.Combine( Repo, "basis-exclusions.json" ) : WriteExclusions() } );
      }

      if( !args.Contains( "--out" ) )
      {
         full.AddRange( new[] { "--out", output } );
      }

      var result = (string[])ConsolidateHarness.Call( "Consolidate", (object)full.ToArray() )!;
      Log.Clear();
      Log.AddRange( result.Skip( 1 ) );
      string folder = full[full.IndexOf( "--out" ) + 1];
      string json = Path.Combine( folder, "consolidated.json" );
      return ( int.Parse( result[0] ), File.Exists( json ) ? JsonDocument.Parse( File.ReadAllText( json ) ).RootElement.Clone() : null,
         File.Exists( Path.Combine( folder, "consolidated.md" ) ) ? File.ReadAllText( Path.Combine( folder, "consolidated.md" ) ) : null );
   }

   /// <summary>
   /// The whole log as one text, for assertion messages.
   /// </summary>
   /// <returns>The log.</returns>
   public string LogText()
   {
      return string.Join( Environment.NewLine, Log );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The conditions block of a synthetic run: machine control on, clock pinned at 3500 MHz, three passes per target at 3492 MHz on every CPU.
   /// </summary>
   /// <param name="targets">Targets.</param>
   /// <returns>The block.</returns>
   private static JsonObject Conditions( List<JsonObject> targets )
   {
      var passes = new JsonArray();
      foreach( JsonObject t in targets )
      {
         foreach( string pass in new[] { "default@1", "default@8", "exact" } )
         {
            var cpus = new JsonArray( Enumerable.Range( 0, 8 ).Select( c => (JsonNode)new JsonObject { ["cpu"] = c, ["median"] = 3492 } ).ToArray() );
            passes.Add( new JsonObject
            {
               ["target"] = (string)t["name"]!, ["pass"] = pass, ["governor"] = "performance", ["engineMhzMedian"] = 3492, ["clientMhzMedian"] = 3492, ["cpuMhz"] = cpus,
               ["clockRead"] = true, ["clockOff"] = false, ["outsideLoadDuring"] = 0.1, ["busyBox"] = false, ["clientCpuMsPerSearch"] = 0.5,
               ["engineCpuMsPerSearch"] = 1.0, ["engineCpusBusy"] = 1.5,
            } );
         }
      }

      return new JsonObject
      {
         ["buildConfiguration"] = "Release", ["machineControl"] = "on", ["governor"] = "performance", ["clientCpus"] = "0-1,4-5", ["engineCpus"] = "2-3,6-7",
         ["warmupSearches"] = 20, ["exactSeconds"] = 60,
         ["cpuIdle"] = new JsonObject { ["driver"] = "intel_idle", ["governor"] = "menu" },
         ["clock"] = new JsonObject
         {
            ["pinned"] = true, ["pinnedMhz"] = 3500, ["ceilingBeforeMhz"] = 3600, ["toleranceBp"] = 100,
            ["noTurbo"] = new JsonObject { ["before"] = 0, ["during"] = 1 }, ["uncoreMsr620"] = new JsonObject { ["before"] = "0xc1e", ["during"] = "0x1e1e" },
         },
         ["passes"] = passes,
      };
   }

   /// <summary>
   /// A fact row.
   /// </summary>
   /// <param name="target">Target.</param>
   /// <param name="kind">Kind.</param>
   /// <param name="text">Text.</param>
   /// <param name="source">Source.</param>
   /// <param name="confidence">Confidence.</param>
   /// <returns>The row.</returns>
   private static JsonObject Fact( string target, string kind, string text, string source, string confidence )
   {
      return new JsonObject { ["target"] = target, ["kind"] = kind, ["text"] = text, ["source"] = source, ["confidence"] = confidence };
   }

   /// <summary>
   /// A target's sink-file stem: its name with a capital first letter and no dashes.
   /// </summary>
   /// <param name="target">Target.</param>
   /// <returns>The stem.</returns>
   private static string Stem( string target )
   {
      string plain = target.Replace( "-", string.Empty, StringComparison.Ordinal );
      return char.ToUpperInvariant( plain[0] ) + plain[1..];
   }

   /// <summary>
   /// Writes the target's sink file once, holding the protocol token on a code line.
   /// </summary>
   /// <param name="target">Target.</param>
   private void Sink( string target )
   {
      if( _sinks.Contains( target ) )
      {
         return;
      }

      _sinks.Add( target );
      Directory.CreateDirectory( Path.Combine( Repo, "src" ) );
      File.WriteAllText( Path.Combine( Repo, "src", $"{Stem( target )}Sink.cs" ), $"using {Stem( target )}.Client;{Environment.NewLine}" );
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Removes the scratch folders.
   /// </summary>
   public void Dispose()
   {
      string root = Path.GetDirectoryName( Results )!;
      if( Directory.Exists( root ) )
      {
         Directory.Delete( root, recursive: true );
      }
   }

   #endregion IDisposable
}
