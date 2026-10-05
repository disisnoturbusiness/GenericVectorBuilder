using System.Reflection;
using System.Text.Json;
using GenericVectorBuilder.Core.Configuration;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Sinks;
using GenericVectorBuilder.Engines.Common;
using Microsoft.Data.SqlClient;
using Qdrant.Client;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// Knows every target the benchmark can measure and builds them by name:
/// "sql" (the builder's SQL Server sink, exact VECTOR_DISTANCE), "sql-diskann" (SQL Server
/// with a DiskANN index in a throwaway database), "qdrant" (the builder's Qdrant sink, exact
/// by design), "qdrant-hnsw" (Qdrant through its HNSW graph), plus every engine sink in the
/// Engines assembly (found through <see cref="EngineCatalog"/>).
/// Where the four built-in targets run: in the benchmark's own containers, gvb-mssql
/// (SQL Server 2025 RTM-CU9, the native server's build) and gvb-qdrant (Qdrant 1.17.0, the native
/// server's version), started from deploy/engines like every other engine, so every engine gets
/// the same CPU split (a cpuset from the container's creation) and the same route (the container's
/// own address on a Docker bridge). The review found the native SQL Server's search work running on
/// SQLPAL threads that its process affinity does not bind, on all 8 CPUs. The native services stay
/// measurable as the comparison targets "sql-native" and "qdrant-native" (the builder's exact sinks
/// on the native servers, as the old "sql" and "qdrant" were); they are left out of the default
/// target list so a run without --targets compares like with like.
/// Why the SQL targets write to their own databases: the benchmark copies up to 760k rows,
/// several gigabytes, and none of that should grow or lock the builder's real database.
/// What each built-in target proves about its index: "sql-diskann" and "qdrant-hnsw" are
/// <see cref="IIndexFinisher"/>s that wait for and prove a real index; "sql" and "qdrant" (the
/// builder's own sinks, which this benchmark wraps rather than changes) get an index-state
/// reader that proves from the server that no index is used, plus their durability text.
/// How compose-hosted engines are reached: through the container's own address on its Docker
/// network and the port inside the container (read with docker inspect when the engine is first
/// used, see <see cref="ContainerRoutes"/>), never through the published 127.0.0.1 port that
/// docker-proxy serves.
/// Where the pipeline's vectors come from is unchanged: the native GenericVectorBuilder database
/// (see the Data folder); only the copies the benchmark searches live in the containers.
/// The "mariadb" target runs in its own container gvbbench-mariadb (deploy/engines/mariadb-bench.compose.yaml,
/// data in ~/gvb-data/engines/mariadb-bench, see <see cref="MariaBench"/>), created on the engine CPUs and
/// started and stopped by run-all like every other engine; the daily gvb-mariadb is never used, asked about
/// or changed by a run.
/// </summary>
public sealed class TargetFactory : IDisposable
{
   #region Data Members

   /// <summary>Database the "sql" target writes to.</summary>
   public const string SQL_BENCH_DATABASE = "GvbBench";

   /// <summary>
   /// The pair of passes a consolidated report compares by default: SQL Server's DiskANN default
   /// search against its own exact mode. Both read the SAME table (gvb_{collection}_ann in
   /// GvbBenchDiskAnn), so the pair differs only in the search method. It replaces the old
   /// default of sql-diskann against the "sql" target, which searched a different database and table.
   /// </summary>
   public static readonly PassPair SQL_DISKANN_PAIR = new( "sql-diskann", "default@1", "exact",
      "DiskANN default search against SQL Server's exact mode on the same table (dbo.gvb_{collection}_ann in GvbBenchDiskAnn); only the search method differs" );

   /// <summary>The comparison target on the native SQL Server service.</summary>
   public const string SQL_NATIVE = "sql-native";

   /// <summary>The comparison target on the native Qdrant service.</summary>
   public const string QDRANT_NATIVE = "qdrant-native";

