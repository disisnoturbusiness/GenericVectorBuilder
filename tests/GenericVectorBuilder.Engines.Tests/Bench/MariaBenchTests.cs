using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using GenericVectorBuilder.Engines.Sinks;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The benchmark's "mariadb" target runs in its own container gvbbench-mariadb and never touches
/// the daily gvb-mariadb, checked without starting anything: the compose file against the daily one,
/// the route table, the target the factory builds against a fake Docker that records every name it
/// is asked about, and what run-all starts and stops. The live proof on this box, with the daily
/// container read before and after, is in <see cref="MariaBenchLiveTests"/>.
/// Why this matters: the daily container is long-running and sized for all 8 CPUs. Measuring it meant
/// moving it with "docker update" after it had started (sized for 8 CPUs, then running on 4) and putting
/// it back, so the numbers were not those of an engine created on the engine CPUs like every other, and
/// a long-running server was loaded and changed.
/// Why the sources are compiled here with Roslyn: the test project does not reference the benchmark
/// console project, so the harness compiles its Targets sources (see <see cref="TargetsTests"/>).
/// </summary>
public class MariaBenchTests
{
   #region Data Members

   private static readonly TimeSpan LIMIT = TimeSpan.FromMinutes( 3 );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The benchmark's compose file has the daily file's image and every server setting (the four
   /// server flags, the memory limit, the stop grace period, the password source and the health
   /// check, compared line by line), and differs only in its names, its published port, its data
   /// folder, its CPU set and its restart policy, which says "no" so run-all alone decides when it runs.
   /// </summary>
   [Fact]
   public void ComposeFile_HasTheDailyFilesImageAndServerSettings_AndOnlyItsOwnNames()
   {
      string[] daily = ActiveLines( "mariadb.compose.yaml" );
      string[] bench = ActiveLines( "mariadb-bench.compose.yaml" );
      Assert.Equal( Value( daily, "image" ), Value( bench, "image" ) );
      Assert.Equal( "mariadb:11.8.9", Value( bench, "image" ) );
      Assert.Equal( 4, Block( bench, "command" ).Length );
      foreach( string key in new[] { "command", "env_file", "healthcheck" } )
      {
         Assert.Equal( Block( daily, key ), Block( bench, key ) );
         Assert.NotEmpty( Block( bench, key ) );
      }

      Assert.Equal( Value( daily, "mem_limit" ), Value( bench, "mem_limit" ) );
      Assert.Equal( Value( daily, "stop_grace_period" ), Value( bench, "stop_grace_period" ) );

      Assert.Equal( "gvbbench-mariadb", Value( bench, "container_name" ) );
      Assert.Equal( "gvbbench-mariadb", Value( bench, "name" ) );
      Assert.Equal( new[] { "\"127.0.0.1:13306:3306\"" }, Block( bench, "ports" ) );
      Assert.Equal( new[] { "/home/dan/gvb-data/engines/mariadb-bench:/var/lib/mysql" }, Block( bench, "volumes" ) );
      Assert.Equal( "\"${GVB_ENGINE_CPUS:-2-3,6-7}\"", Value( bench, "cpuset" ) );
      Assert.Equal( "\"no\"", Value( bench, "restart" ) );
      Assert.Equal( "gvb-mariadb", Value( daily, "container_name" ) );
   }

   /// <summary>
   /// Nothing the benchmark's file declares can land on the daily container: not its container name,
   /// its project name (a project name shared with the daily file would let "compose down" remove the
   /// daily container), its published port, its data folder, or a port other than loopback.
   /// </summary>
   [Fact]
   public void ComposeFile_NeverNamesTheDailyContainerItsPortOrItsData()
   {
      string text = string.Join( "\n", ActiveLines( "mariadb-bench.compose.yaml" ) );
      Assert.DoesNotContain( "container_name: gvb-mariadb", text );
      Assert.DoesNotContain( "name: gvb-mariadb", text.Replace( "name: gvbbench-mariadb", string.Empty ) );
      Assert.DoesNotContain( ":3306:3306", text );
      Assert.DoesNotContain( "/engines/mariadb:", text );
      Assert.DoesNotContain( "0.0.0.0", text );
      Assert.All( Block( ActiveLines( "mariadb-bench.compose.yaml" ), "ports" ), p => Assert.StartsWith( "\"127.0.0.1:", p ) );
      Assert.Contains( "/engines/mariadb-bench:", text );
   }

   /// <summary>
   /// The names the benchmark code carries (the target, the container, the compose file, the data folder
   /// and the host port) are the ones the compose file declares:
   /// the container name and project name, the published host port, the volume's folder, and a data
   /// folder named after the compose file, which is how the factory finds the target's disk figure.
   /// </summary>
   [Fact]
   public void Constants_AgreeWithTheComposeFile()
   {
      string[] lines = MariaBenchHarness.Call( "Constants" );
      Assert.DoesNotContain( lines, l => l.StartsWith( "missing|", StringComparison.Ordinal ) );
      string Constant( string name ) => lines.Single( l => l.StartsWith( name + "|", StringComparison.Ordinal ) ).Split( '|' )[1];
      string[] bench = ActiveLines( "mariadb-bench.compose.yaml" );
      Assert.Equal( "mariadb", Constant( "TARGET" ) );
      Assert.Equal( Value( bench, "container_name" ), Constant( "CONTAINER" ) );
      Assert.Equal( "mariadb-bench.compose.yaml", Constant( "COMPOSE_FILE" ) );
      Assert.True( File.Exists( Path.Combine( TargetsHarness.RepoRoot(), "deploy", "engines", Constant( "COMPOSE_FILE" ) ) ) );
      Assert.Equal( Constant( "COMPOSE_FILE" )[..^".compose.yaml".Length], Constant( "DATA_FOLDER" ) );
      Assert.Equal( Constant( "DATA_FOLDER" ), Path.GetFileName( Block( bench, "volumes" ).Single().Split( ':' )[0] ) );
      string port = Block( bench, "ports" ).Single().Trim( '"' ).Split( ':' )[1];
      Assert.Equal( port, Constant( "HOST_PORT" ) );
   }

   /// <summary>
   /// The route table sends the "mariadb" target to gvbbench-mariadb and to nothing else (the daily
   /// container is not in the list the target asks Docker about), and leaves the other engines' routes
   /// where they were.
   /// </summary>
   [Fact]
   public void Route_SendsMariaDbToItsOwnContainerOnly()
   {
      string[] lines = MariaBenchHarness.Call( "Route" );
      Assert.Equal( "mariadb containers|gvbbench-mariadb", lines[0] );
      Assert.Equal( "pgvector containers|gvb-pgvector", lines[1] );
      Assert.Equal( "routes|13", lines[2] );
   }

   /// <summary>
   /// The target the factory builds is hosted by the benchmark's compose file (so run-all starts, pins
   /// and stops that and not the daily file), asks Docker only after it is bound and then only about
   /// gvbbench-mariadb (a fake Docker that also knows the daily container records every name asked
   /// about), connects to the container's own address and port 3306 and not to any published port, and
   /// leaves another engine's compose file as it was.
   /// </summary>
   [Fact]
   public async Task Target_IsHostedByTheBenchmarkFile_AndNeverAsksForTheDailyContainer()
   {
      string[] lines = await MariaBenchHarness.CallAsync( "TargetAsync", LIMIT, TargetsHarness.RepoRoot() );
      string Line( string prefix ) => lines.Single( l => l.StartsWith( prefix, StringComparison.Ordinal ) );
      Assert.Equal( "target|mariadb|hosting compose|compose mariadb-bench.compose.yaml|exists True|has binding True", Line( "target|" ) );
      Assert.Equal( "before binding|asked none", Line( "before binding|" ) );
      Assert.Equal( "after binding|asked gvbbench-mariadb", Line( "after binding|" ) );
      Assert.Equal( "connection|gvbbench-mariadb (8cefe0e722ed) on network gvbbench-mariadb_default at 172.24.0.2:3306, not through docker-proxy 127.0.0.1:13306", Line( "connection|" ) );
      Assert.Equal( "endpoints|172.24.0.2:3306", Line( "endpoints|" ) );
      Assert.Equal( "container route|gvbbench-mariadb", Line( "container route|" ) );
      Assert.Equal( "pgvector target|compose pgvector.compose.yaml", Line( "pgvector target|" ) );
   }

   /// <summary>
   /// A container of the benchmark's name that publishes only host port 3306 (the daily container's
   /// shape) is refused with the port the route needs named in the message, so a mix-up of the two
   /// containers stops the target and never measures the wrong one.
   /// </summary>
   [Fact]
   public async Task Target_RefusesAContainerShapedLikeTheDailyOne()
   {
      string outcome = await MariaBenchHarness.CallAsync( "DailyShapedContainerAsync", LIMIT, TargetsHarness.RepoRoot() );
      Assert.StartsWith( "InvalidOperationException: Container gvbbench-mariadb does not publish host port 13306", outcome );
      Assert.Contains( "published: 127.0.0.1:3306 to 3306/tcp", outcome );
   }

   /// <summary>
   /// run-all starts the benchmark's compose file on the engine CPUs and stops it afterwards, also while
   /// the daily container's compose file is running (the old route found the daily container running,
   /// used it, and left it running); and a benchmark container that was up before the run is left up.
   /// The daily file is only ever asked about, never started or stopped.
   /// </summary>
   [Fact]
   public async Task RunAll_StartsAndStopsTheBenchmarkContainer_AndNeverTheDailyFile()
   {
      string[] lines = await MariaBenchHarness.CallAsync( "LifecycleAsync", LIMIT, TargetsHarness.RepoRoot() );
      string[] down = lines.Where( l => l.StartsWith( "down before|", StringComparison.Ordinal ) ).ToArray();
      Assert.Equal( new[]
      {
         "down before|running?|mariadb-bench.compose.yaml|False",
         "down before|running?|mariadb-bench.compose.yaml|False",
         "down before|up|mariadb-bench.compose.yaml|cpuset 2-3,6-7",
         "down before|down|mariadb-bench.compose.yaml|",
         "down before|notes|Started by run-all for this measurement with every container created on CPUs 2-3,6-7 (fake start on 2-3,6-7); stopped afterwards. / " + EngineFilesNote( "mariadb-bench.compose.yaml", "This run created the engine from these files." ),
      }, down );

      string[] up = lines.Where( l => l.StartsWith( "already up|", StringComparison.Ordinal ) ).ToArray();
      Assert.Equal( new[]
      {
         "already up|running?|mariadb-bench.compose.yaml|True",
         "already up|running?|mariadb-bench.compose.yaml|True",
         "already up|notes|Engine was already running before the run; left running. / " + EngineFilesNote( "mariadb-bench.compose.yaml", "The engine was already running at its turn, so it may have been created from other files than these." ),
      }, up );
      Assert.DoesNotContain( lines, l => l.Contains( "|mariadb.compose.yaml", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// MariaDbSink's options point the sink wherever Host and Port say, which is all the benchmark needs
   /// to reach another container (the route sets them from Docker's own facts): a sink given a free local
   /// port connects there, not to the default 127.0.0.1:3306 of the daily container.
   /// </summary>
   [Fact]
   public async Task SinkOptions_PointTheSinkAtAnotherHostAndPort()
   {
      var defaults = new MariaDbSinkOptions();
      Assert.Equal( ( "127.0.0.1", 3306 ), ( defaults.Host, defaults.Port ) );
      var listener = new TcpListener( IPAddress.Loopback, 0 );
      listener.Start();
      try
      {
         int port = ( (IPEndPoint)listener.LocalEndpoint ).Port;
         Assert.NotEqual( 3306, port );
         using var sink = new MariaDbSink( new MariaDbSinkOptions { Host = "127.0.0.1", Port = port, Password = "x" } );
         using var limit = new CancellationTokenSource( TimeSpan.FromSeconds( 20 ) );
         Task<TcpClient> accepted = listener.AcceptTcpClientAsync( limit.Token ).AsTask();
         Task attempt = sink.GetIndexStateAsync( "gvbbench_pointing", limit.Token );
         using TcpClient client = await accepted;
         client.Close();
         await Assert.ThrowsAnyAsync<Exception>( () => attempt );
      }
      finally
      {
         listener.Stop();
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The engine-files note the lifecycle adds to a target's notes for a compose file of deploy/engines that
   /// mounts no config file: the file's name and the first 12 hex digits of its SHA-256, computed here
   /// from the file itself, then the sentence that says who created the engine.
   /// </summary>
   /// <param name="composeFile">File name under deploy/engines.</param>
   /// <param name="origin">The closing sentence.</param>
   /// <returns>The note.</returns>
   private static string EngineFilesNote( string composeFile, string origin )
   {
      string path = Path.Combine( TargetsHarness.RepoRoot(), "deploy", "engines", composeFile );
      string hash = Convert.ToHexString( SHA256.HashData( File.ReadAllBytes( path ) ) )[..12].ToLowerInvariant();
      return $"Engine files (SHA-256, first 12 hex digits): [{composeFile} {hash}]. {origin}";
   }

   /// <summary>
   /// The lines of a compose file under deploy/engines without blank lines and comments.
   /// </summary>
   /// <param name="file">File name.</param>
   /// <returns>The lines, indentation kept.</returns>
   private static string[] ActiveLines( string file )
   {
      string path = Path.Combine( TargetsHarness.RepoRoot(), "deploy", "engines", file );
      Assert.True( File.Exists( path ), $"{path} does not exist" );
      return File.ReadAllLines( path ).Where( l => l.Trim().Length > 0 && !l.TrimStart().StartsWith( '#' ) ).ToArray();
   }

   /// <summary>
   /// The value of a "key: value" line (any indentation).
   /// </summary>
   /// <param name="lines">Active lines.</param>
   /// <param name="key">Key.</param>
   /// <returns>The value text.</returns>
   private static string Value( string[] lines, string key )
   {
      return lines.Single( l => l.TrimStart().StartsWith( key + ": ", StringComparison.Ordinal ) ).Trim()[( key.Length + 2 )..];
   }

   /// <summary>
   /// The lines nested under a "key:" line, indentation and the list dash removed.
   /// </summary>
   /// <param name="lines">Active lines.</param>
   /// <param name="key">Key.</param>
   /// <returns>The nested lines, or none when the key is absent.</returns>
   private static string[] Block( string[] lines, string key )
   {
      int at = Array.FindIndex( lines, l => l.Trim() == key + ":" );
      if( at < 0 )
      {
         return Array.Empty<string>();
      }

      int indent = lines[at].Length - lines[at].TrimStart().Length;
      return lines.Skip( at + 1 ).TakeWhile( l => l.Length - l.TrimStart().Length > indent ).Select( l => l.Trim() ).Select( l => l.StartsWith( "- ", StringComparison.Ordinal ) ? l[2..] : l ).ToArray();
   }

   #endregion Private Methods
}

/// <summary>
/// Compiles the benchmark's Targets sources, its engine lifecycle and the MariaBench scenarios into an
/// in-memory assembly and calls the scenarios by name.
/// </summary>
internal static class MariaBenchHarness
{
   #region Data Members

   private const string SCENARIOS_TYPE = "GenericVectorBuilder.Bench.UnderTest.MariaBenchScenarios";
   private const string IMPLICIT_USINGS = "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; "
      + "global using System.Net.Http; global using System.Threading; global using System.Threading.Tasks;";
   private static readonly Lazy<Assembly> COMPILED = new( Compile );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Calls a synchronous scenario method.
   /// </summary>
   /// <param name="method">Method name.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>The result, usable as dynamic or cast.</returns>
   public static dynamic Call( string method, params object?[] arguments )
   {
      return COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( method )!.Invoke( null, arguments )!;
   }

   /// <summary>
   /// Calls an asynchronous scenario method and waits for it, within a limit.
   /// </summary>
   /// <param name="method">Method name.</param>
   /// <param name="limit">Longest wait.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>The task's result.</returns>
   public static async Task<dynamic> CallAsync( string method, TimeSpan limit, params object?[] arguments )
   {
      var task = (Task)COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( method )!.Invoke( null, arguments )!;
      Task finished = await Task.WhenAny( task, Task.Delay( limit ) );
      Assert.True( finished == task, $"{method} did not finish within {limit.TotalMinutes:0.#} minutes" );
      await task;
      return task.GetType().GetProperty( "Result" )!.GetValue( task )!;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Compiles the sources into an in-memory assembly.
   /// </summary>
   /// <returns>The assembly.</returns>
   private static Assembly Compile()
   {
      string root = TargetsHarness.RepoRoot();
      string bench = Path.Combine( root, "src", "GenericVectorBuilder.Bench" );
      var parse = new CSharpParseOptions( LanguageVersion.Latest );
      List<SyntaxTree> trees = Directory.EnumerateFiles( Path.Combine( bench, "Targets" ), "*.cs" ).Select( f => CSharpSyntaxTree.ParseText( File.ReadAllText( f ), parse, f ) ).ToList();
      foreach( string file in new[] { "EngineLifecycle.cs", "EngineHost.cs" } )
      {
         string path = Path.Combine( bench, "Running", file );
         trees.Add( CSharpSyntaxTree.ParseText( File.ReadAllText( path ), parse, path ) );
      }

      string scenarios = Path.Combine( root, "tests", "GenericVectorBuilder.Engines.Tests", "Bench", "MariaBenchScenarios.cs" );
      trees.Add( CSharpSyntaxTree.ParseText( File.ReadAllText( scenarios ), parse.WithPreprocessorSymbols( "BENCH_UNDER_TEST" ), scenarios ) );
      trees.Add( CSharpSyntaxTree.ParseText( IMPLICIT_USINGS, parse ) );
      List<string> platform = ( (string)AppContext.GetData( "TRUSTED_PLATFORM_ASSEMBLIES" )! ).Split( Path.PathSeparator ).ToList();
      List<string> extra = BenchOnlyPackages( bench, platform.Select( Path.GetFileName ).ToHashSet( StringComparer.OrdinalIgnoreCase ) );
      AssemblyLoadContext.Default.Resolving += ( context, name ) =>
         extra.FirstOrDefault( p => Path.GetFileNameWithoutExtension( p ).Equals( name.Name, StringComparison.OrdinalIgnoreCase ) ) is string path ? context.LoadFromAssemblyPath( path ) : null;
      var options = new CSharpCompilationOptions( OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable );
      CSharpCompilation compilation = CSharpCompilation.Create( "MariaBenchUnderTest", trees, platform.Concat( extra ).Select( p => MetadataReference.CreateFromFile( p ) ), options );
      using var image = new MemoryStream();
      Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit( image );
      Assert.True( result.Success, string.Join( Environment.NewLine, result.Diagnostics.Where( d => d.Severity == DiagnosticSeverity.Error ) ) );
      return Assembly.Load( image.ToArray() );
   }

   /// <summary>
   /// Runtime files of the packages the benchmark restored that this test host does not already have,
   /// read from the benchmark's project.assets.json.
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

   #endregion Private Methods
}
