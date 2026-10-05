using System.Reflection;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// What the engine lifecycle writes into a target's notes about the start of its engine: a start that
/// needed more than one attempt (Milvus exits while it starts and is started again) keeps every attempt
/// in the note in all three cases (created on a CPU set, started unrestricted, started again after it
/// was down at its turn), the v6 review having found the two unrestricted notes dropping it; and the
/// engine-files note names the compose file and every read-only config file or folder it mounts with a
/// short SHA-256 of each, so a changed configuration file shows in the results.
/// Why compiled with Roslyn against two small stand-ins: the benchmark is a console project this test
/// project does not reference, and the real EngineLifecycle.cs and EngineHost.cs are what must be tested;
/// the stand-ins replace only BenchTarget (a name and a compose path) and ComposeRunner (the calls
/// the real host makes), neither of which these notes depend on.
/// </summary>
public sealed class ConsolidateEngineStartNoteTests : IDisposable
{
   #region Data Members

   private const string SCENARIOS_TYPE = "GenericVectorBuilder.Bench.Running.StartNoteScenarios";
   private const string ATTEMPTS = "started milvus.compose.yaml without a benchmark cpuset (its own settings apply): gvb-milvus cpuset none (every CPU); start attempts: attempt 1 exited (134); attempt 2 healthy";

   private const string STAND_INS = """
      namespace GenericVectorBuilder.Bench.Targets
      {
         public sealed class BenchTarget
         {
            public string Name { get; set; } = "t";
            public string? ComposePath { get; set; }
         }

         public static class ComposeRunner
         {
            public static Task<bool> IsRunningAsync( string composePath, CancellationToken ct ) => Task.FromResult( false );
            public static Task<string> UpAsync( string composePath, string? cpuset, CancellationToken ct ) => Task.FromResult( string.Empty );
            public static Task<string?> DownAsync( string composePath, CancellationToken ct ) => Task.FromResult<string?>( null );
         }
      }

      namespace GenericVectorBuilder.Bench.Running
      {
         using GenericVectorBuilder.Bench.Targets;

         public static class StartNoteScenarios
         {
            private sealed class Host : IEngineHost
            {
               private readonly Queue<bool> _running;
               private readonly string? _line;

               public Host( bool[] running, string? line ) { _running = new Queue<bool>( running ); _line = line; }

               public Task<bool> IsRunningAsync( string composePath, CancellationToken ct ) => Task.FromResult( _running.Count > 1 ? _running.Dequeue() : _running.Peek() );
               public Task<string?> UpAsync( string composePath, string? cpuset, CancellationToken ct ) => Task.FromResult( _line );
               public Task<string?> DownAsync( string composePath, CancellationToken ct ) => Task.FromResult<string?>( null );
            }

            /// <summary>Runs one start and returns the target's notes.</summary>
            public static async Task<string[]> Start( bool[] running, string? cpuset, string? line, string composePath )
            {
               var lifecycle = new EngineLifecycle( new Host( running, line ), true, _ => { }, cpuset );
               var target = new BenchTarget { ComposePath = composePath };
               var notes = new List<string>();
               await lifecycle.SnapshotAsync( new[] { composePath }, CancellationToken.None );
               await lifecycle.EnsureRunningAsync( target, notes, CancellationToken.None );
               string? files = lifecycle.FilesNote( target );
               if( files != null )
               {
                  notes.Add( files );
               }

               return notes.ToArray();
            }

            /// <summary>The engine-files note of a target that has this compose path (nothing started).</summary>
            public static string? Files( string? composePath )
            {
               var lifecycle = new EngineLifecycle( new Host( new[] { false }, null ), true, _ => { } );
               return lifecycle.FilesNote( new BenchTarget { ComposePath = composePath } );
            }
         }
      }
      """;

   private static readonly Lazy<Assembly> COMPILED = new( Compile );