   private const string ENGINE_DATA_ROOT = "~/gvb-data/engines";
   private const string QDRANT_COLLECTIONS = "~/qdrant/storage/collections";
   private const string NOT_READ = "not read";
   private const string QDRANT_CONFIG = "~/qdrant/config/config.yaml";
   private const int QDRANT_HTTP_PORT = 6333;
   private const string SQL_EXACT_INDEX = "exact VECTOR_DISTANCE cosine, no vector index (full scan)";
   private const string SQL_DISKANN_INDEX = "DiskANN (preview) via VECTOR_SEARCH, cosine, build parameters read once it is built; the exact mode scans the same table";
   private const string QDRANT_EXACT_INDEX = "exact scan: the builder's sink sends exact=true on every search, so no HNSW graph is used whether or not Qdrant has built one (see the index state)";
   private const string NATIVE_LABEL = "native service on this host, a COMPARISON target: not the container CPU split, reached over loopback";
   private static readonly TimeSpan QDRANT_CALL_DEADLINE = TimeSpan.FromSeconds( 60 );
   private static readonly TimeSpan BIND_DEADLINE = TimeSpan.FromSeconds( 30 );
   private static readonly TimeSpan BIND_POLL = TimeSpan.FromSeconds( 1 );
   private static readonly TimeSpan START_READ_LIMIT = TimeSpan.FromSeconds( 30 );
   private static readonly string[] BUILT_IN = { "sql", "sql-diskann", "qdrant", "qdrant-hnsw" };
   private static readonly string[] NATIVE = { SQL_NATIVE, QDRANT_NATIVE };

