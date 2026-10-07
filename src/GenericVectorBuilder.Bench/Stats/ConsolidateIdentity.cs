using System.Globalization;
using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The one identity function of the v8 consolidation: whether two runs are the same experiment at
/// run level, and whether one target in two runs ran at the same setup. The threshold basis uses it
/// to decide which runs may be compared for drift, and guard G3 uses it to decide whether a target's
/// v7 and v8 figures may be ranked together.
/// Run level, every field must match (absent equals absent): pipeline, rows, dimension, the queries
/// text, queryCount, top, concurrency, secondsPerLevel and truthNdcg.
/// Per target: engine and index text must match; search settings, durability, the engine files list,
/// engine settings (key=value, sorted), image id and hosting are each compared only where both runs
/// recorded them, so a field v7 never wrote (engineSettings, image id) can never by itself make a
/// v7 run differ from a v8 run.
/// Why "only where both recorded": a missing field is "not recorded", never a value; treating it as
/// one would trip G3 for all 17 container targets because v7 has no image ids.
/// Session identity (<see cref="SessionDifferences"/>) adds what must also hold across the claim
/// sessions: method, build configuration, machine control, host, governor, partition and clock.
/// </summary>
public static class ConsolidateIdentity
{
   #region Data Members

   /// <summary>The value printed for a field a run did not record.</summary>
   public const string NOT_RECORDED = "not recorded";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The run-level fields that must match, by name, in a fixed order.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <returns>(name, value) pairs; <see cref="NOT_RECORDED"/> for an absent field.</returns>
   public static IReadOnlyList<(string Name, string Value)> RunFields( RunResult run )
   {
      return new[]
      {
         ( "pipeline", run.Pipeline ?? NOT_RECORDED ),
         ( "rows", Num( run.Rows ) ),
         ( "dimension", Num( run.Dimension ) ),
         ( "queries", run.Queries ?? NOT_RECORDED ),
         ( "queryCount", Num( run.QueryCount ) ),
         ( "top", Num( run.Top ) ),
         ( "concurrency", string.Join( ",", run.Concurrency.Select( c => c.ToString( CultureInfo.InvariantCulture ) ) ) ),
         ( "secondsPerLevel", Num( run.SecondsPerLevel ) ),
         ( "truthNdcg", run.TruthNdcg?.ToString( "R", CultureInfo.InvariantCulture ) ?? NOT_RECORDED ),
      };
   }

   /// <summary>
   /// The run-level key: equal keys mean the same experiment at run level.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <returns>The key.</returns>
   public static string RunKey( RunResult run )
   {
      return string.Join( "\u0001", RunFields( run ).Select( f => f.Value ) );
   }

   /// <summary>
   /// The run-level fields that differ between two runs.
   /// </summary>
   /// <param name="a">First run.</param>
   /// <param name="b">Second run.</param>
   /// <returns>"field: a vs b" for each differing field; empty when the keys match.</returns>
   public static IReadOnlyList<string> RunDifferences( RunResult a, RunResult b )
   {
      return RunFields( a ).Zip( RunFields( b ) ).Where( p => p.First.Value != p.Second.Value )
         .Select( p => $"{p.First.Name}: {p.First.Value} vs {p.Second.Value}" ).ToList();
   }

   /// <summary>
   /// True when a target ran at the same setup in two runs (see the class summary).
   /// </summary>
   /// <param name="a">The target in one run.</param>
   /// <param name="b">The same target in another run.</param>
   /// <returns>True when nothing both runs recorded differs.</returns>
   public static bool SameTarget( TargetResult a, TargetResult b )
   {
      return TargetDifferences( a, b ).Count == 0;
   }

   /// <summary>
   /// The per-target fields that differ between two runs, compared as <see cref="SameTarget"/> does.
   /// </summary>
   /// <param name="a">The target in one run.</param>
   /// <param name="b">The same target in another run.</param>
   /// <returns>The field names that differ (engine, index, searchSettings, durability, engineFiles, engineSettings, imageId, hosting).</returns>
   public static IReadOnlyList<string> TargetDifferences( TargetResult a, TargetResult b )
   {
      var differ = new List<string>();
      Strict( differ, "engine", a.Engine, b.Engine );
      Strict( differ, "index", a.Index, b.Index );
      WhereBoth( differ, "searchSettings", a.SearchSettings, b.SearchSettings );
      WhereBoth( differ, "durability", a.Durability, b.Durability );
      WhereBoth( differ, "engineFiles", a.EngineFiles, b.EngineFiles );
      WhereBoth( differ, "engineSettings", SettingsKey( a ), SettingsKey( b ) );
      WhereBoth( differ, "imageId", a.ImageId, b.ImageId );
      WhereBoth( differ, "hosting", a.Hosting, b.Hosting );
      return differ;
   }

