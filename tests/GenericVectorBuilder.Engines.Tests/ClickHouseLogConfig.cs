using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// What the ClickHouse log tests share: where the compose file and the system log config live,
/// which log tables the 26.3.39.7 image switches on by default, and how to read the TTL out of
/// the config file.
/// Why one place: the static tests (no container) and the live tests (real server) must agree on
/// the table list and on how the TTL text is read, or one could pass while the other fails for a
/// reason that has nothing to do with the config.
/// </summary>
internal static class ClickHouseLogConfig
{
   #region Data Members

   /// <summary>Folder of the compose file's config mounts.</summary>
   public const string CONFIG_FOLDER = "clickhouse-config";

   /// <summary>The system log config file.</summary>
   public const string CONFIG_FILE = "gvb-system-logs.xml";

   /// <summary>Where the compose file mounts the config inside the container.</summary>
   public const string CONFIG_MOUNT = "/etc/clickhouse-server/config.d/" + CONFIG_FILE;

   /// <summary>The one table whose config element carries an engine (and so cannot carry a ttl element).</summary>
   public const string SPAN_LOG = "opentelemetry_span_log";

   /// <summary>The cleanup script that drops the renamed copies ClickHouse leaves when a TTL changes.</summary>
   public const string CLEAR_SCRIPT = "clear-system-logs.sh";

   /// <summary>Longest TTL the config may carry: a day. A longer one is not a bound on a benchmark's disk use.</summary>
   public static readonly TimeSpan MAX_TTL = TimeSpan.FromDays( 1 );

   /// <summary>
   /// Every log table element the 26.3.39.7 image's own config.xml switches on (read from
   /// /etc/clickhouse-server/config.xml inside the image on 2026-10-05). The live tests compare
   /// this list with the image, so a newer image that adds a table fails there.
   /// </summary>
   public static readonly string[] DEFAULT_ON_LOGS =
   {
      "query_log", "trace_log", "query_thread_log", "query_views_log", "part_log", "background_schedule_pool_log",
      "text_log", "metric_log", "error_log", "instrumentation_trace_log", "query_metric_log", "asynchronous_metric_log",
      "iceberg_metadata_log", "delta_lake_metadata_log", SPAN_LOG, "crash_log", "processors_profile_log",
      "asynchronous_insert_log", "backup_log", "s3queue_log", "blob_storage_log", "aggregated_zookeeper_log",
      "zookeeper_connection_log"
   };

   /// <summary>
   /// Log elements the image config enables but the 26.3.39.7 server never creates a table for
   /// (measured 2026-10-05: system.tables lists 22 of the 23, instrumentation_trace_log is absent).
   /// </summary>
   public static readonly string[] NOT_CREATED = { "instrumentation_trace_log" };

