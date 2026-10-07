using System.Text.RegularExpressions;
using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The clock check of every claim run, recomputed from the recorded per-CPU medians with the median
/// rule: per CPU group of the partition (engine CPUs, client CPUs; all CPUs as one group when the
/// partition was not split), the median of the per-CPU medians; a pass is off when either group's
/// median lies more than the run's tolerance from the pinned clock.
/// Why recomputed for v7 as well: v7 flagged passes by a mean, which a few slow samples pull down;
/// its three Oracle default@8 warnings read -10.8%, -10.6% and -9.5% by the mean while every per-CPU
/// median read 3492 MHz. Those warnings are carried as dropped, with the medians, never silently lost.
/// Why it fails loud for v8: a v8 run records its own clockOff by the same rule; if the two disagree
/// the reader and the recorder do not implement one rule, and no figure may be published until they do.
/// </summary>
public static class ConsolidateClock
{
   #region Data Members

   private static readonly Regex MEAN_WARNING = new( @"^WARNING: clock off its pinned value during (?<target>\S+) (?<pass>\S+):", RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The recomputed clock of one pass.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <param name="pass">The pass.</param>
   /// <returns>Engine and client group medians, whether every group was read, and whether the pass is off.</returns>
   public static PassClock Recompute( RunResult run, PassFacts pass )
   {
      SortedSet<int>? engine = CpuSet.TryParse( run.Conditions.EngineCpus );
      SortedSet<int>? client = CpuSet.TryParse( run.Conditions.ClientCpus );
      bool split = engine != null && client != null;
      int? engineMedian = split ? MedianOf( pass.CpuMedians, engine! ) : MedianOf( pass.CpuMedians, null );
      int? clientMedian = split ? MedianOf( pass.CpuMedians, client! ) : engineMedian;
      bool read = engineMedian.HasValue && clientMedian.HasValue;
      RunClock clock = run.Conditions.Clock;
      bool off = read && clock.Pinned && ( clock.IsOff( engineMedian!.Value ) || clock.IsOff( clientMedian!.Value ) );
      return new PassClock( engineMedian, clientMedian, read, off );
   }

   /// <summary>
   /// The clock summary of one claim session, checking every pass.
   /// </summary>
   /// <param name="runs">The session's runs.</param>
   /// <returns>The summary.</returns>
   /// <exception cref="InvalidDataException">A run's recorded pass medians or clockOff disagree with the recomputed ones.</exception>
   public static SessionClock ForSession( IReadOnlyList<RunResult> runs )
   {
      var clock = new SessionClock
      {
         Pinned = runs.All( r => r.Conditions.Clock.Pinned ),
         PinnedMhz = Same( runs.Select( r => r.Conditions.Clock.PinnedMhz ) ),
         NoTurbo = Same( runs.Select( r => r.Conditions.Clock.NoTurboDuring ) ),
         Uncore = runs.Select( r => r.Conditions.Clock.UncoreDuring ).Distinct().Count() == 1 ? runs[0].Conditions.Clock.UncoreDuring : null,
         CeilingBeforeMhz = Same( runs.Select( r => r.Conditions.Clock.CeilingBeforeMhz ) ),
         ToleranceBp = Same( runs.Select( r => (int?)r.Conditions.Clock.ToleranceBp ) ),
         LegacyParsed = runs.All( r => r.Conditions.Clock.LegacyParsed ),
      };
      foreach( RunResult run in runs )
      {
         foreach( PassFacts pass in run.Conditions.Passes )
         {
            PassClock found = Recompute( run, pass );
            CheckAgainstRecord( run, pass, found );
            clock.PassesEvaluated++;
            clock.ClockOffPasses += found.Off ? 1 : 0;
            clock.ClockUnreadPasses += found.Read ? 0 : 1;
         }
      }

      return clock;
   }

   /// <summary>
   /// The run's recorded mean-based clock warnings that the median rule does not raise.
   /// </summary>
   /// <param name="runs">Claim runs.</param>
   /// <returns>One entry per such warning, with the recomputed medians.</returns>
   public static List<DroppedWarning> Dropped( IEnumerable<RunResult> runs )
   {
      var dropped = new List<DroppedWarning>();
      foreach( RunResult run in runs )
      {
         foreach( string note in run.Notes.Concat( run.Conditions.Flags ).Distinct( StringComparer.Ordinal ) )
         {
            Match m = MEAN_WARNING.Match( note );
            PassFacts? pass = m.Success ? run.Conditions.Passes.LastOrDefault( p => p.Target == m.Groups["target"].Value && p.Pass == m.Groups["pass"].Value.TrimEnd( ':' ) ) : null;
            if( pass == null )
            {
               continue;
            }

            PassClock found = Recompute( run, pass );
            if( !found.Off )
            {
               dropped.Add( new DroppedWarning { Run = run.Name, Text = note, Target = pass.Target, Pass = pass.Pass, EngineMedianMhz = found.EngineMedian, ClientMedianMhz = found.ClientMedian } );
            }
         }
      }

      return dropped;
   }

   /// <summary>
   /// The median of the per-CPU medians of a set of CPUs, as machine control computes it (an even count gives the mean of the middle two, rounded half away from zero).
   /// </summary>
   /// <param name="medians">Median MHz per CPU.</param>
   /// <param name="set">The CPUs to take, or null for every CPU.</param>
   /// <returns>MHz, or null when none of the CPUs was sampled.</returns>
   public static int? MedianOf( IReadOnlyDictionary<int, int> medians, IReadOnlySet<int>? set )
   {
      List<int> picked = medians.Where( m => set == null || set.Contains( m.Key ) ).Select( m => m.Value ).OrderBy( v => v ).ToList();
      if( picked.Count == 0 )
      {
         return null;
      }

      int middle = picked.Count / 2;
      return picked.Count % 2 == 1 ? picked[middle] : (int)Math.Round( ( picked[middle - 1] + picked[middle] ) / 2.0, MidpointRounding.AwayFromZero );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Fails loud when the recomputed clock disagrees with what the run recorded: the group medians
   /// (v5 onward) and, for v8, clockRead and clockOff.
   /// </summary>
   /// <param name="run">The run.</param>
   /// <param name="pass">The pass.</param>
   /// <param name="found">The recomputed clock.</param>
   /// <exception cref="InvalidDataException">They disagree.</exception>
   private static void CheckAgainstRecord( RunResult run, PassFacts pass, PassClock found )
   {
      string where = $"{run.Name} {pass.Target} {pass.Pass}";
      if( pass.EngineMhzMedian.HasValue && pass.CpuMedians.Count > 0 && pass.EngineMhzMedian != found.EngineMedian )
      {
         throw new InvalidDataException( $"{where}: recorded engine median {pass.EngineMhzMedian} MHz, recomputed {found.EngineMedian} MHz from the per-CPU medians" );
      }

      if( pass.ClientMhzMedian.HasValue && pass.CpuMedians.Count > 0 && pass.ClientMhzMedian != found.ClientMedian )
      {
         throw new InvalidDataException( $"{where}: recorded client median {pass.ClientMhzMedian} MHz, recomputed {found.ClientMedian} MHz from the per-CPU medians" );
      }

      if( pass.ClockRead.HasValue && pass.ClockRead.Value != found.Read )
      {
         throw new InvalidDataException( $"{where}: recorded clockRead {pass.ClockRead}, recomputed {found.Read}" );
      }

      if( pass.ClockOff.HasValue && pass.ClockOff.Value != found.Off )
      {
         throw new InvalidDataException( $"{where}: recorded clockOff {pass.ClockOff}, recomputed {found.Off} (engine {found.EngineMedian} MHz, client {found.ClientMedian} MHz, pinned {run.Conditions.Clock.PinnedMhz} MHz, tolerance {run.Conditions.Clock.ToleranceBp} bp)" );
      }
   }

   /// <summary>
   /// The value when every item holds the same one, else null.
   /// </summary>
   /// <param name="values">Values.</param>
   /// <returns>The common value, or null.</returns>
   private static int? Same( IEnumerable<int?> values )
   {
      List<int?> distinct = values.Distinct().ToList();
      return distinct.Count == 1 ? distinct[0] : null;
   }

   #endregion Private Methods
}

/// <summary>
/// One pass's recomputed clock.
/// </summary>
/// <param name="EngineMedian">Median of the engine CPUs' medians, MHz.</param>
/// <param name="ClientMedian">Median of the client CPUs' medians, MHz.</param>
/// <param name="Read">True when every CPU group had a median.</param>
/// <param name="Off">True when either group's median lies outside the tolerance of the pinned clock.</param>
public sealed record PassClock( int? EngineMedian, int? ClientMedian, bool Read, bool Off );
