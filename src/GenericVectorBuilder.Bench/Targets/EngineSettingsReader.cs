using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using GenericVectorBuilder.Engines.Sinks;
using Microsoft.Data.Sqlite;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// Reads the settings of one target's engine that decide how fast it can go (container memory and
/// CPU limits, heaps, buffer pools, worker counts, the planner settings a search runs under) from
/// the running engine or from Docker, so the results say what the engine was running with and
/// not what a compose file says it should run with.
/// How each is read: container limits from "docker inspect"; Redis, MariaDB, PostgreSQL and
/// Oracle through "docker exec" of the engine's own command-line client, which takes its password
/// from the container's own environment, so no password is read, held or sent by this tool;
/// Elasticsearch and OpenSearch with GET _nodes/jvm and ClickHouse with a query, both at the
/// container's own address from <see cref="BenchTarget.Connections"/>; Weaviate's GOMEMLIMIT from the
/// container's environment (that one name only); Qdrant's search threads from the thread names of
/// its process; DuckDB and SQLite in this process.
/// Why only a fixed list of keys (<see cref="ALLOWED_KEYS"/>): every entry lands in results.json and
/// on a public page, so a key that is not on the list is a bug, and a secret can never arrive as a setting.
/// Why every read has a time limit (<see cref="READ_TIMEOUT"/>) and a failed read is an error that
/// names the key, the target and how it was read: a settings table with a hole in it would look
/// complete, and the run it describes is hours long.
/// Why the readers connect to nothing that outlives them: the run records which connections the
/// client holds after its passes, and a settings read that left a socket open would show up there
/// as a connection that is not part of the measurement.
/// </summary>
public sealed class EngineSettingsReader
{
   #region Data Members

   /// <summary>Longest one setting's read may take.</summary>
   public static readonly TimeSpan READ_TIMEOUT = TimeSpan.FromSeconds( 15 );

   /// <summary>The environment names whose values are kept from "docker inspect"; every other value is dropped.</summary>
   public static readonly IReadOnlyCollection<string> ENV_KEYS = new[] { "GOMEMLIMIT" };

   /// <summary>The only keys an entry may carry. A key not on this list throws when an entry is made.</summary>
   public static readonly IReadOnlySet<string> ALLOWED_KEYS = new HashSet<string>( StringComparer.Ordinal )
   {
      "none", "HostConfig.Memory", "HostConfig.CpusetCpus", "HostConfig.NanoCpus",
      "search-workers", "save", "appendonly", "maxmemory",
      "heap_init_in_bytes", "heap_max_in_bytes", "chroma_version", "oracle_version",
      "innodb_buffer_pool_size", "innodb_flush_log_at_trx_commit", "mhnsw_max_cache_size",
      "GOMEMLIMIT", "enable_seqscan", "jit", "shared_buffers", "cpu_count", "sga_target", "max_threads", "search_threads",
      "duckdb_version", "threads", "memory_limit", "checkpoint_threshold", "hnsw_ef_search", "hnsw_enable_experimental_persistence",
      "sqlite_version", "journal_mode", "synchronous",
   };

   /// <summary>The repository path of the PostgreSQL sink, which sets the planner settings the search runs under.</summary>
   public const string PGVECTOR_SINK = "src/GenericVectorBuilder.Engines/Sinks/PgVectorSink.cs";

   /// <summary>The two statements the PostgreSQL sink runs before every default search (PgVectorSink.SEARCH_SETTINGS up to its ef_search).</summary>
   public const string PGVECTOR_SEARCH_SETTINGS = "SET LOCAL enable_seqscan = off; SET LOCAL jit = off;";

   /// <summary>The repository path of the DuckDB sink.</summary>
   public const string DUCKDB_SINK = "src/GenericVectorBuilder.Engines/Sinks/DuckDbSink.cs";

   /// <summary>The repository path of the sqlite-vec sink.</summary>
   public const string SQLITEVEC_SINK = "src/GenericVectorBuilder.Engines/Sinks/SqliteVecSink.cs";

   private const string NO_CONTAINER = "no container";
   private static readonly Regex KEY_VALUE = new( @"^(?<key>[A-Za-z_][A-Za-z0-9_.\-]*)=(?<value>.*)$", RegexOptions.Compiled );
   private static readonly Regex WHITESPACE = new( @"\s+", RegexOptions.Compiled );
   private static readonly Regex VERSION = new( @"^[0-9]+(\.[0-9A-Za-z]+)+([\-+][0-9A-Za-z.\-]+)?$", RegexOptions.Compiled );

