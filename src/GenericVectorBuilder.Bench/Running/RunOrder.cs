using System.Globalization;

namespace GenericVectorBuilder.Bench.Running;

/// <summary>
/// Decides the order a run measures things in: which target goes first, and inside each target
/// which timed pass goes first. Both orders are shuffled from one seed.
/// Why shuffle: with a fixed order the same query was faster when timed later in a run (Qdrant
/// 2.30 vs 0.87 ms, SQL Server 6.5 vs 4.5 ms), so a fixed order puts the same bias on the same
/// engine and the same pass every time. A random order spreads it, and several runs with
/// different seeds average it out.
/// Why a hand-written generator instead of <see cref="Random"/>: the seed is written to the
/// results so an order can be repeated later, and System.Random does not promise the same
/// sequence for a seed across .NET versions. SplitMix64 is twelve lines and never changes.
/// </summary>
public static class RunOrder
{
   #region Data Members

   /// <summary>Name of the pass that times the engine's own exact (brute force) mode.</summary>
   public const string EXACT_PASS = "exact";

   /// <summary>Prefix of a default-search pass; the number after it is the concurrency.</summary>
   public const string DEFAULT_PASS_PREFIX = "default@";

   private const ulong TARGET_SALT = 0x7461726765747321UL;
   private const ulong PASS_SALT = 0x7061737365732121UL;
   private const ulong FNV_OFFSET = 14695981039346656037UL;
   private const ulong FNV_PRIME = 1099511628211UL;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The seed used when --seed is not given: milliseconds of the start time, folded into a
   /// positive int. Why from the clock: every run gets a different order without anyone
   /// choosing one, and the value is recorded, so it is still repeatable.
   /// </summary>
   /// <param name="startedUtc">When the run started.</param>
   /// <returns>A seed of 0 or more.</returns>
   public static int SeedFromTime( DateTime startedUtc )
   {
      return (int)( startedUtc.Ticks / TimeSpan.TicksPerMillisecond % int.MaxValue );
   }

   /// <summary>
   /// The order the targets run in for a seed.
   /// </summary>
   /// <param name="names">Target names as requested.</param>
   /// <param name="runSeed">The run's seed.</param>
   /// <returns>The same names in a seeded random order.</returns>
   public static IReadOnlyList<string> ShuffleTargets( IReadOnlyList<string> names, int runSeed )
   {
      return Shuffle( names, Mix( (ulong)(uint)runSeed ^ TARGET_SALT ) );
   }

   /// <summary>
   /// Every timed pass a target gets, before shuffling: default search at concurrency 1 (the
   /// latency pass, which also scores recall, followed by throughput at 1 when 1 is a level),
   /// default search at every other concurrency level, and the exact mode when it exists.
   /// Why concurrency 1 is always a pass: latency and recall are measured one query at a time
   /// even when --concurrency leaves 1 out.
   /// </summary>
   /// <param name="concurrency">Concurrency levels from the command line.</param>
   /// <param name="hasExact">True when the target has an exact mode and it is timed.</param>
   /// <returns>Pass names, e.g. "default@1", "default@8", "exact".</returns>
   public static IReadOnlyList<string> Passes( IReadOnlyList<int> concurrency, bool hasExact )
   {
      var passes = new List<string> { DefaultPass( 1 ) };
      passes.AddRange( concurrency.Where( c => c != 1 ).Distinct().Select( DefaultPass ) );
      if( hasExact )
      {
         passes.Add( EXACT_PASS );
      }

      return passes;
   }

   /// <summary>
   /// The order of one target's passes for a seed. The target's name is mixed in, so targets
   /// in the same run get different orders, and a target keeps its order for a seed no matter
   /// which other targets are in the run.
   /// </summary>
   /// <param name="passes">Passes from <see cref="Passes"/>.</param>
   /// <param name="runSeed">The run's seed.</param>
   /// <param name="targetName">Target name (case does not matter).</param>
   /// <returns>The passes in a seeded random order.</returns>
   public static IReadOnlyList<string> ShufflePasses( IReadOnlyList<string> passes, int runSeed, string targetName )
   {
      return Shuffle( passes, Mix( (ulong)(uint)runSeed ^ PASS_SALT ^ StableHash( targetName.ToLowerInvariant() ) ) );
   }

   /// <summary>
   /// The name of a default-search pass.
   /// </summary>
   /// <param name="concurrency">Searches in flight at once.</param>
   /// <returns>e.g. "default@8".</returns>
   public static string DefaultPass( int concurrency )
   {
      return DEFAULT_PASS_PREFIX + concurrency.ToString( CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// Reads the concurrency out of a default-search pass name.
   /// </summary>
   /// <param name="pass">Pass name.</param>
   /// <param name="concurrency">The concurrency, when it is a default pass.</param>
   /// <returns>True for a default-search pass.</returns>
   public static bool TryParseDefault( string pass, out int concurrency )
   {
      concurrency = 0;
      return pass.StartsWith( DEFAULT_PASS_PREFIX, StringComparison.Ordinal )
         && int.TryParse( pass[DEFAULT_PASS_PREFIX.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out concurrency )
         && concurrency > 0;
   }

   /// <summary>
   /// Fisher-Yates shuffle driven by SplitMix64.
   /// </summary>
   /// <param name="items">Items to order.</param>
   /// <param name="state">Generator state (already mixed from the seed).</param>
   /// <returns>A shuffled copy; the input is not changed.</returns>
   public static IReadOnlyList<string> Shuffle( IReadOnlyList<string> items, ulong state )
   {
      string[] order = items.ToArray();
      for( int i = order.Length - 1; i > 0; i-- )
      {
         state += 0x9E3779B97F4A7C15UL;
         int j = (int)( Mix( state ) % (ulong)( i + 1 ) );
         ( order[i], order[j] ) = ( order[j], order[i] );
      }

      return order;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The SplitMix64 finaliser: spreads every input bit over every output bit.
   /// </summary>
   /// <param name="value">Input.</param>
   /// <returns>Mixed value.</returns>
   private static ulong Mix( ulong value )
   {
      value = ( value ^ ( value >> 30 ) ) * 0xBF58476D1CE4E5B9UL;
      value = ( value ^ ( value >> 27 ) ) * 0x94D049BB133111EBUL;
      return value ^ ( value >> 31 );
   }

   /// <summary>
   /// FNV-1a over the characters. Why not string.GetHashCode: it changes on every process
   /// start, so a recorded seed would not repeat the order.
   /// </summary>
   /// <param name="text">Text.</param>
   /// <returns>A hash that is the same on every run and every machine.</returns>
   private static ulong StableHash( string text )
   {
      ulong hash = FNV_OFFSET;
      foreach( char c in text )
      {
         hash = ( hash ^ c ) * FNV_PRIME;
      }

      return hash;
   }

   #endregion Private Methods
}