   private readonly GvbSettings _settings;
   private readonly string _sqlConnection;
   private readonly QdrantClient _qdrant;
   private readonly QdrantServer _qdrantServer;
   private readonly string _repoRoot;
   private readonly int? _hnswEf;
   private readonly Dictionary<string, ISink> _engines;
   private readonly IContainerInspector _inspector;
   private readonly List<BenchTarget> _created = new();
   private readonly List<IDisposable> _owned = new();
   private readonly List<string> _notes = new();
   private readonly Action<string>? _log;
   private bool _initialized;
   private string _sqlVersion = "SQL Server";
   private string _qdrantVersion = "unknown version";
   private string? _sqlDurability;
   private string? _qdrantDurability;
   private SqlServerContainer? _sqlAtStart;
   private QdrantContainer? _qdrantAtStart;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the factory. Engine sinks are instantiated here (their constructors do not
   /// connect), so the list of names is complete before anything runs.
   /// </summary>
   /// <param name="settings">Builder settings (SQL login, Qdrant address).</param>
   /// <param name="repoRoot">Repository root, where deploy/engines lives.</param>
   /// <param name="hnswEf">Search beam for qdrant-hnsw, or null for the server default.</param>
   /// <param name="log">Progress output for the long waits inside targets, or null for the console.</param>
   /// <param name="inspector">Asks Docker about containers; null for the real "docker inspect". Tests pass a fake.</param>
   public TargetFactory( GvbSettings settings, string repoRoot, int? hnswEf, Action<string>? log = null, IContainerInspector? inspector = null )
   {
      _inspector = inspector ?? new DockerInspector();
      _settings = settings;
      _sqlConnection = settings.BuildSqlConnectionString();
      _qdrant = new QdrantClient( settings.QdrantHost, settings.QdrantGrpcPort, false, null, QDRANT_CALL_DEADLINE );
      _qdrantServer = new QdrantServer( _qdrant, settings.QdrantHost, QDRANT_HTTP_PORT, log );
      _log = log;
      _repoRoot = repoRoot;
      _hnswEf = hnswEf;
      _engines = LoadEngines( _notes );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Every target a run measures when no --targets is given: the built-in ones first, then every
   /// engine. All run in containers on the engine CPUs, or embedded in the client.
   /// </summary>
   public IReadOnlyList<string> Names => BUILT_IN.Concat( _engines.Keys.Where( k => !BUILT_IN.Contains( k, StringComparer.OrdinalIgnoreCase ) && !NATIVE.Contains( k, StringComparer.OrdinalIgnoreCase ) ).OrderBy( k => k ) ).ToList();

   /// <summary>
   /// The comparison targets on the native services ("sql-native", "qdrant-native"): built on
   /// request (--targets) but never part of <see cref="Names"/>.
   /// </summary>
   public IReadOnlyList<string> ComparisonNames => NATIVE;

   /// <summary>Problems met while finding engine sinks, for the report.</summary>
   public IReadOnlyList<string> Notes => _notes;

   /// <summary>
   /// Reads the SQL Server and Qdrant versions for the report. Failures leave a placeholder.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   public async Task InitializeAsync( CancellationToken ct )
   {
      try
      {
         await using var connection = new SqlConnection( _sqlConnection );
         await connection.OpenAsync( ct );
         await using var command = new SqlCommand( "SELECT @@VERSION;", connection );
         _sqlVersion = ( Convert.ToString( await command.ExecuteScalarAsync( ct ) ) ?? _sqlVersion ).Split( '\n' )[0].Trim();
      }
      catch( SqlException ex )
      {
         _notes.Add( $"Could not read the SQL Server version: {ex.Message}" );
      }

      try
      {
         using var http = new HttpClient { Timeout = TimeSpan.FromSeconds( 10 ) };
         using JsonDocument root = JsonDocument.Parse( await http.GetStringAsync( $"http://{_settings.QdrantHost}:{QDRANT_HTTP_PORT}/", ct ) );
         _qdrantVersion = root.RootElement.GetProperty( "version" ).GetString() ?? _qdrantVersion;
      }
      catch( Exception ex ) when( ex is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException )
      {
         _notes.Add( $"Could not read the Qdrant version: {ex.Message}" );
      }

      await ReadDurabilityAsync( ct );
      await ReadRunningContainersAsync( ct );
      _initialized = true;
   }

   /// <summary>
   /// Builds a target by name.
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <returns>The target.</returns>
   /// <exception cref="ArgumentException">Unknown name, or a compose-hosted engine with no container route.</exception>
   /// <exception cref="InvalidOperationException"><see cref="InitializeAsync"/> has not run, so the built-in targets would carry no version or durability text.</exception>
   public BenchTarget Create( string name )
   {
      BenchTarget target = Build( name );
      _created.Add( target );
      return target;
   }

   /// <summary>
   /// Drops the "sql-native" target's database on the native server once it holds no table, so a
   /// cleaned-up benchmark leaves nothing behind there. Call it last: the SQL sink remembers that its
   /// database exists and would not create it again in the same session.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when the database was dropped.</returns>
   public Task<bool> DropEmptyBenchDatabaseAsync( CancellationToken ct )
   {
      return DropIfEmptyAsync( _sqlConnection, SQL_BENCH_DATABASE, ct );
   }

   /// <summary>
   /// Drops a benchmark database on one server once it holds no table. The native server's
   /// database (the "sql-native" target's) is dropped by <see cref="DropEmptyBenchDatabaseAsync"/>
   /// at the end of a run; the container's (the "sql" target's) is dropped as soon as its last
   /// table is, because run-all may stop the container right after the target.
   /// </summary>
   /// <param name="serverConnectionString">Connection string without a database.</param>
   /// <param name="database">Database name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when the database was dropped.</returns>
   public static async Task<bool> DropIfEmptyAsync( string serverConnectionString, string database, CancellationToken ct )
   {
      await using var connection = new SqlConnection( serverConnectionString );
      await connection.OpenAsync( ct );
      await using var exists = new SqlCommand( $"SELECT DB_ID( N'{database.Replace( "'", "''" )}' );", connection );
      if( await exists.ExecuteScalarAsync( ct ) is DBNull or null )
      {
         return false;
      }

      string quoted = $"[{database.Replace( "]", "]]" )}]";
      await using var tables = new SqlCommand( $"SELECT COUNT(*) FROM {quoted}.sys.tables WHERE is_ms_shipped = 0;", connection );
      if( Convert.ToInt32( await tables.ExecuteScalarAsync( ct ) ) > 0 )
      {
         return false;
      }

