using System.Text.Json;
using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The per-CPU clock figures of an observer summary: for each pass, the mean clock each CPU ran at by APERF and MPERF, and the mean of the sampled clock of the
/// engine CPUs and of the client CPUs. From them: the CPU that lay furthest from the pinned clock in each run, the pass that ran under the pin on its CPUs more than any
/// other in a session and how far under, how far every other pass stayed from the pin, and the passes whose sampled mean lay outside the clock tolerance.
/// Why per CPU and not per group: the summary's own check averages a pass's CPUs into an engine group and a client group, and that figure is smaller than the worst CPU's
/// (0.52% against 0.66% in v8). A sentence that says "the largest deviation in a pass" must say which of the two it is, and the worst CPU is the larger.
/// Why the mean dips: v7 flagged a pass by the mean of its sampled clock and the median rule does not, so a reader who sees both sessions needs to know that the same pass
/// dips by a mean in both.
/// Why it reads nothing when the summary holds no per-CPU figures: a summary of an older schema simply has no passes, and the report then states only the group figure.
/// </summary>
public static class ConsolidateObserverClock
{
   #region Public Methods

   /// <summary>
   /// Fills the per-CPU figures of an observer block from the summary's runs.
   /// </summary>
   /// <param name="info">The block, whose runs are filled.</param>
   /// <param name="entries">The summary's entry for each run of the block, in the order of the block's runs.</param>
   /// <param name="pinnedMhz">The clock the session's runs were pinned at, MHz; null when they were not (nothing is measured against it).</param>
   /// <param name="toleranceBp">The clock tolerance, bp.</param>
   public static void Fill( ObserverInfo info, IReadOnlyList<JsonElement> entries, int? pinnedMhz, int toleranceBp )
   {
      if( pinnedMhz is not int pin || pin <= 0 )
      {
         return;
      }

      var passes = new List<(string Run, string Target, string Pass, double[] Cpu, double? EngineMean, double? ClientMean)>();
      for( int r = 0; r < entries.Count; r++ )
      {
         if( ResultJson.Child( entries[r], "passes" ) is not { ValueKind: JsonValueKind.Array } list )
         {
            return;
         }

         foreach( JsonElement pass in list.EnumerateArray().Where( p => p.ValueKind == JsonValueKind.Object ) )
         {
            JsonElement? clock = ResultJson.Child( pass, "clock" ) is { ValueKind: JsonValueKind.Object } c ? c : null;
            double[] cpu = clock is JsonElement k && ResultJson.Child( k, "aperfCpuMhz" ) is { ValueKind: JsonValueKind.Array } a
               ? a.EnumerateArray().Where( v => v.ValueKind == JsonValueKind.Number ).Select( v => v.GetDouble() ).ToArray() : Array.Empty<double>();
            passes.Add( (info.Runs[r].Folder, ResultJson.Text( pass, "target" ) ?? string.Empty, ResultJson.Text( pass, "pass" ) ?? string.Empty, cpu,
               clock is JsonElement m ? ResultJson.Number( m, "freqEngineMean" ) : null, clock is JsonElement n ? ResultJson.Number( n, "freqClientMean" ) : null));
         }
      }

      if( !passes.Any( p => p.Cpu.Length > 0 ) )
      {
         return;
      }

      info.PinnedMhz = pin;
      Worst( info, passes.Select( p => (p.Run, p.Target, p.Pass, p.Cpu) ).ToList(), pin );
      info.Dip = Dip( passes.Select( p => (p.Run, p.Target, p.Pass, p.Cpu) ).ToList(), pin );
      foreach( var p in passes.Where( p => p.EngineMean.HasValue && p.ClientMean.HasValue ) )
      {
         if( Math.Abs( Bp( pin, p.EngineMean!.Value ) ) > toleranceBp || Math.Abs( Bp( pin, p.ClientMean!.Value ) ) > toleranceBp )
         {
            info.MeanDips.Add( new ObserverMeanDip { Run = p.Run, Target = p.Target, Pass = p.Pass, EngineMeanMhz = p.EngineMean!.Value, ClientMeanMhz = p.ClientMean!.Value } );
         }
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// How far under the pinned clock a reading is, bp (negative when over it).
   /// </summary>
   /// <param name="pin">The pinned clock, MHz.</param>
   /// <param name="mhz">The reading, MHz.</param>
   /// <returns>Basis points of the pin.</returns>
   private static double Bp( int pin, double mhz )
   {
      return ( pin - mhz ) / pin * 10000.0;
   }

   /// <summary>
   /// The CPU furthest from the pin in each run and in the session.
   /// </summary>
   /// <param name="info">The block.</param>
   /// <param name="passes">Every pass with its per-CPU clock.</param>
   /// <param name="pin">The pinned clock.</param>
   private static void Worst( ObserverInfo info, List<(string Run, string Target, string Pass, double[] Cpu)> passes, int pin )
   {
      foreach( ObserverRun run in info.Runs )
      {
         run.CpuWorst = WorstOf( passes.Where( p => p.Run == run.Folder ), pin );
      }

      info.CpuWorst = info.Runs.Select( r => r.CpuWorst ).OfType<ObserverCpuDeviation>().OrderByDescending( d => d.DeviationBp ).FirstOrDefault();
   }

   /// <summary>
   /// The CPU furthest from the pin, either way, over some passes.
   /// </summary>
   /// <param name="passes">The passes.</param>
   /// <param name="pin">The pinned clock.</param>
   /// <returns>The deviation, or null when no pass has a CPU reading.</returns>
   private static ObserverCpuDeviation? WorstOf( IEnumerable<(string Run, string Target, string Pass, double[] Cpu)> passes, int pin )
   {
      ObserverCpuDeviation? worst = null;
      foreach( (string run, string target, string pass, double[] cpu) in passes )
      {
         for( int i = 0; i < cpu.Length; i++ )
         {
            double bp = Bp( pin, cpu[i] );
            if( worst == null || Math.Abs( bp ) > worst.DeviationBp )
            {
               worst = new ObserverCpuDeviation { Run = run, Target = target, Pass = pass, Cpu = i, Mhz = cpu[i], DeviationBp = Math.Abs( bp ), Under = bp > 0 };
            }
         }
      }

      return worst;
   }

   /// <summary>
   /// The pass type with the largest deviation under the pin on any CPU, with how every reading of it lay and how far any other pass stayed from the pin.
   /// </summary>
   /// <param name="passes">Every pass with its per-CPU clock.</param>
   /// <param name="pin">The pinned clock.</param>
   /// <returns>The dip, or null when no CPU ran under the pin.</returns>
   private static ObserverDip? Dip( List<(string Run, string Target, string Pass, double[] Cpu)> passes, int pin )
   {
      var groups = passes.GroupBy( p => (p.Target, p.Pass) ).ToList();
      var under = groups.Select( g => (Group: g, Max: g.SelectMany( p => p.Cpu ).Select( v => Bp( pin, v ) ).DefaultIfEmpty( double.NegativeInfinity ).Max()) ).Where( x => x.Max > 0 ).OrderByDescending( x => x.Max ).ToList();
      if( under.Count == 0 )
      {
         return null;
      }

      IGrouping<(string Target, string Pass), (string Run, string Target, string Pass, double[] Cpu)> top = under[0].Group;
      List<double> readings = top.SelectMany( p => p.Cpu ).Select( v => Bp( pin, v ) ).ToList();
      List<double> below = readings.Where( bp => bp > 0 ).ToList();
      double other = groups.Where( g => g.Key != top.Key ).SelectMany( g => g ).SelectMany( p => p.Cpu ).Select( v => Math.Abs( Bp( pin, v ) ) ).DefaultIfEmpty( 0 ).Max();
      return new ObserverDip
      {
         Target = top.Key.Target, Pass = top.Key.Pass, Runs = top.Select( p => p.Run ).Distinct( StringComparer.Ordinal ).Count(), CpuReadings = readings.Count, CpusUnder = below.Count,
         EveryCpuUnder = below.Count == readings.Count, MinBp = below.Min(), MaxBp = below.Max(), OtherWorstBp = other,
      };
   }

   #endregion Private Methods
}
