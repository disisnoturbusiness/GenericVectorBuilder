using System.Globalization;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// Tie bands for the request-speed metrics: targets whose slowest-to-fastest ranges over the
/// runs overlap share a band, and so do neighbours whose medians are less than
/// <see cref="MIN_MEDIAN_GAP"/> apart; bands are numbered from the fastest (band 1).
/// Why bands and not strict ranks: at 524 vectors the targets within a few percent of each other
/// swap places from run to run, so "3rd" and "4th" claim a difference the runs do not show.
/// Why overlap is chained (a band is a connected group of overlapping ranges): then two targets
/// whose ranges overlap are always in one band, and two targets in different bands never overlap,
/// so "band 1 is faster than band 2" is true of every pair across them (every run of the faster
/// one beat every run of the slower one). The price is that a band can be linked through a target
/// with a wide range, so its two ends may be separated even though the band as a whole is not.
/// Why a minimum gap as well: ranges drawn from three runs can fail to overlap by a hair, and the
/// v4 review found band boundaries resting on gaps of 2.2% or less between neighbours, inside the
/// roughly 2% repeatability the same engine shows from run to run. A boundary the repeatability
/// could move is not a finding, so neighbours (next to each other in median order) whose medians
/// differ by less than 3%, the measured repeatability plus a margin, share a band even when their
/// ranges do not overlap. A band boundary now means no overlap AND the nearest medians on either
/// side are at least 3% apart (the faster is at least 3% faster than the slower).
/// Inside a band the targets stay in median order, and every consumer says that this order is not
/// a ranking.
/// The same rule is written again in the web page's BenchBands, because the web project does not
/// reference the benchmark; both are tested against the same cases.
/// </summary>
public static class ConsolidateBands
{
   #region Data Members

   /// <summary>
   /// Smallest gap between the medians of two neighbouring targets that may separate them into two
   /// bands, as a share of the worse median: QPS, the better median is at least 3% higher; latency,
   /// the worse p50 is at least 3% above the better one. It sits above the roughly 2% run-to-run
   /// repeatability measured on the same engine at the same settings (v4 review of 2026-10-05) and
   /// is the same for every metric.
   /// </summary>
   public const double MIN_MEDIAN_GAP = 0.03;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Assigns bands to targets from their value ranges: overlapping ranges are chained into one
   /// band, then neighbouring bands whose nearest medians are less than <see cref="MIN_MEDIAN_GAP"/>
   /// apart are joined.
   /// </summary>
   /// <param name="targets">Target name and the metric's spread over the runs, in any order.</param>
   /// <param name="higherIsBetter">True for QPS (a larger value is faster), false for latency.</param>
   /// <returns>One entry per target: band by band (best first), inside a band by median (ties by name), targets with no value last.</returns>
   public static List<BandEntry> Assign( IReadOnlyList<(string Target, Spread Values)> targets, bool higherIsBetter )
   {
      List<Candidate> ordered = targets.Where( t => IsUsable( t.Values ) )
         .Select( t => new Candidate( t.Target, t.Values, Edge( t.Values, higherIsBetter, best: true ), Edge( t.Values, higherIsBetter, best: false ), Sign( higherIsBetter ) * t.Values.Median!.Value ) )
         .OrderByDescending( c => c.Best ).ThenByDescending( c => c.Median ).ThenBy( c => c.Target, StringComparer.Ordinal ).ToList();
      var bands = new List<List<Candidate>>();
      double reach = double.NaN;
      foreach( Candidate item in ordered )
      {
         if( bands.Count == 0 || item.Best < reach )
         {
            bands.Add( new List<Candidate>() );
            reach = item.Worst;
         }

         bands[^1].Add( item );
         reach = Math.Min( reach, item.Worst );
      }

      bands = MergeCloseMedians( bands );
      var entries = new List<BandEntry>();
      for( int b = 0; b < bands.Count; b++ )
      {
         IEnumerable<Candidate> inBand = bands[b].OrderByDescending( c => c.Median ).ThenBy( c => c.Target, StringComparer.Ordinal );
         entries.AddRange( inBand.Select( ( c, i ) => ToEntry( c.Target, c.Values, b + 1, i + 1 ) ) );
      }

      entries.AddRange( targets.Where( t => !IsUsable( t.Values ) ).OrderBy( t => t.Target, StringComparer.Ordinal ).Select( t => ToEntry( t.Target, t.Values, null, null ) ) );
      return entries;
   }

