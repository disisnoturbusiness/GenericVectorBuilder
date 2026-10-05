using System.Text.RegularExpressions;
using System.Xml.Linq;
using GenericVectorBuilder.Engines.Sinks;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// Checks, without a running container, that the ClickHouse compose file mounts the system log
/// config, that the config does nothing to the logging except put a TTL on every log table the
/// image switches on, and that the engine text the benchmark prints states that TTL.
/// Why: the 26.3 image writes query_log, text_log, trace_log and processors_profile_log rows for
/// every search, and an engine that runs with those tables off is faster than the same engine at
/// its defaults (about 16 percent at 8 searchers on this box, v6 against v5). The benchmark must
/// measure the default engine, so the config may bound the disk and nothing else. The live tests in
/// ClickHouseLogHygieneLiveTests prove the server obeys the file; these prove the file, the mount
/// and the engine text are there, so an edit that drops one fails on any machine.
/// </summary>
public sealed class ClickHouseLogHygieneConfigTests
{
   #region Public Methods

   /// <summary>
   /// The compose file mounts the config file into config.d, read-only, and the file exists.
   /// </summary>
   [Fact]
   public void Compose_MountsTheLogConfig_ReadOnly()
   {
      string compose = File.ReadAllText( ClickHouseLogConfig.ComposePath() );
      string expected = $"- ./{ClickHouseLogConfig.CONFIG_FOLDER}/{ClickHouseLogConfig.CONFIG_FILE}:{ClickHouseLogConfig.CONFIG_MOUNT}:ro";
      Assert.Contains( compose.Split( '\n' ), line => line.Trim() == expected );
      Assert.True( File.Exists( ClickHouseLogConfig.ConfigPath() ), $"{ClickHouseLogConfig.ConfigPath()} is missing" );
   }

   /// <summary>
   /// Every published port stays bound to the loopback address; the log change opens nothing.
   /// </summary>
   [Fact]
   public void Compose_PublishesOnlyLoopbackPorts()
   {
      string[] lines = File.ReadAllLines( ClickHouseLogConfig.ComposePath() ).Select( l => l.Trim() ).ToArray();
      int ports = Array.IndexOf( lines, "ports:" );
      Assert.True( ports >= 0, "the compose file lists no ports" );
      List<string> published = lines.Skip( ports + 1 ).TakeWhile( l => l.StartsWith( "- ", StringComparison.Ordinal ) ).ToList();
      Assert.NotEmpty( published );
      Assert.All( published, port => Assert.StartsWith( "- \"127.0.0.1:", port ) );
   }

   /// <summary>
   /// The config names every log table the image switches on, so none is left to grow without a
   /// bound, and names nothing else, so a listen address, a user or a profile cannot slip in
   /// through this file unnoticed. The server's own logger element is not in it either: its
   /// rotation is the image default.
   /// </summary>
   [Fact]
   public void Config_NamesEveryDefaultOnLogTable_AndNothingElse()
   {
      string[] actual = ClickHouseLogConfig.Load().Elements().Select( e => e.Name.LocalName ).OrderBy( n => n, StringComparer.Ordinal ).ToArray();
      string[] expected = ClickHouseLogConfig.DEFAULT_ON_LOGS.OrderBy( n => n, StringComparer.Ordinal ).ToArray();
      Assert.Equal( expected, actual );
   }

   /// <summary>
   /// No log table is switched off, and no element carries a setting that changes how or how often
   /// a table is written: only a ttl (and, for the span log whose table is defined by an engine
   /// text, that engine text) is allowed. A flush interval, a size cap, a level, a partition key or
   /// a remove attribute here would make the benchmark measure something other than the default.
   /// </summary>
   [Fact]
   public void Config_ChangesNothingButTheTtl()
   {
      XElement root = ClickHouseLogConfig.Load();
      foreach( string name in ClickHouseLogConfig.DEFAULT_ON_LOGS )
      {
         XElement element = root.Element( name ) ?? throw new InvalidOperationException( $"{name} is not in the config" );
         Assert.Empty( element.Attributes() );
         string[] children = element.Elements().Select( e => e.Name.LocalName ).ToArray();
         string[] allowed = name == ClickHouseLogConfig.SPAN_LOG ? new[] { "engine" } : new[] { "ttl" };
         Assert.True( children.SequenceEqual( allowed ), $"{name} has children [{string.Join( ", ", children )}], only [{string.Join( ", ", allowed )}] is allowed" );
      }
   }

