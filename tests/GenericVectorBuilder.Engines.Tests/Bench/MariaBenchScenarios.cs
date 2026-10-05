// Compiled only by the MariaBench tests, together with the benchmark's own Targets sources and
// Running/EngineLifecycle.cs and Running/EngineHost.cs (the test project does not reference the
// benchmark console project). BENCH_UNDER_TEST is defined only in that compilation, so the test
// project itself sees an empty file.
// Why nothing here names the MariaBench class directly: on a tree that does not have it (the
// starting tree) the compilation must still succeed, so that each test fails on its own for its own
// reason; the class is found by reflection instead.
#if BENCH_UNDER_TEST
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Bench.Running;
using GenericVectorBuilder.Bench.Targets;
using GenericVectorBuilder.Core.Configuration;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Sinks;
using Document = GenericVectorBuilder.Core.Contracts.Document;

namespace GenericVectorBuilder.Bench.UnderTest;

/// <summary>
/// Runs the benchmark's wiring of its "mariadb" target and hands back plain lines for the tests to
/// check: the route, the target built by the factory against a fake Docker that records every ask,
/// what run-all starts and stops, and (live, on this box) the real container next to the untouched
/// daily one.
/// Why a fake Docker that records the names it is asked about: the rule is that the daily container
/// gvb-mariadb is never named at all, and only a record of every ask can show that.
/// Every table the live part creates is named gvb_gvbbench_mariabench* in the benchmark's own
/// container and is dropped at the end; the daily container and its database are never read or written.
/// </summary>
public static class MariaBenchScenarios
{
   #region Data Members

   private const string BENCH = "gvbbench-mariadb";
   private const string DAILY = "gvb-mariadb";
   private const string BENCH_FILE = "mariadb-bench.compose.yaml";
   private const string DAILY_FILE = "mariadb.compose.yaml";
   private const string CPUS = "2-3,6-7";
   private const int ROWS = 200;
   private const int DIMENSION = 32;
   private static readonly TimeSpan CALL_LIMIT = TimeSpan.FromSeconds( 30 );
   private static readonly Regex SOCKET_LINE = new( @"^\s*(?:\S+\s+)?\d+\s+\d+\s+(?<local>\S+)\s+(?<peer>\S+)(?:\s+users:\((?<users>.*)\))?\s*$", RegexOptions.Compiled );

   // Trimmed from "docker inspect gvbbench-mariadb" on this box (2026-10-05, started by run-all on CPUs 2-3,6-7); the environment
   // values are replaced by "x" so no password from secrets.env is ever copied into a test.
   private const string BENCH_INSPECT = """[{"Id":"8cefe0e722eddce51fa5e0fce4af968ea596214aab41f77fcda81c5d4bca4882","Name":"/gvbbench-mariadb","State":{"Status":"running","Running":true,"Paused":false,"Restarting":false,"Dead":false,"ExitCode":0,"StartedAt":"2026-10-05T01:20:59.991216637Z","Health":{"Status":"healthy"}},"HostConfig":{"CpusetCpus":"2-3,6-7","Memory":8589934592},"Config":{"Image":"mariadb:11.8.9","Env":["MARIADB_ROOT_PASSWORD=x","PATH=x"]},"NetworkSettings":{"Ports":{"3306/tcp":[{"HostIp":"127.0.0.1","HostPort":"13306"}]},"Networks":{"gvbbench-mariadb_default":{"NetworkID":"47a9006ed9604913dcdb9ed791fa1baa32b57215fad69944dd07022b8acd991b","Gateway":"172.24.0.1","IPAddress":"172.24.0.2","MacAddress":"36:50:ae:20:f6:e9"}}}}]""";

