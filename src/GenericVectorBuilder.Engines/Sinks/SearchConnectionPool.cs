using System.Collections.Concurrent;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// The connections of one open collection of an embedded engine (DuckDB, sqlite-vec): one primary
/// connection that writes, and a bounded pool of extra connections that search.
/// Why a pool: an embedded engine runs one call at a time per connection, so one shared connection
/// runs searches one after another and a benchmark pass with 8 searchers measures 1. Giving each
/// concurrent searcher its own connection lets them run side by side.
/// How the access is split: a search rents a connection (at most <c>maxSearchConnections</c> at a
/// time; a connection is opened when a searcher arrives and finds none idle, and kept after that,
/// so the pool ends up as large as the caller's concurrency up to the limit). Work on the primary
/// connection takes the gate, which lets one operation run at a time. An engine that must keep
/// searches away from writes takes exclusive use instead: the gate plus every search slot.
/// Every wait has a deadline and fails with a plain message instead of hanging, and a closed pool
/// refuses new work with <see cref="ObjectDisposedException"/>.
/// </summary>
/// <typeparam name="TConnection">The engine's connection type.</typeparam>
internal sealed class SearchConnectionPool<TConnection> where TConnection : class, IDisposable
{
   #region Data Members

   private readonly SemaphoreSlim _slots;
   private readonly SemaphoreSlim _gate = new( 1, 1 );
   private readonly ConcurrentStack<TConnection> _idle = new();
   private readonly List<TConnection> _opened = new();
   private readonly Func<TConnection> _open;
   private readonly int _maxSearchConnections;
   private readonly TimeSpan _wait;
   private readonly string _label;
   private volatile bool _closed;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Wraps the open primary connection.
   /// </summary>
   /// <param name="primary">The open connection that writes.</param>
   /// <param name="open">Opens and prepares one more search connection.</param>
   /// <param name="maxSearchConnections">How many searches may run at once, which is also the most search connections ever opened.</param>
   /// <param name="wait">Longest any wait for a connection, the gate or exclusive use may last.</param>
   /// <param name="label">Plain name of the collection for messages, such as "duckdb collection 'x'".</param>
   public SearchConnectionPool( TConnection primary, Func<TConnection> open, int maxSearchConnections, TimeSpan wait, string label )
   {
      Primary = primary;
      _open = open;
      _maxSearchConnections = maxSearchConnections;
      _slots = new SemaphoreSlim( maxSearchConnections, maxSearchConnections );
      _wait = wait;
      _label = label;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The primary connection. Use it only while holding the gate or exclusive use.</summary>
   public TConnection Primary { get; }

   /// <summary>How many search connections have been opened so far.</summary>
   public int OpenedCount
   {
      get
      {
         lock( _opened )
         {
            return _opened.Count;
         }
      }
   }

   /// <summary>
   /// Takes the gate: one operation at a time may use the primary connection. Searches on pooled
   /// connections are not held up.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <exception cref="TimeoutException">The wait lasted longer than the deadline.</exception>
   public async Task EnterGateAsync( CancellationToken ct )
   {
      using CancellationTokenSource deadline = Deadline( ct );
      await AcquireAsync( _gate, deadline.Token, ct ).ConfigureAwait( false );
      try
      {
         ObjectDisposedException.ThrowIf( _closed, this );
      }
      catch
      {
         _gate.Release();
         throw;
      }
   }

   /// <summary>
   /// Gives the gate back.
   /// </summary>
   public void ExitGate()
   {
      _gate.Release();
   }

   /// <summary>
   /// Takes exclusive use of the collection: the gate, then every search to hand its connection
   /// back. Nothing else runs on the collection until <see cref="ExitExclusive"/>.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <exception cref="TimeoutException">The wait lasted longer than the deadline.</exception>
   public async Task EnterExclusiveAsync( CancellationToken ct )
   {
      using CancellationTokenSource deadline = Deadline( ct );
      await AcquireAsync( _gate, deadline.Token, ct ).ConfigureAwait( false );
      int held = 0;
      try
      {
         for( ; held < _maxSearchConnections; held++ )
         {
            await AcquireAsync( _slots, deadline.Token, ct ).ConfigureAwait( false );
         }

         ObjectDisposedException.ThrowIf( _closed, this );
      }
      catch
      {
         _slots.Release( held );
         _gate.Release();
         throw;
      }
   }

   /// <summary>
   /// Gives exclusive use back.
   /// </summary>
   public void ExitExclusive()
   {
      _slots.Release( _maxSearchConnections );
      _gate.Release();
   }

   /// <summary>
   /// Rents a search connection for one search, opening a new one while the pool has not reached
   /// its limit and none is idle. Dispose the lease to hand the connection back.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The lease.</returns>
   /// <exception cref="TimeoutException">Every connection stayed busy, or exclusive use stayed taken, past the deadline.</exception>
   public async Task<SearchConnectionLease<TConnection>> RentAsync( CancellationToken ct )
   {
      using( CancellationTokenSource deadline = Deadline( ct ) )
      {
         await AcquireAsync( _slots, deadline.Token, ct ).ConfigureAwait( false );
      }

      try
      {
         ObjectDisposedException.ThrowIf( _closed, this );
         if( _idle.TryPop( out TConnection? idle ) )
         {
            return new SearchConnectionLease<TConnection>( this, idle );
         }

         TConnection opened = await Task.Run( _open, ct ).ConfigureAwait( false );
         lock( _opened )
         {
            _opened.Add( opened );
         }

         return new SearchConnectionLease<TConnection>( this, opened );
      }
      catch
      {
         _slots.Release();
         throw;
      }
   }

   /// <summary>
   /// Hands a rented connection back to the pool. Called by the lease.
   /// </summary>
   /// <param name="connection">The connection.</param>
   public void Return( TConnection connection )
   {
      _idle.Push( connection );
      _slots.Release();
   }

   /// <summary>
   /// Closes every connection, the pooled ones first and the primary last. The caller must hold
   /// exclusive use. Afterwards every call on the pool fails with <see cref="ObjectDisposedException"/>.
   /// Why the primary closes last: closing the last connection to a file writes its log back into
   /// the main file.
   /// </summary>
   public void Close()
   {
      _closed = true;
      lock( _opened )
      {
         foreach( TConnection connection in _opened )
         {
            connection.Dispose();
         }

         _opened.Clear();
      }

      _idle.Clear();
      Primary.Dispose();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A token that is cancelled by the caller or when the wait deadline passes.
   /// </summary>
   /// <param name="ct">The caller's token.</param>
   /// <returns>The linked source; dispose it when the wait is over.</returns>
   private CancellationTokenSource Deadline( CancellationToken ct )
   {
      var source = CancellationTokenSource.CreateLinkedTokenSource( ct );
      source.CancelAfter( _wait );
      return source;
   }

   /// <summary>
   /// Waits on a semaphore, turning "the deadline passed" into a plain timeout message while still
   /// letting the caller's own cancellation through as cancellation.
   /// </summary>
   /// <param name="semaphore">What to wait for.</param>
   /// <param name="deadline">Token that fires on the caller's cancellation or the deadline.</param>
   /// <param name="ct">The caller's own token.</param>
   private async Task AcquireAsync( SemaphoreSlim semaphore, CancellationToken deadline, CancellationToken ct )
   {
      try
      {
         await semaphore.WaitAsync( deadline ).ConfigureAwait( false );
      }
      catch( OperationCanceledException ) when( !ct.IsCancellationRequested )
      {
         throw new TimeoutException( $"Waited {_wait.TotalSeconds:F0} s for the {_label} and it stayed busy: another search or write on it did not finish. Nothing was changed." );
      }
   }

   #endregion Private Methods
}

/// <summary>
/// One rented search connection of a <see cref="SearchConnectionPool{TConnection}"/>. Disposing it
/// hands the connection back to the pool; disposing it twice does nothing.
/// </summary>
/// <typeparam name="TConnection">The engine's connection type.</typeparam>
internal sealed class SearchConnectionLease<TConnection> : IDisposable where TConnection : class, IDisposable
{
   #region Data Members

   private readonly SearchConnectionPool<TConnection> _pool;
   private int _returned;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Wraps a rented connection.
   /// </summary>
   /// <param name="pool">The pool it came from.</param>
   /// <param name="connection">The rented connection.</param>
   public SearchConnectionLease( SearchConnectionPool<TConnection> pool, TConnection connection )
   {
      _pool = pool;
      Connection = connection;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The rented connection, valid until the lease is disposed.</summary>
   public TConnection Connection { get; }

   #endregion Public Methods

   #region IDisposable

   /// <summary>
   /// Hands the connection back.
   /// </summary>
   public void Dispose()
   {
      if( Interlocked.Exchange( ref _returned, 1 ) == 0 )
      {
         _pool.Return( Connection );
      }
   }

   #endregion IDisposable
}
