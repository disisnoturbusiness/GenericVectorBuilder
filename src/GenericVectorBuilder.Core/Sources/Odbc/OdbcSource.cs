using System.Data.Odbc;
using System.Runtime.CompilerServices;
using GenericVectorBuilder.Core.Contracts;

namespace GenericVectorBuilder.Core.Sources.Odbc;

/// <summary>
/// Source over chosen tables and views of one ODBC connection. Each table is one origin,
/// "&lt;connection&gt;/&lt;schema&gt;.&lt;name&gt;", streamed with a SELECT of its readable columns.
/// The safety rules, which mirror the folder source:
/// - A table read to the end is marked clean, so rows that left it may be deleted.
/// - A table that fails (not found, permission denied, the connection dropped mid-read) is
///   rejected with a plain reason and keeps its old vectors; the next table is still read.
/// - The report says Completed only after every chosen table was attempted.
/// - A table that was not read is confirmed gone only when the catalog listing worked, listed at
///   least one table, and did not list it, AND querying it fails with "not found" (SQLSTATE 42S02).
///   Why the query too: SQL Server hides tables a login may not read from the catalog (measured
///   2026-10-03 with DENY SELECT: the table vanished from SQLTables and SQLColumns), so a missing
///   listing alone would delete the vectors of a table that only lost its permission. The query
///   answers "permission denied" for such a table, and that is never treated as gone. The
///   "not found" must also name the table itself, because a view whose inner table was dropped
///   answers 42S02 too (see <see cref="OdbcErrors.IsNotFound"/>).
/// Each table is read on its own connection, so a connection killed mid-read costs that table
/// only. Once a connection cannot even be opened (server down or unknown, login refused, DSN
/// gone), the rest of the run does not try again: the other tables are rejected with the same
/// reason and nothing is confirmed gone. Why: every attempt would fail the same way, each one can
/// wait the driver's full login timeout (15 seconds per table, measured 2026-10-03), and every
/// refused login counts toward the account lockout a server may enforce.
/// Values are formatted culture-invariant (see <see cref="OdbcValues"/>).
/// </summary>
public sealed class OdbcSource : ISource
{
   #region Data Members

   /// <summary>
   /// Seconds a statement may wait for the database. A big view can take minutes before its first
   /// row; ten minutes of silence means the database is stuck, and the table is rejected.
   /// </summary>
   private const int READ_TIMEOUT_SECONDS = 600;

   /// <summary>Start of the reason given to tables that were not tried after a connection could not be opened.</summary>
   private const string NOT_TRIED = "Not tried, because connecting already failed for an earlier table:";

