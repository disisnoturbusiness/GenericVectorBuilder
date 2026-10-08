using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// The flags one run's results.json shows for a target by itself, as the same flag codes the consolidated
/// page uses (see <see cref="BenchLegends"/>): an engine recorded as not settled, an index its engine reported
/// as not ready after the load or after the searches, a timed pass its run recorded as not held, failed searches and warm-up searches, a rise of the
/// thermal throttle counters during the engine's turn, a p50 that disagrees with the mean implied by the
/// one-searcher QPS, and the per-pass records of the clock, of the governor and of CPUs busy outside the benchmark.
/// Why these: the consolidated page raises each of them from the same recorded fields, and a flag the summary
/// shows for an engine must show on the page of the run it came from. The rest (differences between runs, such
/// as the segment layout) need several runs and come from a consolidated file.
/// Every flag is read from a recorded field. The one exception is the p50-against-mean flag, whose mean is the
/// one-searcher pass's own searches per second turned into milliseconds; its evidence names both limits it is
/// raised at, so the reader can see the rule it rests on.
/// </summary>
public static class BenchRunFlags
{
   #region Data Members

   /// <summary>p50 above the mean latency by more than this ratio is flagged. The consolidate command no longer raises this flag; the run page keeps it, with both limits in its evidence.</summary>
   public const double P50_ABOVE_MEAN_RATIO = 1.15;

   /// <summary>Mean above p50 by more than this ratio is flagged. The consolidate command no longer raises this flag; the run page keeps it, with both limits in its evidence.</summary>
   public const double MEAN_ABOVE_P50_RATIO = 1.5;

   /// <summary>The words the settle warning of a target uses to record a timed pass as not held.</summary>
   public const string NOT_HELD_MARK = "NOT HELD";

