using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Sinks;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Proves against the real container (deploy/engines/clickhouse.compose.yaml must be up with its
/// clickhouse-config mount, HTTP port 8123) that ClickHouse runs with its image-default system
/// logging plus a TTL on every log table, that the logs really are written while searches run, and
/// that nothing but those TTL-governed logs grows the data folder across benchmark-style cycles.
/// Why: the v6 benchmark ran with 21 log tables off, which made the engine faster than its default
/// and was not disclosed. Each test reads the evidence itself, from the server's own system tables
/// and config files and from du over the data folder, and does not trust the config file's text.
/// Why du through docker exec: the data folder belongs to the container's clickhouse user and the
/// test runs as an ordinary user, so the container's own view of /var/lib/clickhouse is the one
/// readable route that needs no sudo.
/// What is deliberately NOT asserted: that the whole data folder stays within a fixed size across
/// three cycles. At the image's default logging a cycle of 4,000 searches writes about 120 to 220 MB
/// of log rows, and a TTL removes rows only once they are older than the TTL (1 hour) and their
/// files only after old_parts_lifetime (480 s), so no TTL bounds a two-minute test. The test prints
/// the whole-folder figures anyway, next to the log tables' own bytes.
/// </summary>
[Trait( "Category", "Live" )]
public sealed class ClickHouseLogHygieneLiveTests : IDisposable
{
   #region Data Members

   private const string CONTAINER = "gvb-clickhouse";
   private const string DATA_FOLDER = "/var/lib/clickhouse";
   private const string IMAGE_CONFIG = "/etc/clickhouse-server/config.xml";
   private const string MERGED_CONFIG = DATA_FOLDER + "/preprocessed_configs/config.xml";
   private const int DIMENSION = 1024;
   private const int COUNT = 2000;
   private const int CYCLES = 3;
   private const int SEARCHERS = 8;
   private const int SEARCHES_PER_SEARCHER = 500;
   private const int SHORT_SEARCHES_PER_SEARCHER = 125;
   private const int EXACT_SEARCHES = 100;
   private const int TOP = 10;
   private const int MIN_CREATED_LOG_TABLES = 22;
   private const long NON_LOG_LIMIT_BYTES = 100_000_000;
   private const double MIN_TOP_ONE_RATE = 0.9;
   private static readonly TimeSpan CYCLE_LIMIT = TimeSpan.FromMinutes( 10 );
   private static readonly TimeSpan PROCESS_LIMIT = TimeSpan.FromSeconds( 60 );

   /// <summary>Log tables that must gain rows while searches run (the ones a search or a load itself feeds).</summary>
   private static readonly string[] WRITTEN_BY_SEARCHES = { "query_log", "text_log", "processors_profile_log", "part_log", "metric_log", "asynchronous_metric_log" };

   private readonly ITestOutputHelper _output;
   private readonly HttpClient _http;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Receives the xunit output writer and opens a raw HTTP client for the test's own queries.
   /// </summary>
   /// <param name="output">Where sizes and the engine's evidence are printed.</param>
   public ClickHouseLogHygieneLiveTests( ITestOutputHelper output )
   {
      _output = output;
      ClickHouseSinkOptions options = ClickHouseSinkOptions.LocalDefaults();
      _http = new HttpClient { BaseAddress = new Uri( $"http://{options.Host}:{options.Port}/" ), Timeout = TimeSpan.FromSeconds( 60 ) };
      _http.DefaultRequestHeaders.Add( "X-ClickHouse-User", options.User );
      _http.DefaultRequestHeaders.Add( "X-ClickHouse-Key", options.Password ?? string.Empty );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Every log table the image switches on exists and runs with the TTL from the config, and no
   /// renamed copy of an old table (name_0) is left over. The list of tables is checked against the
   /// image's own config.xml, so a newer image that adds a table fails here.
   /// </summary>
   [Fact]
   public async Task EveryDefaultOnLogTable_Exists_WithTheConfiguredTtl()
   {
      XElement image = await ReadXmlAsync( IMAGE_CONFIG );
      string[] enabled = image.Elements().Select( e => e.Name.LocalName ).Where( n => n.EndsWith( "_log", StringComparison.Ordinal ) ).OrderBy( n => n, StringComparer.Ordinal ).ToArray();
      Assert.Equal( ClickHouseLogConfig.DEFAULT_ON_LOGS.OrderBy( n => n, StringComparer.Ordinal ).ToArray(), enabled );

      Dictionary<string, string> engines = await EnginesAsync();
      string[] copies = engines.Keys.Where( n => Regex.IsMatch( n, @"_\d+$" ) ).ToArray();
      Assert.True( copies.Length == 0, $"renamed copies of old log tables are left over: {string.Join( ", ", copies )}; run deploy/engines/clickhouse-config/{ClickHouseLogConfig.CLEAR_SCRIPT}" );

      XElement config = ClickHouseLogConfig.Load();
      int created = 0;
      foreach( string name in ClickHouseLogConfig.DEFAULT_ON_LOGS )
      {
         if( !engines.TryGetValue( name, out string? engine ) )
         {
            Assert.Contains( name, ClickHouseLogConfig.NOT_CREATED );
            _output.WriteLine( $"{name}: not created by this server (known)" );
            continue;
         }

         created++;
         string expected = "TTL " + ClickHouseLogConfig.TtlServerForm( ClickHouseLogConfig.TtlClause( config, name ) );
         _output.WriteLine( $"{name}: {engine}" );
         Assert.True( engine.Contains( expected, StringComparison.Ordinal ), $"{name} does not run with '{expected}': {engine}" );
      }

      Assert.True( created >= MIN_CREATED_LOG_TABLES, $"only {created} of the default-on log tables exist" );
   }

   /// <summary>
   /// The server's merged config differs from the image's own config.xml only in the TTL: for every
   /// log table every other child (flush interval, buffer sizes, partition key, level and so on) is
   /// the same, no table is removed, and the logger element is untouched.
   /// Why compare the two files and not trust the config: this is the proof that the logging is at
   /// the image defaults, and the merged file is what the server really started with.
   /// </summary>
   [Fact]
   public async Task MergedConfig_DiffersFromTheImageConfig_OnlyInTheTtl()
   {
      XElement image = await ReadXmlAsync( IMAGE_CONFIG );
      XElement merged = await ReadXmlAsync( MERGED_CONFIG );
      foreach( string name in ClickHouseLogConfig.DEFAULT_ON_LOGS )
      {
         XElement fromImage = image.Element( name ) ?? throw new InvalidOperationException( $"{name} is not in the image config" );
         XElement fromMerged = merged.Element( name ) ?? throw new InvalidOperationException( $"{name} is not in the merged config" );
         Assert.Null( fromMerged.Attribute( "remove" ) );
         SortedDictionary<string, string> expected = Settings( fromImage, name );
         SortedDictionary<string, string> actual = Settings( fromMerged, name );
         Assert.True( expected.SequenceEqual( actual ), $"{name}: the merged config differs from the image's beyond the TTL.{Environment.NewLine}image:  {Describe( expected )}{Environment.NewLine}merged: {Describe( actual )}" );
         _output.WriteLine( $"{name}: flush_interval_milliseconds {actual.GetValueOrDefault( "flush_interval_milliseconds", "(none)" )}, partition_by {actual.GetValueOrDefault( "partition_by", "(none)" )}" );
      }

      Assert.True( Settings( image.Element( "logger" )!, "logger" ).SequenceEqual( Settings( merged.Element( "logger" )!, "logger" ) ), "the server's logger element differs from the image's" );
   }

   /// <summary>
   /// After one load and search cycle the log tables really have gained rows: every table a search
   /// or an insert feeds, counted by event_time since the test began (so a TTL dropping old parts
   /// during the test cannot make a count go down). All tables are printed, including the ones that
   /// stay empty in this workload (backup_log, s3queue_log and so on).
   /// </summary>
   [Fact]
   public async Task LogTables_AreWritten_WhileSearchesRun()
   {
      string started = await RawAsync( "SELECT toString( now() - 2 )" );
      await RunCycleAsync( 1, SHORT_SEARCHES_PER_SEARCHER );
      await RawAsync( "SYSTEM FLUSH LOGS" );
      int searches = SEARCHERS * SHORT_SEARCHES_PER_SEARCHER + EXACT_SEARCHES;
      Dictionary<string, string> engines = await EnginesAsync();
      var written = new Dictionary<string, long>();
      foreach( string name in ClickHouseLogConfig.DEFAULT_ON_LOGS.Where( engines.ContainsKey ) )
      {
         string since = name == ClickHouseLogConfig.SPAN_LOG ? $"finish_date >= toDate( '{started}' )" : $"event_time >= toDateTime( '{started}' )";
         written[name] = long.Parse( await RawAsync( $"SELECT count() FROM system.{name} WHERE {since}" ), CultureInfo.InvariantCulture );
         _output.WriteLine( $"{name,-28} {written[name],10:N0} rows since {started}" );
      }

      long finished = long.Parse( await RawAsync( $"SELECT count() FROM system.query_log WHERE event_time >= toDateTime( '{started}' ) AND type = 'QueryFinish' AND query LIKE '%cosineDistance%'" ), CultureInfo.InvariantCulture );
      _output.WriteLine( $"query_log QueryFinish rows of the vector searches: {finished:N0} for {searches:N0} searches sent" );
      Assert.True( finished >= searches, $"query_log holds {finished} finished vector searches, {searches} were sent" );
      Assert.All( WRITTEN_BY_SEARCHES, name => Assert.True( written.GetValueOrDefault( name ) > 0, $"system.{name} gained no rows while searches ran" ) );
      Assert.True( written["text_log"] >= searches, $"system.text_log gained {written["text_log"]} rows for {searches} searches" );
   }

   /// <summary>
   /// Runs three load/search cycles of 2,000 vectors each (load, FinishLoad, 4,000 searches from 8
   /// searchers, 100 exact searches, drop) and checks two things after each, once the logs are
   /// flushed. First, nothing but the log tables grows the data folder: the folder minus the log
   /// tables' own bytes (active and not yet deleted old parts) stays within 100 MB of where it
   /// began. Second, every log part written during the test carries an expiry: its
   /// delete_ttl_info_max is set and no later than now plus the TTL, so the growth is bounded in
   /// time and no written part escapes the TTL. The whole-folder figures are printed.
   /// </summary>
   [Fact]
   public async Task ThreeCycles_OnlyTheTtlGovernedLogsGrow_AndEveryLogPartExpires()
   {
      string started = await RawAsync( "SELECT toString( now() - 2 )" );
      await RawAsync( "SYSTEM FLUSH LOGS" );
      long folderBefore = await FolderBytesAsync();
      long logsBefore = await LogBytesAsync();
      var nonLog = new List<long> { folderBefore - logsBefore };
      _output.WriteLine( $"before: folder {folderBefore:N0} bytes, log tables {logsBefore:N0}, the rest {nonLog[0]:N0}" );
      for( int cycle = 1; cycle <= CYCLES; cycle++ )
      {
         await RunCycleAsync( cycle, SEARCHES_PER_SEARCHER );
         await RawAsync( "SYSTEM FLUSH LOGS" );
         long folder = await FolderBytesAsync();
         long logs = await LogBytesAsync();
         nonLog.Add( folder - logs );
         _output.WriteLine( $"after cycle {cycle}: folder {folder:N0} bytes ({folder - folderBefore:+#,0;-#,0;0} against the start), log tables {logs:N0} ({logs - logsBefore:+#,0;-#,0;0}), the rest {nonLog[^1]:N0} ({nonLog[^1] - nonLog[0]:+#,0;-#,0;0})" );
      }

      long spread = nonLog.Max() - nonLog.Min();
      _output.WriteLine( $"the folder minus the log tables moved {spread:N0} bytes across the {nonLog.Count} readings, limit {NON_LOG_LIMIT_BYTES:N0}" );
      await AssertEveryNewLogPartExpiresAsync( started );
      Assert.True( nonLog[0] > 0, "du read a data folder smaller than the log tables" );
      Assert.True( spread <= NON_LOG_LIMIT_BYTES, $"the data folder outside the log tables moved {spread:N0} bytes across the cycles: {string.Join( ", ", nonLog )}" );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Every active log part written since <paramref name="started"/> has a delete TTL that expires no
   /// later than the TTL after now.
   /// </summary>
   /// <param name="started">Server time the test began, as text.</param>
   private async Task AssertEveryNewLogPartExpiresAsync( string started )
   {
      TimeSpan ttl = ClickHouseLogConfig.ParseTtl( ClickHouseLogConfig.TtlClause( ClickHouseLogConfig.Load(), "query_log" ) ).Length;
      string names = string.Join( ", ", ClickHouseLogConfig.DEFAULT_ON_LOGS.Select( n => $"'{n}'" ) );
      string where = $"database = 'system' AND active AND rows > 0 AND table IN ({names}) AND modification_time >= toDateTime( '{started}' )";
      string unset = await RawAsync( $"SELECT groupArray( table ) FROM system.parts WHERE {where} AND delete_ttl_info_max = toDateTime( 0 )" );
      string late = await RawAsync( $"SELECT groupArray( table ) FROM system.parts WHERE {where} AND delete_ttl_info_max > now() + {( (int)ttl.TotalSeconds + 60 ).ToString( CultureInfo.InvariantCulture )}" );
      string seen = await RawAsync( $"SELECT count(), uniqExact( table ) FROM system.parts WHERE {where}" );
      _output.WriteLine( $"log parts written during the test: {seen.Replace( '\t', ' ' )} (parts, tables); without an expiry: {unset}; expiring later than {ttl.TotalSeconds:F0} s from now: {late}" );
      Assert.Equal( "[]", unset );
      Assert.Equal( "[]", late );
      Assert.True( int.Parse( seen.Split( '\t' )[0], CultureInfo.InvariantCulture ) > 0, "no log part was written during the test" );
   }

   /// <summary>
   /// One benchmark-style cycle on a fresh collection: load 2,000 random vectors, wait for the
   /// index, search from eight concurrent searchers, run a few exact searches, drop the collection.
   /// </summary>
   /// <param name="cycle">Cycle number, used to vary the vectors.</param>
   /// <param name="searchesPerSearcher">How many default searches each of the eight searchers runs.</param>
   private async Task RunCycleAsync( int cycle, int searchesPerSearcher )
   {
      string collection = EnginesAReadinessData.BenchCollection();
      using var sink = new ClickHouseSink();
      using var limit = new CancellationTokenSource( CYCLE_LIMIT );
      List<VectorRecord> records = EnginesAReadinessData.Records( COUNT, DIMENSION, seed: 70 + cycle );
      try
      {
         var clock = Stopwatch.StartNew();
         await sink.EnsureCollectionAsync( collection, DIMENSION, limit.Token );
         await sink.UpsertAsync( collection, records, limit.Token );
         await sink.FinishLoadAsync( collection, limit.Token );
         double loadSeconds = clock.Elapsed.TotalSeconds;

         clock.Restart();
         int[] matches = await Task.WhenAll( Enumerable.Range( 0, SEARCHERS ).Select( s => SearchLoopAsync( sink, collection, records, s, searchesPerSearcher, limit.Token ) ) );
         double searchSeconds = clock.Elapsed.TotalSeconds;
         for( int i = 0; i < EXACT_SEARCHES; i++ )
         {
            await sink.SearchExactAsync( collection, records[i].Vector, TOP, limit.Token );
         }

         int total = SEARCHERS * searchesPerSearcher;
         _output.WriteLine( $"cycle {cycle}: load+index {loadSeconds:F1} s, {total} searches {searchSeconds:F1} s ({total / searchSeconds:F0} per second), top-1 matches {matches.Sum()}" );
         Assert.True( matches.Sum() >= total * MIN_TOP_ONE_RATE, $"only {matches.Sum()} of {total} searches returned the stored vector first" );
      }
      finally
      {
         await sink.DropCollectionAsync( collection, CancellationToken.None );
      }
   }

   /// <summary>
   /// One searcher: runs its share of default searches and counts how many put the stored vector first.
   /// </summary>
   /// <param name="sink">The shared sink.</param>
   /// <param name="collection">Collection to search.</param>
   /// <param name="records">The loaded records; search i asks for record (searcher * 37 + i) mod COUNT.</param>
   /// <param name="searcher">Searcher number.</param>
   /// <param name="searches">How many searches this searcher runs.</param>
   /// <param name="ct">Cycle deadline.</param>
   /// <returns>How many searches returned the asked-for vector first.</returns>
   private static async Task<int> SearchLoopAsync( ClickHouseSink sink, string collection, List<VectorRecord> records, int searcher, int searches, CancellationToken ct )
   {
      int matches = 0;
      for( int i = 0; i < searches; i++ )
      {
         VectorRecord target = records[( searcher * 37 + i ) % COUNT];
         IReadOnlyList<SearchHit> hits = await sink.SearchAsync( collection, target.Vector, TOP, ct );
         if( hits.Count > 0 && hits[0].ChunkId == target.Chunk.ChunkId )
         {
            matches++;
         }
      }

      return matches;
   }

   /// <summary>
   /// The MergeTree tables of the system database and their engine definitions.
   /// </summary>
   /// <returns>Engine definition (system.tables.engine_full) by table name.</returns>
   private async Task<Dictionary<string, string>> EnginesAsync()
   {
      string rows = await RawAsync( "SELECT name, engine_full FROM system.tables WHERE database = 'system' AND engine = 'MergeTree' ORDER BY name FORMAT TSVRaw" );
      return rows.Split( '\n', StringSplitOptions.RemoveEmptyEntries ).Select( l => l.Split( '\t', 2 ) ).ToDictionary( p => p[0], p => p[1], StringComparer.Ordinal );
   }

   /// <summary>
   /// Bytes the system database's tables hold on disk, active parts and old parts not yet deleted.
   /// </summary>
   /// <returns>Bytes.</returns>
   private async Task<long> LogBytesAsync()
   {
      return long.Parse( await RawAsync( "SELECT sum( bytes_on_disk ) FROM system.parts WHERE database = 'system'" ), CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// Reads an XML file inside the container and returns its root element.
   /// </summary>
   /// <param name="path">Path inside the container.</param>
   /// <returns>The root element.</returns>
   private static async Task<XElement> ReadXmlAsync( string path )
   {
      string text = await DockerAsync( "exec", CONTAINER, "cat", path );
      return XDocument.Parse( text ).Root ?? throw new InvalidOperationException( $"{path} has no root element" );
   }

   /// <summary>
   /// The settings of a log table element that the TTL change is allowed to touch removed: the ttl
   /// and settings children, and for the span log the ttl clause inside its engine text. Whitespace
   /// is collapsed so the image's multi-line engine text compares with the one-line text.
   /// </summary>
   /// <param name="element">The log table element.</param>
   /// <param name="name">Its name.</param>
   /// <returns>Child name to normalized text, sorted by name.</returns>
   private static SortedDictionary<string, string> Settings( XElement element, string name )
   {
      var settings = new SortedDictionary<string, string>( StringComparer.Ordinal );
      foreach( XElement child in element.Elements().Where( e => e.Name.LocalName is not ( "ttl" or "settings" ) ) )
      {
         string text = Regex.Replace( child.Value, @"\s+", " " ).Trim();
         if( name == ClickHouseLogConfig.SPAN_LOG && child.Name.LocalName == "engine" )
         {
            text = Regex.Replace( text, @" ttl .*$", string.Empty, RegexOptions.IgnoreCase );
         }

         settings[child.Name.LocalName] = text;
      }

      return settings;
   }

   /// <summary>
   /// One line for a settings dictionary, for failure messages.
   /// </summary>
   /// <param name="settings">The settings.</param>
   /// <returns>name=value pairs.</returns>
   private static string Describe( SortedDictionary<string, string> settings )
   {
      return string.Join( "; ", settings.Select( p => $"{p.Key}={p.Value}" ) );
   }

   /// <summary>
   /// Size of the server's data folder in bytes, from du inside the container (the same bytes as
   /// /home/dan/gvb-data/engines/clickhouse on the host).
   /// </summary>
   /// <returns>Apparent size in bytes.</returns>
   private static async Task<long> FolderBytesAsync()
   {
      string output = await DockerAsync( "exec", CONTAINER, "du", "-sb", DATA_FOLDER );
      return long.Parse( output.Split( '\t', ' ' )[0], CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// Runs docker with a hard time limit and returns its standard output; a non-zero exit or a
   /// timeout throws with the error text.
   /// </summary>
   /// <param name="arguments">The docker arguments.</param>
   /// <returns>Standard output.</returns>
   private static async Task<string> DockerAsync( params string[] arguments )
   {
      var info = new ProcessStartInfo( "docker" ) { RedirectStandardOutput = true, RedirectStandardError = true };
      foreach( string argument in arguments )
      {
         info.ArgumentList.Add( argument );
      }

      using Process process = Process.Start( info ) ?? throw new InvalidOperationException( "docker did not start" );
      using var limit = new CancellationTokenSource( PROCESS_LIMIT );
      Task<string> output = process.StandardOutput.ReadToEndAsync( limit.Token );
      Task<string> error = process.StandardError.ReadToEndAsync( limit.Token );
      try
      {
         await process.WaitForExitAsync( limit.Token );
      }
      catch( OperationCanceledException )
      {
         process.Kill( entireProcessTree: true );
         throw new TimeoutException( $"docker {string.Join( ' ', arguments )} ran longer than {PROCESS_LIMIT.TotalSeconds:F0} s" );
      }

      if( process.ExitCode != 0 )
      {
         throw new InvalidOperationException( $"docker {string.Join( ' ', arguments )} exited {process.ExitCode}: {await error}" );
      }

      return await output;
   }

   /// <summary>
   /// Runs one statement over HTTP and returns the response text; an error status throws with the
   /// server's message.
   /// </summary>
   /// <param name="sql">The statement.</param>
   /// <returns>The response text, trimmed.</returns>
   private async Task<string> RawAsync( string sql )
   {
      using HttpResponseMessage response = await _http.PostAsync( "/", new StringContent( sql ) );
      string text = await response.Content.ReadAsStringAsync();
      if( !response.IsSuccessStatusCode )
      {
         throw new InvalidOperationException( $"ClickHouse {(int)response.StatusCode}: {text}" );
      }

      return text.Trim();
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Releases the HTTP client.
   /// </summary>
   public void Dispose()
   {
      _http.Dispose();
   }

   #endregion IDisposable
}
