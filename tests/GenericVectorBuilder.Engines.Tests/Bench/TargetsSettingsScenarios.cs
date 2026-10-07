// Compiled only by the Targets tests (TargetsHarness), together with the benchmark's own Targets
// sources. BENCH_UNDER_TEST is defined only in that compilation, so the test project itself sees an
// empty file.
#if BENCH_UNDER_TEST
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GenericVectorBuilder.Bench.Targets;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Sinks;

namespace GenericVectorBuilder.Bench.UnderTest;

/// <summary>
/// Runs the benchmark's engine settings reader, image pin check, data-folder measurer and
/// ClickHouse reset against fake Docker, fake web answers and fake files, and hands back plain
/// lines for the tests to check.
/// Why fakes and what they carry: the live engines cannot be started by a unit test, so every
/// command the readers run is answered from a script of the exact text the engine's client prints
/// (the formats are the clients' documented ones; the docker inspect and image inspect texts are
/// shapes captured from this machine). The pilot's settings-only run is the live proof.
/// Every scenario also counts how many of ten secret values it planted in the container's
/// environment come back out in any recorded line, command or error, which must be none.
/// </summary>
public static class TargetsSettingsScenarios
{
   #region Data Members

   /// <summary>The ten values secrets.env holds, planted in every fake container's environment.</summary>
   public static readonly string[] SECRETS =
   {
      "POSTGRES_PASSWORD=pg-secret-7f3a", "MARIADB_ROOT_PASSWORD=maria-secret-91bc", "ORACLE_PASSWORD=ora-secret-22de", "APP_USER=gvbapp",
      "APP_USER_PASSWORD=app-secret-5c10", "REDIS_PASSWORD=redis-secret-ab42", "MONGODB_INITDB_ROOT_PASSWORD=mongo-secret-0e77",
      "CLICKHOUSE_PASSWORD=ch-secret-d8e1", "TYPESENSE_API_KEY=ts-secret-33aa", "MSSQL_SA_PASSWORD=mssql-secret-c4f9",
   };

   private const string IMAGE_ID = "sha256:6f81e8915c60b065a524e6967e0ad1c639ba6efa84d669f823683ea04d9150ee";

   /// <summary>
   /// The shell string of redis.compose.yaml's entrypoint, as "docker inspect" prints it: one string for "sh -c" that holds
   /// --requirepass "$REDIS_PASSWORD" (the shell in the container expands the variable, so inspect shows the name, never the value).
   /// </summary>
   public const string REDIS_ENTRYPOINT = "exec /usr/local/bin/docker-entrypoint.sh redis-server --requirepass \"$REDIS_PASSWORD\" --save \"300 1\" --appendonly no --dir /data";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The text "docker inspect" prints for one fake container, in the shape Docker prints it: a
   /// one-element array with the id, the name, the image id, the host config limits, the config
   /// image and environment, the mounts and the published ports on one bridge network.
   /// </summary>
   /// <param name="container">Container name.</param>
   /// <param name="extraEnv">Environment entries to add to the ten secrets (for example "GOMEMLIMIT=6GiB").</param>
   /// <param name="mounts">"source:destination:rw" or "source:destination:ro" bind mounts.</param>
   /// <param name="ports">"host:container" published ports.</param>
   /// <param name="imageId">The id of the image the container runs.</param>
   /// <returns>The JSON text.</returns>
   public static string Inspect( string container, string[] extraEnv, string[] mounts, string[] ports, string imageId = IMAGE_ID )
   {
      var published = new Dictionary<string, object?>();
      foreach( string port in ports )
      {
         string[] parts = port.Split( ':' );
         published[$"{parts[1]}/tcp"] = new[] { new Dictionary<string, string> { ["HostIp"] = "127.0.0.1", ["HostPort"] = parts[0] } };
      }

      var root = new Dictionary<string, object?>
      {
         ["Id"] = "4f2d0c6a1b7e9d3c5a8b0e1f2a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d",
         ["Name"] = "/" + container,
         ["Image"] = imageId,
         ["Path"] = "sh",
         ["Args"] = new[] { "-c", REDIS_ENTRYPOINT },
         ["State"] = new Dictionary<string, object?> { ["Status"] = "running", ["Running"] = true },
         ["HostConfig"] = new Dictionary<string, object?> { ["CpusetCpus"] = "2-3,6-7", ["Memory"] = 8589934592L, ["NanoCpus"] = 0L },
         ["Config"] = new Dictionary<string, object?> { ["Image"] = "redis:8.10.2", ["Entrypoint"] = new[] { "sh", "-c", REDIS_ENTRYPOINT }, ["Env"] = SECRETS.Concat( extraEnv ).Append( "PATH=/usr/bin" ).ToArray() },
         ["Mounts"] = mounts.Select( m => m.Split( ':' ) ).Select( p => new Dictionary<string, object?> { ["Type"] = "bind", ["Source"] = p[0], ["Destination"] = p[1], ["RW"] = p[2] == "rw" } ).ToArray(),
         ["NetworkSettings"] = new Dictionary<string, object?>
         {
            ["Ports"] = published,
            ["Networks"] = new Dictionary<string, object?> { ["x_default"] = new Dictionary<string, string> { ["IPAddress"] = "172.24.0.2" } },
         },
      };
      return JsonSerializer.Serialize( new[] { root } );
   }

