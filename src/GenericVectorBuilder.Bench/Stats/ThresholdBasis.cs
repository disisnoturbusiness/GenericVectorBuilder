using System.Globalization;
using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The threshold basis: how far one engine, and the ratio of two engines, moved between basis runs
/// at one identity, and the threshold TBp the claim rule uses. A port of the v8c prototype
/// (v8-design/v8c/basis.py) that reproduces its figures on the same runs; the only differences are
/// labels ("build" for its "binary", "not recorded" for its "None") and that a move is carried in
/// basis points rounded up (<see cref="MoveBp"/>), so no move is understated when TBp is set.
/// One-engine move: max/min - 1 of one engine's value over two runs with the same run key, where the
/// engine held one identity (<see cref="ConsolidateIdentity.SameTarget"/>). Pair move: the same-run
/// ratio a/b in two runs, max/min - 1, over runs where both held their identity. Same settings: both
/// runs share turbo, uncore, warm-up and rehearsal (the build is named as a difference, not keyed).
/// Exclusions remove (target, seed, metric) cells, and only rows of basis-exclusions.json do.
/// Why integer basis points: TBp decides which orders are published, and a decision taken on a
/// floating point comparison ((0.30 + 0.05) / 0.05 is not 7 in doubles) could flip on rounding.
/// What the basis leaves out: moves between runs where an engine recorded a different setup. They are not
/// in the threshold (a recorded change of method or configuration is not drift), and the report must say so;
/// <see cref="ThresholdSplits"/> lists each such change with the field that changed, the largest move it
/// hides and the threshold that move would give, so the filter never goes unstated.
/// </summary>
public static class ThresholdBasis
{
   #region Data Members

   /// <summary>The lowest threshold, bp (35%).</summary>
   public const int FLOOR_BP = 3500;

   /// <summary>Thresholds are multiples of this many bp.</summary>
   public const int STEP_BP = 500;

   /// <summary>The threshold is at least this many bp above the largest move.</summary>
   public const int MARGIN_BP = 500;

   /// <summary>The kind of an exclusion row that removes a cell measured with a method defect.</summary>
   public const string KIND_DEFECT = "method defect";

   /// <summary>The kind of an exclusion row that removes cells measured after an unrecorded configuration change.</summary>
   public const string KIND_CONFIG = "unrecorded config change";

   /// <summary>The TBp rule as the report prints it.</summary>
   public const string TBP_RULE = "TBp = max(FLOOR_BP 3500, the smallest multiple of STEP_BP 500 that is at least MaxBp + MARGIN_BP 500)";

   /// <summary>The name a difference of "searchSettings recorded in one run only" goes by in a sentence: the runs differ in whether it was recorded, not in its value.</summary>
   public const string SEARCH_SETTINGS_NAME = "whether searchSettings was recorded";

   /// <summary>The move rule as the report prints it.</summary>
   public const string MOVE_RULE = "moveBp = ceiling(10000 x hi / lo) - 10000, with hi and lo the two values (one engine) or the two same-run ratios (pair)";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The threshold for a largest move: the floor, or the first multiple of the step at least the margin above it.
   /// </summary>
   /// <param name="maxBp">The largest move, bp.</param>
   /// <returns>TBp.</returns>
   public static int TBpFor( int maxBp )
   {
      int needed = maxBp + MARGIN_BP;
      int stepped = ( ( needed + STEP_BP - 1 ) / STEP_BP ) * STEP_BP;
      return Math.Max( FLOOR_BP, stepped );
   }

   /// <summary>
   /// A move in bp, rounded up: ceiling(10000 x hi / lo) - 10000.
   /// </summary>
   /// <param name="hi">The larger value.</param>
   /// <param name="lo">The smaller value (above zero).</param>
   /// <returns>The move, bp.</returns>
   public static int MoveBp( double hi, double lo )
   {
      return checked( (int)Math.Ceiling( 10000.0 * hi / lo ) - 10000 );
   }