   private readonly string _connectionName;
   private readonly string _connectionString;
   private readonly string? _secret;
   private readonly string _originPrefix;
   private readonly IReadOnlyList<OdbcSelection> _tables;
   private HashSet<string>? _listedOrigins;
   private string? _connectFailure;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the source. Duplicate selections are read once.
   /// </summary>
   /// <param name="connectionName">The stable connection name (<see cref="OdbcCatalog.ConnectionName"/>). It goes into every
   /// origin and table id. A raw connection string passed here by mistake is reduced to its stable name, so no login can
   /// leak into an origin.</param>
   /// <param name="connectionString">The connection string to open, login included. Kept in memory only.</param>
   /// <param name="tables">Tables and views to read, in order.</param>
   /// <exception cref="ArgumentException">The connection name or string is empty.</exception>
   public OdbcSource( string connectionName, string connectionString, IReadOnlyList<OdbcSelection> tables )
   {
      if( string.IsNullOrWhiteSpace( connectionName ) || string.IsNullOrWhiteSpace( connectionString ) )
      {
         throw new ArgumentException( "An ODBC source needs a connection name and a connection string." );
      }

      _connectionName = OdbcNaming.ConnectionName( connectionName );
      _connectionString = connectionString;
      _secret = OdbcErrors.SecretOf( connectionString );
      _originPrefix = OdbcNaming.OriginPrefix( _connectionName );
      _tables = tables.Distinct().ToList();
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Description => $"ODBC {_connectionName} ({_tables.Count} table(s))";

   /// <inheritdoc />
   public SourceReadReport Report { get; } = new();

   /// <summary>
   /// The display name of a table, e.g. "production_product".
   /// </summary>
   /// <param name="t">The table.</param>
   /// <returns>The display name.</returns>
   public static string TableName( OdbcSelection t )
   {
      return OdbcNaming.TableName( t );
   }

   /// <summary>
   /// The stable id of a table, from the connection name, schema and table name only. Two tables
   /// with the same columns get different ids, and a changed column list keeps the id.
   /// </summary>
   /// <param name="connectionName">The stable connection name.</param>
   /// <param name="t">The table.</param>
   /// <returns>E.g. "t3f9a2c41d0e7b6a5".</returns>
   public static string TableId( string connectionName, OdbcSelection t )
   {
      return OdbcNaming.TableId( connectionName, t );
   }

   /// <summary>
   /// The origin a table's records carry, e.g. "AdventureWorks2025/Production.Product".
   /// </summary>
   /// <param name="connectionName">The stable connection name.</param>
   /// <param name="t">The table.</param>
   /// <returns>The origin.</returns>
   public static string Origin( string connectionName, OdbcSelection t )
   {
      return OdbcNaming.Origin( connectionName, t );
   }

   /// <inheritdoc />
   public async IAsyncEnumerable<SourceRecord> ReadAsync( [EnumeratorCancellation] CancellationToken ct )
   {
      foreach( OdbcSelection table in _tables )
      {
         ct.ThrowIfCancellationRequested();
         await foreach( SourceRecord record in ReadTableAsync( table, ct ) )
         {
            yield return record;
         }
      }

      Report.Completed = true;
   }

   /// <inheritdoc />
   public async Task<bool> ConfirmGoneAsync( string origin, CancellationToken ct )
   {
      ct.ThrowIfCancellationRequested();
      if( !origin.StartsWith( _originPrefix, StringComparison.Ordinal ) || origin.Length == _originPrefix.Length
         || Report.IsClean( origin ) || Report.IsRejected( origin ) || Report.HasSkippedRows( origin ) )
      {
         return false;
      }

      return await Task.Run( () => IsGone( origin, ct ), ct );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads one table. A failure at any point (connecting, describing, querying, or part way
   /// through the rows) rejects the table with a plain reason and ends it; rows already emitted
   /// are still valid rows. A cancel is passed on as a cancel, never as a rejection.
   /// </summary>
   /// <param name="table">The table.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The table's records.</returns>
   private async IAsyncEnumerable<SourceRecord> ReadTableAsync( OdbcSelection table, [EnumeratorCancellation] CancellationToken ct )
   {
      string origin = Origin( _connectionName, table );
      await Task.Yield();
      if( _connectFailure != null )
      {
         Report.MarkRejected( origin, $"{NOT_TRIED} {_connectFailure}" );
         yield break;
      }

      IEnumerator<SourceRecord> rows = ReadRows( table, origin, ct ).GetEnumerator();
      try
      {
         while( true )
         {
            SourceRecord record;
            try
            {
               if( !rows.MoveNext() )
               {
                  break;
               }

               record = rows.Current;
            }
            catch( Exception ex ) when( ex is not OperationCanceledException )
            {
               ct.ThrowIfCancellationRequested();
               Report.MarkRejected( origin, OdbcErrors.Describe( ex, _secret ) );
               yield break;
            }

            yield return record;
         }

         Report.MarkClean( origin );
      }
      finally
      {
         DisposeQuietly( rows );
      }
   }

   /// <summary>
   /// Disposes a table's row reader (which closes its connection), ignoring failures from a
   /// connection that is already dead, so one dropped table can never fail the whole run.
   /// </summary>
   /// <param name="rows">The row enumerator.</param>
   private static void DisposeQuietly( IDisposable rows )
   {
      try
      {
         rows.Dispose();
      }
      catch( Exception ex ) when( ex is OdbcException or InvalidOperationException )
      {
         // Nothing left to release; the table's outcome is already recorded.
      }
   }

   /// <summary>
   /// Opens a connection. When it cannot be opened, the reason is kept, so the rest of the run
   /// rejects its tables with it instead of trying again (see the class notes).
   /// </summary>
   /// <returns>The open connection; the caller disposes it.</returns>
   /// <exception cref="OdbcException">The connection could not be opened.</exception>
   private OdbcConnection Open()
   {
      var connection = new OdbcConnection( _connectionString );
      try
      {
         connection.Open();
         return connection;
      }
      catch( OdbcException ex )
      {
         _connectFailure = OdbcErrors.Describe( ex, _secret );
         connection.Dispose();
         throw;
      }
   }

   /// <summary>
   /// Opens a connection, works out the readable columns, and streams the rows. When the rows are
   /// not read to the end, the statement is cancelled before the reader closes, so the driver does
   /// not drain the rest of a large result.
   /// </summary>
   /// <param name="table">The table.</param>
   /// <param name="origin">The table's origin.</param>
   /// <param name="ct">Cancellation; it also cancels the running statement.</param>
   /// <returns>The records.</returns>
   private IEnumerable<SourceRecord> ReadRows( OdbcSelection table, string origin, CancellationToken ct )
   {
      using OdbcConnection connection = Open();
      string? quote = OdbcNaming.QuoteCharOf( connection );
      OdbcTableShape shape = OdbcMetadata.LoadShape( connection, table, quote, ct );
      using var command = new OdbcCommand( OdbcMetadata.BuildSelect( shape, table, quote ), connection ) { CommandTimeout = READ_TIMEOUT_SECONDS };
      using CancellationTokenRegistration registration = ct.Register( command.Cancel );
      OdbcDataReader reader = command.ExecuteReader();
      bool finished = false;
      try
      {
         string tableName = TableName( table );
         string tableId = TableId( _connectionName, table );
         IReadOnlyList<string> columns = shape.Columns.Select( c => c.Name ).ToList();
         long rowNumber = 0;
         while( reader.Read() )
         {
            ct.ThrowIfCancellationRequested();
            var values = new string?[columns.Count];
            for( int i = 0; i < values.Length; i++ )
            {
               values[i] = OdbcValues.Read( reader, i, shape.Columns[i].SqlType );
            }

            yield return new SourceRecord( tableName, origin, ++rowNumber, columns, values, tableId );
         }

         finished = true;
      }
      finally
      {
         CloseReader( command, reader, finished );
      }
   }

   /// <summary>
   /// Closes a reader, cancelling its statement first when rows are left. A connection that is
   /// already dead may fail to cancel or close; that is ignored, since the table is rejected anyway.
   /// </summary>
   /// <param name="command">The statement.</param>
   /// <param name="reader">Its reader.</param>
   /// <param name="finished">True when every row was read.</param>
   private static void CloseReader( OdbcCommand command, OdbcDataReader reader, bool finished )
   {
      try
      {
         if( !finished )
         {
            command.Cancel();
         }

         reader.Dispose();
      }
      catch( Exception ex ) when( ex is OdbcException or InvalidOperationException )
      {
         // The connection is gone; nothing left to release on the server.
      }
   }

   /// <summary>
   /// The three-way check behind <see cref="ConfirmGoneAsync"/>: the listing worked and is not
   /// empty, the table is not in it (compared without case, so a case-only difference counts as
   /// present), and every way of reading the origin's table part fails with "not found" when
   /// queried. Any error along the way answers "not gone".
   /// </summary>
   /// <param name="origin">The origin.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True only when the table is confirmed gone.</returns>
   private bool IsGone( string origin, CancellationToken ct )
   {
      if( _connectFailure != null )
      {
         return false;
      }

      try
      {
         using OdbcConnection connection = Open();
         _listedOrigins ??= OdbcMetadata.ListTables( connection, withCounts: false, ct )
            .Select( t => Origin( _connectionName, new OdbcSelection( t.Schema, t.Name ) ) )
            .ToHashSet( StringComparer.OrdinalIgnoreCase );
         if( _listedOrigins.Count == 0 || _listedOrigins.Contains( origin ) )
         {
            return false;
         }

         string? quote = OdbcNaming.QuoteCharOf( connection );
         return OdbcNaming.SelectionsFromOrigin( origin[_originPrefix.Length..] ).All( t => QueryFindsNothing( connection, t, quote, ct ) );
      }
      catch( Exception ex ) when( ex is OdbcException or InvalidOperationException or ArgumentException )
      {
         return false;
      }
   }

   /// <summary>
   /// True when querying a table fails with SQLSTATE 42S02 (base table or view not found) about
   /// that table. A query that works, fails for any other reason (permission, network), or is
   /// "not found" about some other object (a table inside a view), is false.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="table">The table.</param>
   /// <param name="quote">The driver's quote character.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when the database says the table does not exist.</returns>
   private static bool QueryFindsNothing( OdbcConnection connection, OdbcSelection table, string? quote, CancellationToken ct )
   {
      ct.ThrowIfCancellationRequested();
      try
      {
         using var command = new OdbcCommand( $"SELECT 1 FROM {OdbcNaming.QualifiedName( table, quote )} WHERE 1=0", connection );
         using OdbcDataReader reader = command.ExecuteReader();
         return false;
      }
      catch( OdbcException ex )
      {
         return OdbcErrors.IsNotFound( ex, table );
      }
      catch( InvalidOperationException )
      {
         return false;
      }
   }

   #endregion Private Methods
}
