// Compiled only by the Targets tests, together with the benchmark's own Targets sources and the other
// Targets scenario files (the test project does not reference the benchmark console project).
// BENCH_UNDER_TEST is defined only in that compilation, so the test project itself sees an empty file.
#if BENCH_UNDER_TEST
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using GenericVectorBuilder.Bench.Targets;
using GenericVectorBuilder.Core.Configuration;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Bench.UnderTest;

/// <summary>
/// Runs the code behind the benchmark's own SQL Server and Qdrant containers (gvb-mssql, gvb-qdrant)
/// and the cpuset every engine container is started with, and hands back plain lines for the tests.
/// The pure part feeds it texts; the live part asks Docker compose to read the real compose files with
/// the override the benchmark writes, starts the two containers when they are not running (with a CPU
/// set that differs from the compose file's default, so the override is what is proven) and stops them
/// again, loads 524 random vectors into each of the four built-in targets, and samples every SQL Server
/// thread from /proc while eight searchers run.
/// What is created: tables and collections named gvb_gvbbench_pin* in the containers' GvbBench and
/// GvbBenchDiskAnn databases and Qdrant, all dropped at the end. Environment values of a container are
/// never read into a line (several are passwords); only names are.
/// </summary>
public static class TargetsPinnedScenarios
{
   #region Data Members

   /// <summary>
   /// The CPU set the live scenario starts the containers with when it starts them itself: not the compose
   /// files' default (2-3,6-7), so a container that comes up on it can only have got it from the override.
   /// Physical cores 1 and 2 of linus7795 with their hyperthreads.
   /// </summary>
   public const string TEST_CPUSET = "1-2,5-6";

   private const int ROWS = 524;
   private const int DIMENSION = 1024;
   private const int TOP = 10;
   private static readonly TimeSpan SAMPLE_EVERY = TimeSpan.FromMilliseconds( 200 );

   #endregion Data Members

   #region Public Methods

   /// <summary>Reads CPU lists with <see cref="CpuSetText"/>.</summary>
   /// <param name="texts">Texts to read.</param>
   /// <returns>"text|valid|cpus|count" per text, cpus as a plain list or "-".</returns>
   public static string[] CpuSets( string[] texts )
   {
      return texts.Select( t => $"{t}|{CpuSetText.IsValid( t )}|{( CpuSetText.IsValid( t ) ? string.Join( ",", CpuSetText.Parse( t ) ) : "-" )}|{CpuSetText.Count( t )?.ToString() ?? "-"}" ).ToArray();
   }

   /// <summary>Compares two CPU lists with <see cref="CpuSetText.Same"/>.</summary>
   /// <param name="a">One list.</param>
   /// <param name="b">The other.</param>
   /// <returns>True when they name the same CPUs.</returns>
   public static bool SameCpus( string? a, string? b )
   {
      return CpuSetText.Same( a, b );
   }

   /// <summary>The compose override for services and a cpuset, or the refusal.</summary>
   /// <param name="services">Service names.</param>
   /// <param name="cpuset">CPU list.</param>
   /// <returns>The YAML, or "error|Type: message".</returns>
   public static string Override( string[] services, string cpuset )
   {
      try
      {
         return ComposeRunner.OverrideYaml( services, cpuset );
      }
      catch( ArgumentException ex )
      {
         return $"error|{ex.GetType().Name}: {ex.Message}";
      }
   }

   /// <summary>The image a compose file under deploy/engines names, read with <see cref="ComposeRunner.ImageOf"/>.</summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <param name="file">File name, e.g. "mssql.compose.yaml".</param>
   /// <returns>The image reference.</returns>
   public static string ImageOf( string repoRoot, string file )
   {
      return ComposeRunner.ImageOf( Path.Combine( repoRoot, "deploy", "engines", file ) );
   }

