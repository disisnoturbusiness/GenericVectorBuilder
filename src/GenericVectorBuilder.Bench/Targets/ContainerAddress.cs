namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// The address the benchmark uses to reach one port of one container: the container's own
/// address on its Docker bridge network and the port inside the container, with the published
/// host port it replaces kept beside it so a report shows what was avoided.
/// Why this is recorded with every compose-hosted target: a connection through the published
/// 127.0.0.1 port goes through the userland docker-proxy, an extra copy of every byte in user
/// space that the review's probe found adds measurable latency and costs throughput. The
/// always-on SQL Server and Qdrant have no such hop, so a number is only comparable with theirs
/// when the address it was measured through is on record.
/// </summary>
/// <param name="Container">Container name, e.g. "gvb-mariadb".</param>
/// <param name="ContainerId">First 12 characters of the container id.</param>
/// <param name="Network">Docker network the address is on.</param>
/// <param name="Ip">The container's address on that network.</param>
/// <param name="ContainerPort">Port inside the container (what the sink connects to).</param>
/// <param name="HostIp">Host address the port is published on (the proxied route, not used).</param>
/// <param name="HostPort">Host port the port is published on (the proxied route, not used).</param>
public sealed record ContainerAddress( string Container, string ContainerId, string Network, string Ip, int ContainerPort, string HostIp, int HostPort )
{
   #region Public Methods

   /// <summary>The address a sink connects to, as host:port.</summary>
   public string Endpoint => $"{Ip}:{ContainerPort}";

   /// <summary>The published address that docker-proxy serves and the benchmark does not use, as host:port.</summary>
   public string ProxiedEndpoint => $"{HostIp}:{HostPort}";

   /// <summary>
   /// The address as a URL, for the sinks that take one.
   /// </summary>
   /// <param name="scheme">"http" or "https".</param>
   /// <returns>For example "http://172.18.0.3:9200".</returns>
   public string Url( string scheme )
   {
      return $"{scheme}://{Endpoint}";
   }

   /// <summary>
   /// One line for a log or a note.
   /// </summary>
   /// <returns>For example "gvb-mariadb (b115140380d0) on network gvb-mariadb_default at 172.18.0.2:3306, not through docker-proxy 127.0.0.1:3306".</returns>
   public string Describe()
   {
      return $"{Container} ({ContainerId}) on network {Network} at {Endpoint}, not through docker-proxy {ProxiedEndpoint}";
   }

   #endregion Public Methods
}
