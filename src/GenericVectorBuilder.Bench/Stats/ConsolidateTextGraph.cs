using System.Globalization;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Bench.Report;
using S = GenericVectorBuilder.Bench.Stats.ConsolidateSources;
using T = GenericVectorBuilder.Bench.Stats.ConsolidateText;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The caveat on a search fact that says an engine walked an HNSW graph although the engine reported the segment sizes and every
/// segment is smaller than the size at which another engine in the same runs recorded that a segment gets a graph.
/// Why: MongoDB's search fact rests on mongot's own report (executionType Approximate, "searched through the HNSW graph"). Its
/// segments held 2, 2, 251 and 269 vectors in one run, all under the 1,043 vectors at which Elasticsearch's recorded text says a
/// Lucene segment of 1,024 dimensions gets a graph, and Elasticsearch's own run found no graph below that size. Nothing in the
/// runs shows mongot's graph files, so the report says the fact is the engine's own report and that no graph was checked.
/// The sentence is generated from the recorded segment sizes and the recorded graph point, and it appears only when both exist.
/// </summary>
public static class ConsolidateTextGraph
{
   #region Data Members

   /// <summary>The recorded graph point: "a segment under 1,043 vectors gets no graph".</summary>
   private static readonly Regex GRAPH_POINT = new( @"under (?<n>\d[\d,]*) vectors gets no graph", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds( 5 ) );

   /// <summary>The recorded segment sizes: "(269 + 251 + 2 + 2 documents)".</summary>
   private static readonly Regex SEGMENT_SIZES = new( @"\((?<sizes>\d+(?:\s*\+\s*\d+)*)\s+documents\)", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds( 5 ) );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Writes the caveat of every reported target it applies to.
   /// </summary>
   /// <param name="w">The writer.</param>
   public static void Write( Writer w )
   {
      List<RunResult> runs = w.Sessions.SelectMany( s => s.Runs ).ToList();
      ( string Source, string Token, long Point )? point = GraphPoint( runs );
      if( point == null )
      {
         return;
      }

      foreach( WhyRow row in w.Report.Why.Where( r => r.Target != point.Value.Source ) )
      {
         WhyFact? search = row.Facts.FirstOrDefault( f => f.Kind == "search" );
         if( search == null || search.Mode != "approximate" || search.Confidence != "measured" || !search.Source.Contains( "load.indexNote", StringComparison.Ordinal ) )
         {
            continue;
         }

         ( RunResult Run, long Size, string Token )? largest = Largest( runs, row.Target );
         if( largest != null && largest.Value.Size < point.Value.Point )
         {
            Add( w, row.Target, search, largest.Value, point.Value );
         }
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The first recorded graph point in the runs' index texts.
   /// </summary>
   /// <param name="runs">The claim runs.</param>
   /// <returns>The target that recorded it, the recorded words and the size, or null when no run recorded one.</returns>
   private static ( string Source, string Token, long Point )? GraphPoint( List<RunResult> runs )
   {
      foreach( TargetResult t in runs.SelectMany( r => r.Targets ) )
      {
         Match m = t.Index == null ? Match.Empty : GRAPH_POINT.Match( t.Index );
         if( m.Success && long.TryParse( m.Groups["n"].Value.Replace( ",", string.Empty ), NumberStyles.Integer, CultureInfo.InvariantCulture, out long size ) )
         {
            return ( t.Name, m.Value, size );
         }
      }

      return null;
   }

   /// <summary>
   /// The largest segment a target reported in any claim run, when every run reported its segment sizes.
   /// </summary>
   /// <param name="runs">The claim runs.</param>
   /// <param name="target">Target.</param>
   /// <returns>The run that reported it, its size and the digits as written; null when a run reported none.</returns>
   private static ( RunResult Run, long Size, string Token )? Largest( List<RunResult> runs, string target )
   {
      ( RunResult Run, long Size, string Token )? best = null;
      foreach( RunResult run in runs )
      {
         Match m = SEGMENT_SIZES.Match( run.Find( target )?.LoadIndexNote ?? string.Empty );
         if( !m.Success )
         {
            return null;
         }

         foreach( string size in m.Groups["sizes"].Value.Split( '+', StringSplitOptions.TrimEntries ) )
         {
            if( !long.TryParse( size, NumberStyles.None, CultureInfo.InvariantCulture, out long value ) )
            {
               return null;
            }

            if( best == null || value > best.Value.Size )
            {
               best = ( run, value, size );
            }
         }
      }

      return best;
   }

   /// <summary>
   /// The sentence.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="target">The target with the caveat.</param>
   /// <param name="search">Its search fact.</param>
   /// <param name="largest">The largest segment and the run that reported it.</param>
   /// <param name="point">The graph point and who recorded it.</param>
   private static void Add( Writer w, string target, WhyFact search, ( RunResult Run, long Size, string Token ) largest, ( string Source, string Token, long Point ) point )
   {
      string size = S.Whole( largest.Size );
      string graph = point.Point.ToString( "N0", CultureInfo.InvariantCulture );
      w.Add( $"disclosure.graph.{target}", T.Fill( T.DISCLOSURE_GRAPH, target, size, graph, point.Source ),
         S.Fact( search ), S.Token( largest.Run.Name, $"targets[{target}].load.indexNote", largest.Token ), S.TokenAll( $"targets[{point.Source}].index", point.Token ) );
   }

   #endregion Private Methods
}
