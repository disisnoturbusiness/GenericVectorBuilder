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
/// Why the SQL targets write to their own databases: the benchmark copies up to 760k rows,
/// several gigabytes, and none of that should grow or lock the builder's real database.
/// </summary>
public sealed class TargetFactory : IDisposable
{
   #region Data Members

   /// <summary>Database the "sql" target writes to.</summary>
   public const string SQL_BENCH_DATABASE = "GvbBench";

   private const string ENGINE_DATA_ROOT = "~/gvb-data/engines";
   private const string QDRANT_COLLECTIONS = "~/qdrant/storage/collections";
   private const int QDRANT_HTTP_PORT = 6333;
   private static readonly string[] BUILT_IN = { "sql", "sql-diskann", "qdrant", "qdrant-hnsw" };

   private readonly GvbSettings _settings;
   private readonly string _sqlConnection;
   private readonly QdrantClient _qdrant;
   private readonly string _repoRoot;
   private readonly int? _hnswEf;
   private readonly Dictionary<string, ISink> _engines;
   private readonly List<string> _notes = new();
   private string _sqlVersion = "SQL Server";
   private string _qdrantVersion = "unknown version";

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the factory. Engine sinks are instantiated here (their constructors do not
   /// connect), so the list of names is complete before anything runs.
   /// </summary>
   /// <param name="settings">Builder settings (SQL login, Qdrant address).</param>
   /// <param name="repoRoot">Repository root, where deploy/engines lives.</param>
   /// <param name="hnswEf">Search beam for qdrant-hnsw, or null for the server default.</param>
   public TargetFactory( GvbSettings settings, string repoRoot, int? hnswEf )
   {
      _settings = settings;
      _sqlConnection = settings.BuildSqlConnectionString();
      _qdrant = new QdrantClient( settings.QdrantHost, settings.QdrantGrpcPort );
      _repoRoot = repoRoot;
      _hnswEf = hnswEf;
      _engines = LoadEngines( _notes );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Every target name, built-in ones first.</summary>
   public IReadOnlyList<string> Names => BUILT_IN.Concat( _engines.Keys.Where( k => !BUILT_IN.Contains( k, StringComparer.OrdinalIgnoreCase ) ).OrderBy( k => k ) ).ToList();

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
   }

   /// <summary>
   /// Builds a target by name.
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <returns>The target.</returns>
   /// <exception cref="ArgumentException">Unknown name.</exception>
   public BenchTarget Create( string name )
   {
      switch( name.ToLowerInvariant() )
      {
         case "sql":
            return SqlTarget( new SqlVectorSink( _sqlConnection, SQL_BENCH_DATABASE ), SQL_BENCH_DATABASE, c => $"gvb_{c}", "exact VECTOR_DISTANCE cosine, no vector index (full scan)" );
         case "sql-diskann":
            return SqlTarget( new SqlDiskAnnSink( _sqlConnection, _sqlVersion ), SqlDiskAnnSink.DATABASE, SqlDiskAnnSink.AnnTable, "DiskANN (preview) via VECTOR_SEARCH" );
         case "qdrant":
            return QdrantTarget( new QdrantSink( _qdrant ), c => $"gvb_{c}", "exact search (the builder's setting), HNSW m=16 ef_construct=100 built but not used",
               ( c, ct ) => QdrantHnswSink.WaitForGreenAsync( _qdrant, $"gvb_{c}", ct ) );
         case "qdrant-hnsw":
            return QdrantTarget( new QdrantHnswSink( _qdrant, _hnswEf, _qdrantVersion ), QdrantHnswSink.CollectionName, "HNSW", null );
      }

      if( _engines.TryGetValue( name, out ISink? sink ) )
      {
         return EngineTarget( sink );
      }

      throw new ArgumentException( $"Unknown target '{name}'. Known: {string.Join( ", ", Names )}." );
   }

   /// <summary>
   /// Drops the "sql" target's database once it holds no table, so a cleaned-up benchmark
   /// leaves nothing behind on the server. Call it last: the SQL sink remembers that its
   /// database exists and would not create it again in the same session.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when the database was dropped.</returns>
   public async Task<bool> DropEmptyBenchDatabaseAsync( CancellationToken ct )
   {
      await using var connection = new SqlConnection( _sqlConnection );
      await connection.OpenAsync( ct );
      await using var exists = new SqlCommand( $"SELECT DB_ID( N'{SQL_BENCH_DATABASE}' );", connection );
      if( await exists.ExecuteScalarAsync( ct ) is DBNull or null )
      {
         return false;
      }

      await using var tables = new SqlCommand( $"SELECT COUNT(*) FROM [{SQL_BENCH_DATABASE}].sys.tables WHERE is_ms_shipped = 0;", connection );
      if( Convert.ToInt32( await tables.ExecuteScalarAsync( ct ) ) > 0 )
      {
         return false;
      }

      SqlConnection.ClearAllPools();
      await using var drop = new SqlCommand( $"ALTER DATABASE [{SQL_BENCH_DATABASE}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{SQL_BENCH_DATABASE}];", connection ) { CommandTimeout = 300 };
      await drop.ExecuteNonQueryAsync( ct );
      return true;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A SQL Server target: memory is the whole server's, disk is the table's reserved pages.
   /// </summary>
   /// <param name="sink">The sink.</param>
   /// <param name="database">Database it writes to.</param>
   /// <param name="searchedTable">Maps a benchmark collection to the table searches read.</param>
   /// <param name="index">Index description.</param>
   /// <returns>The target.</returns>
   private BenchTarget SqlTarget( ISink sink, string database, Func<string, string> searchedTable, string index )
   {
      return new BenchTarget( sink, _sqlVersion, index, "always-on", null,
         ( _, ct ) => ResourceProbe.SqlServerRamAsync( _sqlConnection, ct ),
         ( c, ct ) => ResourceProbe.SqlTableSizeAsync( _sqlConnection, database, searchedTable( c ), ct ) );
   }

   /// <summary>
   /// A Qdrant target: memory is the whole Qdrant process, disk is the collection's folder.
   /// </summary>
   /// <param name="sink">The sink.</param>
   /// <param name="collectionName">Maps a benchmark collection to the Qdrant collection name.</param>
   /// <param name="index">Index description.</param>
   /// <param name="settle">Wait for background optimisation after loading, or null.</param>
   /// <returns>The target.</returns>
   private BenchTarget QdrantTarget( ISink sink, Func<string, string> collectionName, string index, Func<string, CancellationToken, Task<string>>? settle )
   {
      string engine = sink is IEngineDescription description ? description.Engine : $"Qdrant {_qdrantVersion} (systemd, local)";
      return new BenchTarget( sink, engine, index, "always-on", null,
         ( _, ct ) => ResourceProbe.ProcessRssAsync( "qdrant", "whole Qdrant process, every collection", ct ),
         ( c, ct ) => ResourceProbe.FolderSizeAsync( Path.Combine( GvbSettings.Expand( QDRANT_COLLECTIONS ), collectionName( c ) ), "collection folder", ct ) )
      {
         Settle = settle,
      };
   }

   /// <summary>
   /// An engine sink from the Engines assembly, hosted by compose, in process, or always on.
   /// </summary>
   /// <param name="sink">The sink.</param>
   /// <returns>The target.</returns>
   private BenchTarget EngineTarget( ISink sink )
   {
      var description = sink as IEngineDescription;
      string composeFile = description?.ComposeFile ?? "always-on";
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
      return new BenchTarget( sink, engine, index, "compose", composePath, ( _, ct ) => ResourceProbe.DockerRamAsync( composePath, ct ),
         ( _, ct ) => ResourceProbe.FolderSizeAsync( dataFolder, "whole engine data folder", ct ) );
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
   /// Disposes the Qdrant client and every engine sink that holds resources.
   /// </summary>
   public void Dispose()
   {
      _qdrant.Dispose();
      foreach( ISink sink in _engines.Values )
      {
         ( sink as IDisposable )?.Dispose();
      }
   }

   #endregion IDisposable
}
