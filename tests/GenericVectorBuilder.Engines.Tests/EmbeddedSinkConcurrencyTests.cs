using System.Diagnostics;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// What every embedded engine sink (DuckDB, sqlite-vec) must prove about concurrent searches, run
/// on real database files in a throwaway folder (the engines are in process, so there is no
/// container). The benchmark's QPS at 8 searchers is only a measurement of the engine if the sink
/// really lets 8 searches run at once, and these tests hold the sink to that: the same hits as the
/// serialized control, a clear throughput gain from 1 to 8 searchers on 4 client CPUs, a pool that
/// stays bounded, deleted rows that never come back, searches that survive overlapping writes, a
/// wait that ends with a plain timeout, and a drop that closes every pooled connection.
/// Why a serialized control: with SerializeSearches on, the sink runs searches one at a time on
/// one connection, which is what both sinks did before they had a pool. It is the answer key for
/// the hits and the baseline that shows the throughput test can tell the two apart.
/// Each concrete class only says how to create its sink and where its files are.
/// </summary>
public abstract class EmbeddedSinkConcurrencyTests : IDisposable
{
   #region Data Members

   private const int DIMENSION = GroupCReadinessData.DIMENSION;
   private const int COUNT = GroupCReadinessData.COUNT;
   private const int TOP = 10;
   private const int SEARCHERS = 8;
   private const int QUERIES = 64;
   private const int SPEED_ROUNDS = 5;
   private const double WINDOW_SECONDS = 1.5;
   private const double MIN_SPEEDUP = 1.8;
   private const double MAX_SERIALIZED_SPEEDUP = 1.5;
   private const int MIN_CLIENT_CPUS = 4;
   private const double SCORE_TOLERANCE = 1e-9;

