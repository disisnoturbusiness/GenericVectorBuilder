using System.Globalization;
using System.Net;
using GenericVectorBuilder.Bench.Targets;

namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// How the client reached one target, written into results.json as conditions.connections: the
/// route, every address and port the sink was configured with, and the TCP connections this
/// process actually held open to anything right after the target's timed passes.
/// Why both: the configured address says what was meant, the open connections prove what was
/// used. A compose engine reached through its published 127.0.0.1 port goes through the
/// userland docker-proxy, an extra copy of every byte that the review measured as added latency;
/// a number is only comparable with the native SQL Server's and Qdrant's when the route is on
/// record next to it.
/// </summary>
public sealed class TargetConnection
{
   #region Public Methods

   /// <summary>Target name.</summary>
   public string Target { get; set; } = string.Empty;

   /// <summary>compose, always-on or embedded.</summary>
   public string Hosting { get; set; } = string.Empty;

   /// <summary>The route in words, e.g. "container address on its Docker network (no docker-proxy)".</summary>
   public string Route { get; set; } = string.Empty;

   /// <summary>Every address and port the sink was configured with.</summary>
   public List<ConnectionEndpoint> Endpoints { get; set; } = new();

   /// <summary>Connections this process held open right after the timed passes; empty when the target was not searched.</summary>
   public List<ObservedPeer> Observed { get; set; } = new();

   /// <summary>When the open connections were read (UTC), or null when they were not.</summary>
   public string? ObservedUtc { get; set; }

   /// <summary>What could not be read, or null.</summary>
   public string? Problem { get; set; }

   #endregion Public Methods
}

/// <summary>
/// One configured address of a target.
/// </summary>
public sealed class ConnectionEndpoint
{
   #region Public Methods

   /// <summary>What it is, e.g. "SQL Server" or "container gvb-mariadb".</summary>
   public string What { get; set; } = string.Empty;

   /// <summary>Host name or IP address.</summary>
   public string Address { get; set; } = string.Empty;

   /// <summary>TCP port, or null when the setting does not name one (a named instance).</summary>
   public int? Port { get; set; }

   /// <summary>Container id (12 characters), for a container.</summary>
   public string? ContainerId { get; set; }

   /// <summary>Docker network the address is on, for a container.</summary>
   public string? Network { get; set; }

   /// <summary>The published host address and port that docker-proxy serves and the sink did NOT use, for a container.</summary>
   public string? NotUsed { get; set; }

   #endregion Public Methods
}

/// <summary>
/// One remote address this process had open TCP connections to.
/// </summary>
public sealed class ObservedPeer
{
   #region Public Methods

   /// <summary>Remote IP address (IPv4-mapped IPv6 shown as IPv4).</summary>
   public string Address { get; set; } = string.Empty;

   /// <summary>Remote port.</summary>
   public int Port { get; set; }

   /// <summary>Open (ESTABLISHED) sockets to it.</summary>
   public int Sockets { get; set; }

   /// <summary>"this target", "docker-proxy (published port)" or "not this target".</summary>
   public string Kind { get; set; } = string.Empty;

   #endregion Public Methods
}

/// <summary>
/// Builds <see cref="TargetConnection"/> records: the configured endpoints from the target (a
/// container's address read from Docker) or from the settings (the always-on SQL Server and
/// Qdrant), and the open connections from /proc. Reads only.
/// </summary>
public sealed class ConnectionRecorder
{
   #region Data Members

   /// <summary>Qdrant's HTTP port; the factory reaches the same server on it (TargetFactory.QDRANT_HTTP_PORT).</summary>
   public const int QDRANT_HTTP_PORT = 6333;

   private const int SQL_DEFAULT_PORT = 1433;
   private const string ESTABLISHED = "01";

