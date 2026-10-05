using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// The benchmark's own SQL Server: SQL Server 2025 RTM-CU9 (17.0.5005.3, the build of the native
/// server on this box) in container gvb-mssql, started from deploy/engines/mssql.compose.yaml.
/// The "sql" and "sql-diskann" targets measure it by default; the native service stays available
/// as the comparison target "sql-native".
/// Why a container: the review found that the native server's search work runs on its SQLPAL host
/// threads, which stay on all 8 CPUs whatever ALTER SERVER CONFIGURATION SET PROCESS AFFINITY says
/// (only the scheduler worker threads are bound), so its CPU pinning was not real, and it was
/// reached over loopback while every other engine went over a Docker bridge. In a container
/// created with a cpuset, the kernel holds every thread of the process tree to those CPUs, SQL
/// Server sizes its schedulers for the CPUs it really has, and the client reaches it over the same
/// kind of bridge address as every other engine.
/// What one instance holds: the connection to the container's own address (never the published
/// 127.0.0.1:14330 port), and what was read from the server once it was first reached: the build,
/// the CPUs it counts and the schedulers it runs, the container's cpuset, and its durability
/// settings. Nothing is read until <see cref="BindAsync"/>, because run-all starts the container
/// only at the target's turn.
/// </summary>
public sealed class SqlServerContainer
{
   #region Data Members

   /// <summary>Container name, from the compose file's container_name.</summary>
   public const string CONTAINER = "gvb-mssql";

   /// <summary>Host port the compose file publishes on 127.0.0.1 (the benchmark does not use it; it finds the container port from it).</summary>
   public const int HOST_PORT = 14330;

   /// <summary>Compose file under deploy/engines.</summary>
   public const string COMPOSE_FILE = "mssql.compose.yaml";

   /// <summary>Key of the sa password in secrets.env (handed to the container with env_file).</summary>
   public const string PASSWORD_KEY = "MSSQL_SA_PASSWORD";

   private const string CONF_IN_CONTAINER = "/var/opt/mssql/mssql.conf";
   private const int QUERY_SECONDS = 30;
   private const string FACTS_SQL = "SELECT @@VERSION, CONVERT( NVARCHAR(128), SERVERPROPERTY( 'Edition' ) ), i.cpu_count, "
      + "( SELECT COUNT(*) FROM sys.dm_os_schedulers WHERE status = N'VISIBLE ONLINE' ), i.affinity_type_desc FROM sys.dm_os_sys_info i;";
   private static readonly TimeSpan READY_DEADLINE = TimeSpan.FromMinutes( 2 );
   private static readonly TimeSpan READY_POLL = TimeSpan.FromSeconds( 2 );