   /// <summary>
   /// The span log's engine text is the image's own text (MergeTree, the same partition key and
   /// order key) with a TTL clause added, nothing else.
   /// Why: that table is defined by an engine, and ClickHouse refuses a ttl element next to one,
   /// so the TTL has to go inside the engine text, where it could drift from the image's definition.
   /// </summary>
   [Fact]
   public void Config_SpanLogEngine_IsTheImageEngineWithATtl()
   {
      XElement element = ClickHouseLogConfig.Load().Element( ClickHouseLogConfig.SPAN_LOG )!;
      string engine = Regex.Replace( element.Element( "engine" )!.Value, @"\s+", " " ).Trim();
      Assert.Matches( @"^engine MergeTree partition by toYYYYMM\(finish_date\) order by \(finish_date, finish_time_us\) ttl finish_date \+ INTERVAL \d+ (MINUTE|HOUR|DAY) DELETE$", engine );
   }

   /// <summary>
   /// Every table has a TTL of the same length, in whole minutes, hours or days, and no longer than
   /// a day, so every table is bounded and the one length can be stated in the engine text.
   /// </summary>
   [Fact]
   public void Config_EveryLogTable_CarriesTheSameBoundedTtl()
   {
      XElement root = ClickHouseLogConfig.Load();
      var lengths = new HashSet<TimeSpan>();
      foreach( string name in ClickHouseLogConfig.DEFAULT_ON_LOGS )
      {
         string clause = ClickHouseLogConfig.TtlClause( root, name );
         Assert.False( string.IsNullOrWhiteSpace( clause ), $"{name} has no ttl" );
         ( string column, _, _, TimeSpan length ) = ClickHouseLogConfig.ParseTtl( clause );
         Assert.Equal( name == ClickHouseLogConfig.SPAN_LOG ? "finish_date" : "event_time", column );
         Assert.InRange( length, TimeSpan.FromMinutes( 1 ), ClickHouseLogConfig.MAX_TTL );
         lengths.Add( length );
      }

      Assert.Single( lengths );
   }

   /// <summary>
   /// The engine text the benchmark prints says the logs are at the image defaults and states the
   /// TTL, and the TTL it states is the one in the config file.
   /// Why: the v6 results carried a non-default logging change that no report mentioned. The text
   /// is what results.md and the consolidated tables print for the engine.
   /// </summary>
   [Fact]
   public void EngineText_StatesTheLogDefaultsAndTheTtlOfTheConfig()
   {
      using var sink = new ClickHouseSink( new ClickHouseSinkOptions() );
      string fromConfig = ClickHouseLogConfig.TtlWords( ClickHouseLogConfig.TtlClause( ClickHouseLogConfig.Load(), "query_log" ) );
      Assert.Equal( ClickHouseSink.LOG_TTL_TEXT, fromConfig );
      Assert.StartsWith( "ClickHouse 26.3.39.7 (", sink.Engine );
      Assert.Contains( "image-default system logging", sink.Engine );
      Assert.Contains( $"TTL of {fromConfig}", sink.Engine );
   }

   /// <summary>
   /// The cleanup script drops only the renamed copies ClickHouse leaves when a TTL changes
   /// (name_0, name_1 and so on), never a live log table.
   /// </summary>
   [Fact]
   public void ClearScript_DropsOnlyRenamedCopies()
   {
      string script = File.ReadAllText( ClickHouseLogConfig.ScriptPath() );
      Match pattern = Regex.Match( script, @"^RENAMED_COPY_PATTERN='(?<regex>[^']+)'$", RegexOptions.Multiline );
      Assert.True( pattern.Success, "the script has no RENAMED_COPY_PATTERN line" );
      var renamed = new Regex( pattern.Groups["regex"].Value );
      Assert.All( ClickHouseLogConfig.DEFAULT_ON_LOGS, name => Assert.DoesNotMatch( renamed, name ) );
      Assert.Matches( renamed, "query_log_0" );
      Assert.Matches( renamed, "text_log_12" );
      Assert.DoesNotMatch( new Regex( @"^\s*(run|curl).*\bTRUNCATE\b", RegexOptions.Multiline ), script );
   }

   #endregion Public Methods
}