   /// <summary>
   /// Reads one container target's settings through a scripted probe and returns what came out.
   /// </summary>
   /// <param name="target">Target name (redis, elasticsearch, mariadb ...).</param>
   /// <param name="container">The container name its route lists.</param>
   /// <param name="inspect">The text "docker inspect" prints for it.</param>
   /// <param name="ports">"host:container" ports the route asks the router for (the first is the one the target connects to).</param>
   /// <param name="docker">Docker script: "text=>exitcode|stdout|stderr" entries; the first whose text the command line contains answers. "\n" in a field is a newline.</param>
   /// <param name="http">Web script: "text=>body" entries for the probe's GET.</param>
   /// <param name="clickHouse">ClickHouse script: "text=>status|body" entries for the statements sent over HTTP.</param>
   /// <param name="searchThreads">The count the /proc reader returns for Qdrant, or -1 for none.</param>
   /// <param name="timeoutMs">A short time limit for the read, or 0 for the real 15 s.</param>
   /// <returns>Lines: "setting|key|value|how", "call|text" for each docker command, web read and statement, "error|message", and "leaks|n" (planted secret values found in any line other than a statement sent to ClickHouse, whose headers carry its password by design).</returns>
   public static string[] Read( string target, string container, string inspect, string[] ports, string[] docker, string[] http, string[] clickHouse, int searchThreads = -1, int timeoutMs = 0 )
   {
      return ReadAsync( target, container, inspect, ports, docker, http, clickHouse, searchThreads, timeoutMs ).GetAwaiter().GetResult();
   }

   /// <summary>
   /// The settings of an embedded engine, read from a throwaway in-memory database and, after the
   /// searches, from the bench file (or from the sink's own code when there is none).
   /// </summary>
   /// <param name="target">"duckdb", "sqlitevec" or another name (a target without a reader).</param>
   /// <param name="withFile">True to create a real bench file with the real sink in a temporary folder and read it.</param>
   /// <returns>Lines "setting|key|value|how", or "error|message".</returns>
   public static string[] ReadEmbedded( string target, bool withFile )
   {
      return ReadEmbeddedAsync( target, withFile ).GetAwaiter().GetResult();
   }

   /// <summary>
   /// Whether a key is on the allowed list.
   /// </summary>
   /// <param name="key">The key.</param>
   /// <returns>True when allowed.</returns>
   public static bool KeyAllowed( string key )
   {
      return EngineSettingsReader.ALLOWED_KEYS.Contains( key );
   }

   /// <summary>
   /// Parses "docker inspect" text with the description parser and reports the fields and the
   /// environment names it kept.
   /// </summary>
   /// <param name="json">The text.</param>
   /// <returns>Lines, or one "error|message".</returns>
   public static string[] Describe( string json )
   {
      try
      {
         ContainerDescription c = ContainerDescription.Parse( json, EngineSettingsReader.ENV_KEYS );
         var lines = new List<string> { $"name|{c.Name}", $"image|{c.ImageId}|{c.ImageRef}", $"limits|{c.MemoryBytes}|{c.Cpuset}|{c.NanoCpus}" };
         lines.AddRange( c.Env.Select( e => $"env|{e.Key}|{e.Value}" ) );
         lines.AddRange( c.Mounts.Select( m => $"mount|{m.Type}|{m.Source}|{m.Destination}|{m.ReadWrite}" ) );
         return lines.ToArray();
      }
      catch( InvalidOperationException ex )
      {
         return new[] { "error|" + ex.Message };
      }
   }

