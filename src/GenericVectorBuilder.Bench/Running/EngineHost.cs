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
   /// Starts the engine and returns once it is healthy, with every container created on
   /// <paramref name="cpuset"/> when the host can do that. Throws with a plain message on failure.
   /// Why at creation: an engine sizes its thread pools from the CPUs it sees when it starts
   /// (Elasticsearch read 8 processors when started unpinned and 4 when started pinned, review
   /// probe of 2026-10-04), so moving it with "docker update" afterwards leaves it with pools sized
   /// for twice the CPUs it then runs on.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="cpuset">Kernel CPU list such as "2-3,6-7" to create the containers on, or null for none.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>What was started on which CPUs, read back from Docker, or null when the host did not apply a CPU set (none asked, or it cannot).</returns>
   Task<string?> UpAsync( string composePath, string? cpuset, CancellationToken ct );

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
/// on every call (5 minutes to check or stop, 20 to start). Creating the containers on a CPU set
/// is the compose runner's job (a generated compose override, read back from Docker); this host
/// hands it the CPU set.
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
   /// Starts the engine with every container created on the CPU set (when given), waits until
   /// compose reports it healthy, and returns what Docker says each container got.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="cpuset">CPU list, or null to start without one (the compose file decides).</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The compose runner's read-back line when a CPU set was given, else null.</returns>
   /// <exception cref="ArgumentException">The CPU set is not a CPU list.</exception>
   /// <exception cref="InvalidOperationException">Compose failed, the engine never became healthy, or a container came up on other CPUs.</exception>
   public async Task<string?> UpAsync( string composePath, string? cpuset, CancellationToken ct )
   {
      if( cpuset == null )
      {
         await ComposeRunner.UpAsync( composePath, ct );
         return null;
      }

      return await ComposeRunner.UpAsync( composePath, cpuset, ct );
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