      SqlConnection.ClearAllPools();
      await using var drop = new SqlCommand( $"ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {quoted};", connection ) { CommandTimeout = 300 };
      await drop.ExecuteNonQueryAsync( ct );
      return true;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Builds a target by name (see <see cref="Create"/>).
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <returns>The target.</returns>
   private BenchTarget Build( string name )
   {
      if( ( BUILT_IN.Contains( name, StringComparer.OrdinalIgnoreCase ) || NATIVE.Contains( name, StringComparer.OrdinalIgnoreCase ) ) && !_initialized )
      {
         throw new InvalidOperationException( $"Target '{name}' was requested before InitializeAsync finished, so it would carry no server version and no durability text. Call InitializeAsync first." );
      }

      switch( name.ToLowerInvariant() )
      {
         case "sql":
            return SqlContainerTarget( false );
         case "sql-diskann":
            return SqlContainerTarget( true );
         case "qdrant":
            return QdrantContainerTarget( false );
         case "qdrant-hnsw":
            return QdrantContainerTarget( true );
         case SQL_NATIVE:
            return SqlTarget( new NamedSink( SQL_NATIVE, () => new SqlVectorSink( _sqlConnection, SQL_BENCH_DATABASE ), new SinkText( () => $"{_sqlVersion} ({NATIVE_LABEL})", () => SQL_EXACT_INDEX, "always-on", () => _sqlDurability ?? NOT_READ ) ),
               SQL_BENCH_DATABASE, c => $"gvb_{c}", SQL_EXACT_INDEX, true );
         case QDRANT_NATIVE:
            return QdrantTarget( new NamedSink( QDRANT_NATIVE, () => new QdrantSink( _qdrant ), new SinkText( () => $"Qdrant {_qdrantVersion} ({NATIVE_LABEL})", () => QDRANT_EXACT_INDEX, "always-on", () => _qdrantDurability ?? NOT_READ ) ),
               c => $"gvb_{c}", QDRANT_EXACT_INDEX, true );
      }

      if( _engines.TryGetValue( name, out ISink? sink ) )
      {
         return EngineTarget( sink );
      }

      throw new ArgumentException( $"Unknown target '{name}'. Known: {string.Join( ", ", Names )}; native comparison targets: {string.Join( ", ", NATIVE )}." );
   }

   /// <summary>
   /// "sql" or "sql-diskann" on the benchmark's own SQL Server container. The sink is built when the
   /// target first reaches the container (run-all starts it at the target's turn), on the
   /// container's own address, after the server's build, CPUs and durability have been read.
   /// </summary>
   /// <param name="diskAnn">True for "sql-diskann", false for the builder's exact sink ("sql").</param>
   /// <returns>The target.</returns>
   private BenchTarget SqlContainerTarget( bool diskAnn )
   {
      string composePath = Path.Combine( _repoRoot, "deploy", "engines", SqlServerContainer.COMPOSE_FILE );
      var server = NewSqlContainer( composePath );
      string name = diskAnn ? "sql-diskann" : "sql";
      string index = diskAnn ? SQL_DISKANN_INDEX : SQL_EXACT_INDEX;
      string database = diskAnn ? SqlDiskAnnSink.DATABASE : SQL_BENCH_DATABASE;
      Func<string, string> table = diskAnn ? SqlDiskAnnSink.AnnTable : c => $"gvb_{c}";
      var text = new SinkText( () => server.Engine, () => index, SqlServerContainer.COMPOSE_FILE, () => server.Durability );
      EngineRoute route = EngineRoute.Async( new[] { SqlServerContainer.CONTAINER }, async ( router, ct ) =>
      {
         await server.BindAsync( router, ct );
         return diskAnn
            ? new SqlDiskAnnSink( server.ConnectionString, server.Engine, server.Durability, SqlDiskAnnSink.DATABASE, SqlServerContainer.COMPOSE_FILE )
            : new NamedSink( name, () => new SqlVectorSink( server.ConnectionString, SQL_BENCH_DATABASE ), text, ( _, ct2 ) => DropIfEmptyAsync( server.ConnectionString, SQL_BENCH_DATABASE, ct2 ) );
      } );
      return new BenchTarget( new NamedSink( name, () => throw Unreached( name ), text ), server.Engine, index, "compose", composePath,
         ( _, ct ) => ResourceProbe.DockerRamAsync( composePath, ct ),
         ( c, ct ) => server.IsBound ? ResourceProbe.SqlTableSizeAsync( server.ConnectionString, database, table( c ), ct ) : Task.FromResult( Measurement.None( "the container was never reached" ) ) )
      {
         IndexStateReader = diskAnn ? null : ( c, ct ) => SqlProbe.ExactStateAsync( server.ConnectionString, database, table( c ), ct ),
         IndexStep = diskAnn,
         PairHint = diskAnn ? SQL_DISKANN_PAIR : null,
         Container = new ContainerBinding( route, _inspector, BIND_DEADLINE, BIND_POLL ),
      };
   }