   /// <summary>
   /// Checks image pins: loads a pins file text, reads a container's image against it with a scripted probe.
   /// </summary>
   /// <param name="pinsJson">The pins file's text.</param>
   /// <param name="target">Target name.</param>
   /// <param name="inspect">The container's "docker inspect" text.</param>
   /// <param name="imageInspect">What "docker image inspect" prints, as "exitcode|stdout|stderr".</param>
   /// <returns>Lines "facts|ref|id|pinned|tagTime|problem", "call|text", or "error|message".</returns>
   public static string[] CheckImage( string pinsJson, string target, string inspect, string imageInspect )
   {
      string path = Path.Combine( Path.GetTempPath(), "gvb-pins-" + Guid.NewGuid().ToString( "N" ) + ".json" );
      var probe = new ScriptProbe( new[] { "image inspect=>" + imageInspect }, Array.Empty<string>() );
      try
      {
         File.WriteAllText( path, pinsJson );
         ImagePins pins = ImagePins.Load( path );
         ContainerDescription c = ContainerDescription.Parse( inspect, EngineSettingsReader.ENV_KEYS );
         ImageFacts facts = pins.ReadAsync( probe, target, c, CancellationToken.None ).GetAwaiter().GetResult();
         var lines = new List<string> { $"facts|{facts.Ref}|{facts.Id}|{facts.PinnedId}|{facts.LastTagTimeUtc}|{facts.Problem}" };
         lines.AddRange( probe.Calls.Select( call => "call|" + call ) );
         return lines.ToArray();
      }
      catch( InvalidOperationException ex )
      {
         return new[] { "error|" + ex.Message };
      }
      finally
      {
         File.Delete( path );
      }
   }

   /// <summary>
   /// Loads a pins file from a path.
   /// </summary>
   /// <param name="path">The file (may not exist).</param>
   /// <returns>"targets|a,b,c" sorted, or "error|message".</returns>
   public static string LoadPins( string path )
   {
      try
      {
         return "targets|" + string.Join( ",", ImagePins.Load( path ).Targets.OrderBy( t => t, StringComparer.Ordinal ) );
      }
      catch( InvalidOperationException ex )
      {
         return "error|" + ex.Message;
      }
   }

   /// <summary>
   /// Reads the last tag time out of a metadata JSON text.
   /// </summary>
   /// <param name="json">The text.</param>
   /// <returns>The time, "none", or "error|message".</returns>
   public static string TagTime( string json )
   {
      try
      {
         return ImagePins.ParseTagTime( json ) ?? "none";
      }
      catch( InvalidOperationException ex )
      {
         return "error|" + ex.Message;
      }
   }

   /// <summary>
   /// The set of names the container routes know, plus the four built-in container targets, for
   /// checking the pins file lists exactly the container targets.
   /// </summary>
   /// <returns>The names, sorted.</returns>
   public static string[] ContainerTargetNames()
   {
      string[] routed = new[] { "chroma", "clickhouse", "elasticsearch", MariaBench.TARGET, "milvus", "mongodb", "opensearch", "oracle", "pgvector", "redis", "typesense", "vespa", "weaviate" }
         .Where( n => ContainerRoutes.Find( n ) != null ).ToArray();
      return routed.Concat( new[] { "sql", "sql-diskann", "qdrant", "qdrant-hnsw" } ).OrderBy( n => n, StringComparer.Ordinal ).ToArray();
   }

   /// <summary>
   /// Measures a folder through a scripted du and returns what came out.
   /// </summary>
   /// <param name="script">du answers in order, "exitcode|stdout|stderr", or "hang" for a call that does not return before its time limit.</param>
   /// <param name="timeoutMs">Time limit per call in milliseconds.</param>
   /// <param name="folder">The folder to measure (must exist for du to be asked).</param>
   /// <returns>"bytes|n", "error|message", and "call|text" for each du call.</returns>
   public static string[] Du( string[] script, int timeoutMs, string folder )
   {
      var calls = new List<string>();
      int next = 0;
      var meter = new DataFolderState( async ( arguments, limit, ct ) =>
      {
         calls.Add( string.Join( " ", arguments ) + " @" + limit.TotalMilliseconds.ToString( "0" ) + "ms" );
         string answer = script[Math.Min( next++, script.Length - 1 )];
         if( answer == "hang" )
         {
            await Task.Delay( limit, ct );
            throw new TimeoutException( $"sudo {string.Join( " ", arguments )} did not finish within {limit.TotalMinutes:0.#} minutes." );
         }

         string[] parts = answer.Split( '|' );
         return new ShellResult( int.Parse( parts[0] ), parts[1].Replace( "\\t", "\t" ), parts.Length > 2 ? parts[2] : string.Empty );
      }, TimeSpan.FromMilliseconds( timeoutMs ), TimeSpan.FromMilliseconds( 1 ) );
      var lines = new List<string>();
      try
      {
         lines.Add( "bytes|" + meter.BytesAsync( folder, CancellationToken.None ).GetAwaiter().GetResult() );
      }
      catch( InvalidOperationException ex )
      {
         lines.Add( "error|" + ex.Message );
      }

      lines.AddRange( calls.Select( c => "call|" + c ) );
      return lines.ToArray();
   }

