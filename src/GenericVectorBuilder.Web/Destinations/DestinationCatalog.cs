using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Web.Destinations;

/// <summary>
/// One place a run can write to, as the page lists it.
/// </summary>
/// <param name="Name">The sink name sent with a run or a search, e.g. "pgvector".</param>
/// <param name="DisplayName">What the page shows, e.g. "PostgreSQL 17 + pgvector 0.8.7".</param>
/// <param name="Optional">True for an engine container that is usually stopped; false for SQL Server and Qdrant.</param>
/// <param name="DefaultOn">True when the page ticks it to start with.</param>
/// <param name="Probe">Checks, without starting anything, that an optional destination is running; throws with a plain reason when not. Null for SQL Server and Qdrant, which the main health check covers, and for an engine nobody wrote a probe for.</param>
public sealed record Destination( string Name, string DisplayName, bool Optional, bool DefaultOn, Func<CancellationToken, Task>? Probe );

/// <summary>
/// Every destination the web app knows: SQL Server and Qdrant, then every engine sink.
/// Why SQL Server and Qdrant are not gated by a probe: they are always-on services, and when
/// one fails mid-run the pipeline already drops it and finishes the other, which the page
/// depends on. The engines are containers that are normally stopped, so a run or a search
/// naming one that is not running is refused up front with its name, instead of failing late.
/// Why each probe gets two seconds and they run side by side: the page asks on every load and
/// on every Go, and fourteen stopped engines must not hold either for long.
/// </summary>
public sealed class DestinationCatalog
{
   #region Data Members

   /// <summary>Longest one probe may take.</summary>
   public static readonly TimeSpan PROBE_TIMEOUT = TimeSpan.FromSeconds( 2 );

   /// <summary>The health text of a destination that answered.</summary>
   public const string OK = "ok";

   private const string NOT_RUNNING = "not running";
   private const string SQL = "sql";
   private const string QDRANT = "qdrant";

   private readonly List<Destination> _all;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the catalog.
   /// </summary>
   /// <param name="destinations">Every destination, in the order the page lists them.</param>
   public DestinationCatalog( IEnumerable<Destination> destinations )
   {
      _all = destinations.ToList();
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Every destination, SQL Server and Qdrant first.</summary>
   public IReadOnlyList<Destination> All => _all;

   /// <summary>The destinations ticked to start with: SQL Server and Qdrant.</summary>
   public IReadOnlyList<string> DefaultNames => _all.Where( d => d.DefaultOn ).Select( d => d.Name ).ToList();

   /// <summary>
   /// Builds the catalog from SQL Server, Qdrant and the engine sinks, each engine named by its
   /// <see cref="IEngineDescription.Engine"/> and checked by <see cref="EngineProbes"/>.
   /// </summary>
   /// <param name="engines">The engine sinks.</param>
   /// <returns>The catalog.</returns>
   public static DestinationCatalog Build( IEnumerable<ISink> engines )
   {
      var all = new List<Destination>
      {
         new( SQL, "SQL Server 2025 (VECTOR)", false, true, null ),
         new( QDRANT, "Qdrant", false, true, null ),
      };
      all.AddRange( engines.OrderBy( e => e.Name, StringComparer.Ordinal )
         .Select( e => new Destination( e.Name, ( e as IEngineDescription )?.Engine ?? e.Name, true, false, EngineProbes.For( e ) ) ) );
      return new DestinationCatalog( all );
   }

   /// <summary>
   /// Finds a destination by name, ignoring case.
   /// </summary>
   /// <param name="name">Sink name.</param>
   /// <returns>The destination, or null.</returns>
   public Destination? Find( string name )
   {
      return _all.FirstOrDefault( d => string.Equals( d.Name, name, StringComparison.OrdinalIgnoreCase ) );
   }

   /// <summary>
   /// Checks every optional destination at once, each within <see cref="PROBE_TIMEOUT"/>.
   /// Starts nothing.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>"ok" or "not running: reason", by destination name.</returns>
   public Task<IReadOnlyDictionary<string, string>> ProbeOptionalAsync( CancellationToken ct )
   {
      return ProbeAsync( _all.Where( d => d.Optional ), ct );
   }

   /// <summary>
   /// The named optional destinations that are not running right now, each with its reason.
   /// Names that are not optional destinations are left out (SQL Server and Qdrant are handled
   /// by the pipeline; unknown names by the caller).
   /// </summary>
   /// <param name="names">Sink names from a request.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Destination and reason for each one that is down; empty when all are running.</returns>
   public async Task<IReadOnlyList<( Destination Destination, string Reason )>> UnreachableAsync( IEnumerable<string> names, CancellationToken ct )
   {
      List<Destination> chosen = names.Select( Find ).Where( d => d is { Optional: true } ).Select( d => d! ).DistinctBy( d => d.Name ).ToList();
      IReadOnlyDictionary<string, string> health = await ProbeAsync( chosen, ct );
      return chosen.Where( d => health[d.Name] != OK ).Select( d => ( d, health[d.Name] ) ).ToList();
   }

   /// <summary>
   /// A plain sentence naming destinations that are not running, for a refused run or search.
   /// </summary>
   /// <param name="down">The destinations and their reasons.</param>
   /// <returns>E.g. "Not running: pgvector (PostgreSQL 17 + pgvector 0.8.7)."</returns>
   public static string DescribeDown( IReadOnlyList<( Destination Destination, string Reason )> down )
   {
      return "Not running: " + string.Join( ", ", down.Select( d => $"{d.Destination.Name} ({d.Destination.DisplayName})" ) ) + ".";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Probes destinations side by side, each with its own time limit.
   /// </summary>
   /// <param name="destinations">Destinations to probe.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Health text by name.</returns>
   private static async Task<IReadOnlyDictionary<string, string>> ProbeAsync( IEnumerable<Destination> destinations, CancellationToken ct )
   {
      List<Destination> list = destinations.ToList();
      string[] results = await Task.WhenAll( list.Select( d => ProbeOneAsync( d, ct ) ) );
      var health = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
      for( int i = 0; i < list.Count; i++ )
      {
         health[list[i].Name] = results[i];
      }

      return health;
   }

   /// <summary>
   /// Probes one destination within <see cref="PROBE_TIMEOUT"/>.
   /// </summary>
   /// <param name="destination">The destination.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>"ok", or "not running: reason".</returns>
   private static async Task<string> ProbeOneAsync( Destination destination, CancellationToken ct )
   {
      if( destination.Probe == null )
      {
         return $"{NOT_RUNNING}: no health check is defined for this destination, so it cannot be used";
      }

      using var limit = CancellationTokenSource.CreateLinkedTokenSource( ct );
      limit.CancelAfter( PROBE_TIMEOUT );
      try
      {
         await destination.Probe( limit.Token ).WaitAsync( limit.Token );
         return OK;
      }
      catch( OperationCanceledException ) when( !ct.IsCancellationRequested )
      {
         return $"{NOT_RUNNING}: no answer within {PROBE_TIMEOUT.TotalSeconds:0} seconds";
      }
      catch( Exception ex ) when( ex is not OperationCanceledException )
      {
         return $"{NOT_RUNNING}: {ex.Message}";
      }
   }

   #endregion Private Methods
}