   // Trimmed from "docker inspect gvb-mariadb" on this box (the daily container: published on 127.0.0.1:3306, all 8 CPUs).
   private const string DAILY_INSPECT = """[{"Id":"b115140380d05c5d4ab5d4e123b77af39bb22c23649fa35e3789e06bf0c3fbd0","Name":"/gvb-mariadb","State":{"Status":"running","Running":true,"Paused":false,"Restarting":false,"Dead":false,"ExitCode":0,"StartedAt":"2026-10-04T14:18:30.610916687Z","Health":{"Status":"healthy"}},"HostConfig":{"CpusetCpus":"0-7","Memory":8589934592},"NetworkSettings":{"Ports":{"3306/tcp":[{"HostIp":"127.0.0.1","HostPort":"3306"}]},"Networks":{"gvb-mariadb_default":{"NetworkID":"fb3abb8901cdc12f2541614d0069535593bfc293a15ac1ea61fc39b6f5743fbc","Gateway":"172.18.0.1","IPAddress":"172.18.0.2","MacAddress":"8e:d4:fe:2e:81:6f"}}}}]""";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The names, file and port the MariaBench class carries, read by reflection.
   /// </summary>
   /// <returns>"NAME|value" lines, or one line saying the class does not exist.</returns>
   public static string[] Constants()
   {
      Type? type = typeof( ContainerRoutes ).Assembly.GetType( "GenericVectorBuilder.Bench.Targets.MariaBench" );
      if( type == null )
      {
         return new[] { "missing|there is no MariaBench class in the benchmark's Targets" };
      }

      return new[] { "TARGET", "CONTAINER", "COMPOSE_FILE", "DATA_FOLDER", "HOST_PORT" }
         .Select( name => $"{name}|{type.GetField( name, BindingFlags.Public | BindingFlags.Static )?.GetRawConstantValue() ?? "missing"}" ).ToArray();
   }

   /// <summary>
   /// What the route table says about the "mariadb" target and the engines next to it.
   /// </summary>
   /// <returns>One line per fact.</returns>
   public static string[] Route()
   {
      EngineRoute? route = ContainerRoutes.Find( "mariadb" );
      var lines = new List<string> { $"mariadb containers|{( route == null ? "no route" : string.Join( ",", route.Containers ) )}" };
      lines.Add( $"pgvector containers|{string.Join( ",", ContainerRoutes.Find( "pgvector" )!.Containers )}" );
      lines.Add( $"routes|{ContainerRoutes.Names.Count}" );
      return lines.ToArray();
   }

   /// <summary>
   /// Builds the "mariadb" target through the factory against a fake Docker that knows both the
   /// benchmark's container and the daily one, binds it, and reports what it did and what it asked.
   /// </summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <returns>One line per fact.</returns>
   public static async Task<string[]> TargetAsync( string repoRoot )
   {
      var docker = new RecordingInspector( new Dictionary<string, string> { [BENCH] = BENCH_INSPECT, [DAILY] = DAILY_INSPECT } );
      using var factory = new TargetFactory( new GvbSettings(), repoRoot, null, _ => { }, docker );
      BenchTarget target = factory.Create( "mariadb" );
      var lines = new List<string>
      {
         $"target|{target.Name}|hosting {target.Hosting}|compose {Path.GetFileName( target.ComposePath )}|exists {File.Exists( target.ComposePath )}|has binding {target.Container != null}",
         $"before binding|asked {docker.Asked()}",
      };
      ISink sink = await target.BindAsync( CancellationToken.None );
      lines.Add( $"after binding|asked {docker.Asked()}" );
      lines.Add( $"connection|{target.ConnectionText}" );
      lines.Add( $"endpoints|{string.Join( ",", Endpoints( sink ) )}" );
      lines.Add( $"container route|{string.Join( ",", target.Container!.Route.Containers )}" );
      BenchTarget other = factory.Create( "pgvector" );
      lines.Add( $"pgvector target|compose {Path.GetFileName( other.ComposePath )}" );
      return lines.ToArray();
   }

   /// <summary>
   /// Binds the target when Docker's container of the benchmark's name looks like the DAILY one
   /// (published on host port 3306 only): the route must refuse, because the port it asks for is
   /// the benchmark's own.
   /// </summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <returns>The outcome, "returned" or "Type: message".</returns>
   public static async Task<string> DailyShapedContainerAsync( string repoRoot )
   {
      string renamed = DAILY_INSPECT.Replace( "/gvb-mariadb", "/" + BENCH, StringComparison.Ordinal );
      using var factory = new TargetFactory( new GvbSettings(), repoRoot, null, _ => { }, new RecordingInspector( new Dictionary<string, string> { [BENCH] = renamed } ) );
      try
      {
         await factory.Create( "mariadb" ).BindAsync( CancellationToken.None );
         return "returned";
      }
      catch( InvalidOperationException ex )
      {
         return $"{ex.GetType().Name}: {ex.Message}";
      }
   }