   /// <summary>The facts the benchmark reads from docker inspect beyond the address: cpuset, image, environment names.</summary>
   /// <param name="json">docker inspect text.</param>
   /// <returns>"cpuset|...", "image|...", "env|A,B".</returns>
   public static string[] InspectExtras( string json )
   {
      ContainerFacts facts = ContainerFacts.Parse( json );
      return new[] { $"cpuset|{facts.Cpuset}", $"image|{facts.Image}", $"env|{string.Join( ",", facts.EnvNames )}" };
   }

   /// <summary>The SQL Server container's engine text from given facts.</summary>
   /// <param name="cpuCount">cpu_count.</param>
   /// <param name="schedulers">Online schedulers.</param>
   /// <param name="cpuset">Container cpuset, or null.</param>
   /// <returns>The text.</returns>
   public static string SqlEngineText( int cpuCount, int schedulers, string? cpuset )
   {
      return SqlServerContainer.DescribeEngine( "Microsoft SQL Server 2025 (RTM-CU9) (KB5122048) - 17.0.5005.3 (X64) \n\tAug 27 2026", "Enterprise Developer Edition (64-bit)", cpuCount, schedulers, "AUTO", cpuset, "mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-24.04@sha256:x" );
   }

   /// <summary>The Qdrant container's engine text from given facts.</summary>
   /// <param name="searchThreads">Search threads, or null.</param>
   /// <param name="cpuset">Container cpuset, or null.</param>
   /// <returns>The text.</returns>
   public static string QdrantEngineText( int? searchThreads, string? cpuset )
   {
      return QdrantContainer.DescribeEngine( "1.17.0", searchThreads, cpuset, "qdrant/qdrant:v1.17.0@sha256:x" );
   }

   /// <summary>Whether a /proc/PID/cgroup text is a container's.</summary>
   /// <param name="cgroup">The text.</param>
   /// <returns>True for a container.</returns>
   public static bool IsContainer( string cgroup )
   {
      return HostProcess.IsContainer( cgroup );
   }

   /// <summary>The SQL Server durability sentence with an absent container mssql.conf.</summary>
   /// <returns>The sentence.</returns>
   public static string SqlDurabilityWithoutConf()
   {
      var model = new[] { new DatabaseDurability( "model", "FULL", "DISABLED", "CHECKSUM" ) };
      return SqlDurability.Describe( model, Array.Empty<int>(), new ParsedConfig( new Dictionary<string, string>(), 0 ), "mssql.conf of container gvb-mssql (/var/opt/mssql/mssql.conf) does not exist, so it" );
   }

   /// <summary>
   /// The factory's names and what it refuses before its server reads, with Docker answering that no
   /// container exists (nothing is contacted).
   /// </summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <returns>One line per fact.</returns>
   public static async Task<string[]> FactoryNamesAsync( string repoRoot )
   {
      using var factory = new TargetFactory( new GvbSettings(), repoRoot, null, _ => { }, new NoContainers() );
      var lines = new List<string>
      {
         "names|" + string.Join( ",", factory.Names.Take( 4 ) ),
         $"names include native|{factory.Names.Contains( TargetFactory.SQL_NATIVE ) || factory.Names.Contains( TargetFactory.QDRANT_NATIVE )}",
         "comparison|" + string.Join( ",", factory.ComparisonNames ),
      };
      foreach( string name in factory.ComparisonNames )
      {
         lines.Add( $"{name} before InitializeAsync|" + await Describe( () => Task.Run( () => factory.Create( name ) ) ) );
      }

      lines.Add( "unknown|" + await Describe( () => Task.Run( () => factory.Create( "nosuch" ) ) ) );
      return lines.ToArray();
   }

