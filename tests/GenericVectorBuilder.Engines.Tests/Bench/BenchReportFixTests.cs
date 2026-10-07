using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using GenericVectorBuilder.Engines.Sinks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The fixes of the v6 "mongodb-and-report" piece that live in the benchmark's run and report code:
/// run-all writes each searched target's search settings (so the comparability check of the
/// consolidate command compares something), an engine start that needed more than one attempt keeps
/// its notes even when no CPU set is asked for, and the texts that described an older method or
/// a shared container now say what the code does.
/// Why the sources are compiled here with Roslyn: the benchmark is a console project this test
/// project does not reference, so the real files under src/GenericVectorBuilder.Bench are compiled
/// and run (the same way <see cref="RunnerTests"/> does it).
/// </summary>
public class BenchReportFixTests : IDisposable
{
   #region Data Members

   private const string WRITER_TYPE = "GenericVectorBuilder.Bench.Report.ResultsWriter";
   private const string REPORT_TYPE = "GenericVectorBuilder.Bench.Report.BenchReport";
   private const string TARGET_TYPE = "GenericVectorBuilder.Bench.Report.TargetReport";
   private const string SEARCH_TYPE = "GenericVectorBuilder.Bench.Report.SearchReport";
   private const string RESULT_TYPE = "GenericVectorBuilder.Bench.Report.RunResult";
   private const string SETTINGS_TYPE = "GenericVectorBuilder.Bench.Report.TargetSearchSettings";
   private const string HOST_TYPE = "GenericVectorBuilder.Bench.Running.ComposeEngineHost";
   private const string IMPLICIT_USINGS = "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; "
      + "global using System.Net.Http; global using System.Threading; global using System.Threading.Tasks;";
   private const string PGVECTOR = "HNSW vector_cosine_ops m=16 ef_construction=128, hnsw.ef_search=100 per query, float32 vector(n), cosine; exact mode = same query with index scans off (sequential scan)";
   private static readonly Lazy<Assembly> COMPILED = new( Compile );