   /// <summary>
   /// Which data folder a target gets and whether the container's mounts agree with it.
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <param name="hosting">compose, always-on or embedded.</param>
   /// <param name="composeFile">The compose file name, or null.</param>
   /// <param name="withContainer">True to give the target a container binding.</param>
   /// <returns>"folder|path" or "folder|none", or "error|message".</returns>
   public static string Folder( string name, string hosting, string? composeFile, bool withContainer )
   {
      try
      {
         BenchTarget target = Target( name, hosting, composeFile == null ? null : "/repo/deploy/engines/" + composeFile, withContainer ? "gvb-x" : null, Inspect( "gvb-x", Array.Empty<string>(), Array.Empty<string>(), new[] { "1:1" } ), new[] { "1:1" } );
         return "folder|" + ( DataFolderState.FolderFor( target ) ?? "none" );
      }
      catch( InvalidOperationException ex )
      {
         return "error|" + ex.Message;
      }
   }

   /// <summary>
   /// Checks a folder against the mounts of a container.
   /// </summary>
   /// <param name="folder">The chosen folder.</param>
   /// <param name="dataRoot">The engines' data root.</param>
   /// <param name="mounts">"source:destination:rw" or ":ro" entries.</param>
   /// <returns>"ok" or "error|message".</returns>
   public static string Verify( string folder, string dataRoot, string[] mounts )
   {
      try
      {
         ContainerDescription c = ContainerDescription.Parse( Inspect( "gvb-x", Array.Empty<string>(), mounts, Array.Empty<string>() ), EngineSettingsReader.ENV_KEYS );
         DataFolderState.VerifyAgainstMounts( folder, c, dataRoot );
         return "ok";
      }
      catch( InvalidOperationException ex )
      {
         return "error|" + ex.Message;
      }
   }

   /// <summary>
   /// The size of an embedded engine's bench file and its sidecars.
   /// </summary>
   /// <param name="name">"duckdb" or "sqlitevec".</param>
   /// <param name="folder">Folder to put the files in (the sink's default folder is never touched).</param>
   /// <param name="sizes">"suffix=bytes" files to create next to the bench file ("" is the file itself).</param>
   /// <returns>"bytes|n" or "bytes|none", and "file|path".</returns>
   public static string[] BenchFile( string name, string folder, string[] sizes )
   {
      Directory.CreateDirectory( folder );
      string extension = name == "duckdb" ? ".duckdb" : ".sqlite";
      foreach( string size in sizes )
      {
         string[] parts = size.Split( '=' );
         File.WriteAllBytes( Path.Combine( folder, "gvb_gvbbench_t" + extension + parts[0] ), new byte[int.Parse( parts[1] )] );
      }

      BenchTarget target = Target( name, "embedded", null, null, string.Empty, Array.Empty<string>() );
      string? file = DataFolderState.BenchFile( target, "gvbbench_t" );
      string? expected = file == null ? null : Path.Combine( folder, Path.GetFileName( file ) );
      long? bytes = expected == null ? null : SidecarsOf( name ).Select( s => expected + s ).Where( File.Exists ).Sum( f => new FileInfo( f ).Length );
      return new[] { "file|" + ( file == null ? "none" : Path.GetFileName( file ) ), "bytes|" + ( bytes == null || !File.Exists( expected! ) ? "none" : bytes.ToString() ) };
   }

