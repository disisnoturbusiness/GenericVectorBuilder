using System.Globalization;
using System.Text.Json;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// What the query node serves for one collection, read from the management port.
/// </summary>
/// <param name="IndexedRows">Rows in sealed segments whose index is loaded (the rows a search covers with the index).</param>
/// <param name="GrowingRows">Rows still served from growing segments (searches scan these).</param>
/// <param name="SealedWithIndex">Sealed segments with their index loaded.</param>
/// <param name="SealedWithoutIndex">Sealed segments without a loaded index.</param>
/// <param name="Segments">Fingerprint of every served segment and its index builds (see <see cref="MilvusSearchLedger.Fingerprint"/>).</param>
public sealed record MilvusNodeView( long IndexedRows, long GrowingRows, int SealedWithIndex, int SealedWithoutIndex, string Segments );

/// <summary>
/// What the data coordinator lists for one collection: the segments that exist (not dropped), and
/// how many were already compacted away.
/// Why this view exists: the query node says what is served now, but only the data coordinator
/// says that a compaction is waiting (a live level-zero segment holds delete records that have not
/// been applied yet) or that a compaction ran (a segment marked compacted). Both are per
/// collection, unlike the node-wide compaction counters on /metrics, which another collection's
/// work moves too.
/// </summary>
/// <param name="LiveSegments">Segments that are not dropped, level zero included.</param>
/// <param name="FlushedIndexedSegments">Live flushed data segments (level L1 or L2) whose index build is finished.</param>
/// <param name="FlushedRows">Rows in the live flushed data segments.</param>
/// <param name="DeleteLogSegments">Live level-zero segments: delete records that a level-zero compaction has not applied yet.</param>
/// <param name="NotYetFlushedSegments">Live data segments that are not flushed, or flushed but not indexed yet.</param>
/// <param name="CompactedAway">Segments of this collection that a compaction replaced (listed with compacted true, whatever their state).</param>
/// <param name="Fingerprint">The live segment ids with state and level, sorted, so two readings can be compared.</param>
public sealed record MilvusDatacoordView( int LiveSegments, int FlushedIndexedSegments, long FlushedRows, int DeleteLogSegments, int NotYetFlushedSegments, int CompactedAway, string Fingerprint );

/// <summary>
/// The verdict on one reading of a collection: whether searches now use a finished index over
/// every stored row, whether stored rows still sit in growing segments, and why not when not.
/// </summary>
/// <param name="Ready">True only when every check holds.</param>
/// <param name="Unsealed">True when stored rows are still in growing segments, so another flush is needed.</param>
/// <param name="Waiting">Plain-English reasons the layout is not final yet; empty when it is.</param>
public sealed record MilvusVerdict( bool Ready, bool Unsealed, IReadOnlyList<string> Waiting );

/// <summary>
/// Reads the data coordinator's segment list and judges whether the segment layout of a
/// collection is final: one reading from Milvus's own views, with no server needed to test it.
/// Why the judgement is a pure function: the benchmark's claim "the index is built, every stored
/// row is covered by it, and nothing is about to be compacted" rests on this arithmetic, and it
/// must be tested with the exact readings that went wrong. Measured 2026-10-04: a collection
/// loaded with upserts (which write delete records) was compacted about 80 seconds after the
/// load, during the timed searches, and for about 15 seconds the query node served the old and
/// the new segment together (2 sealed segments covering 1,048 rows for 524 stored); the new segment
/// then had no loaded index until its index build ran. A check of "covered rows at least stored
/// rows" passed on that overlap, so the comparison here is equality.
/// </summary>
public static class MilvusLayout
{
   #region Data Members

   private const string DROPPED = "Dropped";
   private const string FLUSHED = "Flushed";
   private const string LEVEL_ZERO = "L0";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Reads the data coordinator's segment list (/api/v1/_dc/segments?collection_id=N&amp;in=dc).
   /// Milvus answers "null" for a collection it lists no segments for, which reads as empty.
   /// </summary>
   /// <param name="json">The body of the management port's answer.</param>
   /// <returns>The counts and the fingerprint.</returns>
   public static MilvusDatacoordView ParseDatacoord( string json )
   {
      int live = 0;
      int flushedIndexed = 0;
      long flushedRows = 0;
      int deleteLogs = 0;
      int notYet = 0;
      int compacted = 0;
      var ids = new List<string>();
      using JsonDocument document = JsonDocument.Parse( json );
      if( document.RootElement.ValueKind == JsonValueKind.Array )
      {
         foreach( JsonElement segment in document.RootElement.EnumerateArray() )
         {
            string state = Text( segment, "state" );
            string level = segment.TryGetProperty( "level", out JsonElement l ) ? l.GetString() ?? "L1" : "L1";
            compacted += Flag( segment, "compacted" ) ? 1 : 0;
            if( state == DROPPED )
            {
               continue;
            }

            live++;
            ids.Add( $"{Text( segment, "segment_id" )}:{state}:{level}" );
            if( level == LEVEL_ZERO )
            {
               deleteLogs++;
            }
            else if( state == FLUSHED && Flag( segment, "is_indexed" ) )
            {
               flushedIndexed++;
               flushedRows += Rows( segment );
            }
            else
            {
               notYet++;
            }
         }
      }

      ids.Sort( StringComparer.Ordinal );
      return new MilvusDatacoordView( live, flushedIndexed, flushedRows, deleteLogs, notYet, compacted, ids.Count == 0 ? "none" : string.Join( ",", ids ) );
   }