   /// <summary>
   /// "qdrant" or "qdrant-hnsw" on the benchmark's own Qdrant container, built when the target first
   /// reaches it, on the container's own address.
   /// </summary>
   /// <param name="hnsw">True for "qdrant-hnsw", false for the builder's exact sink ("qdrant").</param>
   /// <returns>The target.</returns>
   private BenchTarget QdrantContainerTarget( bool hnsw )
   {
      string composePath = Path.Combine( _repoRoot, "deploy", "engines", QdrantContainer.COMPOSE_FILE );
      QdrantContainer server = NewQdrantContainer( composePath, _qdrantAtStart );
      string name = hnsw ? "qdrant-hnsw" : "qdrant";
      string index = hnsw ? QdrantHnswSink.DescribeIndex( _hnswEf ) : QDRANT_EXACT_INDEX;
      Func<string, string> collection = hnsw ? QdrantHnswSink.CollectionName : c => $"gvb_{c}";
      var text = new SinkText( () => server.Engine, () => index, QdrantContainer.COMPOSE_FILE, () => server.Durability );
      EngineRoute route = EngineRoute.Async( new[] { QdrantContainer.CONTAINER }, async ( router, ct ) =>
      {
         await server.BindAsync( router, ct );
         return hnsw
            ? new QdrantHnswSink( server.Client, server.Server, _hnswEf, server.Version, server.Durability, server.Engine, QdrantContainer.COMPOSE_FILE )
            : new NamedSink( name, () => new QdrantSink( server.Client ), text );
      } );
      string collections = Path.Combine( GvbSettings.Expand( ENGINE_DATA_ROOT ), "qdrant", "collections" );
      return new BenchTarget( new NamedSink( name, () => throw Unreached( name ), text ), server.Engine, index, "compose", composePath,
         ( _, ct ) => ResourceProbe.DockerRamAsync( composePath, ct ),
         ( c, ct ) => ResourceProbe.FolderSizeAsync( Path.Combine( collections, collection( c ) ), "collection folder", ct ) )
      {
         Settle = hnsw ? null : ( c, ct ) => server.Server.WaitUntilSettledAsync( name, collection( c ), false, ct ),
         IndexStateReader = hnsw ? null : ( c, ct ) => server.Server.StateAsync( collection( c ), SearchExpectation.ScanEveryVector, ct ),
         IndexStep = hnsw,
         Container = new ContainerBinding( route, _inspector, BIND_DEADLINE, BIND_POLL ),
      };
   }

   /// <summary>
   /// A context for the benchmark's SQL Server container, falling back to what was read at the start
   /// of the run until it reaches the server itself.
   /// </summary>
   /// <param name="composePath">The container's compose file.</param>
   /// <returns>The context.</returns>
   private SqlServerContainer NewSqlContainer( string composePath )
   {
      return new SqlServerContainer( _inspector, Path.Combine( GvbSettings.Expand( ENGINE_DATA_ROOT ), "mssql" ), ComposeRunner.ImageOf( composePath ), new[] { SQL_BENCH_DATABASE, SqlDiskAnnSink.DATABASE } )
      {
         Earlier = _sqlAtStart,
      };
   }

   /// <summary>
   /// A context for the benchmark's Qdrant container, owned (and disposed) by the factory.
   /// </summary>
   /// <param name="composePath">The container's compose file.</param>
   /// <param name="earlier">What was read at the start of the run, or null.</param>
   /// <returns>The context.</returns>
   private QdrantContainer NewQdrantContainer( string composePath, QdrantContainer? earlier )
   {
      var server = new QdrantContainer( _inspector, ComposeRunner.ImageOf( composePath ), _log ) { Earlier = earlier };
      _owned.Add( server );
      return server;
   }