   /// <summary>
   /// Computes the whole basis.
   /// </summary>
   /// <param name="basis">Basis runs (machine control on), any order.</param>
   /// <param name="noMachineControl">Runs without machine control, for the disclosed maxima.</param>
   /// <param name="exclusions">Exclusion rows; each must match a cell of the basis.</param>
   /// <returns>The basis with TBp.</returns>
   /// <exception cref="InvalidDataException">An exclusion row matches no cell, or no move exists for a metric.</exception>
   public static BasisInfo Compute( IReadOnlyList<BasisRun> basis, IReadOnlyList<BasisRun> noMachineControl, IReadOnlyList<ExclusionRow> exclusions )
   {
      List<BasisRun> pool = basis.OrderBy( r => r.Folder, StringComparer.Ordinal ).ToList();
      List<BasisRun> other = noMachineControl.OrderBy( r => r.Folder, StringComparer.Ordinal ).ToList();
      CheckExclusionsMatch( pool, exclusions );
      var info = new BasisInfo { Exclusions = exclusions.ToList(), TBpRule = TBP_RULE, MoveRule = MOVE_RULE, FloorBp = FLOOR_BP, StepBp = STEP_BP, MarginBp = MARGIN_BP };
      foreach( string metric in ClaimMetrics.ALL )
      {
         info.PerMetric[metric] = ForMetric( pool, other, exclusions, metric );
      }

      List<BasisMove> all = info.PerMetric.Values.SelectMany( m => new[] { m.OneEngine, m.Pair } ).OfType<BasisMove>().ToList();
      if( all.Count == 0 )
      {
         throw new InvalidDataException( "the basis holds no two runs of one engine at one identity, so no move and no threshold can be computed" );
      }

      info.MaxMove = all.OrderByDescending( m => m.MoveBp ).ThenByDescending( m => m.Move ).First();
      info.MaxBp = info.MaxMove.MoveBp;
      info.TBp = TBpFor( info.MaxBp );
      info.NoMachineControl = Maxima( info.PerMetric.Values.Select( m => m.OneEngineNoMachineControl ), info.PerMetric.Values.Select( m => m.PairNoMachineControl ) );
      info.ExclusionsKept = KeptOverall( info, exclusions );
      info.SetupSplits = ThresholdSplits.Compute( pool, exclusions, info.MaxBp );
      return info;
   }

   /// <summary>
   /// Largest one-engine move of a metric over a pool.
   /// </summary>
   /// <param name="pool">Runs, in folder order.</param>
   /// <param name="metric">Metric id.</param>
   /// <param name="sameOnly">Only run pairs at the same settings.</param>
   /// <param name="exclusions">Exclusion rows in force.</param>
   /// <returns>The move, or null when no two runs qualify.</returns>
   public static BasisMove? OneEngine( IReadOnlyList<BasisRun> pool, string metric, bool sameOnly, IReadOnlyList<ExclusionRow> exclusions )
   {
      (double Move, string Engine, BasisRun A, BasisRun B)? best = null;
      foreach( string engine in Engines( pool ) )
      {
         foreach( List<BasisRun> group in Groups( pool, engine, metric, exclusions ) )
         {
            for( int i = 0; i < group.Count; i++ )
            {
               for( int j = i + 1; j < group.Count; j++ )
               {
                  BasisRun a = group[i];
                  BasisRun b = group[j];
                  if( a.RunKey != b.RunKey || ( sameOnly && a.Conditions.SameKey != b.Conditions.SameKey ) )
                  {
                     continue;
                  }

                  double va = a.Value( engine, metric )!.Value;
                  double vb = b.Value( engine, metric )!.Value;
                  double move = Math.Max( va, vb ) / Math.Min( va, vb ) - 1;
                  best = best == null || move > best.Value.Move ? ( move, engine, a, b ) : best;
               }
            }
         }
      }

      return best == null ? null : OneEngineMove( best.Value.Engine, best.Value.A, best.Value.B, metric, best.Value.Move );
   }

