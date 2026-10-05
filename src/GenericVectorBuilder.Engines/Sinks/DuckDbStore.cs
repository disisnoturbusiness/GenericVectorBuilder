using DuckDB.NET.Data;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// One open collection of the DuckDB sink: its write connection and pool of search connections
/// (<see cref="SearchConnectionPool{TConnection}"/>), the vector dimension (0 until the table
/// exists) and whether deleted rows are still waiting to be squeezed out of the HNSW index.
/// Why a pool: DuckDB's C API says "individual connections are thread-safe, they will be locked
/// during querying. It is therefore recommended that each thread uses its own connection to allow
/// for the best parallel performance." One shared connection therefore runs searches one after
/// another (measured: 8 searchers got 0.99 times the throughput of 1), and one connection per
/// searcher lets them run side by side. DuckDB.NET hands every connection opened on the same file
/// in this process the same database instance, so a pooled connection sees every committed row.
/// Why writes take exclusive use: writes, the count, the readiness check and index compaction
/// wait for every search to finish and keep new ones out, so they never overlap a search. The vss
/// extension documents no rule for an HNSW index that is searched on one connection while another
/// connection writes to it, and its file-backed index is marked experimental, so the sink does not
/// rely on that.
/// </summary>
internal sealed class DuckDbStore
{
   #region Data Members

   private readonly SearchConnectionPool<DuckDBConnection> _pool;
   private volatile bool _needsCompaction;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Wraps the open write connection.
   /// </summary>
   /// <param name="connection">The open write connection with the vss extension loaded.</param>
   /// <param name="openSearch">Opens and prepares one more search connection (vss loaded, ef_search set).</param>
   /// <param name="maxSearchConnections">How many searches may run at once.</param>
   /// <param name="wait">Longest any wait for a connection or for exclusive use may last.</param>
   /// <param name="label">Plain name of the collection for messages, such as "duckdb collection 'x'".</param>
   public DuckDbStore( DuckDBConnection connection, Func<DuckDBConnection> openSearch, int maxSearchConnections, TimeSpan wait, string label )
   {
      _pool = new SearchConnectionPool<DuckDBConnection>( connection, openSearch, maxSearchConnections, wait, label );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The write connection. Use it only between <see cref="EnterExclusiveAsync"/> and <see cref="ExitExclusive"/>.</summary>
   public DuckDBConnection Connection => _pool.Primary;

   /// <summary>Vector length of the collection's table, or 0 when the table does not exist yet.</summary>
   public int Dimension { get; set; }

   /// <summary>
   /// True after rows were deleted or replaced. The HNSW index only marks such rows as deleted,
   /// so the next search compacts the index first and clears this flag. Only changed while the
   /// caller has exclusive use.
   /// </summary>
   public bool NeedsCompaction
   {
      get => _needsCompaction;
      set => _needsCompaction = value;
   }

   /// <summary>How many search connections this collection has opened so far.</summary>
   public int SearchConnectionsOpened => _pool.OpenedCount;

   /// <summary>
   /// Takes exclusive use: one writer at a time, and no search running or starting.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   public Task EnterExclusiveAsync( CancellationToken ct )
   {
      return _pool.EnterExclusiveAsync( ct );
   }

   /// <summary>
   /// Gives exclusive use back.
   /// </summary>
   public void ExitExclusive()
   {
      _pool.ExitExclusive();
   }

   /// <summary>
   /// Rents a search connection for one search. Dispose the lease to hand it back.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The lease.</returns>
   public Task<SearchConnectionLease<DuckDBConnection>> RentSearchAsync( CancellationToken ct )
   {
      return _pool.RentAsync( ct );
   }

   /// <summary>
   /// Closes every connection. The caller must hold exclusive use.
   /// </summary>
   public void CloseConnections()
   {
      _pool.Close();
   }

   #endregion Public Methods
}
