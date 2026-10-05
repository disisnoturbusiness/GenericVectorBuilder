using System.Text;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// Starts and stops one engine's containers with docker compose, for run-all.
/// Why an engine that was already running is left running: other work (another benchmark,
/// an engine's own tests) may be using it, and stopping it under them would break that work.
/// run-all only stops what it started itself. Data folders are never removed ("down" without
/// "-v"), matching deploy/engines/README.md.
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

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// True when at least one container of the compose file is running.
   /// Why "--orphans=false": four engine files (elasticsearch, opensearch, typesense, vespa) have no
   /// project name of their own and share the folder's, so without it compose 2.40 also lists the
   /// other three engines' containers and a stopped engine looks running whenever another one is.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when running.</returns>
   public static async Task<bool> IsRunningAsync( string composePath, CancellationToken ct )
   {
      string? ids = await Shell.TryOutputAsync( "sudo", new[] { "docker", "compose", "-f", composePath, "ps", "-q", "--status", "running", "--orphans=false" }, ct );
      return !string.IsNullOrWhiteSpace( ids );
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
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="cpuset">Kernel CPU list such as "2-3,6-7", or null to start without one (the compose file decides).</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>One line saying what was started on which CPUs, read back from Docker.</returns>
   /// <exception cref="ArgumentException">The cpuset is not a CPU list.</exception>
   /// <exception cref="InvalidOperationException">Compose failed, the engine never became healthy, or a container came up with another cpuset.</exception>
   public static async Task<string> UpAsync( string composePath, string? cpuset, CancellationToken ct )
   {
      if( cpuset != null && !CpuSetText.IsValid( cpuset ) )
      {
         throw new ArgumentException( $"'{cpuset}' is not a CPU list such as 2-3,6-7, so {Path.GetFileName( composePath )} was not started.", nameof( cpuset ) );
      }

      string? overridePath = cpuset == null ? null : await WriteOverrideAsync( composePath, cpuset, ct );
      try
      {
         var arguments = new List<string> { "-n", "docker", "compose", "-f", composePath };
         if( overridePath != null )
         {
            arguments.AddRange( new[] { "-f", overridePath } );
         }

         arguments.AddRange( new[] { "up", "-d", "--wait", "--wait-timeout", ( (int)START_TIMEOUT.TotalSeconds ).ToString() } );
         ShellResult result = await Shell.RunAsync( "sudo", arguments, START_TIMEOUT + TimeSpan.FromMinutes( 1 ), ct );
         if( result.ExitCode != 0 )
         {
            throw new InvalidOperationException( $"Could not start {Path.GetFileName( composePath )}: {Last( result.Error, 400 )}" );
         }
      }
      finally
      {
         DeleteQuietly( overridePath );
      }

      return await ReadBackAsync( composePath, cpuset, ct );
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