   /// <summary>
   /// What run-all starts and stops for the "mariadb" target, against a fake host in which the
   /// daily container's compose file is running. First with the benchmark's container down (run-all
   /// starts it on the engine CPUs and stops it), then with it already up before the run (left running).
   /// </summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <returns>The host's calls and the target's notes, in order.</returns>
   public static async Task<string[]> LifecycleAsync( string repoRoot )
   {
      var lines = new List<string>();
      foreach( bool upBefore in new[] { false, true } )
      {
         var host = new FakeHost( upBefore ? new[] { DAILY_FILE, BENCH_FILE } : new[] { DAILY_FILE } );
         using var factory = new TargetFactory( new GvbSettings(), repoRoot, null, _ => { }, new RecordingInspector( new Dictionary<string, string>() ) );
         BenchTarget target = factory.Create( "mariadb" );
         var lifecycle = new EngineLifecycle( host, true, _ => { }, CPUS );
         var notes = new List<string>();
         await lifecycle.SnapshotAsync( new[] { target.ComposePath }, CancellationToken.None );
         await lifecycle.EnsureRunningAsync( target, notes, CancellationToken.None );
         await lifecycle.ReleaseAsync( target, notes );
         string label = upBefore ? "already up" : "down before";
         lines.AddRange( host.Calls.Select( c => $"{label}|{c}" ) );
         lines.Add( $"{label}|notes|{string.Join( " / ", notes )}" );
      }

      return lines.ToArray();
   }