   /// <summary>
   /// Resets ClickHouse's log tables through a scripted HTTP server and a scripted folder size.
   /// </summary>
   /// <param name="http">Statement script: "text=>status|body" entries; "\t" and "\n" in a body are a tab and a newline.</param>
   /// <param name="folderSizes">The sizes the folder measurer returns in order (the last repeats).</param>
   /// <returns>Lines: "reset|describe", "stable|bool", "delays|n", "request|method url headers body" per HTTP request, or "error|message".</returns>
   public static string[] ClickHouse( string[] http, long[] folderSizes )
   {
      var handler = new ScriptHandler( http );
      int delays = 0;
      int next = 0;
      var state = new ClickHouseStartState( () => new ClickHouseHttp( handler, "gvb", "ch-secret-d8e1" ), ( _, _ ) =>
      {
         delays++;
         return Task.CompletedTask;
      } );
      var address = new ContainerAddress( "gvb-clickhouse", "4f2d0c6a1b7e", "x_default", "172.24.0.2", 8123, "127.0.0.1", 8123 );
      var lines = new List<string>();
      try
      {
         ClickHouseReset reset = state.ResetAsync( address, _ => Task.FromResult( folderSizes[Math.Min( next++, folderSizes.Length - 1 )] ), 7_000_000_000, CancellationToken.None ).GetAwaiter().GetResult();
         lines.Add( "reset|" + reset.Describe() );
         lines.Add( "stable|" + reset.Stable );
         lines.Add( $"numbers|{reset.ActiveBytesBefore}|{reset.ActiveBytesAfter}|{reset.FolderBytesBefore}|{reset.FolderBytesAfter}" );
         lines.Add( "delays|" + delays );
      }
      catch( InvalidOperationException ex )
      {
         lines.Add( "error|" + ex.Message );
      }

      lines.AddRange( handler.Requests.Select( r => "request|" + r ) );
      return lines.ToArray();
   }

   /// <summary>
   /// Parses a ClickHouse table list.
   /// </summary>
   /// <param name="tsv">The response text.</param>
   /// <returns>"name=bytes" per table, or "error|message".</returns>
   public static string[] ParseTables( string tsv )
   {
      try
      {
         return ClickHouseStartState.ParseTables( tsv ).Select( t => $"{t.Name}={t.Bytes}" ).ToArray();
      }
      catch( InvalidOperationException ex )
      {
         return new[] { "error|" + ex.Message };
      }
   }

