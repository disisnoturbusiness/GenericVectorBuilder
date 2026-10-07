using System.Globalization;
using System.Text.Json;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// The flags one run's results.json shows for a target by itself, as the same flag codes the consolidated
/// page uses (see <see cref="BenchLegends"/>): an engine recorded as not settled, a p50 that disagrees with
/// the mean implied by the one-searcher QPS, and the per-pass records of the clock and of CPUs busy outside
/// the benchmark.
/// Why only these: the rest (spread, differences between runs) need several runs and come from a
/// consolidated file. Every flag is read from a recorded field; none is inferred.
/// </summary>
public static class BenchRunFlags
{
   #region Data Members

   /// <summary>p50 above the mean latency by more than this ratio is flagged (same value as the Bench tool's ConsolidateFlags).</summary>
   public const double P50_ABOVE_MEAN_RATIO = 1.15;

   /// <summary>Mean above p50 by more than this ratio is flagged (same value as the Bench tool's ConsolidateFlags).</summary>
   public const double MEAN_ABOVE_P50_RATIO = 1.5;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The flags of one target in a run: from its own record and from the run's per-pass records.
   /// </summary>
   /// <param name="target">One entry of results.json's targets.</param>
   /// <param name="passes">The run's conditions.passes array, or default when the run has none.</param>
   /// <returns>The flags, possibly none.</returns>
   public static IReadOnlyList<BenchFlagEntry> FromTarget( JsonElement target, JsonElement passes )
   {
      var flags = new List<BenchFlagEntry>();
      JsonElement search = Child( target, "search" ) ?? default;
      bool? settled = Bool( target, "settled" ) ?? Bool( search, "settled" );
      string? warning = settled == null ? UnsettledNote( target ) : null;
      if( settled == false || warning != null )
      {
         flags.Add( new BenchFlagEntry( "unsettled-target", warning ?? "not settled" ) );
      }

      double? p50 = Number( search, "p50Ms" );
      double? mean = Number( search, "meanMs" ) ?? ( Child( search, "qps" ) is JsonElement qps && Number( qps, "1" ) is double one && one > 0 ? 1000.0 / one : null );
      if( p50 is double p && mean is double m && p > 0 && m > 0 && ( p / m > P50_ABOVE_MEAN_RATIO || m / p > MEAN_ABOVE_P50_RATIO ) )
      {
         flags.Add( new BenchFlagEntry( "p50-mean-inconsistent", $"p50 {p.ToString( "0.00", CultureInfo.InvariantCulture )} ms, mean {m.ToString( "0.00", CultureInfo.InvariantCulture )} ms, p50/mean {( p / m ).ToString( "0.00", CultureInfo.InvariantCulture )}" ) );
      }

      flags.AddRange( FromPasses( Text( target, "name" ), passes ) );
      return flags;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The flags the run's per-pass records give for one target: a pass whose clock was off its pinned value,
   /// a pass whose clock was not read, and a pass recorded as run on a busy box.
   /// </summary>
   /// <param name="name">The target's name.</param>
   /// <param name="passes">The conditions.passes array.</param>
   /// <returns>The flags, one per pass and code.</returns>
   private static IEnumerable<BenchFlagEntry> FromPasses( string? name, JsonElement passes )
   {
      if( name == null || passes.ValueKind != JsonValueKind.Array )
      {
         yield break;
      }

      foreach( JsonElement pass in passes.EnumerateArray().Where( p => p.ValueKind == JsonValueKind.Object && Text( p, "target" ) == name ) )
      {
         string label = Text( pass, "pass" ) ?? "pass";
         if( Bool( pass, "clockOff" ) == true )
         {
            yield return new BenchFlagEntry( "clock-off", $"{label}: engine CPUs median {Mhz( pass, "engineMhzMedian" )}, client CPUs median {Mhz( pass, "clientMhzMedian" )}" );
         }

         if( Bool( pass, "clockRead" ) == false )
         {
            yield return new BenchFlagEntry( "clock-not-read", label );
         }

         if( Bool( pass, "busyBox" ) == true )
         {
            yield return new BenchFlagEntry( "busy-box", $"{label}: CPUs busy outside the benchmark {( Number( pass, "outsideLoadDuring" ) is double load ? load.ToString( "0.###", CultureInfo.InvariantCulture ) : "not recorded" )}" );
         }
      }
   }

   /// <summary>
   /// A recorded MHz figure as text, or "not recorded".
   /// </summary>
   /// <param name="pass">The pass record.</param>
   /// <param name="name">The field.</param>
   /// <returns>The text.</returns>
   private static string Mhz( JsonElement pass, string name )
   {
      return Number( pass, name ) is double mhz ? mhz.ToString( "0.###", CultureInfo.InvariantCulture ) + " MHz" : "not recorded";
   }

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
   /// A string property, or null.
   /// </summary>
   /// <param name="parent">The object.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The text, or null.</returns>
   private static string? Text( JsonElement parent, string name )
   {
      return Child( parent, name ) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
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