   /// <summary>
   /// Reads the benchmark's SQL Server and Qdrant containers when they are already running, so their
   /// targets carry the real build, CPUs and durability from the first line of the report. A container
   /// that is not running is skipped without a word (run-all starts it at its turn, and the target
   /// reads it then); one that runs but cannot be read within 30 s becomes a note.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   private async Task ReadRunningContainersAsync( CancellationToken ct )
   {
      string sqlCompose = Path.Combine( _repoRoot, "deploy", "engines", SqlServerContainer.COMPOSE_FILE );
      var sql = NewSqlContainer( sqlCompose );
      _sqlAtStart = await ReachIfRunningAsync( SqlServerContainer.CONTAINER, sql.BindAsync, ct ) ? sql : null;
      QdrantContainer qdrant = NewQdrantContainer( Path.Combine( _repoRoot, "deploy", "engines", QdrantContainer.COMPOSE_FILE ), null );
      _qdrantAtStart = await ReachIfRunningAsync( QdrantContainer.CONTAINER, qdrant.BindAsync, ct ) ? qdrant : null;
   }

   /// <summary>
   /// Binds a context to its container when the container is running, within <see cref="START_READ_LIMIT"/>.
   /// </summary>
   /// <param name="container">Container name.</param>
   /// <param name="bind">The context's bind.</param>
   /// <param name="ct">Cancellation of the run.</param>
   /// <returns>True when the container was running and was read.</returns>
   private async Task<bool> ReachIfRunningAsync( string container, Func<ContainerRouter, CancellationToken, Task> bind, CancellationToken ct )
   {
      using var limit = CancellationTokenSource.CreateLinkedTokenSource( ct );
      limit.CancelAfter( START_READ_LIMIT );
      try
      {
         if( await _inspector.InspectAsync( container, limit.Token ) is not string json || !ContainerFacts.Parse( json ).Running )
         {
            return false;
         }

         await bind( await ContainerRouter.ResolveAsync( _inspector, new[] { container }, BIND_DEADLINE, BIND_POLL, limit.Token ), limit.Token );
         return true;
      }
      catch( Exception ex ) when( ex is InvalidOperationException or SqlException or HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested )
      {
         _notes.Add( $"Container {container} was running when the run started but could not be read within {START_READ_LIMIT.TotalSeconds:0} s: {ex.Message}" );
         return false;
      }
   }

   /// <summary>
   /// The error for a container target's template sink, which must never do work: the target hands
   /// out the sink built on the container's address instead.
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <returns>The exception.</returns>
   private static InvalidOperationException Unreached( string name )
   {
      return new InvalidOperationException( $"Target '{name}' has not reached its container yet; use the target's Sink or BindAsync, which connect to the container's own address." );
   }

   /// <summary>
   /// A SQL Server target: memory is the whole server's, disk is the table's reserved pages.
   /// </summary>
   /// <param name="sink">The sink.</param>
   /// <param name="database">Database it writes to.</param>
   /// <param name="searchedTable">Maps a benchmark collection to the table searches read.</param>
   /// <param name="index">Index description.</param>
   /// <param name="builderSink">True for the builder's own SQL sink, which this benchmark wraps: it gets an index-state reader that
   /// proves from the plan of a real search that no vector index is used, and the durability text. False for a sink that does both itself.</param>
   /// <param name="pair">The passes a consolidated report compares by default for this target, or null.</param>
   /// <returns>The target.</returns>
   private BenchTarget SqlTarget( ISink sink, string database, Func<string, string> searchedTable, string index, bool builderSink, PassPair? pair = null )
   {
      return new BenchTarget( sink, $"{_sqlVersion} ({NATIVE_LABEL})", index, "always-on", null,
         ( _, ct ) => ResourceProbe.SqlServerRamAsync( _sqlConnection, ct ),
         ( c, ct ) => ResourceProbe.SqlTableSizeAsync( _sqlConnection, database, searchedTable( c ), ct ) )
      {
         IndexStateReader = builderSink ? ( c, ct ) => SqlProbe.ExactStateAsync( _sqlConnection, database, searchedTable( c ), ct ) : null,
         DurabilityNote = builderSink ? _sqlDurability ?? NOT_READ : null,
         DirectNote = $"always-on native SQL Server at {_settings.SqlServer}, reached over loopback (a native service, not a container port published through docker-proxy); a comparison target, not the container CPU split",
         PairHint = pair,
      };
   }

