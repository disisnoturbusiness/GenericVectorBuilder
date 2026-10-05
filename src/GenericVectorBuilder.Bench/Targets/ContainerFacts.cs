using System.Text.Json;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// What the benchmark needs to know about one running container, read from the text of
/// "docker inspect": whether it runs, its address on each Docker network, which of its ports
/// are published to the host (host port to container port), and the CPU set, image and
/// environment variable names it was created with.
/// Why only these facts: the benchmark connects to the container's own address and port instead
/// of the published host port, because a published port on 127.0.0.1 is served by the
/// userland docker-proxy process, one extra hop that some engines would pay and others not. The
/// published-port list is how the benchmark finds the container-side port that a
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
   /// The container's CPU set as Docker holds it (HostConfig.CpusetCpus), e.g. "2-3,6-7"; empty when
   /// the container may use every CPU. Why it is read: an engine sizes its thread pools from the CPUs
   /// it sees, so the report states the CPUs the engine was held to, read from Docker, not assumed.
   /// </summary>
   public string Cpuset { get; init; } = string.Empty;

   /// <summary>The image the container was created from, as written in its config (Config.Image).</summary>
   public string Image { get; init; } = string.Empty;

   /// <summary>
   /// Names of the container's environment variables (Config.Env), never their values: several of
   /// them are passwords from secrets.env. The names show which settings were overridden.
   /// </summary>
   public IReadOnlyList<string> EnvNames { get; init; } = Array.Empty<string>();

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
            ReadNetworks( settings ), ReadPorts( settings ) )
         {
            Cpuset = Text( root, "HostConfig", "CpusetCpus" ),
            Image = Text( root, "Config", "Image" ),
            EnvNames = ReadEnvNames( root ),
         };
      }
      catch( Exception ex ) when( ex is JsonException or KeyNotFoundException or InvalidOperationException or InvalidCastException )
      {
         throw new InvalidOperationException( $"docker inspect did not return a container description ({ex.Message})" );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads a string two levels down, or empty when either level is missing or null.
   /// </summary>
   /// <param name="root">The container object.</param>
   /// <param name="section">First level, e.g. "HostConfig".</param>
   /// <param name="key">Second level, e.g. "CpusetCpus".</param>
   /// <returns>The text, or empty.</returns>
   private static string Text( JsonElement root, string section, string key )
   {
      return root.TryGetProperty( section, out JsonElement outer ) && outer.ValueKind == JsonValueKind.Object
         && outer.TryGetProperty( key, out JsonElement value ) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
   }

   /// <summary>
   /// Reads the names of Config.Env ("NAME=value" entries), sorted, values dropped at once.
   /// </summary>
   /// <param name="root">The container object.</param>
   /// <returns>The names.</returns>
   private static List<string> ReadEnvNames( JsonElement root )
   {
      var names = new List<string>();
      if( root.TryGetProperty( "Config", out JsonElement config ) && config.ValueKind == JsonValueKind.Object
         && config.TryGetProperty( "Env", out JsonElement env ) && env.ValueKind == JsonValueKind.Array )
      {
         names.AddRange( env.EnumerateArray().Select( e => ( e.GetString() ?? string.Empty ).Split( '=', 2 )[0] ).Where( n => n.Length > 0 ) );
      }

      return names.OrderBy( n => n, StringComparer.Ordinal ).ToList();
   }

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
