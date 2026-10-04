using System.Globalization;
using System.Text.Json;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// One warning about an engine's numbers, as the summary shows it: a kind (the Bench tool's
/// flag name, e.g. "spread") and the evidence text the tool wrote for it.
/// </summary>
/// <param name="Kind">Flag kind, e.g. "spread" or "governor-not-performance".</param>
/// <param name="Detail">Evidence from the result files (values and run counts), never a cause.</param>
public sealed record BenchFlag( string Kind, string Detail );

/// <summary>
/// What each flag kind is called on the page and what it means in plain words, plus the two
/// flags a single run can show by itself (an unsettled engine, p50 against mean).
/// Why a table here: the marker next to an engine is two letters, so every code needs one fixed
/// sentence below the table that says what it means and why it matters.
/// </summary>
public static class BenchFlagInfo
{
   #region Data Members

   /// <summary>p50 above the mean latency by more than this ratio is flagged (same value as the Bench tool's ConsolidateFlags).</summary>
   public const double P50_ABOVE_MEAN_RATIO = 1.15;

   /// <summary>Mean above p50 by more than this ratio is flagged (same value as the Bench tool's ConsolidateFlags).</summary>
   public const double MEAN_ABOVE_P50_RATIO = 1.5;

   private static readonly (string Kind, string Code, string Meaning)[] KINDS =
   {
      ( "spread", "Sp", "Spread: the best and worst runs differ by more than 15%, in p50 or in searches per second, so one run's number does not repeat." ),
      ( "p50-mean-inconsistent", "Pm", "p50 and mean disagree: the median latency and the mean from the one-searcher pass differ by more than expected, which usually means the two passes ran under different conditions." ),
      ( "unsettled-target", "Un", "Unsettled: the engine was still starting, building or compacting when it was timed." ),
      ( "busy-box", "Bz", "Busy box: other work was using the CPUs when the run started, so latencies may be worse than the box can do." ),
      ( "governor-not-performance", "Gv", "CPU governor was not performance: the CPU may have run slower than its top speed, most of all with one searcher." ),
      ( "client-engine-share-cores", "Sh", "The test client and the engine ran on the same CPU cores and may have slowed each other." ),
      ( "not-release-build", "Db", "The test client was a Debug build, which can add client time to every search." ),
      ( "index-not-ready-after-load", "Ix", "The engine did not report a finished index after the load or after the searches, so the search may have been a scan or a half-built index." ),
      ( "index-not-ready-after-search", "Ix", "The engine did not report a finished index after the load or after the searches, so the search may have been a scan or a half-built index." ),
      ( "durability-not-stated", "Du", "What a crash can lose with this engine's settings was not stated." ),
      ( "warmup-errors", "Er", "Warm-up or timed searches failed." ),
      ( "search-errors", "Er", "Warm-up or timed searches failed." ),
      ( "exact-recall-below-1", "Ex", "The engine's exact mode did not return the exact answers." ),
      ( "fields-missing", "Ms", "Fields missing: the run did not record some of what is needed to judge it (see consolidated.md)." ),
      ( "engine-or-index-text-differs", "Ch", "The engine version or index description changed between the runs." ),
   };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The two-letter marker for a flag kind; "??" for a kind this page does not know, so a new
   /// flag from the Bench tool still shows up.
   /// </summary>
   /// <param name="kind">Flag kind.</param>
   /// <returns>The code.</returns>
   public static string Code( string kind )
   {
      return KINDS.FirstOrDefault( k => k.Kind == kind ).Code ?? "??";
   }

   /// <summary>
   /// The plain-words meaning of a flag kind; the kind itself when unknown.
   /// </summary>
   /// <param name="kind">Flag kind.</param>
   /// <returns>One sentence.</returns>
   public static string Meaning( string kind )
   {
      return KINDS.FirstOrDefault( k => k.Kind == kind ).Meaning ?? $"Flag \"{kind}\" (see consolidated.md)";
   }