   /// <summary>
   /// A Qdrant target: memory is the whole Qdrant process, disk is the collection's folder.
   /// </summary>
   /// <param name="sink">The sink.</param>
   /// <param name="collectionName">Maps a benchmark collection to the Qdrant collection name.</param>
   /// <param name="index">Index description.</param>
   /// <param name="builderSink">True for the builder's own sink, which this benchmark wraps: it gets a settle wait, an
   /// index-state reader that proves every search scans, and the durability text. False for a sink that does those itself.</param>
   /// <returns>The target.</returns>
   private BenchTarget QdrantTarget( ISink sink, Func<string, string> collectionName, string index, bool builderSink )
   {
      string engine = sink is IEngineDescription description ? description.Engine : $"Qdrant {_qdrantVersion} ({NATIVE_LABEL})";
      return new BenchTarget( sink, engine, index, "always-on", null,
         ( _, ct ) => ResourceProbe.ProcessRssAsync( "qdrant", "whole Qdrant process, every collection", ct ),
         ( c, ct ) => ResourceProbe.FolderSizeAsync( Path.Combine( GvbSettings.Expand( QDRANT_COLLECTIONS ), collectionName( c ) ), "collection folder", ct ) )
      {
         Settle = builderSink ? ( c, ct ) => _qdrantServer.WaitUntilSettledAsync( sink.Name, collectionName( c ), false, ct ) : null,
         IndexStateReader = builderSink ? ( c, ct ) => _qdrantServer.StateAsync( collectionName( c ), SearchExpectation.ScanEveryVector, ct ) : null,
         DurabilityNote = builderSink ? _qdrantDurability ?? NOT_READ : null,
         DirectNote = $"always-on native Qdrant at {_settings.QdrantHost}, gRPC {_settings.QdrantGrpcPort} and HTTP {QDRANT_HTTP_PORT}, reached over loopback (a native service, not a container port published through docker-proxy); a comparison target, not the container CPU split",
      };
   }

   /// <summary>
   /// Reads the SQL Server and Qdrant durability settings once, so every target of a run reports
   /// the same text. A failure becomes a note and a placeholder, never a stopped run.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   private async Task ReadDurabilityAsync( CancellationToken ct )
   {
      try
      {
         _sqlDurability = await SqlDurability.ReadAsync( _sqlConnection, new[] { SQL_BENCH_DATABASE, SqlDiskAnnSink.DATABASE }, ct );
      }
      catch( Exception ex ) when( ex is not OperationCanceledException || !ct.IsCancellationRequested )
      {
         _notes.Add( $"Could not read the SQL Server durability settings: {ex.Message}" );
         _sqlDurability = $"{NOT_READ}: the SQL Server durability settings could not be read ({ex.Message})";
      }

      try
      {
         _qdrantDurability = await QdrantServer.ReadDurabilityAsync( GvbSettings.Expand( QDRANT_CONFIG ), _qdrantVersion, ct );
      }
      catch( Exception ex ) when( ex is not OperationCanceledException || !ct.IsCancellationRequested )
      {
         _notes.Add( $"Could not read the Qdrant durability settings: {ex.Message}" );
         _qdrantDurability = $"{NOT_READ}: the Qdrant durability settings could not be read ({ex.Message})";
      }
   }

