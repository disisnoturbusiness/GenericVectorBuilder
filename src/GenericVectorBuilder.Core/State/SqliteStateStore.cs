using GenericVectorBuilder.Core.Contracts;
using Microsoft.Data.Sqlite;

namespace GenericVectorBuilder.Core.State;

/// <summary>
/// Run state in one local SQLite file: per pipeline and sink, which documents are written,
/// their content hashes, origins and chunk ids, plus each pipeline's embedder fingerprint.
/// Why SQLite and not the sinks themselves: the same question ("what does this sink hold?")
/// gets the same answer for every sink type, sinks can be added later without migrating
/// anything, and the file survives restarts so the next run is incremental.
/// Writes are serialized with a semaphore because several sinks commit in parallel and SQLite
/// allows one writer at a time.
/// </summary>
public sealed class SqliteStateStore : IStateStore
{
   #region Data Members

   private const char ID_SEPARATOR = ',';

   private readonly string _connectionString;
   private readonly SemaphoreSlim _writeLock = new( 1, 1 );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Opens (and if needed creates) the state database.
   /// </summary>
   /// <param name="path">Path of the SQLite file; its folder is created if missing.</param>
   public SqliteStateStore( string path )
   {
      Directory.CreateDirectory( Path.GetDirectoryName( Path.GetFullPath( path ) )! );
      _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
      using SqliteConnection connection = Open();
      Exec( connection, @"PRAGMA journal_mode = WAL;
CREATE TABLE IF NOT EXISTS doc_state ( pipeline TEXT NOT NULL, sink TEXT NOT NULL, doc_key TEXT NOT NULL,
   content_hash TEXT NOT NULL, origin TEXT NOT NULL, chunk_ids TEXT NOT NULL, PRIMARY KEY ( pipeline, sink, doc_key ) );
CREATE TABLE IF NOT EXISTS pipeline_meta ( pipeline TEXT NOT NULL PRIMARY KEY, fingerprint TEXT NOT NULL );" );
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public Task<Dictionary<string, DocState>> LoadAsync( string pipeline, string sink, CancellationToken ct )
   {
      var states = new Dictionary<string, DocState>( StringComparer.Ordinal );
      using SqliteConnection connection = Open();
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = "SELECT doc_key, content_hash, origin, chunk_ids FROM doc_state WHERE pipeline = $p AND sink = $s;";
      command.Parameters.AddWithValue( "$p", pipeline );
      command.Parameters.AddWithValue( "$s", sink );
      using SqliteDataReader reader = command.ExecuteReader();
      while( reader.Read() )
      {
         ct.ThrowIfCancellationRequested();
         string key = reader.GetString( 0 );
         states[key] = new DocState( key, reader.GetString( 1 ), reader.GetString( 2 ), ParseIds( reader.GetString( 3 ) ) );
      }

      return Task.FromResult( states );
   }

   /// <inheritdoc />
   public Task SaveAsync( string pipeline, string sink, IReadOnlyList<DocState> states, CancellationToken ct )
   {
      return WriteAsync( ( connection, transaction ) =>
      {
         using SqliteCommand command = connection.CreateCommand();
         command.Transaction = transaction;
         command.CommandText = "INSERT OR REPLACE INTO doc_state ( pipeline, sink, doc_key, content_hash, origin, chunk_ids ) VALUES ( $p, $s, $k, $h, $o, $c );";
         SqliteParameter key = command.Parameters.Add( "$k", SqliteType.Text );
         SqliteParameter hash = command.Parameters.Add( "$h", SqliteType.Text );
         SqliteParameter origin = command.Parameters.Add( "$o", SqliteType.Text );
         SqliteParameter ids = command.Parameters.Add( "$c", SqliteType.Text );
         command.Parameters.AddWithValue( "$p", pipeline );
         command.Parameters.AddWithValue( "$s", sink );
         foreach( DocState state in states )
         {
            key.Value = state.DocKey;
            hash.Value = state.ContentHash;
            origin.Value = state.Origin;
            ids.Value = string.Join( ID_SEPARATOR, state.ChunkIds );
            command.ExecuteNonQuery();
         }
      }, ct );
   }

   /// <inheritdoc />
   public Task RemoveAsync( string pipeline, string sink, IReadOnlyList<string> docKeys, CancellationToken ct )
   {
      return WriteAsync( ( connection, transaction ) =>
      {
         using SqliteCommand command = connection.CreateCommand();
         command.Transaction = transaction;
         command.CommandText = "DELETE FROM doc_state WHERE pipeline = $p AND sink = $s AND doc_key = $k;";
         command.Parameters.AddWithValue( "$p", pipeline );
         command.Parameters.AddWithValue( "$s", sink );
         SqliteParameter key = command.Parameters.Add( "$k", SqliteType.Text );
         foreach( string docKey in docKeys )
         {
            key.Value = docKey;
            command.ExecuteNonQuery();
         }
      }, ct );
   }

   /// <inheritdoc />
   public Task<string?> GetFingerprintAsync( string pipeline, CancellationToken ct )
   {
      using SqliteConnection connection = Open();
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = "SELECT fingerprint FROM pipeline_meta WHERE pipeline = $p;";
      command.Parameters.AddWithValue( "$p", pipeline );
      return Task.FromResult( command.ExecuteScalar() as string );
   }

   /// <inheritdoc />
   public Task SetFingerprintAsync( string pipeline, string fingerprint, CancellationToken ct )
   {
      return WriteAsync( ( connection, transaction ) =>
      {
         using SqliteCommand command = connection.CreateCommand();
         command.Transaction = transaction;
         command.CommandText = "INSERT OR REPLACE INTO pipeline_meta ( pipeline, fingerprint ) VALUES ( $p, $f );";
         command.Parameters.AddWithValue( "$p", pipeline );
         command.Parameters.AddWithValue( "$f", fingerprint );
         command.ExecuteNonQuery();
      }, ct );
   }

   /// <inheritdoc />
   public Task ForgetSinkAsync( string pipeline, string sink, CancellationToken ct )
   {
      return WriteAsync( ( connection, transaction ) =>
      {
         using SqliteCommand command = connection.CreateCommand();
         command.Transaction = transaction;
         command.CommandText = "DELETE FROM doc_state WHERE pipeline = $p AND sink = $s;";
         command.Parameters.AddWithValue( "$p", pipeline );
         command.Parameters.AddWithValue( "$s", sink );
         command.ExecuteNonQuery();
      }, ct );
   }

   /// <inheritdoc />
   public Task ForgetPipelineAsync( string pipeline, CancellationToken ct )
   {
      return WriteAsync( ( connection, transaction ) =>
      {
         using SqliteCommand command = connection.CreateCommand();
         command.Transaction = transaction;
         command.CommandText = "DELETE FROM doc_state WHERE pipeline = $p; DELETE FROM pipeline_meta WHERE pipeline = $p;";
         command.Parameters.AddWithValue( "$p", pipeline );
         command.ExecuteNonQuery();
      }, ct );
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<string>> ListPipelinesAsync( CancellationToken ct )
   {
      var names = new List<string>();
      using SqliteConnection connection = Open();
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = "SELECT pipeline FROM pipeline_meta ORDER BY pipeline;";
      using SqliteDataReader reader = command.ExecuteReader();
      while( reader.Read() )
      {
         names.Add( reader.GetString( 0 ) );
      }

      return Task.FromResult<IReadOnlyList<string>>( names );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Runs a write inside one transaction, one writer at a time.
   /// </summary>
   /// <param name="write">The write.</param>
   /// <param name="ct">Cancellation (checked before the write starts).</param>
   private async Task WriteAsync( Action<SqliteConnection, SqliteTransaction> write, CancellationToken ct )
   {
      await _writeLock.WaitAsync( ct );
      try
      {
         using SqliteConnection connection = Open();
         using SqliteTransaction transaction = connection.BeginTransaction();
         write( connection, transaction );
         transaction.Commit();
      }
      finally
      {
         _writeLock.Release();
      }
   }

   /// <summary>
   /// Opens a pooled connection with a busy timeout, so a reader never fails just because a
   /// write is in progress.
   /// </summary>
   /// <returns>The open connection.</returns>
   private SqliteConnection Open()
   {
      var connection = new SqliteConnection( _connectionString );
      connection.Open();
      Exec( connection, "PRAGMA busy_timeout = 10000;" );
      return connection;
   }

   /// <summary>
   /// Executes a statement with no result.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="sql">Statement.</param>
   private static void Exec( SqliteConnection connection, string sql )
   {
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = sql;
      command.ExecuteNonQuery();
   }

   /// <summary>
   /// Parses the stored comma-separated chunk id list.
   /// </summary>
   /// <param name="text">Stored text.</param>
   /// <returns>The ids.</returns>
   private static IReadOnlyList<Guid> ParseIds( string text )
   {
      return text.Length == 0 ? Array.Empty<Guid>() : text.Split( ID_SEPARATOR ).Select( Guid.Parse ).ToArray();
   }

   #endregion Private Methods
}
