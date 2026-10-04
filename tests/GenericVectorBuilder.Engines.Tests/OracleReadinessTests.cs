using System.Diagnostics;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Oracle.ManagedDataAccess.Client;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Proves the Oracle sink's index readiness check against the real container
/// (deploy/engines/oracle.compose.yaml must be up).
/// Why: Oracle keeps rows loaded after the HNSW index was created in a change log and searches
/// them with an exact scan until the graph is rebuilt, while the plan of the search still says
/// "VECTOR INDEX HNSW SCAN". The sink must rebuild the graph and say what the views report. The
/// proof that "Ready" means what it says does not come from the sink's own readings: the
/// index_used_count that Oracle keeps for the index must grow with the real searches.
/// </summary>
[Trait( "Category", "Live" )]
public sealed class OracleReadinessTests
{
   #region Data Members

   private const int DIMENSION = 1024;
   private const int COUNT = 2000;
   private const int PROBE_SEARCHES = 20;
   private const int COMMITS = 20;

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Receives the xunit output writer.
   /// </summary>
   /// <param name="output">Where timings and the engine's evidence are printed.</param>
   public OracleReadinessTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Loads 2,000 random vectors, shows the check says "not ready" while the graph is empty and the
   /// change log holds every row, rebuilds with FinishLoadAsync, requires Ready with the graph
   /// holding every vector, and then proves with Oracle's own use counter that real searches use
   /// the index.
   /// </summary>
   [Fact]
   public async Task FinishLoad_Rebuilds_UntilGraphHoldsEveryVector()
   {
      string collection = EnginesAReadinessData.BenchCollection();
      var sink = new OracleSink { Progress = line => _output.WriteLine( line ) };
      List<VectorRecord> records = EnginesAReadinessData.Records( COUNT, DIMENSION, seed: 31 );
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         var clock = Stopwatch.StartNew();
         await sink.UpsertAsync( collection, records, CancellationToken.None );
         double loadSeconds = clock.Elapsed.TotalSeconds;

         IndexState before = await sink.GetIndexStateAsync( collection, CancellationToken.None );
         _output.WriteLine( $"Oracle before FinishLoadAsync: Ready={before.Ready}, indexed {before.IndexedVectors} of {before.TotalVectors}, {before.Detail}" );
         Assert.False( before.Ready, "the graph was created empty, so it cannot hold the loaded rows yet" );
         Assert.Equal( 0, before.IndexedVectors );

         clock.Restart();
         string note = await sink.FinishLoadAsync( collection, CancellationToken.None );
         double finishSeconds = clock.Elapsed.TotalSeconds;

         IndexState after = await sink.GetIndexStateAsync( collection, CancellationToken.None );
         _output.WriteLine( $"Oracle {COUNT} x {DIMENSION}: load {loadSeconds:F1} s, FinishLoadAsync {finishSeconds:F1} s. Note: {note}" );
         _output.WriteLine( $"Oracle after: Ready={after.Ready}, indexed {after.IndexedVectors} of {after.TotalVectors}, {after.Detail}" );
         Assert.True( after.Ready, after.Detail );
         Assert.Equal( COUNT, after.TotalVectors );
         Assert.Equal( COUNT, after.IndexedVectors );

         long usedBefore = await IndexUsedCountAsync( collection );
         for( int i = 0; i < PROBE_SEARCHES; i++ )
         {
            IReadOnlyList<SearchHit> hits = await sink.SearchAsync( collection, records[i].Vector, 10, CancellationToken.None );
            Assert.Equal( records[i].Chunk.ChunkId, hits[0].ChunkId );
         }

         long usedAfter = await IndexUsedCountAsync( collection );
         _output.WriteLine( $"Oracle V$VECTOR_INDEX.INDEX_USED_COUNT after FinishLoadAsync: {usedBefore} before, {usedAfter} after {PROBE_SEARCHES} searches" );
         Assert.Equal( PROBE_SEARCHES, usedAfter - usedBefore );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// Without a login that can read the V$ views the sink must fail at once, before it spends a
   /// rebuild, with a message that names the setting.
   /// </summary>
   [Fact]
   public async Task FinishLoad_FailsAtOnce_WhenViewsCannotBeRead()
   {
      string collection = EnginesAReadinessData.BenchCollection();
      OracleSinkOptions options = OracleSinkOptions.LocalDefaults();
      options.EvidenceUser = null;
      options.EvidencePassword = null;
      var sink = new OracleSink( options );
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         await sink.UpsertAsync( collection, EnginesAReadinessData.Records( 50, DIMENSION, seed: 32 ), CancellationToken.None );
         var clock = Stopwatch.StartNew();
         InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>( () => sink.FinishLoadAsync( collection, CancellationToken.None ) );
         _output.WriteLine( error.Message );
         Assert.Contains( "EvidenceUser", error.Message );
         Assert.True( clock.Elapsed < TimeSpan.FromSeconds( 30 ), $"took {clock.Elapsed.TotalSeconds:F0} s to fail" );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// The durability statement says every commit waits for a redo write. Prove it on the sink's
   /// own commit path: twenty single-row upserts must raise Oracle's 'redo synch writes' statistic
   /// by at least twenty.
   /// </summary>
   [Fact]
   public async Task Durability_EveryUpsertWaitsForARedoWrite()
   {
      string collection = EnginesAReadinessData.BenchCollection();
      var sink = new OracleSink();
      Assert.NotEqual( "not stated", ( (IEngineDescription)sink ).Durability );
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         List<VectorRecord> records = EnginesAReadinessData.Records( COMMITS, DIMENSION, seed: 33 );
         long before = await SyncWritesAsync();
         foreach( VectorRecord record in records )
         {
            await sink.UpsertAsync( collection, new[] { record }, CancellationToken.None );
         }

         long delta = await SyncWritesAsync() - before;
         _output.WriteLine( $"Oracle 'redo synch writes' grew by {delta} over {COMMITS} single-row upserts" );
         Assert.True( delta >= COMMITS, $"only {delta} synchronous redo writes for {COMMITS} commits" );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Opens a connection with the evidence login.
   /// </summary>
   /// <returns>An open connection; the caller disposes it.</returns>
   private static async Task<OracleConnection> EvidenceConnectionAsync()
   {
      OracleSinkOptions options = OracleSinkOptions.LocalDefaults();
      var connection = new OracleConnection( new OracleConnectionStringBuilder
      {
         UserID = options.EvidenceUser,
         Password = options.EvidencePassword ?? string.Empty,
         DataSource = $"{options.Host}:{options.Port}/{options.ServiceName}",
         ConnectionTimeout = 30,
      }.ConnectionString );
      await connection.OpenAsync();
      return connection;
   }

   /// <summary>
   /// Reads how many queries Oracle says have used the collection's vector index.
   /// </summary>
   /// <param name="collection">Collection name as passed to the sink.</param>
   /// <returns>V$VECTOR_INDEX.INDEX_USED_COUNT.</returns>
   private static async Task<long> IndexUsedCountAsync( string collection )
   {
      await using OracleConnection connection = await EvidenceConnectionAsync();
      await using OracleCommand command = connection.CreateCommand();
      command.CommandTimeout = 60;
      command.CommandText = $"SELECT NVL( index_used_count, 0 ) FROM v$vector_index WHERE index_name = 'GVB_{collection.ToUpperInvariant()}_VIDX'";
      return Convert.ToInt64( await command.ExecuteScalarAsync() );
   }

   /// <summary>
   /// Reads Oracle's count of commits that waited for a redo write.
   /// </summary>
   /// <returns>V$SYSSTAT 'redo synch writes'.</returns>
   private static async Task<long> SyncWritesAsync()
   {
      await using OracleConnection connection = await EvidenceConnectionAsync();
      await using OracleCommand command = connection.CreateCommand();
      command.CommandTimeout = 60;
      command.CommandText = "SELECT value FROM v$sysstat WHERE name = 'redo synch writes'";
      return Convert.ToInt64( await command.ExecuteScalarAsync() );
   }

   #endregion Private Methods
}