   /// <summary>
   /// An engine sink from the Engines assembly, hosted by compose, in process, or always on. A
   /// compose-hosted engine's data folder is named after its compose file; MariaDB's compose file is the
   /// benchmark's own (<see cref="MariaBench.COMPOSE_FILE"/>, not the sink's mariadb.compose.yaml of the daily
   /// container), so its disk figure is read from the benchmark container's folder.
   /// </summary>
   /// <param name="sink">The sink.</param>
   /// <returns>The target.</returns>
   private BenchTarget EngineTarget( ISink sink )
   {
      var description = sink as IEngineDescription;
      string composeFile = ( MariaBench.Owns( sink.Name ) ? MariaBench.COMPOSE_FILE : description?.ComposeFile ) ?? "always-on";
      string engine = description?.Engine ?? sink.GetType().Name;
      string index = description?.IndexDescription ?? "not described";
      if( composeFile == "embedded" )
      {
         string folder = Path.Combine( GvbSettings.Expand( ENGINE_DATA_ROOT ), sink.Name );
         return new BenchTarget( sink, engine, index, "embedded", null, ( _, _ ) => Task.FromResult( Measurement.None( "in this process, not measured" ) ),
            ( _, ct ) => ResourceProbe.FolderSizeAsync( folder, "engine data folder", ct ) );
      }

      if( !composeFile.EndsWith( ".compose.yaml", StringComparison.Ordinal ) )
      {
         return new BenchTarget( sink, engine, index, "always-on", null, ( _, _ ) => Task.FromResult( Measurement.None( "not measured" ) ),
            ( _, _ ) => Task.FromResult( Measurement.None( "not measured" ) ) );
      }

      string key = composeFile[..^".compose.yaml".Length];
      string composePath = Path.Combine( _repoRoot, "deploy", "engines", composeFile );
      string dataFolder = Path.Combine( GvbSettings.Expand( ENGINE_DATA_ROOT ), key );
      EngineRoute route = ContainerRoutes.Find( sink.Name )
         ?? throw new ArgumentException( $"Target '{sink.Name}' is hosted by compose but has no entry in ContainerRoutes, so it would be measured through docker-proxy. Add its container route first." );
      return new BenchTarget( sink, engine, index, "compose", composePath, ( _, ct ) => ResourceProbe.DockerRamAsync( composePath, ct ),
         ( _, ct ) => ResourceProbe.FolderSizeAsync( dataFolder, "whole engine data folder", ct ) )
      {
         Container = new ContainerBinding( route, _inspector, BIND_DEADLINE, BIND_POLL ),
      };
   }

   /// <summary>
   /// Instantiates every engine sink. Uses <see cref="EngineCatalog.CreateAll"/>; if one
   /// sink's constructor throws (which would lose them all), falls back to building them one
   /// at a time so a single broken engine is reported and the rest still run.
   /// </summary>
   /// <param name="notes">Where problems are recorded.</param>
   /// <returns>Sinks by name.</returns>
   private static Dictionary<string, ISink> LoadEngines( List<string> notes )
   {
      try
      {
         return new Dictionary<string, ISink>( EngineCatalog.CreateAll(), StringComparer.OrdinalIgnoreCase );
      }
      catch( Exception ex ) when( ex is TargetInvocationException or ArgumentException or InvalidOperationException )
      {
         notes.Add( $"EngineCatalog.CreateAll failed ({ex.InnerException?.Message ?? ex.Message}); engines were loaded one at a time instead." );
      }

      var sinks = new Dictionary<string, ISink>( StringComparer.OrdinalIgnoreCase );
      IEnumerable<Type> types = typeof( EngineCatalog ).Assembly.GetTypes()
         .Where( t => t is { IsClass: true, IsAbstract: false } && typeof( ISink ).IsAssignableFrom( t ) && t.GetConstructor( Type.EmptyTypes ) != null );
      foreach( Type type in types )
      {
         try
         {
            var sink = (ISink)Activator.CreateInstance( type )!;
            sinks.TryAdd( sink.Name, sink );
         }
         catch( Exception ex ) when( ex is TargetInvocationException or InvalidOperationException )
         {
            notes.Add( $"Engine {type.Name} could not be created: {ex.InnerException?.Message ?? ex.Message}" );
         }
      }

      return sinks;
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Disposes the sinks the targets built on container addresses, the clients of the benchmark's own
   /// Qdrant container, the native Qdrant client and every engine sink that holds resources.
   /// </summary>
   public void Dispose()
   {
      _created.ForEach( t => t.Dispose() );
      _owned.ForEach( o => o.Dispose() );
      _qdrantServer.Dispose();
      _qdrant.Dispose();
      foreach( ISink sink in _engines.Values )
      {
         ( sink as IDisposable )?.Dispose();
      }
   }

   #endregion IDisposable
}
