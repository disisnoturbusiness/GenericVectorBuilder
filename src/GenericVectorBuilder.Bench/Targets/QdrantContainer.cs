using System.Diagnostics;
using System.Text.Json;
using Qdrant.Client;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// The benchmark's own Qdrant: Qdrant 1.17.0 (the version of the native server on this box) in
/// container gvb-qdrant, started from deploy/engines/qdrant.compose.yaml. The "qdrant" and
/// "qdrant-hnsw" targets measure it by default; the native service stays available as the
/// comparison target "qdrant-native".
/// Why a container: every other engine runs in a container created on the engine CPUs and is
/// reached over a Docker bridge address; the native Qdrant was pinned with taskset after it had
/// started (so it sized its search and optimizer threads for 8 CPUs) and was reached over loopback.
/// In the container it sees only the engine CPUs from its start and is reached the same way as the rest.
/// What one instance holds once <see cref="BindAsync"/> has run: a gRPC client and the HTTP helper on
/// the container's own address (never the published 127.0.0.1:16333/16334 ports), the version and
/// core count the server reports, the container's cpuset, and the durability read from the config
/// file inside the container. The caller (the target factory) disposes it.
/// </summary>
public sealed class QdrantContainer : IDisposable
{
   #region Data Members

   /// <summary>Container name, from the compose file's container_name.</summary>
   public const string CONTAINER = "gvb-qdrant";

   /// <summary>Published HTTP host port (the container's 6333).</summary>
   public const int HTTP_HOST_PORT = 16333;

   /// <summary>Published gRPC host port (the container's 6334).</summary>
   public const int GRPC_HOST_PORT = 16334;

   /// <summary>Compose file under deploy/engines.</summary>
   public const string COMPOSE_FILE = "qdrant.compose.yaml";

   private const string CONFIG_IN_CONTAINER = "/qdrant/config/config.yaml";
   private static readonly TimeSpan CALL_DEADLINE = TimeSpan.FromSeconds( 60 );
   private static readonly TimeSpan HTTP_TIMEOUT = TimeSpan.FromSeconds( 10 );
   private static readonly TimeSpan READY_DEADLINE = TimeSpan.FromMinutes( 2 );
   private static readonly TimeSpan READY_POLL = TimeSpan.FromSeconds( 1 );