   /// <summary>
   /// Reads every engine compose file under deploy/engines through Docker compose with the override the
   /// benchmark writes for a start, and reports the cpuset compose would give each service. Starts
   /// nothing. Only service names and cpusets are taken from compose's output.
   /// </summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <param name="cpuset">CPU list to write into the override.</param>
   /// <returns>"file|service|cpuset" per service, or "file|ERROR|message".</returns>
   public static async Task<string[]> ComposeOverrideAsync( string repoRoot, string cpuset )
   {
      var lines = new List<string>();
      foreach( string file in Directory.EnumerateFiles( Path.Combine( repoRoot, "deploy", "engines" ), "*.compose.yaml" ).OrderBy( f => f, StringComparer.Ordinal ) )
      {
         string name = Path.GetFileName( file );
         ShellResult services = await Shell.RunAsync( "sudo", new[] { "-n", "docker", "compose", "-f", file, "config", "--services" }, TimeSpan.FromMinutes( 1 ), CancellationToken.None );
         string[] names = services.Output.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
         string overridePath = Path.Combine( Path.GetTempPath(), $"gvb-cpuset-test-{Guid.NewGuid():N}.yaml" );
         try
         {
            await File.WriteAllTextAsync( overridePath, ComposeRunner.OverrideYaml( names, cpuset ) );
            ShellResult config = await Shell.RunAsync( "sudo", new[] { "-n", "docker", "compose", "-f", file, "-f", overridePath, "config", "--format", "json" }, TimeSpan.FromMinutes( 1 ), CancellationToken.None );
            if( config.ExitCode != 0 )
            {
               lines.Add( $"{name}|ERROR|{config.Error.Trim()}" );
               continue;
            }

            using JsonDocument document = JsonDocument.Parse( config.Output );
            foreach( JsonProperty service in document.RootElement.GetProperty( "services" ).EnumerateObject() )
            {
               string set = service.Value.TryGetProperty( "cpuset", out JsonElement value ) ? value.GetString() ?? "null" : "none";
               lines.Add( $"{name}|{service.Name}|{set}" );
            }
         }
         finally
         {
            File.Delete( overridePath );
         }
      }

      return lines.ToArray();
   }

   /// <summary>
   /// The live run of the benchmark's own containers: starts each one that is not running with
   /// <see cref="TEST_CPUSET"/> through <see cref="ComposeRunner.UpAsync(string, string, CancellationToken)"/>,
   /// builds the four built-in targets through the factory, binds them, loads 524 random vectors into
   /// each, reads every index state, runs ten searches, drops everything, and stops what it started.
   /// </summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <returns>One line per fact.</returns>
   public static async Task<string[]> LiveContainersAsync( string repoRoot )
   {
      var lines = new List<string>();
      var started = new List<string>();
      using var cancel = new CancellationTokenSource( TimeSpan.FromMinutes( 12 ) );
      try
      {
         foreach( string file in new[] { SqlServerContainer.COMPOSE_FILE, QdrantContainer.COMPOSE_FILE } )
         {
            string path = Path.Combine( repoRoot, "deploy", "engines", file );
            if( await ComposeRunner.IsRunningAsync( path, cancel.Token ) )
            {
               lines.Add( $"start|{file}|already running, not restarted" );
               continue;
            }

            started.Add( path );
            lines.Add( $"start|{file}|{await ComposeRunner.UpAsync( path, TEST_CPUSET, cancel.Token )}" );
         }

         lines.Add( $"started by this scenario|{started.Count}" );
         await LoadAllAsync( repoRoot, lines, cancel.Token );
      }
      finally
      {
         foreach( string path in started )
         {
            lines.Add( $"stop|{Path.GetFileName( path )}|{await ComposeRunner.DownAsync( path, CancellationToken.None ) ?? "stopped"}" );
         }
      }

      return lines.ToArray();
   }

   /// <summary>
   /// Starts one compose file on <see cref="TEST_CPUSET"/> the way run-all does.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <returns>The line <see cref="ComposeRunner.UpAsync(string, string, CancellationToken)"/> returned.</returns>
   public static async Task<string[]> StartAsync( string composePath )
   {
      using var cancel = new CancellationTokenSource( TimeSpan.FromMinutes( 4 ) );
      return new[] { await ComposeRunner.UpAsync( composePath, TEST_CPUSET, cancel.Token ) };
   }

