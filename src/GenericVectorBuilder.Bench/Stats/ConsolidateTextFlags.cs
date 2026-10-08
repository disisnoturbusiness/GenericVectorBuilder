using GenericVectorBuilder.Bench.Report;
using S = GenericVectorBuilder.Bench.Stats.ConsolidateSources;
using T = GenericVectorBuilder.Bench.Stats.ConsolidateText;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The sentence of every flag on a table row, one per piece of evidence, each bound to the
/// results.json paths <see cref="ConsolidateFlags"/> found it at (and to the pass's target and name,
/// so a wrong pass index cannot pass the audit).
/// </summary>
public static class ConsolidateTextFlags
{
   #region Public Methods

   /// <summary>
   /// Writes a row's flag sentences and stores each text on the row's flags.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="metric">Metric id.</param>
   /// <param name="row">The row, flag codes filled by the consolidator.</param>
   /// <exception cref="InvalidOperationException">The evidence does not line up with the codes the consolidator set (a bug guard).</exception>
   public static void Write( Writer w, string metric, MetricRow row )
   {
      bool oneSession = row.Status == ClaimRule.ONE_SESSION && w.TwoSessions;
      IReadOnlyList<RunResult> shown = oneSession ? w.Newest.Runs : w.Sessions.SelectMany( s => s.Runs ).ToList();
      List<FlagEvidence> evidence = ConsolidateFlags.For( row.Target, metric, shown );
      int offset = oneSession ? 1 : 0;
      if( row.Flags.Count != evidence.Count + offset )
      {
         throw new InvalidOperationException( $"{row.Target} {metric}: {row.Flags.Count} flags but {evidence.Count + offset} pieces of evidence" );
      }

      string slot = $"flag.{metric}.{row.Target}";
      if( oneSession )
      {
         row.Flags[0].Text = OneSession( w, slot + ".0", row.Target );
      }

      for( int i = 0; i < evidence.Count; i++ )
      {
         if( row.Flags[i + offset].Code != evidence[i].Code )
         {
            throw new InvalidOperationException( $"{row.Target} {metric}: flag {i + offset} is {row.Flags[i + offset].Code}, evidence says {evidence[i].Code}" );
         }

         row.Flags[i + offset].Text = One( w, $"{slot}.{i + offset}", evidence[i] );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The sentence of one piece of evidence.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="slot">Its slot.</param>
   /// <param name="e">The evidence.</param>
   /// <returns>The text.</returns>
   private static string One( Writer w, string slot, FlagEvidence e )
   {
      var sources = e.Code == ConsolidateFlags.NOT_HELD ? new List<SentenceSource> { S.Token( e.Run, e.Values[0].Path, e.Values[0].Value ) } : e.Values.Select( v => S.Result( e.Run, v.Path, v.Value ) ).ToList();
      if( e.PassIndex is int index )
      {
         sources.Add( S.Token( e.Run, $"conditions.passes[{index}].target", e.Target ) );
         sources.Add( S.Token( e.Run, $"conditions.passes[{index}].pass", e.Pass! ) );
      }

      string pass = e.Pass == null ? string.Empty : T.PassLabel( e.Pass );
      string V( int i ) => e.Values[i].Value;
      string text = e.Code switch
      {
         ConsolidateFlags.BUSY_BOX => T.Fill( T.FLAG_BUSY, e.Run, pass, V( 0 ) ),
         ConsolidateFlags.CLOCK_OFF => T.Fill( T.FLAG_CLOCK_OFF, e.Run, pass, V( 0 ), V( 1 ), Pinned( w, e.Run, sources ) ),
         ConsolidateFlags.CLOCK_NOT_READ => T.Fill( T.FLAG_CLOCK_NOT_READ, e.Run, pass ),
         ConsolidateFlags.GOVERNOR => T.Fill( T.FLAG_GOVERNOR, e.Run, pass, V( 0 ) ),
         ConsolidateFlags.THROTTLE => T.Fill( T.FLAG_THROTTLE, e.Run, e.Target, V( 0 ), V( 1 ) ),
         ConsolidateFlags.UNSETTLED => T.Fill( T.FLAG_UNSETTLED, e.Run, e.Target ),
         ConsolidateFlags.NOT_HELD => T.Fill( T.FLAG_NOT_HELD, e.Run, pass, e.Target, V( 1 ), V( 2 ), V( 3 ), V( 4 ) ),
         ConsolidateFlags.INDEX_NOT_READY => T.Fill( T.FLAG_INDEX, e.Run, e.Target, e.Pass == "afterLoad" ? T.AFTER_LOAD : T.AFTER_SEARCH, V( 0 ), V( 1 ) ),
         ConsolidateFlags.SEGMENT_LAYOUT => T.Fill( T.FLAG_LAYOUT, e.Run, e.Target, V( 0 ) ),
         ConsolidateFlags.SEARCH_ERRORS => T.Fill( T.FLAG_SEARCH_ERRORS, e.Run, V( 0 ), e.Target ),
         ConsolidateFlags.WARMUP_ERRORS => T.Fill( T.FLAG_WARMUP_ERRORS, e.Run, V( 0 ), e.Target ),
         _ => throw new InvalidOperationException( $"no template for flag {e.Code}" ),
      };
      if( e.Code is ConsolidateFlags.SEGMENT_LAYOUT )
      {
         sources = new List<SentenceSource> { S.Token( e.Run, e.Values[0].Path, V( 0 ) ) };
      }
      else if( e.Code is ConsolidateFlags.UNSETTLED )
      {
         sources.Add( Unsettled( w, e ) );
      }

      sources.Add( w.RunSource( e.Run ) );

      return w.Add( slot, text, sources );
   }

   /// <summary>
   /// The source of an unsettled flag: the run's own words for it, or its settled field.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="e">The evidence.</param>
   /// <returns>The source.</returns>
   private static SentenceSource Unsettled( Writer w, FlagEvidence e )
   {
      TargetResult t = w.Sessions.SelectMany( s => s.Runs ).First( r => r.Name == e.Run ).Find( e.Target )!;
      return t.SettleDetail != null ? S.Token( e.Run, $"targets[{e.Target}]", t.SettleDetail ) : S.Result( e.Run, $"targets[{e.Target}].settled", "false" );
   }

   /// <summary>
   /// The pinned clock of the session a run belongs to, with its source.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="run">Run folder.</param>
   /// <param name="sources">Receives the source.</param>
   /// <returns>The text.</returns>
   private static string Pinned( Writer w, string run, List<SentenceSource> sources )
   {
      string session = w.Sessions.First( s => s.Runs.Any( r => r.Name == run ) ).Name;
      string mhz = S.Whole( w.Report.Clock.PerSession[session].PinnedMhz ?? 0 );
      sources.Add( S.Consolidated( $"clock.perSession.{session}.pinnedMhz", mhz ) );
      return mhz;
   }

   /// <summary>
   /// The one-session flag of a G3 target.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="slot">Its slot.</param>
   /// <param name="target">Target.</param>
   /// <returns>The text.</returns>
   private static string OneSession( Writer w, string slot, string target )
   {
      SetupChange change = w.Report.Guards.G3.Targets.First( t => t.Target == target );
      return w.Add( slot, T.Fill( T.FLAG_ONE_SESSION, target, w.Sessions[0].Name, w.Sessions[1].Name, S.List( change.Fields ) ),
         change.Fields.Select( ( f, i ) => S.Consolidated( $"guards.g3.targets[target={target}].fields[{i}]", f ) ) );
   }

   #endregion Private Methods
}
