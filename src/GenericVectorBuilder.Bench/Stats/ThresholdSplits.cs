using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The setup splits of the threshold basis: every place where an engine's recorded setup differs between
/// two groups of basis runs, with the field that changed, the largest move across the change that the basis
/// leaves out, and the threshold the basis would give if that move counted.
/// Why: <see cref="ThresholdBasis"/> compares an engine's runs only where the engine recorded one setup
/// (<see cref="ConsolidateIdentity.SameTarget"/>), so a change of setup between v5 and v6 hides the move it
/// caused. The report must say so and show each hidden move, because the headline's margin can depend on
/// which moves the basis counts (the dry check of the v8 pipeline found a pair move of 37.94% between ClickHouse
/// v6 and v7 that would raise the threshold from 35% to 45%).
/// Counterfactual per split: only that engine's two groups are joined. A pair move is formed with another engine
/// only when that other engine is in one of its own identity groups in both runs, so a move is attributed to the
/// change that caused it and never to two changes at once. The threshold rule itself is untouched.
/// </summary>
public static class ThresholdSplits
{
   #region Public Methods

   /// <summary>
   /// Every setup split of every engine of the basis pool.
   /// </summary>
   /// <param name="pool">Basis runs, in folder order.</param>
   /// <param name="exclusions">Exclusion rows in force: an excluded cell is in no group and no move.</param>
   /// <param name="basisMaxBp">The basis' largest move, bp, which a split's threshold is never below.</param>
   /// <returns>The splits, engines in name order and each engine's group pairs in order.</returns>
   public static List<SetupSplit> Compute( IReadOnlyList<BasisRun> pool, IReadOnlyList<ExclusionRow> exclusions, int basisMaxBp )
   {
      var splits = new List<SetupSplit>();
      foreach( string engine in ThresholdBasis.Engines( pool ) )
      {
         List<List<BasisRun>> groups = IdentityGroups( pool, engine, exclusions );
         for( int i = 0; i < groups.Count; i++ )
         {
            for( int j = i + 1; j < groups.Count; j++ )
            {
               splits.Add( Split( pool, exclusions, engine, groups[i], groups[j], basisMaxBp ) );
            }
         }
      }

      return splits;
   }

