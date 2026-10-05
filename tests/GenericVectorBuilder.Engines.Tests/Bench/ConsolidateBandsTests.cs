using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The third-review rules of the consolidate command: tie bands instead of strict ranks, the
/// refusal to merge runs that are not the same kind of experiment (command, engine hosting,
/// segment layout), the framing of the speed ranking, and the per-engine notes.
/// Each test writes synthetic result folders and reads consolidated.json and consolidated.md.
/// The band cases below are written out by hand; the web page's tests (BenchResultsSummaryTests)
/// use the same numbers and expect the same bands, which is how the two copies of the rule are
/// kept in agreement.
/// </summary>
public sealed partial class ConsolidateTests
{
   #region Data Members

   private const string FRAMING_TYPE = "GenericVectorBuilder.Bench.Stats.ConsolidateFraming";
   private const string LAYOUT_TYPE = "GenericVectorBuilder.Bench.Stats.SegmentLayout";
   private const string BANDS_TYPE = "GenericVectorBuilder.Bench.Stats.ConsolidateBands";
   private const string REFUSAL = "Refusing to merge runs that are not the same kind of experiment.";
   private const string QDRANT_2 = "status Green, optimizer ok, indexed_vectors_count 524 of 524 points, 2 segments, indexing_threshold_kb 1, full_scan_threshold_kb 10; every one of 1 non-empty segments has a complete HNSW graph; 98,871 segment searches walked the graph and none scanned";

   /// <summary>Per run (r1, r2, r3): p50 ms and QPS at 8 of five targets whose ranges overlap in chains: a and b, then d and c, then e alone.</summary>
   private static readonly (string Name, double[] P50, double[] Qps8)[] BAND_CASE =
   {
      ( "a", new[] { 1.0, 1.2, 1.1 }, new[] { 1000.0, 1100, 1050 } ),
      ( "b", new[] { 1.15, 1.3, 1.2 }, new[] { 1090.0, 1200, 1150 } ),
      ( "c", new[] { 2.0, 2.2, 2.1 }, new[] { 800.0, 850, 900 } ),
      ( "d", new[] { 1.9, 2.05, 2.0 }, new[] { 880.0, 950, 910 } ),
      ( "e", new[] { 5.0, 5.2, 5.1 }, new[] { 300.0, 310, 305 } ),
   };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Targets whose min-max ranges overlap share a band; bands are numbered from the fastest;
   /// inside a band the targets are in median order, and that order differs per metric. p50
   /// (lower is faster) and QPS at 8 (higher is faster) are banded separately, each with its
   /// median, min, max and run count.
   /// </summary>
   [Fact]
   public void Bands_GroupOverlappingRanges_AndKeepMedianOrderInside()
   {
      WriteBandCase();

      JsonElement json = Consolidate( "--targets", "a,b,c,d,e", Path.Combine( _root, "r?" ) );

      Assert.Equal( new[] { "p50Ms", "qps@8", "qps@1" }, json.GetProperty( "bands" ).EnumerateArray().Select( b => b.GetProperty( "metric" ).GetString() ).ToArray() );
      Assert.Equal( new[] { "a:1:1", "b:1:2", "d:2:1", "c:2:2", "e:3:1" }, BandList( json, "p50Ms" ) );
      Assert.Equal( new[] { "b:1:1", "a:1:2", "d:2:1", "c:2:2", "e:3:1" }, BandList( json, "qps@8" ) );
      JsonElement qps8 = Metric( json, "qps@8" );
      Assert.True( qps8.GetProperty( "banded" ).GetBoolean() );
      Assert.True( qps8.GetProperty( "higherIsBetter" ).GetBoolean() );
      Assert.False( Metric( json, "p50Ms" ).GetProperty( "higherIsBetter" ).GetBoolean() );
      Assert.Equal( 3, qps8.GetProperty( "runs" ).GetInt32() );
      JsonElement a = qps8.GetProperty( "entries" ).EnumerateArray().Single( e => e.GetProperty( "target" ).GetString() == "a" );
      Assert.Equal( 1050, a.GetProperty( "median" ).GetDouble() );
      Assert.Equal( 1000, a.GetProperty( "min" ).GetDouble() );
      Assert.Equal( 1100, a.GetProperty( "max" ).GetDouble() );
      Assert.Equal( 3, a.GetProperty( "n" ).GetInt32() );
   }