   private readonly IEngineProbe _probe;
   private readonly Func<ClickHouseHttp> _clickHouse;
   private readonly Func<string, int?> _searchThreads;
   private readonly TimeSpan _readTimeout;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the reader.
   /// </summary>
   /// <param name="probe">Runs Docker commands and web reads.</param>
   /// <param name="clickHouse">Makes the ClickHouse web client (its password comes from secrets.env there); null for the real one.</param>
   /// <param name="searchThreads">Counts the search threads of the Qdrant process in a container given the container's 12-character id; null for the real /proc reader.</param>
   /// <param name="readTimeout">Longest one setting's read may take; null for <see cref="READ_TIMEOUT"/> (a test passes a short one).</param>
   public EngineSettingsReader( IEngineProbe probe, Func<ClickHouseHttp>? clickHouse = null, Func<string, int?>? searchThreads = null, TimeSpan? readTimeout = null )
   {
      _probe = probe;
      _clickHouse = clickHouse ?? ClickHouseHttp.Local;
      _searchThreads = searchThreads ?? CountQdrantSearchThreads;
      _readTimeout = readTimeout ?? READ_TIMEOUT;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Reads a target's settings as the engine stands after it was bound: the container limits first,
   /// then the engine's own, in a fixed order so two runs list them alike. A target with no container
   /// gets one entry that says nothing was read (never an empty list).
   /// </summary>
   /// <param name="target">The target, already bound.</param>
   /// <param name="container">What "docker inspect" said about its container, or null when it has none.</param>
   /// <param name="ct">Cancellation of the whole run.</param>
   /// <returns>The settings, never empty.</returns>
   /// <exception cref="InvalidOperationException">A read failed or took longer than <see cref="READ_TIMEOUT"/>; the message names the target, the key and how it was read.</exception>
   public async Task<IReadOnlyList<SettingRead>> ReadAsync( BenchTarget target, ContainerDescription? container, CancellationToken ct )
   {
      if( container == null )
      {
         return target.Hosting == "embedded" && IsEmbeddedWithReader( target.Name )
            ? await ReadEmbeddedAsync( target.Name, ct )
            : new[] { Entry( "none", "not read", target.Hosting == "embedded" ? "embedded engine without a settings reader" : NO_CONTAINER ) };
      }

      var settings = new List<SettingRead>
      {
         Entry( "HostConfig.Memory", container.MemoryBytes.ToString( CultureInfo.InvariantCulture ), "read: docker inspect HostConfig.Memory (0 = no limit)" ),
         Entry( "HostConfig.CpusetCpus", container.Cpuset, "read: docker inspect HostConfig.CpusetCpus (empty = every CPU)" ),
         Entry( "HostConfig.NanoCpus", container.NanoCpus.ToString( CultureInfo.InvariantCulture ), "read: docker inspect HostConfig.NanoCpus (0 = no limit)" ),
      };
      settings.AddRange( await ReadEngineAsync( target, container, ct ) );
      return settings;
   }

   /// <summary>
   /// The settings of an embedded engine that live in its bench database file or, when there is no
   /// file (a settings-only run, which loads nothing), in the sink's own code: DuckDB's memory limit
   /// and checkpoint threshold, SQLite's journal mode. Read after the last timed pass, from a second
   /// connection, so the first read of the file is never inside a measurement.
   /// Why the same connection string as the sink for DuckDB: DuckDB.NET shares one database instance
   /// per file name and refuses a second configuration, so the read must open the file exactly as
   /// the sink does, and it then sees the values the sink set.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="benchFile">The bench database file after the searches, or null when none exists.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The entries to add after the others; empty for a target that is not DuckDB or sqlite-vec.</returns>
   public async Task<IReadOnlyList<SettingRead>> ReadEmbeddedFileAsync( BenchTarget target, string? benchFile, CancellationToken ct )
   {
      if( target.Hosting != "embedded" || !IsEmbeddedWithReader( target.Name ) )
      {
         return Array.Empty<SettingRead>();
      }

      return target.Name == "duckdb" ? await ReadDuckDbFileAsync( benchFile, ct ) : await ReadSqliteFileAsync( benchFile, ct );
   }

   /// <summary>
   /// Reads "KEY=value" lines (psql, sqlplus and similar clients print them one per line) into a
   /// dictionary; lines that are not of that form (command tags, blank lines) are ignored.
   /// </summary>
   /// <param name="output">The client's output.</param>
   /// <returns>Key to value (the last of a repeated key wins).</returns>
   public static Dictionary<string, string> ParseKeyValues( string output )
   {
      var values = new Dictionary<string, string>( StringComparer.Ordinal );
      foreach( string raw in output.Split( '\n' ) )
      {
         Match match = KEY_VALUE.Match( raw.Trim( '\r', ' ', '\t' ) );
         if( match.Success )
         {
            values[match.Groups["key"].Value] = match.Groups["value"].Value.Trim();
         }
      }

      return values;
   }

   /// <summary>
   /// Reads the answer of "redis-cli CONFIG GET name": the name on one line and its value on the next
   /// (an empty value is an empty line).
   /// </summary>
   /// <param name="name">The configuration name asked for.</param>
   /// <param name="output">The client's output.</param>
   /// <returns>The value, possibly empty.</returns>
   /// <exception cref="InvalidOperationException">The output does not start with the name (an error message, or nothing: the name is unknown to this Redis).</exception>
   public static string ParseRedisConfig( string name, string output )
   {
      string[] lines = output.Replace( "\r", string.Empty ).Split( '\n' );
      if( lines.Length < 1 || lines[0] != name )
      {
         throw new InvalidOperationException( $"redis-cli CONFIG GET {name} did not answer with the name and its value: {Squeeze( output, 120 )}" );
      }

      return lines.Length > 1 ? lines[1].Trim() : string.Empty;
   }

   /// <summary>
   /// Reads the answer of Chroma's GET /api/v2/version: a JSON string such as "1.4.4" (the bare text is accepted too).
   /// </summary>
   /// <param name="body">The response body.</param>
   /// <returns>The version.</returns>
   /// <exception cref="InvalidOperationException">The body is not a version number.</exception>
   public static string ParseChromaVersion( string body )
   {
      string text = body.Trim();
      if( text.StartsWith( '"' ) )
      {
         try
         {
            text = JsonSerializer.Deserialize<string>( text ) ?? string.Empty;
         }
         catch( JsonException )
         {
            throw new InvalidOperationException( $"GET /api/v2/version did not answer with a JSON string: {Squeeze( body, 80 )}" );
         }
      }

      return VERSION.IsMatch( text ) ? text : throw new InvalidOperationException( $"GET /api/v2/version did not answer with a version number: {Squeeze( body, 80 )}" );
   }

   /// <summary>
   /// Reads tab-separated "name value" lines, as "mariadb -N -B" prints them.
   /// </summary>
   /// <param name="output">The client's output.</param>
   /// <returns>Name to value.</returns>
   public static Dictionary<string, string> ParseTabbed( string output )
   {
      var values = new Dictionary<string, string>( StringComparer.Ordinal );
      foreach( string[] parts in output.Replace( "\r", string.Empty ).Split( '\n', StringSplitOptions.RemoveEmptyEntries ).Select( l => l.Split( '\t' ) ).Where( p => p.Length == 2 ) )
      {
         values[parts[0].Trim()] = parts[1].Trim();
      }

      return values;
   }

   /// <summary>
   /// Reads the heap sizes from the answer of GET _nodes/jvm of a one-node Elasticsearch or OpenSearch.
   /// </summary>
   /// <param name="json">The response body.</param>
   /// <returns>heap_init_in_bytes and heap_max_in_bytes, as text.</returns>
   /// <exception cref="InvalidOperationException">The answer does not hold exactly one node with both figures.</exception>
   public static (string Init, string Max) ParseHeap( string json )
   {
      try
      {
         using JsonDocument document = JsonDocument.Parse( json );
         JsonElement nodes = document.RootElement.GetProperty( "nodes" );
         List<JsonProperty> list = nodes.EnumerateObject().ToList();
         if( list.Count != 1 )
         {
            throw new InvalidOperationException( $"_nodes/jvm lists {list.Count} nodes; a one-node engine was expected" );
         }

         JsonElement memory = list[0].Value.GetProperty( "jvm" ).GetProperty( "mem" );
         return ( memory.GetProperty( "heap_init_in_bytes" ).GetRawText(), memory.GetProperty( "heap_max_in_bytes" ).GetRawText() );
      }
      catch( Exception ex ) when( ex is JsonException or KeyNotFoundException or InvalidOperationException or InvalidCastException )
      {
         throw new InvalidOperationException( $"GET _nodes/jvm did not carry nodes.<id>.jvm.mem.heap_init_in_bytes and heap_max_in_bytes ({( ex is InvalidOperationException ? ex.Message : ex.GetType().Name )})" );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Makes one entry, refusing a key that is not on the allowed list.
   /// </summary>
   /// <param name="key">The key.</param>
   /// <param name="value">The value.</param>
   /// <param name="how">How it was obtained.</param>
   /// <returns>The entry.</returns>
   /// <exception cref="InvalidOperationException">The key is not allowed.</exception>
   private static SettingRead Entry( string key, string value, string how )
   {
      return ALLOWED_KEYS.Contains( key ) ? new SettingRead( key, value, how ) : throw new InvalidOperationException( $"'{key}' is not one of the setting keys this benchmark records." );
   }

   /// <summary>
   /// Whether the target is one of the two embedded engines this reader knows.
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <returns>True for duckdb and sqlitevec.</returns>
   private static bool IsEmbeddedWithReader( string name )
   {
      return name is "duckdb" or "sqlitevec";
   }

   /// <summary>
   /// The engine-specific settings of a container target, in a fixed order.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="container">Its container.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The entries; none for an engine with nothing beyond the container limits.</returns>
   private async Task<IReadOnlyList<SettingRead>> ReadEngineAsync( BenchTarget target, ContainerDescription container, CancellationToken ct )
   {
      return target.Name switch
      {
         "redis" => await ReadRedisAsync( target, container.Name, ct ),
         "elasticsearch" or "opensearch" => await ReadHeapAsync( target, container, ct ),
         MariaBench.TARGET => await ReadMariaDbAsync( target, container.Name, ct ),
         "pgvector" => await ReadPgVectorAsync( target, container.Name, ct ),
         "oracle" => await ReadOracleAsync( target, container.Name, ct ),
         "chroma" => await ReadChromaAsync( target, container, ct ),
         "clickhouse" => await ReadClickHouseAsync( target, container, ct ),
         "qdrant" or "qdrant-hnsw" => ReadQdrant( target, container ),
         "weaviate" => ReadWeaviate( target, container ),
         _ => Array.Empty<SettingRead>(),
      };
   }

   /// <summary>
   /// Redis: search-workers, save, appendonly and maxmemory with "CONFIG GET", one exec each.
   /// The script goes through "sh -c" inside the container so the shell there expands the
   /// container's own REDIS_PASSWORD; the host never sees the password.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="container">Container name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The four entries.</returns>
   private async Task<IReadOnlyList<SettingRead>> ReadRedisAsync( BenchTarget target, string container, CancellationToken ct )
   {
      var entries = new List<SettingRead>();
      foreach( string name in new[] { "search-workers", "save", "appendonly", "maxmemory" } )
      {
         string how = $"read: docker exec {container} redis-cli CONFIG GET {name}";
         string value = await Guarded( target.Name, name, how, async limit => ParseRedisConfig( name, await ExecAsync( container, $"REDISCLI_AUTH=\"$REDIS_PASSWORD\" redis-cli CONFIG GET {name}", limit ) ), ct );
         entries.Add( Entry( name, value, how ) );
      }

      return entries;
   }

   /// <summary>
   /// Elasticsearch and OpenSearch: heap_init_in_bytes and heap_max_in_bytes from GET _nodes/jvm at
   /// the container's own address.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="container">Its container.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The two entries.</returns>
   private async Task<IReadOnlyList<SettingRead>> ReadHeapAsync( BenchTarget target, ContainerDescription container, CancellationToken ct )
   {
      string how = "read: GET /_nodes/jvm (nodes.<id>.jvm.mem.heap_init_in_bytes and heap_max_in_bytes)";
      ContainerAddress address = AddressOf( target, container.Name );
      (string init, string max) = await Guarded( target.Name, "heap_init_in_bytes and heap_max_in_bytes", how,
         async limit => ParseHeap( await _probe.HttpGetAsync( $"{address.Url( "http" )}/_nodes/jvm", _readTimeout, limit ) ), ct );
      return new[] { Entry( "heap_init_in_bytes", init, how ), Entry( "heap_max_in_bytes", max, how ) };
   }

   /// <summary>
   /// MariaDB of the benchmark's own container only: the buffer pool, the flush setting and the
   /// HNSW graph cache with SHOW GLOBAL VARIABLES. Refuses any other container, so the daily
   /// gvb-mariadb can never be asked.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="container">Container name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The three entries.</returns>
   private async Task<IReadOnlyList<SettingRead>> ReadMariaDbAsync( BenchTarget target, string container, CancellationToken ct )
   {
      if( container != MariaBench.CONTAINER )
      {
         throw new InvalidOperationException( $"The mariadb target's settings are read only from {MariaBench.CONTAINER}, not from '{container}' (the daily gvb-mariadb is never asked)." );
      }

      string[] keys = { "innodb_buffer_pool_size", "innodb_flush_log_at_trx_commit", "mhnsw_max_cache_size" };
      string how = $"read: docker exec {container} mariadb SHOW GLOBAL VARIABLES";
      string script = "MYSQL_PWD=\"$MARIADB_ROOT_PASSWORD\" mariadb -uroot -N -B -e \"SHOW GLOBAL VARIABLES WHERE Variable_name IN ( " + string.Join( ", ", keys.Select( k => $"'{k}'" ) ) + " )\"";
      Dictionary<string, string> values = await Guarded( target.Name, string.Join( ", ", keys ), how, async limit => ParseTabbed( await ExecAsync( container, script, limit ) ), ct );
      return keys.Select( k => Entry( k, Required( target.Name, k, values ), how ) ).ToList();
   }

   /// <summary>
   /// PostgreSQL: shared_buffers read live, and the two planner settings the sink's own search runs
   /// under, set and read back inside one transaction. Why the latter are labelled "set": reading back
   /// what was just set in the same transaction shows only that the server accepted the statement; the
   /// value in force during a search is the sink's, in the file named in the entry.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="container">Container name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The three entries.</returns>
   private async Task<IReadOnlyList<SettingRead>> ReadPgVectorAsync( BenchTarget target, string container, CancellationToken ct )
   {
      string script = "PGPASSWORD=\"$POSTGRES_PASSWORD\" psql -h 127.0.0.1 -U postgres -d gvb -X -q -At -c \"BEGIN; " + PGVECTOR_SEARCH_SETTINGS
         + " SELECT 'enable_seqscan=' || current_setting('enable_seqscan'); SELECT 'jit=' || current_setting('jit'); SELECT 'shared_buffers=' || current_setting('shared_buffers'); COMMIT;\"";
      string how = $"read: docker exec {container} psql current_setting";
      Dictionary<string, string> values = await Guarded( target.Name, "enable_seqscan, jit, shared_buffers", how, async limit => ParseKeyValues( await ExecAsync( container, script, limit ) ), ct );
      string set = $"set: {PGVECTOR_SINK}#{PGVECTOR_SEARCH_SETTINGS}";
      return new[]
      {
         Entry( "shared_buffers", Required( target.Name, "shared_buffers", values ), how ),
         Entry( "enable_seqscan", Required( target.Name, "enable_seqscan", values ), set ),
         Entry( "jit", Required( target.Name, "jit", values ), set ),
      };
   }

   /// <summary>
   /// Oracle: cpu_count and sga_target from V$PARAMETER and the instance's full version from V$INSTANCE, asked
   /// as SYSDBA inside the container (the operating system login of the container's own user, so no password
   /// is involved). Read at the instance, not through the pluggable database, where sga_target can show 0.
   /// Why the version: the sink states its version as a fixed text, and the image tag floats.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="container">Container name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The three entries.</returns>
   private async Task<IReadOnlyList<SettingRead>> ReadOracleAsync( BenchTarget target, string container, CancellationToken ct )
   {
      const string SCRIPT = "sqlplus -S / as sysdba <<'GVB_EOF'\nSET HEADING OFF\nSET FEEDBACK OFF\nSET PAGESIZE 0\nSET LINESIZE 200\n"
         + "SELECT name || '=' || value FROM v$parameter WHERE name IN ('cpu_count', 'sga_target') ORDER BY name;\n"
         + "SELECT 'oracle_version=' || version_full FROM v$instance;\nEXIT\nGVB_EOF";
      string how = $"read: docker exec {container} sqlplus / as sysdba, V$PARAMETER and V$INSTANCE at the instance";
      Dictionary<string, string> values = await Guarded( target.Name, "cpu_count, sga_target, oracle_version", how, async limit => ParseKeyValues( await ExecAsync( container, SCRIPT, limit ) ), ct );
      return new[]
      {
         Entry( "cpu_count", Required( target.Name, "cpu_count", values ), how ),
         Entry( "sga_target", Required( target.Name, "sga_target", values ), how ),
         Entry( "oracle_version", Required( target.Name, "oracle_version", values ), how ),
      };
   }

   /// <summary>
   /// Chroma: the server's version from GET /api/v2/version at the container's own address. Why: the sink
   /// states "Chroma 1.4.4" as a fixed text and the image tag (latest) floats, so the version in force is read.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="container">Its container.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The entry.</returns>
   private async Task<IReadOnlyList<SettingRead>> ReadChromaAsync( BenchTarget target, ContainerDescription container, CancellationToken ct )
   {
      const string HOW = "read: GET /api/v2/version";
      ContainerAddress address = AddressOf( target, container.Name );
      string version = await Guarded( target.Name, "chroma_version", HOW, async limit => ParseChromaVersion( await _probe.HttpGetAsync( $"{address.Url( "http" )}/api/v2/version", _readTimeout, limit ) ), ct );
      return new[] { Entry( "chroma_version", version, HOW ) };
   }

   /// <summary>
   /// ClickHouse: max_threads from system.settings, asked over HTTP at the container's own address.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="container">Its container.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The entry.</returns>
   private async Task<IReadOnlyList<SettingRead>> ReadClickHouseAsync( BenchTarget target, ContainerDescription container, CancellationToken ct )
   {
      const string HOW = "read: ClickHouse system.settings, name max_threads, over HTTP";
      ContainerAddress address = AddressOf( target, container.Name );
      string value = await Guarded( target.Name, "max_threads", HOW, async limit =>
      {
         using ClickHouseHttp http = _clickHouse();
         string answer = ( await http.QueryAsync( address, "SELECT value FROM system.settings WHERE name = 'max_threads' FORMAT TSV", limit ) ).Trim();
         return answer.Length > 0 && !answer.Contains( '\n' ) ? answer : throw new InvalidOperationException( $"max_threads is not one row: {Squeeze( answer, 80 )}" );
      }, ct );
      return new[] { Entry( "max_threads", value, HOW ) };
   }

   /// <summary>
   /// Qdrant: how many search threads its process started, counted from the thread names under /proc
   /// (the figure the engine text already reports, now an entry). Not a network read.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="container">Its container.</param>
   /// <returns>The entry.</returns>
   private IReadOnlyList<SettingRead> ReadQdrant( BenchTarget target, ContainerDescription container )
   {
      string how = $"read: threads named search-* of the qdrant process in container {container.Name} (/proc)";
      int? count = _searchThreads( container.Id.Length > 12 ? container.Id[..12] : container.Id );
      if( count == null )
      {
         throw new InvalidOperationException( $"Could not read the setting search_threads of {target.Name} ({how}): no qdrant process or no thread names were found for container {container.Name}." );
      }

      return new[] { Entry( "search_threads", count.Value.ToString( CultureInfo.InvariantCulture ), how ) };
   }

   /// <summary>
   /// Weaviate: GOMEMLIMIT from the container's environment, that one name only.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="container">Its container.</param>
   /// <returns>The entry.</returns>
   private static IReadOnlyList<SettingRead> ReadWeaviate( BenchTarget target, ContainerDescription container )
   {
      string how = "read: docker inspect Config.Env, the name GOMEMLIMIT only";
      return container.Env.TryGetValue( "GOMEMLIMIT", out string? value )
         ? new[] { Entry( "GOMEMLIMIT", value, how ) }
         : throw new InvalidOperationException( $"Could not read the setting GOMEMLIMIT of {target.Name} ({how}): the container's environment has no GOMEMLIMIT." );
   }

   /// <summary>
   /// Runs "docker exec CONTAINER sh -c SCRIPT" and returns its output, or fails with the tail of its error.
   /// </summary>
   /// <param name="container">Container name.</param>
   /// <param name="script">The script for "sh -c"; it may name the container's environment variables, never carry a password.</param>
   /// <param name="ct">Cancellation, carrying the read's time limit.</param>
   /// <returns>Standard output.</returns>
   /// <exception cref="InvalidOperationException">Docker exited with a failure.</exception>
   private async Task<string> ExecAsync( string container, string script, CancellationToken ct )
   {
      ShellResult result = await _probe.DockerAsync( new[] { "exec", container, "sh", "-c", script }, _readTimeout, ct );
      return result.ExitCode == 0 ? result.Output : throw new InvalidOperationException( $"docker exec {container} exited with code {result.ExitCode}: {Squeeze( result.Error, 200 )}" );
   }

   /// <summary>
   /// Runs one read within <see cref="READ_TIMEOUT"/> and turns any failure into an error that
   /// names the target, the key and how it was read (a bare "The operation was canceled." says nothing).
   /// </summary>
   /// <typeparam name="T">What the read returns.</typeparam>
   /// <param name="target">Target name.</param>
   /// <param name="key">The key or keys.</param>
   /// <param name="how">How it is read.</param>
   /// <param name="read">The read; it receives a token that fires at the time limit.</param>
   /// <param name="ct">Cancellation of the whole run.</param>
   /// <returns>The read's result.</returns>
   /// <exception cref="InvalidOperationException">The read failed or timed out.</exception>
   private async Task<T> Guarded<T>( string target, string key, string how, Func<CancellationToken, Task<T>> read, CancellationToken ct )
   {
      using var limit = CancellationTokenSource.CreateLinkedTokenSource( ct );
      limit.CancelAfter( _readTimeout );
      try
      {
         return await read( limit.Token );
      }
      catch( OperationCanceledException ) when( !ct.IsCancellationRequested )
      {
         throw new InvalidOperationException( $"Could not read the setting {key} of {target} ({how}): no answer within {_readTimeout.TotalSeconds:0.###} s." );
      }
      catch( Exception ex ) when( ex is InvalidOperationException or TimeoutException or HttpRequestException or System.ComponentModel.Win32Exception or IOException or System.Data.Common.DbException )
      {
         throw new InvalidOperationException( $"Could not read the setting {key} of {target} ({how}): {Squeeze( ex.Message, 300 )}" );
      }
   }

   /// <summary>
   /// A value a client printed under a key, or an error when it printed none.
   /// </summary>
   /// <param name="target">Target name.</param>
   /// <param name="key">The key.</param>
   /// <param name="values">What was parsed.</param>
   /// <returns>The value.</returns>
   /// <exception cref="InvalidOperationException">The key is missing.</exception>
   private static string Required( string target, string key, Dictionary<string, string> values )
   {
      return values.TryGetValue( key, out string? value ) ? value : throw new InvalidOperationException( $"Could not read the setting {key} of {target}: the engine's answer had no {key}." );
   }

   /// <summary>
   /// The container's own address among the target's connections.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="container">Container name.</param>
   /// <returns>The address.</returns>
   /// <exception cref="InvalidOperationException">The target recorded no address for the container.</exception>
   private static ContainerAddress AddressOf( BenchTarget target, string container )
   {
      return target.Connections.FirstOrDefault( c => c.Container == container )
         ?? throw new InvalidOperationException( $"{target.Name} has no recorded address for container {container}, so its settings cannot be read over the network." );
   }

   /// <summary>
   /// One line of at most <paramref name="max"/> characters: whitespace collapsed, cut with "...".
   /// </summary>
   /// <param name="text">The text.</param>
   /// <param name="max">Characters to keep.</param>
   /// <returns>The short text.</returns>
   private static string Squeeze( string text, int max )
   {
      string one = WHITESPACE.Replace( text, " " ).Trim();
      return one.Length <= max ? one : one[..max] + "...";
   }

   /// <summary>
   /// Counts the threads named search-* in the Qdrant process of a container, as the container start did.
   /// </summary>
   /// <param name="containerId">The container's 12-character id.</param>
   /// <returns>The count, or null when no process or no thread names were found.</returns>
   private static int? CountQdrantSearchThreads( string containerId )
   {
      int pid = HostProcess.FindInContainer( "qdrant", containerId ).FirstOrDefault();
      IReadOnlyList<string> names = pid > 0 ? HostProcess.ThreadNames( pid ) : Array.Empty<string>();
      return names.Count == 0 ? null : names.Count( n => n.StartsWith( "search-", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// The in-process settings of DuckDB or sqlite-vec that need no bench file: library versions
   /// and DuckDB's thread count read from throwaway in-memory databases, and the settings the sink's
   /// code sets for every connection.
   /// </summary>
   /// <param name="name">"duckdb" or "sqlitevec".</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The entries.</returns>
   private async Task<IReadOnlyList<SettingRead>> ReadEmbeddedAsync( string name, CancellationToken ct )
   {
      if( name == "duckdb" )
      {
         const string HOW = "read: SELECT {0} on an in-memory DuckDB connection";
         (string version, string threads) = await Guarded( name, "duckdb_version and threads", string.Format( HOW, "version() and current_setting('threads')" ),
            _ => Task.Run( ReadDuckDbInMemory, ct ).WaitAsync( _readTimeout, ct ), ct );
         var options = new DuckDbSinkOptions();
         return new[]
         {
            Entry( "duckdb_version", version, string.Format( HOW, "version()" ) ),
            Entry( "threads", threads, string.Format( HOW, "current_setting('threads')" ) + "; DuckDB's own thread count, not a CPU cap (it counts all CPUs and ignores affinity)" ),
            Entry( "hnsw_ef_search", options.HnswEfSearch.ToString( CultureInfo.InvariantCulture ), $"set: {DUCKDB_SINK}#SET hnsw_ef_search" ),
            Entry( "hnsw_enable_experimental_persistence", "true", $"set: {DUCKDB_SINK}#SET hnsw_enable_experimental_persistence = true" ),
         };
      }

      string sqliteVersion = await Guarded( name, "sqlite_version", "read: SELECT sqlite_version() on an in-memory SQLite connection", _ => Task.Run( ReadSqliteInMemory, ct ).WaitAsync( _readTimeout, ct ), ct );
      return new[]
      {
         Entry( "sqlite_version", sqliteVersion, "read: SELECT sqlite_version() on an in-memory SQLite connection" ),
         Entry( "synchronous", "NORMAL", $"set: {SQLITEVEC_SINK}#PRAGMA synchronous=NORMAL" ),
      };
   }

   /// <summary>
   /// DuckDB's version and thread count from a throwaway in-memory database.
   /// </summary>
   /// <returns>Version and thread count, as text.</returns>
   private static (string Version, string Threads) ReadDuckDbInMemory()
   {
      using var connection = new DuckDBConnection( "Data Source=:memory:" );
      connection.Open();
      return ( Scalar( connection, "SELECT version();" ), Scalar( connection, "SELECT current_setting('threads');" ) );
   }

   /// <summary>
   /// SQLite's version from a throwaway in-memory database.
   /// </summary>
   /// <returns>The version.</returns>
   private static string ReadSqliteInMemory()
   {
      using var connection = new SqliteConnection( "Data Source=:memory:" );
      connection.Open();
      return Scalar( connection, "SELECT sqlite_version();" );
   }

   /// <summary>
   /// DuckDB's memory limit and checkpoint threshold: from the bench file's instance when the file
   /// exists, else the sink's own defaults, each entry saying which.
   /// </summary>
   /// <param name="file">The bench file, or null.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The two entries.</returns>
   private async Task<IReadOnlyList<SettingRead>> ReadDuckDbFileAsync( string? file, CancellationToken ct )
   {
      var options = new DuckDbSinkOptions();
      if( file == null )
      {
         return new[]
         {
            Entry( "memory_limit", options.MemoryLimit, $"set: {DUCKDB_SINK}#SET memory_limit" ),
            Entry( "checkpoint_threshold", options.CheckpointThreshold, $"set: {DUCKDB_SINK}#SET checkpoint_threshold" ),
         };
      }

      const string HOW = "read: SELECT current_setting('{0}') on a second connection to the bench file opened with the sink's own connection string";
      (string limit, string threshold) = await Guarded( "duckdb", "memory_limit and checkpoint_threshold", string.Format( HOW, "memory_limit" ),
         _ => Task.Run( () => ReadDuckDbFile( file ), ct ).WaitAsync( _readTimeout, ct ), ct );
      return new[] { Entry( "memory_limit", limit, string.Format( HOW, "memory_limit" ) ), Entry( "checkpoint_threshold", threshold, string.Format( HOW, "checkpoint_threshold" ) ) };
   }

   /// <summary>
   /// Reads the two DuckDB settings from the bench file's instance.
   /// </summary>
   /// <param name="file">The bench file.</param>
   /// <returns>Memory limit and checkpoint threshold, as DuckDB prints them.</returns>
   private static (string Limit, string Threshold) ReadDuckDbFile( string file )
   {
      using var connection = new DuckDBConnection( $"Data Source={file}" );
      connection.Open();
      return ( Scalar( connection, "SELECT current_setting('memory_limit');" ), Scalar( connection, "SELECT current_setting('checkpoint_threshold');" ) );
   }

   /// <summary>
   /// SQLite's journal mode: from the bench file when it exists (the mode is stored in the file),
   /// else the sink's own statement.
   /// </summary>
   /// <param name="file">The bench file, or null.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The entry.</returns>
   private async Task<IReadOnlyList<SettingRead>> ReadSqliteFileAsync( string? file, CancellationToken ct )
   {
      if( file == null )
      {
         return new[] { Entry( "journal_mode", "WAL", $"set: {SQLITEVEC_SINK}#PRAGMA journal_mode=WAL" ) };
      }

      const string HOW = "read: PRAGMA journal_mode on a second, read-only connection to the bench file";
      string mode = await Guarded( "sqlitevec", "journal_mode", HOW, _ => Task.Run( () => ReadSqliteFile( file ), ct ).WaitAsync( _readTimeout, ct ), ct );
      return new[] { Entry( "journal_mode", mode, HOW ) };
   }

   /// <summary>
   /// Reads the journal mode through a read-only connection with pooling off, closed at once.
   /// </summary>
   /// <param name="file">The bench file.</param>
   /// <returns>The mode as SQLite prints it ("wal").</returns>
   private static string ReadSqliteFile( string file )
   {
      var builder = new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
      using var connection = new SqliteConnection( builder.ConnectionString );
      connection.Open();
      return Scalar( connection, "PRAGMA journal_mode;" );
   }

   /// <summary>
   /// Runs one scalar query on an open DuckDB connection and returns it as text.
   /// </summary>
   /// <param name="connection">The connection.</param>
   /// <param name="sql">The query.</param>
   /// <returns>The value as text.</returns>
   private static string Scalar( DuckDBConnection connection, string sql )
   {
      using System.Data.Common.DbCommand command = connection.CreateCommand();
      command.CommandText = sql;
      return Convert.ToString( command.ExecuteScalar(), CultureInfo.InvariantCulture ) ?? string.Empty;
   }

   /// <summary>
   /// Runs one scalar query on an open SQLite connection and returns it as text.
   /// </summary>
   /// <param name="connection">The connection.</param>
   /// <param name="sql">The query.</param>
   /// <returns>The value as text.</returns>
   private static string Scalar( SqliteConnection connection, string sql )
   {
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = sql;
      return Convert.ToString( command.ExecuteScalar(), CultureInfo.InvariantCulture ) ?? string.Empty;
   }

   #endregion Private Methods
}
