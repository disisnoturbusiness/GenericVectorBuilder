namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// Asks Docker about one container. An interface so the address rules are tested with
/// captured "docker inspect" text and without Docker.
/// </summary>
public interface IContainerInspector
{
   /// <summary>
   /// Returns the text "docker inspect" prints for a container.
   /// </summary>
   /// <param name="container">Container name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The JSON text, or null when no container of that name exists.</returns>
   /// <exception cref="InvalidOperationException">Docker could not be asked (not null: a missing container is null, anything else is a failure).</exception>
   Task<string?> InspectAsync( string container, CancellationToken ct );
}

/// <summary>
/// The real inspector: "sudo -n docker inspect", the same way every other docker call in the
/// benchmark is made (see <see cref="ComposeRunner"/>), with a time limit.
/// Why "-n": sudo must never wait for a password inside a benchmark; a box without passwordless
/// sudo gets a plain failure instead of a hang.
/// </summary>
public sealed class DockerInspector : IContainerInspector
{
   #region Data Members

   private static readonly TimeSpan INSPECT_TIMEOUT = TimeSpan.FromSeconds( 30 );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Runs "docker inspect" for one container.
   /// </summary>
   /// <param name="container">Container name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The JSON text, or null when Docker says there is no such container.</returns>
   public async Task<string?> InspectAsync( string container, CancellationToken ct )
   {
      ShellResult result;
      try
      {
         result = await Shell.RunAsync( "sudo", new[] { "-n", "docker", "inspect", "--type", "container", container }, INSPECT_TIMEOUT, ct );
      }
      catch( Exception ex ) when( ex is TimeoutException or System.ComponentModel.Win32Exception )
      {
         throw new InvalidOperationException( $"Could not ask Docker about {container}: {ex.Message}" );
      }

      if( result.ExitCode == 0 )
      {
         return result.Output;
      }

      if( result.Error.Contains( "No such", StringComparison.OrdinalIgnoreCase ) )
      {
         return null;
      }

      throw new InvalidOperationException( $"docker inspect {container} failed with exit code {result.ExitCode}: {result.Error.Trim()}" );
   }

   #endregion Public Methods
}

/// <summary>
/// What the settings readers need from the outside world: run a Docker command, and fetch a web
/// page from an engine. An interface so every read is tested with recorded answers and without
/// Docker or an engine, and so a test can prove which command lines were run.
/// </summary>
public interface IEngineProbe
{
   /// <summary>
   /// Runs "sudo -n docker" with the given arguments within a time limit.
   /// </summary>
   /// <param name="arguments">The arguments after "docker", each passed as one argument (never glued into a shell string on the host).</param>
   /// <param name="timeout">Longest wait before the command is killed.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Exit code, standard output and standard error.</returns>
   Task<ShellResult> DockerAsync( IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct );

   /// <summary>
   /// Fetches a page with GET, from a new client that is closed again before this returns, so no
   /// connection to the engine outlives the read (the run records which connections the client
   /// holds after its passes, and a leftover keep-alive socket would show up there).
   /// </summary>
   /// <param name="url">The address.</param>
   /// <param name="timeout">Longest wait.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The response body.</returns>
   /// <exception cref="HttpRequestException">The engine did not answer with success.</exception>
   Task<string> HttpGetAsync( string url, TimeSpan timeout, CancellationToken ct );
}

/// <summary>
/// The real probe: "sudo -n docker" through <see cref="Shell"/> and a short-lived HttpClient.
/// Why "-n": sudo must never wait for a password inside a benchmark.
/// </summary>
public sealed class DockerEngineProbe : IEngineProbe
{
   #region Public Methods

   /// <summary>
   /// Runs "sudo -n docker" with the arguments.
   /// </summary>
   /// <param name="arguments">Arguments after "docker".</param>
   /// <param name="timeout">Longest wait.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The result.</returns>
   public Task<ShellResult> DockerAsync( IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct )
   {
      return Shell.RunAsync( "sudo", new[] { "-n", "docker" }.Concat( arguments ), timeout, ct );
   }

   /// <summary>
   /// GETs a page.
   /// </summary>
   /// <param name="url">The address.</param>
   /// <param name="timeout">Longest wait.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The body.</returns>
   public async Task<string> HttpGetAsync( string url, TimeSpan timeout, CancellationToken ct )
   {
      using var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.Zero };
      using var client = new HttpClient( handler ) { Timeout = timeout };
      using HttpResponseMessage answer = await client.GetAsync( url, ct );
      answer.EnsureSuccessStatusCode();
      return await answer.Content.ReadAsStringAsync( ct );
   }

   #endregion Public Methods
}
