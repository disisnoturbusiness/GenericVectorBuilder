using System.Data;
using Microsoft.Data.SqlClient;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// Reads what a crash can lose on the SQL Server the benchmark writes to, from the server's own
/// settings: the recovery model and delayed-durability setting of the model database (which every
/// database created here copies) and of any benchmark database that exists, the trace flags
/// enabled globally, and mssql.conf.
/// Why it is printed with the numbers: an engine that skips fsync writes faster, and a reader
/// comparing engines must see which ones do.
/// </summary>
public static class SqlDurability
{
   #region Data Members

   /// <summary>Where SQL Server for Linux keeps its settings file.</summary>
   public const string MSSQL_CONF = "/var/opt/mssql/mssql.conf";

   private const int QUERY_LIMIT_SECONDS = 60;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Reads the facts and describes them. A problem reading the server becomes words in the text,
   /// never an exception, so a missing fact leaves a visible gap and the benchmark still runs.
   /// </summary>
   /// <param name="serverConnectionString">Connection string without a database.</param>
   /// <param name="benchDatabases">Benchmark databases to report on when they exist.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The sentence for the report.</returns>
   public static async Task<string> ReadAsync( string serverConnectionString, IReadOnlyList<string> benchDatabases, CancellationToken ct )
   {
      ParsedConfig? conf = await ConfigFiles.ReadWithSudoFallbackAsync( MSSQL_CONF, ct ) is string text ? ConfigFiles.ParseIni( text ) : null;
      try
      {
         await using var connection = new SqlConnection( serverConnectionString );
         await connection.OpenAsync( ct );
         IReadOnlyList<DatabaseDurability> databases = await ReadDatabasesAsync( connection, benchDatabases, ct );
         IReadOnlyList<int> flags = await ReadTraceFlagsAsync( connection, ct );
         return Describe( databases, flags, conf );
      }
      catch( SqlException ex )
      {
         return $"not read: the server's durability settings could not be queried ({ex.Message}); {DescribeConf( conf )}";
      }
   }