   private readonly string _root;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a private folder for the compose files and config files this test writes, under the test
   /// binaries, removed again in <see cref="Dispose"/>.
   /// </summary>
   public ConsolidateEngineStartNoteTests()
   {
      _root = Path.Combine( AppContext.BaseDirectory, "start-note-tests", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( _root );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// An engine started on a CPU set keeps the host's read-back line with every attempt in its note (as
   /// before the fix).
   /// </summary>
   [Fact]
   public async Task StartOnACpuSet_KeepsEveryAttempt()
   {
      string[] notes = await Start( new[] { false }, "2-3,6-7", ATTEMPTS );

      Assert.Single( notes );
      Assert.StartsWith( "Started by run-all for this measurement with every container created on CPUs 2-3,6-7 (started milvus.compose.yaml", notes[0] );
      Assert.Contains( "start attempts: attempt 1 exited (134); attempt 2 healthy", notes[0] );
   }

   /// <summary>
   /// An engine started with no CPU set keeps the attempts too: the note says it was started unrestricted
   /// and then what the host reported, so a Milvus that needed a second start leaves a trace. A clean
   /// start with no CPU set says nothing more than before.
   /// </summary>
   [Fact]
   public async Task UnrestrictedStart_KeepsEveryAttempt_AndACleanOneSaysNothingMore()
   {
      string[] retried = await Start( new[] { false }, null, ATTEMPTS );
      string[] clean = await Start( new[] { false }, null, null );

      Assert.Equal( "Started by run-all for this measurement, unrestricted (machine control off or the CPUs not split), and stopped afterwards. What the host reported about the start: " + ATTEMPTS + ".", retried[0] );
      Assert.Contains( "attempt 1 exited (134); attempt 2 healthy", retried[0] );
      Assert.Equal( "Started by run-all for this measurement, unrestricted (machine control off or the CPUs not split), and stopped afterwards.", clean[0] );
   }

   /// <summary>
   /// An engine that was running before the run and was down at its turn is started as it was, and its note
   /// keeps the attempts as well; without them it reads as before.
   /// </summary>
   [Fact]
   public async Task EngineDownAtItsTurn_KeepsEveryAttempt()
   {
      string[] retried = await Start( new[] { true, false }, "2-3,6-7", ATTEMPTS );
      string[] clean = await Start( new[] { true, false }, "2-3,6-7", null );

      Assert.Equal( "Engine was running before the run but was down at its turn; run-all started it unrestricted (as it was) and leaves it running, as it found it. What the host reported about the start: " + ATTEMPTS + ".", retried[0] );
      Assert.Equal( "Engine was running before the run but was down at its turn; run-all started it unrestricted (as it was) and leaves it running, as it found it.", clean[0] );
   }

   /// <summary>
   /// The engine-files note lists the compose file and every file under a read-only relative mount with the
   /// first 12 hex digits of its SHA-256 (checked here against an independent computation), sorted by path;
   /// data folders, environment files and read-write mounts are not listed; and the sentence after the list
   /// says this run created the engine.
   /// </summary>
   [Fact]
   public async Task FilesNote_ListsTheComposeFileAndItsReadOnlyMounts_WithTheirHashes()
   {
      string compose = WriteEngine( out string[] files );

      string[] notes = await Start( new[] { false }, "2-3,6-7", "read back" , compose );

      string note = notes[1];
      string expected = string.Join( "; ", new[]
      {
         "cfg/a.xml " + Hash( files[0] ),
         "cfg/sub/b.xml " + Hash( files[1] ),
         "engine.compose.yaml " + Hash( compose ),
         "solo.conf " + Hash( files[2] ),
      } );
      Assert.Equal( $"Engine files (SHA-256, first 12 hex digits): [{expected}]. This run created the engine from these files.", note );
      Assert.DoesNotContain( "big.bin", note );
      Assert.DoesNotContain( "secrets", note );
   }

   /// <summary>
   /// A changed config file changes the note, and an engine that was already running says it may have been
   /// created from other files; a missing mount is listed as missing, not skipped.
   /// </summary>
   [Fact]
   public async Task FilesNote_ChangesWithAConfigFile_AndSaysWhenTheEngineWasAlreadyRunning()
   {
      string compose = WriteEngine( out string[] files );
      string before = ( await Start( new[] { true }, null, null, compose ) )[^1];
      File.WriteAllText( files[0], "<clickhouse><logger><level>warning</level></logger></clickhouse>" );
      string after = ( await Start( new[] { true }, null, null, compose ) )[^1];
      File.AppendAllText( compose, "      - ./gone.conf:/etc/gone.conf:ro\n" );
      string withMissing = ( await Start( new[] { true }, null, null, compose ) )[^1];

      Assert.NotEqual( before.Split( ']' )[0], after.Split( ']' )[0] );
      Assert.EndsWith( "The engine was already running at its turn, so it may have been created from other files than these.", after );
      Assert.Contains( "gone.conf missing", withMissing );
   }

   /// <summary>
   /// A target with no compose file (a native or embedded engine), or a compose path that does not exist,
   /// gets no engine-files note: the host reports a missing file itself when it tries to start it.
   /// </summary>
   [Fact]
   public void FilesNote_IsNull_WithoutAComposeFileOnDisk()
   {
      Assert.Null( Scenario<string?>( "Files", new object?[] { null } ) );
      Assert.Null( Scenario<string?>( "Files", new object?[] { Path.Combine( _root, "nope.compose.yaml" ) } ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Runs one start through the compiled lifecycle.
   /// </summary>
   /// <param name="running">What the host answers to each "is it running" question (the last answer repeats).</param>
   /// <param name="cpuset">The CPU set run-all was given, or null.</param>
   /// <param name="line">What the host reports about the start, or null.</param>
   /// <returns>The target's notes.</returns>
   private Task<string[]> Start( bool[] running, string? cpuset, string? line )
   {
      return Start( running, cpuset, line, Path.Combine( _root, "no-such.compose.yaml" ) );
   }

   /// <summary>
   /// Runs one start through the compiled lifecycle for a compose path.
   /// </summary>
   /// <param name="running">What the host answers to each "is it running" question (the last answer repeats).</param>
   /// <param name="cpuset">The CPU set run-all was given, or null.</param>
   /// <param name="line">What the host reports about the start, or null.</param>
   /// <param name="compose">The compose path.</param>
   /// <returns>The target's notes (with the engine-files note last when the compose file exists).</returns>
   private async Task<string[]> Start( bool[] running, string? cpuset, string? line, string compose )
   {
      MethodInfo start = COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( "Start" )!;
      var task = (Task<string[]>)start.Invoke( null, new object?[] { running, cpuset, line, compose } )!;
      Task finished = await Task.WhenAny( task, Task.Delay( TimeSpan.FromSeconds( 30 ) ) );
      Assert.True( finished == task, "the scenario did not finish within 30 s" );
      return await task;
   }

   /// <summary>
   /// Calls a synchronous method of the compiled scenarios.
   /// </summary>
   /// <typeparam name="T">Result type.</typeparam>
   /// <param name="method">Method name.</param>
   /// <param name="arguments">Arguments.</param>
   /// <returns>The result.</returns>
   private static T Scenario<T>( string method, object?[] arguments )
   {
      return (T)COMPILED.Value.GetType( SCENARIOS_TYPE )!.GetMethod( method )!.Invoke( null, arguments )!;
   }

   /// <summary>
   /// Writes a compose file that mounts two config files in a folder, one config file on its own, a data
   /// folder (read-write) and an env file, with the config files' contents.
   /// </summary>
   /// <param name="files">Receives the paths of cfg/a.xml, cfg/sub/b.xml and solo.conf.</param>
   /// <returns>The compose file's path.</returns>
   private string WriteEngine( out string[] files )
   {
      Directory.CreateDirectory( Path.Combine( _root, "cfg", "sub" ) );
      Directory.CreateDirectory( Path.Combine( _root, "data" ) );
      files = new[] { Path.Combine( _root, "cfg", "a.xml" ), Path.Combine( _root, "cfg", "sub", "b.xml" ), Path.Combine( _root, "solo.conf" ) };
      File.WriteAllText( files[0], "<a/>" );
      File.WriteAllText( files[1], "<b/>" );
      File.WriteAllText( files[2], "solo=1" );
      File.WriteAllText( Path.Combine( _root, "data", "big.bin" ), "data that must not be listed" );
      string compose = Path.Combine( _root, "engine.compose.yaml" );
      File.WriteAllText( compose, string.Join( "\n", "name: gvb-test", "services:", "  e:", "    env_file:", "      - /home/dan/gvb-data/engines/secrets.env", "    volumes:",
         "      - ./data:/var/lib/e", "      - ./cfg:/etc/e/config.d:ro", "      - \"./solo.conf:/etc/e/solo.conf:ro\"", string.Empty ) );
      return compose;
   }

   /// <summary>
   /// The first 12 hex digits of a file's SHA-256, computed here independently of the code under test.
   /// </summary>
   /// <param name="path">The file.</param>
   /// <returns>The digits, lower case.</returns>
   private static string Hash( string path )
   {
      return Convert.ToHexString( SHA256.HashData( File.ReadAllBytes( path ) ) )[..12].ToLowerInvariant();
   }

   /// <summary>
   /// Compiles EngineLifecycle.cs and EngineHost.cs from the repository with the stand-ins.
   /// </summary>
   /// <returns>The assembly.</returns>
   private static Assembly Compile()
   {
      string bench = Path.Combine( RepoRoot(), "src", "GenericVectorBuilder.Bench", "Running" );
      var parse = new CSharpParseOptions( LanguageVersion.Latest );
      var trees = new List<SyntaxTree>
      {
         CSharpSyntaxTree.ParseText( File.ReadAllText( Path.Combine( bench, "EngineLifecycle.cs" ) ), parse, "EngineLifecycle.cs" ),
         CSharpSyntaxTree.ParseText( File.ReadAllText( Path.Combine( bench, "EngineHost.cs" ) ), parse, "EngineHost.cs" ),
         CSharpSyntaxTree.ParseText( STAND_INS, parse, "StandIns.cs" ),
         CSharpSyntaxTree.ParseText( "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; global using System.Threading; global using System.Threading.Tasks;", parse ),
      };
      IEnumerable<MetadataReference> references = ( (string)AppContext.GetData( "TRUSTED_PLATFORM_ASSEMBLIES" )! ).Split( Path.PathSeparator ).Select( p => MetadataReference.CreateFromFile( p ) );
      var options = new CSharpCompilationOptions( OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable );
      CSharpCompilation compilation = CSharpCompilation.Create( "StartNoteUnderTest", trees, references, options );
      using var image = new MemoryStream();
      Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit( image );
      Assert.True( result.Success, string.Join( Environment.NewLine, result.Diagnostics.Where( d => d.Severity == DiagnosticSeverity.Error ) ) );
      return Assembly.Load( image.ToArray() );
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

   #region IDisposable

   /// <summary>
   /// Removes this test's folder.
   /// </summary>
   public void Dispose()
   {
      try
      {
         Directory.Delete( _root, recursive: true );
      }
      catch( IOException )
      {
         // A leftover folder under the test binaries is harmless; the next build's clean removes it.
      }
   }

   #endregion IDisposable
}
