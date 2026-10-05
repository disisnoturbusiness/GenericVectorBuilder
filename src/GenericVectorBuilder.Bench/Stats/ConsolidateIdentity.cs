using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// Decides whether runs are the same kind of experiment, and refuses to merge them when they
/// are not: the same benchmark command for the whole report, and for each target the same engine
/// hosting (a container against a native service) and the same engine setup (its description, its
/// recorded non-default settings, the files it was configured from, and its durability statement).
/// Why a refusal and not "use the largest group": the settings check drops the odd run and says
/// so, which is right for a stray Debug build; but a run-all and a bench run, or a container and a
/// native engine, are both legitimate sets of numbers, and quietly choosing one by count would
/// pick the answer. The caller must say which kind it wants by passing only those runs.
/// Why these two: a bench run searches a copy that an earlier replicate loaded while a run-all
/// loads its own; a container is cut off from the host by its network and CPU limits, a native
/// service is not.
/// How far a refusal reaches: a different command is a different experiment for every target, so it
/// stops the whole command (<see cref="Refusal"/>); a different hosting or engine setup is a fact about
/// one target, so it refuses that target alone (<see cref="RefusedTargets"/>) and the report names it,
/// with the reason, under "Targets not in this report" instead of failing and writing nothing. When
/// every listed target is refused there is nothing to report and the whole command stops.
/// Why the engine setup is on the list: the v6 runs were measured after ClickHouse's server configuration
/// had been changed (21 system log tables switched off) and nothing in the results said so, so v5 and v6
/// numbers could have been merged under one description. A setup the run recorded (engine text, structured
/// engine settings, the hashes of the files it was configured from, durability) that differs is refused;
/// a setup nobody recorded counts as one value ("not recorded"), as every other missing field does.
/// A field a run did not record counts as a value of its own, so a run from before the field
/// existed is not mixed with runs that have it.
/// The segment layout is not on that list any more (it was until the v5 review). Qdrant ended a
/// replicate load with 3 or 4 segments and a run-all load with 2, Milvus compacted two sealed
/// segments covering 1,048 rows for 524 stored, and MongoDB ended two runs with 4 segments and a
/// third with 2; refusing the whole command for that dropped MongoDB from the published report
/// with no word said. A layout that differs between runs is now shown: the target stays in every
/// table and carries the "segment-layout-differs-between-runs" flag that names each layout and the
/// runs that had it (<see cref="Difference"/>), so the reader sees the difference next to the
/// numbers it may have caused instead of not seeing the engine at all.
/// </summary>
public static class ConsolidateIdentity
{
   #region Data Members

   /// <summary>How many run names are listed for each side of a difference before "and N more".</summary>
   public const int MAX_RUNS_LISTED = 3;

   private const string NOT_RECORDED = "not recorded";
   private const string NOT_REPORTED = "not reported";
   private const string COMMAND_FIELD = "command";
   private const string HOSTING_PREFIX = "hosting of ";
   private const string SETUP_SEPARATOR = " of ";
   private const int MAX_VALUE_SHOWN = 120;
   private const int VALUE_CONTEXT = 30;
   private const string HOSTING_EXPLANATION = "A container and a native service are different experiments and are never merged";
   private const string SETUP_EXPLANATION = "Runs of an engine whose description, settings, configuration files or durability differ are different experiments and are never merged";
   private const string BOTH_EXPLANATION = "A container and a native service are different experiments, and so are runs of an engine whose description, settings, configuration files or durability differ; neither is ever merged";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Checks that the runs are one kind of experiment for the whole command: the same benchmark
   /// command in every run, and at least one listed target whose hosting is the same in every run.
   /// </summary>
   /// <param name="runs">Runs that would be merged.</param>
   /// <param name="targets">The listed targets (hosting is compared for these only).</param>
   /// <returns>Null when the command may go on; otherwise a message that names every difference, the runs on each side, and what to do.</returns>
   public static string? Refusal( IReadOnlyList<RunResult> runs, IReadOnlyList<string> targets )
   {
      List<string> differences = Differences( runs, targets, f => f == COMMAND_FIELD );
      IReadOnlyList<RefusedTarget> refused = RefusedTargets( runs, targets );
      if( differences.Count == 0 && refused.Count < targets.Count )
      {
         return null;
      }

      differences.AddRange( refused.Select( r => r.Difference ) );
      return "Refusing to merge runs that are not the same kind of experiment. " + string.Join( "; ", differences )
         + ". Consolidate each kind on its own: pass only the runs of one kind (--runs).";
   }

