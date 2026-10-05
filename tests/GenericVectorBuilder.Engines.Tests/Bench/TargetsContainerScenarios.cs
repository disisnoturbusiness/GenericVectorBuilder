// Compiled only by the Targets tests, together with the benchmark's own Targets sources and
// TargetsScenarios.cs (the test project does not reference the benchmark console project).
// BENCH_UNDER_TEST is defined only in that compilation, so the test project itself sees an empty file.
#if BENCH_UNDER_TEST
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Bench.Targets;
using GenericVectorBuilder.Core.Configuration;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Document = GenericVectorBuilder.Core.Contracts.Document;

namespace GenericVectorBuilder.Bench.UnderTest;

/// <summary>
/// Runs the benchmark's container-address code and hands back plain lines for the tests to
/// check. The pure part feeds it "docker inspect" text (one real capture, the rest built to
/// match) and compose files from deploy/engines; the live part sends real searches to the running
/// benchmark container gvbbench-mariadb (the target's own, never the daily gvb-mariadb) and looks at
/// the kernel's socket table with ss while they run.
/// Why the live part exists: the pure part proves the address a sink is BUILT with, but only the
/// socket table shows where the bytes actually go. Every MariaDB table it creates is named
/// gvb_gvbbench_direct* and is dropped at the end; the daily container is never contacted.
/// </summary>
public static class TargetsContainerScenarios
{
   #region Data Members

   private const string FIXED_NETWORK = "gvb-test_default";