   private static readonly TimeSpan MATCH_TIMEOUT = TimeSpan.FromSeconds( 5 );
   private static readonly Regex PASS_START = new( @"(?<![A-Za-z0-9@])(?<pass>default@\d+|exact): warm-up", RegexOptions.Compiled | RegexOptions.CultureInvariant, MATCH_TIMEOUT );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The flags of one target in a run: from its own record and from the run's per-pass records.
   /// </summary>
   /// <param name="target">One entry of results.json's targets.</param>
   /// <param name="passes">The run's conditions.passes array, or default when the run has none.</param>
   /// <param name="conditions">The run's conditions object (for the throttle counters), or default when the run has none.</param>
   /// <returns>The flags, possibly none.</returns>
   public static IReadOnlyList<BenchFlagEntry> FromTarget( JsonElement target, JsonElement passes, JsonElement conditions = default )
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
         flags.Add( new BenchFlagEntry( "p50-mean-inconsistent", $"p50 {p.ToString( "0.00", CultureInfo.InvariantCulture )} ms, mean {m.ToString( "0.00", CultureInfo.InvariantCulture )} ms, p50/mean {( p / m ).ToString( "0.00", CultureInfo.InvariantCulture )}; "
            + $"limits: p50 above the mean by more than {P50_ABOVE_MEAN_RATIO.ToString( "0.##", CultureInfo.InvariantCulture )} times, or the mean above p50 by more than {MEAN_ABOVE_P50_RATIO.ToString( "0.##", CultureInfo.InvariantCulture )} times" ) );
      }

      flags.AddRange( FromNotHeld( target ) );
      flags.AddRange( FromIndexState( target ) );
      flags.AddRange( FromErrors( target, search ) );
      flags.AddRange( FromThrottle( Text( target, "name" ), conditions ) );
      flags.AddRange( FromPasses( Text( target, "name" ), passes ) );
      return flags;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The not-held flag: one per timed pass that the target's settle warning records as NOT HELD (the engine was still changing when the pass was timed),
   /// with the pass's name and the warning's clause for it, from "the timed pass" to "NOT HELD". The same flag code the consolidated page uses, read from
   /// the same recorded note, so a pass the summary shows as not held shows so on the page of its run. A mark the page cannot place in a pass or a clause
   /// is flagged all the same, with the words around it, and never dropped.
   /// </summary>
   /// <param name="target">The target object.</param>
   /// <returns>The flags, one per pass marked.</returns>
   private static IEnumerable<BenchFlagEntry> FromNotHeld( JsonElement target )
   {
      if( UnsettledNote( target ) is not { } warning )
      {
         yield break;
      }

      List<Match> starts = PASS_START.Matches( warning ).ToList();
      for( int at = warning.IndexOf( NOT_HELD_MARK, StringComparison.Ordinal ); at >= 0; at = warning.IndexOf( NOT_HELD_MARK, at + NOT_HELD_MARK.Length, StringComparison.Ordinal ) )
      {
         string pass = starts.LastOrDefault( m => m.Index < at )?.Groups["pass"].Value ?? "pass not named";
         int from = warning.LastIndexOf( "the timed pass ", at, StringComparison.Ordinal );
         string clause = from >= 0 ? warning[from..( at + NOT_HELD_MARK.Length )] : warning[Math.Max( 0, at - 120 )..Math.Min( warning.Length, at + NOT_HELD_MARK.Length )];
         yield return new BenchFlagEntry( BenchRow.NOT_HELD, $"{pass}: {clause}" );
      }
   }

   /// <summary>
   /// The index-not-ready flag: one per snapshot (after the load, after the searches) in which the engine reported its index not ready, with the vector counts
   /// it recorded. A snapshot with no "ready" field says nothing and raises none.
   /// </summary>
   /// <param name="target">The target object.</param>
   /// <returns>The flags, one per snapshot not ready.</returns>
   private static IEnumerable<BenchFlagEntry> FromIndexState( JsonElement target )
   {
      if( Child( target, "indexState" ) is not JsonElement state )
      {
         yield break;
      }

      foreach( string name in new[] { "afterLoad", "afterSearch" } )
      {
         if( Child( state, name ) is JsonElement snapshot && Bool( snapshot, "ready" ) == false )
         {
            string counts = Number( snapshot, "indexedVectors" ) is double indexed && Number( snapshot, "totalVectors" ) is double total
               ? $"{indexed.ToString( "0.###", CultureInfo.InvariantCulture )} of {total.ToString( "0.###", CultureInfo.InvariantCulture )} vectors indexed"
               : "vector counts not recorded";
            yield return new BenchFlagEntry( "index-not-ready", $"{name}: the engine's index state read not ready, {counts}" );
         }
      }
   }

   /// <summary>
   /// The flags for failed searches (search.errors) and failed warm-up searches (warmupErrors), each when the recorded count is above zero.
   /// </summary>
   /// <param name="target">The target object.</param>
   /// <param name="search">The target's search object.</param>
   /// <returns>The flags.</returns>
   private static IEnumerable<BenchFlagEntry> FromErrors( JsonElement target, JsonElement search )
   {
      if( Number( search, "errors" ) is double errors and > 0 )
      {
         yield return new BenchFlagEntry( "search-errors", $"search errors {errors.ToString( "0.###", CultureInfo.InvariantCulture )}" );
      }

      if( Number( target, "warmupErrors" ) is double warmup and > 0 )
      {
         yield return new BenchFlagEntry( "warmup-errors", $"warm-up errors {warmup.ToString( "0.###", CultureInfo.InvariantCulture )}" );
      }
   }

   /// <summary>
   /// The throttle-rise flag: the run's conditions.throttleByTarget entry for the target, read at the start and at the end of the target's turn, shows a rise
   /// of the core or the package counter.
   /// </summary>
   /// <param name="name">The target's name.</param>
   /// <param name="conditions">The run's conditions object, or default.</param>
   /// <returns>The flag, or none.</returns>
   private static IEnumerable<BenchFlagEntry> FromThrottle( string? name, JsonElement conditions )
   {
      if( name == null || Child( conditions, "throttleByTarget" ) is not JsonElement byTarget || Child( byTarget, name ) is not JsonElement rise )
      {
         yield break;
      }

      double core = Number( rise, "coreRise" ) ?? 0;
      double package = Number( rise, "packageRise" ) ?? 0;
      if( core > 0 || package > 0 )
      {
         yield return new BenchFlagEntry( "throttle-rise", $"coreRise {core.ToString( "0.###", CultureInfo.InvariantCulture )}, packageRise {package.ToString( "0.###", CultureInfo.InvariantCulture )}" );
      }
   }

   /// <summary>
   /// The flags the run's per-pass records give for one target: a pass whose clock was off its pinned value,
   /// a pass whose clock was not read, a pass under a governor other than performance, and a pass recorded as run on a busy box.
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

         if( Text( pass, "governor" ) is { } governor && !string.Equals( governor.Trim(), BenchConditions.GOVERNOR_TARGET, StringComparison.OrdinalIgnoreCase ) )
         {
            yield return new BenchFlagEntry( "governor-not-performance", $"{label}: governor {governor}" );
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