   /// <summary>
   /// The proof on this box: starts the benchmark's container on the engine CPUs, binds the target,
   /// loads and searches a throwaway table, and reads the daily container before and after.
   /// Says which container this process's sockets lead to, which CPUs the container's threads may use,
   /// and whether the daily container's cpuset, start time, restarts and connections are as they were,
   /// with the Docker events of the daily container for the whole time (health checks aside).
   /// </summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <returns>One line per fact.</returns>
   public static async Task<string[]> LiveAsync( string repoRoot )
   {
      var lines = new List<string>();
      using var cancel = new CancellationTokenSource( TimeSpan.FromMinutes( 12 ) );
      CancellationToken ct = cancel.Token;
      string compose = Path.Combine( repoRoot, "deploy", "engines", BENCH_FILE );
      string stem = "gvbbench_mariabench" + Guid.NewGuid().ToString( "N" )[..8];
      long since = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
      string before = await DailyFactsAsync( ct );
      lines.Add( "daily before|" + before );
      bool wasUp = await ComposeRunner.IsRunningAsync( compose, ct );
      lines.Add( $"benchmark container up before the test|{wasUp}" );
      Stopwatch clock = Stopwatch.StartNew();
      using var factory = new TargetFactory( new GvbSettings(), repoRoot, null, _ => { } );
      BenchTarget target = factory.Create( "mariadb" );
      ISink? bound = null;
      try
      {
         if( !wasUp )
         {
            lines.Add( "started|" + await ComposeRunner.UpAsync( compose, CPUS, ct ) );
         }

         ISink sink = bound = await target.BindAsync( ct );
         ContainerAddress address = target.Connections.Single();
         lines.Add( "connection|" + address.Describe() );
         lines.AddRange( await ContainerFactsAsync( address, ct ) );
         VectorRecord[] data = Make( ROWS, DIMENSION );
         await sink.EnsureCollectionAsync( stem, DIMENSION, ct );
         await sink.UpsertAsync( stem, data, ct );
         lines.Add( $"loaded|{await sink.CountAsync( stem, ct )} rows into gvb.gvb_{stem} of the benchmark's container" );
         lines.AddRange( await SearchAndWatchSocketsAsync( sink, stem, data, address, ct ) );
      }
      finally
      {
         if( bound != null )
         {
            await bound.DropCollectionAsync( stem, CancellationToken.None );
            lines.Add( $"cleanup|dropped gvb.gvb_{stem}" );
         }

         if( !wasUp )
         {
            lines.Add( $"stopped|{await ComposeRunner.DownAsync( compose, CancellationToken.None ) ?? "compose down was clean"}" );
         }
      }

      string after = await DailyFactsAsync( CancellationToken.None );
      lines.Add( "daily after|" + after );
      lines.Add( $"daily same container, start time, restarts, cpuset|{SameIdentity( before, after )}" );
      lines.Add( $"daily connections|{ConnectionDelta( before, after, clock.Elapsed )}" );
      lines.Add( $"daily events|{await DailyEventsAsync( since, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1 )}" );
      lines.Add( $"benchmark container after the test|up {await ComposeRunner.IsRunningAsync( compose, CancellationToken.None )}" );
      return lines.ToArray();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Every host:port a sink's options point at, read from the sink's own options object.
   /// </summary>
   /// <param name="sink">The sink.</param>
   /// <returns>The endpoints.</returns>
   private static List<string> Endpoints( ISink sink )
   {
      object options = sink.GetType().GetField( "_options", BindingFlags.NonPublic | BindingFlags.Instance )!.GetValue( sink )!;
      return new List<string> { $"{options.GetType().GetProperty( "Host" )!.GetValue( options )}:{options.GetType().GetProperty( "Port" )!.GetValue( options )}" };
   }

   /// <summary>
   /// The daily container as Docker and its own server describe it, on one line, read without
   /// changing anything: identity, start time, restarts, cpuset, and three server counters.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>"key=value" pairs separated by spaces.</returns>
   private static async Task<string> DailyFactsAsync( CancellationToken ct )
   {
      string docker = ( await Shell.TryOutputAsync( "sudo", new[] { "-n", "docker", "inspect", "-f", "id={{.Id}} started={{.State.StartedAt}} restarts={{.RestartCount}} cpuset={{.HostConfig.CpusetCpus}} status={{.State.Status}}", DAILY }, ct ) ?? "docker inspect failed" ).Trim();
      string password = ReadSecret( "MARIADB_ROOT_PASSWORD" );
      ShellResult server = await Shell.RunAsync( "sudo", new[] { "-n", "docker", "exec", "-e", $"MYSQL_PWD={password}", DAILY, "mariadb", "-uroot", "-N", "-e",
         "SHOW GLOBAL STATUS WHERE Variable_name IN ('Connections','Uptime','Aborted_connects')" }, CALL_LIMIT, ct );
      string counters = string.Join( " ", server.Output.Split( '\n', StringSplitOptions.RemoveEmptyEntries ).Select( l => l.Replace( '\t', '=' ).Trim() ) );
      return $"{docker} {counters}";
   }

   /// <summary>
   /// Whether two daily readings name the same container started at the same time with the same
   /// restart count and cpuset.
   /// </summary>
   /// <param name="before">First reading.</param>
   /// <param name="after">Second reading.</param>
   /// <returns>"yes" or the fields that differ.</returns>
   private static string SameIdentity( string before, string after )
   {
      string[] keys = { "id", "started", "restarts", "cpuset", "status" };
      string[] different = keys.Where( k => Field( before, k ) != Field( after, k ) ).Select( k => $"{k}: {Field( before, k )} -> {Field( after, k )}" ).ToArray();
      return different.Length == 0 ? $"yes (id {Field( after, "id" )[..12]}, started {Field( after, "started" )}, restarts {Field( after, "restarts" )}, cpuset {Field( after, "cpuset" )})" : "NO " + string.Join( "; ", different );
   }

   /// <summary>
   /// The new connections the daily server accepted between two readings, against what its own
   /// docker health check explains (measured 2026-10-05: 4 new connections per 10 s on an idle server,
   /// from the check that runs every 5 s). A coarse check on purpose: the strong one is the socket table,
   /// which shows no TCP connection to the daily container at all.
   /// </summary>
   /// <param name="before">First reading.</param>
   /// <param name="after">Second reading.</param>
   /// <param name="elapsed">Time between them.</param>
   /// <returns>"within the health check's share" or "TOO MANY", with the numbers.</returns>
   private static string ConnectionDelta( string before, string after, TimeSpan elapsed )
   {
      long delta = long.Parse( Field( after, "Connections" ) ) - long.Parse( Field( before, "Connections" ) );
      double allowed = elapsed.TotalSeconds * 0.5 + 4;
      return $"{( delta <= allowed ? "within the health check's share" : "TOO MANY" )}|{delta} new in {elapsed.TotalSeconds:0} s, at most {allowed:0} expected (health check about 0.4 per second, plus this reader's own read and a margin of 3); Aborted_connects {Field( before, "Aborted_connects" )} -> {Field( after, "Aborted_connects" )}";
   }