   /// <summary>
   /// Overlap is chained: x overlaps y and y overlaps z, so all three share a band though x and z
   /// do not overlap; ranges that only touch (u ends where v begins) overlap; a range clear of the
   /// others is its own band. Bands numbered from the fastest, so two targets in different bands
   /// never overlap.
   /// </summary>
   [Fact]
   public void Bands_ChainThroughOverlaps_AndTouchingRangesOverlap()
   {
      var cases = new (string Name, double[] Qps8)[]
      {
         ( "x", new[] { 100.0, 200, 150 } ), ( "y", new[] { 190.0, 300, 250 } ), ( "z", new[] { 290.0, 400, 350 } ),
         ( "u", new[] { 10.0, 20, 15 } ), ( "v", new[] { 20.0, 30, 25 } ), ( "w", new[] { 1.0, 2, 1.5 } ),
      };
      for( int run = 0; run < 3; run++ )
      {
         WriteRun( $"r{run + 1}", $"2026-10-05T1{run}:00:00Z", cases.Select( c => Target( c.Name, 2.0, c.Qps8[run], 100 ) ).ToArray() );
      }

      JsonElement json = Consolidate( "--targets", "x,y,z,u,v,w", Path.Combine( _root, "r?" ) );

      Assert.Equal( new[] { "z:1:1", "y:1:2", "x:1:3", "v:2:1", "u:2:2", "w:3:1" }, BandList( json, "qps@8" ) );
   }

   /// <summary>
   /// Neighbours whose medians are less than 3% apart share a band even when their slowest-to-fastest
   /// ranges do not overlap (the v4 review found boundaries resting on gaps of 2.2% or less, inside
   /// the roughly 2% repeatability). Each target here varies by only 0.1% between runs, so no two
   /// ranges touch. The gap is measured the same way for QPS (higher is faster) and p50 (lower is
   /// faster): the faster median is less than 3% faster than the slower.
   /// </summary>
   [Fact]
   public void Bands_JoinNeighboursWithinThreePercent_EvenWhenRangesDoNotOverlap()
   {
      WriteTightRuns( ( "a", 1.00, 1000 ), ( "b", 1.02, 1020 ), ( "c", 1.06, 1100 ), ( "d", 1.09, 1130 ) );

      JsonElement json = Consolidate( "--targets", "a,b,c,d", Path.Combine( _root, "r?" ) );

      Assert.Equal( new[] { "a:1:1", "b:1:2", "c:2:1", "d:2:2" }, BandList( json, "p50Ms" ) );
      Assert.Equal( new[] { "d:1:1", "c:1:2", "b:2:1", "a:2:2" }, BandList( json, "qps@8" ) );
      Assert.Equal( new[] { "d:1:1", "c:1:2", "b:2:1", "a:2:2" }, BandList( json, "qps@1" ) );
      JsonElement[] pair = Metric( json, "qps@8" ).GetProperty( "entries" ).EnumerateArray().Where( e => e.GetProperty( "target" ).GetString() is "a" or "b" ).ToArray();
      Assert.True( pair[0].GetProperty( "min" ).GetDouble() > pair[1].GetProperty( "max" ).GetDouble(), "b and a must not overlap, or the case proves nothing" );
   }

   /// <summary>
   /// The limit is "less than 3%": 2.9% apart share a band, 3.1% apart do not. A chain of
   /// neighbours each under 3% apart is one band however far its ends are apart (2.4% and 2.5%
   /// steps make 5%), and the next engine 3.8% above the chain's top is its own band: only the two
   /// nearest medians at a boundary count.
   /// </summary>
   [Fact]
   public void Bands_ThreePercentLimit_IsStrict_AndChainsThroughCloseNeighbours()
   {
      WriteTightRuns( ( "s", 1.0, 3093 ), ( "r", 1.0, 3000 ), ( "q", 1.0, 2058 ), ( "p", 1.0, 2000 ), ( "w", 1.0, 1090 ), ( "z", 1.0, 1050 ), ( "y", 1.0, 1025 ), ( "x", 1.0, 1000 ) );

      JsonElement json = Consolidate( "--targets", "s,r,q,p,w,z,y,x", Path.Combine( _root, "r?" ) );

      Assert.Equal( new[] { "s:1:1", "r:2:1", "q:3:1", "p:3:2", "w:4:1", "z:5:1", "y:5:2", "x:5:3" }, BandList( json, "qps@8" ) );
      Assert.Equal( 0.03, (double)COMPILED.Value.GetType( BANDS_TYPE )!.GetField( "MIN_MEDIAN_GAP" )!.GetRawConstantValue()!, 12 );
   }

   /// <summary>
   /// A target with no value for a metric (here no QPS with one searcher) has no band and comes
   /// last, while its other metrics are banded.
   /// </summary>
   [Fact]
   public void Bands_PutTargetsWithoutAValueLast()
   {
      JsonObject NoQps1()
      {
         JsonObject none = Target( "none", 2, 900, 100 );
         ( (JsonObject)( (JsonObject)none["search"]! )["qps"]! ).Remove( "1" );
         return none;
      }

      WriteRun( "r1", "2026-10-05T10:00:00Z", Target( "a", 2, 900, 100 ), NoQps1() );
      WriteRun( "r2", "2026-10-05T11:00:00Z", Target( "a", 2, 910, 100 ), NoQps1() );

      JsonElement json = Consolidate( "--targets", "none,a", Path.Combine( _root, "r?" ) );

      Assert.Equal( new[] { "a:1:1", "none:1:2" }, BandList( json, "p50Ms" ) );
      JsonElement[] entries = Metric( json, "qps@1" ).GetProperty( "entries" ).EnumerateArray().ToArray();
      Assert.Equal( new[] { "a", "none" }, entries.Select( e => e.GetProperty( "target" ).GetString() ).ToArray() );
      Assert.Equal( 1, entries[0].GetProperty( "band" ).GetInt32() );
      Assert.Equal( JsonValueKind.Null, entries[1].GetProperty( "band" ).ValueKind );
      Assert.Equal( JsonValueKind.Null, entries[1].GetProperty( "median" ).ValueKind );
   }

