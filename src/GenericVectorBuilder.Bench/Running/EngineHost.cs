using GenericVectorBuilder.Bench.Targets;

namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// Starts, stops and checks an engine that docker compose hosts.
/// Why an interface: the rule "run-all stops only what it started" decides whether someone's
/// running engine gets shut down under them, so it is tested with a fake host instead of being
/// trusted untested.
/// </summary>
public interface IEngineHost
{
   /// <summary>
   /// True when at least one container of the compose file is running.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when running.</returns>
   Task<bool> IsRunningAsync( string composePath, CancellationToken ct );

   /// <summary>
   /// Starts the engine and returns once it is healthy. Throws with a plain message on failure.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   Task UpAsync( string composePath, CancellationToken ct );

   /// <summary>
   /// Stops the engine, keeping its data.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Null when stopped cleanly, else what went wrong.</returns>
   Task<string?> DownAsync( string composePath, CancellationToken ct );
}

/// <summary>
/// The real host: docker compose through <see cref="ComposeRunner"/>, which puts a time limit
/// on every call (5 minutes to check or stop, 20 to start).
/// </summary>
public sealed class ComposeEngineHost : IEngineHost
{
   #region Public Methods

   /// <summary>
   /// True when at least one container of the compose file is running.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when running.</returns>
   public Task<bool> IsRunningAsync( string composePath, CancellationToken ct )
   {
      return ComposeRunner.IsRunningAsync( composePath, ct );
   }

   /// <summary>
   /// Starts the engine and waits until compose reports it healthy.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   public Task UpAsync( string composePath, CancellationToken ct )
   {
      return ComposeRunner.UpAsync( composePath, ct );
   }

   /// <summary>
   /// Stops and removes the containers, keeping their data folders.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Null when stopped cleanly, else what compose said.</returns>
   public Task<string?> DownAsync( string composePath, CancellationToken ct )
   {
      return ComposeRunner.DownAsync( composePath, ct );
   }

   #endregion Public Methods
}