   /// <summary>
   /// Docker events of the daily container between two times that mean something happened to it
   /// (anything but the health check's exec events).
   /// </summary>
   /// <param name="since">Unix seconds.</param>
   /// <param name="until">Unix seconds.</param>
   /// <returns>"none" or the events.</returns>
   private static async Task<string> DailyEventsAsync( long since, long until )
   {
      var arguments = new List<string> { "-n", "docker", "events", "--since", since.ToString(), "--until", until.ToString(), "--filter", $"container={DAILY}" };
      foreach( string action in new[] { "update", "restart", "die", "kill", "stop", "start", "pause", "unpause", "oom", "create", "destroy", "rename", "health_status" } )
      {
         arguments.AddRange( new[] { "--filter", $"event={action}" } );
      }

      arguments.AddRange( new[] { "--format", "{{.Action}} {{.Actor.Attributes.name}}" } );
      ShellResult result = await Shell.RunAsync( "sudo", arguments, TimeSpan.FromSeconds( 60 ), CancellationToken.None );
      string text = result.Output.Trim();
      return result.ExitCode != 0 ? $"docker events failed: {result.Error.Trim()}" : text.Length == 0 ? "none" : text.Replace( '\n', ',' );
   }

   /// <summary>
   /// What Docker and the kernel say about the benchmark's container: its cpuset from creation, the CPUs
   /// every thread of its server may use, and the address of the daily container for comparison.
   /// </summary>
   /// <param name="address">The address the target recorded.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>One line per fact.</returns>
   private static async Task<List<string>> ContainerFactsAsync( ContainerAddress address, CancellationToken ct )
   {
      string inspect = ( await Shell.TryOutputAsync( "sudo", new[] { "-n", "docker", "inspect", "-f", "cpuset={{.HostConfig.CpusetCpus}} pid={{.State.Pid}} image={{.Config.Image}} ports={{json .NetworkSettings.Ports}}", BENCH }, ct ) ?? "unreadable" ).Trim();
      var lines = new List<string> { "container|" + inspect };
      int pid = int.Parse( Regex.Match( inspect, @"pid=(\d+)" ).Groups[1].Value );
      string? status = await Shell.TryOutputAsync( "sudo", new[] { "-n", "sh", "-c", $"cat /proc/{pid}/task/*/status" }, ct );
      string[] allowed = ( status ?? string.Empty ).Split( '\n' ).Where( l => l.StartsWith( "Cpus_allowed_list:", StringComparison.Ordinal ) ).Select( l => l.Split( ':' )[1].Trim() ).ToArray();
      lines.Add( $"threads|{allowed.Length}|CPUs allowed {string.Join( ",", allowed.Distinct() )}" );
      lines.Add( $"address|{address.Ip}:{address.ContainerPort}|published {address.HostIp}:{address.HostPort}" );
      return lines;
   }