   /// <summary>
   /// Describes what a crash can lose from facts already read. Pure, so the wording is tested
   /// without a server.
   /// </summary>
   /// <param name="databases">The model database first, then any benchmark databases.</param>
   /// <param name="traceFlags">Trace flags enabled globally.</param>
   /// <param name="conf">mssql.conf parsed, or null when it could not be read.</param>
   /// <returns>The sentence for the report.</returns>
   public static string Describe( IReadOnlyList<DatabaseDurability> databases, IReadOnlyList<int> traceFlags, ParsedConfig? conf )
   {
      DatabaseDurability? model = databases.FirstOrDefault( d => d.Name == "model" );
      string[] delayed = databases.Where( d => d.DelayedDurability != "DISABLED" ).Select( d => $"{d.Name} {d.DelayedDurability}" ).ToArray();
      string commit = delayed.Length == 0
         ? "A commit returns after its transaction-log records are written to disk (SQL Server write-ahead logging; delayed durability is DISABLED)"
         : $"Delayed durability is on ({string.Join( ", ", delayed )}), so the last commits can be lost in a crash";
      string inherited = model == null ? "the model database could not be read"
         : $"a database created here copies the model database: recovery model {model.Recovery}, page_verify {model.PageVerify}";
      string existing = databases.Count( d => d.Name != "model" ) == 0 ? string.Empty
         : $"; existing benchmark databases: {string.Join( ", ", databases.Where( d => d.Name != "model" ).Select( d => $"{d.Name} {d.Recovery}/{d.DelayedDurability}/{d.PageVerify}" ) )}";
      string flags = traceFlags.Count == 0 ? "no global trace flags are enabled" : $"global trace flags enabled: {string.Join( ", ", traceFlags )}";
      return $"{commit}; {inherited}{existing}; {flags}; {DescribeConf( conf )}. Not tested by cutting power; whether the disk's own write cache reaches the media was not checked.";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Describes mssql.conf, and says whether it changes how writes are flushed.
   /// </summary>
   /// <param name="conf">mssql.conf parsed, or null when it could not be read.</param>
   /// <returns>The sentence part.</returns>
   private static string DescribeConf( ParsedConfig? conf )
   {
      if( conf == null )
      {
         return $"{MSSQL_CONF} could not be read (this account cannot open it and sudo -n cat failed), so its settings are unknown";
      }

      bool flushTuned = conf.Values.Keys.Any( k => k.StartsWith( "control.", StringComparison.OrdinalIgnoreCase ) || k.StartsWith( "traceflag.", StringComparison.OrdinalIgnoreCase ) );
      string skipped = conf.SkippedLines > 0 ? $" ({conf.SkippedLines} line(s) the reader did not understand)" : string.Empty;
      return $"{MSSQL_CONF} sets: {ConfigFiles.List( conf )}{skipped}; "
         + ( flushTuned ? "it has [control] or [traceflag] entries, which change how writes are flushed"
            : "it has no [control] or [traceflag] entry, so SQL Server's own Linux defaults for flushing writes apply (Microsoft's Linux performance guide names trace flag 3982 as that default; read from the guide, not tested here)" );
   }

   /// <summary>
   /// Reads the recovery model, delayed durability and page verify option of the model database
   /// and of each named database that exists.
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="benchDatabases">Benchmark database names.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The model database first, then the others by name.</returns>
   private static async Task<IReadOnlyList<DatabaseDurability>> ReadDatabasesAsync( SqlConnection connection, IReadOnlyList<string> benchDatabases, CancellationToken ct )
   {
      string names = string.Join( ", ", benchDatabases.Select( ( _, i ) => $"@d{i}" ).Prepend( "N'model'" ) );
      await using var command = new SqlCommand( $"SELECT name, recovery_model_desc, delayed_durability_desc, page_verify_option_desc FROM sys.databases WHERE name IN ( {names} ) ORDER BY CASE WHEN name = N'model' THEN 0 ELSE 1 END, name;", connection )
      {
         CommandTimeout = QUERY_LIMIT_SECONDS,
      };
      for( int i = 0; i < benchDatabases.Count; i++ )
      {
         command.Parameters.Add( $"@d{i}", SqlDbType.NVarChar, 128 ).Value = benchDatabases[i];
      }

      var rows = new List<DatabaseDurability>();
      await using SqlDataReader reader = await command.ExecuteReaderAsync( ct );
      while( await reader.ReadAsync( ct ) )
      {
         rows.Add( new DatabaseDurability( reader.GetString( 0 ), reader.GetString( 1 ), reader.GetString( 2 ), reader.GetString( 3 ) ) );
      }

      return rows;
   }

   /// <summary>
   /// Reads the trace flags enabled globally (DBCC TRACESTATUS(-1)).
   /// </summary>
   /// <param name="connection">Open connection.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The flag numbers, empty when none is enabled.</returns>
   private static async Task<IReadOnlyList<int>> ReadTraceFlagsAsync( SqlConnection connection, CancellationToken ct )
   {
      var flags = new List<int>();
      await using var command = new SqlCommand( "DBCC TRACESTATUS( -1 ) WITH NO_INFOMSGS;", connection ) { CommandTimeout = QUERY_LIMIT_SECONDS };
      await using SqlDataReader reader = await command.ExecuteReaderAsync( ct );
      while( await reader.ReadAsync( ct ) )
      {
         if( Convert.ToInt32( reader.GetValue( 1 ) ) == 1 && Convert.ToInt32( reader.GetValue( 2 ) ) == 1 )
         {
            flags.Add( Convert.ToInt32( reader.GetValue( 0 ) ) );
         }
      }

      return flags;
   }

   #endregion Private Methods
}

/// <summary>
/// The durability-relevant settings of one database.
/// </summary>
/// <param name="Name">Database name.</param>
/// <param name="Recovery">Recovery model: FULL, SIMPLE or BULK_LOGGED.</param>
/// <param name="DelayedDurability">DISABLED, ALLOWED or FORCED.</param>
/// <param name="PageVerify">CHECKSUM, TORN_PAGE_DETECTION or NONE.</param>
public sealed record DatabaseDurability( string Name, string Recovery, string DelayedDurability, string PageVerify );