   /// <summary>
   /// The listed targets whose engine hosting or engine setup is not the same in every run (a container in
   /// some runs and a native service in others; a changed engine description, setting, configuration file or
   /// durability statement). Each is refused alone: the other targets are consolidated and the report names
   /// these with the reason.
   /// </summary>
   /// <param name="runs">Runs that would be merged.</param>
   /// <param name="targets">The listed targets.</param>
   /// <returns>One entry per refused target, in the order of <paramref name="targets"/>; empty for fewer than two runs or when everything agrees.</returns>
   public static IReadOnlyList<RefusedTarget> RefusedTargets( IReadOnlyList<RunResult> runs, IReadOnlyList<string> targets )
   {
      var refused = new List<RefusedTarget>();
      foreach( string target in targets )
      {
         string field = HOSTING_PREFIX + target;
         string? hosting = Differences( runs, new[] { target }, f => f == field ).FirstOrDefault();
         List<string> setup = Differences( runs, new[] { target }, f => IsSetupField( f, target ) );
         if( hosting == null && setup.Count == 0 )
         {
            continue;
         }

         string explanation = hosting != null && setup.Count > 0 ? BOTH_EXPLANATION : hosting != null ? HOSTING_EXPLANATION : SETUP_EXPLANATION;
         refused.Add( new RefusedTarget( target, string.Join( "; ", ( hosting == null ? Array.Empty<string>() : new[] { hosting } ).Concat( setup ) ), explanation ) );
      }

      return refused;
   }

   /// <summary>
   /// The engine setup a target had when it is not the same in every run: its description, recorded
   /// settings, configuration files and durability statement, each with its runs.
   /// </summary>
   /// <param name="found">The runs that hold the target, with its result.</param>
   /// <returns>One text per differing part (long texts cut around the first difference), or null when every run had the same setup.</returns>
   public static string? SetupDifference( IReadOnlyList<( string Run, TargetResult Result )> found )
   {
      var parts = new List<string>();
      foreach( string label in SetupLabels() )
      {
         if( Difference( found.Select( x => ( x.Run, SetupValue( x.Result, label ) ) ), shortenLong: true ) is string difference )
         {
            parts.Add( $"{label}: {difference}" );
         }
      }

      return parts.Count == 0 ? null : string.Join( "; ", parts );
   }

   /// <summary>
   /// Names the values a target had (segment layout, hosting) when they are not the same in every run.
   /// </summary>
   /// <param name="perRun">One (run name, value) pair per run; a null value is a run whose engine reported none.</param>
   /// <returns>Null when every run had the same value; otherwise each value with the runs that had it, e.g. "4 segments (r1, r2); 2 segments (r3)".</returns>
   public static string? Difference( IEnumerable<(string Run, string? Value)> perRun )
   {
      return Difference( perRun, shortenLong: false );
   }