   private static readonly Regex TTL_PATTERN = new( @"^(?<column>event_time|finish_date)\s*\+\s*INTERVAL\s+(?<count>\d+)\s+(?<unit>MINUTE|HOUR|DAY)\s+DELETE$", RegexOptions.IgnoreCase | RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Reads the config file and returns its clickhouse root element.
   /// </summary>
   /// <returns>The root element.</returns>
   public static XElement Load()
   {
      XElement root = XDocument.Load( ConfigPath() ).Root ?? throw new InvalidOperationException( "the config file has no root element" );
      Assert.Equal( "clickhouse", root.Name.LocalName );
      return root;
   }

   /// <summary>
   /// The TTL clause the config gives one table, as written in the file: the ttl element, or for
   /// the span log the clause after "ttl" in its engine text.
   /// </summary>
   /// <param name="root">The config root.</param>
   /// <param name="table">Table (element) name.</param>
   /// <returns>The expression, e.g. "event_time + INTERVAL 1 HOUR DELETE".</returns>
   public static string TtlClause( XElement root, string table )
   {
      XElement element = root.Element( table ) ?? throw new InvalidOperationException( $"{table} is not in {CONFIG_FILE}" );
      if( table != SPAN_LOG )
      {
         return ( element.Element( "ttl" )?.Value ?? string.Empty ).Trim();
      }

      string engine = Regex.Replace( element.Element( "engine" )?.Value ?? string.Empty, @"\s+", " " ).Trim();
      Match clause = Regex.Match( engine, @"\bttl (?<clause>.+?)( settings .*)?$", RegexOptions.IgnoreCase );
      return clause.Success ? clause.Groups["clause"].Value.Trim() : string.Empty;
   }

   /// <summary>
   /// Splits a TTL clause into its parts.
   /// </summary>
   /// <param name="clause">The clause, e.g. "event_time + INTERVAL 1 HOUR DELETE".</param>
   /// <returns>The column, the count, the unit (upper case) and the length of time.</returns>
   public static ( string Column, int Count, string Unit, TimeSpan Length ) ParseTtl( string clause )
   {
      Match match = TTL_PATTERN.Match( clause.Trim() );
      Assert.True( match.Success, $"TTL is not '<column> + INTERVAL n MINUTE|HOUR|DAY DELETE': '{clause}'" );
      int count = int.Parse( match.Groups["count"].Value );
      string unit = match.Groups["unit"].Value.ToUpperInvariant();
      TimeSpan length = unit switch
      {
         "MINUTE" => TimeSpan.FromMinutes( count ),
         "HOUR" => TimeSpan.FromHours( count ),
         _ => TimeSpan.FromDays( count ),
      };
      return ( match.Groups["column"].Value.ToLowerInvariant(), count, unit, length );
   }

   /// <summary>
   /// The TTL as the sink's engine text writes it: "1 hour", "30 minutes", "1 day".
   /// </summary>
   /// <param name="clause">The clause from the config.</param>
   /// <returns>Count and unit in words.</returns>
   public static string TtlWords( string clause )
   {
      ( _, int count, string unit, _ ) = ParseTtl( clause );
      return $"{count} {unit.ToLowerInvariant()}{( count == 1 ? string.Empty : "s" )}";
   }

   /// <summary>
   /// The TTL as ClickHouse prints it in system.tables.engine_full: "event_time + toIntervalHour(1)".
   /// </summary>
   /// <param name="clause">The clause from the config.</param>
   /// <returns>The expression in the server's spelling.</returns>
   public static string TtlServerForm( string clause )
   {
      ( string column, int count, string unit, _ ) = ParseTtl( clause );
      string interval = unit switch { "MINUTE" => "toIntervalMinute", "HOUR" => "toIntervalHour", _ => "toIntervalDay" };
      return $"{column} + {interval}({count})";
   }

   /// <summary>
   /// Path of the compose file under test.
   /// </summary>
   /// <returns>The full path.</returns>
   public static string ComposePath()
   {
      return Path.Combine( RepoRoot(), "deploy", "engines", "clickhouse.compose.yaml" );
   }

   /// <summary>
   /// Path of the system log config file under test.
   /// </summary>
   /// <returns>The full path.</returns>
   public static string ConfigPath()
   {
      return Path.Combine( RepoRoot(), "deploy", "engines", CONFIG_FOLDER, CONFIG_FILE );
   }

   /// <summary>
   /// Path of the cleanup script under test.
   /// </summary>
   /// <returns>The full path.</returns>
   public static string ScriptPath()
   {
      return Path.Combine( RepoRoot(), "deploy", "engines", CONFIG_FOLDER, CLEAR_SCRIPT );
   }

   /// <summary>
   /// Finds the repository root by walking up from the test binary until the solution file appears.
   /// </summary>
   /// <returns>The folder holding GenericVectorBuilder.slnx.</returns>
   public static string RepoRoot()
   {
      string? folder = AppContext.BaseDirectory;
      while( folder != null && !File.Exists( Path.Combine( folder, "GenericVectorBuilder.slnx" ) ) )
      {
         folder = Path.GetDirectoryName( folder );
      }

      return folder ?? throw new InvalidOperationException( "GenericVectorBuilder.slnx was not found above the test binary" );
   }

   #endregion Public Methods
}