   /// <summary>
   /// An engine's recorded value of one identity field, as the identity compares it.
   /// </summary>
   /// <param name="target">The engine in one run.</param>
   /// <param name="field">The field name (engine, index, searchSettings, durability, engineFiles, engineSettings, imageId, hosting).</param>
   /// <returns>The recorded text, or "not recorded".</returns>
   public static string FieldValue( TargetResult target, string field )
   {
      string? value = field switch
      {
         "engine" => target.Engine,
         "index" => target.Index,
         "searchSettings" => target.SearchSettings,
         "durability" => target.Durability,
         "engineFiles" => target.EngineFiles,
         "engineSettings" => target.EngineSettingsList.Count == 0 ? null : string.Join( "; ", target.EngineSettingsList.Select( s => $"{s.Key}={s.Value}" ).OrderBy( s => s, StringComparer.Ordinal ) ),
         "imageId" => target.ImageId,
         "hosting" => target.Hosting,
         _ => null,
      };
      return value ?? ConsolidateIdentity.NOT_RECORDED;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The identity groups of one engine over every metric: runs in pool order that hold at least one value of the engine
   /// the exclusions leave in, each joining the first group whose first run it matches (as the basis groups do), singletons kept.
   /// </summary>
   /// <param name="pool">Runs, in folder order.</param>
   /// <param name="engine">Engine.</param>
   /// <param name="exclusions">Exclusion rows in force.</param>
   /// <returns>The groups.</returns>
   private static List<List<BasisRun>> IdentityGroups( IReadOnlyList<BasisRun> pool, string engine, IReadOnlyList<ExclusionRow> exclusions )
   {
      var groups = new List<List<BasisRun>>();
      foreach( BasisRun run in pool.Where( r => ClaimMetrics.ALL.Any( m => Holds( r, engine, m, exclusions ) ) ) )
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

      return groups;
   }

   /// <summary>
   /// Whether a run holds a value of the engine for a metric that no exclusion removes.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <param name="engine">Engine.</param>
   /// <param name="metric">Metric id.</param>
   /// <param name="exclusions">Exclusion rows in force.</param>
   /// <returns>True when the cell counts.</returns>
   private static bool Holds( BasisRun run, string engine, string metric, IReadOnlyList<ExclusionRow> exclusions )
   {
      return run.Value( engine, metric ) != null && !ThresholdBasis.Excluded( exclusions, engine, run.Seed, metric );
   }

   /// <summary>
   /// One split between two groups of an engine.
   /// </summary>
   /// <param name="pool">Every basis run.</param>
   /// <param name="exclusions">Exclusion rows in force.</param>
   /// <param name="engine">Engine.</param>
   /// <param name="earlier">The group whose first run is earlier.</param>
   /// <param name="later">The other group.</param>
   /// <param name="basisMaxBp">The basis' largest move, bp.</param>
   /// <returns>The split.</returns>
   private static SetupSplit Split( IReadOnlyList<BasisRun> pool, IReadOnlyList<ExclusionRow> exclusions, string engine, List<BasisRun> earlier, List<BasisRun> later, int basisMaxBp )
   {
      TargetResult a = earlier[0].Run.Find( engine )!;
      TargetResult b = later[0].Run.Find( engine )!;
      List<string> fields = ConsolidateIdentity.TargetDifferences( a, b ).ToList();
      var split = new SetupSplit
      {
         Target = engine,
         Fields = fields,
         Changes = fields.Select( f => new FieldChange { Field = f, Before = FieldValue( a, f ), After = FieldValue( b, f ) } ).ToList(),
         Before = Side( earlier ),
         After = Side( later ),
      };
      foreach( string metric in ClaimMetrics.ALL )
      {
         split.PerMetric[metric] = new SplitMetric
         {
            OneEngine = OneEngine( earlier, later, engine, metric, exclusions ),
            Pair = Pair( pool, earlier, later, engine, metric, exclusions ),
         };
      }

      split.OneEngine = Largest( split.PerMetric.Values.Select( m => m.OneEngine ) );
      split.Pair = Largest( split.PerMetric.Values.Select( m => m.Pair ) );
      split.TBpIfCounted = ThresholdBasis.TBpFor( Math.Max( basisMaxBp, Math.Max( split.OneEngine?.MoveBp ?? 0, split.Pair?.MoveBp ?? 0 ) ) );
      return split;
   }

   /// <summary>
   /// The sessions and runs of one group.
   /// </summary>
   /// <param name="group">The group.</param>
   /// <returns>The side.</returns>
   private static SplitSide Side( List<BasisRun> group )
   {
      return new SplitSide { Sessions = group.Select( r => r.Session ).Distinct( StringComparer.Ordinal ).ToList(), Runs = group.Select( r => r.Folder ).ToList() };
   }

   /// <summary>
   /// The largest one-engine move between a run of one group and a run of the other at one run key.
   /// </summary>
   /// <param name="earlier">One group.</param>
   /// <param name="later">The other group.</param>
   /// <param name="engine">Engine.</param>
   /// <param name="metric">Metric id.</param>
   /// <param name="exclusions">Exclusion rows in force.</param>
   /// <returns>The move, or null when no two runs qualify.</returns>
   private static BasisMove? OneEngine( List<BasisRun> earlier, List<BasisRun> later, string engine, string metric, IReadOnlyList<ExclusionRow> exclusions )
   {
      (double Move, BasisRun A, BasisRun B)? best = null;
      foreach( BasisRun a in earlier.Where( r => Holds( r, engine, metric, exclusions ) ) )
      {
         foreach( BasisRun b in later.Where( r => Holds( r, engine, metric, exclusions ) && r.RunKey == a.RunKey ) )
         {
            double va = a.Value( engine, metric )!.Value;
            double vb = b.Value( engine, metric )!.Value;
            double move = Math.Max( va, vb ) / Math.Min( va, vb ) - 1;
            best = best == null || move > best.Value.Move ? ( move, a, b ) : best;
         }
      }

      return best == null ? null : ThresholdBasis.OneEngineMove( engine, best.Value.A, best.Value.B, metric, best.Value.Move );
   }

   /// <summary>
   /// The largest pair move across the change: the engine's value in a run of one group over another engine's value in the same run, against the
   /// same ratio in a run of the other group, where the other engine is in one identity group of its own in both runs.
   /// </summary>
   /// <param name="pool">Every basis run.</param>
   /// <param name="earlier">One group.</param>
   /// <param name="later">The other group.</param>
   /// <param name="engine">Engine.</param>
   /// <param name="metric">Metric id.</param>
   /// <param name="exclusions">Exclusion rows in force.</param>
   /// <returns>The move, or null when no pair qualifies.</returns>
   private static BasisMove? Pair( IReadOnlyList<BasisRun> pool, List<BasisRun> earlier, List<BasisRun> later, string engine, string metric, IReadOnlyList<ExclusionRow> exclusions )
   {
      (double Move, string Other, BasisRun A, BasisRun B, int Common)? best = null;
      foreach( string other in ThresholdBasis.Engines( pool ).Where( e => e != engine ) )
      {
         List<List<BasisRun>> otherGroups = ThresholdBasis.Groups( pool, other, metric, exclusions );
         foreach( BasisRun a in earlier.Where( r => Holds( r, engine, metric, exclusions ) && Holds( r, other, metric, exclusions ) ) )
         {
            foreach( BasisRun b in later.Where( r => Holds( r, engine, metric, exclusions ) && Holds( r, other, metric, exclusions ) && r.RunKey == a.RunKey ) )
            {
               List<BasisRun>? home = otherGroups.FirstOrDefault( g => g.Contains( a ) && g.Contains( b ) );
               if( home == null )
               {
                  continue;
               }

               double q1 = a.Value( engine, metric )!.Value / a.Value( other, metric )!.Value;
               double q2 = b.Value( engine, metric )!.Value / b.Value( other, metric )!.Value;
               double move = Math.Max( q1, q2 ) / Math.Min( q1, q2 ) - 1;
               best = best == null || move > best.Value.Move ? ( move, other, a, b, home.Count ) : best;
            }
         }
      }

      if( best == null )
      {
         return null;
      }

      bool engineFirst = string.CompareOrdinal( engine, best.Value.Other ) < 0;
      return ThresholdBasis.PairMove( engineFirst ? engine : best.Value.Other, engineFirst ? best.Value.Other : engine, best.Value.A, best.Value.B, metric, best.Value.Move, best.Value.Common );
   }

   /// <summary>
   /// The largest of several moves, the first of equals.
   /// </summary>
   /// <param name="moves">The moves (nulls skipped).</param>
   /// <returns>The largest, or null when there is none.</returns>
   private static BasisMove? Largest( IEnumerable<BasisMove?> moves )
   {
      BasisMove? best = null;
      foreach( BasisMove? move in moves )
      {
         if( move != null && ( best == null || move.MoveBp > best.MoveBp || ( move.MoveBp == best.MoveBp && move.Move > best.Move ) ) )
         {
            best = move;
         }
      }

      return best;
   }

   #endregion Private Methods
}