   private readonly IContainerInspector _inspector;
   private readonly string _image;
   private readonly Action<string>? _log;
   private readonly SemaphoreSlim _bindLock = new( 1, 1 );
   private QdrantClient? _client;
   private QdrantServer? _server;
   private volatile ReachedQdrant? _reached;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the description of the container; nothing is contacted.
   /// </summary>
   /// <param name="inspector">Asks Docker about the container (cpuset, image, environment names).</param>
   /// <param name="image">The image the compose file names, for the text shown before the server is reached.</param>
   /// <param name="log">Progress output for long waits, or null for the console.</param>
   public QdrantContainer( IContainerInspector inspector, string image, Action<string>? log )
   {
      _inspector = inspector;
      _image = image;
      _log = log;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>True once <see cref="BindAsync"/> has reached the server.</summary>
   public bool IsBound => _reached != null;

   /// <summary>
   /// The same container as read when the run started (by the target factory, if it was running then),
   /// whose engine and durability text stand in until this instance reaches the server itself. Why:
   /// the runner records a target's engine and durability before it connects, and a container that
   /// was already running should not be reported with "to be read" text.
   /// </summary>
   public QdrantContainer? Earlier { get; init; }

   /// <summary>gRPC client on the container's address.</summary>
   /// <exception cref="InvalidOperationException">Not reached yet.</exception>
   public QdrantClient Client => _reached != null && _client != null ? _client : throw NotReached();

   /// <summary>Settle, state and telemetry helper on the container's address.</summary>
   /// <exception cref="InvalidOperationException">Not reached yet.</exception>
   public QdrantServer Server => _reached != null && _server != null ? _server : throw NotReached();

   /// <summary>Version the server reports, or "unknown version" before it is reached.</summary>
   public string Version => _reached?.Version ?? "unknown version";

   /// <summary>Engine text for the report.</summary>
   public string Engine => _reached?.Engine ?? ( Earlier is { IsBound: true } earlier ? earlier.Engine : null ) ?? $"Qdrant in container {CONTAINER} (image {_image}); the version and CPUs are read from the server when it is first reached";

   /// <summary>Durability text for the report.</summary>
   public string Durability => _reached?.Durability ?? ( Earlier is { IsBound: true } earlier ? earlier.Durability : null ) ?? $"to be read from the Qdrant in container {CONTAINER} when it is first reached";

   /// <summary>
   /// Reaches the server at the container's own address, waits up to two minutes for it to report
   /// ready, and reads its version, core count, the container's cpuset and the durability settings.
   /// Safe to call again and from several threads: the first call does the work.
   /// </summary>
   /// <param name="router">Container addresses read from Docker for this target.</param>
   /// <param name="ct">Cancellation.</param>
   /// <exception cref="InvalidOperationException">A port is not published, or the server did not report ready in time.</exception>
   public async Task BindAsync( ContainerRouter router, CancellationToken ct )
   {
      await _bindLock.WaitAsync( ct );
      try
      {
         if( _reached != null )
         {
            return;
         }

         ContainerAddress grpc = router.Address( CONTAINER, GRPC_HOST_PORT );
         ContainerAddress http = router.Address( CONTAINER, HTTP_HOST_PORT );
         using var web = new HttpClient { BaseAddress = new Uri( http.Url( "http" ) + "/" ), Timeout = HTTP_TIMEOUT };
         await WaitUntilReadyAsync( web, http, ct );
         string version = await ReadVersionAsync( web, ct );
         ContainerFacts? container = await InspectAsync( ct );
         int? searchThreads = container == null ? null : CountSearchThreads( container.Id );
         string durability = await QdrantServer.ReadContainerDurabilityAsync( CONTAINER, CONFIG_IN_CONTAINER, container?.EnvNames, version, ct );
         _client = new QdrantClient( grpc.Ip, grpc.ContainerPort, false, null, CALL_DEADLINE );
         _server = new QdrantServer( _client, http.Ip, http.ContainerPort, _log );
         _reached = new ReachedQdrant( version, DescribeEngine( version, searchThreads, container?.Cpuset, container?.Image ), durability );
      }
      finally
      {
         _bindLock.Release();
      }
   }

   /// <summary>
   /// The engine text from facts already read. Pure, so the wording is tested without a server.
   /// Why search threads and not the telemetry's core count: measured 2026-10-04, the telemetry says
   /// "cores 8" inside a container held to 4 CPUs (it counts the host), while the thread pools follow
   /// the CPUs Qdrant may use: the native server runs 7 "search-N" threads on 8 CPUs, the container 3
   /// on 4. One fewer search thread than usable CPUs is what a correctly sized server shows.
   /// </summary>
   /// <param name="version">Version the server reports.</param>
   /// <param name="searchThreads">Threads named "search-N" in the container's qdrant process, or null when not read.</param>
   /// <param name="cpuset">The container's cpuset from docker inspect; empty for none; null when Docker could not be read.</param>
   /// <param name="image">The container's image, or null.</param>
   /// <returns>For example "Qdrant 1.17.0 in container gvb-qdrant (image qdrant/qdrant:v1.17.0@sha256:...; cpuset 2-3,6-7 from its creation; 3 search threads)".</returns>
   public static string DescribeEngine( string version, int? searchThreads, string? cpuset, string? image )
   {
      string held = cpuset == null ? "cpuset not read from Docker" : cpuset.Length == 0 ? "NO cpuset, every CPU" : $"cpuset {cpuset} from its creation";
      string pool = searchThreads.HasValue ? $"{searchThreads} search thread(s)" : "search threads not counted";
      string warn = cpuset is { Length: > 0 } && searchThreads.HasValue && CpuSetText.Count( cpuset ) is int allowed && searchThreads > Math.Max( 1, allowed - 1 )
         ? $"; WARNING: {searchThreads} search threads for {allowed} allowed CPUs, so Qdrant sized its pools for more CPUs than it has" : string.Empty;
      return $"Qdrant {version} in container {CONTAINER} ({( string.IsNullOrEmpty( image ) ? "image not read" : "image " + image )}; {held}; {pool}{warn})";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Asks /readyz until it answers 200, at most <see cref="READY_DEADLINE"/>.
   /// </summary>
   /// <param name="web">HTTP client on the container's address.</param>
   /// <param name="http">Address, for the message.</param>
   /// <param name="ct">Cancellation.</param>
   /// <exception cref="InvalidOperationException">Not ready within the deadline.</exception>
   private static async Task WaitUntilReadyAsync( HttpClient web, ContainerAddress http, CancellationToken ct )
   {
      var clock = Stopwatch.StartNew();
      string last = "no answer";
      while( clock.Elapsed < READY_DEADLINE )
      {
         try
         {
            using HttpResponseMessage answer = await web.GetAsync( "readyz", ct );
            if( answer.IsSuccessStatusCode )
            {
               return;
            }

            last = $"HTTP {(int)answer.StatusCode}";
         }
         catch( Exception ex ) when( ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested )
         {
            last = ex.Message;
         }

         await Task.Delay( READY_POLL, ct );
      }

      throw new InvalidOperationException( $"The Qdrant in container {CONTAINER} at {http.Endpoint} did not report ready within {READY_DEADLINE.TotalSeconds:0} s (last answer: {last})." );
   }

   /// <summary>
   /// Reads the version from GET /.
   /// </summary>
   /// <param name="web">HTTP client.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The version.</returns>
   private static async Task<string> ReadVersionAsync( HttpClient web, CancellationToken ct )
   {
      using JsonDocument root = JsonDocument.Parse( await web.GetStringAsync( string.Empty, ct ) );
      return root.RootElement.TryGetProperty( "version", out JsonElement version ) ? version.GetString() ?? "unknown version" : "unknown version";
   }

   /// <summary>
   /// Counts the "search-N" threads of the qdrant process in the container, from /proc.
   /// </summary>
   /// <param name="containerId">The container's short id.</param>
   /// <returns>The count, or null when the process or its threads could not be read.</returns>
   private static int? CountSearchThreads( string containerId )
   {
      int pid = HostProcess.FindInContainer( "qdrant", containerId ).FirstOrDefault();
      IReadOnlyList<string> names = pid > 0 ? HostProcess.ThreadNames( pid ) : Array.Empty<string>();
      return names.Count == 0 ? null : names.Count( n => n.StartsWith( "search-", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// Reads the container from Docker, or null when Docker cannot be asked.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The facts, or null.</returns>
   private async Task<ContainerFacts?> InspectAsync( CancellationToken ct )
   {
      try
      {
         return await _inspector.InspectAsync( CONTAINER, ct ) is string json ? ContainerFacts.Parse( json ) : null;
      }
      catch( InvalidOperationException )
      {
         return null;
      }
   }

   /// <summary>
   /// The error for a use before the server was reached.
   /// </summary>
   /// <returns>The exception.</returns>
   private static InvalidOperationException NotReached()
   {
      return new InvalidOperationException( $"The Qdrant in container {CONTAINER} has not been reached yet; the target binds to it when the engine is first used." );
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Disposes the clients built on the container's address.
   /// </summary>
   public void Dispose()
   {
      _server?.Dispose();
      _client?.Dispose();
      _bindLock.Dispose();
   }

   #endregion IDisposable
}

/// <summary>
/// What was read once the container's Qdrant was reached.
/// </summary>
/// <param name="Version">Version the server reports.</param>
/// <param name="Engine">Engine text.</param>
/// <param name="Durability">Durability text.</param>
internal sealed record ReachedQdrant( string Version, string Engine, string Durability );