   /// <summary>
   /// The session-level fields of a run that must match across every run of the claim sessions,
   /// and those that are printed when they differ but do not refuse.
   /// Why: the run-level key leaves out the method (warm-up, exact seconds), the build configuration,
   /// machine control, host, governor, partition and clock; a change in any of them between v7 and v8
   /// would pool two methods under one description.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <returns>(name, value, refuses) triples.</returns>
   public static IReadOnlyList<(string Name, string Value, bool Refuses)> SessionFields( RunResult run )
   {
      RunConditions c = run.Conditions;
      return new[]
      {
         ( "warm-up method", c.WarmupMethod?.Description ?? NOT_RECORDED, true ),
         ( "warmupSearches", Num( c.WarmupSearches ), true ),
         ( "exactSeconds", Num( c.ExactSeconds ), true ),
         ( "buildConfiguration", c.BuildConfiguration ?? NOT_RECORDED, true ),
         ( "machineControl", c.MachineControlState ?? NOT_RECORDED, true ),
         ( "host", run.Host ?? NOT_RECORDED, true ),
         ( "command", run.Command ?? NOT_RECORDED, true ),
         ( "governor", c.Governor ?? NOT_RECORDED, true ),
         ( "partition", c.Partition ?? NOT_RECORDED, true ),
         ( "clock", $"{c.Clock.TurboKey}; {c.Clock.UncoreKey}; pinned {Num( c.Clock.PinnedMhz )} MHz; tolerance {c.Clock.ToleranceBp} bp", true ),
         ( "cpu", run.MachineCpu ?? NOT_RECORDED, true ),
         ( "logicalCpus", Num( run.MachineLogicalCpus ), true ),
         ( "ramGiB", run.MachineRamGiB?.ToString( "R", CultureInfo.InvariantCulture ) ?? NOT_RECORDED, true ),
         ( "os", run.MachineOs ?? NOT_RECORDED, false ),
         ( "dotNet", run.MachineDotNet ?? NOT_RECORDED, false ),
      };
   }

   /// <summary>
   /// Every session-level field that is not the same in all the runs given.
   /// </summary>
   /// <param name="runs">The claim runs, oldest first.</param>
   /// <returns>(field, refuses, "value (runs); value (runs)") for each differing field.</returns>
   public static IReadOnlyList<(string Field, bool Refuses, string Values)> SessionDifferences( IReadOnlyList<RunResult> runs )
   {
      var found = new List<(string Field, bool Refuses, string Values)>();
      if( runs.Count == 0 )
      {
         return found;
      }

      int count = SessionFields( runs[0] ).Count;
      for( int i = 0; i < count; i++ )
      {
         int index = i;
         var values = runs.Select( r => ( Run: r.Name, Field: SessionFields( r )[index] ) ).ToList();
         if( values.Select( v => v.Field.Value ).Distinct( StringComparer.Ordinal ).Count() > 1 )
         {
            string text = string.Join( "; ", values.GroupBy( v => v.Field.Value, StringComparer.Ordinal ).Select( g => $"{g.Key} ({string.Join( ", ", g.Select( v => v.Run ) )})" ) );
            found.Add( ( values[0].Field.Name, values[0].Field.Refuses, text ) );
         }
      }

      return found;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The engine settings as sorted "key=value" text, leaving out how each was read (that text names commands and paths, which may differ between runs while the value does not).
   /// </summary>
   /// <param name="target">The target.</param>
   /// <returns>The text, or null when the run recorded no engineSettings.</returns>
   private static string? SettingsKey( TargetResult target )
   {
      return target.EngineSettingsList.Count == 0 ? null
         : string.Join( "; ", target.EngineSettingsList.Select( s => $"{s.Key}={s.Value}" ).OrderBy( s => s, StringComparer.Ordinal ) );
   }

   /// <summary>
   /// Adds the field when the two values differ, absent counting as a value of its own.
   /// </summary>
   /// <param name="differ">Receives the field name.</param>
   /// <param name="name">Field name.</param>
   /// <param name="a">One value.</param>
   /// <param name="b">The other.</param>
   private static void Strict( List<string> differ, string name, string? a, string? b )
   {
      if( !string.Equals( a, b, StringComparison.Ordinal ) )
      {
         differ.Add( name );
      }
   }

   /// <summary>
   /// Adds the field when both values are recorded and differ.
   /// </summary>
   /// <param name="differ">Receives the field name.</param>
   /// <param name="name">Field name.</param>
   /// <param name="a">One value, or null.</param>
   /// <param name="b">The other, or null.</param>
   private static void WhereBoth( List<string> differ, string name, string? a, string? b )
   {
      if( a != null && b != null && !string.Equals( a, b, StringComparison.Ordinal ) )
      {
         differ.Add( name );
      }
   }

   /// <summary>
   /// A whole number as text, or <see cref="NOT_RECORDED"/>.
   /// </summary>
   /// <param name="value">The number, or null.</param>
   /// <returns>The text.</returns>
   private static string Num( int? value )
   {
      return value?.ToString( CultureInfo.InvariantCulture ) ?? NOT_RECORDED;
   }

   #endregion Private Methods
}