   // A small table: the proof is about the route of the bytes, not about the index. The index step
   // (FinishLoadAsync) is deliberately not run: its readiness walk asks for LIMIT = every row of a small
   // table, and MariaDB's optimizer then scans instead of using the vector index (measured 2026-10-04 on a
   // 2,000-row table: LIMIT 1000 gives key NULL, LIMIT 10 gives vec_idx), so it would never report ready.
   private const int ROWS = 300;
   private const int DIMENSION = 32;
   private static readonly Regex SOCKET_LINE = new( @"^\s*(?:\S+\s+)?\d+\s+\d+\s+(?<local>\S+)\s+(?<peer>\S+)(?:\s+users:\((?<users>.*)\))?\s*$", RegexOptions.Compiled );
   private static readonly Regex USER = new( @"\(""(?<name>[^""]*)"",pid=(?<pid>\d+)", RegexOptions.Compiled );
   private static readonly Regex COMPOSE_PORT = new( @"^\s*-\s*""?(?:127\.0\.0\.1|0\.0\.0\.0):(?<host>\d+):(?<container>\d+)(?:/tcp)?""?\s*$", RegexOptions.Compiled );
   private static readonly Regex COMPOSE_NAME = new( @"^\s*container_name:\s*(?<name>\S+)\s*$", RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>Reads docker inspect text with <see cref="ContainerFacts.Parse"/>.</summary>
   /// <param name="json">The text.</param>
   /// <returns>One line per fact, or one line starting "error|" with the message.</returns>
   public static string[] ParseInspect( string json )
   {
      try
      {
         ContainerFacts facts = ContainerFacts.Parse( json );
         var lines = new List<string> { $"container|{facts.Name}|{facts.Id}|{facts.Status}|{facts.Running}" };
         lines.AddRange( facts.Networks.Select( n => $"network|{n.Name}|{n.Ip}" ) );
         lines.AddRange( facts.Ports.Select( p => $"port|{p.ContainerPort}|{p.Protocol}|{p.HostIp}|{p.HostPort}" ) );
         return lines.ToArray();
      }
      catch( InvalidOperationException ex )
      {
         return new[] { "error|" + ex.Message };
      }
   }

   /// <summary>
   /// Builds docker inspect text for a container the way Docker prints it, for the cases a real
   /// container cannot be put in on demand (not running, no address, several networks).
   /// </summary>
   /// <param name="name">Container name.</param>
   /// <param name="status">State text.</param>
   /// <param name="networks">"network=ip" entries; an empty ip means no address yet.</param>
   /// <param name="ports">"host:container" published ports on 127.0.0.1.</param>
   /// <returns>The JSON text.</returns>
   public static string InspectJson( string name, string status, string[] networks, string[] ports )
   {
      var published = new Dictionary<string, object?>();
      foreach( string port in ports )
      {
         string[] parts = port.Split( ':' );
         published[$"{parts[1]}/tcp"] = new[] { new Dictionary<string, string> { ["HostIp"] = "127.0.0.1", ["HostPort"] = parts[0] } };
      }

      var nets = networks.Select( n => n.Split( '=' ) ).ToDictionary( p => p[0], p => (object)new Dictionary<string, string> { ["IPAddress"] = p[1] } );
      var root = new Dictionary<string, object>
      {
         ["Id"] = Convert.ToHexString( System.Security.Cryptography.SHA256.HashData( System.Text.Encoding.UTF8.GetBytes( name ) ) ).ToLowerInvariant(),
         ["Name"] = "/" + name,
         ["State"] = new Dictionary<string, object> { ["Status"] = status, ["Running"] = status == "running" },
         ["NetworkSettings"] = new Dictionary<string, object> { ["Ports"] = published, ["Networks"] = nets },
      };
      return JsonSerializer.Serialize( new[] { root } );
   }

   /// <summary>
   /// Rewrites one default URL through a router built from one container's inspect text.
   /// </summary>
   /// <param name="json">docker inspect text of the container.</param>
   /// <param name="container">Container name.</param>
   /// <param name="publishedUrl">The sink's default URL.</param>
   /// <returns>"ok|url" or "error|message".</returns>
   public static string RouterUrl( string json, string container, string publishedUrl )
   {
      try
      {
         return "ok|" + new ContainerRouter( new[] { ContainerFacts.Parse( json ) } ).Url( container, publishedUrl );
      }
      catch( InvalidOperationException ex )
      {
         return "error|" + ex.Message;
      }
   }

   /// <summary>
   /// Resolves one container through a fake Docker that answers a sequence of states, one per ask
   /// (the last answer repeats), within a deadline.
   /// </summary>
   /// <param name="container">Container name.</param>
   /// <param name="answers">docker inspect texts in order; "missing" means no such container.</param>
   /// <param name="deadlineMs">Deadline.</param>
   /// <param name="pollMs">Pause between asks.</param>
   /// <returns>"ok|asks|ip" or "error|asks|message".</returns>
   public static async Task<string> ResolveAsync( string container, string[] answers, int deadlineMs, int pollMs )
   {
      var fake = new FakeInspector( new Dictionary<string, List<string?>> { [container] = answers.Select( a => a == "missing" ? null : a ).ToList() } );
      try
      {
         ContainerRouter router = await ContainerRouter.ResolveAsync( fake, new[] { container }, TimeSpan.FromMilliseconds( deadlineMs ), TimeSpan.FromMilliseconds( pollMs ), CancellationToken.None );
         ContainerAddress address = router.Address( container, 3306 );
         return $"ok|{fake.Calls}|{address.Endpoint}";
      }
      catch( InvalidOperationException ex )
      {
         return $"error|{fake.Calls}|{ex.Message}";
      }
   }

   /// <summary>
   /// Builds every compose-hosted engine's target through the factory, binds it against a fake Docker
   /// whose containers and ports come from the repository's compose files, and checks the addresses
   /// the sink was built with. Three sources must agree: the compose file (container port), the sink's
   /// own default options (the host port it would have used) and the address the built sink carries.
   /// </summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <returns>One line per engine ("engine|ok|..." or "engine|FAIL|..."), then "coverage|" lines.</returns>
   public static async Task<string[]> ComposeRoutesAsync( string repoRoot )
   {
      Dictionary<string, ComposeContainer> compose = ReadCompose( repoRoot );
      Dictionary<string, string> addresses = compose.Keys.OrderBy( k => k ).Select( ( k, i ) => ( k, ip: $"172.30.0.{i + 2}" ) ).ToDictionary( p => p.k, p => p.ip );
      var answers = compose.ToDictionary( p => p.Key, p => new List<string?> { InspectJson( p.Key, "running", new[] { $"{FIXED_NETWORK}={addresses[p.Key]}" }, p.Value.Ports.Select( q => $"{q.Host}:{q.Container}" ).ToArray() ) } );
      using var factory = new TargetFactory( new GvbSettings(), repoRoot, null, _ => { }, new FakeInspector( answers ) );
      IReadOnlyDictionary<string, ISink> catalog = EngineCatalog.CreateAll();
      var lines = new List<string>();
      foreach( string name in ContainerRoutes.Names.OrderBy( n => n ) )
      {
         lines.Add( await CheckEngineAsync( factory, catalog, compose, addresses, name ) );
      }

      string[] hosted = catalog.Values.Where( s => ( s as IEngineDescription )?.ComposeFile.EndsWith( ".compose.yaml", StringComparison.Ordinal ) == true ).Select( s => s.Name ).ToArray();
      lines.Add( "coverage|hosted without a route|" + string.Join( ",", hosted.Where( h => ContainerRoutes.Find( h ) == null ) ) );
      lines.Add( "coverage|routes without a hosted sink|" + string.Join( ",", ContainerRoutes.Names.Where( n => !hosted.Contains( n, StringComparer.OrdinalIgnoreCase ) ) ) );
      lines.Add( "coverage|hosted|" + hosted.Length );
      return lines.ToArray();
   }

   /// <summary>
   /// Exercises one compose-hosted target's binding against a fake Docker and counts the asks.
   /// </summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <param name="mariadbJson">docker inspect text of the benchmark's MariaDB container (gvbbench-mariadb).</param>
   /// <returns>One line per fact.</returns>
   public static async Task<string[]> BindingAsync( string repoRoot, string mariadbJson )
   {
      var lines = new List<string>();
      var fake = new FakeInspector( new Dictionary<string, List<string?>> { [MariaBench.CONTAINER] = new() { mariadbJson } } );
      using var factory = new TargetFactory( new GvbSettings(), repoRoot, null, _ => { }, fake );
      BenchTarget target = factory.Create( "mariadb" );
      string known = $"{target.Name}|{target.Engine.Length > 0}|{target.Hosting}|{target.Durability.Length > 0}|{target.Index.Length > 0}|{target.HasIndexFinisher}";
      lines.Add( $"before binding|asks {fake.Calls}|connections {target.Connections.Count}|{known}" );
      ISink[] sinks = await Task.WhenAll( Enumerable.Range( 0, 8 ).Select( _ => Task.Run( () => target.BindAsync( CancellationToken.None ) ) ) );
      lines.Add( $"8 concurrent binds|distinct sinks {sinks.Distinct().Count()}|asks {fake.Calls}" );
      lines.Add( $"sink getter|same object {ReferenceEquals( target.Sink, sinks[0] )}|asks {fake.Calls}" );
      lines.Add( $"connections|{target.Connections.Count}|{target.ConnectionText}" );
      lines.Add( $"endpoints|{string.Join( ",", Endpoints( target.Sink ) )}" );

      using var missingFactory = new TargetFactory( new GvbSettings(), repoRoot, null, _ => { }, new FakeInspector( new Dictionary<string, List<string?>>() ) );
      BenchTarget missing = missingFactory.Create( "mariadb" );
      IndexState state = await missing.ReadIndexStateAsync( "gvbbench_none", TimeSpan.FromSeconds( 5 ), CancellationToken.None );
      lines.Add( $"container missing, index state|ready {state.Ready}|{state.Detail}" );
      lines.Add( "container missing, sink getter|" + await Describe( () => Task.Run( () => missing.Sink ) ) );
      lines.Add( "container missing, bind|" + await Describe( () => missing.BindAsync( CancellationToken.None ) ) );
      lines.Add( $"container missing, name still works|{missing.Name}" );
      return lines.ToArray();
   }

   /// <summary>
   /// What the factory refuses and what it still offers before its server reads are done.
   /// </summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <returns>One line per fact.</returns>
   public static async Task<string[]> FactoryGuardsAsync( string repoRoot )
   {
      using var factory = new TargetFactory( new GvbSettings(), repoRoot, null, _ => { }, new FakeInspector( new Dictionary<string, List<string?>>() ) );
      var lines = new List<string>();
      foreach( string name in new[] { "sql", "sql-diskann", "qdrant", "qdrant-hnsw" } )
      {
         lines.Add( $"{name} before InitializeAsync|" + await Describe( () => Task.Run( () => factory.Create( name ) ) ) );
      }

      BenchTarget engine = factory.Create( "mariadb" );
      lines.Add( $"mariadb before InitializeAsync|created|hosting {engine.Hosting}|binding {engine.Container != null}|pair {engine.PairHint?.Target ?? "none"}" );
      lines.Add( $"default pair|{TargetFactory.SQL_DISKANN_PAIR.Target}|{TargetFactory.SQL_DISKANN_PAIR.PassA}|{TargetFactory.SQL_DISKANN_PAIR.PassB}" );
      return lines.ToArray();
   }

   /// <summary>Parses "ss -tnpH" text.</summary>
   /// <param name="text">The text.</param>
   /// <returns>One line per socket: "local|peer|name:pid,name:pid".</returns>
   public static string[] ParseSockets( string text )
   {
      return Sockets( text ).Select( s => $"{s.Local}|{s.Peer}|{string.Join( ",", s.Users.Select( u => $"{u.Name}:{u.Pid}" ) )}" ).ToArray();
   }

   /// <summary>
   /// Sends real searches to the running benchmark container gvbbench-mariadb (start it first with
   /// deploy/engines/mariadb-bench.compose.yaml; the daily gvb-mariadb is never used) twice, once on the
   /// container's own address (the benchmark's route) and once on the published 127.0.0.1 port
   /// (docker-proxy, as a positive control), and while each runs reads the kernel's socket table with "sudo ss -tnp". For
   /// each route it reports where this process's sockets lead and whether docker-proxy holds a socket
   /// facing one of ours. Then times single searches on both routes in alternating rounds (evidence,
   /// not an assertion).
   /// </summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <returns>One line per fact.</returns>
   public static async Task<string[]> LiveMariaDbAsync( string repoRoot )
   {
      string stem = "gvbbench_direct" + Guid.NewGuid().ToString( "N" )[..8];
      var lines = new List<string>();
      using var cancel = new CancellationTokenSource( TimeSpan.FromMinutes( 8 ) );
      using var factory = new TargetFactory( new GvbSettings(), repoRoot, null, _ => { } );
      BenchTarget target = factory.Create( "mariadb" );
      ISink direct = await target.BindAsync( cancel.Token );
      ContainerAddress address = target.Connections.Single();
      lines.Add( "connection|" + address.Describe() );
      lines.Add( "docker says the address is|" + ( await Shell.TryOutputAsync( "sudo", new[] { "-n", "docker", "inspect", "-f", "{{range .NetworkSettings.Networks}}{{.IPAddress}} {{end}}", address.Container }, cancel.Token ) ) );
      MariaDbSinkOptions proxiedOptions = MariaDbSinkOptions.LocalDefaults();
      proxiedOptions.Port = address.HostPort;
      using var proxied = new MariaDbSink( proxiedOptions );
      VectorRecord[] data = Make( ROWS, DIMENSION, 7 );
      try
      {
         await direct.EnsureCollectionAsync( stem, DIMENSION, cancel.Token );
         await direct.UpsertAsync( stem, data, cancel.Token );
         lines.Add( $"loaded|{await direct.CountAsync( stem, cancel.Token )} rows of {DIMENSION} dimensions into gvb.gvb_{stem}" );
         lines.AddRange( await ProbeRouteAsync( "direct", direct, stem, data, address.Ip, address.ContainerPort, address.ProxiedEndpoint, cancel.Token ) );
         lines.AddRange( await ProbeRouteAsync( "proxied control", proxied, stem, data, address.HostIp, address.HostPort, address.ProxiedEndpoint, cancel.Token ) );
         lines.AddRange( await TimeRoutesAsync( direct, proxied, stem, data, cancel.Token ) );
      }
      finally
      {
         await direct.DropCollectionAsync( stem, CancellationToken.None );
         lines.Add( $"cleanup|dropped gvb.gvb_{stem}" );
      }

      return lines.ToArray();
   }

   /// <summary>
   /// Binds the target of every engine whose container is running right now, against the real Docker, and
   /// checks the address it recorded two independent ways: a second, differently formatted docker read of
   /// the container's address, and a real TCP connection to that address and port from this process.
   /// Engines whose container is not running (other work starts and stops them) are listed as skipped.
   /// Nothing is written to any engine.
   /// </summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <returns>One line per engine, and per container port for those that are bound.</returns>
   public static async Task<string[]> LiveBindRunningAsync( string repoRoot )
   {
      var lines = new List<string>();
      using var cancel = new CancellationTokenSource( TimeSpan.FromMinutes( 3 ) );
      using var factory = new TargetFactory( new GvbSettings(), repoRoot, null, _ => { } );
      foreach( string name in ContainerRoutes.Names.OrderBy( n => n, StringComparer.Ordinal ) )
      {
         BenchTarget target = factory.Create( name );
         try
         {
            await target.BindAsync( cancel.Token );
         }
         catch( InvalidOperationException ex )
         {
            lines.Add( $"{name}|skipped|{ex.Message}" );
            continue;
         }

         foreach( ContainerAddress address in target.Connections )
         {
            string docker = ( await Shell.TryOutputAsync( "sudo", new[] { "-n", "docker", "inspect", "-f", "{{range .NetworkSettings.Networks}}{{.IPAddress}} {{end}}", address.Container }, cancel.Token ) ) ?? "unreadable";
            lines.Add( $"{name}|bound|{address.Endpoint}|{address.Container}|proxied {address.ProxiedEndpoint}|reachable {await CanConnectAsync( address.Ip, address.ContainerPort, cancel.Token )}|docker says {docker}" );
         }
      }

      return lines.ToArray();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Opens a TCP connection to an address and port and closes it again, within a few seconds.
   /// </summary>
   /// <param name="ip">Address.</param>
   /// <param name="port">Port.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when the connection was accepted.</returns>
   private static async Task<bool> CanConnectAsync( string ip, int port, CancellationToken ct )
   {
      using var limit = CancellationTokenSource.CreateLinkedTokenSource( ct );
      limit.CancelAfter( TimeSpan.FromSeconds( 5 ) );
      try
      {
         using var client = new System.Net.Sockets.TcpClient();
         await client.ConnectAsync( ip, port, limit.Token );
         return true;
      }
      catch( Exception ex ) when( ex is System.Net.Sockets.SocketException or OperationCanceledException )
      {
         return false;
      }
   }

   /// <summary>
   /// Builds one engine's target, binds it, and compares the sink's addresses with what the compose
   /// file and the sink's default options say they should be.
   /// </summary>
   /// <param name="factory">Factory with the fake Docker.</param>
   /// <param name="catalog">Every sink with its default options.</param>
   /// <param name="compose">Containers and ports from the compose files.</param>
   /// <param name="addresses">Fake container address per container.</param>
   /// <param name="name">Sink name.</param>
   /// <returns>"engine|ok|..." or "engine|FAIL|...".</returns>
   private static async Task<string> CheckEngineAsync( TargetFactory factory, IReadOnlyDictionary<string, ISink> catalog, Dictionary<string, ComposeContainer> compose, Dictionary<string, string> addresses, string name )
   {
      EngineRoute route = ContainerRoutes.Find( name )!;
      string container = route.Containers.Single();
      if( !compose.TryGetValue( container, out ComposeContainer? declared ) )
      {
         return $"{name}|FAIL|no container {container} in the compose files";
      }

      BenchTarget target = factory.Create( name );
      ISink bound = await target.BindAsync( CancellationToken.None );
      string[] defaults = Endpoints( catalog[name] ).Select( e => RouteHostEndpoint( name, e ) ).ToArray();
      string[] wanted = defaults.Select( e => ExpectedEndpoint( e, declared, addresses[container] ) ).ToArray();
      string[] actual = Endpoints( bound ).OrderBy( e => e, StringComparer.Ordinal ).ToArray();
      bool same = actual.SequenceEqual( wanted.OrderBy( e => e, StringComparer.Ordinal ) ) && actual.Length > 0;
      bool noProxy = actual.All( e => !e.StartsWith( "127.0.0.1:", StringComparison.Ordinal ) && !e.StartsWith( "localhost:", StringComparison.Ordinal ) );
      string remapped = string.Join( " ", defaults.Zip( wanted, ( d, w ) => ( Host: d.Split( ':' )[1], Container: w.Split( ':' )[1] ) ).Where( p => p.Host != p.Container ).Select( p => $"{p.Host}->{p.Container}" ) );
      string verdict = same && noProxy && target.Connections.Count == actual.Length ? "ok" : "FAIL";
      return $"{name}|{verdict}|default {string.Join( ",", defaults )}|expected {string.Join( ",", wanted )}|actual {string.Join( ",", actual )}|recorded {target.Connections.Count}|remapped {( remapped.Length == 0 ? "none" : remapped )}";
   }

   /// <summary>
   /// The host endpoint a route asks Docker for: the sink's own default, except for MariaDB, whose
   /// default (127.0.0.1:3306) is the daily container's and whose benchmark container publishes
   /// another host port (see deploy/engines/mariadb-bench.compose.yaml), which the route sets itself.
   /// </summary>
   /// <param name="sinkName">Sink name.</param>
   /// <param name="defaultEndpoint">host:port from the sink's default options.</param>
   /// <returns>host:port the route maps to the container's own port.</returns>
   private static string RouteHostEndpoint( string sinkName, string defaultEndpoint )
   {
      return sinkName == MariaBench.TARGET ? $"{defaultEndpoint.Split( ':' )[0]}:{MariaBench.HOST_PORT}" : defaultEndpoint;
   }

   /// <summary>
   /// The address a default endpoint ("127.0.0.1:9201") must become: the container's address and the
   /// container port the compose file maps that host port to.
   /// </summary>
   /// <param name="defaultEndpoint">host:port from the sink's default options.</param>
   /// <param name="declared">The container as the compose file declares it.</param>
   /// <param name="ip">The fake container address.</param>
   /// <returns>host:port.</returns>
   private static string ExpectedEndpoint( string defaultEndpoint, ComposeContainer declared, string ip )
   {
      int hostPort = int.Parse( defaultEndpoint.Split( ':' )[1] );
      return $"{ip}:{declared.Ports.Single( p => p.Host == hostPort ).Container}";
   }

   /// <summary>
   /// Every host:port a sink's options point at, read from the sink's own options object: Host with Port,
   /// and each of the URL settings.
   /// </summary>
   /// <param name="sink">The sink.</param>
   /// <returns>The endpoints.</returns>
   private static List<string> Endpoints( ISink sink )
   {
      object options = sink.GetType().GetField( "_options", BindingFlags.NonPublic | BindingFlags.Instance )!.GetValue( sink )!;
      var found = new List<string>();
      string? host = null;
      int? port = null;
      foreach( PropertyInfo property in options.GetType().GetProperties() )
      {
         object? value = property.GetValue( options );
         if( property.Name == "Host" )
         {
            host = value as string;
         }
         else if( property.Name == "Port" )
         {
            port = value as int?;
         }
         else if( property.Name is "BaseUrl" or "QueryUrl" or "ConfigUrl" or "ManagementUrl" && value is string url )
         {
            var uri = new Uri( url );
            found.Add( $"{uri.Host}:{uri.Port}" );
         }
      }

      if( host != null && port != null )
      {
         found.Add( $"{host}:{port}" );
      }

      return found;
   }

   /// <summary>
   /// Reads the containers and published ports out of every compose file in deploy/engines. A port
   /// line belongs to the container_name above it.
   /// </summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <returns>Containers by name.</returns>
   private static Dictionary<string, ComposeContainer> ReadCompose( string repoRoot )
   {
      var containers = new Dictionary<string, ComposeContainer>( StringComparer.Ordinal );
      foreach( string file in Directory.EnumerateFiles( Path.Combine( repoRoot, "deploy", "engines" ), "*.compose.yaml" ) )
      {
         ComposeContainer? current = null;
         foreach( string line in File.ReadAllLines( file ) )
         {
            Match name = COMPOSE_NAME.Match( line );
            Match port = COMPOSE_PORT.Match( line );
            if( name.Success )
            {
               current = new ComposeContainer( name.Groups["name"].Value, new List<( int, int )>() );
               containers[current.Name] = current;
            }
            else if( port.Success && current != null )
            {
               current.Ports.Add( ( int.Parse( port.Groups["host"].Value ), int.Parse( port.Groups["container"].Value ) ) );
            }
         }
      }

      return containers;
   }

   /// <summary>
   /// Runs searches on one route for a few seconds and, three times while they run, reads the socket
   /// table and says where this process's sockets lead.
   /// </summary>
   /// <param name="label">Route name for the lines.</param>
   /// <param name="sink">The sink on that route.</param>
   /// <param name="collection">Collection with data.</param>
   /// <param name="data">The loaded records (their vectors are the queries).</param>
   /// <param name="peerIp">Address the route is meant to reach.</param>
   /// <param name="peerPort">Port the route is meant to reach.</param>
   /// <param name="proxiedEndpoint">The published address docker-proxy serves (host:port).</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>One line per sample, then one line of search counts.</returns>
   private static async Task<List<string>> ProbeRouteAsync( string label, ISink sink, string collection, VectorRecord[] data, string peerIp, int peerPort, string proxiedEndpoint, CancellationToken ct )
   {
      var lines = new List<string>();
      var tally = new SearchTally();
      using var stop = CancellationTokenSource.CreateLinkedTokenSource( ct );
      Task[] searchers = Enumerable.Range( 0, 4 ).Select( i => Task.Run( () => SearchLoopAsync( sink, collection, data, i, tally, stop.Token ), ct ) ).ToArray();
      try
      {
         await Task.Delay( 800, ct );
         for( int sample = 1; sample <= 3; sample++ )
         {
            lines.Add( $"{label}|sample {sample}|" + await AnalyseSocketsAsync( peerIp, peerPort, proxiedEndpoint, ct ) );
            await Task.Delay( 500, ct );
         }
      }
      finally
      {
         stop.Cancel();
         await Task.WhenAll( searchers );
      }

      lines.Add( $"{label}|searches|completed {tally.Completed}|failed {tally.Failed}{( tally.FirstError == null ? string.Empty : "|first error " + tally.FirstError )}" );
      return lines;
   }

   /// <summary>
   /// Searches back to back until stopped, counting what completed and what failed.
   /// </summary>
   /// <param name="sink">The sink.</param>
   /// <param name="collection">Collection.</param>
   /// <param name="data">Records whose vectors are the queries.</param>
   /// <param name="offset">Where in the data this searcher starts.</param>
   /// <param name="tally">Where the counts go.</param>
   /// <param name="ct">Stops the loop.</param>
   private static async Task SearchLoopAsync( ISink sink, string collection, VectorRecord[] data, int offset, SearchTally tally, CancellationToken ct )
   {
      for( int i = offset; !ct.IsCancellationRequested; i++ )
      {
         try
         {
            await sink.SearchAsync( collection, data[i % data.Length].Vector, 10, ct );
            Interlocked.Increment( ref tally.Completed );
         }
         catch( OperationCanceledException ) when( ct.IsCancellationRequested )
         {
            return;
         }
         catch( Exception ex )
         {
            Interlocked.Increment( ref tally.Failed );
            tally.FirstError ??= ex.Message;
            await Task.Delay( 50, CancellationToken.None );
         }
      }
   }

   /// <summary>
   /// Reads the socket table once and summarises it against the route this process is meant to use.
   /// </summary>
   /// <param name="peerIp">Address the route is meant to reach.</param>
   /// <param name="peerPort">Port the route is meant to reach.</param>
   /// <param name="proxiedEndpoint">The published address docker-proxy serves (host:port).</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The summary.</returns>
   private static async Task<string> AnalyseSocketsAsync( string peerIp, int peerPort, string proxiedEndpoint, CancellationToken ct )
   {
      ShellResult result = await Shell.RunAsync( "sudo", new[] { "-n", "ss", "-tnpH", "state", "established" }, TimeSpan.FromSeconds( 20 ), ct );
      if( result.ExitCode != 0 )
      {
         return $"ss failed ({result.ExitCode}): {result.Error.Trim()}";
      }

      List<SocketRow> all = Sockets( result.Output );
      int me = Environment.ProcessId;
      List<SocketRow> ours = all.Where( s => s.Users.Any( u => u.Pid == me ) ).ToList();
      int toRoute = ours.Count( s => s.Peer == $"{peerIp}:{peerPort}" );
      int viaProxy = ours.Count( s => s.Peer == proxiedEndpoint );
      var ourLocals = ours.Select( s => s.Local ).ToHashSet( StringComparer.Ordinal );
      int proxyFacingUs = all.Count( s => s.Users.Any( u => u.Name == "docker-proxy" ) && ourLocals.Contains( s.Peer ) );
      return $"ours {ours.Count}|to {peerIp}:{peerPort} {toRoute}|to {proxiedEndpoint} {viaProxy}|docker-proxy sockets facing ours {proxyFacingUs}";
   }

   /// <summary>
   /// Times single searches on both routes in alternating rounds.
   /// </summary>
   /// <param name="direct">Sink on the container address.</param>
   /// <param name="proxied">Sink on the published port.</param>
   /// <param name="collection">Collection.</param>
   /// <param name="data">Records whose vectors are the queries.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Median per round and overall, as evidence.</returns>
   private static async Task<List<string>> TimeRoutesAsync( ISink direct, ISink proxied, string collection, VectorRecord[] data, CancellationToken ct )
   {
      var lines = new List<string>();
      var samples = new Dictionary<string, List<double>> { ["direct"] = new(), ["proxied"] = new() };
      for( int round = 1; round <= 4; round++ )
      {
         foreach( ( string label, ISink sink ) in round % 2 == 1 ? new[] { ( "direct", direct ), ( "proxied", proxied ) } : new[] { ( "proxied", proxied ), ( "direct", direct ) } )
         {
            List<double> times = await TimeSearchesAsync( sink, collection, data, 300, ct );
            samples[label].AddRange( times );
            lines.Add( $"timing|round {round}|{label}|p50 {Median( times ):0.000} ms" );
         }
      }

      lines.Add( $"timing|all rounds|direct p50 {Median( samples["direct"] ):0.000} ms|proxied p50 {Median( samples["proxied"] ):0.000} ms|{samples["direct"].Count} searches each, one at a time, no CPU pinning" );
      return lines;
   }

   /// <summary>
   /// Times sequential searches after a short warm-up.
   /// </summary>
   /// <param name="sink">The sink.</param>
   /// <param name="collection">Collection.</param>
   /// <param name="data">Records whose vectors are the queries.</param>
   /// <param name="count">Timed searches.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Milliseconds per search.</returns>
   private static async Task<List<double>> TimeSearchesAsync( ISink sink, string collection, VectorRecord[] data, int count, CancellationToken ct )
   {
      for( int i = 0; i < 40; i++ )
      {
         await sink.SearchAsync( collection, data[i % data.Length].Vector, 10, ct );
      }

      var times = new List<double>( count );
      for( int i = 0; i < count; i++ )
      {
         long start = Stopwatch.GetTimestamp();
         await sink.SearchAsync( collection, data[( i * 7 ) % data.Length].Vector, 10, ct );
         times.Add( Stopwatch.GetElapsedTime( start ).TotalMilliseconds );
      }

      return times;
   }

   /// <summary>
   /// Median of a list.
   /// </summary>
   /// <param name="values">Values.</param>
   /// <returns>The median.</returns>
   private static double Median( List<double> values )
   {
      List<double> sorted = values.OrderBy( v => v ).ToList();
      return sorted[sorted.Count / 2];
   }

   /// <summary>
   /// Parses the sockets in "ss -tnpH" text: local and peer address, and the processes holding the socket.
   /// </summary>
   /// <param name="text">The text.</param>
   /// <returns>The sockets.</returns>
   private static List<SocketRow> Sockets( string text )
   {
      var rows = new List<SocketRow>();
      foreach( string line in text.Split( '\n', StringSplitOptions.RemoveEmptyEntries ) )
      {
         Match match = SOCKET_LINE.Match( line );
         if( match.Success )
         {
            List<( string Name, int Pid )> users = USER.Matches( match.Groups["users"].Value ).Select( m => ( m.Groups["name"].Value, int.Parse( m.Groups["pid"].Value ) ) ).ToList();
            rows.Add( new SocketRow( Plain( match.Groups["local"].Value ), Plain( match.Groups["peer"].Value ), users ) );
         }
      }

      return rows;
   }

   /// <summary>
   /// Turns an IPv4-mapped IPv6 address ("[::ffff:127.0.0.1]:3306") into plain "127.0.0.1:3306".
   /// </summary>
   /// <param name="address">The address as ss prints it.</param>
   /// <returns>The plain address.</returns>
   private static string Plain( string address )
   {
      return address.Replace( "[::ffff:", string.Empty ).Replace( "]:", ":" );
   }

   /// <summary>
   /// Runs an action and describes how it ended.
   /// </summary>
   /// <typeparam name="T">Result type, ignored.</typeparam>
   /// <param name="action">The action.</param>
   /// <returns>"returned" or "ExceptionType: message".</returns>
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

   /// <summary>
   /// Random unit vectors wrapped as records.
   /// </summary>
   /// <param name="count">Records.</param>
   /// <param name="dimension">Dimension.</param>
   /// <param name="seed">Random seed.</param>
   /// <returns>The records.</returns>
   private static VectorRecord[] Make( int count, int dimension, int seed )
   {
      var random = new Random( seed );
      var records = new VectorRecord[count];
      for( int i = 0; i < count; i++ )
      {
         float[] vector = Enumerable.Range( 0, dimension ).Select( _ => (float)( random.NextDouble() * 2 - 1 ) ).ToArray();
         float norm = MathF.Sqrt( vector.Sum( v => v * v ) );
         vector = vector.Select( v => v / norm ).ToArray();
         var document = new Document( $"file{i % 50}.cs", "files", "direct-test", $"text {i}", "h", new Dictionary<string, string>() );
         records[i] = new VectorRecord( document, new Chunk( new Guid( i + 1, 0, 0, new byte[8] ), document.DocKey, i, $"chunk text {i}" ), vector );
      }

      return records;
   }

   #endregion Private Methods

   #region Nested Types

   /// <summary>Answers docker inspect from a script, counting every ask.</summary>
   private sealed class FakeInspector : IContainerInspector
   {
      private readonly Dictionary<string, List<string?>> _answers;
      private readonly Dictionary<string, int> _asks = new();

      /// <summary>Creates the fake.</summary>
      /// <param name="answers">Per container, the answers in order (the last repeats); a container not listed does not exist.</param>
      public FakeInspector( Dictionary<string, List<string?>> answers )
      {
         _answers = answers;
      }

      /// <summary>Total asks so far.</summary>
      public int Calls
      {
         get
         {
            lock( _asks )
            {
               return _asks.Values.Sum();
            }
         }
      }

      /// <summary>Answers one ask.</summary>
      /// <param name="container">Container name.</param>
      /// <param name="ct">Cancellation.</param>
      /// <returns>The scripted answer.</returns>
      public Task<string?> InspectAsync( string container, CancellationToken ct )
      {
         lock( _asks )
         {
            int count = _asks.GetValueOrDefault( container );
            _asks[container] = count + 1;
            return Task.FromResult( _answers.TryGetValue( container, out List<string?>? list ) ? list[Math.Min( count, list.Count - 1 )] : null );
         }
      }
   }

   /// <summary>Counts of searches that completed and failed, shared by the searcher tasks.</summary>
   private sealed class SearchTally
   {
      /// <summary>Searches that returned.</summary>
      public int Completed;

      /// <summary>Searches that threw.</summary>
      public int Failed;

      /// <summary>Message of the first failure, if any.</summary>
      public string? FirstError;
   }

   /// <summary>A container as a compose file declares it.</summary>
   /// <param name="Name">container_name.</param>
   /// <param name="Ports">Published (host, container) ports.</param>
   private sealed record ComposeContainer( string Name, List<( int Host, int Container )> Ports );

   /// <summary>One line of ss output.</summary>
   /// <param name="Local">Local host:port.</param>
   /// <param name="Peer">Peer host:port.</param>
   /// <param name="Users">Processes holding the socket.</param>
   private sealed record SocketRow( string Local, string Peer, List<( string Name, int Pid )> Users );

   #endregion Nested Types
}
#endif
