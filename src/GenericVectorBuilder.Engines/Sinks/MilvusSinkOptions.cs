namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Where Milvus is and how its HNSW index is built. The defaults match the local container
/// started by deploy/engines/milvus.compose.yaml (REST API on host port 19530).
/// Why these index numbers: M=16 and ef_construction=128 are the values every engine in the
/// benchmark uses where it can be tuned. Query ef is left to Milvus unless set here.
/// </summary>
/// <param name="BaseUrl">Server address, e.g. http://127.0.0.1:19530.</param>
/// <param name="M">HNSW M, the links kept per node.</param>
/// <param name="EfConstruction">HNSW candidate list size while building.</param>
/// <param name="Ef">HNSW candidate list size while searching, or null to let Milvus choose.</param>
/// <param name="SearchConsistency">Milvus consistency level for searches: Strong, Bounded, Session or Eventually.</param>
/// <param name="ManagementUrl">
/// Milvus's health and management port (9091 in milvus.compose.yaml). Why it is here: the REST API on
/// the main port says when the index is built, but only the management port says which segments the
/// query node serves and whether each has its index loaded, and a search uses the index only once
/// the query node serves the sealed, indexed segment instead of the raw growing one.
/// </param>
/// <param name="IndexWaitMinutes">Longest <see cref="MilvusSink.FinishLoadAsync"/> waits for the index before it fails with a plain message.</param>
/// <param name="CompactionWaitSeconds">
/// Longest the compaction step of <see cref="MilvusSink.FinishLoadAsync"/> may take (trigger the
/// compaction, wait for it, wait for the new segment to be indexed and served, wait for the layout
/// to stay unchanged) before it fails with a plain message. Why seconds: a test of the deadline must
/// not take minutes. The default is as long as the index wait, because a compaction that merges
/// segments builds a new index.
/// </param>
/// <param name="LayoutQuietSeconds">
/// How long the segment layout must stay unchanged, with Milvus saying it has nothing left to
/// compact, before it counts as final. Why 75: milvus.yaml of the pinned image (v2.6.25) checks for
/// mix compactions every 60 seconds (dataCoord.compaction.mix.triggerInterval) and for level-zero
/// compactions every 10 seconds (dataCoord.compaction.levelzero.triggerInterval), so a quiet window
/// shorter than a mix check can end just before the compaction a load makes due. Measured
/// 2026-10-04 on this box with no manual compaction: sealed and indexed 19 s after the load, the
/// level-zero compaction at 48 s and the mix compaction at 98 s.
/// </param>
/// <param name="PollMilliseconds">How long the wait loops sleep between readings. Why an option: the tests that drive the loops against a scripted server must not take a second per reading.</param>
public sealed record MilvusSinkOptions(
   string BaseUrl = "http://127.0.0.1:19530",
   int M = 16,
   int EfConstruction = 128,
   int? Ef = 100,
   string SearchConsistency = "Strong",
   string ManagementUrl = "http://127.0.0.1:9091",
   int IndexWaitMinutes = 30,
   int CompactionWaitSeconds = 1800,
   int LayoutQuietSeconds = 75,
   int PollMilliseconds = 1000 );