   /// <summary>
   /// Whether a token cited by a "set:" entry is found in the file the entry names.
   /// </summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <param name="how">The entry's how, "set: path#token".</param>
   /// <returns>True when the file exists and holds the token.</returns>
   public static bool SetTokenFound( string repoRoot, string how )
   {
      string text = how["set: ".Length..];
      int hash = text.IndexOf( '#' );
      string path = Path.Combine( repoRoot, text[..hash] );
      return File.Exists( path ) && File.ReadAllText( path ).Contains( text[( hash + 1 )..], StringComparison.Ordinal );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Builds a bound container target and reads its settings.
   /// </summary>
   private static async Task<string[]> ReadAsync( string target, string container, string inspect, string[] ports, string[] docker, string[] http, string[] clickHouse, int searchThreads, int timeoutMs )
   {
      inspect = WithPublishedPorts( inspect, ports );
      var probe = new ScriptProbe( docker, http );
      var handler = new ScriptHandler( clickHouse );
      TimeSpan? limit = timeoutMs > 0 ? TimeSpan.FromMilliseconds( timeoutMs ) : null;
      var reader = new EngineSettingsReader( probe, () => new ClickHouseHttp( handler, "gvb", "ch-secret-d8e1" ), _ => searchThreads < 0 ? null : searchThreads, limit );
      var lines = new List<string>();
      try
      {
         BenchTarget bound = Target( target, "compose", $"/repo/deploy/engines/{target}.compose.yaml", container, inspect, ports );
         await bound.BindAsync( CancellationToken.None );
         ContainerDescription description = ContainerDescription.Parse( inspect, EngineSettingsReader.ENV_KEYS );
         IReadOnlyList<SettingRead> settings = await reader.ReadAsync( bound, description, CancellationToken.None );
         lines.AddRange( settings.Select( s => $"setting|{s.Key}|{s.Value}|{s.How}" ) );
      }
      catch( InvalidOperationException ex )
      {
         lines.Add( "error|" + ex.Message );
      }

      lines.AddRange( probe.Calls.Select( c => "call|" + c ) );
      lines.AddRange( handler.Requests.Select( r => "call|http " + r ) );
      string everything = string.Join( "\n", lines.Where( l => !l.StartsWith( "call|http " ) ) );
      lines.Add( "leaks|" + SECRETS.Select( s => s.Split( '=', 2 )[1] ).Count( v => v.Length > 4 && everything.Contains( v, StringComparison.Ordinal ) ) );
      return lines.ToArray();
   }

   /// <summary>
   /// Adds the "host:container" ports the route will ask for to the published ports of an inspect text, so a
   /// fixture written for one purpose (its limits, its environment) can be read through any target's route.
   /// </summary>
   /// <param name="inspect">The inspect text.</param>
   /// <param name="ports">"host:container" ports.</param>
   /// <returns>The text with those ports published.</returns>
   private static string WithPublishedPorts( string inspect, string[] ports )
   {
      JsonNode root = JsonNode.Parse( inspect )!;
      JsonObject published = root[0]!["NetworkSettings"]!["Ports"]!.AsObject();
      foreach( string port in ports )
      {
         string[] parts = port.Split( ':' );
         if( !published.ContainsKey( $"{parts[1]}/tcp" ) || published[$"{parts[1]}/tcp"]!.AsArray().All( p => p!["HostPort"]!.GetValue<string>() != parts[0] ) )
         {
            published[$"{parts[1]}/tcp"] = new JsonArray( new JsonObject { ["HostIp"] = "127.0.0.1", ["HostPort"] = parts[0] } );
         }
      }

      return root.ToJsonString();
   }

   /// <summary>
   /// Reads an embedded engine's settings, with a real bench file when asked.
   /// </summary>
   private static async Task<string[]> ReadEmbeddedAsync( string target, bool withFile )
   {
      string folder = Path.Combine( Path.GetTempPath(), "gvb-embedded-" + Guid.NewGuid().ToString( "N" ) );
      var lines = new List<string>();
      try
      {
         BenchTarget bound = Target( target, "embedded", null, null, string.Empty, Array.Empty<string>() );
         var reader = new EngineSettingsReader( new ScriptProbe( Array.Empty<string>(), Array.Empty<string>() ) );
         lines.AddRange( ( await reader.ReadAsync( bound, null, CancellationToken.None ) ).Select( s => $"setting|{s.Key}|{s.Value}|{s.How}" ) );
         string? file = withFile ? await CreateBenchFileAsync( target, folder ) : null;
         lines.AddRange( ( await reader.ReadEmbeddedFileAsync( bound, file, CancellationToken.None ) ).Select( s => $"setting|{s.Key}|{s.Value}|{s.How}" ) );
      }
      catch( InvalidOperationException ex )
      {
         lines.Add( "error|" + ex.Message );
      }
      finally
      {
         if( Directory.Exists( folder ) )
         {
            Directory.Delete( folder, true );
         }
      }

      return lines.ToArray();
   }

   /// <summary>
   /// Creates a real bench file with the real sink of an embedded engine in a temporary folder and leaves the sink open (as a run does until the copy is dropped).
   /// </summary>
   private static async Task<string> CreateBenchFileAsync( string target, string folder )
   {
      const string COLLECTION = "gvbbench_t";
      var records = Enumerable.Range( 0, 5 ).Select( i => new VectorRecord( new Document( $"d{i}", "t", "o", "text", "h", new Dictionary<string, string>() ),
         new Chunk( Guid.NewGuid(), $"d{i}", 0, "text" ), Enumerable.Range( 0, 8 ).Select( d => ( i + 1 ) * 0.1f + d * 0.01f ).ToArray() ) ).ToList();
      if( target == "duckdb" )
      {
         var sink = new DuckDbSink( new DuckDbSinkOptions { DirectoryPath = folder } );
         await sink.EnsureCollectionAsync( COLLECTION, 8, CancellationToken.None );
         await sink.UpsertAsync( COLLECTION, records, CancellationToken.None );
         KeepOpen.Add( sink );
         return Path.Combine( folder, $"gvb_{COLLECTION}.duckdb" );
      }

      var lite = new SqliteVecSink( new SqliteVecSinkOptions { DirectoryPath = folder } );
      await lite.EnsureCollectionAsync( COLLECTION, 8, CancellationToken.None );
      await lite.UpsertAsync( COLLECTION, records, CancellationToken.None );
      KeepOpen.Add( lite );
      return Path.Combine( folder, $"gvb_{COLLECTION}.sqlite" );
   }

   /// <summary>Sinks left open until the process ends, so a bench file stays open the way it is during a run.</summary>
   private static readonly List<IDisposable> KeepOpen = new();

   /// <summary>
   /// Builds a target (bound when it has a container).
   /// </summary>
   private static BenchTarget Target( string name, string hosting, string? compose, string? container, string inspect, string[] ports )
   {
      Func<string, CancellationToken, Task<Measurement>> none = ( _, _ ) => Task.FromResult( Measurement.None( "fake" ) );
      if( container == null )
      {
         return new BenchTarget( new StubSink( name ), "Fake 1.0", "fake index", hosting, compose, none, none );
      }

      var route = new EngineRoute( new[] { container }, router =>
      {
         ports.ToList().ForEach( p => router.Address( container, int.Parse( p.Split( ':' )[0] ) ) );
         return new StubSink( name );
      } );
      return new BenchTarget( new StubSink( name ), "Fake 1.0", "fake index", hosting, compose, none, none )
      {
         Container = new ContainerBinding( route, new FixedInspector( container, inspect ), TimeSpan.FromSeconds( 5 ), TimeSpan.FromMilliseconds( 10 ) ),
      };
   }

   /// <summary>
   /// The suffixes that belong to one bench file.
   /// </summary>
   private static string[] SidecarsOf( string name )
   {
      return name == "duckdb" ? new[] { string.Empty, ".wal" } : new[] { string.Empty, "-wal", "-shm" };
   }

   #endregion Private Methods
}

/// <summary>
/// A sink that only has a name; none of its calls are made in these scenarios.
/// </summary>
public sealed class StubSink : ISink
{
   #region Constructor