   /// <summary>
   /// Searches back to back on four tasks for a few seconds and three times reads the kernel's socket
   /// table: this process's sockets to the benchmark container's own address, to its published port
   /// (docker-proxy), and to the daily container (its address and its published port).
   /// </summary>
   /// <param name="sink">The bound sink.</param>
   /// <param name="stem">Collection.</param>
   /// <param name="data">Records whose vectors are the queries.</param>
   /// <param name="address">The benchmark container's recorded address.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>One line per sample and one for the search counts.</returns>
   private static async Task<List<string>> SearchAndWatchSocketsAsync( ISink sink, string stem, VectorRecord[] data, ContainerAddress address, CancellationToken ct )
   {
      var lines = new List<string>();
      int completed = 0;
      int failed = 0;
      using var stop = CancellationTokenSource.CreateLinkedTokenSource( ct );
      Task[] searchers = Enumerable.Range( 0, 4 ).Select( offset => Task.Run( async () =>
      {
         for( int i = offset; !stop.IsCancellationRequested; i++ )
         {
            try
            {
               await sink.SearchAsync( stem, data[i % data.Length].Vector, 10, stop.Token );
               Interlocked.Increment( ref completed );
            }
            catch( OperationCanceledException ) when( stop.IsCancellationRequested )
            {
               return;
            }
            catch( Exception )
            {
               Interlocked.Increment( ref failed );
               await Task.Delay( 50, CancellationToken.None );
            }
         }
      }, ct ) ).ToArray();
      try
      {
         await Task.Delay( 800, ct );
         for( int sample = 1; sample <= 3; sample++ )
         {
            lines.Add( $"sockets sample {sample}|" + await SocketSummaryAsync( address, ct ) );
            await Task.Delay( 500, ct );
         }
      }
      finally
      {
         stop.Cancel();
         await Task.WhenAll( searchers );
      }

      lines.Add( $"searches|completed {completed}|failed {failed}" );
      return lines;
   }

   /// <summary>
   /// Counts this process's established sockets by where they lead.
   /// </summary>
   /// <param name="address">The benchmark container's recorded address.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The counts on one line.</returns>
   private static async Task<string> SocketSummaryAsync( ContainerAddress address, CancellationToken ct )
   {
      ShellResult result = await Shell.RunAsync( "sudo", new[] { "-n", "ss", "-tnpH", "state", "established" }, TimeSpan.FromSeconds( 20 ), ct );
      if( result.ExitCode != 0 )
      {
         return $"ss failed ({result.ExitCode}): {result.Error.Trim()}";
      }

      int me = Environment.ProcessId;
      var peers = new List<string>();
      foreach( string line in result.Output.Split( '\n', StringSplitOptions.RemoveEmptyEntries ) )
      {
         Match match = SOCKET_LINE.Match( line );
         if( match.Success && Regex.Matches( match.Groups["users"].Value, @"pid=(\d+)" ).Any( m => int.Parse( m.Groups[1].Value ) == me ) )
         {
            peers.Add( match.Groups["peer"].Value.Replace( "[::ffff:", string.Empty ).Replace( "]:", ":" ) );
         }
      }

      int Count( string peer ) => peers.Count( p => p == peer );
      return $"to the benchmark container {address.Ip}:{address.ContainerPort} {Count( $"{address.Ip}:{address.ContainerPort}" )}"
         + $"|to its published port {address.HostIp}:{address.HostPort} {Count( $"{address.HostIp}:{address.HostPort}" )}"
         + $"|to the daily container 172.18.0.2:3306 {Count( "172.18.0.2:3306" )}|to the daily's published port 127.0.0.1:3306 {Count( "127.0.0.1:3306" )}";
   }

   /// <summary>
   /// One field of a "key=value key=value" line.
   /// </summary>
   /// <param name="line">The line.</param>
   /// <param name="key">Field name.</param>
   /// <returns>The value, or "missing".</returns>
   private static string Field( string line, string key )
   {
      Match match = Regex.Match( line, $@"(?:^|\s){Regex.Escape( key )}=(\S*)" );
      return match.Success ? match.Groups[1].Value : "missing";
   }

