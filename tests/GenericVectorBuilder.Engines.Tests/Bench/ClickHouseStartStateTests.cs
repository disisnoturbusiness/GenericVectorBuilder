namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The ClickHouse log-table reset (Targets/ClickHouseStartState.cs), against a scripted HTTP server: which statements it sends and in
/// what order, how it reads the table list, that it fails loud when a truncated table still holds data or ClickHouse refuses, how it
/// waits for the data folder to stop changing (and gives up at its deadline), and what the recorded sentence says.
/// Why a reset at all: ClickHouse writes its own logs into its data folder, and v7's three runs started with 99.17 KiB, 4.22 GiB and
/// 6.79 GiB of them. Why a scripted server: the engine cannot be started here; the pilot is the live proof, and it reads ClickHouse's
/// own active bytes and the folder's size side by side so it also shows whether freed parts leave the disk at once.
/// </summary>
public sealed class ClickHouseStartStateTests
{
   #region Data Members

   private const string LIST_BEFORE = "SELECT name, total_bytes=>200|metric_log\\t1048576000\\nquery_log\\t3145728000\\nquery_log_0\\t52428800\\ntext_log\\t0\\ntrace_log\\t104857600\\n";
   private const string LIST_AFTER = "SELECT name, total_bytes=>200|metric_log\\t0\\nquery_log\\t0\\nquery_log_0\\t52428800\\ntext_log\\t0\\ntrace_log\\t0\\n";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The reset: lists the MergeTree tables of database system, truncates those named *_log (not query_log_0, which is listed as
   /// skipped), lists again, then waits for the folder. The statements go out in that order, each by POST to the container's own address
   /// with the user and password in headers and the connection closed; the numbers are ClickHouse's own active bytes before and
   /// after and the folder's before and after, and the sentence carries only those.
   /// </summary>
   [Fact]
   public void Reset_TruncatesTheLogTablesInOrderAndRecordsBeforeAndAfter()
   {
      string[] lines = Run( new[] { LIST_BEFORE, "TRUNCATE=>200|", LIST_AFTER }, new long[] { 7_000_000_000, 150_000_000, 150_500_000 } );
      Assert.Equal( "stable|True", lines.Single( l => l.StartsWith( "stable|" ) ) );
      Assert.Equal( "numbers|4299161600|0|7000000000|150500000", lines.Single( l => l.StartsWith( "numbers|" ) ) );
      string[] statements = lines.Where( l => l.StartsWith( "request|" ) ).Select( l => l[( l.IndexOf( "closeConnection=True " ) + "closeConnection=True ".Length )..] ).ToArray();
      Assert.Equal( 6, statements.Length );
      Assert.StartsWith( "SELECT name, total_bytes FROM system.tables WHERE database = 'system' AND engine LIKE '%MergeTree' ORDER BY name FORMAT TSV", statements[0] );
      Assert.Equal( new[] { "TRUNCATE TABLE system.`metric_log`", "TRUNCATE TABLE system.`query_log`", "TRUNCATE TABLE system.`text_log`", "TRUNCATE TABLE system.`trace_log`" }, statements[1..5] );
      Assert.StartsWith( "SELECT name, total_bytes FROM system.tables", statements[5] );
      string first = lines.First( l => l.StartsWith( "request|" ) );
      Assert.StartsWith( "request|POST http://172.24.0.2:8123/ [", first );
      Assert.Contains( "X-ClickHouse-User=gvb", first );
      Assert.Contains( "X-ClickHouse-Key=ch-secret-d8e1", first );
      Assert.DoesNotContain( "ch-secret-d8e1", first.Split( ' ' )[1] );
      string reset = lines.Single( l => l.StartsWith( "reset|" ) );
      Assert.Equal( "reset|truncated 4 MergeTree log tables in database system (metric_log, query_log, text_log, trace_log); "
         + "ClickHouse's own active bytes of those tables 4 GiB before and 0 B after; data folder 6.52 GiB before and 143.53 MiB after (the folder was steady); "
         + "not truncated, name does not end in _log: query_log_0", reset );
   }

   /// <summary>
   /// A truncation that did not take effect (a table still holds more than 1 MiB of active data afterwards) is an error that says how
   /// much it still holds: the start state would not be the empty one the record claims.
   /// </summary>
   [Fact]
   public void Reset_FailsLoudWhenATruncatedTableStillHoldsData()
   {
      string stillFull = LIST_AFTER.Replace( "query_log\\t0", "query_log\\t5242880" );
      string[] lines = Run( new[] { LIST_BEFORE, "TRUNCATE=>200|", stillFull }, new long[] { 100, 100 } );
      Assert.Contains( "error|ClickHouse truncated 4 log tables but they still hold 5 MiB of active data (limit 1 MiB), so the reset did not take effect.", lines );
   }

   /// <summary>
   /// ClickHouse refusing a statement ends the reset with the status and the start of its message; the password is never in it.
   /// </summary>
   [Fact]
   public void Reset_AnErrorAnswerEndsItWithTheStatusAndTheStartOfTheMessage()
   {
      string[] lines = Run( new[] { "SELECT name, total_bytes=>200|query_log\\t10\\n", "TRUNCATE=>497|Code: 497. DB::Exception: gvb: Not enough privileges. To execute this query, it's necessary to have the grant TRUNCATE ON system.query_log" }, new long[] { 1, 1 } );
      string error = lines.Single( l => l.StartsWith( "error|" ) );
      Assert.StartsWith( "error|ClickHouse at 172.24.0.2:8123 answered HTTP 497 to a statement of 33 characters: Code: 497. DB::Exception: gvb: Not enough privileges.", error );
      Assert.DoesNotContain( "ch-secret-d8e1", error );
   }

   /// <summary>
   /// The wait for the folder: the first two readings within 1 MiB of each other (10 s apart) end it as steady, one wait of 10 s; readings
   /// that keep moving by more than that end it at the 60 s deadline (6 waits) with the last reading and Stable false, and the sentence says
   /// the folder was still changing.
   /// </summary>
   [Fact]
   public void Reset_WaitsForASteadyFolderAndGivesUpAtItsDeadline()
   {
      string[] steady = Run( new[] { LIST_BEFORE, "TRUNCATE=>200|", LIST_AFTER }, new long[] { 100_000_000, 100_900_000 } );
      Assert.Equal( "delays|1", steady.Single( l => l.StartsWith( "delays|" ) ) );
      Assert.Equal( "stable|True", steady.Single( l => l.StartsWith( "stable|" ) ) );
      long[] moving = Enumerable.Range( 0, 10 ).Select( i => 100_000_000L + i * 50_000_000L ).ToArray();
      string[] giveUp = Run( new[] { LIST_BEFORE, "TRUNCATE=>200|", LIST_AFTER }, moving );
      Assert.Equal( "delays|6", giveUp.Single( l => l.StartsWith( "delays|" ) ) );
      Assert.Equal( "stable|False", giveUp.Single( l => l.StartsWith( "stable|" ) ) );
      Assert.Equal( "numbers|4299161600|0|7000000000|400000000", giveUp.Single( l => l.StartsWith( "numbers|" ) ) );
      Assert.Contains( "the folder was STILL CHANGING at the deadline", giveUp.Single( l => l.StartsWith( "reset|" ) ) );
   }

   /// <summary>
   /// The table list is read strictly: a row is a name, a tab and a number (or ClickHouse's \N for no figure, taken as 0); anything else is an
   /// error that quotes the start of the row, not a guess.
   /// </summary>
   [Fact]
   public void TheTableListIsReadStrictly()
   {
      Assert.Equal( new[] { "metric_log=1048576000", "query_log=0", "text_log=0" }, (string[])TargetsHarness.CallSettings( "ParseTables", "metric_log\t1048576000\nquery_log\t\\N\ntext_log\t0\n" ) );
      Assert.Equal( new[] { "error|ClickHouse listed a table in a form that was not understood: 'query_log 10'." }, (string[])TargetsHarness.CallSettings( "ParseTables", "query_log 10\n" ) );
      Assert.Equal( new[] { "error|ClickHouse listed a table in a form that was not understood: 'query_log\t-5'." }, (string[])TargetsHarness.CallSettings( "ParseTables", "query_log\t-5\n" ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Runs the reset against a script of statement answers.
   /// </summary>
   /// <param name="http">Script entries.</param>
   /// <param name="folderSizes">Folder sizes the measurer returns in order.</param>
   /// <returns>The scenario's lines.</returns>
   private static string[] Run( string[] http, long[] folderSizes )
   {
      return (string[])TargetsHarness.CallSettings( "ClickHouse", http, folderSizes );
   }

   #endregion Private Methods
}
