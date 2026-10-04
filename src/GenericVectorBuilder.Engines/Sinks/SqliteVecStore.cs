using Microsoft.Data.Sqlite;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// One open collection of the SqliteVec sink: its connection, the gate that serialises use of that
/// connection, and the vector dimension (0 until the table exists).
/// Why a gate: SQLite connections must not be used by two threads at once.
/// </summary>
internal sealed class SqliteVecStore
{
   #region Constructor

   /// <summary>
   /// Wraps an open connection.
   /// </summary>
   /// <param name="connection">The open connection with vec0 loaded.</param>
   public SqliteVecStore( SqliteConnection connection )
   {
      Connection = connection;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The open connection.</summary>
   public SqliteConnection Connection { get; }

   /// <summary>Lets one operation at a time use the connection.</summary>
   public SemaphoreSlim Gate { get; } = new( 1, 1 );

   /// <summary>Vector length of the collection's table, or 0 when the table does not exist yet.</summary>
   public int Dimension { get; set; }

   #endregion Public Methods
}
