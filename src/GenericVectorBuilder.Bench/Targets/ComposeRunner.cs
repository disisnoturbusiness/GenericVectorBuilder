using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// Starts and stops one engine's containers with docker compose, for run-all.
/// Why an engine that was already running is left running: other work (another benchmark,
/// an engine's own tests) may be using it, and stopping it under them would break that work.
/// run-all only stops what it started itself. Data folders are never removed: a stop is "down"
/// without "-v", matching deploy/engines/README.md. The one "-v" is the start retry's removal of a
/// container that exited while starting (see <see cref="ComposeStart"/>), which takes the container
/// and its anonymous volumes and never a bind-mounted data folder.
/// Why a start is retried: the review of 2026-10-05 had Milvus exit during start-up ("panic:
/// etcdserver: leader changed", exit 134) on 14 of 14 starts from one data folder, and a benchmark
/// that dies there loses the whole engine's turn. A container that exits while starting is removed
/// and started again, up to three attempts inside one time budget, and every attempt is written into
/// the run notes. The retry also finishes a repair that the first, failed start begins: Milvus's
/// embedded etcd writes a snapshot while it fails, and the next start is then quick enough to win
/// (deploy/engines/milvus.compose.yaml has the measurements). That is why the data folder must
/// survive between attempts.
/// Why an engine is started with its CPU set already applied (<see cref="UpAsync(string, string, CancellationToken)"/>):
/// most engines size their thread pools from the CPUs they can see when they start (SQL Server
/// makes one scheduler per CPU, Elasticsearch sizes its search pool from its processor count,
/// Qdrant its search and optimizer threads). The review found Elasticsearch running with 4
/// processors' worth of threads on 8 CPUs because it was moved with "docker update" after it
/// had started. Starting the containers with the cpuset in place means each engine sees only
/// the CPUs it will really run on. The cpuset is added with a generated compose override file,
/// so no engine's own compose file has to know about it, and every container of the file gets
/// it (an engine of several containers, such as Oracle with its seeding step, is held as a whole).
/// </summary>
public static class ComposeRunner
{
   #region Data Members