   /// <summary>
   /// One run has no spread, so no band can be drawn: the entries give that run's order, the
   /// markdown says so, and the table is headed "order in this run".
   /// </summary>
   [Fact]
   public void Bands_NeedTwoRuns_OneRunShowsItsOwnOrder()
   {
      WriteRun( "r1", "2026-10-05T10:00:00Z", Target( "slow", 5, 100, 50 ), Target( "fast", 1, 900, 400 ) );

      JsonElement json = Consolidate( "--targets", "slow,fast", Path.Combine( _root, "r1" ) );

      JsonElement p50 = Metric( json, "p50Ms" );
      Assert.False( p50.GetProperty( "banded" ).GetBoolean() );
      Assert.Equal( new[] { "fast:null:1", "slow:null:2" }, p50.GetProperty( "entries" ).EnumerateArray().Select( e => $"{e.GetProperty( "target" ).GetString()}:{( e.GetProperty( "band" ).ValueKind == JsonValueKind.Null ? "null" : "band" )}:{e.GetProperty( "orderInBand" ).GetInt32()}" ).ToArray() );
      string md = File.ReadAllText( Path.Combine( _root, "out", "consolidated.md" ) );
      Assert.Contains( "One run: no spread is known, so no band can be drawn. The order shows this run only.", md );
      Assert.Contains( "| order in this run | target | value | runs | engine notes |", md );
      string ranking = md[md.IndexOf( "## Request speed", StringComparison.Ordinal )..md.IndexOf( "## Per-engine notes", StringComparison.Ordinal )];
      Assert.DoesNotContain( "never overlap", ranking );
   }

   /// <summary>
   /// The markdown titles the ranking "Request speed on a small collection (524 vectors)", says in
   /// one line that at this size it measures per-request cost and not index scaling, explains a band
   /// and says the order inside a band is not a ranking, prints one banded table per metric, and
   /// prints no "median rank" anywhere: the per-run rank tables end in the band instead.
   /// </summary>
   [Fact]
   public void Markdown_ShowsBandsUnderTheFraming_AndNoMedianRank()
   {
      WriteBandCase();

      Consolidate( "--targets", "a,b,c,d,e", Path.Combine( _root, "r?" ) );

      string md = File.ReadAllText( Path.Combine( _root, "out", "consolidated.md" ) );
      Assert.Contains( "## Request speed on a small collection (524 vectors)\n\nMeasured end to end through each engine's .NET client; at this size it reflects per-request cost including the client library, not index scaling.\n\n", md.Replace( "\r", string.Empty ) );
      Assert.Contains( "Engines in different bands never overlap: every run of an engine in a faster band beat every run of an engine in a slower band, and the medians on either side of a band boundary are at least 3% apart. Engines in one band are linked by overlapping slowest-to-fastest ranges or by neighboring medians less than 3% apart (an engine varies about 2% from run to run), so these runs do not separate them cleanly. Inside a band they are listed by median, and that order is not a ranking.", md );
      Assert.Contains( "### p50 latency, one search at a time (lower is faster)", md );
      Assert.Contains( "### QPS with 8 searchers at once (higher is faster)", md );
      Assert.Contains( "| band | target | median [min, max] | runs | engine notes |", md );
      Assert.Contains( "| 1 | b | 1150.0 [1090.0, 1200.0] | 3 | - |", md );
      Assert.Contains( "| 1 | a | 1.10 [1.00, 1.20] | 3 | - |", md );
      Assert.DoesNotContain( "median rank", md );
      Assert.Contains( "| target | r1 | r2 | r3 | band |", md );
      Assert.Contains( "| e | 5 | 5 | 5 | 3 |", md );
      Assert.DoesNotContain( "\u2014", md );
   }