   private readonly ITestOutputHelper _output;
   private readonly string _folder;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xunit's output sink and a throwaway folder under ~/gvb-work.
   /// </summary>
   /// <param name="output">Where the evidence is printed.</param>
   /// <param name="tag">Short engine tag for folder and collection names, e.g. "duck".</param>
   protected EmbeddedSinkConcurrencyTests( ITestOutputHelper output, string tag )
   {
      _output = output;
      Tag = tag;
      _folder = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ), "gvb-work", $"gvbbench-{tag}-conc-" + Guid.NewGuid().ToString( "N" )[..8] );
   }

   #endregion Constructor

   #region Overrides

   /// <summary>Short engine tag, e.g. "duck".</summary>
   protected string Tag { get; }

   /// <summary>
   /// Creates the engine's sink on a folder.
   /// </summary>
   /// <param name="folder">Where the database files live.</param>
   /// <param name="serialized">True for the one-search-at-a-time control.</param>
   /// <param name="maxSearchConnections">The pool limit to ask for.</param>
   /// <param name="waitSeconds">The connection wait limit in seconds.</param>
   /// <returns>The sink, which must also implement <see cref="IExactSearchSink"/>, <see cref="IEngineDescription"/> and <see cref="IDisposable"/>.</returns>
   protected abstract ISink CreateSink( string folder, bool serialized, int maxSearchConnections, int waitSeconds );

   /// <summary>
   /// How many search connections the sink has opened for a collection.
   /// </summary>
   /// <param name="sink">A sink made by <see cref="CreateSink"/>.</param>
   /// <param name="collection">Collection name.</param>
   /// <returns>The count.</returns>
   protected abstract int ConnectionsOpened( ISink sink, string collection );

   /// <summary>
   /// Path of the collection's database file.
   /// </summary>
   /// <param name="folder">The throwaway folder.</param>
   /// <param name="collection">Collection name.</param>
   /// <returns>The path.</returns>
   protected abstract string FileOf( string folder, string collection );

   /// <summary>
   /// How many records one upsert call needs to be slow enough that a wait of 1 second in another
   /// call ends with a timeout while it runs. Engines write at very different speeds (sqlite-vec
   /// appends, DuckDB maintains an HNSW graph), so each says its own number.
   /// </summary>
   protected abstract int LongUpsertCount { get; }

   /// <summary>The throwaway folder this test class writes to.</summary>
   protected string Folder => _folder;

   #endregion Overrides

   #region Public Methods

   /// <summary>
   /// The index description tells the report how searches run: concurrently for the default sink,
   /// and exactly "searches run one at a time (single connection)" for the serialized control.
   /// </summary>
   [Fact]
   public void IndexDescription_SaysHowSearchesRun()
   {
      using var pooled = (IDisposable)CreateSink( Folder, serialized: false, maxSearchConnections: 32, waitSeconds: 120 );
      using var serial = (IDisposable)CreateSink( Folder, serialized: true, maxSearchConnections: 32, waitSeconds: 120 );
      string concurrent = ( (IEngineDescription)pooled ).IndexDescription;
      string serialized = ( (IEngineDescription)serial ).IndexDescription;
      _output.WriteLine( "concurrent: " + concurrent );
      _output.WriteLine( "serialized: " + serialized );
      Assert.Contains( "searches run concurrently", concurrent );
      Assert.DoesNotContain( "searches run one at a time", concurrent );
      Assert.Contains( "searches run one at a time (single connection)", serialized );
      Assert.DoesNotContain( "concurrently", serialized );
   }

   /// <summary>
   /// 8 searchers on the pooled sink get exactly the hits (same chunks, same order, same scores)
   /// that one searcher gets from the serialized control, in both the default and the exact mode,
   /// and the pool really opened more than one connection.
   /// </summary>
   [Fact]
   public async Task ConcurrentSearches_ReturnTheSameHitsAsSerializedSearches()
   {
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      CancellationToken ct = deadline.Token;
      string collection = NewCollection();
      List<VectorRecord> records = GroupCReadinessData.MakeRecords( COUNT, 71 );
      float[][] queries = MakeQueries( records );
      try
      {
         await LoadThenCloseAsync( collection, records, ct );
         IReadOnlyList<SearchHit>[] serialDefault, serialExact;
         using( var serial = (IDisposable)CreateSink( Folder, serialized: true, maxSearchConnections: 32, waitSeconds: 120 ) )
         {
            serialDefault = await RunAllAsync( (ISink)serial, collection, queries, exact: false, searchers: 1, ct );
            serialExact = await RunAllAsync( (ISink)serial, collection, queries, exact: true, searchers: 1, ct );
            Assert.Equal( 0, ConnectionsOpened( (ISink)serial, collection ) );
         }

         using var pooled = (IDisposable)CreateSink( Folder, serialized: false, maxSearchConnections: 32, waitSeconds: 120 );
         IReadOnlyList<SearchHit>[] poolDefault = await RunAllAsync( (ISink)pooled, collection, queries, exact: false, SEARCHERS, ct );
         IReadOnlyList<SearchHit>[] poolExact = await RunAllAsync( (ISink)pooled, collection, queries, exact: true, SEARCHERS, ct );
         int opened = ConnectionsOpened( (ISink)pooled, collection );
         _output.WriteLine( $"{QUERIES} queries x 2 modes compared; the pooled sink opened {opened} search connections for {SEARCHERS} searchers" );
         AssertSameHits( serialDefault, poolDefault, "default" );
         AssertSameHits( serialExact, poolExact, "exact" );
         Assert.InRange( opened, 2, SEARCHERS );
      }
      finally
      {
         await DropAsync( collection );
      }
   }

   /// <summary>
   /// With 4 client CPUs, 8 searchers on the pooled sink get at least 1.8 times the searches per
   /// second that 1 searcher gets, while the serialized control stays near 1 times. Each ratio is
   /// the median of 5 rounds, and each round times 1 searcher and 8 searchers back to back (in
   /// alternating order), so a slow moment on a shared box hits both sides of a ratio.
   /// Measured on this box pinned to 4 CPUs, with other work running on it: DuckDB 2.2x to 4.1x,
   /// sqlite-vec 2.6x to 3.5x; the serialized control 0.8x to 1.1x; the code before the pool
   /// scored 0.90x (DuckDB) and 0.95x (sqlite-vec).
   /// </summary>
   [Fact]
   public async Task EightSearchers_BeatOneSearcher_ByAClearMargin()
   {
      Assert.True( Environment.ProcessorCount >= MIN_CLIENT_CPUS, $"this proof needs {MIN_CLIENT_CPUS} client CPUs and the process sees {Environment.ProcessorCount}" );
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      CancellationToken ct = deadline.Token;
      string collection = NewCollection();
      List<VectorRecord> records = GroupCReadinessData.MakeRecords( COUNT, 72 );
      float[][] queries = MakeQueries( records );
      try
      {
         await LoadThenCloseAsync( collection, records, ct );
         double serialRatio, poolRatio;
         int opened;
         using( var serial = (IDisposable)CreateSink( Folder, serialized: true, maxSearchConnections: 32, waitSeconds: 120 ) )
         {
            serialRatio = await SpeedupAsync( (ISink)serial, collection, queries, "serialized", ct );
         }

         using( var pooled = (IDisposable)CreateSink( Folder, serialized: false, maxSearchConnections: 32, waitSeconds: 120 ) )
         {
            poolRatio = await SpeedupAsync( (ISink)pooled, collection, queries, "pooled", ct );
            opened = ConnectionsOpened( (ISink)pooled, collection );
         }

         _output.WriteLine( $"client CPUs seen by the process: {Environment.ProcessorCount}; search connections opened by the pooled sink: {opened}" );
         _output.WriteLine( $"QPS@8 / QPS@1: pooled {poolRatio:F2}x, serialized control {serialRatio:F2}x" );
         Assert.True( poolRatio >= MIN_SPEEDUP, $"8 searchers got only {poolRatio:F2}x the throughput of 1 on the pooled sink (needs {MIN_SPEEDUP}x)" );
         Assert.True( serialRatio <= MAX_SERIALIZED_SPEEDUP, $"the serialized control scaled {serialRatio:F2}x, so the comparison cannot tell serialized from concurrent" );
         Assert.InRange( opened, 4, SEARCHERS );
      }
      finally
      {
         await DropAsync( collection );
      }
   }

   /// <summary>
   /// The pool is bounded: a limit below the minimum is raised to 8, 40 searchers in flight never
   /// make it open more than 8 connections, and every one of their searches still completes.
   /// </summary>
   [Fact]
   public async Task Pool_StaysBounded_WhenMoreSearchersThanConnectionsArrive()
   {
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      CancellationToken ct = deadline.Token;
      string collection = NewCollection();
      List<VectorRecord> records = GroupCReadinessData.MakeRecords( COUNT, 73 );
      float[][] queries = MakeQueries( records );
      try
      {
         await LoadThenCloseAsync( collection, records, ct );
         using var sink = (IDisposable)CreateSink( Folder, serialized: false, maxSearchConnections: 1, waitSeconds: 120 );
         IReadOnlyList<SearchHit>[] hits = await RunAllAsync( (ISink)sink, collection, Enumerable.Repeat( queries, 3 ).SelectMany( q => q ).ToArray(), exact: false, searchers: 40, ct );
         int opened = ConnectionsOpened( (ISink)sink, collection );
         _output.WriteLine( $"40 searchers, limit asked for 1: {opened} search connections opened, {hits.Length} searches completed" );
         Assert.InRange( opened, 2, 8 );
         Assert.All( hits, h => Assert.Equal( TOP, h.Count ) );
      }
      finally
      {
         await DropAsync( collection );
      }
   }

   /// <summary>
   /// Rows deleted before a burst of concurrent searches never come back from any of them, in
   /// either mode, and every search still returns a full result (DuckDB compacts its HNSW index
   /// before the first search that follows a delete).
   /// </summary>
   [Fact]
   public async Task DeletedChunks_NeverComeBackFromConcurrentSearches()
   {
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      CancellationToken ct = deadline.Token;
      string collection = NewCollection();
      List<VectorRecord> records = GroupCReadinessData.MakeRecords( COUNT, 74 );
      float[][] queries = MakeQueries( records );
      try
      {
         using var sink = (IDisposable)CreateSink( Folder, serialized: false, maxSearchConnections: 32, waitSeconds: 120 );
         var target = (ISink)sink;
         await target.EnsureCollectionAsync( collection, DIMENSION, ct );
         await GroupCReadinessData.LoadAsync( target, collection, records, ct );
         HashSet<Guid> gone = records.Take( 500 ).Select( r => r.Chunk.ChunkId ).ToHashSet();
         await target.DeleteAsync( collection, gone.ToList(), ct );
         foreach( bool exact in new[] { false, true } )
         {
            IReadOnlyList<SearchHit>[] hits = await RunAllAsync( target, collection, queries, exact, SEARCHERS, ct );
            Assert.All( hits, h => Assert.Equal( TOP, h.Count ) );
            Assert.DoesNotContain( hits.SelectMany( h => h ), hit => gone.Contains( hit.ChunkId ) );
         }

         Assert.Equal( COUNT - 500, await target.CountAsync( collection, ct ) );
         _output.WriteLine( $"{QUERIES} queries x 2 modes after deleting 500 of {COUNT} rows: none returned a deleted chunk" );
      }
      finally
      {
         await DropAsync( collection );
      }
   }

   /// <summary>
   /// Searches that run while a writer upserts new batches never fail, every hit is a whole row,
   /// and when the writer is done every batch is counted.
   /// </summary>
   [Fact]
   public async Task Searches_DuringUpserts_NeverFail_AndSeeWholeRows()
   {
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      CancellationToken ct = deadline.Token;
      string collection = NewCollection();
      List<VectorRecord> first = GroupCReadinessData.MakeRecords( 500, 75 );
      List<VectorRecord> more = GroupCReadinessData.MakeRecords( 2000, 76 );
      try
      {
         using var sink = (IDisposable)CreateSink( Folder, serialized: false, maxSearchConnections: 32, waitSeconds: 120 );
         var target = (ISink)sink;
         await target.EnsureCollectionAsync( collection, DIMENSION, ct );
         await GroupCReadinessData.LoadAsync( target, collection, first, ct );
         bool writing = true;
         int searches = 0;
         Task writer = Task.Run( async () =>
         {
            foreach( VectorRecord[] batch in more.Chunk( 250 ) )
            {
               await target.UpsertAsync( collection, batch, ct );
            }

            writing = false;
         }, ct );
         Task[] searchers = Enumerable.Range( 0, 4 ).Select( s => Task.Run( async () =>
         {
            while( Volatile.Read( ref writing ) )
            {
               IReadOnlyList<SearchHit> hits = await target.SearchAsync( collection, first[s].Vector, TOP, ct );
               Assert.InRange( hits.Count, 1, TOP );
               Assert.All( hits, h => Assert.StartsWith( "chunk text ", h.Text ) );
               Interlocked.Increment( ref searches );
            }
         }, ct ) ).ToArray();
         await Task.WhenAll( searchers.Append( writer ) );
         Assert.Equal( first.Count + more.Count, await target.CountAsync( collection, ct ) );
         _output.WriteLine( $"{searches} searches ran while 8 batches of 250 were written; none failed" );
         Assert.True( searches > 0, "no search ran during the writes, so the overlap was not exercised" );
      }
      finally
      {
         await DropAsync( collection );
      }
   }

   /// <summary>
   /// A call that has to wait for a long write gives up at the wait limit with a plain message
   /// that says nothing was changed, and the write itself still finishes and counts every row.
   /// </summary>
   [Fact]
   public async Task BusyCollection_EndsTheWaitWithATimeout_AndChangesNothing()
   {
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      CancellationToken ct = deadline.Token;
      string collection = NewCollection();
      List<VectorRecord> records = GroupCReadinessData.MakeRecords( LongUpsertCount, 77 );
      try
      {
         using var sink = (IDisposable)CreateSink( Folder, serialized: false, maxSearchConnections: 32, waitSeconds: 1 );
         var target = (ISink)sink;
         await target.EnsureCollectionAsync( collection, DIMENSION, ct );
         Task write = Task.Run( () => target.UpsertAsync( collection, records, ct ), ct );
         await Task.Delay( 500, ct );
         var clock = Stopwatch.StartNew();
         TimeoutException error = await Assert.ThrowsAsync<TimeoutException>( () => target.CountAsync( collection, ct ) );
         double waited = clock.Elapsed.TotalSeconds;
         _output.WriteLine( $"count gave up after {waited:F1} s: {error.Message}" );
         Assert.InRange( waited, 0.8, 5.0 );
         Assert.Contains( collection, error.Message );
         Assert.Contains( "Nothing was changed", error.Message );
         Assert.False( write.IsCompleted, "the write finished before the wait limit, so nothing was proven" );
         await write;
         Assert.Equal( LongUpsertCount, await target.CountAsync( collection, ct ) );
      }
      finally
      {
         await DropAsync( collection );
      }
   }

   /// <summary>
   /// Dropping a collection whose pool is open closes every pooled connection: the files are gone,
   /// and the same name can be created again and starts empty.
   /// </summary>
   [Fact]
   public async Task Drop_ClosesPooledConnections_AndTheNameCanBeReused()
   {
      using var deadline = new CancellationTokenSource( GroupCReadinessData.TEST_DEADLINE );
      CancellationToken ct = deadline.Token;
      string collection = NewCollection();
      List<VectorRecord> records = GroupCReadinessData.MakeRecords( 300, 78 );
      try
      {
         using var sink = (IDisposable)CreateSink( Folder, serialized: false, maxSearchConnections: 32, waitSeconds: 120 );
         var target = (ISink)sink;
         await target.EnsureCollectionAsync( collection, DIMENSION, ct );
         await GroupCReadinessData.LoadAsync( target, collection, records, ct );
         await RunAllAsync( target, collection, MakeQueries( records ), exact: false, SEARCHERS, ct );
         Assert.True( ConnectionsOpened( target, collection ) >= 2, "the pool never opened a second connection" );

         await target.DropCollectionAsync( collection, ct );
         Assert.False( File.Exists( FileOf( Folder, collection ) ), "the database file is still there after the drop" );
         Assert.Equal( 0, ConnectionsOpened( target, collection ) );
         Assert.True( await target.EnsureCollectionAsync( collection, DIMENSION, ct ), "the name could not be created again" );
         Assert.Equal( 0, await target.CountAsync( collection, ct ) );
      }
      finally
      {
         await DropAsync( collection );
      }
   }

   /// <summary>
   /// Removes the throwaway folder with its database files.
   /// </summary>
   public void Dispose()
   {
      if( Directory.Exists( _folder ) && Path.GetFileName( _folder ).StartsWith( $"gvbbench-{Tag}-conc-", StringComparison.Ordinal ) )
      {
         Directory.Delete( _folder, recursive: true );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A throwaway collection name that starts with gvbbench_ and uses only [a-z0-9_].
   /// </summary>
   /// <returns>A unique name.</returns>
   private string NewCollection()
   {
      return $"gvbbench_{Tag}_conc_{Guid.NewGuid().ToString( "N" )[..8]}";
   }

   /// <summary>
   /// Creates the collection, loads the records with a serialized sink, and closes the sink, so
   /// every sink the test then opens starts from the file as it is on disk.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="records">Records to load.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task LoadThenCloseAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      using var loader = (IDisposable)CreateSink( Folder, serialized: true, maxSearchConnections: 32, waitSeconds: 120 );
      var sink = (ISink)loader;
      await sink.EnsureCollectionAsync( collection, DIMENSION, ct );
      await GroupCReadinessData.LoadAsync( sink, collection, records, ct );
   }

   /// <summary>
   /// Drops a collection with a fresh sink, whatever state the test left it in.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   private async Task DropAsync( string collection )
   {
      using var cleaner = (IDisposable)CreateSink( Folder, serialized: true, maxSearchConnections: 32, waitSeconds: 120 );
      await ( (ISink)cleaner ).DropCollectionAsync( collection, CancellationToken.None );
   }

   /// <summary>
   /// Builds the fixed query set: half are stored vectors (so the right answer is known to be in
   /// the data) and half are fresh random unit vectors.
   /// </summary>
   /// <param name="records">The loaded records.</param>
   /// <returns>The queries.</returns>
   private static float[][] MakeQueries( IReadOnlyList<VectorRecord> records )
   {
      float[][] fresh = GroupCReadinessData.MakeRecords( QUERIES / 2, 999 ).Select( r => r.Vector ).ToArray();
      return records.Take( QUERIES / 2 ).Select( r => r.Vector ).Concat( fresh ).ToArray();
   }

   /// <summary>
   /// Runs every query once, spread over the given number of searchers, and keeps each answer in
   /// the position of its query.
   /// </summary>
   /// <param name="sink">The sink under test.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="queries">The queries.</param>
   /// <param name="exact">True for the exact search mode.</param>
   /// <param name="searchers">How many searchers run at once.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>One hit list per query.</returns>
   private static async Task<IReadOnlyList<SearchHit>[]> RunAllAsync( ISink sink, string collection, float[][] queries, bool exact, int searchers, CancellationToken ct )
   {
      var results = new IReadOnlyList<SearchHit>[queries.Length];
      int next = -1;
      Task[] workers = Enumerable.Range( 0, searchers ).Select( _ => Task.Run( async () =>
      {
         int i;
         while( ( i = Interlocked.Increment( ref next ) ) < queries.Length )
         {
            results[i] = exact
               ? await ( (IExactSearchSink)sink ).SearchExactAsync( collection, queries[i], TOP, ct )
               : await sink.SearchAsync( collection, queries[i], TOP, ct );
         }
      }, ct ) ).ToArray();
      await Task.WhenAll( workers );
      return results;
   }

   /// <summary>
   /// Requires two answer sets to match query by query: the same chunks in the same order with the
   /// same payload and the same scores.
   /// </summary>
   /// <param name="expected">The serialized control's answers.</param>
   /// <param name="actual">The pooled sink's answers.</param>
   /// <param name="mode">Mode name for the message.</param>
   private static void AssertSameHits( IReadOnlyList<SearchHit>[] expected, IReadOnlyList<SearchHit>[] actual, string mode )
   {
      Assert.Equal( expected.Length, actual.Length );
      for( int q = 0; q < expected.Length; q++ )
      {
         Assert.Equal( TOP, expected[q].Count );
         Assert.Equal( expected[q].Count, actual[q].Count );
         for( int h = 0; h < expected[q].Count; h++ )
         {
            SearchHit want = expected[q][h];
            SearchHit got = actual[q][h];
            Assert.True( want.ChunkId == got.ChunkId, $"{mode} query {q} hit {h}: chunk {got.ChunkId} instead of {want.ChunkId}" );
            Assert.Equal( want.Text, got.Text );
            Assert.InRange( Math.Abs( want.Score - got.Score ), 0, SCORE_TOLERANCE );
         }
      }
   }

   /// <summary>
   /// Measures searches per second with 1 searcher and with 8 in each of several rounds, the two
   /// windows of a round back to back in alternating order, and returns the median of the
   /// per-round ratios. Why per round: on a shared box a slow moment lasts longer than one
   /// window, so a ratio of two neighbouring windows is steadier than a ratio of two medians.
   /// </summary>
   /// <param name="sink">The sink under test.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="queries">The queries to cycle through.</param>
   /// <param name="label">Name for the printed line.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Median over the rounds of QPS at 8 searchers divided by QPS at 1.</returns>
   private async Task<double> SpeedupAsync( ISink sink, string collection, float[][] queries, string label, CancellationToken ct )
   {
      await RunAllAsync( sink, collection, queries, exact: false, SEARCHERS, ct );
      var ratios = new List<double>();
      var lines = new List<string>();
      for( int round = 0; round < SPEED_ROUNDS; round++ )
      {
         double one, eight;
         if( round % 2 == 0 )
         {
            one = await WindowQpsAsync( sink, collection, queries, 1, ct );
            eight = await WindowQpsAsync( sink, collection, queries, SEARCHERS, ct );
         }
         else
         {
            eight = await WindowQpsAsync( sink, collection, queries, SEARCHERS, ct );
            one = await WindowQpsAsync( sink, collection, queries, 1, ct );
         }

         ratios.Add( eight / one );
         lines.Add( $"{one:F0}->{eight:F0}" );
      }

      double median = Median( ratios );
      _output.WriteLine( $"{label}: QPS@1->QPS@8 per round {string.Join( ", ", lines )}; ratios {string.Join( " ", ratios.Select( r => r.ToString( "F2" ) ) )}; median {median:F2}" );
      return median;
   }

   /// <summary>
   /// Runs the given number of searchers back to back for one window and returns searches per second.
   /// </summary>
   /// <param name="sink">The sink under test.</param>
   /// <param name="collection">Collection name.</param>
   /// <param name="queries">The queries to cycle through.</param>
   /// <param name="searchers">How many searchers.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Searches per second over the window.</returns>
   private static async Task<double> WindowQpsAsync( ISink sink, string collection, float[][] queries, int searchers, CancellationToken ct )
   {
      long done = 0;
      var clock = Stopwatch.StartNew();
      Task[] workers = Enumerable.Range( 0, searchers ).Select( s => Task.Run( async () =>
      {
         for( int i = s; clock.Elapsed.TotalSeconds < WINDOW_SECONDS; i++ )
         {
            await sink.SearchAsync( collection, queries[i % queries.Length], TOP, ct );
            Interlocked.Increment( ref done );
         }
      }, ct ) ).ToArray();
      await Task.WhenAll( workers );
      return done / clock.Elapsed.TotalSeconds;
   }

   /// <summary>
   /// The middle value of a list of figures.
   /// </summary>
   /// <param name="values">The figures.</param>
   /// <returns>The median.</returns>
   private static double Median( List<double> values )
   {
      List<double> sorted = values.OrderBy( v => v ).ToList();
      return sorted[sorted.Count / 2];
   }

   #endregion Private Methods
}