   /// <summary>
   /// Eight searchers on the "sql" container target for a few seconds while every thread of the
   /// container's SQL Server is sampled from /proc every 200 ms: its allowed CPUs, the CPU it last ran
   /// on, and its CPU time. Needs the container running (the caller starts it). Loads 524 rows first
   /// and drops them at the end.
   /// </summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <param name="seconds">Length of the eight-searcher burst.</param>
   /// <returns>One line per fact.</returns>
   public static async Task<string[]> SqlThreadsAsync( string repoRoot, int seconds )
   {
      string stem = "gvbbench_pin" + Guid.NewGuid().ToString( "N" )[..8];
      var lines = new List<string>();
      using var factory = new TargetFactory( new GvbSettings(), repoRoot, null, _ => { } );
      await factory.InitializeAsync( CancellationToken.None );
      BenchTarget target = factory.Create( "sql" );
      ISink sink = await target.BindAsync( CancellationToken.None );
      try
      {
         VectorRecord[] data = TargetsScenarios.Make( ROWS, DIMENSION, 17 );
         await sink.EnsureCollectionAsync( stem, DIMENSION, CancellationToken.None );
         await sink.UpsertAsync( stem, data, CancellationToken.None );
         ContainerFacts facts = ContainerFacts.Parse( ( await new DockerInspector().InspectAsync( SqlServerContainer.CONTAINER, CancellationToken.None ) )! );
         lines.Add( $"container|{facts.Name}|cpuset {facts.Cpuset}|engine {target.Engine}" );
         IReadOnlyList<int> pids = HostProcess.FindInContainer( "sqlservr", facts.Id );
         lines.Add( $"sqlservr processes|{string.Join( ",", pids )}" );
         Dictionary<string, ThreadSample> before = SampleThreads( pids );
         var seen = new Dictionary<int, int>();
         var allowed = new HashSet<string>();
         using var stop = new CancellationTokenSource( TimeSpan.FromSeconds( seconds ) );
         Task<int>[] searchers = Enumerable.Range( 0, 8 ).Select( i => SearchUntilAsync( sink, stem, data, i, stop.Token ) ).ToArray();
         Dictionary<string, ThreadSample> last = before;
         int samples = 0;
         while( !stop.IsCancellationRequested )
         {
            await Task.Delay( SAMPLE_EVERY );
            Dictionary<string, ThreadSample> now = SampleThreads( pids );
            CountBusy( last, now, seen, allowed );
            last = now;
            samples++;
         }

         int searches = ( await Task.WhenAll( searchers ) ).Sum();
         Dictionary<string, ThreadSample> after = SampleThreads( pids );
         long ticks = after.Sum( p => p.Value.Ticks - ( before.TryGetValue( p.Key, out ThreadSample? b ) ? b.Ticks : 0 ) );
         int busyThreads = after.Count( p => p.Value.Ticks > ( before.TryGetValue( p.Key, out ThreadSample? b ) ? b.Ticks : 0 ) );
         lines.Add( $"burst|{seconds} s|8 searchers|{searches} searches|{samples} samples" );
         lines.Add( $"threads|{after.Count} threads|{busyThreads} used CPU during the burst|{ticks} ticks of CPU time" );
         lines.Add( $"allowed lists of every thread|{string.Join( " ", after.Values.Select( v => v.Allowed ).Distinct().OrderBy( v => v ) )}" );
         lines.Add( $"allowed lists of busy threads|{string.Join( " ", allowed.OrderBy( v => v ) )}" );
         lines.Add( $"cpu of busy-thread samples|{string.Join( " ", seen.OrderBy( p => p.Key ).Select( p => $"{p.Key}:{p.Value}" ) )}" );
         lines.Add( $"busy samples outside the cpuset|{seen.Where( p => !CpuSetText.Parse( facts.Cpuset ).Contains( p.Key ) ).Sum( p => p.Value )}" );
      }
      finally
      {
         await sink.DropCollectionAsync( stem, CancellationToken.None );
         lines.Add( $"cleanup|dropped {stem}" );
      }

      return lines.ToArray();
   }

