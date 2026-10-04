namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// An engine host that pins a compose engine to the engine CPUs as soon as run-all has started
/// it. Why a wrapper around the host: run-all starts an engine inside the target's measurement,
/// right before loading it, and the load must already run on the engine CPUs; the host's
/// start call is the one moment the containers exist and nothing has used them yet.
/// </summary>
public sealed class PinningEngineHost : IEngineHost
{
   #region Data Members

   private readonly IEngineHost _inner;
   private readonly Func<string, CancellationToken, Task> _afterUp;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Wraps a host.
   /// </summary>
   /// <param name="inner">The real host.</param>
   /// <param name="afterUp">Called with the compose file after each successful start.</param>
   public PinningEngineHost( IEngineHost inner, Func<string, CancellationToken, Task> afterUp )
   {
      _inner = inner;
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
   /// Starts the engine, then pins it.
   /// </summary>
   /// <param name="composePath">Compose file.</param>
   /// <param name="ct">Cancellation.</param>
   public async Task UpAsync( string composePath, CancellationToken ct )
   {
      await _inner.UpAsync( composePath, ct );
      await _afterUp( composePath, ct );
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
