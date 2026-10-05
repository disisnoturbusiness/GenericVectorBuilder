namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// An engine host that records a pinned start in the state file before it happens, then pins
/// (or confirms the pin of) a compose engine as soon as run-all has started it. Why a wrapper
/// around the host: run-all starts an engine inside the target's measurement, right before
/// loading it, and the load must already run on the engine CPUs; the host's start call is the
/// one moment the containers exist and nothing has used them yet.
/// </summary>
public sealed class PinningEngineHost : IEngineHost
{
   #region Data Members

   private readonly IEngineHost _inner;
   private readonly Action<string, string?> _beforeUp;
   private readonly Func<string, CancellationToken, Task> _afterUp;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Wraps a host.
   /// </summary>
   /// <param name="inner">The real host.</param>
   /// <param name="beforeUp">Called with the compose file and CPU set before each start (records it).</param>
   /// <param name="afterUp">Called with the compose file after each successful start.</param>
   public PinningEngineHost( IEngineHost inner, Action<string, string?> beforeUp, Func<string, CancellationToken, Task> afterUp )
   {
      _inner = inner;
      _beforeUp = beforeUp;
      _afterUp = afterUp;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// True when at least one container of the compose file is running.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>True when running.</returns>
   public Task<bool> IsRunningAsync( string composePath, CancellationToken ct )
   {
      return _inner.IsRunningAsync( composePath, ct );
   }

   /// <summary>
   /// Records the start, starts the engine (on the CPU set, when given and the host can), then
   /// pins it (a container already on the engine CPUs is left as it is).
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="cpuset">CPUs to create the containers on, or null.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>What the host reported about the start, or null.</returns>
   public async Task<string?> UpAsync( string composePath, string? cpuset, CancellationToken ct )
   {
      _beforeUp( composePath, cpuset );
      string? started = await _inner.UpAsync( composePath, cpuset, ct );
      await _afterUp( composePath, ct );
      return started;
   }

   /// <summary>
   /// Stops the engine (its containers are removed, so their pins need no undoing).
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Null when stopped cleanly, else what went wrong.</returns>
   public Task<string?> DownAsync( string composePath, CancellationToken ct )
   {
      return _inner.DownAsync( composePath, ct );
   }

   #endregion Public Methods
}