   /// <summary>Creates the sink.</summary>
   /// <param name="name">Name.</param>
   public StubSink( string name )
   {
      Name = name;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Sink name.</summary>
   public string Name { get; }

   /// <summary>Not used.</summary>
   /// <param name="collection">Collection.</param>
   /// <param name="dimension">Dimension.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True.</returns>
   public Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct ) => Task.FromResult( true );

   /// <summary>Not used.</summary>
   /// <param name="collection">Collection.</param>
   /// <param name="records">Records.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Done.</returns>
   public Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct ) => Task.CompletedTask;

   /// <summary>Not used.</summary>
   /// <param name="collection">Collection.</param>
   /// <param name="chunkIds">Ids.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Done.</returns>
   public Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct ) => Task.CompletedTask;

   /// <summary>Not used.</summary>
   /// <param name="collection">Collection.</param>
   /// <param name="vector">Query.</param>
   /// <param name="top">Hits.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>No hits.</returns>
   public Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct ) => Task.FromResult<IReadOnlyList<SearchHit>>( Array.Empty<SearchHit>() );

   /// <summary>Not used.</summary>
   /// <param name="collection">Collection.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Done.</returns>
   public Task DropCollectionAsync( string collection, CancellationToken ct ) => Task.CompletedTask;

   #endregion Public Methods
}

/// <summary>
/// Answers "docker inspect" for one container with fixed text.
/// </summary>
public sealed class FixedInspector : IContainerInspector
{
   #region Data Members

   private readonly string _container;
   private readonly string _json;

   #endregion Data Members

   #region Constructor

   /// <summary>Creates the inspector.</summary>
   /// <param name="container">The only container it knows.</param>
   /// <param name="json">The text it prints.</param>
   public FixedInspector( string container, string json )
   {
      _container = container;
      _json = json;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Returns the text for the known container, null for any other.</summary>
   /// <param name="container">Container name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The text or null.</returns>
   public Task<string?> InspectAsync( string container, CancellationToken ct )
   {
      return Task.FromResult<string?>( container == _container ? _json : null );
   }

   #endregion Public Methods
}

/// <summary>
/// A probe that answers Docker commands and web reads from scripts and records every call.
/// A command whose script entry is "hang" never returns before its token is cancelled.
/// </summary>
public sealed class ScriptProbe : IEngineProbe
{
   #region Data Members

   private readonly string[] _docker;
   private readonly string[] _http;

   #endregion Data Members

   #region Constructor