   /// <summary>
   /// The native processes the comparison targets measure, told apart from the containers' processes of the same name.
   /// </summary>
   /// <returns>"name|pid|cgroup" per native process.</returns>
   public static string[] NativeProcesses()
   {
      var lines = new List<string>();
      foreach( string name in new[] { "qdrant", "sqlservr" } )
      {
         foreach( int pid in HostProcess.FindNative( name ) )
         {
            lines.Add( $"{name}|{pid}|{File.ReadAllText( $"/proc/{pid}/cgroup" ).Trim()}" );
         }
      }

      return lines.ToArray();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Builds, binds, loads and checks the four built-in targets on the containers, then drops their collections.
   /// </summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <param name="lines">Where facts go.</param>
   /// <param name="ct">Cancellation.</param>
   private static async Task LoadAllAsync( string repoRoot, List<string> lines, CancellationToken ct )
   {
      string stem = "gvbbench_pin" + Guid.NewGuid().ToString( "N" )[..8];
      VectorRecord[] data = TargetsScenarios.Make( ROWS, DIMENSION, 23 );
      using var factory = new TargetFactory( new GvbSettings(), repoRoot, null, _ => { } );
      await factory.InitializeAsync( ct );
      lines.AddRange( factory.Notes.Select( n => "factory note|" + n ) );
      foreach( string name in new[] { "sql", "sql-diskann", "qdrant", "qdrant-hnsw" } )
      {
         BenchTarget target = factory.Create( name );
         ISink sink = await target.BindAsync( ct );
         try
         {
            lines.Add( $"{name}|hosting {target.Hosting}|compose {Path.GetFileName( target.ComposePath )}|finisher {target.HasIndexFinisher}|pair {target.PairHint?.PassB ?? "none"}" );
            lines.Add( $"{name} engine|{target.Engine}" );
            lines.Add( $"{name} connection|{target.ConnectionText}" );
            lines.Add( $"{name} durability|{target.Durability}" );
            await sink.EnsureCollectionAsync( stem, DIMENSION, ct );
            await sink.UpsertAsync( stem, data, ct );
            string finish = sink is IIndexFinisher finisher ? await finisher.FinishLoadAsync( stem, ct ) : target.Settle != null ? await target.Settle( stem, ct ) : "no index step";
            int top1 = 0;
            for( int i = 0; i < 10; i++ )
            {
               IReadOnlyList<SearchHit> hits = await sink.SearchAsync( stem, data[i].Vector, TOP, ct );
               top1 += hits.Count > 0 && hits[0].ChunkId == data[i].Chunk.ChunkId ? 1 : 0;
            }

            IndexState state = await target.ReadIndexStateAsync( stem, TimeSpan.FromMinutes( 2 ), ct );
            lines.Add( $"{name} load|count {await sink.CountAsync( stem, ct )}|top1 {top1} of 10|ready {state.Ready}|{finish}" );
            lines.Add( $"{name} state|{state.Detail}" );
            lines.Add( $"{name} disk|{( await target.MeasureDiskAsync( stem, ct ) ).Text}" );
         }
         finally
         {
            await sink.DropCollectionAsync( stem, CancellationToken.None );
         }
      }
   }

   /// <summary>
   /// Searches in a loop with stored vectors until the token fires.
   /// </summary>
   /// <param name="sink">Sink.</param>
   /// <param name="stem">Collection.</param>
   /// <param name="data">Vectors.</param>
   /// <param name="offset">Where this searcher starts in the data.</param>
   /// <param name="stop">Ends the loop.</param>
   /// <returns>Searches completed.</returns>
   private static async Task<int> SearchUntilAsync( ISink sink, string stem, VectorRecord[] data, int offset, CancellationToken stop )
   {
      int done = 0;
      for( int i = offset; !stop.IsCancellationRequested; i += 8 )
      {
         await sink.SearchAsync( stem, data[i % data.Length].Vector, TOP, CancellationToken.None );
         done++;
      }

      return done;
   }

   /// <summary>
   /// Reads every thread of the given processes: allowed CPUs, last CPU, CPU time (user + system ticks).
   /// </summary>
   /// <param name="pids">Process ids.</param>
   /// <returns>Samples keyed by "pid/tid".</returns>
   private static Dictionary<string, ThreadSample> SampleThreads( IReadOnlyList<int> pids )
   {
      var samples = new Dictionary<string, ThreadSample>( StringComparer.Ordinal );
      foreach( int pid in pids )
      {
         foreach( string task in SafeDirectories( $"/proc/{pid}/task" ) )
         {
            try
            {
               string stat = File.ReadAllText( Path.Combine( task, "stat" ) );
               string[] fields = stat[( stat.LastIndexOf( ')' ) + 2 )..].Split( ' ' );
               string? allowed = File.ReadAllLines( Path.Combine( task, "status" ) ).FirstOrDefault( l => l.StartsWith( "Cpus_allowed_list:", StringComparison.Ordinal ) )?.Split( ':' )[1].Trim();
               long ticks = long.Parse( fields[11], CultureInfo.InvariantCulture ) + long.Parse( fields[12], CultureInfo.InvariantCulture );
               samples[$"{pid}/{Path.GetFileName( task )}"] = new ThreadSample( allowed ?? "?", int.Parse( fields[36], CultureInfo.InvariantCulture ), ticks );
            }
            catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
            {
               // The thread ended between the listing and the read.
            }
         }
      }

      return samples;
   }

   /// <summary>
   /// For every thread whose CPU time grew since the last sample, counts the CPU it last ran on and its allowed list.
   /// </summary>
   /// <param name="last">Previous sample.</param>
   /// <param name="now">This sample.</param>
   /// <param name="seen">Samples per CPU.</param>
   /// <param name="allowed">Allowed lists of busy threads.</param>
   private static void CountBusy( Dictionary<string, ThreadSample> last, Dictionary<string, ThreadSample> now, Dictionary<int, int> seen, HashSet<string> allowed )
   {
      foreach( ( string key, ThreadSample sample ) in now )
      {
         if( last.TryGetValue( key, out ThreadSample? previous ) && sample.Ticks > previous.Ticks )
         {
            seen[sample.Cpu] = seen.GetValueOrDefault( sample.Cpu ) + 1;
            allowed.Add( sample.Allowed );
         }
      }
   }

   /// <summary>
   /// Lists a folder's subfolders, or none when it is gone.
   /// </summary>
   /// <param name="path">Folder.</param>
   /// <returns>Subfolders.</returns>
   private static IEnumerable<string> SafeDirectories( string path )
   {
      try
      {
         return Directory.EnumerateDirectories( path ).ToList();
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
      {
         return Array.Empty<string>();
      }
   }

   /// <summary>
   /// Runs an action and describes how it ended.
   /// </summary>
   /// <typeparam name="T">Result type.</typeparam>
   /// <param name="action">The action.</param>
   /// <returns>"returned" or "Type: message".</returns>
   private static async Task<string> Describe<T>( Func<Task<T>> action )
   {
      try
      {
         await action();
         return "returned";
      }
      catch( Exception ex )
      {
         return $"{ex.GetType().Name}: {ex.Message}";
      }
   }

   #endregion Private Methods

   #region Nested Types

   /// <summary>One thread at one moment.</summary>
   /// <param name="Allowed">Cpus_allowed_list.</param>
   /// <param name="Cpu">The CPU it last ran on (stat field 39).</param>
   /// <param name="Ticks">User plus system CPU time in clock ticks.</param>
   private sealed record ThreadSample( string Allowed, int Cpu, long Ticks );

   /// <summary>A Docker that has no containers at all.</summary>
   private sealed class NoContainers : IContainerInspector
   {
      /// <summary>Answers that the container does not exist.</summary>
      /// <param name="container">Container name.</param>
      /// <param name="ct">Cancellation.</param>
      /// <returns>Null.</returns>
      public Task<string?> InspectAsync( string container, CancellationToken ct )
      {
         return Task.FromResult<string?>( null );
      }
   }

   #endregion Nested Types
}
#endif