   /// <summary>
   /// Where a kind sits in the legend: the order of the table above, unknown kinds last.
   /// </summary>
   /// <param name="kind">Flag kind.</param>
   /// <returns>The position.</returns>
   public static int Order( string kind )
   {
      int index = Array.FindIndex( KINDS, k => k.Kind == kind );
      return index < 0 ? int.MaxValue : index;
   }

   /// <summary>
   /// The flags one run's results.json shows for a target by itself: an engine recorded as not
   /// settled, and a p50 that disagrees with the mean implied by the one-searcher QPS. The rest
   /// (spread, busy box, governor, shared cores) need several runs or the run's conditions, and
   /// come from consolidated.json or the conditions line.
   /// </summary>
   /// <param name="target">One entry of results.json's targets.</param>
   /// <returns>The flags, possibly none.</returns>
   public static IReadOnlyList<BenchFlag> FromTarget( JsonElement target )
   {
      var flags = new List<BenchFlag>();
      JsonElement search = Child( target, "search" ) ?? default;
      bool? settled = Bool( target, "settled" ) ?? Bool( search, "settled" );
      string? warning = settled == null ? UnsettledNote( target ) : null;
      if( settled == false || warning != null )
      {
         flags.Add( new BenchFlag( "unsettled-target", warning ?? "not settled" ) );
      }

      double? p50 = Number( search, "p50Ms" );
      double? mean = Number( search, "meanMs" ) ?? ( Child( search, "qps" ) is JsonElement qps && Number( qps, "1" ) is double one && one > 0 ? 1000.0 / one : null );
      if( p50 is double p && mean is double m && p > 0 && m > 0 && ( p / m > P50_ABOVE_MEAN_RATIO || m / p > MEAN_ABOVE_P50_RATIO ) )
      {
         flags.Add( new BenchFlag( "p50-mean-inconsistent", $"p50 {p.ToString( "0.00", CultureInfo.InvariantCulture )} ms, mean {m.ToString( "0.00", CultureInfo.InvariantCulture )} ms, p50/mean {( p / m ).ToString( "0.00", CultureInfo.InvariantCulture )}" ) );
      }

      return flags;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The measurement step's own warning that the engine had not settled ("WARNING: latency had
   /// NOT settled ..." or "no settle ran"), from the target's notes; null when there is none.
   /// </summary>
   /// <param name="target">The target object.</param>
   /// <returns>The warning without its "WARNING: " prefix, or null.</returns>
   private static string? UnsettledNote( JsonElement target )
   {
      if( Child( target, "notes" ) is not { ValueKind: JsonValueKind.Array } notes )
      {
         return null;
      }

      string? warning = notes.EnumerateArray().Where( n => n.ValueKind == JsonValueKind.String ).Select( n => n.GetString()! )
         .FirstOrDefault( n => n.Contains( "NOT settled", StringComparison.Ordinal ) || n.Contains( "no settle ran", StringComparison.Ordinal ) );
      return warning != null && warning.StartsWith( "WARNING: ", StringComparison.Ordinal ) ? warning["WARNING: ".Length..] : warning;
   }

   /// <summary>
   /// A property of an object, ignoring case; null when absent or JSON null.
   /// </summary>
   /// <param name="parent">The object.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The value, or null.</returns>
   private static JsonElement? Child( JsonElement parent, string name )
   {
      if( parent.ValueKind != JsonValueKind.Object )
      {
         return null;
      }

      foreach( JsonProperty property in parent.EnumerateObject() )
      {
         if( string.Equals( property.Name, name, StringComparison.OrdinalIgnoreCase ) )
         {
            return property.Value.ValueKind == JsonValueKind.Null ? null : property.Value;
         }
      }

      return null;
   }

   /// <summary>
   /// A true/false property, or null.
   /// </summary>
   /// <param name="parent">The object.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The value, or null.</returns>
   private static bool? Bool( JsonElement parent, string name )
   {
      return Child( parent, name ) is JsonElement v && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
   }

   /// <summary>
   /// A finite number property, or null.
   /// </summary>
   /// <param name="parent">The object.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The value, or null.</returns>
   private static double? Number( JsonElement parent, string name )
   {
      return Child( parent, name ) is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble( out double d ) && double.IsFinite( d ) ? d : null;
   }

   #endregion Private Methods
}