   /// <summary>
   /// Largest pair move of a metric over a pool.
   /// </summary>
   /// <param name="pool">Runs, in folder order.</param>
   /// <param name="metric">Metric id.</param>
   /// <param name="sameOnly">Only run pairs at the same settings.</param>
   /// <param name="exclusions">Exclusion rows in force.</param>
   /// <returns>The move, or null when no two runs qualify.</returns>
   public static BasisMove? Pair( IReadOnlyList<BasisRun> pool, string metric, bool sameOnly, IReadOnlyList<ExclusionRow> exclusions )
   {
      (double Move, string A, string B, BasisRun Ra, BasisRun Rb, int Common)? best = null;
      List<string> engines = Engines( pool );
      var groups = engines.ToDictionary( e => e, e => Groups( pool, e, metric, exclusions ), StringComparer.Ordinal );
      for( int i = 0; i < engines.Count; i++ )
      {
         for( int j = i + 1; j < engines.Count; j++ )
         {
            foreach( List<BasisRun> x in groups[engines[i]] )
            {
               foreach( List<BasisRun> y in groups[engines[j]] )
               {
                  var candidate = BestPairIn( x, y, engines[i], engines[j], metric, sameOnly );
                  if( candidate != null && ( best == null || candidate.Value.Move > best.Value.Move ) )
                  {
                     best = ( candidate.Value.Move, engines[i], engines[j], candidate.Value.Ra, candidate.Value.Rb, candidate.Value.Common );
                  }
               }
            }
         }
      }

      return best == null ? null : PairMove( best.Value.A, best.Value.B, best.Value.Ra, best.Value.Rb, metric, best.Value.Move, best.Value.Common );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Every move of one metric: all runs, same settings, each exclusion kind kept in, and the runs without machine control.
   /// </summary>
   /// <param name="pool">Basis runs.</param>
   /// <param name="other">Runs without machine control.</param>
   /// <param name="exclusions">Exclusion rows.</param>
   /// <param name="metric">Metric id.</param>
   /// <returns>The metric's moves.</returns>
   private static MetricBasis ForMetric( IReadOnlyList<BasisRun> pool, IReadOnlyList<BasisRun> other, IReadOnlyList<ExclusionRow> exclusions, string metric )
   {
      var result = new MetricBasis
      {
         OneEngine = OneEngine( pool, metric, false, exclusions ),
         Pair = Pair( pool, metric, false, exclusions ),
         OneEngineSameSettings = OneEngine( pool, metric, true, exclusions ),
         PairSameSettings = Pair( pool, metric, true, exclusions ),
         OneEngineNoMachineControl = OneEngine( other, metric, false, exclusions ),
         PairNoMachineControl = Pair( other, metric, false, exclusions ),
      };
      foreach( string kind in exclusions.Select( e => e.Kind ).Distinct( StringComparer.Ordinal ) )
      {
         List<ExclusionRow> rest = exclusions.Where( e => e.Kind != kind ).ToList();
         result.OneEngineExclusionsKept.Add( new KeptIn { Kind = kind, OneEngine = OneEngine( pool, metric, false, rest ) } );
         result.PairExclusionsKept.Add( new KeptIn { Kind = kind, Pair = Pair( pool, metric, false, rest ) } );
      }

      return result;
   }

   /// <summary>
   /// Per exclusion kind, the largest moves over every metric with that kind kept in, and the threshold it would give.
   /// </summary>
   /// <param name="info">The basis, per-metric moves filled.</param>
   /// <param name="exclusions">Exclusion rows.</param>
   /// <returns>One entry per kind.</returns>
   private static List<KeptIn> KeptOverall( BasisInfo info, IReadOnlyList<ExclusionRow> exclusions )
   {
      var kept = new List<KeptIn>();
      foreach( string kind in exclusions.Select( e => e.Kind ).Distinct( StringComparer.Ordinal ) )
      {
         MoveMaxima maxima = Maxima( info.PerMetric.Values.Select( m => m.OneEngineExclusionsKept.First( k => k.Kind == kind ).OneEngine ),
            info.PerMetric.Values.Select( m => m.PairExclusionsKept.First( k => k.Kind == kind ).Pair ) );
         int largest = new[] { maxima.OneEngine?.MoveBp ?? 0, maxima.Pair?.MoveBp ?? 0 }.Max();
         kept.Add( new KeptIn { Kind = kind, OneEngine = maxima.OneEngine, Pair = maxima.Pair, TBpIfKept = TBpFor( largest ) } );
      }

      return kept;
   }

   /// <summary>
   /// The largest one-engine and pair moves of several metrics.
   /// </summary>
   /// <param name="oneEngine">One-engine moves (nulls skipped).</param>
   /// <param name="pair">Pair moves (nulls skipped).</param>
   /// <returns>The maxima.</returns>
   private static MoveMaxima Maxima( IEnumerable<BasisMove?> oneEngine, IEnumerable<BasisMove?> pair )
   {
      return new MoveMaxima
      {
         OneEngine = oneEngine.OfType<BasisMove>().OrderByDescending( m => m.Move ).FirstOrDefault(),
         Pair = pair.OfType<BasisMove>().OrderByDescending( m => m.Move ).FirstOrDefault(),
      };
   }

   /// <summary>
   /// The best pair move between two identity groups: runs in both, at one run key (the first common run's), every pair of them.
   /// </summary>
   /// <param name="x">Group of engine a.</param>
   /// <param name="y">Group of engine b.</param>
   /// <param name="a">Engine a.</param>
   /// <param name="b">Engine b.</param>
   /// <param name="metric">Metric id.</param>
   /// <param name="sameOnly">Only run pairs at the same settings.</param>
   /// <returns>The best move, its runs and how many runs held both, or null.</returns>
   private static (double Move, BasisRun Ra, BasisRun Rb, int Common)? BestPairIn( List<BasisRun> x, List<BasisRun> y, string a, string b, string metric, bool sameOnly )
   {
      List<BasisRun> common = x.Where( r => y.Any( r2 => ReferenceEquals( r, r2 ) ) ).ToList();
      if( common.Count > 0 )
      {
         string key = common[0].RunKey;
         common = common.Where( r => r.RunKey == key ).ToList();
      }

      (double Move, BasisRun Ra, BasisRun Rb, int Common)? best = null;
      for( int i = 0; i < common.Count; i++ )
      {
         for( int j = i + 1; j < common.Count; j++ )
         {
            BasisRun ra = common[i];
            BasisRun rb = common[j];
            if( sameOnly && ra.Conditions.SameKey != rb.Conditions.SameKey )
            {
               continue;
            }

            double q1 = ra.Value( a, metric )!.Value / ra.Value( b, metric )!.Value;
            double q2 = rb.Value( a, metric )!.Value / rb.Value( b, metric )!.Value;
            double move = Math.Max( q1, q2 ) / Math.Min( q1, q2 ) - 1;
            best = best == null || move > best.Value.Move ? ( move, ra, rb, common.Count ) : best;
         }
      }

      return best;
   }

   /// <summary>
   /// Identity groups of the runs holding an engine's value for a metric: greedy, each run joins the
   /// first group whose first run it matches; groups of one are dropped.
   /// </summary>
   /// <param name="pool">Runs, in folder order.</param>
   /// <param name="engine">Engine (target name).</param>
   /// <param name="metric">Metric id.</param>
   /// <param name="exclusions">Exclusion rows in force.</param>
   /// <returns>Groups of two runs or more.</returns>
   internal static List<List<BasisRun>> Groups( IReadOnlyList<BasisRun> pool, string engine, string metric, IReadOnlyList<ExclusionRow> exclusions )
   {
      var groups = new List<List<BasisRun>>();
      foreach( BasisRun run in pool.Where( r => r.Value( engine, metric ) != null && !Excluded( exclusions, engine, r.Seed, metric ) ) )
      {
         TargetResult target = run.Run.Find( engine )!;
         List<BasisRun>? home = groups.FirstOrDefault( g => ConsolidateIdentity.SameTarget( g[0].Run.Find( engine )!, target ) );
         if( home != null )
         {
            home.Add( run );
         }
         else
         {
            groups.Add( new List<BasisRun> { run } );
         }
      }

      return groups.Where( g => g.Count >= 2 ).ToList();
   }

   /// <summary>
   /// True when an exclusion row removes this cell.
   /// </summary>
   /// <param name="exclusions">Rows in force.</param>
   /// <param name="engine">Target.</param>
   /// <param name="seed">Run seed.</param>
   /// <param name="metric">Metric id.</param>
   /// <returns>True when excluded.</returns>
   internal static bool Excluded( IReadOnlyList<ExclusionRow> exclusions, string engine, int? seed, string metric )
   {
      return seed.HasValue && exclusions.Any( e => e.Target == engine && e.Seeds.Contains( seed.Value ) && ( e.Metric == "*" || ClaimMetrics.FromAnyName( e.Metric ) == metric ) );
   }

   /// <summary>
   /// Fails loud when an exclusion row removes nothing: a typo in a seed or a target would otherwise leave a defect in the basis unseen.
   /// </summary>
   /// <param name="pool">Basis runs.</param>
   /// <param name="exclusions">Rows.</param>
   /// <exception cref="InvalidDataException">A row matches no cell.</exception>
   private static void CheckExclusionsMatch( IReadOnlyList<BasisRun> pool, IReadOnlyList<ExclusionRow> exclusions )
   {
      foreach( ExclusionRow row in exclusions )
      {
         if( row.Kind != KIND_DEFECT && row.Kind != KIND_CONFIG )
         {
            throw new InvalidDataException( $"exclusion row for {row.Target} has kind '{row.Kind}', not '{KIND_DEFECT}' or '{KIND_CONFIG}'" );
         }

         IEnumerable<string> metrics = row.Metric == "*" ? ClaimMetrics.ALL : new[] { ClaimMetrics.FromAnyName( row.Metric ) };
         bool matches = pool.Any( r => r.Seed.HasValue && row.Seeds.Contains( r.Seed.Value ) && metrics.Any( m => r.Value( row.Target, m ) != null ) );
         if( !matches )
         {
            throw new InvalidDataException( $"exclusion row for {row.Target} seeds {string.Join( ",", row.Seeds )} metric {row.Metric} matches no cell of the basis runs" );
         }
      }
   }

   /// <summary>
   /// Engine names over a pool, sorted ordinal.
   /// </summary>
   /// <param name="pool">Runs.</param>
   /// <returns>Names.</returns>
   internal static List<string> Engines( IReadOnlyList<BasisRun> pool )
   {
      return pool.SelectMany( r => r.Run.Targets.Select( t => t.Name ) ).Distinct( StringComparer.Ordinal ).OrderBy( n => n, StringComparer.Ordinal ).ToList();
   }

   /// <summary>
   /// The move object of a one-engine maximum.
   /// </summary>
   /// <param name="engine">Engine.</param>
   /// <param name="a">First run.</param>
   /// <param name="b">Second run.</param>
   /// <param name="metric">Metric id.</param>
   /// <param name="move">The raw move.</param>
   /// <returns>The move.</returns>
   internal static BasisMove OneEngineMove( string engine, BasisRun a, BasisRun b, string metric, double move )
   {
      double va = a.Value( engine, metric )!.Value;
      double vb = b.Value( engine, metric )!.Value;
      return new BasisMove
      {
         Metric = metric,
         Move = move,
         MoveBp = MoveBp( Math.Max( va, vb ), Math.Min( va, vb ) ),
         Target = engine,
         Runs = new List<string> { a.Folder, b.Folder },
         Values = new List<double> { va, vb },
         Differences = Differences( a, b, engine, metric ),
         DifferenceNames = DifferenceNames( Differences( a, b, engine, metric ) ),
         Seeds = new List<int?> { a.Seed, b.Seed },
      };
   }

   /// <summary>
   /// The move object of a pair maximum.
   /// </summary>
   /// <param name="ea">Engine a.</param>
   /// <param name="eb">Engine b.</param>
   /// <param name="a">First run.</param>
   /// <param name="b">Second run.</param>
   /// <param name="metric">Metric id.</param>
   /// <param name="move">The raw move.</param>
   /// <param name="common">Runs holding both engines.</param>
   /// <returns>The move.</returns>
   internal static BasisMove PairMove( string ea, string eb, BasisRun a, BasisRun b, string metric, double move, int common )
   {
      double q1 = a.Value( ea, metric )!.Value / a.Value( eb, metric )!.Value;
      double q2 = b.Value( ea, metric )!.Value / b.Value( eb, metric )!.Value;
      List<string> differences = Differences( a, b, ea, metric );
      differences.AddRange( Differences( a, b, eb, metric ).Where( d => d.StartsWith( eb, StringComparison.Ordinal ) ) );
      return new BasisMove
      {
         Metric = metric,
         Move = move,
         MoveBp = MoveBp( Math.Max( q1, q2 ), Math.Min( q1, q2 ) ),
         Pair = $"{ea}/{eb}",
         Runs = new List<string> { a.Folder, b.Folder },
         Values = new List<double> { q1, q2 },
         RunsWithBoth = common,
         Differences = differences,
         DifferenceNames = DifferenceNames( differences ),
         Seeds = new List<int?> { a.Seed, b.Seed },
      };
   }

   /// <summary>
   /// Recorded differences between two runs for one engine and metric, as the prototype lists them:
   /// each condition that differs, the pass medians when they differ, and search settings recorded in one run only.
   /// </summary>
   /// <param name="a">First run.</param>
   /// <param name="b">Second run.</param>
   /// <param name="engine">Engine.</param>
   /// <param name="metric">Metric id.</param>
   /// <returns>The differences.</returns>
   private static List<string> Differences( BasisRun a, BasisRun b, string engine, string metric )
   {
      List<string> found = a.Conditions.Labelled().Zip( b.Conditions.Labelled() ).Where( p => p.First.Value != p.Second.Value )
         .Select( p => $"{p.First.Name}: {p.First.Value} vs {p.Second.Value}" ).ToList();
      string ma = a.MhzText( engine, metric );
      string mb = b.MhzText( engine, metric );
      if( ma != mb )
      {
         found.Add( $"{engine} pass median MHz engine/client: {ma} vs {mb}" );
      }

      if( ( a.Run.Find( engine )!.SearchSettings == null ) != ( b.Run.Find( engine )!.SearchSettings == null ) )
      {
         found.Add( "searchSettings recorded in one run only" );
      }

      return found;
   }

   /// <summary>
   /// The names of the differing fields, one per difference line and each once, in line order:
   /// a condition by its label, "median MHz" for a pass-median line, and for the recorded-in-one-run line
   /// <see cref="SEARCH_SETTINGS_NAME"/>, so a sentence never says two runs differ in searchSettings when one of them recorded none.
   /// Why names apart from the full lines: a sentence names the fields; the lines stay as data.
   /// </summary>
   /// <param name="differences">The difference lines.</param>
   /// <returns>The names.</returns>
   private static List<string> DifferenceNames( IReadOnlyList<string> differences )
   {
      return differences.Select( d => d.Contains( "pass median MHz", StringComparison.Ordinal ) ? "median MHz"
            : d.StartsWith( "searchSettings", StringComparison.Ordinal ) ? SEARCH_SETTINGS_NAME
            : d[..d.IndexOf( ':' )] )
         .Distinct( StringComparer.Ordinal ).ToList();
   }

   #endregion Private Methods
}

/// <summary>
/// A run in the threshold basis: the run, its session, its recorded conditions and its run key.
/// </summary>
public sealed class BasisRun
{
   #region Constructor