   private readonly string _root;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a private folder for the results this test writes, under the test binaries.
   /// </summary>
   public BenchReportFixTests()
   {
      _root = Path.Combine( AppContext.BaseDirectory, "bench-report-fix-tests", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( _root );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The settings are the name=value pairs of the index description, sorted, with no space around
   /// the equals sign (so "exact mode = ..." is prose), trailing full stops dropped, and the quoted
   /// pairs of SQL Server's DiskANN build record; a description that states none is kept whole.
   /// </summary>
   [Theory]
   [InlineData( PGVECTOR, "ef_construction=128, hnsw.ef_search=100, m=16" )]
   [InlineData( "HNSW TYPE FLOAT32 M=16 EF_CONSTRUCTION=128, EF_RUNTIME=100 per query, cosine; exact mode = FLAT index built on first exact query", "EF_CONSTRUCTION=128, EF_RUNTIME=100, M=16" )]
   [InlineData( "DiskANN (preview) via VECTOR_SEARCH, cosine, build {\"StartId\":\"306\", \"L\":\"48\", \"M\":\"8\", \"R\":\"48\"}; the exact mode scans the same table", "L=48, M=8, R=48, StartId=306" )]
   [InlineData( "VECTOR INDEX (HNSW variant) DISTANCE=cosine, M=16 (no ef_construction setting exists), mhnsw_ef_search=100 per statement (the ef 100 most engines here use)", "DISTANCE=cosine, M=16, mhnsw_ef_search=100" )]
   [InlineData( "HNSW float32, m=16, ef_construction=128, cosine; search k=top, num_candidates=100; 1 shard, 0 replicas", "ef_construction=128, k=top, m=16, num_candidates=100" )]
   [InlineData( "HNSW (vss extension) FLOAT[n] metric=cosine m=16, persistent (hnsw_enable_experimental_persistence=true.) score = 1 - cosine", "hnsw_enable_experimental_persistence=true, m=16, metric=cosine" )]
   [InlineData( "exact VECTOR_DISTANCE cosine, no vector index (full scan)", "description=exact VECTOR_DISTANCE cosine, no vector index (full scan)" )]
   public void SearchSettings_AreTheNamedSettingsOfTheIndexDescription( string index, string expected )
   {
      Assert.Equal( expected, string.Join( ", ", Settings( index ).Select( p => $"{p.Key}={p.Value}" ) ) );
   }

   /// <summary>
   /// An empty description has no settings, and a long description with none is cut, not dropped.
   /// </summary>
   [Fact]
   public void SearchSettings_OfNothing_AreEmpty_AndALongDescriptionIsCut()
   {
      Assert.Empty( Settings( null ) );
      Assert.Empty( Settings( "  " ) );
      string cut = Settings( new string( 'x', 900 ) )["description"];
      Assert.Equal( 403, cut.Length );
      Assert.EndsWith( "...", cut );
   }

   /// <summary>
   /// The settings survive the whole path the consolidate command relies on: written into a run's
   /// results.json by the results writer, read back by the consolidate reader as the sorted text it
   /// compares between runs, and printed in results.md. A target that was not searched carries none.
   /// </summary>
   [Fact]
   public void SearchSettings_AreWrittenToResults_AndReadBackByTheConsolidateReader()
   {
      dynamic report = Activator.CreateInstance( COMPILED.Value.GetType( REPORT_TYPE )! )!;
      report.Command = "run-all";
      report.Pipeline = "p";
      report.StartedUtc = "2026-10-05T10:00:00Z";
      dynamic searched = Target( "pgvector", withSearch: true );
      searched.SearchSettings = Settings( PGVECTOR );
      dynamic notSearched = Target( "mariadb", withSearch: false );
      report.Targets.Add( searched );
      report.Targets.Add( notSearched );

      COMPILED.Value.GetType( WRITER_TYPE )!.GetMethod( "Write" )!.Invoke( null, new object[] { report, _root } );

      string json = File.ReadAllText( Path.Combine( _root, "results.json" ) );
      using JsonDocument doc = JsonDocument.Parse( json );
      JsonElement[] targets = doc.RootElement.GetProperty( "targets" ).EnumerateArray().ToArray();
      Assert.Equal( "100", targets[0].GetProperty( "searchSettings" ).GetProperty( "hnsw.ef_search" ).GetString() );
      Assert.False( targets[1].TryGetProperty( "searchSettings", out _ ) );
      dynamic run = COMPILED.Value.GetType( RESULT_TYPE )!.GetMethod( "Load" )!.Invoke( null, new object[] { _root } )!;
      Assert.Equal( "ef_construction=128, hnsw.ef_search=100, m=16", (string)run.Find( "pgvector" ).SearchSettings );
      Assert.Null( (string?)run.Find( "mariadb" ).SearchSettings );
      Assert.Contains( "- Search settings: ef_construction=128, hnsw.ef_search=100, m=16", File.ReadAllText( Path.Combine( _root, "results.md" ) ) );
   }

   /// <summary>
   /// Every target of the 19 engine run of 5 Oct (07:33, run 601, the first of the v6 final runs) gets
   /// settings out of its own index text, so run-all would have recorded something for each of the 19
   /// engines it measured. Read from the raw run folder, which is never renamed, and not from a
   /// published or blocked folder, which is.
   /// </summary>
   [Fact]
   public void SearchSettings_CoverEveryTargetOfARealRun()
   {
      using JsonDocument doc = JsonDocument.Parse( File.ReadAllText( Path.Combine( RepoRoot(), "bench-results", "20261005-073329-eshoponweb", "results.json" ) ) );
      JsonElement[] targets = doc.RootElement.GetProperty( "targets" ).EnumerateArray().ToArray();

      Assert.Equal( 19, targets.Length );
      foreach( JsonElement t in targets )
      {
         string index = t.GetProperty( "index" ).GetString()!;
         Assert.NotEmpty( Settings( index ) );
      }

      Assert.Equal( "ef_search=100", string.Join( ", ", Settings( targets.Single( t => t.GetProperty( "name" ).GetString() == "chroma" ).GetProperty( "index" ).GetString() ).Where( p => p.Key == "ef_search" ).Select( p => $"{p.Key}={p.Value}" ) ) );
   }

   /// <summary>
   /// A start that needed more than one attempt keeps its notes when no CPU set is asked for (it used
   /// to call the overload that threw the line away); a clean start with no CPU set still says
   /// nothing, and one with a CPU set returns its read-back line either way.
   /// </summary>
   [Fact]
   public async Task EngineHost_KeepsStartAttemptNotes_WithoutACpuSet()
   {
      var asked = new List<string>();
      Func<string, string?, CancellationToken, Task<string>> fake = ( path, cpuset, ct ) =>
      {
         asked.Add( $"{Path.GetFileName( path )}|{cpuset ?? "none"}" );
         return Task.FromResult( path.Contains( "milvus", StringComparison.Ordinal )
            ? "started milvus.compose.yaml without a benchmark cpuset (its own settings apply): gvb-milvus cpuset none (every CPU); start attempts: attempt 1 exited (134); attempt 2 healthy"
            : cpuset == null ? "started redis.compose.yaml without a benchmark cpuset (its own settings apply): gvb-redis cpuset none (every CPU)"
            : $"started redis.compose.yaml with every container created on CPUs {cpuset}: gvb-redis cpuset {cpuset} (read back from docker inspect)" );
      };
      Type type = COMPILED.Value.GetType( HOST_TYPE )!;
      object host = Activator.CreateInstance( type, fake )!;

      string? retried = await UpAsync( type, host, "/x/milvus.compose.yaml", null );
      string? clean = await UpAsync( type, host, "/x/redis.compose.yaml", null );
      string? pinned = await UpAsync( type, host, "/x/redis.compose.yaml", "2-3,6-7" );

      Assert.Contains( "start attempts: attempt 1 exited (134); attempt 2 healthy", retried );
      Assert.Null( clean );
      Assert.Equal( "started redis.compose.yaml with every container created on CPUs 2-3,6-7: gvb-redis cpuset 2-3,6-7 (read back from docker inspect)", pinned );
      Assert.Equal( new[] { "milvus.compose.yaml|none", "redis.compose.yaml|none", "redis.compose.yaml|2-3,6-7" }, asked );
   }

   /// <summary>
   /// The method note on warm-up no longer says warm-up and timed pass run back to back with the
   /// quiet check right before the warm-up: the session takes its warm-up and settle notes from
   /// the search runner (SearchRunner.DescribeMethod, kept next to the lengths it quotes), which
   /// says the check is made when the warm-up is announced, that every pass has its trial between
   /// its warm-up and its clock, and where the lead is recorded. The doc of the machine control
   /// class says the same.
   /// </summary>
   [Fact]
   public void WarmupTexts_SayWhatTheCodeDoes()
   {
      string session = File.ReadAllText( Path.Combine( RepoRoot(), "src", "GenericVectorBuilder.Bench", "Running", "BenchSession.cs" ) );
      string runner = File.ReadAllText( Path.Combine( RepoRoot(), "src", "GenericVectorBuilder.Bench", "Running", "SearchRunner.cs" ) );
      string control = File.ReadAllText( Path.Combine( RepoRoot(), "src", "GenericVectorBuilder.Bench", "Running", "MachineControl.cs" ) );

      Assert.DoesNotContain( "so warm-up and timed pass run back to back", session + runner );
      Assert.DoesNotContain( "run back to back after the check, as the review", control );
      Assert.Contains( "SearchRunner.DescribeMethod( _options.Warmup", session );
      Assert.Contains( "then a {TRIAL_TIME.TotalSeconds:0} s trial of the same pass", runner );
      Assert.Contains( "(quietCheckLeadSeconds)", runner );
      Assert.Contains( "the quiet check is as old as the warm-up when the", control.Replace( "\n/// ", "\n" ).Replace( "\r", string.Empty ).Replace( "\n", " " ).Replace( "  ", " " ) );
      Assert.Contains( "at least {warmupSearches} searches", runner );
   }

   /// <summary>
   /// The MariaDB durability text names the benchmark's own container and compose file, no longer
   /// says the container it tested against is shared, and says the crash behaviour was not tested on
   /// either container.
   /// </summary>
   [Fact]
   public void MariaDbDurabilityText_NamesTheBenchContainer()
   {
      string text = new MariaDbSink().Durability;

      Assert.Contains( "mariadb-bench.compose.yaml", text );
      Assert.Contains( "gvbbench-mariadb", text );
      Assert.Contains( "mariadb.compose.yaml for the daily gvb-mariadb", text );
      Assert.DoesNotContain( "gvb-mariadb is shared", text );
      Assert.Contains( "was not tested on either container", text );
   }

   /// <summary>
   /// The README says to stop an engine with "down" and without "-v", says where the benchmark does
   /// use "-v" (the clean-up of a container that exited while starting), and lists the benchmark's
   /// own MariaDB container beside the daily one.
   /// </summary>
   [Fact]
   public void Readme_SaysDownWithoutDashV_AndListsTheBenchMariaDb()
   {
      string readme = File.ReadAllText( Path.Combine( RepoRoot(), "deploy", "engines", "README.md" ) );

      Assert.Contains( "Stop it with `down` and without `-v`.", readme );
      Assert.Contains( "the clean-up of a container that exited while it was starting", readme );
      Assert.Contains( "| mariadb-bench |", readme );
      Assert.Contains( "gvbbench-mariadb", readme );
      Assert.Contains( "the benchmark never touches it", readme );
   }

   /// <summary>
   /// Removes this test's folder.
   /// </summary>
   public void Dispose()
   {
      if( Directory.Exists( _root ) )
      {
         Directory.Delete( _root, true );
      }

      GC.SuppressFinalize( this );
   }

   /// <summary>
   /// The pipeline reader puts a time limit on every SQL command it runs (120 s), where it had none (0): a server that stopped answering mid-read
   /// would have held a run at its first step with the heartbeat seeing a live process and no progress. Every SqlCommand in the file gets the limit.
   /// </summary>
   [Fact]
   public void PipelineReader_EverySqlCommandHasATimeLimit()
   {
      string text = File.ReadAllText( Path.Combine( RepoRoot(), "src", "GenericVectorBuilder.Bench", "Data", "PipelineReader.cs" ) );
      Assert.DoesNotContain( "CommandTimeout = 0", text );
      Assert.Contains( "public const int COMMAND_TIMEOUT_SECONDS = 120;", text );
      Assert.Equal( System.Text.RegularExpressions.Regex.Matches( text, @"new SqlCommand\(" ).Count, System.Text.RegularExpressions.Regex.Matches( text, @"CommandTimeout = COMMAND_TIMEOUT_SECONDS" ).Count );
   }

   /// <summary>
   /// The run-start load average is recorded as an information line and never judged: the session no longer asks MachineFacts.IsBusy about it
   /// and the note no longer starts with WARNING (the load average counts the benchmark's own earlier work and the engines it started).
   /// </summary>
   [Fact]
   public void SessionLoadAverageAtStart_IsRecordedAndNeverJudged()
   {
      string session = File.ReadAllText( Path.Combine( RepoRoot(), "src", "GenericVectorBuilder.Bench", "Running", "BenchSession.cs" ) );
      Assert.Contains( "Load average at start: {report.Machine.LoadAverage} on {report.Machine.LogicalCpus} logical CPUs (1/5/15 min).", session );
      Assert.DoesNotContain( "WARNING: load average", session );
      Assert.DoesNotContain( "MachineFacts.IsBusy", session );
   }

   /// <summary>
   /// A real run needs a build that names its commit, and that is checked before machine control changes anything: the stamp is read before
   /// MachineControl.StartAsync, with the commit required for run-all only.
   /// </summary>
   [Fact]
   public void Session_ReadsTheBuildStampBeforeItTouchesTheMachine()
   {
      string session = File.ReadAllText( Path.Combine( RepoRoot(), "src", "GenericVectorBuilder.Bench", "Running", "BenchSession.cs" ) );
      int stamp = session.IndexOf( "RunStamp.Read( requireCommit: _options.Command == \"run-all\" )", StringComparison.Ordinal );
      int machine = session.IndexOf( "await MachineControl.StartAsync(", StringComparison.Ordinal );
      Assert.True( stamp > 0 && machine > stamp, "the build stamp must be read before machine control starts" );
      Assert.Contains( "Enabled = timed && _options.MachineControl && !_options.SettingsOnly", session );
   }

   /// <summary>
   /// The golden questions file's hash is taken from the same bytes the questions are read from, and the queries description (which sessions are
   /// matched on, and which says "embedded now" when the vector cache was cold) is written exactly as before: the hash travels beside the set and
   /// never inside its text.
   /// </summary>
   [Fact]
   public void GoldenQueries_HashComesFromTheSameBytesAndTheDescriptionIsUnchanged()
   {
      string text = File.ReadAllText( Path.Combine( RepoRoot(), "src", "GenericVectorBuilder.Bench", "Data", "GoldenQueries.cs" ) );
      Assert.Contains( "byte[] bytes = await File.ReadAllBytesAsync( questionsFile, ct );", text );
      Assert.Contains( "string hash = Sha256Hex( bytes );", text );
      Assert.Contains( "JsonSerializer.Deserialize<List<GoldenQuestion>>( Decode( bytes ), JSON )", text );
      Assert.Contains( "string description = $\"golden: {questions.Count} labelled questions from {questionsFile}, embedded with {GvbServices.ModelFromFingerprint( fingerprint )} ({source})\";", text );
      Assert.Contains( "string source = $\"cached in {cacheFile}, no embedding calls\";", text );
      Assert.Contains( "source = $\"embedded now: 1 probe + {questions.Count} query calls to the GPU service\";", text );
   }

   /// <summary>
   /// The benchmark project copies the three files the program reads from beside itself (the image pins, the engine facts and the basis exclusions)
   /// to its output root, with its byte order mark and LF endings kept; engine-facts.json lies inside the project and is updated, not included a
   /// second time (a duplicate item is a build error).
   /// </summary>
   [Fact]
   public void Csproj_CopiesTheFilesTheProgramReadsBesideItself()
   {
      byte[] bytes = File.ReadAllBytes( Path.Combine( RepoRoot(), "src", "GenericVectorBuilder.Bench", "GenericVectorBuilder.Bench.csproj" ) );
      Assert.Equal( new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3] );
      Assert.DoesNotContain( (byte)'\r', bytes );
      string text = System.Text.Encoding.UTF8.GetString( bytes );
      Assert.Contains( "<None Include=\"..\\..\\deploy\\bench\\image-pins.json\" Link=\"image-pins.json\" CopyToOutputDirectory=\"PreserveNewest\" />", text );
      Assert.Contains( "<None Update=\"Report\\engine-facts.json\" Link=\"engine-facts.json\" CopyToOutputDirectory=\"PreserveNewest\" />", text );
      Assert.Contains( "<None Include=\"..\\..\\deploy\\bench\\basis-exclusions.json\" Link=\"basis-exclusions.json\" CopyToOutputDirectory=\"PreserveNewest\" Condition=\"Exists('..\\..\\deploy\\bench\\basis-exclusions.json')\" />", text );
      Assert.True( File.Exists( Path.Combine( RepoRoot(), "deploy", "bench", "image-pins.json" ) ), "the pins file the csproj copies must exist or the build fails" );
   }

   /// <summary>
   /// No file this piece wrote or changed has an em-dash (U+2014) in it, in code, comments, strings or tests.
   /// </summary>
   [Fact]
   public void OwnedFiles_HaveNoEmDashes()
   {
      string root = RepoRoot();
      string[] files = Directory.EnumerateFiles( Path.Combine( root, "src", "GenericVectorBuilder.Bench" ), "*.cs", SearchOption.AllDirectories )
         .Where( f => Path.GetRelativePath( root, f ).Split( Path.DirectorySeparatorChar ).All( part => part is not ( "bin" or "obj" ) ) )
         .Concat( Directory.EnumerateFiles( Path.Combine( root, "tests", "GenericVectorBuilder.Engines.Tests", "Bench" ), "*.cs" ) )
         .Concat( Directory.EnumerateFiles( Path.Combine( root, "deploy", "bench" ) ) ).ToArray();
      foreach( string file in files.Where( f => File.ReadAllText( f ).Contains( '\u2014' ) && IsOwnedByThisPiece( Path.GetRelativePath( root, f ) ) ) )
      {
         Assert.Fail( $"{Path.GetRelativePath( root, file )} holds an em-dash" );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Calls the compiled settings reader.
   /// </summary>
   /// <param name="index">Index description.</param>
   /// <returns>The settings, sorted by name.</returns>
   private static SortedDictionary<string, string> Settings( string? index )
   {
      return (SortedDictionary<string, string>)COMPILED.Value.GetType( SETTINGS_TYPE )!.GetMethod( "From" )!.Invoke( null, new object?[] { index } )!;
   }

   /// <summary>
   /// A synthetic target report.
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <param name="withSearch">True to give it a search section.</param>
   /// <returns>The target report.</returns>
   private static object Target( string name, bool withSearch )
   {
      dynamic target = Activator.CreateInstance( COMPILED.Value.GetType( TARGET_TYPE )! )!;
      target.Name = name;
      target.Engine = name + " 1.0";
      target.Index = "test index";
      target.Hosting = "compose";
      if( withSearch )
      {
         target.Search = (dynamic)Activator.CreateInstance( COMPILED.Value.GetType( SEARCH_TYPE )! )!;
      }

      return target;
   }

   /// <summary>
   /// Calls the compiled host's start.
   /// </summary>
   /// <param name="type">The host type.</param>
   /// <param name="host">The host.</param>
   /// <param name="path">Compose file.</param>
   /// <param name="cpuset">CPU set, or null.</param>
   /// <returns>What the host returned.</returns>
   private static async Task<string?> UpAsync( Type type, object host, string path, string? cpuset )
   {
      return await (Task<string?>)type.GetMethod( "UpAsync" )!.Invoke( host, new object?[] { path, cpuset, CancellationToken.None } )!;
   }

   /// <summary>
   /// The repository root: the folder above the test binaries that holds GenericVectorBuilder.slnx.
   /// </summary>
   /// <returns>The root folder.</returns>
   private static string RepoRoot()
   {
      for( var dir = new DirectoryInfo( AppContext.BaseDirectory ); dir != null; dir = dir.Parent )
      {
         if( File.Exists( Path.Combine( dir.FullName, "GenericVectorBuilder.slnx" ) ) )
         {
            return dir.FullName;
         }
      }

      throw new InvalidOperationException( "GenericVectorBuilder.slnx not found above " + AppContext.BaseDirectory );
   }

   /// <summary>
   /// Compiles every file of the benchmark project into an in-memory assembly.
   /// </summary>
   /// <returns>The assembly.</returns>
   private static Assembly Compile()
   {
      string bench = Path.Combine( RepoRoot(), "src", "GenericVectorBuilder.Bench" );
      var parse = new CSharpParseOptions( LanguageVersion.Latest );
      List<SyntaxTree> trees = Directory.EnumerateFiles( bench, "*.cs", SearchOption.AllDirectories )
         .Where( f => Path.GetRelativePath( bench, f ).Split( Path.DirectorySeparatorChar )[0] is not ( "bin" or "obj" ) )
         .Select( f => CSharpSyntaxTree.ParseText( File.ReadAllText( f ), parse, f ) ).ToList();
      trees.Add( CSharpSyntaxTree.ParseText( IMPLICIT_USINGS, parse ) );
      List<string> platform = ( (string)AppContext.GetData( "TRUSTED_PLATFORM_ASSEMBLIES" )! ).Split( Path.PathSeparator ).ToList();
      List<string> extra = BenchOnlyPackages( bench, platform.Select( Path.GetFileName ).ToHashSet( StringComparer.OrdinalIgnoreCase ) );
      AssemblyLoadContext.Default.Resolving += ( context, name ) =>
         extra.FirstOrDefault( p => Path.GetFileNameWithoutExtension( p ).Equals( name.Name, StringComparison.OrdinalIgnoreCase ) ) is string path ? context.LoadFromAssemblyPath( path ) : null;
      var options = new CSharpCompilationOptions( OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable );
      CSharpCompilation compilation = CSharpCompilation.Create( "BenchReportFixUnderTest", trees, platform.Concat( extra ).Select( p => MetadataReference.CreateFromFile( p ) ), options );
      using var image = new MemoryStream();
      Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit( image );
      Assert.True( result.Success, string.Join( Environment.NewLine, result.Diagnostics.Where( d => d.Severity == DiagnosticSeverity.Error ) ) );
      return Assembly.Load( image.ToArray() );
   }

   /// <summary>
   /// Runtime files of the packages the benchmark restored that this test host does not already
   /// have, read from the benchmark's project.assets.json.
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
   /// Whether a repository-relative path is one of the files this piece owns or created (the other pieces' files are checked by their own tests).
   /// </summary>
   /// <param name="relative">Path relative to the repository root.</param>
   /// <returns>True when owned.</returns>
   private static bool IsOwnedByThisPiece( string relative )
   {
      string[] owned =
      {
         "src/GenericVectorBuilder.Bench/Running/TargetRunner.cs", "src/GenericVectorBuilder.Bench/Running/BenchSession.cs", "src/GenericVectorBuilder.Bench/Running/RunStamp.cs",
         "src/GenericVectorBuilder.Bench/Targets/ContainerInspector.cs", "src/GenericVectorBuilder.Bench/Targets/ComposeRunner.cs", "src/GenericVectorBuilder.Bench/Targets/ContainerDescription.cs",
         "src/GenericVectorBuilder.Bench/Targets/EngineSettingsReader.cs", "src/GenericVectorBuilder.Bench/Targets/ImagePins.cs", "src/GenericVectorBuilder.Bench/Targets/DataFolderState.cs",
         "src/GenericVectorBuilder.Bench/Targets/ClickHouseStartState.cs", "src/GenericVectorBuilder.Bench/Report/BenchReport.cs", "src/GenericVectorBuilder.Bench/Report/ResultsWriter.cs",
         "src/GenericVectorBuilder.Bench/Data/PipelineReader.cs", "src/GenericVectorBuilder.Bench/Data/GoldenQueries.cs", "src/GenericVectorBuilder.Bench/Cli/BenchOptions.cs",
         "deploy/bench/make-image-pins.sh", "deploy/bench/image-pins.json",
      };
      string testFolder = "tests/GenericVectorBuilder.Engines.Tests/Bench/";
      string name = Path.GetFileName( relative );
      bool ownedTest = relative.StartsWith( testFolder, StringComparison.Ordinal )
         && ( new[] { "Targets", "Runner", "Compose", "Measurement", "BenchReportFixTests", "EngineSettingsReader", "ImagePins", "DataFolderState", "ClickHouseStartState" }.Any( p => name.StartsWith( p, StringComparison.Ordinal ) ) );
      return owned.Contains( relative.Replace( '\\', '/' ) ) || ownedTest;
   }

   #endregion Private Methods
}