   /// <summary>Creates the probe.</summary>
   /// <param name="docker">"text=>exitcode|stdout|stderr" entries.</param>
   /// <param name="http">"text=>body" entries.</param>
   public ScriptProbe( string[] docker, string[] http )
   {
      _docker = docker;
      _http = http;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Every command and web read, in order, as text.</summary>
   public List<string> Calls { get; } = new();

   /// <summary>Records and answers a Docker command.</summary>
   /// <param name="arguments">Arguments after docker.</param>
   /// <param name="timeout">Time limit (not used by the script).</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The scripted result.</returns>
   public async Task<ShellResult> DockerAsync( IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct )
   {
      string line = string.Join( " ", arguments );
      Calls.Add( "docker " + line );
      string? answer = Find( _docker, line );
      if( answer == null )
      {
         return new ShellResult( 127, string.Empty, "no scripted answer" );
      }

      if( answer == "hang" )
      {
         await Task.Delay( Timeout.Infinite, ct );
      }

      string[] parts = answer.Split( '|' );
      return new ShellResult( int.Parse( parts[0] ), parts.Length > 1 ? Unescape( parts[1] ) : string.Empty, parts.Length > 2 ? Unescape( parts[2] ) : string.Empty );
   }

   /// <summary>Records and answers a web read.</summary>
   /// <param name="url">The address.</param>
   /// <param name="timeout">Time limit (not used by the script).</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The scripted body.</returns>
   public Task<string> HttpGetAsync( string url, TimeSpan timeout, CancellationToken ct )
   {
      Calls.Add( "get " + url );
      string? answer = Find( _http, url );
      return answer == null ? throw new HttpRequestException( "no scripted answer for " + url ) : Task.FromResult( Unescape( answer ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The answer of the first script entry whose text the call contains.
   /// </summary>
   private static string? Find( string[] script, string call )
   {
      foreach( string entry in script )
      {
         int split = entry.IndexOf( "=>", StringComparison.Ordinal );
         if( call.Contains( entry[..split], StringComparison.Ordinal ) )
         {
            return entry[( split + 2 )..];
         }
      }

      return null;
   }

   /// <summary>
   /// Turns the script's "\n" and "\t" into a newline and a tab.
   /// </summary>
   private static string Unescape( string text )
   {
      return text.Replace( "\\n", "\n" ).Replace( "\\t", "\t" );
   }

   #endregion Private Methods
}

/// <summary>
/// An HTTP handler that answers statements from a script and records every request with its
/// method, address, headers and body. A request is answered by the first script entry whose text its
/// body contains and that has not answered yet; when every matching entry has answered, the first
/// matching one answers again. So two entries for the same statement are two answers in order.
/// </summary>
public sealed class ScriptHandler : HttpMessageHandler
{
   #region Data Members

   private readonly string[] _script;
   private readonly HashSet<int> _used = new();

   #endregion Data Members

   #region Constructor

   /// <summary>Creates the handler.</summary>
   /// <param name="script">"text=>status|body" entries.</param>
   public ScriptHandler( string[] script )
   {
      _script = script;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Every request, as "METHOD url [headers] body".</summary>
   public List<string> Requests { get; } = new();

   #endregion Public Methods

   #region Overrides

   /// <summary>
   /// Records the request and answers it from the script.
   /// </summary>
   /// <param name="request">The request.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The scripted response.</returns>
   protected override async Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken ct )
   {
      string body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync( ct );
      string headers = string.Join( ",", request.Headers.Select( h => $"{h.Key}={string.Join( ";", h.Value )}" ).OrderBy( h => h, StringComparer.Ordinal ) );
      Requests.Add( $"{request.Method} {request.RequestUri} [{headers}] closeConnection={request.Headers.ConnectionClose} {body}" );
      int chosen = -1;
      for( int i = 0; i < _script.Length; i++ )
      {
         int split = _script[i].IndexOf( "=>", StringComparison.Ordinal );
         if( body.Contains( _script[i][..split], StringComparison.Ordinal ) && ( chosen < 0 || !_used.Contains( i ) && _used.Contains( chosen ) ) )
         {
            chosen = i;
         }
      }

      if( chosen >= 0 )
      {
         _used.Add( chosen );
         string entry = _script[chosen];
         string[] parts = entry[( entry.IndexOf( "=>", StringComparison.Ordinal ) + 2 )..].Split( '|', 2 );
         return new HttpResponseMessage( (HttpStatusCode)int.Parse( parts[0] ) ) { Content = new StringContent( parts.Length > 1 ? parts[1].Replace( "\\n", "\n" ).Replace( "\\t", "\t" ) : string.Empty, Encoding.UTF8 ) };
      }

      return new HttpResponseMessage( HttpStatusCode.NotFound ) { Content = new StringContent( "no scripted answer", Encoding.UTF8 ) };
   }

   #endregion Overrides
}
#endif
