namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// Starts and stops one engine's containers with docker compose, for run-all.
/// Why an engine that was already running is left running: other work (another benchmark,
/// an engine's own tests) may be using it, and stopping it under them would break that work.
/// run-all only stops what it started itself. Data folders are never removed ("down" without
/// "-v"), matching deploy/engines/README.md.
/// </summary>
public static class ComposeRunner
{
   #region Data Members

   private static readonly TimeSpan START_TIMEOUT = TimeSpan.FromMinutes( 20 );
   private static readonly TimeSpan STOP_TIMEOUT = TimeSpan.FromMinutes( 5 );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// True when at least one container of the compose file is running.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when running.</returns>
   public static async Task<bool> IsRunningAsync( string composePath, CancellationToken ct )
   {
      string? ids = await Shell.TryOutputAsync( "sudo", new[] { "docker", "compose", "-f", composePath, "ps", "-q", "--status", "running" }, ct );
      return !string.IsNullOrWhiteSpace( ids );
   }

   /// <summary>
   /// Starts the containers and waits until compose reports them healthy (or running, for a
   /// service without a health check).
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   /// <exception cref="InvalidOperationException">Compose failed or the engine never became healthy.</exception>
   public static async Task UpAsync( string composePath, CancellationToken ct )
   {
      string[] arguments = { "docker", "compose", "-f", composePath, "up", "-d", "--wait", "--wait-timeout", ( (int)START_TIMEOUT.TotalSeconds ).ToString() };
      ShellResult result = await Shell.RunAsync( "sudo", arguments, START_TIMEOUT + TimeSpan.FromMinutes( 1 ), ct );
      if( result.ExitCode != 0 )
      {
         throw new InvalidOperationException( $"Could not start {Path.GetFileName( composePath )}: {Last( result.Error, 400 )}" );
      }
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

   #endregion Public Methods

   #region Private Methods

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
