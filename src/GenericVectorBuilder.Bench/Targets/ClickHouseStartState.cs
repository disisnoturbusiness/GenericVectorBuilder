using System.Globalization;
using System.Text;
using GenericVectorBuilder.Engines.Sinks;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// A small ClickHouse client over HTTP for the benchmark's own reads: its own HttpClient (the
/// sink's is private), the address of the container's own network address from
/// <see cref="BenchTarget.Connections"/>, the user and password the sink uses, and an injectable
/// message handler so the requests are tested without a server.
/// Why the password travels in a header and never in the address: an address ends up in error
/// messages, and a password in an error message ends up in results.json.
/// Why "Connection: close" on every request: the run records which connections the client holds
/// after its passes, and a read made before the load must leave none behind.
/// </summary>
public sealed class ClickHouseHttp : IDisposable
{
   #region Data Members

   /// <summary>Longest one request may take.</summary>
   public static readonly TimeSpan REQUEST_TIMEOUT = TimeSpan.FromSeconds( 120 );

   private readonly HttpClient _client;
   private readonly string _user;
   private readonly string? _password;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a client.
   /// </summary>
   /// <param name="handler">The message handler (a fake in tests); null for the real one.</param>
   /// <param name="user">ClickHouse user.</param>
   /// <param name="password">Its password, or null for none.</param>
   public ClickHouseHttp( HttpMessageHandler? handler, string user, string? password )
   {
      _client = handler == null ? new HttpClient() : new HttpClient( handler, disposeHandler: false );
      _client.Timeout = REQUEST_TIMEOUT;
      _user = user;
      _password = password;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The real client: the user and password the ClickHouse sink uses (<see cref="ClickHouseSinkOptions.LocalDefaults"/>,
   /// which reads secrets.env), and no handler of its own.
   /// </summary>
   /// <returns>A new client; the caller disposes it.</returns>
   public static ClickHouseHttp Local()
   {
      ClickHouseSinkOptions options = ClickHouseSinkOptions.LocalDefaults();
      return new ClickHouseHttp( null, options.User, options.Password );
   }

   /// <summary>
   /// Runs one statement and returns the response body.
   /// </summary>
   /// <param name="address">The container's own address (never the published port).</param>
   /// <param name="sql">The statement, sent as the request body.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The response text.</returns>
   /// <exception cref="InvalidOperationException">ClickHouse answered with an error; the message has its status and the start of its text.</exception>
   public async Task<string> QueryAsync( ContainerAddress address, string sql, CancellationToken ct )
   {
      using var request = new HttpRequestMessage( HttpMethod.Post, address.Url( "http" ) + "/" ) { Content = new StringContent( sql, Encoding.UTF8, "text/plain" ) };
      request.Headers.ConnectionClose = true;
      request.Headers.Add( "X-ClickHouse-User", _user );
      if( !string.IsNullOrEmpty( _password ) )
      {
         request.Headers.Add( "X-ClickHouse-Key", _password );
      }

      using HttpResponseMessage answer = await _client.SendAsync( request, ct );
      string body = await answer.Content.ReadAsStringAsync( ct );
      if( !answer.IsSuccessStatusCode )
      {
         string text = string.Join( ' ', body.Split( new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries ) );
         throw new InvalidOperationException( $"ClickHouse at {address.Endpoint} answered HTTP {(int)answer.StatusCode} to a statement of {sql.Length} characters: {( text.Length <= 300 ? text : text[..300] + "..." )}" );
      }

      return body;
   }

   #endregion Public Methods

   #region IDisposable

   /// <summary>
   /// Disposes the client (the message handler of a test is the test's).
   /// </summary>
   public void Dispose()
   {
      _client.Dispose();
   }

   #endregion IDisposable
}

/// <summary>
/// What a reset of ClickHouse's system log tables did, for the target's data-folder record.
/// </summary>
/// <param name="Truncated">Names of the tables truncated.</param>
/// <param name="Skipped">Names of MergeTree tables with "_log" in the name that were not truncated because the name does not end in "_log" (for example query_log_0).</param>
/// <param name="ActiveBytesBefore">Active bytes of the truncated tables according to ClickHouse (system.tables total_bytes) before the reset.</param>
/// <param name="ActiveBytesAfter">The same after the reset.</param>
/// <param name="FolderBytesBefore">The data folder's size before the reset.</param>
/// <param name="FolderBytesAfter">The data folder's size after the reset, once it stopped changing (or at the deadline).</param>
/// <param name="Stable">True when the folder stayed within the tolerance over a full stability window before the deadline.</param>
public sealed record ClickHouseReset( IReadOnlyList<string> Truncated, IReadOnlyList<string> Skipped, long ActiveBytesBefore, long ActiveBytesAfter, long FolderBytesBefore, long FolderBytesAfter, bool Stable )
{
   #region Public Methods

   /// <summary>
   /// The reset as the sentence recorded in the target's data-folder record. Every figure in it is a field
   /// of this record; nothing is added.
   /// </summary>
   /// <returns>The text.</returns>
   public string Describe()
   {
      string skipped = Skipped.Count == 0 ? "none" : string.Join( ", ", Skipped );
      string settled = Stable ? "the folder was steady" : "the folder was STILL CHANGING at the deadline";
      return $"truncated {Truncated.Count} MergeTree log tables in database system ({string.Join( ", ", Truncated )}); "
         + $"ClickHouse's own active bytes of those tables {Measurement.Format( ActiveBytesBefore )} before and {Measurement.Format( ActiveBytesAfter )} after; "
         + $"data folder {Measurement.Format( FolderBytesBefore )} before and {Measurement.Format( FolderBytesAfter )} after ({settled}); "
         + $"not truncated, name does not end in _log: {skipped}";
   }

   #endregion Public Methods
}

/// <summary>
/// Puts ClickHouse's system log tables back to empty before a run, so every session starts from
/// the same state. Why: ClickHouse writes its own query, trace and metric logs into its data
/// folder, and v7's three runs started with 99.17 KiB, 4.22 GiB and 6.79 GiB of them (one by
/// hand, the others not), which the ClickHouse results then carried unexplained.
/// What it does, in order: lists the tables of database system whose engine is in the MergeTree
/// family; truncates those whose name ends in "_log" (the rotated copies such as query_log_0 are
/// listed as skipped, not touched); reads the active bytes again and fails when a table kept more
/// than <see cref="TRUNCATED_LIMIT"/> (the truncation did not take effect); then waits for the
/// data folder to stop changing (log flushes write about every 7.5 s, so "steady" means within
/// <see cref="STEADY_TOLERANCE"/> over <see cref="STEADY_WINDOW"/>, and the wait ends at
/// <see cref="STEADY_DEADLINE"/> with the last figure and Stable false).
/// Why it only counts the folder after the truncation and does not wait for removal: whether ClickHouse
/// frees the parts at once or after old_parts_lifetime (480 s) is not verified here; both figures
/// (its own active bytes and the folder's size) are recorded so the pilot shows which it is.
/// Every request is bounded by <see cref="ClickHouseHttp.REQUEST_TIMEOUT"/> and the whole reset by
/// the overall deadline of the caller's token.
/// </summary>
public sealed class ClickHouseStartState
{
   #region Data Members

   /// <summary>Most active bytes a truncated table may still hold (1 MiB).</summary>
   public const long TRUNCATED_LIMIT = 1024 * 1024;

   /// <summary>How much the folder may change over one window and still count as steady (1 MiB).</summary>
   public const long STEADY_TOLERANCE = 1024 * 1024;

   /// <summary>The window over which the folder must hold steady.</summary>
   public static readonly TimeSpan STEADY_WINDOW = TimeSpan.FromSeconds( 10 );

   /// <summary>Longest the wait for a steady folder lasts.</summary>
   public static readonly TimeSpan STEADY_DEADLINE = TimeSpan.FromSeconds( 60 );

   private readonly Func<ClickHouseHttp> _http;
   private readonly Func<TimeSpan, CancellationToken, Task> _delay;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the reset.
   /// </summary>
   /// <param name="http">Makes the client for one reset (disposed after it); null for the real one.</param>
   /// <param name="delay">Waits for a time (a test passes one that returns at once); null for Task.Delay.</param>
   public ClickHouseStartState( Func<ClickHouseHttp>? http = null, Func<TimeSpan, CancellationToken, Task>? delay = null )
   {
      _http = http ?? ClickHouseHttp.Local;
      _delay = delay ?? Task.Delay;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Truncates the log tables and waits for the data folder to settle.
   /// </summary>
   /// <param name="address">ClickHouse's own address.</param>
   /// <param name="folderBytes">Measures the data folder's size in bytes (the same du the record uses).</param>
   /// <param name="folderBefore">The folder's size just before this call, already measured.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>What was done.</returns>
   /// <exception cref="InvalidOperationException">A statement failed, a table kept data after its truncation, or the server's answer was not understood.</exception>
   public async Task<ClickHouseReset> ResetAsync( ContainerAddress address, Func<CancellationToken, Task<long>> folderBytes, long folderBefore, CancellationToken ct )
   {
      using ClickHouseHttp http = _http();
      IReadOnlyList<LogTable> tables = await ListAsync( http, address, ct );
      List<LogTable> truncate = tables.Where( t => t.Name.EndsWith( "_log", StringComparison.Ordinal ) ).ToList();
      List<string> skipped = tables.Where( t => !t.Name.EndsWith( "_log", StringComparison.Ordinal ) && t.Name.Contains( "_log", StringComparison.Ordinal ) ).Select( t => t.Name ).ToList();
      foreach( LogTable table in truncate )
      {
         await http.QueryAsync( address, $"TRUNCATE TABLE system.`{table.Name}`", ct );
      }

      IReadOnlyList<LogTable> after = await ListAsync( http, address, ct );
      long left = after.Where( t => truncate.Any( x => x.Name == t.Name ) ).Sum( t => t.Bytes );
      if( after.Where( t => truncate.Any( x => x.Name == t.Name ) ).Any( t => t.Bytes > TRUNCATED_LIMIT ) )
      {
         throw new InvalidOperationException( $"ClickHouse truncated {truncate.Count} log tables but they still hold {Measurement.Format( left )} of active data (limit {Measurement.Format( TRUNCATED_LIMIT )}), so the reset did not take effect." );
      }

      ( long folderAfter, bool stable ) = await WaitSteadyAsync( folderBytes, ct );
      return new ClickHouseReset( truncate.Select( t => t.Name ).ToList(), skipped, truncate.Sum( t => t.Bytes ), left, folderBefore, folderAfter, stable );
   }

   /// <summary>
   /// Reads "name, total_bytes" rows as ClickHouse prints them in TSV.
   /// </summary>
   /// <param name="tsv">The response text.</param>
   /// <returns>The tables, in the order listed.</returns>
   /// <exception cref="InvalidOperationException">A row is not "name" TAB "number".</exception>
   public static IReadOnlyList<LogTable> ParseTables( string tsv )
   {
      var tables = new List<LogTable>();
      foreach( string line in tsv.Replace( "\r", string.Empty ).Split( '\n', StringSplitOptions.RemoveEmptyEntries ) )
      {
         string[] parts = line.Split( '\t' );
         bool number = parts.Length == 2 && ( long.TryParse( parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long bytes ) || parts[1] == "\\N" );
         if( !number )
         {
            throw new InvalidOperationException( $"ClickHouse listed a table in a form that was not understood: '{( line.Length <= 80 ? line : line[..80] + "..." )}'." );
         }

         tables.Add( new LogTable( parts[0], parts[1] == "\\N" ? 0 : long.Parse( parts[1], CultureInfo.InvariantCulture ) ) );
      }

      return tables;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Lists the MergeTree tables of database system with their active bytes.
   /// </summary>
   /// <param name="http">The client.</param>
   /// <param name="address">ClickHouse's address.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The tables.</returns>
   private static async Task<IReadOnlyList<LogTable>> ListAsync( ClickHouseHttp http, ContainerAddress address, CancellationToken ct )
   {
      string sql = "SELECT name, total_bytes FROM system.tables WHERE database = 'system' AND engine LIKE '%MergeTree' ORDER BY name FORMAT TSV";
      return ParseTables( await http.QueryAsync( address, sql, ct ) );
   }

   /// <summary>
   /// Measures the folder, waits one window, measures again, and repeats until two readings one window
   /// apart are within the tolerance or the deadline passes.
   /// </summary>
   /// <param name="folderBytes">Measures the folder.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The last reading and whether it was steady.</returns>
   private async Task<(long Bytes, bool Steady)> WaitSteadyAsync( Func<CancellationToken, Task<long>> folderBytes, CancellationToken ct )
   {
      long previous = await folderBytes( ct );
      TimeSpan waited = TimeSpan.Zero;
      while( waited < STEADY_DEADLINE )
      {
         await _delay( STEADY_WINDOW, ct );
         waited += STEADY_WINDOW;
         long now = await folderBytes( ct );
         if( Math.Abs( now - previous ) <= STEADY_TOLERANCE )
         {
            return ( now, true );
         }

         previous = now;
      }

      return ( previous, false );
   }

   #endregion Private Methods
}

/// <summary>
/// One MergeTree table of database system with its active bytes.
/// </summary>
/// <param name="Name">Table name.</param>
/// <param name="Bytes">Active bytes (system.tables total_bytes; 0 when ClickHouse gives none).</param>
public sealed record LogTable( string Name, long Bytes );
