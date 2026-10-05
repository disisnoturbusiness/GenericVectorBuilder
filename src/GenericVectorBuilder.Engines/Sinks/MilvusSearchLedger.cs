using System.Globalization;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// The query node's search counters for one collection, read from Milvus's management port
/// (/metrics).
/// </summary>
/// <param name="SealedSegmentSearches">
/// milvus_querynode_sq_segment_latency_count with query_type "search" and segment_state "Sealed":
/// how many segment searches ran on sealed segments, node-wide (the metric has no collection label).
/// Concurrent searches can be merged into one segment search, so this can be lower than the number of requests.
/// </param>
/// <param name="GrowingSegmentSearches">The same counter for segment_state "Growing", the raw unindexed rows, node-wide.</param>
/// <param name="CollectionRequests">
/// milvus_querynode_sq_req_count with query_type "search", scope "OnLeader" and status "success" for
/// this collection's id: how many search requests the query node answered for it.
/// </param>
/// <param name="SegmentsCompactedAway">
/// How many segments of this collection the data coordinator lists as compacted away (a compaction
/// replaced them). Not a /metrics counter: those are node-wide, so another collection's compaction
/// moves them too, while this count is per collection. 0 when not read.
/// </param>
public sealed record MilvusSearchCounters( double SealedSegmentSearches, double GrowingSegmentSearches, double CollectionRequests, long SegmentsCompactedAway = 0 );

/// <summary>
/// Where the search ledger of one collection started: the counters, how many searches the sink had
/// sent, and which sealed segments (with which index builds) the query node served, all read at
/// the moment the index was proven ready.
/// </summary>
/// <param name="Counters">Query node counters at the start.</param>
/// <param name="SearchesSent">Searches the sink had sent for this collection at the start.</param>
/// <param name="Segments">Fingerprint of the served segments at the start (see <see cref="MilvusSearchLedger.Fingerprint"/>).</param>
public sealed record MilvusLedgerStart( MilvusSearchCounters Counters, long SearchesSent, string Segments );

/// <summary>
/// What one comparison of the ledger found.
/// </summary>
/// <param name="Problems">Plain-English reasons the searches since the start cannot be shown to have used the index; empty when they can.</param>
/// <param name="Detail">One line of evidence for the report.</param>
public sealed record MilvusLedgerReading( IReadOnlyList<string> Problems, string Detail );

/// <summary>
/// Turns the query node's search counters into proof that the searches a sink sent ran on sealed,
/// indexed segments and never on growing ones.
/// Why this exists: a readiness check made before the searches proves the index was built and
/// served at that moment, not that the timed searches used it. The proof here has three parts,
/// because each alone can mislead. Measured once on 2026-10-04 on this box: a sealed segment whose
/// index list was still empty was served five searches and the node-wide "Sealed" counter rose with
/// them, so the counter alone does not show an index was used, and a segment list read only before
/// the searches does not show where they ran. (1) At the start every segment holding rows is Sealed
/// with its index loaded (the sink's readiness check). (2) Between the two readings every search
/// the sink sent shows up as a search request for this collection, some segment searches show up
/// as Sealed, and no Growing segment search appears. (3) At the end the same sealed segments with
/// the same index builds are still served, and the data coordinator lists no new compacted-away
/// segment for this collection (a compaction while the searches run swaps the segment under them,
/// and the new one has no loaded index until its build is done). Why Sealed is only required to move and not to equal the
/// number of searches: measured 2026-10-04, 200 searches from one thread raised the Sealed counter
/// by exactly 200, but from 8 threads by 170 to 175 and from 16 threads by 175 of 192, while the
/// request counter rose by exactly the number sent every time. The query node merges searches that
/// arrive together into one segment search, so under concurrency the segment counter runs lower
/// than the request counter by design. A probe with a deliberately small ef is not used: on a loaded HNSW index
/// Milvus accepted ef 1 for 10 hits and returned 10 hits (3 of 4 runs; once it was refused, cause not
/// found), so its answer is not a reliable sign of the index in use.
/// Why the comparison is a pure function: the wording and the arithmetic are tested without a
/// server.
/// </summary>
public static class MilvusSearchLedger
{
   #region Data Members

