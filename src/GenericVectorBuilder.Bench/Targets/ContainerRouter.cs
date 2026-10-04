using System.Diagnostics;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// Turns the address a sink would use by default (127.0.0.1 and a published host port) into the
/// container's own address and the port inside the container, from the facts "docker inspect"
/// gave. Also remembers every address it handed out, which is what the report records.
/// Why the host port is the key: every sink's default address is its container's published
/// host port (OpenSearch 9201, Vespa 8090, Weaviate 8085 are not the container's own ports), and
/// the compose file and the container's port table are the only truth for what each stands for.
/// Nothing here guesses: a container that is not running, has no address, or does not publish
/// the port asked for stops the target with a plain message, because the alternative is to fall
/// back to the proxied route without saying so.
/// </summary>
public sealed class ContainerRouter
{
   #region Data Members

   private readonly Dictionary<string, ContainerFacts> _facts;
   private readonly List<ContainerAddress> _used = new();

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a router over facts already read.
   /// </summary>
   /// <param name="facts">One entry per container, keyed by its name.</param>
   public ContainerRouter( IEnumerable<ContainerFacts> facts )
   {
      _facts = facts.ToDictionary( f => f.Name, StringComparer.Ordinal );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Every address handed out so far, without repeats, in the order first asked for.</summary>
   public IReadOnlyList<ContainerAddress> Used => _used;

   /// <summary>
   /// Asks Docker about each container and builds a router. A container that exists but is not
   /// running yet (compose reports healthy before the last network step in rare cases) is asked
   /// again every <paramref name="poll"/> until <paramref name="deadline"/>; a container that does
   /// not exist at all fails at once, because waiting cannot create it.
   /// </summary>
   /// <param name="inspector">Asks Docker.</param>
   /// <param name="containers">Container names to read.</param>
   /// <param name="deadline">Longest to wait for all of them to be running with an address.</param>
   /// <param name="poll">Pause between asks.</param>
   /// <param name="ct">Cancellation of the whole run.</param>
   /// <returns>The router.</returns>
   /// <exception cref="InvalidOperationException">A container is missing, not running, has no address, or Docker could not be asked.</exception>
   public static async Task<ContainerRouter> ResolveAsync( IContainerInspector inspector, IEnumerable<string> containers, TimeSpan deadline, TimeSpan poll, CancellationToken ct )
   {
      var clock = Stopwatch.StartNew();
      var facts = new List<ContainerFacts>();
      foreach( string name in containers )
      {
         facts.Add( await ReadOneAsync( inspector, name, clock, deadline, poll, ct ) );
      }

      return new ContainerRouter( facts );
   }

   /// <summary>
   /// The container's own address for a sink's default host port.
   /// </summary>
   /// <param name="container">Container name.</param>
   /// <param name="hostPort">The host port the sink's default address uses (what compose publishes).</param>
   /// <returns>The address: container IP and the port inside the container.</returns>
   /// <exception cref="InvalidOperationException">The container is unknown, has no address, or does not publish that host port.</exception>
   public ContainerAddress Address( string container, int hostPort )
   {
      if( !_facts.TryGetValue( container, out ContainerFacts? facts ) )
      {
         throw new InvalidOperationException( $"No facts for container {container}; known: {string.Join( ", ", _facts.Keys )}." );
      }

      PublishedPort? port = facts.Ports.FirstOrDefault( p => p.HostPort == hostPort && p.Protocol == "tcp" );
      if( port == null )
      {
         string published = facts.Ports.Count == 0 ? "none" : string.Join( ", ", facts.Ports.Select( p => $"{p.HostIp}:{p.HostPort} to {p.ContainerPort}/{p.Protocol}" ) );
         throw new InvalidOperationException( $"Container {container} does not publish host port {hostPort}, so the container-side port is unknown (published: {published})." );
      }

      ContainerNetwork network = facts.Networks.FirstOrDefault()
         ?? throw new InvalidOperationException( $"Container {container} has no address on any Docker network." );
      var address = new ContainerAddress( container, facts.Id, network.Name, network.Ip, port.ContainerPort, port.HostIp, port.HostPort );
      if( !_used.Contains( address ) )
      {
         _used.Add( address );
      }

      return address;
   }

   /// <summary>
   /// Rewrites a sink's default URL (for example "http://127.0.0.1:9201") to the container's own
   /// address and port, keeping the scheme and any path.
   /// </summary>
   /// <param name="container">Container name.</param>
   /// <param name="publishedUrl">The sink's default URL; its port is the published host port.</param>
   /// <returns>The URL on the container's address.</returns>
   /// <exception cref="InvalidOperationException">See <see cref="Address"/>.</exception>
   public string Url( string container, string publishedUrl )
   {
      var uri = new Uri( publishedUrl );
      ContainerAddress address = Address( container, uri.Port );
      string rest = uri.PathAndQuery == "/" ? string.Empty : uri.PathAndQuery;
      return $"{uri.Scheme}://{address.Endpoint}{rest}";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads one container, waiting while it exists but is not yet running with an address.
   /// </summary>
   /// <param name="inspector">Asks Docker.</param>
   /// <param name="name">Container name.</param>
   /// <param name="clock">Runs from the start of the whole resolve.</param>
   /// <param name="deadline">Longest to wait in total.</param>
   /// <param name="poll">Pause between asks.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The facts of a running container with an address.</returns>
   private static async Task<ContainerFacts> ReadOneAsync( IContainerInspector inspector, string name, Stopwatch clock, TimeSpan deadline, TimeSpan poll, CancellationToken ct )
   {
      while( true )
      {
         string json = await inspector.InspectAsync( name, ct )
            ?? throw new InvalidOperationException( $"There is no container named {name}. Start its engine first (sudo docker compose -f deploy/engines/<engine>.compose.yaml up -d, or use run-all)." );
         ContainerFacts facts = ContainerFacts.Parse( json );
         if( facts.Running && facts.Networks.Count > 0 )
         {
            return facts;
         }

         if( clock.Elapsed + poll > deadline )
         {
            string address = facts.Networks.Count == 0 ? "no network address" : $"address {facts.Networks[0].Ip}";
            throw new InvalidOperationException( $"Container {name} is not ready after waiting {clock.Elapsed.TotalSeconds:0} s: state {facts.Status}, {address}. It must be running with an address before it is measured." );
         }

         await Task.Delay( poll, ct );
      }
   }

   #endregion Private Methods
}