   /// <summary>
   /// Decides whether searches now use a finished index over exactly the stored rows: the index
   /// description says Finished, type HNSW, nothing pending, every sealed row indexed and equal to
   /// the stored rows; the collection is loaded; the query node serves only sealed segments with
   /// their index loaded and they cover exactly the stored rows; and the data coordinator lists no
   /// data segment still waiting to be flushed or indexed. Delete-log (L0) segments that wait for a
   /// compaction do not make the reading not ready, because they change nothing that a search
   /// returns; <see cref="MilvusSink.FinishLoadAsync"/> compacts them away and the search ledger
   /// flags a compaction that runs later.
   /// </summary>
   /// <param name="indexState">The index description's state, e.g. Finished.</param>
   /// <param name="indexType">The index description's type, e.g. HNSW.</param>
   /// <param name="indexedRows">Rows the index description says are indexed.</param>
   /// <param name="sealedRows">Rows the index description says are in sealed segments.</param>
   /// <param name="pendingRows">Rows the index description says still wait for an index build.</param>
   /// <param name="stored">Rows stored, a Strong count(*).</param>
   /// <param name="loadState">The collection's load state, e.g. LoadStateLoaded.</param>
   /// <param name="node">What the query node serves.</param>
   /// <param name="datacoord">What the data coordinator lists, or null when it was not read.</param>
   /// <returns>The verdict, with the reasons when it is not ready.</returns>
   public static MilvusVerdict Judge( string indexState, string indexType, long indexedRows, long sealedRows, long pendingRows, long stored, string loadState, MilvusNodeView node, MilvusDatacoordView? datacoord )
   {
      var waiting = new List<string>();
      if( indexState != "Finished" )
      {
         waiting.Add( $"the index state is {indexState}, not Finished" );
      }

      if( indexType != "HNSW" )
      {
         waiting.Add( $"the index type is {indexType}, not HNSW" );
      }

      if( pendingRows != 0 )
      {
         waiting.Add( $"{pendingRows} rows wait for an index build" );
      }

      if( indexedRows != sealedRows )
      {
         waiting.Add( $"the index covers {indexedRows} of {sealedRows} sealed rows" );
      }

      if( sealedRows != stored )
      {
         waiting.Add( $"{sealedRows} rows are in sealed segments but {stored} are stored" );
      }

      if( loadState != "LoadStateLoaded" )
      {
         waiting.Add( $"the collection is {loadState}, not loaded" );
      }

      AddNodeReasons( waiting, node, stored );
      AddDatacoordReasons( waiting, datacoord );
      bool unsealed = sealedRows < stored || node.GrowingRows > 0;
      return new MilvusVerdict( waiting.Count == 0, unsealed, waiting );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Adds the reasons from the query node's view: growing rows, sealed segments without a loaded
   /// index, and covered rows that differ from the stored rows (more than stored is as wrong as
   /// fewer: it is two generations of one segment served together during a compaction).
   /// </summary>
   /// <param name="waiting">The list to add to.</param>
   /// <param name="node">What the query node serves.</param>
   /// <param name="stored">Rows stored.</param>
   private static void AddNodeReasons( List<string> waiting, MilvusNodeView node, long stored )
   {
      if( node.GrowingRows > 0 )
      {
         waiting.Add( $"{node.GrowingRows} rows are served from growing segments" );
      }

      if( node.SealedWithoutIndex > 0 )
      {
         waiting.Add( $"{node.SealedWithoutIndex} sealed segment(s) are served without a loaded index" );
      }

      if( node.IndexedRows > stored )
      {
         waiting.Add( $"the query node serves {node.IndexedRows} indexed rows for {stored} stored (a compaction is handing over, or deleted rows are still in sealed segments)" );
      }
      else if( node.IndexedRows < stored )
      {
         waiting.Add( $"the query node serves {node.IndexedRows} indexed rows for {stored} stored" );
      }
   }

   /// <summary>
   /// Adds the reason from the data coordinator's view: data segments not flushed and indexed yet.
   /// </summary>
   /// <param name="waiting">The list to add to.</param>
   /// <param name="datacoord">What the data coordinator lists, or null when it was not read.</param>
   private static void AddDatacoordReasons( List<string> waiting, MilvusDatacoordView? datacoord )
   {
      if( datacoord is null )
      {
         return;
      }

      if( datacoord.NotYetFlushedSegments > 0 )
      {
         waiting.Add( $"{datacoord.NotYetFlushedSegments} segment(s) are not flushed and indexed yet according to the data coordinator" );
      }
   }

   /// <summary>
   /// Reads a string field, or empty when it is missing or not a string.
   /// </summary>
   /// <param name="element">The JSON object.</param>
   /// <param name="key">Field name.</param>
   /// <returns>The value.</returns>
   private static string Text( JsonElement element, string key )
   {
      return element.TryGetProperty( key, out JsonElement value ) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
   }

   /// <summary>
   /// Reads a boolean field that Milvus omits when it is false.
   /// </summary>
   /// <param name="element">The JSON object.</param>
   /// <param name="key">Field name.</param>
   /// <returns>True only when the field is the JSON value true.</returns>
   private static bool Flag( JsonElement element, string key )
   {
      return element.TryGetProperty( key, out JsonElement value ) && value.ValueKind == JsonValueKind.True;
   }

   /// <summary>
   /// Reads a segment's row count, which Milvus writes as a string and omits for a level-zero segment.
   /// </summary>
   /// <param name="segment">One entry of the segment list.</param>
   /// <returns>The rows, 0 when missing.</returns>
   private static long Rows( JsonElement segment )
   {
      return long.TryParse( Text( segment, "num_of_rows" ), NumberStyles.Integer, CultureInfo.InvariantCulture, out long rows ) ? rows : 0;
   }

   #endregion Private Methods
}
