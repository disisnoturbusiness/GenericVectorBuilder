using System.Diagnostics;
using System.Globalization;
using System.Text;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using GenericVectorBuilder.Engines.Sinks;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Proves the ClickHouse sink's index readiness check against the real container
/// (deploy/engines/clickhouse.compose.yaml must be up, HTTP port 8123).
/// Why: ClickHouse builds each part's vector index inside the INSERT, so the risk is not a
/// half-built index but a part that has none (the plan still lists the index) and merges that
/// rebuild parts while a run is being timed. The tests read the evidence themselves, straight from
/// system.parts and system.events, instead of trusting the sink's own readings.
/// </summary>
[Trait( "Category", "Live" )]
public sealed class ClickHouseReadinessTests : IDisposable
{
   #region Data Members

   private const int DIMENSION = 1024;
   private const int COUNT = 2000;
   private const int PROBE_SEARCHES = 20;

   private readonly ITestOutputHelper _output;
   private readonly HttpClient _http;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Receives the xunit output writer and opens a raw HTTP client for the test's own queries.
   /// </summary>
   /// <param name="output">Where timings and the engine's evidence are printed.</param>
   public ClickHouseReadinessTests( ITestOutputHelper output )
   {
      _output = output;
      ClickHouseSinkOptions options = ClickHouseSinkOptions.LocalDefaults();
      _http = new HttpClient { BaseAddress = new Uri( $"http://{options.Host}:{options.Port}/" ), Timeout = TimeSpan.FromSeconds( 60 ) };
      _http.DefaultRequestHeaders.Add( "X-ClickHouse-User", options.User );
      _http.DefaultRequestHeaders.Add( "X-ClickHouse-Key", options.Password ?? string.Empty );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Loads 2,000 random vectors, waits with FinishLoadAsync, requires Ready with every vector in
   /// an indexed part, then proves with the engine's own counters that default searches run the
   /// graph search and exact searches do not.
   /// </summary>
   [Fact]
   public async Task FinishLoad_Waits_UntilEveryPartHasItsIndex()
   {
      string collection = EnginesAReadinessData.BenchCollection();
      using var sink = new ClickHouseSink { Progress = line => _output.WriteLine( line ) };
      List<VectorRecord> records = EnginesAReadinessData.Records( COUNT, DIMENSION, seed: 41 );
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         var clock = Stopwatch.StartNew();
         await sink.UpsertAsync( collection, records, CancellationToken.None );
         double loadSeconds = clock.Elapsed.TotalSeconds;

         clock.Restart();
         string note = await sink.FinishLoadAsync( collection, CancellationToken.None );
         double finishSeconds = clock.Elapsed.TotalSeconds;

         IndexState after = await sink.GetIndexStateAsync( collection, CancellationToken.None );
         _output.WriteLine( $"ClickHouse {COUNT} x {DIMENSION}: load {loadSeconds:F1} s, FinishLoadAsync {finishSeconds:F1} s. Note: {note}" );
         _output.WriteLine( $"ClickHouse after: Ready={after.Ready}, indexed {after.IndexedVectors} of {after.TotalVectors}, {after.Detail}" );
         Assert.True( after.Ready, after.Detail );
         Assert.Equal( COUNT, after.TotalVectors );
         Assert.Equal( COUNT, after.IndexedVectors );

         string parts = await RawAsync( $"SELECT count(), countIf( secondary_indices_uncompressed_bytes > 0 ), sum( rows ) FROM system.parts WHERE database = 'gvb' AND table = 'gvb_{collection}' AND active FORMAT TSV" );
         _output.WriteLine( $"system.parts (parts, parts with index files, rows): {parts}" );
         string[] cells = parts.Split( '\t' );
         Assert.Equal( cells[0], cells[1] );
         Assert.Equal( COUNT.ToString( CultureInfo.InvariantCulture ), cells[2] );

         long beforeDefault = await GraphSearchesAsync();
         for( int i = 0; i < PROBE_SEARCHES; i++ )
         {
            IReadOnlyList<SearchHit> hits = await sink.SearchAsync( collection, records[i].Vector, 10, CancellationToken.None );
            Assert.Equal( records[i].Chunk.ChunkId, hits[0].ChunkId );
         }

         long afterDefault = await GraphSearchesAsync();
         for( int i = 0; i < PROBE_SEARCHES; i++ )
         {
            await sink.SearchExactAsync( collection, records[i].Vector, 10, CancellationToken.None );
         }

         long afterExact = await GraphSearchesAsync();
         _output.WriteLine( $"USearchSearchCount (system.events): {PROBE_SEARCHES} default searches +{afterDefault - beforeDefault}, {PROBE_SEARCHES} exact searches +{afterExact - afterDefault}" );
         Assert.True( afterDefault - beforeDefault >= PROBE_SEARCHES, "every default search must run at least one graph search" );
         Assert.Equal( 0, afterExact - afterDefault );

         var unit = new float[DIMENSION];
         unit[0] = 1f;
         long beforeUnit = await GraphSearchesAsync();
         await sink.SearchAsync( collection, unit, 10, CancellationToken.None );
         long afterUnit = await GraphSearchesAsync();
         _output.WriteLine( $"USearchSearchCount for a search with an all-whole-number vector: +{afterUnit - beforeUnit}" );
         Assert.True( afterUnit - beforeUnit >= 1, "a vector of whole numbers must still be searched through the graph" );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// Loads the same vectors as twenty small inserts, so ClickHouse has many parts to merge. After
   /// FinishLoadAsync the index must be complete and the part set must have stopped changing.
   /// </summary>
   [Fact]
   public async Task FinishLoad_Waits_UntilMergesSettle()
   {
      string collection = EnginesAReadinessData.BenchCollection();
      using var sink = new ClickHouseSink( new ClickHouseSinkOptions { Password = ClickHouseSinkOptions.LocalDefaults().Password, UpsertBatch = 100 } ) { Progress = line => _output.WriteLine( line ) };
      List<VectorRecord> records = EnginesAReadinessData.Records( COUNT, DIMENSION, seed: 42 );
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         await sink.UpsertAsync( collection, records, CancellationToken.None );
         string partsAsLoaded = await RawAsync( $"SELECT count() FROM system.parts WHERE database = 'gvb' AND table = 'gvb_{collection}' AND active FORMAT TSV" );

         var clock = Stopwatch.StartNew();
         string note = await sink.FinishLoadAsync( collection, CancellationToken.None );
         double finishSeconds = clock.Elapsed.TotalSeconds;
         IndexState after = await sink.GetIndexStateAsync( collection, CancellationToken.None );
         _output.WriteLine( $"ClickHouse 20 inserts of 100: {partsAsLoaded} parts right after the load, FinishLoadAsync {finishSeconds:F1} s. Note: {note}" );
         Assert.True( after.Ready, after.Detail );
         Assert.Equal( COUNT, after.IndexedVectors );
         Assert.Equal( COUNT, after.TotalVectors );

         string namesNow = await RawAsync( $"SELECT groupConcat( ',' )( name ) FROM ( SELECT name FROM system.parts WHERE database = 'gvb' AND table = 'gvb_{collection}' AND active ORDER BY name ) FORMAT TSV" );
         await Task.Delay( TimeSpan.FromSeconds( 3 ) );
         string namesLater = await RawAsync( $"SELECT groupConcat( ',' )( name ) FROM ( SELECT name FROM system.parts WHERE database = 'gvb' AND table = 'gvb_{collection}' AND active ORDER BY name ) FORMAT TSV" );
         string merges = await RawAsync( $"SELECT count() FROM system.merges WHERE database = 'gvb' AND table = 'gvb_{collection}' FORMAT TSV" );
         _output.WriteLine( $"Parts after the wait: {namesNow}; 3 s later: {namesLater}; merges running: {merges}" );
         Assert.Equal( namesNow, namesLater );
         Assert.Equal( "0", merges );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// A table whose index was added after the data (ALTER ... ADD INDEX without MATERIALIZE) has
   /// parts with no index files, although its definition and its plan list the index. The sink
   /// must say not ready, and FinishLoadAsync must build the index and then say ready.
   /// </summary>
   [Fact]
   public async Task GetIndexState_IsNotReady_WhenPartsLackTheIndex_AndFinishLoadBuildsIt()
   {
      string collection = EnginesAReadinessData.BenchCollection();
      string table = $"gvb.gvb_{collection}";
      using var sink = new ClickHouseSink { Progress = line => _output.WriteLine( line ) };
      try
      {
         await RawAsync( "CREATE DATABASE IF NOT EXISTS gvb" );
         await RawAsync( $"CREATE TABLE {table} ( chunk_id UUID, doc_key String, `table` String, origin String, ordinal Int32, text String, meta Map(String, String), embedding Array(Float32) ) ENGINE = MergeTree ORDER BY chunk_id" );
         await RawAsync( $"INSERT INTO {table} SELECT generateUUIDv4(), 'k', 't', 'o', 0, 'x', map(), arrayMap( x -> randCanonical() * 2 - 1, range( {DIMENSION} ) ) FROM numbers( {COUNT} )" );
         await RawAsync( $"ALTER TABLE {table} ADD INDEX vec_idx embedding TYPE vector_similarity( 'hnsw', 'cosineDistance', {DIMENSION}, 'bf16', 16, 128 ) GRANULARITY 100000000" );

         IndexState before = await sink.GetIndexStateAsync( collection, CancellationToken.None );
         _output.WriteLine( $"ClickHouse before FinishLoadAsync: Ready={before.Ready}, indexed {before.IndexedVectors} of {before.TotalVectors}, {before.Detail}" );
         Assert.False( before.Ready, "the parts have no index files yet" );
         Assert.Equal( 0, before.IndexedVectors );
         Assert.Equal( COUNT, before.TotalVectors );

         var clock = Stopwatch.StartNew();
         string note = await sink.FinishLoadAsync( collection, CancellationToken.None );
         _output.WriteLine( $"ClickHouse MATERIALIZE INDEX on {COUNT} x {DIMENSION}: FinishLoadAsync {clock.Elapsed.TotalSeconds:F1} s. Note: {note}" );

         IndexState after = await sink.GetIndexStateAsync( collection, CancellationToken.None );
         Assert.True( after.Ready, after.Detail );
         Assert.Equal( COUNT, after.IndexedVectors );
         string withIndex = await RawAsync( $"SELECT countIf( secondary_indices_uncompressed_bytes > 0 ), count() FROM system.parts WHERE database = 'gvb' AND table = 'gvb_{collection}' AND active FORMAT TSV" );
         string[] cells = withIndex.Split( '\t' );
         Assert.Equal( cells[1], cells[0] );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// When the table never settles (here: a settle time far longer than the wait), FinishLoadAsync
   /// must fail at its deadline with a message that carries the last reading.
   /// </summary>
   [Fact]
   public async Task FinishLoad_Fails_WithAPlainMessage_WhenTheTableNeverSettles()
   {
      string collection = EnginesAReadinessData.BenchCollection();
      var options = new ClickHouseSinkOptions { Password = ClickHouseSinkOptions.LocalDefaults().Password, IndexWaitSeconds = 4, MergeSettleSeconds = 600 };
      using var sink = new ClickHouseSink( options );
      try
      {
         await sink.EnsureCollectionAsync( collection, DIMENSION, CancellationToken.None );
         await sink.UpsertAsync( collection, EnginesAReadinessData.Records( 50, DIMENSION, seed: 44 ), CancellationToken.None );
         var clock = Stopwatch.StartNew();
         InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>( () => sink.FinishLoadAsync( collection, CancellationToken.None ) );
         _output.WriteLine( error.Message );
         Assert.Contains( "did not finish indexing", error.Message );
         Assert.Contains( "data part(s)", error.Message );
         Assert.True( clock.Elapsed < TimeSpan.FromSeconds( 30 ), $"took {clock.Elapsed.TotalSeconds:F0} s to fail" );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// A table that does not exist is not ready, and the answer says so instead of throwing.
   /// </summary>
   [Fact]
   public async Task GetIndexState_IsNotReady_ForAMissingTable()
   {
      using var sink = new ClickHouseSink();
      IndexState state = await sink.GetIndexStateAsync( EnginesAReadinessData.BenchCollection(), CancellationToken.None );
      _output.WriteLine( $"Missing table: Ready={state.Ready}, {state.Detail}" );
      Assert.False( state.Ready );
      Assert.Contains( "no table", state.Detail );
   }

   /// <summary>
   /// The durability statement must be filled in, not the default.
   /// </summary>
   [Fact]
   public void Durability_IsStated()
   {
      using var sink = new ClickHouseSink();
      Assert.NotEqual( "not stated", ( (IEngineDescription)sink ).Durability );
      Assert.Contains( "fsync_after_insert", sink.Durability );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Runs one statement with the test's own login and returns the trimmed answer.
   /// </summary>
   /// <param name="sql">The statement.</param>
   /// <returns>The response body.</returns>
   private async Task<string> RawAsync( string sql )
   {
      using var content = new StringContent( sql, Encoding.UTF8, "text/plain" );
      using HttpResponseMessage response = await _http.PostAsync( string.Empty, content );
      string body = await response.Content.ReadAsStringAsync();
      Assert.True( response.IsSuccessStatusCode, body );
      return body.Trim();
   }

   /// <summary>
   /// Reads the server's count of graph searches (one per data part per query that uses the index).
   /// </summary>
   /// <returns>USearchSearchCount from system.events.</returns>
   private async Task<long> GraphSearchesAsync()
   {
      string value = await RawAsync( "SELECT value FROM system.events WHERE event = 'USearchSearchCount' FORMAT TSV" );
      return value.Length == 0 ? 0 : long.Parse( value, CultureInfo.InvariantCulture );
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Releases the raw HTTP client.
   /// </summary>
   public void Dispose()
   {
      _http.Dispose();
   }

   #endregion IDisposable
}
