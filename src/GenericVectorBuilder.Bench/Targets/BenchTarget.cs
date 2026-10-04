using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// One thing the benchmark measures: a sink plus how it is hosted and how to read its memory
/// and disk use.
/// Why memory and disk are functions and not numbers: they are read after the load, and each
/// kind of host answers differently (docker stats for a container, a DMV for SQL Server, the
/// process for Qdrant, nothing for an engine running inside this process).
/// Why a compose-hosted target builds its sink late: the sink must connect to the container's own
/// address on its Docker network, not to the host port that docker-proxy serves, and that address
/// exists only once the container runs (run-all starts it at the target's turn). Until then the
/// target answers questions about the engine (name, durability, whether it has an index step)
/// from the sink the factory created, which never connects; the first use of <see cref="Sink"/> after
/// the engine is up reads the address from Docker and builds the sink that does the work.
/// </summary>
public sealed class BenchTarget : IDisposable
{
   #region Data Members

   private readonly ISink _template;
   private readonly Func<string, CancellationToken, Task<Measurement>> _ram;
   private readonly Func<string, CancellationToken, Task<Measurement>> _disk;
   private const string NOT_STATED = "not stated";

   private readonly string _fallbackIndex;
   private readonly SemaphoreSlim _bindLock = new( 1, 1 );
   private volatile ISink? _bound;
   private IReadOnlyList<ContainerAddress> _connections = Array.Empty<ContainerAddress>();

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a target.
   /// </summary>
   /// <param name="sink">The sink under test. For a compose-hosted target (see <see cref="Container"/>) this one only answers
   /// questions about the engine and never connects; the sink that does the work is built by <see cref="BindAsync"/>.</param>
   /// <param name="engine">Engine name and version for the report.</param>
   /// <param name="index">Index description, used when the sink does not describe itself.</param>
   /// <param name="hosting">"compose", "always-on" or "embedded".</param>
   /// <param name="composePath">Absolute compose file path when hosted by compose, else null.</param>
   /// <param name="ram">Reads memory use for a collection.</param>
   /// <param name="disk">Reads disk use for a collection.</param>
   public BenchTarget( ISink sink, string engine, string index, string hosting, string? composePath,
      Func<string, CancellationToken, Task<Measurement>> ram, Func<string, CancellationToken, Task<Measurement>> disk )
   {
      _template = sink;
      Engine = engine;
      _fallbackIndex = index;
      Hosting = hosting;
      ComposePath = composePath;
      _ram = ram;
      _disk = disk;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Target name as typed in --targets.</summary>
   public string Name => _template.Name;

   /// <summary>
   /// The sink under test, ready to use. For a compose-hosted target the first call after the
   /// engine is up reads the container's address from Docker and builds the sink on it (waiting
   /// at most the binding's deadline); call <see cref="BindAsync"/> first to do that without blocking a thread.
   /// </summary>
   /// <exception cref="InvalidOperationException">A compose-hosted target whose container is missing, not running, or without an address.</exception>
   public ISink Sink => _bound ?? ( Container == null ? _template : Task.Run( () => BindAsync( CancellationToken.None ) ).GetAwaiter().GetResult() );

   /// <summary>
   /// How a compose-hosted target reaches its container, or null for a target that is not a
   /// container (always-on servers and embedded engines).
   /// </summary>
   public ContainerBinding? Container { get; init; }

   /// <summary>
   /// The container addresses the sink connects to, once <see cref="BindAsync"/> has run: one entry per
   /// container port in use, each with the container's id, its network, its address and port, and
   /// the published host port that was NOT used. Empty before binding and for a target that is not a
   /// container. This is what the report records so a number is tied to the route it was measured on.
   /// </summary>
   public IReadOnlyList<ContainerAddress> Connections => _connections;

   /// <summary>
   /// Which two passes the consolidated report should compare by default for this target, or null
   /// when the target has none. Why it travels with the target: only the target knows what a fair
   /// pair is (SQL Server's DiskANN default search against an exact scan of the SAME table).
   /// </summary>
   public PassPair? PairHint { get; init; }

   /// <summary>
   /// One line saying how the sink reaches its engine, for the log and the report's notes.
   /// </summary>
   public string ConnectionText
   {
      get
      {
         if( Container != null )
         {
            return _connections.Count == 0 ? "not connected yet: the container's address is read when the engine is first used" : string.Join( "; ", _connections.Select( c => c.Describe() ) );
         }

         return Hosting == "embedded" ? "in this process, no network" : DirectNote ?? "always-on service, not started by compose; the route to it was not checked";
      }
   }

   /// <summary>
   /// What to say in <see cref="ConnectionText"/> for a target that is not a container, when the
   /// factory knows (for example the address of the always-on SQL Server).
   /// </summary>
   public string? DirectNote { get; init; }

   /// <summary>Engine name and version.</summary>
   public string Engine { get; }

   /// <summary>Index description, read live so settings learned during the load (build parameters) show.</summary>
   public string Index => Current is IEngineDescription description ? description.IndexDescription : _fallbackIndex;

   /// <summary>"compose", "always-on" or "embedded".</summary>
   public string Hosting { get; }

   /// <summary>Compose file that starts the engine, or null when nothing needs starting.</summary>
   public string? ComposePath { get; }

   /// <summary>
   /// Optional wait after loading for a sink that is not an <see cref="IIndexFinisher"/> but
   /// keeps working in the background (the builder's Qdrant sink optimises after writes).
   /// Returns a note for the report. Null when the sink is idle once its writes return.
   /// </summary>
   public Func<string, CancellationToken, Task<string>>? Settle { get; init; }

   /// <summary>
   /// Optional reader of the engine's index state for a sink that is not an
   /// <see cref="IIndexFinisher"/> (for example the builder's own Qdrant sink, which the
   /// benchmark wraps rather than changes). Null when the sink reports its own state or none.
   /// </summary>
   public Func<string, CancellationToken, Task<IndexState>>? IndexStateReader { get; init; }

   /// <summary>
   /// Optional crash-safety statement for a sink that does not describe itself (the builder's
   /// SQL Server and Qdrant sinks). Used only when the sink's own
   /// <see cref="IEngineDescription.Durability"/> is missing or says "not stated".
   /// </summary>
   public string? DurabilityNote { get; init; }

   /// <summary>
   /// What a crash can lose with this engine's settings, as the sink states it, else
   /// <see cref="DurabilityNote"/>, else "not stated". Why it is printed with the numbers: an
   /// engine that skips fsync writes faster, and the reader must see that it does.
   /// </summary>
   public string Durability
   {
      get
      {
         string? stated = ( Current as IEngineDescription )?.Durability;
         return !string.IsNullOrWhiteSpace( stated ) && stated != NOT_STATED ? stated : DurabilityNote ?? NOT_STATED;
      }
   }

   /// <summary>True when the sink builds or waits for its index in a separate, timed step after loading.</summary>
   public bool HasIndexFinisher => _template is IIndexFinisher;

   /// <summary>
   /// Reads the engine's own account of its index, within <paramref name="timeout"/>. Never
   /// throws for an engine problem: a failed or slow read becomes a not-ready state whose
   /// detail says why, so the report shows the gap instead of the run stopping.
   /// Why not ready when nothing is reported: a number from an index nobody can prove was
   /// built must not look like one that was.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="timeout">Longest the engine gets to answer.</param>
   /// <param name="ct">Cancellation of the whole run.</param>
   /// <returns>The state.</returns>
   public async Task<IndexState> ReadIndexStateAsync( string collection, TimeSpan timeout, CancellationToken ct )
   {
      using var limit = CancellationTokenSource.CreateLinkedTokenSource( ct );
      limit.CancelAfter( timeout );
      try
      {
         ISink sink = await BindAsync( limit.Token );
         Func<string, CancellationToken, Task<IndexState>>? read = sink is IIndexFinisher finisher ? finisher.GetIndexStateAsync : IndexStateReader;
         if( read == null )
         {
            return new IndexState( false, null, null, "not reported: this target gives no index state" );
         }

         return await read( collection, limit.Token );
      }
      catch( Exception ex ) when( !ct.IsCancellationRequested )
      {
         string why = ex is OperationCanceledException ? $"no answer within {timeout.TotalSeconds:0} s" : ex.Message;
         return new IndexState( false, null, null, $"could not read the index state: {why}" );
      }
   }

   /// <summary>
   /// Connects the target to its engine. For a compose-hosted target: asks Docker for the
   /// container's address on its network and the port inside the container (waiting up to the
   /// binding's deadline for a container that is still coming up), builds the sink on that address
   /// and records it in <see cref="Connections"/>. Safe to call again and from several threads: the
   /// sink is built once. For any other target it returns the sink at once.
   /// Why it does not fall back to 127.0.0.1 when Docker cannot answer: that would route the run
   /// through docker-proxy without saying so, and every number from it would carry the extra hop.
   /// </summary>
   /// <param name="ct">Cancellation of the whole run.</param>
   /// <returns>The sink to measure.</returns>
   /// <exception cref="InvalidOperationException">The container is missing, not running, has no address, or does not publish the sink's port.</exception>
   public async Task<ISink> BindAsync( CancellationToken ct )
   {
      if( Container == null )
      {
         return _template;
      }

      if( _bound is ISink ready )
      {
         return ready;
      }

      await _bindLock.WaitAsync( ct );
      try
      {
         if( _bound is ISink built )
         {
            return built;
         }

         var router = await ContainerRouter.ResolveAsync( Container.Inspector, Container.Route.Containers, Container.Deadline, Container.Poll, ct );
         ISink sink = Container.Route.Build( router );
         _connections = router.Used.ToList();
         _bound = sink;
         return sink;
      }
      finally
      {
         _bindLock.Release();
      }
   }

   /// <summary>
   /// Reads memory use.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The reading.</returns>
   public Task<Measurement> MeasureRamAsync( string collection, CancellationToken ct )
   {
      return _ram( collection, ct );
   }

   /// <summary>
   /// Reads disk use.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The reading.</returns>
   public Task<Measurement> MeasureDiskAsync( string collection, CancellationToken ct )
   {
      return _disk( collection, ct );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>The sink answering questions about the engine: the bound one once there is one, else the factory's.</summary>
   private ISink Current => _bound ?? _template;

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Disposes the sink this target built itself. The factory's own sink is the factory's to dispose.
   /// </summary>
   public void Dispose()
   {
      ( _bound as IDisposable )?.Dispose();
      _bindLock.Dispose();
   }

   #endregion IDisposable
}

/// <summary>
/// How a compose-hosted target finds its container: which containers, how to ask Docker, and how
/// long to wait for them.
/// </summary>
/// <param name="Route">The engine's route (containers and sink builder).</param>
/// <param name="Inspector">Asks Docker about a container.</param>
/// <param name="Deadline">Longest to wait for every container to be running with an address.</param>
/// <param name="Poll">Pause between asks while waiting.</param>
public sealed record ContainerBinding( EngineRoute Route, IContainerInspector Inspector, TimeSpan Deadline, TimeSpan Poll );

/// <summary>
/// Two passes of one target that a consolidated report should compare by default.
/// </summary>
/// <param name="Target">Target name.</param>
/// <param name="PassA">First pass, as named in the run's passOrder, e.g. "default@1".</param>
/// <param name="PassB">Second pass, e.g. "exact".</param>
/// <param name="Note">What makes the pair fair, for the report.</param>
public sealed record PassPair( string Target, string PassA, string PassB, string Note );

/// <summary>
/// A memory or disk reading: bytes when known, and words saying what was measured (a whole
/// server's memory is not the same claim as one collection's files).
/// </summary>
/// <param name="Bytes">Bytes, or null when not measurable.</param>
/// <param name="Text">Human-readable value and scope, e.g. "1.2 GiB (docker stats)".</param>
public sealed record Measurement( long? Bytes, string Text )
{
   /// <summary>
   /// A reading that could not be taken.
   /// </summary>
   /// <param name="why">Why.</param>
   /// <returns>The reading.</returns>
   public static Measurement None( string why )
   {
      return new Measurement( null, why );
   }

   /// <summary>
   /// Formats bytes as KiB, MiB or GiB with three significant figures.
   /// </summary>
   /// <param name="bytes">Bytes.</param>
   /// <returns>The text.</returns>
   public static string Format( long bytes )
   {
      string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
      double value = bytes;
      int unit = 0;
      while( value >= 1024 && unit < units.Length - 1 )
      {
         value /= 1024;
         unit++;
      }

      return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
   }
}