   private readonly IContainerInspector _inspector;
   private readonly string _dataFolder;
   private readonly string _image;
   private readonly IReadOnlyList<string> _benchDatabases;
   private readonly SemaphoreSlim _bindLock = new( 1, 1 );
   private volatile ReachedServer? _reached;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the description of the container; nothing is contacted.
   /// </summary>
   /// <param name="inspector">Asks Docker about the container (for its cpuset, image and environment names).</param>
   /// <param name="dataFolder">The container's data folder on the host (its /var/opt/mssql).</param>
   /// <param name="image">The image the compose file names, for the text shown before the server is reached.</param>
   /// <param name="benchDatabases">Benchmark databases whose durability settings are reported when they exist.</param>
   public SqlServerContainer( IContainerInspector inspector, string dataFolder, string image, IReadOnlyList<string> benchDatabases )
   {
      _inspector = inspector;
      _dataFolder = dataFolder;
      _image = image;
      _benchDatabases = benchDatabases;
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
   public SqlServerContainer? Earlier { get; init; }

   /// <summary>
   /// Connection string to the container's own address, without a database.
   /// </summary>
   /// <exception cref="InvalidOperationException">The server has not been reached yet.</exception>
   public string ConnectionString => _reached?.Connection ?? throw new InvalidOperationException( $"The SQL Server in container {CONTAINER} has not been reached yet; the target binds to it when the engine is first used." );

   /// <summary>Engine text for the report: the build read from the server and the CPUs it runs on, or what is known before that.</summary>
   public string Engine => _reached?.Engine ?? ( Earlier is { IsBound: true } earlier ? earlier.Engine : null ) ?? $"SQL Server 2025 in container {CONTAINER} (image {_image}); the build and CPUs are read from the server when it is first reached";

   /// <summary>Durability text for the report, read from the server once it is reached.</summary>
   public string Durability => _reached?.Durability ?? ( Earlier is { IsBound: true } earlier ? earlier.Durability : null ) ?? $"to be read from the SQL Server in container {CONTAINER} when it is first reached";

   /// <summary>
   /// Reaches the server at the container's own address and reads what the report states about it.
   /// Waits up to two minutes for the server to accept a login (compose reports the container healthy
   /// only after its own login check, so this covers a container someone else has just started).
   /// Safe to call again and from several threads: the first call does the work.
   /// </summary>
   /// <param name="router">Container addresses read from Docker for this target.</param>
   /// <param name="ct">Cancellation.</param>
   /// <exception cref="InvalidOperationException">No password in secrets.env, no published port, or no login within the deadline.</exception>
   public async Task BindAsync( ContainerRouter router, CancellationToken ct )
   {
      await _bindLock.WaitAsync( ct );
      try
      {
         if( _reached != null )
         {
            return;
         }

         ContainerAddress address = router.Address( CONTAINER, HOST_PORT );
         string connection = BuildConnection( address );
         await WaitForLoginAsync( connection, address, ct );
         ContainerFacts? container = await InspectAsync( ct );
         string engine = await ReadEngineAsync( connection, container, ct );
         string durability = await SqlDurability.ReadAsync( connection, _benchDatabases, ct, Path.Combine( _dataFolder, "mssql.conf" ),
            $"mssql.conf of container {CONTAINER} ({CONF_IN_CONTAINER}, on the host at {_dataFolder}/mssql.conf)", true );
         _reached = new ReachedServer( connection, engine, $"{durability} {DescribeEnvironment( container )}" );
      }
      finally
      {
         _bindLock.Release();
      }
   }

   /// <summary>
   /// The engine text from facts already read. Pure, so the wording is tested without a server.
   /// </summary>
   /// <param name="version">@@VERSION.</param>
   /// <param name="edition">SERVERPROPERTY('Edition').</param>
   /// <param name="cpuCount">sys.dm_os_sys_info.cpu_count.</param>
   /// <param name="schedulers">Schedulers with status VISIBLE ONLINE.</param>
   /// <param name="affinity">sys.dm_os_sys_info.affinity_type_desc.</param>
   /// <param name="cpuset">The container's cpuset from docker inspect; empty for none; null when Docker could not be read.</param>
   /// <param name="image">The container's image from docker inspect, or null.</param>
   /// <returns>For example "Microsoft SQL Server 2025 (RTM-CU9) (KB5122048) - 17.0.5005.3 (X64), Enterprise Developer Edition (64-bit), in container gvb-mssql (...)".</returns>
   public static string DescribeEngine( string version, string edition, int cpuCount, int schedulers, string affinity, string? cpuset, string? image )
   {
      string held = cpuset == null ? "cpuset not read from Docker" : cpuset.Length == 0 ? "NO cpuset, every CPU" : $"cpuset {cpuset} from its creation";
      string sees = $"SQL Server counts {cpuCount} CPU(s) and runs {schedulers} visible scheduler(s), affinity {affinity}";
      string warn = cpuset is { Length: > 0 } && CpuSetText.Count( cpuset ) is int allowed && allowed != schedulers ? $"; WARNING: {schedulers} schedulers for {allowed} allowed CPUs" : string.Empty;
      return $"{version.Split( '\n' )[0].Trim()}, {edition}, in container {CONTAINER} ({( string.IsNullOrEmpty( image ) ? "image not read" : "image " + image )}; {held}; {sees}{warn})";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The connection string: the container's address and port, sa with the password from
   /// secrets.env, and the same encryption settings the native connection uses (encrypted, the
   /// self-signed certificate trusted), so both are reached the same way apart from the route.
   /// </summary>
   /// <param name="address">The container's address.</param>
   /// <returns>The connection string.</returns>
   private static string BuildConnection( ContainerAddress address )
   {
      return new SqlConnectionStringBuilder
      {
         DataSource = $"{address.Ip},{address.ContainerPort}",
         UserID = "sa",
         Password = EngineSecrets.Read( PASSWORD_KEY ),
         TrustServerCertificate = true,
         Encrypt = true,
         ConnectTimeout = 15,
      }.ConnectionString;
   }

   /// <summary>
   /// Logs in until the server answers, at most <see cref="READY_DEADLINE"/>.
   /// </summary>
   /// <param name="connection">Connection string.</param>
   /// <param name="address">Address, for the message.</param>
   /// <param name="ct">Cancellation.</param>
   /// <exception cref="InvalidOperationException">No login within the deadline.</exception>
   private static async Task WaitForLoginAsync( string connection, ContainerAddress address, CancellationToken ct )
   {
      var clock = Stopwatch.StartNew();
      while( true )
      {
         try
         {
            await using var sql = new SqlConnection( connection );
            await sql.OpenAsync( ct );
            await using var command = new SqlCommand( "SELECT 1;", sql ) { CommandTimeout = QUERY_SECONDS };
            await command.ExecuteScalarAsync( ct );
            return;
         }
         catch( SqlException ) when( clock.Elapsed + READY_POLL < READY_DEADLINE )
         {
            await Task.Delay( READY_POLL, ct );
         }
         catch( SqlException ex )
         {
            throw new InvalidOperationException( $"The SQL Server in container {CONTAINER} at {address.Endpoint} did not accept a login within {READY_DEADLINE.TotalSeconds:0} s: {ex.Message}" );
         }
      }
   }

   /// <summary>
   /// Reads the build, edition, CPU count and online schedulers, and words them with the container's cpuset.
   /// </summary>
   /// <param name="connection">Connection string.</param>
   /// <param name="container">The container's facts, or null.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The engine text.</returns>
   private static async Task<string> ReadEngineAsync( string connection, ContainerFacts? container, CancellationToken ct )
   {
      await using var sql = new SqlConnection( connection );
      await sql.OpenAsync( ct );
      await using var command = new SqlCommand( FACTS_SQL, sql ) { CommandTimeout = QUERY_SECONDS };
      await using SqlDataReader reader = await command.ExecuteReaderAsync( ct );
      if( !await reader.ReadAsync( ct ) )
      {
         throw new InvalidOperationException( $"The SQL Server in container {CONTAINER} returned no row for sys.dm_os_sys_info." );
      }

      return DescribeEngine( reader.GetString( 0 ), reader.GetString( 1 ), Convert.ToInt32( reader.GetValue( 2 ) ), Convert.ToInt32( reader.GetValue( 3 ) ), reader.GetString( 4 ), container?.Cpuset, container?.Image );
   }

   /// <summary>
   /// Reads the container from Docker, or null when Docker cannot be asked (the text then says so).
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
   /// Names the MSSQL_ settings the container was given through its environment (names only; one is the password).
   /// </summary>
   /// <param name="container">The container's facts, or null.</param>
   /// <returns>One sentence.</returns>
   private static string DescribeEnvironment( ContainerFacts? container )
   {
      if( container == null )
      {
         return "The container's environment could not be read from Docker.";
      }

      string[] names = container.EnvNames.Where( n => n.StartsWith( "MSSQL_", StringComparison.Ordinal ) ).ToArray();
      return $"Container settings from its environment (names only): {( names.Length == 0 ? "none" : string.Join( ", ", names ) )}.";
   }

   #endregion Private Methods
}

/// <summary>
/// What was read once the container's server was reached.
/// </summary>
/// <param name="Connection">Connection string to the container's own address.</param>
/// <param name="Engine">Engine text.</param>
/// <param name="Durability">Durability text.</param>
internal sealed record ReachedServer( string Connection, string Engine, string Durability );
