using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// One thing the benchmark measures: a sink plus how it is hosted and how to read its memory
/// and disk use.
/// Why memory and disk are functions and not numbers: they are read after the load, and each
/// kind of host answers differently (docker stats for a container, a DMV for SQL Server, the
/// process for Qdrant, nothing for an engine running inside this process).
/// </summary>
public sealed class BenchTarget
{
   #region Data Members

   private readonly Func<string, CancellationToken, Task<Measurement>> _ram;
   private readonly Func<string, CancellationToken, Task<Measurement>> _disk;
   private const string NOT_STATED = "not stated";

   private readonly string _fallbackIndex;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a target.
   /// </summary>
   /// <param name="sink">The sink under test.</param>
   /// <param name="engine">Engine name and version for the report.</param>
   /// <param name="index">Index description, used when the sink does not describe itself.</param>
   /// <param name="hosting">"compose", "always-on" or "embedded".</param>
   /// <param name="composePath">Absolute compose file path when hosted by compose, else null.</param>
   /// <param name="ram">Reads memory use for a collection.</param>
   /// <param name="disk">Reads disk use for a collection.</param>
   public BenchTarget( ISink sink, string engine, string index, string hosting, string? composePath,
      Func<string, CancellationToken, Task<Measurement>> ram, Func<string, CancellationToken, Task<Measurement>> disk )
   {
      Sink = sink;
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
   public string Name => Sink.Name;

   /// <summary>The sink under test.</summary>
   public ISink Sink { get; }

   /// <summary>Engine name and version.</summary>
   public string Engine { get; }

   /// <summary>Index description, read live so settings learned during the load (build parameters) show.</summary>
   public string Index => Sink is IEngineDescription description ? description.IndexDescription : _fallbackIndex;

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
         string? stated = ( Sink as IEngineDescription )?.Durability;
         return !string.IsNullOrWhiteSpace( stated ) && stated != NOT_STATED ? stated : DurabilityNote ?? NOT_STATED;
      }
   }

   /// <summary>True when the sink builds or waits for its index in a separate, timed step after loading.</summary>
   public bool HasIndexFinisher => Sink is IIndexFinisher;

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
      Func<string, CancellationToken, Task<IndexState>>? read = Sink is IIndexFinisher finisher ? finisher.GetIndexStateAsync : IndexStateReader;
      if( read == null )
      {
         return new IndexState( false, null, null, "not reported: this target gives no index state" );
      }

      using var limit = CancellationTokenSource.CreateLinkedTokenSource( ct );
      limit.CancelAfter( timeout );
      try
      {
         return await read( collection, limit.Token );
      }
      catch( Exception ex ) when( !ct.IsCancellationRequested )
      {
         string why = ex is OperationCanceledException ? $"no answer within {timeout.TotalSeconds:0} s" : ex.Message;
         return new IndexState( false, null, null, $"could not read the index state: {why}" );
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
}

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
