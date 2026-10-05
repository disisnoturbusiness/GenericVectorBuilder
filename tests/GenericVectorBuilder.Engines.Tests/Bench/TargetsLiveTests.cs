using System.Text.RegularExpressions;
using GenericVectorBuilder.Engines.Common;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The target code against the real native SQL Server and Qdrant on this box (the sinks behind the
/// four built-in targets, and the native comparison targets), loaded with 524 random 1024-dimension
/// vectors (the size of the eShopOnWeb corpus). The built-in targets themselves run in the
/// benchmark's own containers and are checked end to end by <see cref="TargetsPinnedLiveTests"/>.
/// Every Qdrant collection starts with gvbbench_ and every SQL database is a throwaway named
/// GvbBenchTgt*, dropped when the test ends; nothing else on either server is read or written.
/// Why live: the verdict code is tested with hand-made facts elsewhere, but only the real
/// servers can show that the facts it reads (telemetry counters, the vector index catalog, the
/// actual plan) are really there and say what the code thinks they say.
/// </summary>
[Trait( "Category", "Live" )]
public class TargetsLiveTests
{
   #region Data Members

   private const int COUNT = 524;
   private const int DIMENSION = 1024;
   private static readonly TimeSpan LIMIT = TimeSpan.FromMinutes( 10 );
   private static readonly Lazy<Task<dynamic>> QDRANT = new( () => TargetsHarness.CallAsync( "QdrantLiveAsync", LIMIT, COUNT, DIMENSION ) );
   private static readonly Lazy<Task<dynamic>> SQL = new( () => TargetsHarness.CallAsync( "SqlLiveAsync", LIMIT, COUNT, DIMENSION ) );

   private readonly ITestOutputHelper _output;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the test with xunit's output sink.
   /// </summary>
   /// <param name="output">Where the evidence is printed.</param>
   public TargetsLiveTests( ITestOutputHelper output )
   {
      _output = output;
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// At 524 vectors the HNSW target really builds a graph: the server reports every vector in
   /// an HNSW segment after the index step, default searches walk it (the server's own counters),
   /// and they find the stored vector first. Before this fix both Qdrant targets were scans.
   /// </summary>
   [Fact]
   public async Task QdrantHnsw_BuildsAndWalksARealGraph()
   {
      dynamic r = await QDRANT.Value;
      IndexState afterLoad = r.AfterLoad;
      IndexState afterSearch = r.AfterSearch;
      _output.WriteLine( $"finish ({r.FinishSeconds:0.0} s): {r.FinishNote}" );
      _output.WriteLine( "after load: " + afterLoad );
      _output.WriteLine( "after search: " + afterSearch );
      Assert.True( afterLoad.Ready, afterLoad.Detail );
      Assert.Equal( (long?)COUNT, afterLoad.IndexedVectors );
      Assert.Equal( (long?)COUNT, afterLoad.TotalVectors );
      Assert.Contains( $"indexed_vectors_count {COUNT} of {COUNT} points", afterLoad.Detail );
      Assert.True( afterSearch.Ready, afterSearch.Detail );
      Assert.Matches( @"unfiltered_hnsw [1-9][\d,]*, unfiltered_plain 0,", afterSearch.Detail );
      Assert.Contains( "none scanned", afterSearch.Detail );
      Assert.True( (int)r.Top1Hits >= (int)r.Queries - 1, $"{r.Top1Hits} of {r.Queries} default searches found the stored vector first" );
   }

   /// <summary>
   /// The exact Qdrant target is ready with its evidence: at 524 vectors the server's default
   /// thresholds build no graph at all, and every search was counted as a plain scan.
   /// </summary>
   [Fact]
   public async Task QdrantExact_IsAScanByDesign()
   {
      dynamic r = await QDRANT.Value;
      IndexState state = r.ExactState;
      _output.WriteLine( $"exact target after searches: indexed {r.DefaultIndexed} of {r.DefaultPoints}; " + state );
      Assert.True( state.Ready, state.Detail );
      Assert.Contains( "no index used, exact scan by design", state.Detail );
      Assert.Equal( 0L, (long)r.DefaultIndexed );
      Assert.Equal( (long)COUNT, (long)r.DefaultPoints );
      Assert.Matches( @"unfiltered_hnsw 0, unfiltered_plain [1-9][\d,]*,", state.Detail );
   }

   /// <summary>
   /// The reasons behind the HNSW target's two thresholds, on the real server: indexing_threshold_kb 0
   /// does not help (0 disables indexing), and indexing_threshold_kb 1 alone builds a graph that
   /// searches do not walk, which the target's own state check calls not ready.
   /// </summary>
   [Fact]
   public async Task QdrantControls_ShowWhyTheThresholdsAreSet()
   {
      dynamic r = await QDRANT.Value;
      IndexState before = r.OneKbBefore;
      IndexState after = r.OneKbAfter;
      _output.WriteLine( $"indexing_threshold_kb 0: indexed {r.ZeroIndexed} of {COUNT}" );
      _output.WriteLine( "indexing_threshold_kb 1, full_scan_threshold_kb default, before searches: " + before );
      _output.WriteLine( "the same after default searches: " + after );
      Assert.Equal( 0L, (long)r.ZeroIndexed );
      Assert.False( before.Ready, before.Detail );
      Assert.Equal( (long?)COUNT, before.IndexedVectors );
      Assert.Contains( "smaller than full_scan_threshold, so searches scan them", before.Detail );
      Assert.False( after.Ready, after.Detail );
      Assert.Contains( "scanned instead of walking the graph", after.Detail );
   }

   /// <summary>
   /// The DiskANN index exists and is used, on the real SQL Server: the catalog row is an enabled
   /// DiskANN index whose graph table covers every row, the actual plan of a real VECTOR_SEARCH
   /// has a Vector Index Seek on it, and the exact mode scans the SAME table.
   /// </summary>
   [Fact]
   public async Task SqlDiskAnn_IndexExistsAndIsUsed()
   {
      dynamic r = await SQL.Value;
      IndexState state = r.AnnState;
      _output.WriteLine( $"finish ({r.FinishSeconds:0.0} s): {r.FinishNote}" );
      _output.WriteLine( "state: " + state );
      _output.WriteLine( $"default vs exact on the same table: {r.Overlap} of {r.PossibleHits} hits shared; exact finds the stored vector first {r.ExactTop1} of 20 times" );
      Assert.True( state.Ready, state.Detail );
      Assert.Equal( (long?)COUNT, state.IndexedVectors );
      Assert.Equal( (long?)COUNT, state.TotalVectors );
      Assert.Contains( "DiskANN index built and used", state.Detail );
      Assert.Contains( "Vector Index Seek on index vix_gvb_gvbbench_tgt", state.Detail );
      Assert.Contains( "(IndexKind DiskANN)", state.Detail );
      Assert.Contains( $"graph table rows {COUNT} of {COUNT} table rows", state.Detail );
      Assert.Contains( "The exact mode searches the SAME table", state.Detail );
      Assert.Contains( $"Clustered Index Scan of GvbBenchTgtA", state.Detail );
      Assert.Contains( $"{COUNT} rows read, no vector index operator", state.Detail );
      Assert.Equal( (long)COUNT, (long)r.Counted );
      Assert.Equal( 20, (int)r.ExactTop1 );
      Assert.True( (int)r.Overlap >= (int)r.PossibleHits * 9 / 10, $"DiskANN and exact shared only {r.Overlap} of {r.PossibleHits} hits" );
      Assert.Contains( "the exact mode scans the same table", (string)r.IndexDescription );
   }

   /// <summary>
   /// The plain SQL target is ready with its evidence: no vector index in the catalog, and the
   /// actual plan of a real exact search is a clustered index scan that read every row.
   /// </summary>
   [Fact]
   public async Task Sql_IsAScanByDesign()
   {
      dynamic r = await SQL.Value;
      IndexState state = r.PlainState;
      _output.WriteLine( "state: " + state );
      Assert.True( state.Ready, state.Detail );
      Assert.Contains( "no index used, exact scan by design", state.Detail );
      Assert.Contains( "sys.vector_indexes lists no index on the table", state.Detail );
      Assert.Contains( $"{COUNT} rows read, no vector index operator", state.Detail );
   }

   /// <summary>
   /// The factory gives each native comparison target ("sql-native", "qdrant-native": the builder's own
   /// sinks on the native servers) its proof and its durability text from the real settings, labels it
   /// as a comparison target off the container CPU split, and loading the Qdrant one through the
   /// target's own members gives ready states before and after searching. The four default targets now
   /// run in the benchmark's own containers; TargetsPinnedLiveTests checks them end to end.
   /// </summary>
   [Fact]
   public async Task Wiring_EachTargetCarriesItsProofAndDurability()
   {
      string[] lines = await TargetsHarness.CallAsync( "WiringAsync", LIMIT, TargetsHarness.RepoRoot() );
      foreach( string line in lines )
      {
         _output.WriteLine( line );
      }

      string Line( string prefix ) => lines.Single( l => l.StartsWith( prefix, StringComparison.Ordinal ) );
      Assert.DoesNotContain( lines, l => l.StartsWith( "factory note:", StringComparison.Ordinal ) && !l.Contains( "Container gvb-", StringComparison.Ordinal ) );
      Assert.Contains( "hosting always-on|finisher False|settle False|reader True|durability A commit returns after", Line( "target sql-native|" ) );
      Assert.Contains( "hosting always-on|finisher False|settle True|reader True|durability What was measured (strace -f on the Qdrant server process", Line( "target qdrant-native|" ) );
      Assert.All( lines.Where( l => l.StartsWith( "target ", StringComparison.Ordinal ) ), l => Assert.DoesNotContain( "not stated", l ) );
      Assert.All( lines.Where( l => l.StartsWith( "target ", StringComparison.Ordinal ) ), l => Assert.DoesNotContain( "durability not read", l ) );
      Assert.All( lines.Where( l => l.StartsWith( "target ", StringComparison.Ordinal ) ), l => Assert.Contains( "|pair none|", l ) );
      Assert.All( lines.Where( l => l.StartsWith( "target ", StringComparison.Ordinal ) ), l => Assert.Contains( "(native service on this host, a COMPARISON target: not the container CPU split, reached over loopback)", l ) );
      Assert.Contains( "|engine Microsoft SQL Server 2025 (RTM-CU9) (KB5122048) - 17.0.5005.3 (X64) (native service", Line( "target sql-native|" ) );
      Assert.Contains( "|engine Qdrant 1.17.0 (native service", Line( "target qdrant-native|" ) );
      Assert.Contains( "connection always-on native SQL Server at ", Line( "target sql-native|" ) );
      Assert.Contains( "connection always-on native Qdrant at ", Line( "target qdrant-native|" ) );
      Assert.All( lines.Where( l => l.StartsWith( "target ", StringComparison.Ordinal ) ), l => Assert.Contains( "not a container port published through docker-proxy", l ) );
      Assert.Contains( "ready False", Line( "sql-native target, table absent|" ) );
      Assert.Contains( "ready True|0/524|", Line( "qdrant-native state before searches|" ) );
      Assert.Contains( "ready True|0/524|", Line( "qdrant-native state after searches|" ) );
   }

   /// <summary>
   /// The proof that a compose-hosted engine is measured without the docker-proxy hop, on the real
   /// benchmark container gvbbench-mariadb (which must be running: start it with
   /// deploy/engines/mariadb-bench.compose.yaml, see <see cref="MariaBenchLiveTests"/> for a test that
   /// starts and stops it itself): the target's recorded address is the container's (confirmed by a second,
   /// independent docker read), and while searches run the kernel's socket table (sudo ss -tnp) shows
   /// this process connected to the container's own address and port, with no socket to the published
   /// 127.0.0.1 port and no docker-proxy socket facing one of ours. The control runs the same searches
   /// through the published port and must show the opposite, which proves the check can see a proxy at all.
   /// Prints the sockets and an alternating-rounds timing of both routes as evidence.
   /// </summary>
   [Fact]
   public async Task Direct_MariaDbSearchesNeverTouchDockerProxy()
   {
      string[] lines = await TargetsHarness.CallContainerAsync( "LiveMariaDbAsync", TimeSpan.FromMinutes( 8 ), TargetsHarness.RepoRoot() );
      foreach( string line in lines )
      {
         _output.WriteLine( line );
      }

      string connection = lines.Single( l => l.StartsWith( "connection|", StringComparison.Ordinal ) );
      Match address = Regex.Match( connection, @"^connection\|gvbbench-mariadb \((?<id>[0-9a-f]{12})\) on network (?<net>\S+) at (?<ip>[\d.]+):3306, not through docker-proxy 127\.0\.0\.1:13306$" );
      Assert.True( address.Success, connection );
      string ip = address.Groups["ip"].Value;
      Assert.Equal( ip, lines.Single( l => l.StartsWith( "docker says the address is|", StringComparison.Ordinal ) ).Split( '|' )[1].Trim() );

      string[] direct = lines.Where( l => l.StartsWith( "direct|sample ", StringComparison.Ordinal ) ).ToArray();
      Assert.Equal( 3, direct.Length );
      Assert.All( direct, l => Assert.Matches( $@"\|ours [1-9]\d*\|to {Regex.Escape( ip )}:3306 [1-9]\d*\|to 127\.0\.0\.1:13306 0\|docker-proxy sockets facing ours 0$", l ) );
      Assert.Matches( @"^direct\|searches\|completed [1-9]\d{2,}\|failed 0$", lines.Single( l => l.StartsWith( "direct|searches|", StringComparison.Ordinal ) ) );

      string[] control = lines.Where( l => l.StartsWith( "proxied control|sample ", StringComparison.Ordinal ) ).ToArray();
      Assert.Equal( 3, control.Length );
      Assert.All( control, l => Assert.Matches( @"\|to 127\.0\.0\.1:13306 [1-9]\d*\|docker-proxy sockets facing ours [1-9]\d*$", l ) );
      Assert.Matches( @"^proxied control\|searches\|completed [1-9]\d{2,}\|failed 0$", lines.Single( l => l.StartsWith( "proxied control|searches|", StringComparison.Ordinal ) ) );
      Assert.Contains( lines, l => l.StartsWith( "cleanup|dropped gvb.gvb_gvbbench_direct", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// Real "docker inspect" output of every running engine container (one or two published ports each, on
   /// different networks) is read into a recorded address that matches a second docker read and accepts a
   /// real TCP connection from this process. The daily gvb-mariadb is never among them (no target uses it);
   /// the benchmark's gvbbench-mariadb is bound when it is running and skipped when it is not, like the
   /// engines that other work has stopped.
   /// </summary>
   [Fact]
   public async Task Bind_EveryRunningContainerResolvesAndAcceptsConnections()
   {
      string[] lines = await TargetsHarness.CallContainerAsync( "LiveBindRunningAsync", TimeSpan.FromMinutes( 4 ), TargetsHarness.RepoRoot() );
      foreach( string line in lines )
      {
         _output.WriteLine( line );
      }

      string[] bound = lines.Where( l => l.Contains( "|bound|", StringComparison.Ordinal ) ).ToArray();
      Assert.DoesNotContain( bound, l => l.Contains( "|gvb-mariadb|", StringComparison.Ordinal ) );
      Assert.All( lines.Where( l => l.StartsWith( "mariadb|", StringComparison.Ordinal ) ), l => Assert.True( l.StartsWith( "mariadb|bound|", StringComparison.Ordinal ) ? l.Contains( "|gvbbench-mariadb|", StringComparison.Ordinal ) : l.Contains( "gvbbench-mariadb", StringComparison.Ordinal ), l ) );
      foreach( string[] fields in bound.Select( l => l.Split( '|' ) ) )
      {
         string ip = fields[2].Split( ':' )[0];
         Assert.True( fields[5] == "reachable True", string.Join( " | ", fields ) );
         Assert.Contains( ip, fields[6] );
         Assert.StartsWith( "proxied 127.0.0.1:", fields[4] );
      }
   }

   /// <summary>
   /// Every way a target can fail to get an index is loud and plain: a missing collection, a
   /// collection that is idle with no graph, a time limit, a cancelled wait, an empty table, and
   /// a missing table or database. None waits for long and none returns a ready state.
   /// </summary>
   [Fact]
   public async Task Failures_AreLoudAndPlain()
   {
      string[] lines = await TargetsHarness.CallAsync( "FailuresAsync", LIMIT );
      foreach( string line in lines )
      {
         _output.WriteLine( line );
      }

      string Line( string prefix ) => lines.Single( l => l.StartsWith( prefix, StringComparison.Ordinal ) );
      Assert.Contains( "InvalidOperationException: Qdrant has no collection gvb_gvbbench_tgt", Line( "missing collection, finish:" ) );
      Assert.Contains( "ready False, Qdrant has no collection gvb_gvbbench_tgt", Line( "missing collection, state:" ) );
      Assert.Contains( "InvalidOperationException: Qdrant collection gvb_gvbbench_tgt", Line( "no graph ever, finish:" ) );
      Assert.Contains( "has been idle (green) for 2 s with only 0 of 50 vectors in HNSW segments, so nothing will index the rest", Line( "no graph ever, finish:" ) );
      Assert.Contains( "TimeoutException: Qdrant collection gvb_gvbbench_tgt", Line( "time limit passed, finish:" ) );
      Assert.Contains( "was not ready 0 s after the load", Line( "time limit passed, finish:" ) );
      Assert.StartsWith( "cancelled, finish: OperationCanceledException: The call to Qdrant was cancelled.", Line( "cancelled, finish:" ) );
      Assert.Contains( "ready False, table dbo.gvb_gvbbench_tgt", Line( "diskann before any index, state:" ) );
      Assert.Contains( "so no DiskANN index was built", Line( "diskann before any index, state:" ) );
      Assert.DoesNotContain( "returned:", Line( "diskann empty table, finish:" ) );
      Assert.Contains( "ready False, could not read the DiskANN index of GvbBenchTgtF", Line( "diskann database missing, state:" ) );
   }

   /// <summary>
   /// The four targets timed on the same vectors with the new method: seeded random pass order,
   /// a warm-up before every pass, the index finished first, the state proven before and after the
   /// searches. Prints the timings as evidence; asserts only that no search failed and every
   /// state stayed ready.
   /// </summary>
   [Fact]
   public async Task Timings_FourTargetsOnTheSameVectors()
   {
      string[] lines = await TargetsHarness.CallAsync( "TimingsAsync", LIMIT, COUNT, DIMENSION, 200, 20261004 );
      foreach( string line in lines )
      {
         _output.WriteLine( line );
      }

      Assert.All( lines.Where( l => l.StartsWith( "state ", StringComparison.Ordinal ) ), l => Assert.Contains( "ready True", l ) );
      Assert.All( lines.Where( l => l.StartsWith( "pass ", StringComparison.Ordinal ) && !l.StartsWith( "pass order", StringComparison.Ordinal ) ), l => Assert.EndsWith( "warm-up errors 0, timed errors 0", l ) );
      Assert.Equal( 6, lines.Count( l => l.StartsWith( "pass ", StringComparison.Ordinal ) && !l.StartsWith( "pass order", StringComparison.Ordinal ) ) );
   }

   #endregion Public Methods
}