   /// <summary>
   /// The framing says "small collection" only for a small one: above the limit it names the size
   /// and does not claim the numbers are per-request cost; when the size was not recorded it says so.
   /// </summary>
   [Fact]
   public void Framing_NamesTheSize_AndClaimsSmallOnlyWhenSmall()
   {
      Type framing = COMPILED.Value.GetType( FRAMING_TYPE )!;
      string Call( string method, int? rows ) => (string)framing.GetMethod( method )!.Invoke( null, new object?[] { rows } )!;

      Assert.Equal( "Request speed on a small collection (524 vectors)", Call( "Title", 524 ) );
      Assert.Equal( "Request speed on a small collection (10,000 vectors)", Call( "Title", 10000 ) );
      Assert.Equal( "Measured end to end through each engine's .NET client; at this size it reflects per-request cost including the client library, not index scaling.", Call( "Line", 524 ) );
      Assert.Equal( "Request speed at 100,000 vectors", Call( "Title", 100000 ) );
      Assert.Equal( "Measured end to end through each engine's .NET client at 100,000 vectors; the order applies to this size only.", Call( "Line", 100000 ) );
      Assert.DoesNotContain( "per-request", Call( "Line", 100000 ) );
      Assert.Equal( "Request speed (collection size not recorded)", Call( "Title", null ) );
      Assert.Equal( "The collection size was not recorded in these results. Measured end to end through each engine's .NET client.", Call( "Line", null ) );
      Assert.Equal( "Request speed (collection size not recorded)", Call( "Title", 0 ) );
   }