   private static readonly TimeSpan START_TIMEOUT = TimeSpan.FromMinutes( 20 );
   private static readonly TimeSpan STOP_TIMEOUT = TimeSpan.FromMinutes( 5 );
   private static readonly TimeSpan QUERY_TIMEOUT = TimeSpan.FromMinutes( 2 );
   private static readonly Regex TOP_LEVEL_VOLUMES = new( @"^volumes:", RegexOptions.Multiline | RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// True when at least one container of the compose file is running.
   /// Why "--orphans=false": four engine files (elasticsearch, opensearch, typesense, vespa) have no
   /// project name of their own and share the folder's, so without it compose 2.40 also lists the
   /// other three engines' containers and a stopped engine looks running whenever another one is.
   /// Why it throws when compose itself fails: the older version answered "not running" for a failed
   /// "compose ps" (a daemon that did not answer, a sudo that wanted a password), and run-all then
   /// started an engine that was in fact up, or took a running engine for one it had started and
   /// stopped it afterwards. A question that could not be asked has no answer, so it is an error.
   /// Called for every target of the run at its start (see EngineLifecycle.SnapshotAsync), so a
   /// failure there ends the whole run before anything is measured, not one target.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when running.</returns>
   /// <exception cref="InvalidOperationException">Compose could not be asked or answered with a failure.</exception>
   public static Task<bool> IsRunningAsync( string composePath, CancellationToken ct )
   {
      return IsRunningAsync( composePath, ( arguments, limit, token ) => Shell.RunAsync( "sudo", arguments, limit, token ), ct );
   }

   /// <summary>
   /// The same question through a given command runner, so a test can make "compose ps" fail, time
   /// out or answer without Docker.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="run">Runs "sudo" with the given arguments within the given time and returns its result.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when running.</returns>
   /// <exception cref="InvalidOperationException">The command did not start, did not finish in time, or exited with a failure.</exception>
   public static async Task<bool> IsRunningAsync( string composePath, Func<IEnumerable<string>, TimeSpan, CancellationToken, Task<ShellResult>> run, CancellationToken ct )
   {
      string file = Path.GetFileName( composePath );
      ShellResult ps;
      try
      {
         ps = await run( new[] { "-n", "docker", "compose", "-f", composePath, "ps", "-q", "--status", "running", "--orphans=false" }, QUERY_TIMEOUT, ct );
      }
      catch( Exception ex ) when( ex is TimeoutException or System.ComponentModel.Win32Exception )
      {
         throw new InvalidOperationException( $"Could not ask docker compose whether {file} is running: {ex.Message}" );
      }

      if( ps.ExitCode != 0 )
      {
         throw new InvalidOperationException( $"docker compose ps for {file} failed with exit code {ps.ExitCode}, so it is not known whether the engine is running: {Last( ps.Error, 300 )}" );
      }

      return !string.IsNullOrWhiteSpace( ps.Output );
   }

   /// <summary>
   /// Starts the containers and waits until compose reports them healthy (or running, for a
   /// service without a health check). No CPU set is added: the compose file's own settings apply.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   /// <exception cref="InvalidOperationException">Compose failed or the engine never became healthy.</exception>
   public static Task UpAsync( string composePath, CancellationToken ct )
   {
      return UpAsync( composePath, null, ct );
   }

   /// <summary>
   /// Starts the containers with every one of them held to <paramref name="cpuset"/> from the
   /// moment it is created, waits until compose reports them healthy, then reads the cpuset back
   /// from Docker for every container of the file.
   /// Why compose is asked to recreate nothing by hand: a container that exists but is stopped and
   /// was created with other settings is recreated by compose itself because the override changes
   /// its configuration, so an old unpinned container can never be started as it was.
   /// Why the line can carry attempts: a container that exits while starting is removed and
   /// started again (see <see cref="ComposeStart"/>); when that happened the returned line lists
   /// every attempt, so the run notes show an engine that needed a second try.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="cpuset">Kernel CPU list such as "2-3,6-7", or null to start without one (the compose file decides).</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>One line saying what was started on which CPUs, read back from Docker, followed by the start attempts when there was more than one.</returns>
   /// <exception cref="ArgumentException">The cpuset is not a CPU list.</exception>
   /// <exception cref="InvalidOperationException">Compose failed, every attempt exited, the engine never became healthy, or a container came up with another cpuset.</exception>
   public static async Task<string> UpAsync( string composePath, string? cpuset, CancellationToken ct )
   {
      if( cpuset != null && !CpuSetText.IsValid( cpuset ) )
      {
         throw new ArgumentException( $"'{cpuset}' is not a CPU list such as 2-3,6-7, so {Path.GetFileName( composePath )} was not started.", nameof( cpuset ) );
      }

      string? overridePath = cpuset == null ? null : await WriteOverrideAsync( composePath, cpuset, ct );
      IReadOnlyList<string> attempts;
      try
      {
         var commands = new DockerComposeCommands( composePath, overridePath, RemovesVolumesOnRetry( ConfigFiles.TryReadFile( composePath ) ) );
         attempts = await ComposeStart.RunAsync( commands, Path.GetFileName( composePath ), StartPolicy.Default, ct );
      }
      finally
      {
         DeleteQuietly( overridePath );
      }

      string started = await ReadBackAsync( composePath, cpuset, ct );
      return attempts.Count > 1 ? $"{started}; start attempts: {string.Join( "; ", attempts )}" : started;
   }

   /// <summary>
   /// Stops and removes the containers, keeping their data folders.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Null when stopped cleanly, else what compose said.</returns>
   public static async Task<string?> DownAsync( string composePath, CancellationToken ct )
   {
      ShellResult result = await Shell.RunAsync( "sudo", new[] { "docker", "compose", "-f", composePath, "down" }, STOP_TIMEOUT, ct );
      return result.ExitCode == 0 ? null : $"compose down said: {Last( result.Error, 300 )}";
   }

   /// <summary>
   /// The image the compose file's first service names, for a report line written before the
   /// engine runs (the image actually used is read from Docker once the container is up).
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <returns>The image reference, or words saying it could not be read.</returns>
   public static string ImageOf( string composePath )
   {
      string? text = ConfigFiles.TryReadFile( composePath );
      Match image = text == null ? Match.Empty : Regex.Match( text, @"^\s*image:\s*""?(?<image>[^\s""#]+)", RegexOptions.Multiline );
      return image.Success ? image.Groups["image"].Value : $"not read from {Path.GetFileName( composePath )}";
   }

   /// <summary>
   /// The compose override that holds every named service to one CPU set. Pure, so the text is
   /// tested without Docker.
   /// </summary>
   /// <param name="services">Service names from "docker compose config --services".</param>
   /// <param name="cpuset">Kernel CPU list.</param>
   /// <returns>The YAML text.</returns>
   /// <exception cref="ArgumentException">No service, a service name compose would not accept, or a malformed cpuset.</exception>
   public static string OverrideYaml( IReadOnlyCollection<string> services, string cpuset )
   {
      if( !CpuSetText.IsValid( cpuset ) )
      {
         throw new ArgumentException( $"'{cpuset}' is not a CPU list such as 2-3,6-7.", nameof( cpuset ) );
      }

      if( services.Count == 0 || services.Any( s => s.Length == 0 || !s.All( c => char.IsAsciiLetterOrDigit( c ) || c is '-' or '_' or '.' ) ) )
      {
         throw new ArgumentException( $"Unexpected service names from compose: '{string.Join( "', '", services )}'.", nameof( services ) );
      }

      var text = new StringBuilder( "# Written by the benchmark for one start: every container of the engine gets the engine CPUs from its creation.\nservices:\n" );
      foreach( string service in services )
      {
         text.Append( "  " ).Append( service ).Append( ":\n    cpuset: \"" ).Append( cpuset ).Append( "\"\n" );
      }

      return text.ToString();
   }

   /// <summary>
   /// True when a start that failed may be cleaned up with "down -v": the compose file was read and
   /// declares no named volumes. Why: "-v" removes named volumes and anonymous volumes, and a named
   /// volume could hold an engine's data. None of the engine files under deploy/engines declares
   /// one (every data folder is a bind mount, which "-v" never touches), but a file that does, or
   /// one that could not be read, is cleaned up with a plain "down" so the rule "data is never
   /// removed" holds for any compose file.
   /// </summary>
   /// <param name="composeText">The compose file's text, or null when it could not be read.</param>
   /// <returns>True when "-v" is safe.</returns>
   public static bool RemovesVolumesOnRetry( string? composeText )
   {
      return composeText != null && !TOP_LEVEL_VOLUMES.IsMatch( composeText );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Asks compose for the file's services and writes the cpuset override to a new temporary file.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="cpuset">Kernel CPU list.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The override's path; the caller deletes it.</returns>
   private static async Task<string> WriteOverrideAsync( string composePath, string cpuset, CancellationToken ct )
   {
      ShellResult services = await Shell.RunAsync( "sudo", new[] { "-n", "docker", "compose", "-f", composePath, "config", "--services" }, QUERY_TIMEOUT, ct );
      if( services.ExitCode != 0 )
      {
         throw new InvalidOperationException( $"Could not read the services of {Path.GetFileName( composePath )}: {Last( services.Error, 400 )}" );
      }

      string[] names = services.Output.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
      string path = Path.Combine( Path.GetTempPath(), $"gvb-cpuset-{Guid.NewGuid():N}.yaml" );
      await File.WriteAllTextAsync( path, OverrideYaml( names, cpuset ), ct );
      return path;
   }

   /// <summary>
   /// Reads every container of the compose file (running or finished) and checks its cpuset.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="cpuset">The cpuset asked for, or null when none was.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>One line naming each container and its cpuset.</returns>
   /// <exception cref="InvalidOperationException">A container has another cpuset than the one asked for, or Docker could not be read.</exception>
   private static async Task<string> ReadBackAsync( string composePath, string? cpuset, CancellationToken ct )
   {
      ShellResult ps = await Shell.RunAsync( "sudo", new[] { "-n", "docker", "compose", "-f", composePath, "ps", "-a", "-q", "--orphans=false" }, QUERY_TIMEOUT, ct );
      string[] ids = ps.Output.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
      if( ps.ExitCode != 0 || ids.Length == 0 )
      {
         throw new InvalidOperationException( $"{Path.GetFileName( composePath )} reported healthy but its containers could not be listed: {Last( ps.Error, 300 )}" );
      }

      ShellResult inspect = await Shell.RunAsync( "sudo", new[] { "-n", "docker", "inspect", "-f", "{{.Name}}|{{.HostConfig.CpusetCpus}}" }.Concat( ids ), QUERY_TIMEOUT, ct );
      var seen = new List<string>();
      foreach( string line in inspect.Output.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
      {
         string[] parts = line.Split( '|' );
         string name = parts[0].TrimStart( '/' );
         string actual = parts.Length > 1 ? parts[1] : string.Empty;
         if( cpuset != null && !CpuSetText.Same( actual, cpuset ) )
         {
            throw new InvalidOperationException( $"{Path.GetFileName( composePath )} started, but container {name} has cpuset '{actual}', not {cpuset}; it would not run on the engine CPUs." );
         }

         seen.Add( $"{name} cpuset {( actual.Length == 0 ? "none (every CPU)" : actual )}" );
      }

      if( inspect.ExitCode != 0 || seen.Count != ids.Length )
      {
         throw new InvalidOperationException( $"Could not read back the cpuset of every container of {Path.GetFileName( composePath )}: {Last( inspect.Error, 300 )}" );
      }

      return cpuset == null
         ? $"started {Path.GetFileName( composePath )} without a benchmark cpuset (its own settings apply): {string.Join( ", ", seen )}"
         : $"started {Path.GetFileName( composePath )} with every container created on CPUs {cpuset}: {string.Join( ", ", seen )} (read back from docker inspect)";
   }

   /// <summary>
   /// Deletes a temporary file, ignoring a file that is already gone.
   /// </summary>
   /// <param name="path">File, or null.</param>
   private static void DeleteQuietly( string? path )
   {
      try
      {
         if( path != null )
         {
            File.Delete( path );
         }
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
      {
         // A leftover override file holds no secret and changes nothing: it is only read when named with -f.
      }
   }

   /// <summary>
   /// The last characters of a long message (compose puts the cause at the end).
   /// </summary>
   /// <param name="text">Message.</param>
   /// <param name="max">Characters to keep.</param>
   /// <returns>The tail.</returns>
   private static string Last( string text, int max )
   {
      string trimmed = text.Trim();
      return trimmed.Length <= max ? trimmed : "..." + trimmed[^max..];
   }

   #endregion Private Methods
}

/// <summary>
/// The docker compose commands one engine start needs, behind an interface so the retry rules are
/// tested without Docker (a container that exits, a container that never exits, a clean-up that
/// fails). The real implementation runs "sudo -n docker compose" with a time limit on every call.
/// </summary>
public interface IComposeCommands
{
   /// <summary>
   /// Runs "up -d --wait" for the engine's containers and waits for them to be healthy.
   /// </summary>
   /// <param name="waitTimeout">Longest wait for the containers to become healthy.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Compose's exit code and output; a non-zero exit code is a failed start, not an exception.</returns>
   Task<ShellResult> UpAsync( TimeSpan waitTimeout, CancellationToken ct );

   /// <summary>
   /// Lists the engine's containers that have exited, with their exit codes.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The exited containers (empty when none has).</returns>
   /// <exception cref="InvalidOperationException">Compose could not be asked, or answered in a form that was not understood.</exception>
   Task<IReadOnlyList<ExitedContainer>> ExitedAsync( CancellationToken ct );

   /// <summary>
   /// The recent log of one container, for the run notes.
   /// </summary>
   /// <param name="container">Container name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The log text (empty when it could not be read).</returns>
   Task<string> LogsAsync( string container, CancellationToken ct );

   /// <summary>
   /// The clean-up command as it will be run, for the run notes (for example "docker compose down -v").
   /// </summary>
   string RemoveDescription { get; }

   /// <summary>
   /// Removes the engine's containers (and, when allowed, their anonymous volumes), never a bind-mounted data folder.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Null when removed, else what compose said.</returns>
   Task<string?> RemoveContainersAsync( CancellationToken ct );
}

/// <summary>
/// A container that has exited.
/// </summary>
/// <param name="Name">Container name, e.g. "gvb-milvus".</param>
/// <param name="ExitCode">Its exit code (134 is a Go panic, 137 a kill).</param>
public sealed record ExitedContainer( string Name, int ExitCode );

/// <summary>
/// How many times a start is tried and how long it may take in all.
/// Why a total time as well as an attempt count: each attempt may wait up to 20 minutes for an engine
/// that is slow but alive, and three of those must not become an hour of silence.
/// </summary>
/// <param name="Attempts">Most starts to try (at least 1).</param>
/// <param name="AttemptTimeout">Longest wait for one attempt to become healthy.</param>
/// <param name="TotalTimeout">Longest time for all attempts together; the last attempt is cut short to fit.</param>
/// <param name="Pause">Wait between a clean-up and the next attempt, so a host that was starved can settle.</param>
/// <param name="MinimumAttempt">Least time left for another attempt to be worth starting.</param>
public sealed record StartPolicy( int Attempts, TimeSpan AttemptTimeout, TimeSpan TotalTimeout, TimeSpan Pause, TimeSpan MinimumAttempt )
{
   /// <summary>
   /// Three attempts of up to 20 minutes inside 30 minutes, 5 seconds apart, none begun with less than 2 minutes left.
   /// </summary>
   public static StartPolicy Default { get; } = new( 3, TimeSpan.FromMinutes( 20 ), TimeSpan.FromMinutes( 30 ), TimeSpan.FromSeconds( 5 ), TimeSpan.FromMinutes( 2 ) );
}

/// <summary>
/// Starts an engine and starts it again when a container exits while it is starting.
/// Why: Milvus standalone runs its embedded etcd and the engine in one process, and when the etcd
/// log it must replay is long it exits during start-up ("panic: etcdserver: leader changed", exit
/// 134, review 2026-10-05).
/// Only a container that EXITED with a non-zero code is retried. A start that fails without an
/// exit (image missing, port taken, a health check that never passes, a time limit) is a real fault
/// that a second attempt would only repeat for 20 more minutes, so it fails at once, as before.
/// Between attempts the failed containers are removed (down -v); a bind-mounted data folder is never
/// touched by that, and a compose file with named volumes is cleaned up without -v at all.
/// Every attempt is returned as one line for the run notes, and a start that never works throws with
/// all of them, so the cause of the last failure is never hidden behind the first.
/// </summary>
public static class ComposeStart
{
   #region Data Members

   // Lines of a Go stack dump: a frame address, a goroutine header, a "created by" line. They follow the panic line and must never be taken for the cause.
   private static readonly Regex STACK_LINE = new( @"\+0x[0-9a-f]+|\(0x[0-9a-f]+|^goroutine \d+|^created by |^runtime\.", RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Starts the engine, retrying when a container exits during start-up.
   /// </summary>
   /// <param name="commands">The compose commands.</param>
   /// <param name="fileName">Compose file name, for messages.</param>
   /// <param name="policy">Attempts and time limits.</param>
   /// <param name="ct">Cancellation.</param>
   /// <param name="elapsed">Time since the start began; null to use a stopwatch (tests pass a fake clock).</param>
   /// <returns>One line per attempt, the last of them the healthy one.</returns>
   /// <exception cref="ArgumentException">The policy allows no attempt.</exception>
   /// <exception cref="InvalidOperationException">The start failed without an exit, every attempt exited, the time ran out, or a failed container could not be removed.</exception>
   /// <exception cref="TimeoutException">A compose call outlived its time limit (never retried).</exception>
   public static async Task<IReadOnlyList<string>> RunAsync( IComposeCommands commands, string fileName, StartPolicy policy, CancellationToken ct, Func<TimeSpan>? elapsed = null )
   {
      if( policy.Attempts < 1 )
      {
         throw new ArgumentException( "A start needs at least one attempt.", nameof( policy ) );
      }

      var clock = Stopwatch.StartNew();
      Func<TimeSpan> now = elapsed ?? ( () => clock.Elapsed );
      var notes = new List<string>();
      int attempt = 0;
      while( true )
      {
         attempt++;
         TimeSpan began = now();
         ShellResult result = await commands.UpAsync( Shorter( policy.AttemptTimeout, policy.TotalTimeout - began ), ct );
         if( result.ExitCode == 0 )
         {
            notes.Add( $"attempt {attempt} of {policy.Attempts}: healthy after {( now() - began ).TotalSeconds:0} s" );
            return notes;
         }

         List<ExitedContainer> crashed = await CrashedAsync( commands, fileName, result, notes, ct );
         string note = $"attempt {attempt} of {policy.Attempts}: {await DescribeAsync( commands, crashed, result, ct )}";
         string? noMore = NoMoreAttempts( attempt, policy, policy.TotalTimeout - now() );
         if( noMore != null )
         {
            notes.Add( $"{note}; {noMore}" );
            throw new InvalidOperationException( $"Could not start {fileName}: {string.Join( " | ", notes )}" );
         }

         string? problem = await commands.RemoveContainersAsync( ct );
         notes.Add( problem == null
            ? $"{note}; removed the failed container only ({commands.RemoveDescription}), the data folder was not touched"
            : $"{note}; could not remove it: {Tail( problem, 200 )}" );
         if( problem != null )
         {
            throw new InvalidOperationException( $"Could not start {fileName}, and the failed container could not be removed for another attempt: {string.Join( " | ", notes )}" );
         }

         await Task.Delay( policy.Pause, ct );
      }
   }

   /// <summary>
   /// Reads "docker compose ps --format '{{.Name}}|{{.ExitCode}}'" output.
   /// </summary>
   /// <param name="text">The output, one "name|code" per line.</param>
   /// <returns>The containers.</returns>
   /// <exception cref="InvalidOperationException">A line is not "name|number".</exception>
   public static List<ExitedContainer> ParseExited( string text )
   {
      var list = new List<ExitedContainer>();
      foreach( string line in text.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ) )
      {
         string[] parts = line.Split( '|' );
         if( parts.Length != 2 || parts[0].Length == 0 || !int.TryParse( parts[1], out int code ) )
         {
            throw new InvalidOperationException( $"Unexpected line from docker compose ps: '{Tail( line, 100 )}'." );
         }

         list.Add( new ExitedContainer( parts[0], code ) );
      }

      return list;
   }

   /// <summary>
   /// The line of a container's log that says why it died: a Go panic or fatal error first, else the
   /// last error line, else the last line. Stack-dump lines are never chosen (Milvus's panic is followed by
   /// about 1700 lines of them, so the last line of its log says nothing). One line, whitespace collapsed,
   /// cut to 200 characters.
   /// </summary>
   /// <param name="logs">The container's log text.</param>
   /// <returns>The line, or words saying there was none.</returns>
   public static string FailureLine( string logs )
   {
      string[] lines = logs.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ).Where( l => !STACK_LINE.IsMatch( l ) ).ToArray();
      string? line = lines.FirstOrDefault( l => l.Contains( "panic:", StringComparison.Ordinal ) || l.Contains( "fatal error:", StringComparison.Ordinal ) )
         ?? lines.LastOrDefault( l => l.Contains( "[PANIC]", StringComparison.Ordinal ) || l.Contains( "[FATAL]", StringComparison.Ordinal ) || l.Contains( "[ERROR]", StringComparison.Ordinal ) )
         ?? lines.LastOrDefault();
      if( line == null )
      {
         return "no log lines";
      }

      string one = string.Join( ' ', line.Split( ' ', StringSplitOptions.RemoveEmptyEntries ) );
      return one.Length <= 200 ? one : one[..200] + "...";
   }

   /// <summary>
   /// The last non-empty line of a message, whitespace collapsed, cut to <paramref name="max"/> characters.
   /// Why the last line: compose writes its progress ("Container x Creating", "Container x Started") to
   /// standard error and states the cause on the final line ("container x exited (134)").
   /// </summary>
   /// <param name="text">Message.</param>
   /// <param name="max">Characters to keep.</param>
   /// <returns>The line, or words saying there was none.</returns>
   public static string LastLine( string text, int max )
   {
      string? line = text.Split( '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries ).LastOrDefault();
      if( line == null )
      {
         return "nothing";
      }

      string one = string.Join( ' ', line.Split( ' ', StringSplitOptions.RemoveEmptyEntries ) );
      return one.Length <= max ? one : one[..max] + "...";
   }

   /// <summary>
   /// The last characters of a long message, whitespace collapsed (compose puts the cause at the end).
   /// </summary>
   /// <param name="text">Message.</param>
   /// <param name="max">Characters to keep.</param>
   /// <returns>The tail.</returns>
   public static string Tail( string text, int max )
   {
      string one = string.Join( ' ', text.Split( new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries ) );
      return one.Length <= max ? one : "..." + one[^max..];
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The containers that exited with a non-zero code, after a failed "up". Exit code 0 is a one-shot
   /// step that finished (Oracle's seeding step), not a crash.
   /// </summary>
   /// <param name="commands">The compose commands.</param>
   /// <param name="fileName">Compose file name, for messages.</param>
   /// <param name="failed">The failed "up".</param>
   /// <param name="notes">Attempt lines so far, repeated in the error.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The crashed containers.</returns>
   /// <exception cref="InvalidOperationException">None crashed (the start failed for another reason), or compose could not say.</exception>
   private static async Task<List<ExitedContainer>> CrashedAsync( IComposeCommands commands, string fileName, ShellResult failed, List<string> notes, CancellationToken ct )
   {
      string earlier = notes.Count == 0 ? string.Empty : $" Earlier: {string.Join( " | ", notes )}";
      List<ExitedContainer> crashed;
      try
      {
         crashed = ( await commands.ExitedAsync( ct ) ).Where( c => c.ExitCode != 0 ).ToList();
      }
      catch( InvalidOperationException ex )
      {
         throw new InvalidOperationException( $"Could not start {fileName}: {Tail( failed.Error, 400 )} (and could not tell whether a container exited: {ex.Message}).{earlier}" );
      }

      if( crashed.Count == 0 )
      {
         throw new InvalidOperationException( $"Could not start {fileName}: {Tail( failed.Error, 400 )}.{earlier}" );
      }

      return crashed;
   }

   /// <summary>
   /// The text for one failed attempt: which containers exited with what code, what compose said, and
   /// the log line that says why.
   /// </summary>
   /// <param name="commands">The compose commands.</param>
   /// <param name="crashed">The crashed containers.</param>
   /// <param name="failed">The failed "up".</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The text.</returns>
   private static async Task<string> DescribeAsync( IComposeCommands commands, List<ExitedContainer> crashed, ShellResult failed, CancellationToken ct )
   {
      string who = string.Join( ", ", crashed.Select( c => $"{c.Name} exited with code {c.ExitCode}" ) );
      string log = FailureLine( await commands.LogsAsync( crashed[0].Name, ct ) );
      return $"{who} during start-up (compose said: {LastLine( failed.Error, 150 )}; log: {log})";
   }

   /// <summary>
   /// Why no further attempt may be made, or null when one may.
   /// </summary>
   /// <param name="attempt">The attempt that just failed (1-based).</param>
   /// <param name="policy">Attempts and time limits.</param>
   /// <param name="left">Time left of the total.</param>
   /// <returns>The reason, or null.</returns>
   private static string? NoMoreAttempts( int attempt, StartPolicy policy, TimeSpan left )
   {
      if( attempt >= policy.Attempts )
      {
         return $"no attempts left after {policy.Attempts}";
      }

      return left < policy.MinimumAttempt ? "no time left for another attempt" : null;
   }

   /// <summary>
   /// The smaller of two times, never below one second (a wait of zero or less would be refused by compose).
   /// </summary>
   /// <param name="a">One time.</param>
   /// <param name="b">The other.</param>
   /// <returns>The smaller, at least one second.</returns>
   private static TimeSpan Shorter( TimeSpan a, TimeSpan b )
   {
      TimeSpan smaller = a < b ? a : b;
      return smaller < TimeSpan.FromSeconds( 1 ) ? TimeSpan.FromSeconds( 1 ) : smaller;
   }

   #endregion Private Methods
}

/// <summary>
/// The real compose commands: "sudo -n docker compose" on one compose file (and its CPU override),
/// each with a time limit.
/// Why "-n": a sudo that wants a password must fail at once, not wait for input that never comes.
/// </summary>
internal sealed class DockerComposeCommands : IComposeCommands
{
   #region Data Members

   private static readonly TimeSpan STOP_TIMEOUT = TimeSpan.FromMinutes( 5 );
   private static readonly TimeSpan QUERY_TIMEOUT = TimeSpan.FromMinutes( 2 );
   private const int LOG_LINES = 5000;

   private readonly string _composePath;
   private readonly string? _overridePath;
   private readonly bool _removeVolumes;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the commands for one compose file.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="overridePath">The CPU override file, or null.</param>
   /// <param name="removeVolumes">True to clean up a failed start with "down -v" (see <see cref="ComposeRunner.RemovesVolumesOnRetry"/>).</param>
   public DockerComposeCommands( string composePath, string? overridePath, bool removeVolumes )
   {
      _composePath = composePath;
      _overridePath = overridePath;
      _removeVolumes = removeVolumes;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Runs "up -d --wait" with the CPU override, if any.
   /// </summary>
   /// <param name="waitTimeout">Longest wait for healthy.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Compose's result.</returns>
   public Task<ShellResult> UpAsync( TimeSpan waitTimeout, CancellationToken ct )
   {
      List<string> arguments = Arguments( _composePath, _overridePath, "up", "-d", "--wait", "--wait-timeout", ( (int)waitTimeout.TotalSeconds ).ToString() );
      return Shell.RunAsync( "sudo", arguments, waitTimeout + TimeSpan.FromMinutes( 1 ), ct );
   }

   /// <summary>
   /// Lists exited containers with "ps -a --status exited".
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The exited containers.</returns>
   /// <exception cref="InvalidOperationException">Compose failed or printed something unexpected.</exception>
   public async Task<IReadOnlyList<ExitedContainer>> ExitedAsync( CancellationToken ct )
   {
      List<string> arguments = Arguments( _composePath, null, "ps", "-a", "--orphans=false", "--status", "exited", "--format", "{{.Name}}|{{.ExitCode}}" );
      ShellResult ps = await Shell.RunAsync( "sudo", arguments, QUERY_TIMEOUT, ct );
      if( ps.ExitCode != 0 )
      {
         throw new InvalidOperationException( $"docker compose ps said: {ComposeStart.Tail( ps.Error, 200 )}" );
      }

      return ComposeStart.ParseExited( ps.Output );
   }

   /// <summary>
   /// Reads the last lines of a container's log (both streams).
   /// </summary>
   /// <param name="container">Container name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The log text, or empty when it could not be read.</returns>
   public async Task<string> LogsAsync( string container, CancellationToken ct )
   {
      try
      {
         ShellResult logs = await Shell.RunAsync( "sudo", new[] { "-n", "docker", "logs", "--tail", LOG_LINES.ToString(), container }, QUERY_TIMEOUT, ct );
         return logs.Output + "\n" + logs.Error;
      }
      catch( Exception ex ) when( ex is TimeoutException or InvalidOperationException )
      {
         return string.Empty;
      }
   }

   /// <summary>
   /// The clean-up command as run, "docker compose down -v" or "docker compose down".
   /// </summary>
   public string RemoveDescription => _removeVolumes ? "docker compose down -v" : "docker compose down";

   /// <summary>
   /// Removes the containers with "down", and "-v" when allowed.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Null when removed, else what compose said.</returns>
   public async Task<string?> RemoveContainersAsync( CancellationToken ct )
   {
      ShellResult down = await Shell.RunAsync( "sudo", DownArguments( _composePath, _overridePath, _removeVolumes ), STOP_TIMEOUT, ct );
      return down.ExitCode == 0 ? null : $"compose down said: {ComposeStart.Tail( down.Error, 300 )}";
   }

   /// <summary>
   /// The arguments of the clean-up between attempts. Pure, so a test can prove that no path of a data
   /// folder is ever named and that "-v" appears only when it was allowed.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="overridePath">The CPU override file, or null.</param>
   /// <param name="removeVolumes">True to add "-v".</param>
   /// <returns>The arguments after "sudo".</returns>
   public static List<string> DownArguments( string composePath, string? overridePath, bool removeVolumes )
   {
      List<string> arguments = Arguments( composePath, overridePath, "down" );
      if( removeVolumes )
      {
         arguments.Add( "-v" );
      }

      return arguments;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// "-n docker compose -f file [-f override]" followed by the command's own arguments.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="overridePath">The CPU override file, or null.</param>
   /// <param name="command">The compose command and its arguments.</param>
   /// <returns>The full argument list after "sudo".</returns>
   private static List<string> Arguments( string composePath, string? overridePath, params string[] command )
   {
      var arguments = new List<string> { "-n", "docker", "compose", "-f", composePath };
      if( overridePath != null )
      {
         arguments.AddRange( new[] { "-f", overridePath } );
      }

      arguments.AddRange( command );
      return arguments;
   }

   #endregion Private Methods
}
