using System.Diagnostics;
using System.Globalization;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Decides when mongot's segment layout has stopped changing, from a series of readings taken one
/// after another: the layout counts as steady once the index has been ready, with the same layout,
/// for at least a set time and a set number of readings.
/// Why: a vector search index that is READY, holds every document and searches every segment through
/// the HNSW graph can still be in the middle of a layout change (the v5 runs of 2026-10-05 ended
/// MongoDB with 4 segments in two runs and 2 in the third), and a timed search should not start while
/// the engine is still rearranging what it searches. The watch only judges the readings it is given;
/// it takes none, so the rule is tested without a server.
/// A layout is written as one comparable text (the segment count and each segment's document count,
/// see <see cref="MongoDbSink"/>); any change of that text, or a reading where the index is not
/// ready, starts the count again.
/// Measured on this box 2026-10-05 (524 random 1024-dimension vectors, the sink's own load and
/// readiness wait, one collection per load): a first set of three loads ended with 4, 2 and 3
/// segments and each layout stood unchanged for the 90 s it was watched after its first ready reading;
/// a second set of three, on a quieter box, ended with 2, 2 and 2 and each stood for the 15 s watched.
/// So the watch confirms that a layout is final; it cannot make two loads end alike, which is why a
/// layout that differs between runs is flagged in the consolidated report and not hidden.
/// </summary>
public sealed class MongoDbLayoutWatch
{
   #region Data Members

   private readonly TimeSpan _steadyFor;
   private readonly int _minimumReads;
   private readonly Func<TimeSpan> _clock;
   private string? _layout;
   private TimeSpan _since;
   private int _reads;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a watch.
   /// </summary>
   /// <param name="steadyFor">How long the same layout must be seen, ready, before it counts as steady; zero for readings alone.</param>
   /// <param name="minimumReads">How many ready readings in a row with the same layout are needed (at least 1).</param>
   /// <param name="clock">Time since the wait began; null to use a stopwatch started now. Tests pass their own.</param>
   public MongoDbLayoutWatch( TimeSpan steadyFor, int minimumReads, Func<TimeSpan>? clock = null )
   {
      _steadyFor = steadyFor;
      _minimumReads = Math.Max( 1, minimumReads );
      var stopwatch = Stopwatch.StartNew();
      _clock = clock ?? ( () => stopwatch.Elapsed );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>The layout text of the latest ready reading, or null when the last reading was not ready or none was taken.</summary>
   public string? Layout => _layout;

   /// <summary>How many ready readings in a row have shown the current layout.</summary>
   public int Reads => _reads;

   /// <summary>How long the current layout has been seen, ready, as of the latest reading.</summary>
   public TimeSpan SteadyFor => _reads == 0 ? TimeSpan.Zero : _clock() - _since;

   /// <summary>
   /// Takes one reading.
   /// </summary>
   /// <param name="ready">True when the index was ready on this reading (status READY, queryable, every document indexed, every segment searched through the graph).</param>
   /// <param name="layout">The layout text of this reading; ignored when the index was not ready.</param>
   /// <returns>True when the layout has now been steady for the whole time and the number of readings asked for.</returns>
   public bool Observe( bool ready, string layout )
   {
      if( !ready )
      {
         _layout = null;
         _reads = 0;
         return false;
      }

      if( _reads == 0 || !string.Equals( _layout, layout, StringComparison.Ordinal ) )
      {
         _layout = layout;
         _since = _clock();
         _reads = 1;
      }
      else
      {
         _reads++;
      }

      return _reads >= _minimumReads && _clock() - _since >= _steadyFor;
   }

   /// <summary>
   /// Writes a layout as one comparable text.
   /// </summary>
   /// <param name="segments">Number of segments.</param>
   /// <param name="documents">Each segment's document count, largest first, or none when the engine did not state them.</param>
   /// <returns>E.g. "2 segments holding 272 + 252 documents", or "3 segments" without counts.</returns>
   public static string Signature( int segments, IReadOnlyList<long> documents )
   {
      string noun = segments == 1 ? "segment" : "segments";
      return documents.Count == 0
         ? string.Create( CultureInfo.InvariantCulture, $"{segments} {noun}" )
         : string.Create( CultureInfo.InvariantCulture, $"{segments} {noun} holding {string.Join( " + ", documents )} documents" );
   }

   /// <summary>
   /// One sentence on how steady the layout was, for the load note.
   /// </summary>
   /// <returns>E.g. "segment layout unchanged for 10.1 s over 11 readings".</returns>
   public string Describe()
   {
      return _reads == 0
         ? "segment layout not read as ready"
         : string.Create( CultureInfo.InvariantCulture, $"segment layout unchanged for {SteadyFor.TotalSeconds:0.0} s over {_reads} reading{( _reads == 1 ? string.Empty : "s" )}" );
   }

   #endregion Public Methods
}
