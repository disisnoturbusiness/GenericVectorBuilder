using DuckDB.NET.Data;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// One open collection of the DuckDB sink: its connection, the gate that serialises use of that
/// connection, the vector dimension (0 until the table exists) and whether deleted rows are
/// still waiting to be squeezed out of the HNSW index.
/// Why a gate: a DuckDB connection must not be used by two threads at once.
/// </summary>
internal sealed class DuckDbStore
{
   #region Constructor

   /// <summary>
   /// Wraps an open connection.
   /// </summary>
   /// <param name="connection">The open connection with the vss extension loaded.</param>
   public DuckDbStore( DuckDBConnection connection )
   {
      Connection = connection;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The open connection.</summary>
   public DuckDBConnection Connection { get; }

   /// <summary>Lets one operation at a time use the connection.</summary>
   public SemaphoreSlim Gate { get; } = new( 1, 1 );

   /// <summary>Vector length of the collection's table, or 0 when the table does not exist yet.</summary>
   public int Dimension { get; set; }

   /// <summary>
   /// True after rows were deleted or replaced. The HNSW index only marks such rows as deleted,
   /// so the next search compacts the index first and clears this flag.
   /// </summary>
   public bool NeedsCompaction { get; set; }

   #endregion Public Methods
}