   /// <summary>
   /// Builds the bands of every request-speed metric: p50 latency, then QPS at each level, highest
   /// level first.
   /// With fewer than two runs no range exists, so no bands are drawn: the entries give this run's
   /// order and <see cref="MetricBands.Banded"/> is false.
   /// </summary>
   /// <param name="summaries">Target summaries, in target order.</param>
   /// <param name="levels">Concurrency levels, lowest first.</param>
   /// <param name="runs">Runs used.</param>
   /// <returns>One <see cref="MetricBands"/> per metric.</returns>
   public static List<MetricBands> Build( IReadOnlyList<TargetSummary> summaries, IReadOnlyList<int> levels, int runs )
   {
      var metrics = new List<(string Key, string Title, bool Higher, Func<TargetSummary, Spread?> Spread)>
      {
         ( "p50Ms", "p50 latency, one search at a time (lower is faster)", false, t => t.P50Ms ),
      };
      foreach( int level in levels.OrderByDescending( l => l ) )
      {
         string key = level.ToString( CultureInfo.InvariantCulture );
         string how = level == 1 ? "one searcher" : $"{level} searchers at once";
         metrics.Add( ( $"qps@{key}", $"QPS with {how} (higher is faster)", true, t => t.Qps.TryGetValue( key, out Spread? s ) ? s : null ) );
      }

      return metrics.Select( m => ForMetric( m.Key, m.Title, m.Higher, summaries.Select( s => ( s.Name, m.Spread( s ) ?? new Spread() ) ).ToList(), runs ) ).ToList();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Joins neighbouring bands whose nearest medians are less than <see cref="MIN_MEDIAN_GAP"/> apart.
   /// Why only the two medians at the boundary: the bands do not overlap, so every median of a band
   /// is better than every median of the band after it, and the two targets next to each other in
   /// median order across a boundary are the band's slowest median and the next band's fastest.
   /// A merged band is compared by its slowest median, so a chain of close neighbours joins into
   /// one band however long it is.
   /// </summary>
   /// <param name="bands">Bands of overlapping ranges, best first.</param>
   /// <returns>The bands after joining, best first.</returns>
   private static List<List<Candidate>> MergeCloseMedians( List<List<Candidate>> bands )
   {
      var merged = new List<List<Candidate>>();
      foreach( List<Candidate> band in bands )
      {
         if( merged.Count > 0 && MedianGap( merged[^1].Min( c => c.Median ), band.Max( c => c.Median ) ) < MIN_MEDIAN_GAP )
         {
            merged[^1].AddRange( band );
         }
         else
         {
            merged.Add( band );
         }
      }

      return merged;
   }

   /// <summary>
   /// How much faster the better of two medians is than the worse, as a share of the worse
   /// (0.03 is 3% faster). Both are given as numbers where larger is better, so a latency arrives
   /// negated; the share is the ratio of the larger magnitude to the smaller one, minus 1, which is
   /// the same whether the metric is QPS or a latency.
   /// </summary>
   /// <param name="a">One median, larger is better.</param>
   /// <param name="b">The other median.</param>
   /// <returns>The gap; infinite when one median is zero and the other is not, 0 for two zeros.</returns>
   private static double MedianGap( double a, double b )
   {
      double high = Math.Max( Math.Abs( a ), Math.Abs( b ) );
      double low = Math.Min( Math.Abs( a ), Math.Abs( b ) );
      return high == low ? 0 : low == 0 ? double.PositiveInfinity : high / low - 1;
   }

   /// <summary>
   /// One metric's bands, or its single-run order when there is no spread.
   /// </summary>
   /// <param name="key">Metric key.</param>
   /// <param name="title">Reader-facing title.</param>
   /// <param name="higherIsBetter">Direction of the metric.</param>
   /// <param name="targets">Target name and spread.</param>
   /// <param name="runs">Runs used.</param>
   /// <returns>The metric's bands.</returns>
   private static MetricBands ForMetric( string key, string title, bool higherIsBetter, IReadOnlyList<(string Target, Spread Values)> targets, int runs )
   {
      var bands = new MetricBands { Metric = key, Title = title, HigherIsBetter = higherIsBetter, Runs = runs, Banded = runs >= 2 };
      if( runs >= 2 )
      {
         bands.Entries = Assign( targets, higherIsBetter );
         return bands;
      }

      List<BandEntry> order = Assign( targets.Select( t => ( t.Target, Point( t.Values ) ) ).ToList(), higherIsBetter );
      bands.Entries = order.Select( ( e, i ) => ToEntry( e.Target, targets.First( t => t.Target == e.Target ).Values, null, e.Median.HasValue ? i + 1 : null ) ).ToList();
      return bands;
   }

   /// <summary>
   /// A spread collapsed to its median alone, so the single-run order is by value and ties by name.
   /// </summary>
   /// <param name="spread">The spread.</param>
   /// <returns>A spread whose min and max equal the median, or the spread itself when it has none.</returns>
   private static Spread Point( Spread spread )
   {
      return IsUsable( spread ) ? new Spread { Median = spread.Median, Min = spread.Median, Max = spread.Median, N = spread.N } : spread;
   }

   /// <summary>
   /// True when the spread has a median, a smallest and a largest value.
   /// </summary>
   /// <param name="spread">The spread.</param>
   /// <returns>True for a usable range.</returns>
   private static bool IsUsable( Spread spread )
   {
      return ConsolidateMath.IsNumber( spread.Median ) && ConsolidateMath.IsNumber( spread.Min ) && ConsolidateMath.IsNumber( spread.Max );
   }

   /// <summary>
   /// The best or the worst end of a range, as a number where larger is always better.
   /// </summary>
   /// <param name="spread">The spread.</param>
   /// <param name="higherIsBetter">Direction of the metric.</param>
   /// <param name="best">True for the best end, false for the worst.</param>
   /// <returns>The end, sign-flipped for a latency so that larger means better.</returns>
   private static double Edge( Spread spread, bool higherIsBetter, bool best )
   {
      bool useMax = higherIsBetter == best;
      return Sign( higherIsBetter ) * ( useMax ? spread.Max!.Value : spread.Min!.Value );
   }

   /// <summary>
   /// 1 when larger is better, -1 when smaller is better.
   /// </summary>
   /// <param name="higherIsBetter">Direction of the metric.</param>
   /// <returns>The sign.</returns>
   private static int Sign( bool higherIsBetter )
   {
      return higherIsBetter ? 1 : -1;
   }

   /// <summary>
   /// Builds an entry from a target's spread.
   /// </summary>
   /// <param name="target">Target name.</param>
   /// <param name="values">Its spread.</param>
   /// <param name="band">Band number, or null.</param>
   /// <param name="order">Position inside the band, or null.</param>
   /// <returns>The entry.</returns>
   private static BandEntry ToEntry( string target, Spread values, int? band, int? order )
   {
      return new BandEntry { Target = target, Band = band, OrderInBand = order, Median = values.Median, Min = values.Min, Max = values.Max, N = values.N };
   }

   /// <summary>
   /// A target while the bands are drawn: its spread and its two ends and median as numbers where larger is better.
   /// </summary>
   /// <param name="Target">Target name.</param>
   /// <param name="Values">The spread over the runs.</param>
   /// <param name="Best">The best end of the range (larger is better).</param>
   /// <param name="Worst">The worst end of the range.</param>
   /// <param name="Median">The median.</param>
   private sealed record Candidate( string Target, Spread Values, double Best, double Worst, double Median );

   #endregion Private Methods
}