   /// <summary>
   /// Runs of different commands (run-all against bench) are refused, not merged: exit 1, nothing
   /// written, and the log names the field, the values and the runs on each side. Runs of one
   /// command, or all without the field (results from before it was written), still merge.
   /// </summary>
   [Fact]
   public void Refuses_ToMergeDifferentCommands()
   {
      WriteRun( "r1", "2026-10-05T10:00:00Z", Target( "sql", 2, 900, 100 ) );
      WriteRun( "r2", "2026-10-05T11:00:00Z", Target( "sql", 2, 900, 100 ) );
      WriteRun( "r3", "2026-10-05T12:00:00Z", run => run["command"] = "bench", Target( "sql", 2, 900, 100 ) );
      string output = Path.Combine( _root, "out" );

      int exit = Run( "--targets", "sql", "--out", output, Path.Combine( _root, "r?" ) );

      Assert.Equal( 1, exit );
      Assert.Contains( _log, l => l.StartsWith( $"Failed: {REFUSAL} command: run-all (r1, r2) vs bench (r3). Consolidate each kind on its own", StringComparison.Ordinal ) );
      Assert.False( Directory.Exists( output ) );

      JsonElement together = Consolidate( "--targets", "sql", "--runs", Path.Combine( _root, "r{1,2}" ) );
      Assert.Equal( "run-all", together.GetProperty( "settings" ).GetProperty( "command" ).GetString() );
      Assert.Contains( "| benchmark command (every run) | run-all |", File.ReadAllText( Path.Combine( _root, "out", "consolidated.md" ) ) );

      string old = Path.Combine( _root, "old" );
      WriteRun( "o1", "2026-10-05T13:00:00Z", run => run.Remove( "command" ), Target( "sql", 2, 900, 100 ) );
      WriteRun( "o2", "2026-10-05T14:00:00Z", run => run.Remove( "command" ), Target( "sql", 2, 900, 100 ) );
      JsonElement legacy = Consolidate( "--targets", "sql", "--out", old, Path.Combine( _root, "o?" ) );
      Assert.Equal( JsonValueKind.Null, legacy.GetProperty( "settings" ).GetProperty( "command" ).ValueKind );
      WriteRun( "o3", "2026-10-05T15:00:00Z", Target( "sql", 2, 900, 100 ) );
      Assert.Equal( 1, Run( "--targets", "sql", "--out", Path.Combine( _root, "out2" ), Path.Combine( _root, "o?" ) ) );
      Assert.Contains( _log, l => l.Contains( "command: not recorded (o1, o2) vs run-all (o3)", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// A container and a native engine are different experiments and are refused; "compose" and
   /// "container", "always-on" and "systemd" are the same hosting and merge.
   /// </summary>
   [Fact]
   public void Refuses_ToMergeContainerAndNativeHosting()
   {
      WriteRun( "r1", "2026-10-05T10:00:00Z", Hosted( "sql", "compose" ) );
      WriteRun( "r2", "2026-10-05T11:00:00Z", Hosted( "sql", "always-on" ) );
      Assert.Equal( 1, Run( "--targets", "sql", "--out", Path.Combine( _root, "out" ), Path.Combine( _root, "r?" ) ) );
      Assert.Contains( _log, l => l.Contains( "hosting of sql: container (r1) vs native (r2)", StringComparison.Ordinal ) );
      Assert.False( Directory.Exists( Path.Combine( _root, "out" ) ) );

      WriteRun( "s1", "2026-10-05T10:00:00Z", Hosted( "sql", "compose" ) );
      WriteRun( "s2", "2026-10-05T11:00:00Z", Hosted( "sql", "container (host network)" ) );
      WriteRun( "n1", "2026-10-05T12:00:00Z", Hosted( "sql", "always-on" ) );
      WriteRun( "n2", "2026-10-05T13:00:00Z", Hosted( "sql", "systemd (native)" ) );
      Assert.Equal( 2, Consolidate( "--targets", "sql", Path.Combine( _root, "s?" ) ).GetProperty( "runs" ).GetArrayLength() );
      Assert.Equal( 2, Consolidate( "--targets", "sql", "--out", Path.Combine( _root, "out3" ), Path.Combine( _root, "n?" ) ).GetProperty( "runs" ).GetArrayLength() );
   }

   /// <summary>
   /// Runs whose engine reported different segment layouts after the load are refused (Qdrant ended
   /// a replicate load with 3 or 4 segments and a run-all load with 2); the same layout written
   /// two ways ("1 segment(s)" and "1 segments") is one layout; a run that reports none does not
   /// merge with runs that do; a count of searches ("98,871 segment searches") is not a layout.
   /// </summary>
   [Fact]
   public void Refuses_ToMergeDifferentSegmentLayouts()
   {
      WriteRun( "r1", "2026-10-05T10:00:00Z", Laid( "qdrant-hnsw", QDRANT_2 ) );
      WriteRun( "r2", "2026-10-05T11:00:00Z", Laid( "qdrant-hnsw", QDRANT_2.Replace( "2 segments", "4 segments" ) ) );
      Assert.Equal( 1, Run( "--targets", "qdrant-hnsw", "--out", Path.Combine( _root, "out" ), Path.Combine( _root, "r?" ) ) );
      Assert.Contains( _log, l => l.Contains( "segment layout of qdrant-hnsw after the load: 2 segments (r1) vs 4 segments (r2)", StringComparison.Ordinal ) );

      WriteRun( "s1", "2026-10-05T10:00:00Z", Laid( "elasticsearch", "NO HNSW GRAPH: 1 segment(s), 524 vectors" ) );
      WriteRun( "s2", "2026-10-05T11:00:00Z", Laid( "elasticsearch", "NO HNSW GRAPH: 1 segments, 524 vectors" ) );
      JsonElement merged = Consolidate( "--targets", "elasticsearch", Path.Combine( _root, "s?" ) );
      Assert.Equal( "1 segment", merged.GetProperty( "targetSummaries" )[0].GetProperty( "perRun" )[0].GetProperty( "segmentLayoutAfterLoad" ).GetString() );

      WriteRun( "t1", "2026-10-05T10:00:00Z", Laid( "qdrant", QDRANT_2 ) );
      WriteRun( "t2", "2026-10-05T11:00:00Z", Laid( "qdrant", "status Green, no segment count here" ) );
      Assert.Equal( 1, Run( "--targets", "qdrant", "--out", Path.Combine( _root, "out4" ), Path.Combine( _root, "t?" ) ) );
      Assert.Contains( _log, l => l.Contains( "segment layout of qdrant after the load: 2 segments (t1) vs not reported (t2)", StringComparison.Ordinal ) );
   }

   /// <summary>
   /// The refusal looks only at the runs that would be merged: a bench run dropped because its
   /// build configuration differs does not make the run-all runs refuse.
   /// </summary>
   [Fact]
   public void Refusal_IgnoresRunsDroppedForOtherReasons()
   {
      WriteRun( "r1", "2026-10-05T10:00:00Z", Target( "sql", 2, 900, 100 ) );
      WriteRun( "r2", "2026-10-05T11:00:00Z", Target( "sql", 2, 900, 100 ) );
      WriteRun( "r3", "2026-10-05T12:00:00Z", run => { run["command"] = "bench"; Machine( run )["buildConfiguration"] = "Debug"; }, Target( "sql", 2, 900, 100 ) );

      JsonElement json = Consolidate( "--targets", "sql", Path.Combine( _root, "r?" ) );

      Assert.Equal( new[] { "r1", "r2" }, Names( json.GetProperty( "runs" ) ) );
      Assert.StartsWith( "settings differ from the runs used: buildConfiguration Debug vs Release", json.GetProperty( "dropped" )[0].GetProperty( "reason" ).GetString() );
   }

   /// <summary>
   /// The layouts are read from the engines' evidence text in every spelling the harness writes,
   /// and from structured fields when a writer gives them; counts of searches and "non-empty
   /// segments" are not layouts.
   /// </summary>
   [Fact]
   public void SegmentLayout_IsReadFromEvidenceText_InEverySpelling()
   {
      MethodInfo fromText = COMPILED.Value.GetType( LAYOUT_TYPE )!.GetMethod( "FromText" )!;
      string? Read( string? text ) => (string?)fromText.Invoke( null, new object?[] { text } );

      Assert.Equal( "2 segments", Read( QDRANT_2 ) );
      Assert.Equal( "1 segment", Read( "NO HNSW GRAPH, searches scan all 524 vectors: 1 segment(s), 524 vectors" ) );
      Assert.Equal( "1 sealed segment covering 524 rows", Read( "query node: 1 sealed segment(s) with the index loaded covering 524 rows, 0 sealed without it" ) );
      Assert.Equal( "2 sealed segments covering 1,048 rows", Read( "query node: 2 sealed segment(s) with the index loaded covering 1048 rows, 0 sealed without it" ) );
      Assert.Null( Read( "98,871 segment searches walked the graph and none scanned" ) );
      Assert.Null( Read( "every one of 1 non-empty segments has a complete HNSW graph" ) );
      Assert.Null( Read( null ) );

      JsonObject state = State( true, 524, 524, "no count in this text" );
      state["segments"] = 3;
      WriteRun( "r1", "2026-10-05T10:00:00Z", WithState( "pg", state ) );
      JsonElement json = Consolidate( "--targets", "pg", Path.Combine( _root, "r1" ) );
      Assert.Equal( "3 segments", json.GetProperty( "targetSummaries" )[0].GetProperty( "perRun" )[0].GetProperty( "segmentLayoutAfterLoad" ).GetString() );
   }

   /// <summary>
   /// A layout that changes while the engine is searched (Milvus compacting two sealed segments
   /// covering 1,048 rows for 524 stored) is flagged with both layouts; an unchanged layout is not.
   /// </summary>
   [Fact]
   public void SegmentLayoutChange_DuringTheRun_IsFlagged()
   {
      JsonObject milvus = Target( "milvus", 2, 900, 100 );
      milvus["indexState"] = new JsonObject
      {
         ["afterLoad"] = State( true, 524, 524, "query node: 1 sealed segment(s) with the index loaded covering 524 rows, 0 sealed without it" ),
         ["afterSearch"] = State( true, 524, 524, "query node: 2 sealed segment(s) with the index loaded covering 1048 rows, 0 sealed without it" ),
      };
      JsonObject steady = Laid( "qdrant", QDRANT_2 );
      WriteRun( "r1", "2026-10-05T10:00:00Z", milvus, steady );

      JsonElement json = Consolidate( "--targets", "milvus,qdrant", Path.Combine( _root, "r1" ) );

      Assert.Equal( "1 sealed segment covering 524 rows after the load, 2 sealed segments covering 1,048 rows after the searches", Flags( json, "milvus" )["segment-layout-changed"] );
      Assert.False( Flags( json, "qdrant" ).ContainsKey( "segment-layout-changed" ) );
   }

   /// <summary>
   /// Each engine's notes are read from the results: an engine that says "exact scan by design"
   /// is exact by design, an engine that built no graph scanned every vector, and Oracle Free's
   /// 2 CPU cap is stated as "known" when the run did not record it and as "results" when it did.
   /// An engine with nothing to note has no note. The ranking table shows the note kinds, and
   /// the markdown lists every note with its source and the engine's flags.
   /// </summary>
   [Fact]
   public void EngineNotes_ExactByDesign_ScansAll_AndOracleCap()
   {
      JsonObject sql = Target( "sql", 4, 800, 200 );
      sql["indexState"] = new JsonObject { ["afterLoad"] = State( true, 0, 524, "no index used, exact scan by design: dbo.t holds 524 rows; sys.vector_indexes lists no index" ), ["afterSearch"] = State( true, 0, 524, "no index used, exact scan by design: dbo.t holds 524 rows" ) };
      JsonObject es = Target( "elasticsearch", 1.3, 700, 700 );
      es["indexState"] = new JsonObject { ["afterLoad"] = State( false, 0, 524, "NO HNSW GRAPH, searches scan all 524 vectors: 1 segment(s), 524 vectors" ), ["afterSearch"] = State( false, 0, 524, "NO HNSW GRAPH, searches scan all 524 vectors: 1 segment(s), 524 vectors" ) };
      JsonObject oracle = Target( "oracle", 0.9, 1000, 1000 );
      oracle["engine"] = "Oracle AI Database Free 23.26 (23ai line, VECTOR FLOAT32)";
      JsonObject plain = Target( "plain", 1, 1000, 1000 );
      WriteRun( "r1", "2026-10-05T10:00:00Z", sql, es, oracle, plain );

      JsonElement json = Consolidate( "--targets", "sql,elasticsearch,oracle,plain", Path.Combine( _root, "r1" ) );

      JsonElement[] t = json.GetProperty( "targetSummaries" ).EnumerateArray().ToArray();
      Assert.Equal( new[] { "exact-by-design:results" }, NoteList( t[0] ) );
      Assert.Equal( new[] { "scans-all-vectors:results" }, NoteList( t[1] ) );
      Assert.Equal( new[] { "cpu-cap:known" }, NoteList( t[2] ) );
      Assert.Empty( NoteList( t[3] ) );
      Assert.Equal( "Exact by design: every search compares the query with every stored vector, so its time grows with the collection and index quality plays no part in its speed. The results say: no index used, exact scan by design.",
         t[0].GetProperty( "engineNotes" )[0].GetProperty( "text" ).GetString() );
      Assert.StartsWith( "Oracle Free caps itself at 2 CPUs (cpu_count 2, set by its license) whatever CPUs it is given", t[2].GetProperty( "engineNotes" )[0].GetProperty( "text" ).GetString() );
      string md = File.ReadAllText( Path.Combine( _root, "out", "consolidated.md" ) );
      Assert.Contains( "## Per-engine notes", md );
      Assert.Contains( "| sql | exact-by-design | Exact by design:", md );
      Assert.Contains( "| results |", md );
      Assert.Contains( "| oracle | cpu-cap | Oracle Free caps itself at 2 CPUs", md );
      Assert.Contains( "| known |", md );
      Assert.Contains( "| elasticsearch | flags | index-not-ready-after-load, index-not-ready-after-search", md );
      Assert.Contains( "| 4 | sql | 4.00 | 1 | exact-by-design |", md );
      Assert.Contains( "| 3 | elasticsearch | 1.30 | 1 | scans-all-vectors |", md );
      Assert.Contains( "| 1 | oracle | 0.90 | 1 | cpu-cap |", md );
      Assert.DoesNotContain( "\u2014", md );
   }

   /// <summary>
   /// A CPU cap a run records (on the target, or on its engine record under conditions) is shown
   /// as read from the results, not as the known limit; an engine with no recorded cap and not
   /// Oracle Free gets no cap note.
   /// </summary>
   [Fact]
   public void EngineNotes_RecordedCpuCap_IsReadFromTheResults()
   {
      JsonObject oracle = Target( "oracle", 0.9, 1000, 1000 );
      oracle["engine"] = "Oracle AI Database Free 23.26";
      oracle["cpuCap"] = "cpu_count 2";
      JsonObject other = Target( "other", 1, 1000, 1000 );
      JsonObject none = Target( "none", 1, 1000, 1000 );
      WriteRun( "r1", "2026-10-05T10:00:00Z", run => Conditions( run )["engines"] = new JsonArray( new JsonObject { ["target"] = "other", ["hosting"] = "compose", ["cpuCap"] = "2 threads" } ), oracle, other, none );

      JsonElement json = Consolidate( "--targets", "oracle,other,none", Path.Combine( _root, "r1" ) );

      JsonElement[] t = json.GetProperty( "targetSummaries" ).EnumerateArray().ToArray();
      Assert.Equal( new[] { "cpu-cap:results" }, NoteList( t[0] ) );
      Assert.Equal( "CPU cap recorded in the results: cpu_count 2. The engine cannot use more CPUs than that, whatever it is pinned to.", t[0].GetProperty( "engineNotes" )[0].GetProperty( "text" ).GetString() );
      Assert.Equal( new[] { "cpu-cap:results" }, NoteList( t[1] ) );
      Assert.Contains( "2 threads", t[1].GetProperty( "engineNotes" )[0].GetProperty( "text" ).GetString() );
      Assert.Empty( NoteList( t[2] ) );
   }

   /// <summary>
   /// An engine whose own index-state text says it caps itself ("Oracle Free caps itself at 2 CPUs
   /// (...)") has the cap read from the results, quoted as written, not stated as the known limit;
   /// a run that records the cap in a field keeps that field's wording; the words "segment searches"
   /// or an unrelated sentence do not make a cap.
   /// </summary>
   [Fact]
   public void EngineNotes_CpuCapSaidInTheIndexState_IsReadFromTheResults()
   {
      string said = "Oracle Free caps itself at 2 CPUs (cpu_count 2 in V$PARAMETER, edition FREE in V$INSTANCE, 8 host CPUs in V$OSSTAT NUM_CPUS; the 2 CPU thread limit is Oracle's documented Free edition limit)";
      JsonObject oracle = Target( "oracle", 0.9, 1000, 1000 );
      oracle["engine"] = "Oracle AI Database Free 23.26";
      oracle["indexState"] = new JsonObject { ["afterLoad"] = State( true, 524, 524, "HNSW in-memory graph ready; " + said ), ["afterSearch"] = State( true, 524, 524, "HNSW in-memory graph ready" ) };
      JsonObject other = Target( "other", 1, 1000, 1000 );
      other["indexState"] = new JsonObject { ["afterLoad"] = State( true, 524, 524, "98,871 segment searches walked the graph; the host caps nothing" ) };
      WriteRun( "r1", "2026-10-05T10:00:00Z", oracle, other );

      JsonElement json = Consolidate( "--targets", "oracle,other", Path.Combine( _root, "r1" ) );

      JsonElement[] t = json.GetProperty( "targetSummaries" ).EnumerateArray().ToArray();
      Assert.Equal( new[] { "cpu-cap:results" }, NoteList( t[0] ) );
      string text = t[0].GetProperty( "engineNotes" )[0].GetProperty( "text" ).GetString()!;
      Assert.Contains( "The results say: " + said + ".", text );
      Assert.DoesNotContain( "set by its license", text );
      Assert.Empty( NoteList( t[1] ) );
   }

   /// <summary>
   /// None of the framing strings contains an em-dash or an en-dash.
   /// </summary>
   [Fact]
   public void FramingText_HasNoEmDashes()
   {
      Type framing = COMPILED.Value.GetType( FRAMING_TYPE )!;
      foreach( FieldInfo field in framing.GetFields( BindingFlags.Public | BindingFlags.Static ).Where( f => f.FieldType == typeof( string ) ) )
      {
         string text = (string)field.GetRawConstantValue()!;
         Assert.DoesNotContain( "\u2014", text );
         Assert.DoesNotContain( "\u2013", text );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Writes three runs of the hand-made band case (r1, r2, r3).
   /// </summary>
   private void WriteBandCase()
   {
      for( int run = 0; run < 3; run++ )
      {
         WriteRun( $"r{run + 1}", $"2026-10-05T1{run}:00:00Z", BAND_CASE.Select( c => Target( c.Name, c.P50[run], c.Qps8[run], c.Qps8[run] / 4 ) ).ToArray() );
      }
   }

   /// <summary>
   /// Writes three runs (r1, r2, r3) of targets that vary by only 0.1% from run to run (factors
   /// 0.999, 1.000, 1.001 on both p50 and QPS), so that ranges which are not within 0.2% of each
   /// other cannot overlap and only the medians decide the bands.
   /// </summary>
   /// <param name="cases">Name, median p50 ms, median QPS at 8 (QPS at 1 is a quarter of it).</param>
   private void WriteTightRuns( params (string Name, double P50, double Qps8)[] cases )
   {
      double[] factors = { 0.999, 1.0, 1.001 };
      for( int run = 0; run < 3; run++ )
      {
         WriteRun( $"r{run + 1}", $"2026-10-05T1{run}:00:00Z", cases.Select( c => Target( c.Name, c.P50 * factors[run], c.Qps8 * factors[run], c.Qps8 * factors[run] / 4 ) ).ToArray() );
      }
   }

   /// <summary>
   /// The bands of one metric as "target:band:orderInBand" text, in table order.
   /// </summary>
   /// <param name="json">consolidated.json root.</param>
   /// <param name="metric">Metric key.</param>
   /// <returns>One text per target.</returns>
   private static string[] BandList( JsonElement json, string metric )
   {
      return Metric( json, metric ).GetProperty( "entries" ).EnumerateArray()
         .Select( e => $"{e.GetProperty( "target" ).GetString()}:{e.GetProperty( "band" ).GetInt32()}:{e.GetProperty( "orderInBand" ).GetInt32()}" ).ToArray();
   }

   /// <summary>
   /// One metric's bands object.
   /// </summary>
   /// <param name="json">consolidated.json root.</param>
   /// <param name="metric">Metric key.</param>
   /// <returns>The object.</returns>
   private static JsonElement Metric( JsonElement json, string metric )
   {
      return json.GetProperty( "bands" ).EnumerateArray().Single( b => b.GetProperty( "metric" ).GetString() == metric );
   }

   /// <summary>
   /// "kind:source" of each engine note of a target summary.
   /// </summary>
   /// <param name="target">A targetSummaries entry.</param>
   /// <returns>The texts.</returns>
   private static string[] NoteList( JsonElement target )
   {
      return target.GetProperty( "engineNotes" ).EnumerateArray().Select( n => $"{n.GetProperty( "kind" ).GetString()}:{n.GetProperty( "source" ).GetString()}" ).ToArray();
   }

   /// <summary>
   /// A synthetic target with the given hosting text.
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <param name="hosting">Hosting text.</param>
   /// <returns>The target object.</returns>
   private static JsonObject Hosted( string name, string hosting )
   {
      JsonObject target = Target( name, 2, 900, 100 );
      target["hosting"] = hosting;
      return target;
   }

   /// <summary>
   /// A synthetic target whose index state (after the load and after the searches) says the given text.
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <param name="detail">Evidence text.</param>
   /// <returns>The target object.</returns>
   private static JsonObject Laid( string name, string detail )
   {
      return WithState( name, State( true, 524, 524, detail ) );
   }

   /// <summary>
   /// A synthetic target with this index state after the load and a copy after the searches.
   /// </summary>
   /// <param name="name">Target name.</param>
   /// <param name="state">The state object.</param>
   /// <returns>The target object.</returns>
   private static JsonObject WithState( string name, JsonObject state )
   {
      JsonObject target = Target( name, 2, 900, 100 );
      target["indexState"] = new JsonObject { ["afterLoad"] = state, ["afterSearch"] = JsonNode.Parse( state.ToJsonString() ) };
      return target;
   }

   #endregion Private Methods
}