   private readonly IMachineSystem _system;
   private readonly string _sqlServer;
   private readonly string _qdrantHost;
   private readonly int _qdrantGrpcPort;
   private readonly Dictionary<string, (List<ObservedPeer> Peers, string When, string? Problem)> _observed = new( StringComparer.Ordinal );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the recorder.
   /// </summary>
   /// <param name="system">The machine (/proc reads).</param>
   /// <param name="sqlServer">The SQL Server setting (a data source such as "localhost" or "host,1433").</param>
   /// <param name="qdrantHost">The Qdrant host setting.</param>
   /// <param name="qdrantGrpcPort">The Qdrant gRPC port setting.</param>
   public ConnectionRecorder( IMachineSystem system, string sqlServer, string qdrantHost, int qdrantGrpcPort )
   {
      _system = system;
      _sqlServer = sqlServer;
      _qdrantHost = qdrantHost;
      _qdrantGrpcPort = qdrantGrpcPort;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Reads the connections this process holds open now and keeps them for the target. Called
   /// right after the target's last timed pass, while its pooled connections are still open.
   /// Never throws: a failed read is kept as the record's problem.
   /// </summary>
   /// <param name="target">The target just searched.</param>
   public void Observe( BenchTarget target )
   {
      string when = DateTime.UtcNow.ToString( "yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture );
      try
      {
         List<ObservedPeer> peers = EstablishedPeers( _system, _system.ProcessId ).Select( p => new ObservedPeer { Address = p.Key.Address, Port = p.Key.Port, Sockets = p.Value } ).ToList();
         _observed[target.Name] = ( peers, when, peers.Count == 0 ? "no open TCP connection was found" : null );
      }
      catch( Exception ex ) when( ex is FormatException or IOException or IndexOutOfRangeException or OverflowException or ArgumentException )
      {
         _observed[target.Name] = ( new List<ObservedPeer>(), when, $"could not read the open connections: {ex.Message}" );
      }
   }

   /// <summary>
   /// The full record of one target: route, configured endpoints, and what <see cref="Observe"/>
   /// saw (each peer marked as this target's endpoint, its docker-proxy port, or something else).
   /// </summary>
   /// <param name="target">The target.</param>
   /// <returns>The record.</returns>
   public TargetConnection Describe( BenchTarget target )
   {
      var record = new TargetConnection { Target = target.Name, Hosting = target.Hosting };
      FillEndpoints( record, target );
      if( _observed.TryGetValue( target.Name, out var seen ) )
      {
         record.Observed = seen.Peers.OrderByDescending( p => p.Sockets ).ThenBy( p => p.Address, StringComparer.Ordinal ).ThenBy( p => p.Port ).ToList();
         record.Observed.ForEach( p => p.Kind = Classify( p, record.Endpoints ) );
         record.ObservedUtc = seen.When;
         record.Problem ??= seen.Problem;
      }

      return record;
   }

   /// <summary>
   /// One note for the target: route, endpoints, and the connections seen.
   /// </summary>
   /// <param name="c">The record.</param>
   /// <returns>The note.</returns>
   public static string Note( TargetConnection c )
   {
      string endpoints = c.Endpoints.Count == 0 ? "no network address" : string.Join( "; ", c.Endpoints.Select( Describe ) );
      string seen = c.ObservedUtc == null
         ? "Open connections not read (not searched)"
         : c.Observed.Count == 0 ? $"No open connection seen ({c.Problem})" : "Open after the passes: " + string.Join( ", ", c.Observed.Select( p => $"{p.Address}:{p.Port} x{p.Sockets} ({p.Kind})" ) );
      return $"Connection: {c.Route}: {endpoints}. {seen}.";
   }

   /// <summary>
   /// Remote endpoints of this process's ESTABLISHED TCP sockets (IPv4 and IPv6), with how many
   /// sockets each has. A socket counts as this process's when one of its file descriptors links
   /// to the socket's inode.
   /// </summary>
   /// <param name="system">The machine.</param>
   /// <param name="pid">Process id.</param>
   /// <returns>Sockets per remote endpoint.</returns>
   public static Dictionary<(string Address, int Port), int> EstablishedPeers( IMachineSystem system, int pid )
   {
      string prefix = string.Create( CultureInfo.InvariantCulture, $"/proc/{pid}" );
      HashSet<string> inodes = system.ListDirectory( prefix + "/fd" )
         .Select( fd => system.ReadLink( $"{prefix}/fd/{fd}" ) )
         .Where( link => link != null && link.StartsWith( "socket:[", StringComparison.Ordinal ) )
         .Select( link => link!["socket:[".Length..].TrimEnd( ']' ) ).ToHashSet( StringComparer.Ordinal );
      var peers = new Dictionary<(string Address, int Port), int>();
      foreach( string table in new[] { "/net/tcp", "/net/tcp6" } )
      {
         foreach( string line in ( system.ReadFile( prefix + table ) ?? string.Empty ).Split( '\n' ).Skip( 1 ) )
         {
            string[] f = line.Split( ' ', StringSplitOptions.RemoveEmptyEntries );
            if( f.Length > 9 && f[3] == ESTABLISHED && inodes.Contains( f[9] ) )
            {
               (string, int) remote = ParseEndpoint( f[2] );
               peers[remote] = peers.GetValueOrDefault( remote ) + 1;
            }
         }
      }

      return peers;
   }

   /// <summary>
   /// Decodes an address as /proc/net/tcp and tcp6 print it: "0100007F:0599" is 127.0.0.1:1433.
   /// The address is the in-memory bytes printed as 32-bit little-endian words; the port is
   /// plain hexadecimal. An IPv4-mapped IPv6 address comes back as IPv4.
   /// </summary>
   /// <param name="hex">"ADDRESS:PORT" in hexadecimal.</param>
   /// <returns>Address text and port.</returns>
   /// <exception cref="FormatException">Not in that form.</exception>
   public static (string Address, int Port) ParseEndpoint( string hex )
   {
      string[] parts = hex.Split( ':' );
      if( parts.Length != 2 || parts[0].Length is not ( 8 or 32 ) )
      {
         throw new FormatException( $"'{hex}' is not an address as /proc/net/tcp prints it." );
      }

      var bytes = new byte[parts[0].Length / 2];
      for( int word = 0; word < parts[0].Length / 8; word++ )
      {
         uint value = uint.Parse( parts[0].Substring( word * 8, 8 ), NumberStyles.HexNumber, CultureInfo.InvariantCulture );
         BitConverter.GetBytes( value ).CopyTo( bytes, word * 4 );
         if( !BitConverter.IsLittleEndian )
         {
            Array.Reverse( bytes, word * 4, 4 );
         }
      }

      var address = new IPAddress( bytes );
      address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
      return ( address.ToString(), int.Parse( parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture ) );
   }

   /// <summary>
   /// Splits a SQL Server data source into host and port: "tcp:host,1500" is host and 1500,
   /// "host" is host and 1433, "host\instance" is host with no known port.
   /// </summary>
   /// <param name="dataSource">The setting.</param>
   /// <returns>Host and port.</returns>
   public static (string Host, int? Port) SqlEndpoint( string dataSource )
   {
      string text = dataSource.Trim();
      text = text.StartsWith( "tcp:", StringComparison.OrdinalIgnoreCase ) ? text[4..] : text;
      int comma = text.IndexOf( ',' );
      if( comma >= 0 )
      {
         return ( text[..comma].Trim(), int.TryParse( text[( comma + 1 )..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port ) ? port : null );
      }

      return text.Contains( '\\' ) ? ( text[..text.IndexOf( '\\' )], null ) : ( text, SQL_DEFAULT_PORT );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Fills the route and the configured endpoints for the target's hosting.
   /// </summary>
   /// <param name="record">The record.</param>
   /// <param name="target">The target.</param>
   private void FillEndpoints( TargetConnection record, BenchTarget target )
   {
      if( target.Hosting == "embedded" )
      {
         record.Route = "in this process (embedded), no network";
         return;
      }

      if( target.Container != null )
      {
         record.Route = "container address on its Docker network, not the published port (no docker-proxy)";
         record.Endpoints = target.Connections.Select( a => new ConnectionEndpoint
         {
            What = $"container {a.Container}", Address = a.Ip, Port = a.ContainerPort, ContainerId = a.ContainerId, Network = a.Network, NotUsed = a.ProxiedEndpoint,
         } ).ToList();
         record.Problem = record.Endpoints.Count == 0 ? "the container's address was never read (the target failed before it connected)" : null;
         return;
      }

      record.Route = "native service on this host, reached directly (no container, no docker-proxy)";
      switch( EnginePinner.Kind( target.Name, target.Hosting, target.ComposePath ) )
      {
         case "sql":
            ( string host, int? port ) = SqlEndpoint( _sqlServer );
            record.Endpoints.Add( new ConnectionEndpoint { What = "SQL Server", Address = host, Port = port } );
            break;
         case "qdrant":
            record.Endpoints.Add( new ConnectionEndpoint { What = "Qdrant gRPC", Address = _qdrantHost, Port = _qdrantGrpcPort } );
            record.Endpoints.Add( new ConnectionEndpoint { What = "Qdrant HTTP", Address = _qdrantHost, Port = QDRANT_HTTP_PORT } );
            break;
         default:
            record.Problem = $"no known address for always-on target {target.Name}: {target.ConnectionText}";
            break;
      }
   }

   /// <summary>
   /// What an open connection was: one of the target's endpoints, a container's published port
   /// (served by docker-proxy), or something else of the client's (the source database).
   /// </summary>
   /// <param name="peer">The connection's remote end.</param>
   /// <param name="endpoints">The target's endpoints.</param>
   /// <returns>The kind.</returns>
   private static string Classify( ObservedPeer peer, IReadOnlyList<ConnectionEndpoint> endpoints )
   {
      if( endpoints.Any( e => e.Port == peer.Port && SameHost( e.Address, peer.Address ) ) )
      {
         return "this target";
      }

      bool proxied = endpoints.Any( e => e.NotUsed is string published && int.TryParse( published[( published.LastIndexOf( ':' ) + 1 )..], NumberStyles.None, CultureInfo.InvariantCulture, out int port )
         && port == peer.Port && SameHost( published[..published.LastIndexOf( ':' )], peer.Address ) );
      return proxied ? "docker-proxy (published port)" : "not this target";
   }

   /// <summary>
   /// True when two host texts name the same host: equal, or both loopback (localhost,
   /// 127.0.0.0/8, ::1), or "0.0.0.0" (published on every address) against loopback.
   /// </summary>
   /// <param name="configured">The configured host.</param>
   /// <param name="seen">The address seen.</param>
   /// <returns>True when the same.</returns>
   private static bool SameHost( string configured, string seen )
   {
      static bool Loopback( string h ) => h is "localhost" or "0.0.0.0" or "::" || ( IPAddress.TryParse( h.Trim( '[', ']' ), out IPAddress? ip ) && IPAddress.IsLoopback( ip ) );
      return string.Equals( configured, seen, StringComparison.OrdinalIgnoreCase ) || ( Loopback( configured ) && Loopback( seen ) );
   }

   /// <summary>
   /// One endpoint as text.
   /// </summary>
   /// <param name="e">The endpoint.</param>
   /// <returns>e.g. "container gvb-mariadb (b115140380d0) on network x at 172.18.0.2:3306, not 127.0.0.1:3306".</returns>
   private static string Describe( ConnectionEndpoint e )
   {
      string port = e.Port?.ToString( CultureInfo.InvariantCulture ) ?? "default port";
      return e.ContainerId == null
         ? $"{e.What} at {e.Address}:{port}"
         : $"{e.What} ({e.ContainerId}) on network {e.Network} at {e.Address}:{port}, not through docker-proxy {e.NotUsed}";
   }

   #endregion Private Methods
}