   private const string SEGMENT_METRIC = "milvus_querynode_sq_segment_latency_count";
   private const string REQUEST_METRIC = "milvus_querynode_sq_req_count";
   private static readonly Regex LINE = new( @"^(?<name>milvus_querynode_sq_(?:segment_latency_count|req_count))\{(?<labels>[^}]*)\}\s+(?<value>\S+)\s*$",
      RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline );
   private static readonly Regex LABEL = new( "(?<key>\\w+)=\"(?<value>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Reads the search counters out of a Prometheus text page. Counters that do not exist yet (a
   /// counter appears only after its first event) count as zero.
   /// </summary>
   /// <param name="metrics">The body of the management port's /metrics page.</param>
   /// <param name="collectionId">Milvus's internal id of the collection.</param>
   /// <returns>The three counters, summed over query nodes.</returns>
   public static MilvusSearchCounters Parse( string metrics, long collectionId )
   {
      string id = collectionId.ToString( CultureInfo.InvariantCulture );
      double sealedSearches = 0;
      double growingSearches = 0;
      double requests = 0;
      foreach( Match line in LINE.Matches( metrics ) )
      {
         Dictionary<string, string> labels = LABEL.Matches( line.Groups["labels"].Value ).ToDictionary( m => m.Groups["key"].Value, m => m.Groups["value"].Value );
         if( labels.GetValueOrDefault( "query_type" ) != "search" || !double.TryParse( line.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value ) )
         {
            continue;
         }

         string name = line.Groups["name"].Value;
         if( name == SEGMENT_METRIC && labels.GetValueOrDefault( "segment_state" ) == "Sealed" )
         {
            sealedSearches += value;
         }
         else if( name == SEGMENT_METRIC && labels.GetValueOrDefault( "segment_state" ) == "Growing" )
         {
            growingSearches += value;
         }
         else if( name == REQUEST_METRIC && labels.GetValueOrDefault( "collection_id" ) == id && labels.GetValueOrDefault( "scope" ) == "OnLeader" && labels.GetValueOrDefault( "status" ) == "success" )
         {
            requests += value;
         }
      }

      return new MilvusSearchCounters( sealedSearches, growingSearches, requests );
   }

   /// <summary>
   /// A stable text for the set of segments the query node serves and the index builds on them,
   /// so two readings can be compared for "the same segments, the same indexes".
   /// </summary>
   /// <param name="segments">One entry per segment: segment id, state and the build ids of its loaded indexes.</param>
   /// <returns>The entries sorted and joined, or "none" when the node serves no segment.</returns>
   public static string Fingerprint( IEnumerable<( string SegmentId, string State, IEnumerable<string> IndexBuilds )> segments )
   {
      string[] entries = segments.Select( s => $"{s.SegmentId}:{s.State}:[{string.Join( "+", s.IndexBuilds.OrderBy( b => b, StringComparer.Ordinal ) )}]" ).OrderBy( e => e, StringComparer.Ordinal ).ToArray();
      return entries.Length == 0 ? "none" : string.Join( ",", entries );
   }

   /// <summary>
   /// Compares the counters now with the counters at the start and decides whether the searches
   /// in between can be shown to have run on the sealed, indexed segments.
   /// </summary>
   /// <param name="start">The ledger start.</param>
   /// <param name="now">Counters now.</param>
   /// <param name="searchesSentNow">Searches the sink has sent for this collection now.</param>
   /// <param name="segmentsNow">Fingerprint of the served segments now.</param>
   /// <param name="searchParams">How the sink's searches name their effort, e.g. "params.ef=100", for the line of evidence.</param>
   /// <returns>The problems found (none when the searches are shown to have used the index) and one line of evidence.</returns>
   public static MilvusLedgerReading Evaluate( MilvusLedgerStart start, MilvusSearchCounters now, long searchesSentNow, string segmentsNow, string searchParams )
   {
      long sent = searchesSentNow - start.SearchesSent;
      double sealedDelta = now.SealedSegmentSearches - start.Counters.SealedSegmentSearches;
      double growingDelta = now.GrowingSegmentSearches - start.Counters.GrowingSegmentSearches;
      double requestDelta = now.CollectionRequests - start.Counters.CollectionRequests;
      var problems = new List<string>();
      if( sealedDelta < 0 || growingDelta < 0 || requestDelta < 0 || sent < 0 )
      {
         problems.Add( "a search counter went down since the index finished, so Milvus restarted or the sink was recreated and the searches cannot be accounted for" );
      }

      if( growingDelta > 0 )
      {
         problems.Add( $"{growingDelta:0} segment searches ran on growing (unindexed) segments since the index finished (the counter is node-wide, so another collection's searches also count)" );
      }

      if( sent > 0 && sealedDelta <= 0 )
      {
         problems.Add( $"the sink sent {sent} searches but the query node counted no segment search on a sealed segment" );
      }

      if( sent > 0 && requestDelta < sent )
      {
         problems.Add( $"the sink sent {sent} searches but the query node counted only {requestDelta:0} search requests for this collection" );
      }

      if( segmentsNow != start.Segments )
      {
         problems.Add( $"the sealed segments or their index builds changed since the index finished (then {start.Segments}; now {segmentsNow})" );
      }

      long compacted = now.SegmentsCompactedAway - start.Counters.SegmentsCompactedAway;
      if( compacted > 0 )
      {
         problems.Add( $"{compacted} segment(s) of this collection were compacted away since the index finished, so a compaction ran while the searches were timed" );
      }

      string detail = $"search ledger since the index finished: the sink sent {sent} searches ({searchParams}), the query node counted {requestDelta:0} search requests for this collection, "
         + $"segment searches Sealed +{sealedDelta:0} and Growing +{growingDelta:0}, "
         + ( segmentsNow == start.Segments ? "the same sealed segments with the same loaded index builds at both readings" : "the served segments differ between the readings" )
         + $", segments compacted away +{Math.Max( compacted, 0 )}";
      return new MilvusLedgerReading( problems, problems.Count == 0 ? detail : $"{detail}; PROBLEM: {string.Join( "; ", problems )}" );
   }

   #endregion Public Methods
}
