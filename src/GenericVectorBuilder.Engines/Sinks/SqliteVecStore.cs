using Microsoft.Data.Sqlite;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// One open collection of the SqliteVec sink: its write connection and pool of search connections
/// (<see cref="SearchConnectionPool{TConnection}"/>), and the vector dimension (0 until the table
/// exists).
/// Why a pool: one SQLite connection runs one call at a time. SQLite's threading documentation
/// says that in multi-thread mode no connection may be "used in two or more threads at the same
/// time", and that its serialized mode only makes such calls safe by letting them take turns, so
/// one shared connection runs searches one after another (measured: 8 searchers got 0.98 times the
/// throughput of 1). In WAL mode "readers do not block writers and a writer does not block
/// readers" and "there can only be one writer at a time" (sqlite.org/wal.html), so one connection
/// per searcher lets searches run side by side, and they may overlap a write. The sink switches
/// every file to WAL when it opens it.
/// How the access is split: writes, the count and the readiness check take the gate on the write
/// connection, one at a time, and do not hold up searches. Closing a collection takes exclusive
/// use.
/// </summary>
internal sealed class SqliteVecStore
{
   #region Data Members

   private readonly SearchConnectionPool<SqliteConnection> _pool;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Wraps the open write connection.
   /// </summary>
   /// <param name="connection">The open write connection with vec0 loaded.</param>
   /// <param name="openSearch">Opens and prepares one more search connection (vec0 loaded, query only).</param>
   /// <param name="maxSearchConnections">How many searches may run at once.</param>
   /// <param name="wait">Longest any wait for a connection or for the gate may last.</param>
   /// <param name="label">Plain name of the collection for messages, such as "sqlitevec collection 'x'".</param>
   public SqliteVecStore( SqliteConnection connection, Func<SqliteConnection> openSearch, int maxSearchConnections, TimeSpan wait, string label )
   {
      _pool = new SearchConnectionPool<SqliteConnection>( connection, openSearch, maxSearchConnections, wait, label );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The write connection. Use it only between <see cref="EnterWriteAsync"/> and <see cref="ExitWrite"/>.</summary>
   public SqliteConnection Connection => _pool.Primary;

   /// <summary>Vector length of the collection's table, or 0 when the table does not exist yet.</summary>
   public int Dimension { get; set; }

   /// <summary>How many search connections this collection has opened so far.</summary>
   public int SearchConnectionsOpened => _pool.OpenedCount;

   /// <summary>
   /// Takes the write gate: one operation at a time may use the write connection. Searches on
   /// pooled connections are not held up.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   public Task EnterWriteAsync( CancellationToken ct )
   {
      return _pool.EnterGateAsync( ct );
   }

   /// <summary>
   /// Gives the write gate back.
   /// </summary>
   public void ExitWrite()
   {
      _pool.ExitGate();
   }

   /// <summary>
   /// Takes exclusive use: the gate and every search slot. Used to close the collection.
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
   public Task<SearchConnectionLease<SqliteConnection>> RentSearchAsync( CancellationToken ct )
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
