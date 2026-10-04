using System.Text.Json;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// What the benchmark needs to know about one running container, read from the text of
/// "docker inspect": whether it runs, its address on each Docker network, and which of its
/// ports are published to the host (host port to container port).
/// Why only these facts: the benchmark connects to the container's own address and port instead
/// of the published host port, because a published port on 127.0.0.1 is served by the
/// userland docker-proxy process, one extra hop that the always-on SQL Server and Qdrant never
/// pay. The published-port list is how the benchmark finds the container-side port that a
/// sink's default host port (for example OpenSearch's 9201) stands for (9200).
/// </summary>
/// <param name="Name">Container name without the leading slash, e.g. "gvb-mariadb".</param>
/// <param name="Id">First 12 characters of the container id, so a report names the exact container measured.</param>
/// <param name="Status">Docker's state text ("running", "exited", "created", "restarting").</param>
/// <param name="Running">True when the container is running.</param>
/// <param name="Networks">Every network the container is on that gave it an address, by network name.</param>
/// <param name="Ports">Every port published to the host.</param>
public sealed record ContainerFacts( string Name, string Id, string Status, bool Running, IReadOnlyList<ContainerNetwork> Networks, IReadOnlyList<PublishedPort> Ports )
{
   #region Public Methods

   /// <summary>
   /// Reads the text "docker inspect NAME" prints (a JSON array with one container).
   /// Why it throws a plain message: a half-read container must never become a guessed address,
   /// so anything unexpected stops the target with words, not a stack trace.
   /// </summary>
   /// <param name="json">The inspect output.</param>
   /// <returns>The facts.</returns>
   /// <exception cref="InvalidOperationException">The text is not an inspect result.</exception>
   public static ContainerFacts Parse( string json )
   {
      try
      {
         using JsonDocument document = JsonDocument.Parse( json );
         JsonElement root = document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0 ? document.RootElement[0] : document.RootElement;
         JsonElement state = root.GetProperty( "State" );
         string id = root.GetProperty( "Id" ).GetString() ?? string.Empty;
         string name = ( root.GetProperty( "Name" ).GetString() ?? string.Empty ).TrimStart( '/' );
         JsonElement settings = root.GetProperty( "NetworkSettings" );
         return new ContainerFacts( name, id.Length > 12 ? id[..12] : id, state.GetProperty( "Status" ).GetString() ?? "unknown", state.GetProperty( "Running" ).GetBoolean(),
            ReadNetworks( settings ), ReadPorts( settings ) );
      }
      catch( Exception ex ) when( ex is JsonException or KeyNotFoundException or InvalidOperationException or InvalidCastException )
      {
         throw new InvalidOperationException( $"docker inspect did not return a container description ({ex.Message})" );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads the container's address on each network, skipping a network with no address (a
   /// container that has not finished starting has none yet). Networks are sorted by name so the
   /// first one is the same on every run.
   /// </summary>
   /// <param name="settings">The NetworkSettings object.</param>
   /// <returns>The networks that have an address.</returns>
   private static List<ContainerNetwork> ReadNetworks( JsonElement settings )
   {
      var networks = new List<ContainerNetwork>();
      if( settings.TryGetProperty( "Networks", out JsonElement all ) && all.ValueKind == JsonValueKind.Object )
      {
         foreach( JsonProperty network in all.EnumerateObject() )
         {
            string ip = network.Value.TryGetProperty( "IPAddress", out JsonElement address ) ? address.GetString() ?? string.Empty : string.Empty;
            if( ip.Length > 0 )
            {
               networks.Add( new ContainerNetwork( network.Name, ip ) );
            }
         }
      }

      return networks.OrderBy( n => n.Name, StringComparer.Ordinal ).ToList();
   }

   /// <summary>
   /// Reads the published ports ("3306/tcp": [ { HostIp, HostPort } ]). A port that is exposed
   /// but not published has no binding and is left out: it cannot be what a sink's host port means.
   /// </summary>
   /// <param name="settings">The NetworkSettings object.</param>
   /// <returns>The published ports.</returns>
   private static List<PublishedPort> ReadPorts( JsonElement settings )
   {
      var ports = new List<PublishedPort>();
      if( !settings.TryGetProperty( "Ports", out JsonElement all ) || all.ValueKind != JsonValueKind.Object )
      {
         return ports;
      }

      foreach( JsonProperty port in all.EnumerateObject().Where( p => p.Value.ValueKind == JsonValueKind.Array ) )
      {
         string[] parts = port.Name.Split( '/' );
         foreach( JsonElement binding in port.Value.EnumerateArray() )
         {
            if( int.TryParse( parts[0], out int containerPort ) && int.TryParse( binding.GetProperty( "HostPort" ).GetString(), out int hostPort ) )
            {
               ports.Add( new PublishedPort( containerPort, parts.Length > 1 ? parts[1] : "tcp", binding.GetProperty( "HostIp" ).GetString() ?? string.Empty, hostPort ) );
            }
         }
      }

      return ports;
   }

   #endregion Private Methods
}

/// <summary>
/// A container's address on one Docker network.
/// </summary>
/// <param name="Name">Network name, e.g. "gvb-mariadb_default".</param>
/// <param name="Ip">The container's IPv4 address on it.</param>
public sealed record ContainerNetwork( string Name, string Ip );

/// <summary>
/// One container port published to the host.
/// </summary>
/// <param name="ContainerPort">Port inside the container.</param>
/// <param name="Protocol">"tcp" or "udp".</param>
/// <param name="HostIp">Host address it is bound to, e.g. "127.0.0.1".</param>
/// <param name="HostPort">Port on the host.</param>
public sealed record PublishedPort( int ContainerPort, string Protocol, string HostIp, int HostPort );