   /// <summary>
   /// Wraps a run for the basis.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <param name="session">Its session name.</param>
   public BasisRun( RunResult run, string session )
   {
      Run = run;
      Session = session;
      Conditions = BasisConditions.From( run );
      RunKey = ConsolidateIdentity.RunKey( run );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The run.</summary>
   public RunResult Run { get; }

   /// <summary>Its session.</summary>
   public string Session { get; }

   /// <summary>Its conditions.</summary>
   public BasisConditions Conditions { get; }

   /// <summary>Its run-level identity key.</summary>
   public string RunKey { get; }

   /// <summary>Folder name.</summary>
   public string Folder => Run.Name;

   /// <summary>runSeed.</summary>
   public int? Seed => Run.RunSeed;

   /// <summary>
   /// An engine's value for a metric in this run, or null when absent.
   /// </summary>
   /// <param name="engine">Engine.</param>
   /// <param name="metric">Metric id.</param>
   /// <returns>The value, or null.</returns>
   public double? Value( string engine, string metric )
   {
      return Run.Find( engine ) is TargetResult t ? ClaimMetrics.Value( t, metric ) : null;
   }

   /// <summary>
   /// The pass a metric is measured in, for an engine (the last one recorded under that name).
   /// </summary>
   /// <param name="engine">Engine.</param>
   /// <param name="metric">Metric id.</param>
   /// <returns>The pass, or null.</returns>
   public PassFacts? PassFor( string engine, string metric )
   {
      string pass = ClaimMetrics.PassOf( metric );
      return Run.Conditions.Passes.LastOrDefault( p => p.Target == engine && p.Pass == pass );
   }

   /// <summary>
   /// The pass medians as "(engine, client)" MHz, "not recorded" for a missing pass or value.
   /// </summary>
   /// <param name="engine">Engine.</param>
   /// <param name="metric">Metric id.</param>
   /// <returns>The text.</returns>
   public string MhzText( string engine, string metric )
   {
      PassFacts? pass = PassFor( engine, metric );
      if( pass == null )
      {
         return ConsolidateIdentity.NOT_RECORDED;
      }

      return $"({Num( pass.EngineMhzMedian )}, {Num( pass.ClientMhzMedian )})";
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A whole number as text, or "not recorded".
   /// </summary>
   /// <param name="value">The number.</param>
   /// <returns>The text.</returns>
   private static string Num( int? value )
   {
      return value?.ToString( CultureInfo.InvariantCulture ) ?? ConsolidateIdentity.NOT_RECORDED;
   }

   #endregion Private Methods
}

/// <summary>
/// The recorded conditions of a basis run, as labels: turbo and uncore (from conditions.clock or the
/// legacy note), warm-up and rehearsal (from the method notes), build (binary tree, plus commit when
/// recorded) and machine control.
/// Why labels from notes for v5 to v7: those runs recorded these conditions only in prose.
/// </summary>
public sealed class BasisConditions
{
   #region Data Members

   private static readonly System.Text.RegularExpressions.Regex REHEARSAL = new( @"rehearsal of every pass type[^.]*?for (\d+) s each", System.Text.RegularExpressions.RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>Turbo label.</summary>
   public string Turbo { get; init; } = string.Empty;

   /// <summary>Uncore label.</summary>
   public string Uncore { get; init; } = string.Empty;

   /// <summary>Warm-up label.</summary>
   public string Warmup { get; init; } = string.Empty;

   /// <summary>Rehearsal label.</summary>
   public string Rehearsal { get; init; } = string.Empty;

   /// <summary>Build label.</summary>
   public string Build { get; init; } = string.Empty;

   /// <summary>Machine control state.</summary>
   public string MachineControl { get; init; } = string.Empty;

   /// <summary>The same-settings key: turbo and uncore as held during the run, warm-up and rehearsal.</summary>
   public string SameKey { get; init; } = string.Empty;

   /// <summary>
   /// Reads the conditions of a run.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <returns>The conditions.</returns>
   public static BasisConditions From( RunResult run )
   {
      string notes = string.Join( "\n", run.Notes );
      string warmup = notes.Contains( "level test", StringComparison.Ordinal ) ? "settle check with level test"
         : notes.Contains( "Warm-up and settle check", StringComparison.Ordinal ) ? "settle check"
         : notes.Contains( "warm-up of 20 searches", StringComparison.Ordinal ) ? "20-search warm-up"
         : "warm-up not recorded";
      System.Text.RegularExpressions.Match rehearsal = REHEARSAL.Match( notes );
      string rehearsalLabel = rehearsal.Success ? $"rehearsal {rehearsal.Groups[1].Value} s" : "no rehearsal recorded";
      RunClock clock = run.Conditions.Clock;
      string build = ( run.BinaryRoot ?? ConsolidateIdentity.NOT_RECORDED ) + ( run.Conditions.BuildCommit is string commit ? $" (commit {commit})" : string.Empty );
      return new BasisConditions
      {
         Turbo = clock.TurboLabel,
         Uncore = clock.UncoreLabel,
         Warmup = warmup,
         Rehearsal = rehearsalLabel,
         Build = build,
         MachineControl = run.Conditions.MachineControl ?? ConsolidateIdentity.NOT_RECORDED,
         SameKey = string.Join( "|", clock.TurboKey, clock.UncoreKey, warmup, rehearsalLabel ),
      };
   }

   /// <summary>
   /// The conditions as (name, value) in the prototype's order.
   /// </summary>
   /// <returns>turbo, uncore, warmup, rehearsal, build, machineControl.</returns>
   public IReadOnlyList<(string Name, string Value)> Labelled()
   {
      return new[] { ( "turbo", Turbo ), ( "uncore", Uncore ), ( "warmup", Warmup ), ( "rehearsal", Rehearsal ), ( "build", Build ), ( "machineControl", MachineControl ) };
   }

   /// <summary>
   /// The conditions in the contract's shape.
   /// </summary>
   /// <returns>The info object.</returns>
   public BasisConditionsInfo ToInfo()
   {
      return new BasisConditionsInfo { Turbo = Turbo, Uncore = Uncore, Warmup = Warmup, Rehearsal = Rehearsal, Build = Build, MachineControl = MachineControl };
   }

   #endregion Public Methods
}