   /// <summary>
   /// The hosting class of a target: container, native or embedded; anything else lower-cased as written.
   /// Why classes and not the raw text: "compose" and "container" are the same hosting, "always-on" and
   /// "systemd" are the same hosting, and the difference that matters is container against native.
   /// </summary>
   /// <param name="hosting">The hosting text a run recorded, or null.</param>
   /// <returns>The class, or "not recorded".</returns>
   public static string HostingClass( string? hosting )
   {
      if( string.IsNullOrWhiteSpace( hosting ) )
      {
         return NOT_RECORDED;
      }

      string text = hosting.Trim().ToLowerInvariant();
      if( text.Contains( "embedded" ) || text.Contains( "in-process" ) || text.Contains( "in process" ) )
      {
         return "embedded";
      }

      if( text.Contains( "compose" ) || text.Contains( "container" ) || text.Contains( "docker" ) || text.Contains( "podman" ) )
      {
         return "container";
      }

      return text.Contains( "always-on" ) || text.Contains( "native" ) || text.Contains( "systemd" ) || text.Contains( "service" ) ? "native" : text;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Names the values when they are not the same in every run, optionally cutting long texts to the part that differs.
   /// </summary>
   /// <param name="perRun">One (run name, value) pair per run.</param>
   /// <param name="shortenLong">True to cut values longer than a line to a window around the first character where they differ.</param>
   /// <returns>Null when every run had the same value; otherwise each value with the runs that had it.</returns>
   private static string? Difference( IEnumerable<(string Run, string? Value)> perRun, bool shortenLong )
   {
      List<IGrouping<string, string>> groups = perRun.GroupBy( x => x.Value ?? NOT_REPORTED, x => x.Run ).ToList();
      if( groups.Count < 2 )
      {
         return null;
      }

      IReadOnlyList<string> shown = shortenLong ? Excerpts( groups.Select( g => g.Key ).ToList() ) : groups.Select( g => g.Key ).ToList();
      return string.Join( "; ", groups.Select( ( g, i ) => $"{shown[i]} ({RunList( g )})" ) );
   }

   /// <summary>
   /// Every field of the kind asked for on which the runs do not all agree, each as "field: value (runs) vs value (runs)".
   /// </summary>
   /// <param name="runs">The runs.</param>
   /// <param name="targets">The targets whose hosting and setup fields are looked at.</param>
   /// <param name="include">True for the field names to check.</param>
   /// <returns>One text per differing field; empty for fewer than two runs.</returns>
   private static List<string> Differences( IReadOnlyList<RunResult> runs, IReadOnlyList<string> targets, Func<string, bool> include )
   {
      var differences = new List<string>();
      if( runs.Count < 2 )
      {
         return differences;
      }

      foreach( string field in Fields( runs[0], targets ).Select( f => f.Name ).Where( include ) )
      {
         List<IGrouping<string, string>> list = runs
            .Select( r => ( Run: r.Name, Value: Fields( r, targets ).First( f => f.Name == field ).Value ) )
            .GroupBy( x => x.Value, x => x.Run ).ToList();
         if( list.Count > 1 )
         {
            IReadOnlyList<string> shown = Excerpts( list.Select( g => g.Key ).ToList() );
            differences.Add( $"{field}: {string.Join( " vs ", list.Select( ( g, i ) => $"{shown[i]} ({RunList( g )})" ) )}" );
         }
      }

      return differences;
   }

   /// <summary>
   /// The facts that must agree between runs, as (name, value) pairs in a fixed order.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <param name="targets">The listed targets.</param>
   /// <returns>The pairs.</returns>
   private static IEnumerable<(string Name, string Value)> Fields( RunResult run, IReadOnlyList<string> targets )
   {
      yield return ( COMMAND_FIELD, run.Command ?? NOT_RECORDED );
      foreach( string target in targets )
      {
         TargetResult? t = run.Find( target );
         string? hosting = t?.Hosting ?? run.Conditions.Engines.FirstOrDefault( e => e.Target == target )?.Hosting;
         yield return ( HOSTING_PREFIX + target, HostingClass( hosting ) );
         foreach( string label in SetupLabels() )
         {
            yield return ( label + SETUP_SEPARATOR + target, SetupValue( t, label ) ?? NOT_RECORDED );
         }
      }
   }

   /// <summary>
   /// The parts of an engine's setup that must agree between runs, by the label the report uses.
   /// </summary>
   /// <returns>The labels, in report order.</returns>
   private static string[] SetupLabels()
   {
      return new[] { "engine description", "engine settings", "engine files", "durability" };
   }

   /// <summary>
   /// One part of an engine's setup as one run recorded it.
   /// </summary>
   /// <param name="result">The target's result in that run, or null.</param>
   /// <param name="label">A label from <see cref="SetupLabels"/>.</param>
   /// <returns>The recorded text, or null when the run recorded none.</returns>
   private static string? SetupValue( TargetResult? result, string label )
   {
      return label switch
      {
         "engine description" => result?.Engine,
         "engine settings" => result?.EngineSettings,
         "engine files" => result?.EngineFiles,
         _ => result?.Durability,
      };
   }

   /// <summary>
   /// True when a field name is one of a target's setup fields.
   /// </summary>
   /// <param name="field">The field name.</param>
   /// <param name="target">The target.</param>
   /// <returns>True for "engine files of sql" and the like.</returns>
   private static bool IsSetupField( string field, string target )
   {
      return SetupLabels().Any( label => field == label + SETUP_SEPARATOR + target );
   }

   /// <summary>
   /// Cuts values longer than a line to the stretch around the first character where they differ, so a
   /// difference between two long statements is shown where it is and not as two identical openings.
   /// </summary>
   /// <param name="values">The distinct values.</param>
   /// <returns>The values to print, in the same order.</returns>
   private static IReadOnlyList<string> Excerpts( IReadOnlyList<string> values )
   {
      if( values.All( v => v.Length <= MAX_VALUE_SHOWN ) )
      {
         return values;
      }

      int common = values.Min( v => v.Length );
      foreach( string value in values.Skip( 1 ) )
      {
         int i = 0;
         while( i < common && value[i] == values[0][i] )
         {
            i++;
         }

         common = i;
      }

      int start = Math.Max( 0, common - VALUE_CONTEXT );
      return values.Select( v => ( start > 0 ? "..." : string.Empty ) + ( v.Length - start > MAX_VALUE_SHOWN ? v.Substring( start, MAX_VALUE_SHOWN ) + "..." : v[start..] ) ).ToList();
   }

   /// <summary>
   /// The first few run names of a group, then how many more.
   /// </summary>
   /// <param name="names">Run names.</param>
   /// <returns>E.g. "r1, r2, r3 and 2 more".</returns>
   private static string RunList( IEnumerable<string> names )
   {
      List<string> list = names.ToList();
      string shown = string.Join( ", ", list.Take( MAX_RUNS_LISTED ) );
      return list.Count > MAX_RUNS_LISTED ? $"{shown} and {list.Count - MAX_RUNS_LISTED} more" : shown;
   }

   #endregion Private Methods
}

/// <summary>
/// A target whose engine hosting or setup differs between runs, and the difference.
/// </summary>
/// <param name="Target">Target name.</param>
/// <param name="Difference">What differs, e.g. "hosting of sql: container (r1) vs native (r2)".</param>
/// <param name="Explanation">Why runs that differ that way are never merged, in one sentence (no full stop).</param>
public sealed record RefusedTarget( string Target, string Difference, string Explanation = "A container and a native service are different experiments and are never merged" );
