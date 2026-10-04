using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// Reads memory and disk use from the places each kind of host reports them.
/// Every method returns a <see cref="Measurement"/> instead of throwing: a missing number
/// should leave a gap in the report, not stop a benchmark that took an hour to load.
/// </summary>
public static class ResourceProbe
{
   #region Data Members

   private static readonly TimeSpan COMMAND_TIMEOUT = TimeSpan.FromMinutes( 5 );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Memory of every container a compose file runs, summed, from "docker stats".
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The reading.</returns>
   public static async Task<Measurement> DockerRamAsync( string composePath, CancellationToken ct )
   {
      string? ids = await Shell.TryOutputAsync( "sudo", new[] { "docker", "compose", "-f", composePath, "ps", "-q" }, ct );
      string[] containers = ( ids ?? string.Empty ).Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
      if( containers.Length == 0 )
      {
         return Measurement.None( "container not running" );
      }

      string? usage = await Shell.TryOutputAsync( "sudo", new[] { "docker", "stats", "--no-stream", "--format", "{{.MemUsage}}" }.Concat( containers ), ct );
      long total = 0;
      foreach( string line in ( usage ?? string.Empty ).Split( '\n', StringSplitOptions.RemoveEmptyEntries ) )
      {
         total += ParseDockerSize( line.Split( '/' )[0] ) ?? 0;
      }

      return total > 0 ? new Measurement( total, $"{Measurement.Format( total )} (docker stats)" ) : Measurement.None( "docker stats gave nothing" );
   }

   /// <summary>
   /// Total size of a folder, read with "sudo du" because engine data folders are owned by the
   /// container's user.
   /// </summary>
   /// <param name="path">Folder.</param>
   /// <param name="scope">What the folder holds, for the report.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The reading.</returns>
   public static async Task<Measurement> FolderSizeAsync( string path, string scope, CancellationToken ct )
   {
      if( !Directory.Exists( path ) )
      {
         return Measurement.None( $"no folder {path}" );
      }

      string? output = await Shell.TryOutputAsync( "sudo", new[] { "du", "-sb", path }, ct );
      return long.TryParse( output?.Split( '\t', ' ' )[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes )
         ? new Measurement( bytes, $"{Measurement.Format( bytes )} ({scope})" )
         : Measurement.None( $"could not size {path}" );
   }

   /// <summary>
   /// Resident memory of a local process found by its exact name (for Qdrant, which runs
   /// under systemd and serves every collection, so the number is the whole server's).
   /// </summary>
   /// <param name="processName">Process name, e.g. "qdrant".</param>
   /// <param name="scope">What the number covers.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The reading.</returns>
   public static async Task<Measurement> ProcessRssAsync( string processName, string scope, CancellationToken ct )
   {
      string? pid = ( await Shell.TryOutputAsync( "pgrep", new[] { "-x", processName }, ct ) )?.Split( '\n' )[0];
      string status = pid == null ? string.Empty : $"/proc/{pid}/status";
      string? line = File.Exists( status ) ? ( await File.ReadAllLinesAsync( status, ct ) ).FirstOrDefault( l => l.StartsWith( "VmRSS:", StringComparison.Ordinal ) ) : null;
      if( line == null || !long.TryParse( line.Split( ' ', StringSplitOptions.RemoveEmptyEntries )[1], out long kb ) )
      {
         return Measurement.None( $"{processName} process not found" );
      }

      return new Measurement( kb * 1024, $"{Measurement.Format( kb * 1024 )} ({scope})" );
   }

   /// <summary>
   /// Memory SQL Server is using, from sys.dm_os_process_memory. It is the whole server (every
   /// database shares one buffer pool), so it says how big the process is, not this table.
   /// </summary>
   /// <param name="serverConnectionString">Connection string.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The reading.</returns>
   public static async Task<Measurement> SqlServerRamAsync( string serverConnectionString, CancellationToken ct )
   {
      long? kb = await SqlScalarAsync( serverConnectionString, "SELECT physical_memory_in_use_kb FROM sys.dm_os_process_memory;", null, ct );
      return kb.HasValue ? new Measurement( kb.Value * 1024, $"{Measurement.Format( kb.Value * 1024 )} (whole SQL Server process)" ) : Measurement.None( "could not read SQL Server memory" );
   }

   /// <summary>
   /// Pages reserved by one SQL table plus its internal tables (where a vector index lives).
   /// </summary>
   /// <param name="serverConnectionString">Connection string without a database.</param>
   /// <param name="database">Database holding the table.</param>
   /// <param name="table">Table name without schema.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The reading.</returns>
   public static async Task<Measurement> SqlTableSizeAsync( string serverConnectionString, string database, string table, CancellationToken ct )
   {
      const string SQL = @"SELECT SUM( ps.reserved_page_count ) * 8192 FROM sys.dm_db_partition_stats ps
WHERE ps.object_id = OBJECT_ID( @t ) OR ps.object_id IN ( SELECT it.object_id FROM sys.internal_tables it WHERE it.parent_object_id = OBJECT_ID( @t ) );";
      string connection = new SqlConnectionStringBuilder( serverConnectionString ) { InitialCatalog = database }.ConnectionString;
      long? bytes = await SqlScalarAsync( connection, SQL, $"dbo.[{table}]", ct );
      return bytes.HasValue ? new Measurement( bytes.Value, $"{Measurement.Format( bytes.Value )} (table and its indexes, reserved pages)" ) : Measurement.None( "table not found" );
   }

   /// <summary>
   /// Parses a docker size such as "1.5GiB", "512MiB" or "800kB".
   /// </summary>
   /// <param name="text">The size text.</param>
   /// <returns>Bytes, or null when unreadable.</returns>
   public static long? ParseDockerSize( string text )
   {
      string value = text.Trim();
      int split = 0;
      while( split < value.Length && ( char.IsDigit( value[split] ) || value[split] == '.' ) )
      {
         split++;
      }

      if( !double.TryParse( value[..split], NumberStyles.Float, CultureInfo.InvariantCulture, out double number ) )
      {
         return null;
      }

      double factor = value[split..].Trim() switch
      {
         "B" => 1, "KiB" => 1024, "MiB" => 1024 * 1024, "GiB" => 1024L * 1024 * 1024, "TiB" => 1024L * 1024 * 1024 * 1024,
         "kB" or "KB" => 1e3, "MB" => 1e6, "GB" => 1e9, "TB" => 1e12, _ => double.NaN,
      };
      return double.IsNaN( factor ) ? null : (long)( number * factor );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Runs a scalar query, returning null on any failure or a NULL result.
   /// </summary>
   /// <param name="connectionString">Connection string.</param>
   /// <param name="sql">Query; may use @t.</param>
   /// <param name="table">Value for @t, or null.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The value, or null.</returns>
   private static async Task<long?> SqlScalarAsync( string connectionString, string sql, string? table, CancellationToken ct )
   {
      try
      {
         await using var connection = new SqlConnection( connectionString );
         await connection.OpenAsync( ct );
         await using var command = new SqlCommand( sql, connection ) { CommandTimeout = (int)COMMAND_TIMEOUT.TotalSeconds };
         if( table != null )
         {
            command.Parameters.Add( "@t", SqlDbType.NVarChar, 300 ).Value = table;
         }

         object? value = await command.ExecuteScalarAsync( ct );
         return value is null or DBNull ? null : Convert.ToInt64( value, CultureInfo.InvariantCulture );
      }
      catch( SqlException )
      {
         return null;
      }
   }

   #endregion Private Methods
}