   /// <summary>
   /// Reads one KEY=VALUE secret from the shared secrets file.
   /// </summary>
   /// <param name="key">Secret name.</param>
   /// <returns>The value, or an empty text when absent.</returns>
   private static string ReadSecret( string key )
   {
      string path = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), "gvb-data", "engines", "secrets.env" );
      string? line = File.Exists( path ) ? File.ReadLines( path ).LastOrDefault( l => l.StartsWith( key + "=", StringComparison.Ordinal ) ) : null;
      return line == null ? string.Empty : line[( key.Length + 1 )..].Trim();
   }

   /// <summary>
   /// Random unit vectors wrapped as records.
   /// </summary>
   /// <param name="count">Records.</param>
   /// <param name="dimension">Dimension.</param>
   /// <returns>The records.</returns>
   private static VectorRecord[] Make( int count, int dimension )
   {
      var random = new Random( 5 );
      var records = new VectorRecord[count];
      for( int i = 0; i < count; i++ )
      {
         float[] vector = Enumerable.Range( 0, dimension ).Select( _ => (float)( random.NextDouble() * 2 - 1 ) ).ToArray();
         float norm = MathF.Sqrt( vector.Sum( v => v * v ) );
         vector = vector.Select( v => v / norm ).ToArray();
         var document = new Document( $"file{i % 20}.cs", "files", "mariabench", $"text {i}", "h", new Dictionary<string, string>() );
         records[i] = new VectorRecord( document, new Chunk( new Guid( i + 1, 0, 0, new byte[8] ), document.DocKey, i, $"chunk text {i}" ), vector );
      }

      return records;
   }

   #endregion Private Methods

   #region Nested Types

   /// <summary>Answers docker inspect from a table and records every name it is asked about.</summary>
   private sealed class RecordingInspector : IContainerInspector
   {
      private readonly Dictionary<string, string> _answers;
      private readonly List<string> _asked = new();

      /// <summary>Creates the fake.</summary>
      /// <param name="answers">docker inspect text per container; a container not listed does not exist.</param>
      public RecordingInspector( Dictionary<string, string> answers )
      {
         _answers = answers;
      }

      /// <summary>The names asked about so far, in order.</summary>
      /// <returns>The names joined by commas, or "none".</returns>
      public string Asked()
      {
         lock( _asked )
         {
            return _asked.Count == 0 ? "none" : string.Join( ",", _asked );
         }
      }

      /// <summary>Answers one ask and records its name.</summary>
      /// <param name="container">Container name.</param>
      /// <param name="ct">Cancellation.</param>
      /// <returns>The scripted answer, or null for a container that is not listed.</returns>
      public Task<string?> InspectAsync( string container, CancellationToken ct )
      {
         lock( _asked )
         {
            _asked.Add( container );
         }

         return Task.FromResult( _answers.TryGetValue( container, out string? json ) ? json : null );
      }
   }

   /// <summary>A compose host that records its calls and knows which compose files are running.</summary>
   private sealed class FakeHost : IEngineHost
   {
      private readonly HashSet<string> _running;
      private readonly List<string> _calls = new();

      /// <summary>Creates the host.</summary>
      /// <param name="running">Compose file names that are running now.</param>
      public FakeHost( IEnumerable<string> running )
      {
         _running = new HashSet<string>( running, StringComparer.Ordinal );
      }

      /// <summary>Every call so far as "operation|file|argument".</summary>
      public IReadOnlyList<string> Calls => _calls;

      /// <summary>Records the question and answers from the table.</summary>
      /// <param name="composePath">Compose file.</param>
      /// <param name="ct">Cancellation.</param>
      /// <returns>True when the file's name is in the running set.</returns>
      public Task<bool> IsRunningAsync( string composePath, CancellationToken ct )
      {
         _calls.Add( $"running?|{Path.GetFileName( composePath )}|{_running.Contains( Path.GetFileName( composePath ) )}" );
         return Task.FromResult( _running.Contains( Path.GetFileName( composePath ) ) );
      }

      /// <summary>Records the start and marks the file running.</summary>
      /// <param name="composePath">Compose file.</param>
      /// <param name="cpuset">CPU list or null.</param>
      /// <param name="ct">Cancellation.</param>
      /// <returns>A read-back line when a cpuset was given.</returns>
      public Task<string?> UpAsync( string composePath, string? cpuset, CancellationToken ct )
      {
         _calls.Add( $"up|{Path.GetFileName( composePath )}|cpuset {cpuset ?? "none"}" );
         _running.Add( Path.GetFileName( composePath ) );
         return Task.FromResult<string?>( cpuset == null ? null : $"fake start on {cpuset}" );
      }

      /// <summary>Records the stop and marks the file down.</summary>
      /// <param name="composePath">Compose file.</param>
      /// <param name="ct">Cancellation.</param>
      /// <returns>Null (a clean stop).</returns>
      public Task<string?> DownAsync( string composePath, CancellationToken ct )
      {
         _calls.Add( $"down|{Path.GetFileName( composePath )}|" );
         _running.Remove( Path.GetFileName( composePath ) );
         return Task.FromResult<string?>( null );
      }
   }

   #endregion Nested Types
}
#endif
