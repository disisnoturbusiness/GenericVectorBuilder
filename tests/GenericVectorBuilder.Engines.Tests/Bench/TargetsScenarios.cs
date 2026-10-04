// Compiled only by TargetsTests and TargetsLiveTests, together with the benchmark's own sources
// (the test project does not reference the benchmark console project). BENCH_UNDER_TEST is
// defined only in that compilation, so the test project itself sees an empty file.
#if BENCH_UNDER_TEST
using System.Diagnostics;
using GenericVectorBuilder.Bench.Targets;
using GenericVectorBuilder.Core.Configuration;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Sinks;
using GenericVectorBuilder.Engines.Common;
using Grpc.Core;
using Microsoft.Data.SqlClient;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Document = GenericVectorBuilder.Core.Contracts.Document;

namespace GenericVectorBuilder.Bench.UnderTest;

/// <summary>
/// Runs the benchmark's real target code (the two Qdrant targets and the two SQL Server
/// targets) and hands back plain results for the tests to check. The pure part feeds the
/// verdict and parsing code with hand-made facts; the live part loads random vectors into the
/// real SQL Server and Qdrant on this box, under names that cannot collide with anything real:
/// Qdrant collections start with gvbbench_ and every SQL database is a throwaway whose name
/// starts with GvbBenchTgt. Nothing outside those names is read or written.
/// Why random unit vectors: they are the hardest case for an approximate index (no clusters to
/// exploit), so what is proven here holds for real embeddings too.
/// </summary>
public static class TargetsScenarios
{
   #region Data Members

   private const int TOP = 10;
   private const int QDRANT_HTTP_PORT = 6333;
   private static readonly TimeSpan CALL_DEADLINE = TimeSpan.FromSeconds( 60 );
   private static readonly string[] NAMES = { "qdrant-hnsw", "qdrant", "sql-diskann", "sql" };

   #endregion Data Members

   #region Public Methods

   /// <summary>Parses "key: value" yaml with <see cref="ConfigFiles.FlattenYaml"/>.</summary>
   /// <param name="text">The text.</param>
   /// <returns>Sorted "key=value" lines and the count of lines not understood.</returns>
   public static ParsedLines ParseYaml( string text )
   {
      return ToLines( ConfigFiles.FlattenYaml( text ) );
   }

   /// <summary>Parses ini text with <see cref="ConfigFiles.ParseIni"/>.</summary>
   /// <param name="text">The text.</param>
   /// <returns>Sorted "key=value" lines and the count of lines not understood.</returns>
   public static ParsedLines ParseIni( string text )
   {
      return ToLines( ConfigFiles.ParseIni( text ) );
   }

   /// <summary>The Qdrant durability sentence for a config text and environment.</summary>
   /// <param name="yaml">config.yaml text, or null when it could not be read.</param>
   /// <param name="environmentNames">QDRANT__ variable names, or null when unreadable.</param>
   /// <param name="version">Server version.</param>
   /// <returns>The sentence.</returns>
   public static string QdrantDurabilityText( string? yaml, string[]? environmentNames, string version )
   {
      return QdrantServer.DescribeDurability( yaml == null ? null : ConfigFiles.FlattenYaml( yaml ), "/x/config.yaml", environmentNames, version );
   }

   /// <summary>The SQL Server durability sentence for database rows, trace flags and mssql.conf text.</summary>
   /// <param name="databases">Rows "name|recovery|delayed|pageverify", model first.</param>
   /// <param name="traceFlags">Global trace flags.</param>
   /// <param name="ini">mssql.conf text, or null when it could not be read.</param>
   /// <returns>The sentence.</returns>
   public static string SqlDurabilityText( string[] databases, int[] traceFlags, string? ini )
   {
      List<DatabaseDurability> rows = databases.Select( d => d.Split( '|' ) ).Select( p => new DatabaseDurability( p[0], p[1], p[2], p[3] ) ).ToList();
      return SqlDurability.Describe( rows, traceFlags, ini == null ? null : ConfigFiles.ParseIni( ini ) );
   }

   /// <summary>Judges a Qdrant collection from hand-made facts.</summary>
   /// <param name="walkGraph">True for the HNSW target's expectation, false for the exact target's.</param>
   /// <param name="status">Server status text.</param>
   /// <param name="optimizerError">Optimiser error, or null.</param>
   /// <param name="points">points_count.</param>
   /// <param name="indexed">indexed_vectors_count.</param>
   /// <param name="segments">segments_count.</param>
   /// <param name="indexingKb">indexing_threshold_kb or null.</param>
   /// <param name="fullScanKb">full_scan_threshold_kb or null.</param>
   /// <param name="problem">Why telemetry is missing.</param>
   /// <param name="segmentSpecs">Segments as "type:vectors:indexed:bytes:fullScanKb:hnsw:plain:exact" (fullScanKb "-" for none), or null for no telemetry.</param>
   /// <returns>The verdict.</returns>
   public static IndexState QdrantVerdict( bool walkGraph, string status, string? optimizerError, long points, long indexed, int segments, long? indexingKb, long? fullScanKb,
      string? problem, string[]? segmentSpecs )
   {
      var info = new InfoFacts( status, optimizerError, points, indexed, segments, indexingKb, fullScanKb );
      CollectionFacts? facts = segmentSpecs == null ? null : new CollectionFacts( segmentSpecs.Select( ToSegment ).ToList() );
      return QdrantServer.Judge( info, facts, problem, walkGraph ? SearchExpectation.WalkGraph : SearchExpectation.ScanEveryVector );
   }

   /// <summary>Reads a collection out of a telemetry body.</summary>
   /// <param name="json">The body.</param>
   /// <param name="collection">Collection name.</param>
   /// <returns>Segments as "type:vectors:indexed:bytes:fullScanKb:hnsw:plain:exact", or null when the collection is not listed.</returns>
   public static string[]? Telemetry( string json, string collection )
   {
      CollectionFacts? facts = QdrantServer.ParseTelemetry( json, collection );
      return facts?.Segments.Select( s => $"{s.IndexType}:{s.Vectors}:{s.IndexedVectors}:{s.VectorBytes}:{( s.FullScanThresholdKb?.ToString() ?? "-" )}:{s.Hnsw}:{s.Plain}:{s.Exact}" ).ToArray();
   }

   /// <summary>Reads a showplan XML text.</summary>
   /// <param name="xml">The plan.</param>
   /// <returns>The operators and the answers the benchmark asks of them.</returns>
   public static PlanSummary Plan( string xml )
   {
      PlanFacts facts = SqlPlan.Parse( xml );
      return new PlanSummary( facts.Operators.Select( o => $"{o.PhysicalOp}|{o.Database}|{o.Table}|{o.Index}|{o.IndexKind}|{o.ActualRows}" ).ToArray(), facts.VectorSeek != null, facts.Scan != null, facts.Describe() );
   }

   /// <summary>Judges the "sql-diskann" target from hand-made facts.</summary>
   /// <param name="rows">Rows in the table.</param>
   /// <param name="graphRows">Rows of the index's graph table, or null.</param>
   /// <param name="disabled">Whether the index is disabled.</param>
   /// <param name="indexType">Index type text.</param>
   /// <param name="indexName">Index name.</param>
   /// <param name="seekOperators">Operators of the VECTOR_SEARCH plan, "op|index|kind|rows" joined by ";".</param>
   /// <param name="seekHits">Hits of that search.</param>
   /// <param name="exactOperators">Operators of the exact search plan.</param>
   /// <param name="exactHits">Hits of that search.</param>
   /// <returns>The verdict.</returns>
   public static IndexState DiskAnnVerdict( long rows, long? graphRows, bool disabled, string indexType, string indexName, string seekOperators, int seekHits, string exactOperators, int exactHits )
   {
      var table = new TableFacts( rows, 64, new[] { "PK CLUSTERED" } );
      var index = new VectorIndexFacts( indexName, indexType, "COSINE", disabled, "{\"L\":\"48\"}", graphRows );
      return SqlProbe.JudgeDiskAnn( "Db", "t_ann", table, index, ToRun( seekOperators, seekHits ), ToRun( exactOperators, exactHits ) );
   }

   /// <summary>Judges the "sql" target from hand-made facts.</summary>
   /// <param name="rows">Rows in the table.</param>
   /// <param name="hasIndex">Whether sys.vector_indexes lists an index on the table.</param>
   /// <param name="operators">Operators of the exact search plan, "op|index|kind|rows" joined by ";".</param>
   /// <param name="hits">Hits of that search.</param>
   /// <returns>The verdict.</returns>
   public static IndexState ExactVerdict( long rows, bool hasIndex, string operators, int hits )
   {
      var table = new TableFacts( rows, 64, new[] { "PK CLUSTERED" } );
      VectorIndexFacts? index = hasIndex ? new VectorIndexFacts( "vix", "DiskANN", "COSINE", false, "{}", rows ) : null;
      return SqlProbe.JudgeExact( "Db", "t", table, index, ToRun( operators, hits ) );
   }

   /// <summary>
   /// Loads random vectors into the HNSW target and proves from the server what its graph is,
   /// beside three control collections that show why the target sets what it sets.
   /// </summary>
   /// <param name="count">Vectors.</param>
   /// <param name="dimension">Dimension.</param>
   /// <returns>Everything measured.</returns>
   public static async Task<QdrantLiveResult> QdrantLiveAsync( int count, int dimension )
   {
      string stem = "gvbbench_tgt" + Guid.NewGuid().ToString( "N" )[..8];
      VectorRecord[] data = Make( count, dimension, 7 );
      var settings = new GvbSettings();
      using var client = new QdrantClient( settings.QdrantHost, settings.QdrantGrpcPort, false, null, CALL_DEADLINE );
      using var server = new QdrantServer( client, settings.QdrantHost, QDRANT_HTTP_PORT, _ => { } );
      var hnsw = new QdrantHnswSink( client, server, null, "test", "durability under test" );
      var core = new QdrantSink( client );
      string zero = $"gvb_{stem}_zero";
      string oneKb = $"gvb_{stem}_onekb";
      try
      {
         await hnsw.EnsureCollectionAsync( stem, dimension, CancellationToken.None );
         await hnsw.UpsertAsync( stem, data, CancellationToken.None );
         var clock = Stopwatch.StartNew();
         string note = await hnsw.FinishLoadAsync( stem, CancellationToken.None );
         double finishSeconds = clock.Elapsed.TotalSeconds;
         IndexState afterLoad = await hnsw.GetIndexStateAsync( stem, CancellationToken.None );
         int top1 = 0;
         for( int i = 0; i < 20; i++ )
         {
            IReadOnlyList<SearchHit> hits = await hnsw.SearchAsync( stem, data[i].Vector, TOP, CancellationToken.None );
            top1 += hits.Count > 0 && hits[0].ChunkId == data[i].Chunk.ChunkId ? 1 : 0;
         }

         for( int i = 0; i < 5; i++ )
         {
            await hnsw.SearchExactAsync( stem, data[i].Vector, TOP, CancellationToken.None );
         }

         IndexState afterSearch = await hnsw.GetIndexStateAsync( stem, CancellationToken.None );

         await core.EnsureCollectionAsync( stem, dimension, CancellationToken.None );
         await core.UpsertAsync( stem, data, CancellationToken.None );
         await server.WaitUntilSettledAsync( "qdrant", $"gvb_{stem}", false, CancellationToken.None );
         for( int i = 0; i < 5; i++ )
         {
            await core.SearchAsync( stem, data[i].Vector, TOP, CancellationToken.None );
         }

         IndexState scan = await server.StateAsync( $"gvb_{stem}", SearchExpectation.ScanEveryVector, CancellationToken.None );
         CollectionInfo defaults = await server.InfoAsync( $"gvb_{stem}", CancellationToken.None );

         long zeroIndexed = await LoadControlAsync( client, server, zero, dimension, data, 0, false );
         await LoadControlAsync( client, server, oneKb, dimension, data, 1, true );
         IndexState oneKbBefore = await server.StateAsync( oneKb, SearchExpectation.WalkGraph, CancellationToken.None );
         for( int i = 0; i < 5; i++ )
         {
            await client.SearchAsync( oneKb, data[i].Vector, searchParams: new SearchParams { Exact = false }, limit: TOP );
         }

         IndexState oneKbAfter = await server.StateAsync( oneKb, SearchExpectation.WalkGraph, CancellationToken.None );
         return new QdrantLiveResult( note, finishSeconds, afterLoad, afterSearch, top1, 20, (long)defaults.IndexedVectorsCount, (long)defaults.PointsCount, scan, zeroIndexed, oneKbBefore, oneKbAfter );
      }
      finally
      {
         await hnsw.DropCollectionAsync( stem, CancellationToken.None );
         await core.DropCollectionAsync( stem, CancellationToken.None );
         await DropIfExistsAsync( client, zero );
         await DropIfExistsAsync( client, oneKb );
      }
   }

   /// <summary>
   /// Loads random vectors into the DiskANN target and the plain SQL target (each in its own
   /// throwaway database) and proves from SQL Server what each searches.
   /// </summary>
   /// <param name="count">Rows.</param>
   /// <param name="dimension">Dimension.</param>
   /// <returns>Everything measured.</returns>
   public static async Task<SqlLiveResult> SqlLiveAsync( int count, int dimension )
   {
      string stem = "gvbbench_tgt" + Guid.NewGuid().ToString( "N" )[..8];
      string connection = new GvbSettings().BuildSqlConnectionString();
      string annDb = "GvbBenchTgtA" + stem[^8..];
      string plainDb = "GvbBenchTgtP" + stem[^8..];
      VectorRecord[] data = Make( count, dimension, 11 );
      var ann = new SqlDiskAnnSink( connection, "SQL Server under test", "durability under test", annDb );
      var plain = new SqlVectorSink( connection, plainDb );
      try
      {
         await ann.EnsureCollectionAsync( stem, dimension, CancellationToken.None );
         await ann.UpsertAsync( stem, data, CancellationToken.None );
         var clock = Stopwatch.StartNew();
         string note = await ann.FinishLoadAsync( stem, CancellationToken.None );
         double finishSeconds = clock.Elapsed.TotalSeconds;
         IndexState annState = await ann.GetIndexStateAsync( stem, CancellationToken.None );
         int overlap = 0;
         int top1 = 0;
         for( int i = 0; i < 20; i++ )
         {
            IReadOnlyList<SearchHit> seek = await ann.SearchAsync( stem, data[i].Vector, TOP, CancellationToken.None );
            IReadOnlyList<SearchHit> exact = await ann.SearchExactAsync( stem, data[i].Vector, TOP, CancellationToken.None );
            overlap += seek.Select( h => h.ChunkId ).Intersect( exact.Select( h => h.ChunkId ) ).Count();
            top1 += exact.Count > 0 && exact[0].ChunkId == data[i].Chunk.ChunkId ? 1 : 0;
         }

         long counted = await ann.CountAsync( stem, CancellationToken.None );
         await plain.EnsureCollectionAsync( stem, dimension, CancellationToken.None );
         await plain.UpsertAsync( stem, data, CancellationToken.None );
         IndexState plainState = await SqlProbe.ExactStateAsync( connection, plainDb, $"gvb_{stem}", CancellationToken.None );
         return new SqlLiveResult( note, finishSeconds, annState, plainState, overlap, top1, 20 * TOP, counted, ann.IndexDescription );
      }
      finally
      {
         await ann.DropCollectionAsync( stem, CancellationToken.None );
         await plain.DropCollectionAsync( stem, CancellationToken.None );
         await DropDatabaseAsync( connection, plainDb );
         await DropDatabaseAsync( connection, annDb );
      }
   }

   /// <summary>
   /// Provokes each way a target can fail to get a usable index and records how it reports it:
   /// a missing collection, a collection that never gets a graph, a time limit that passes, a
   /// cancelled wait, an empty table, a table that does not exist.
   /// </summary>
   /// <returns>One plain line per case: the exception type and message, or the state's detail.</returns>
   public static async Task<string[]> FailuresAsync()
   {
      string stem = "gvbbench_tgt" + Guid.NewGuid().ToString( "N" )[..8];
      string connection = new GvbSettings().BuildSqlConnectionString();
      var settings = new GvbSettings();
      string annDb = "GvbBenchTgtF" + stem[^8..];
      using var client = new QdrantClient( settings.QdrantHost, settings.QdrantGrpcPort, false, null, CALL_DEADLINE );
      using var server = new QdrantServer( client, settings.QdrantHost, QDRANT_HTTP_PORT, _ => { }, null, 2 );
      using var slow = new QdrantServer( client, settings.QdrantHost, QDRANT_HTTP_PORT, _ => { }, TimeSpan.Zero );
      var hnsw = new QdrantHnswSink( client, server, null, "test", "durability under test" );
      var core = new QdrantSink( client );
      var ann = new SqlDiskAnnSink( connection, "SQL Server under test", null, annDb );
      var lines = new List<string>();
      try
      {
         lines.Add( "missing collection, finish: " + await Describe( () => hnsw.FinishLoadAsync( stem, CancellationToken.None ) ) );
         IndexState missing = await hnsw.GetIndexStateAsync( stem, CancellationToken.None );
         lines.Add( $"missing collection, state: ready {missing.Ready}, {missing.Detail}" );

         await core.EnsureCollectionAsync( stem, 64, CancellationToken.None );
         await core.UpsertAsync( stem, Make( 50, 64, 3 ), CancellationToken.None );
         lines.Add( "no graph ever, finish: " + await Describe( () => server.WaitUntilSettledAsync( "qdrant-hnsw", $"gvb_{stem}", true, CancellationToken.None ) ) );
         lines.Add( "time limit passed, finish: " + await Describe( () => slow.WaitUntilSettledAsync( "qdrant-hnsw", $"gvb_{stem}", true, CancellationToken.None ) ) );
         using var cancelled = new CancellationTokenSource();
         cancelled.Cancel();
         lines.Add( "cancelled, finish: " + await Describe( () => server.WaitUntilSettledAsync( "qdrant-hnsw", $"gvb_{stem}", false, cancelled.Token ) ) );

         await ann.EnsureCollectionAsync( stem, 64, CancellationToken.None );
         IndexState noTable = await ann.GetIndexStateAsync( stem, CancellationToken.None );
         lines.Add( $"diskann before any index, state: ready {noTable.Ready}, {noTable.Detail}" );
         lines.Add( "diskann empty table, finish: " + await Describe( () => ann.FinishLoadAsync( stem, CancellationToken.None ) ) );
         IndexState noDatabase = await new SqlDiskAnnSink( connection, "x", null, annDb + "None" ).GetIndexStateAsync( stem, CancellationToken.None );
         lines.Add( $"diskann database missing, state: ready {noDatabase.Ready}, {noDatabase.Detail}" );
      }
      finally
      {
         await core.DropCollectionAsync( stem, CancellationToken.None );
         await ann.DropCollectionAsync( stem, CancellationToken.None );
         await DropDatabaseAsync( connection, annDb );
      }

      return lines.ToArray();
   }

   /// <summary>
   /// Builds the four built-in targets through the factory the benchmark uses and reports what
   /// each one carries, then loads the two Qdrant targets with real vectors and reads their
   /// state through the target's own members, the way the runner does.
   /// </summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <returns>One line per fact.</returns>
   public static async Task<string[]> WiringAsync( string repoRoot )
   {
      string stem = "gvbbench_tgt" + Guid.NewGuid().ToString( "N" )[..8];
      var lines = new List<string>();
      using var factory = new TargetFactory( new GvbSettings(), repoRoot, null, _ => { } );
      await factory.InitializeAsync( CancellationToken.None );
      lines.AddRange( factory.Notes.Select( n => "factory note: " + n ) );
      foreach( string name in new[] { "sql", "sql-diskann", "qdrant", "qdrant-hnsw" } )
      {
         BenchTarget target = factory.Create( name );
         lines.Add( $"target {name}|finisher {target.HasIndexFinisher}|settle {target.Settle != null}|reader {target.IndexStateReader != null}|durability {target.Durability}" );
      }

      BenchTarget missing = factory.Create( "sql" );
      IndexState none = await missing.ReadIndexStateAsync( stem, TimeSpan.FromSeconds( 30 ), CancellationToken.None );
      lines.Add( $"sql target, table absent|ready {none.Ready}|{none.Detail}" );

      VectorRecord[] data = Make( 524, 1024, 5 );
      foreach( string name in new[] { "qdrant", "qdrant-hnsw" } )
      {
         BenchTarget target = factory.Create( name );
         try
         {
            await target.Sink.EnsureCollectionAsync( stem, 1024, CancellationToken.None );
            await target.Sink.UpsertAsync( stem, data, CancellationToken.None );
            string note = target.Sink is IIndexFinisher finisher ? await finisher.FinishLoadAsync( stem, CancellationToken.None ) : await target.Settle!( stem, CancellationToken.None );
            IndexState before = await target.ReadIndexStateAsync( stem, TimeSpan.FromSeconds( 30 ), CancellationToken.None );
            for( int i = 0; i < 10; i++ )
            {
               await target.Sink.SearchAsync( stem, data[i].Vector, TOP, CancellationToken.None );
            }

            IndexState after = await target.ReadIndexStateAsync( stem, TimeSpan.FromSeconds( 30 ), CancellationToken.None );
            lines.Add( $"{name} finish|{note}" );
            lines.Add( $"{name} state before searches|ready {before.Ready}|{before.IndexedVectors}/{before.TotalVectors}|{before.Detail}" );
            lines.Add( $"{name} state after searches|ready {after.Ready}|{after.IndexedVectors}/{after.TotalVectors}|{after.Detail}" );
         }
         finally
         {
            await target.Sink.DropCollectionAsync( stem, CancellationToken.None );
         }
      }

      return lines.ToArray();
   }

   /// <summary>
   /// A small timed comparison of the four targets on the same vectors, run the way the method
   /// now requires: targets and passes in a seeded random order, a warm-up before every pass,
   /// the index finished and proven before any search, and the index state read again afterwards.
   /// Single searcher only; the numbers are evidence that each target works, not a ranking.
   /// </summary>
   /// <param name="count">Vectors.</param>
   /// <param name="dimension">Dimension.</param>
   /// <param name="queries">Timed searches per pass.</param>
   /// <param name="seed">Order seed.</param>
   /// <returns>One line per fact.</returns>
   public static async Task<string[]> TimingsAsync( int count, int dimension, int queries, int seed )
   {
      string stem = "gvbbench_tgt" + Guid.NewGuid().ToString( "N" )[..8];
      string connection = new GvbSettings().BuildSqlConnectionString();
      var settings = new GvbSettings();
      string annDb = "GvbBenchTgtT" + stem[^8..];
      string plainDb = "GvbBenchTgtU" + stem[^8..];
      VectorRecord[] data = Make( count, dimension, 13 );
      using var client = new QdrantClient( settings.QdrantHost, settings.QdrantGrpcPort, false, null, CALL_DEADLINE );
      using var server = new QdrantServer( client, settings.QdrantHost, QDRANT_HTTP_PORT, _ => { } );
      var hnsw = new QdrantHnswSink( client, server, null, "test", "x" );
      var core = new QdrantSink( client );
      var ann = new SqlDiskAnnSink( connection, "x", null, annDb );
      var plain = new SqlVectorSink( connection, plainDb );
      var lines = new List<string>();
      try
      {
         foreach( ISink sink in new ISink[] { hnsw, core, ann, plain } )
         {
            var clock = Stopwatch.StartNew();
            await sink.EnsureCollectionAsync( stem, dimension, CancellationToken.None );
            await sink.UpsertAsync( stem, data, CancellationToken.None );
            double load = clock.Elapsed.TotalSeconds;
            clock.Restart();
            string note = sink is IIndexFinisher finisher ? await finisher.FinishLoadAsync( stem, CancellationToken.None )
               : sink == core ? await server.WaitUntilSettledAsync( "qdrant", $"gvb_{stem}", false, CancellationToken.None ) : "no index step";
            lines.Add( $"load {sink.Name}: write {load:0.00} s, index step {clock.Elapsed.TotalSeconds:0.00} s, {note}" );
         }

         IndexState[] before = await ReadStatesAsync( stem, connection, plainDb, server, hnsw, ann );
         lines.AddRange( before.Select( ( s, i ) => $"state after load {NAMES[i]}: ready {s.Ready}, {s.IndexedVectors}/{s.TotalVectors}" ) );

         var passes = new List<( string Target, string Pass )> { ( "qdrant-hnsw", "default" ), ( "qdrant-hnsw", "exact" ), ( "qdrant", "default" ), ( "sql-diskann", "default" ), ( "sql-diskann", "exact" ), ( "sql", "default" ) };
         var random = new Random( seed );
         ( string Target, string Pass )[] order = passes.OrderBy( _ => random.Next() ).ToArray();
         lines.Add( "pass order: " + string.Join( ", ", order.Select( p => $"{p.Target}/{p.Pass}" ) ) );
         ISink[] sinks = { hnsw, core, ann, plain };
         foreach( ( string target, string pass ) in order )
         {
            ISink sink = sinks.First( s => s.Name == target );
            Func<float[], Task<IReadOnlyList<SearchHit>>> search = pass == "exact" ? v => ( (IExactSearchSink)sink ).SearchExactAsync( stem, v, TOP, CancellationToken.None )
               : v => sink.SearchAsync( stem, v, TOP, CancellationToken.None );
            int errors = 0;
            for( int i = 0; i < 20; i++ )
            {
               errors += await TryAsync( () => search( data[i].Vector ) ) ? 0 : 1;
            }

            var millis = new List<double>();
            int timedErrors = 0;
            for( int i = 0; i < queries; i++ )
            {
               var clock = Stopwatch.StartNew();
               bool ok = await TryAsync( () => search( data[( i * 7 ) % count].Vector ) );
               timedErrors += ok ? 0 : 1;
               millis.Add( clock.Elapsed.TotalMilliseconds );
            }

            millis.Sort();
            lines.Add( $"pass {target}/{pass}: p50 {millis[millis.Count / 2]:0.00} ms, p95 {millis[(int)( millis.Count * 0.95 )]:0.00} ms, warm-up errors {errors}, timed errors {timedErrors}" );
         }

         IndexState[] after = await ReadStatesAsync( stem, connection, plainDb, server, hnsw, ann );
         lines.AddRange( after.Select( ( s, i ) => $"state after searches {NAMES[i]}: ready {s.Ready}, {s.IndexedVectors}/{s.TotalVectors}" ) );
         lines.Add( "hnsw after: " + after[0].Detail );
         lines.Add( "qdrant after: " + after[1].Detail );
         lines.Add( "diskann after: " + after[2].Detail );
         lines.Add( "sql after: " + after[3].Detail );
      }
      finally
      {
         await hnsw.DropCollectionAsync( stem, CancellationToken.None );
         await core.DropCollectionAsync( stem, CancellationToken.None );
         await ann.DropCollectionAsync( stem, CancellationToken.None );
         await plain.DropCollectionAsync( stem, CancellationToken.None );
         await DropDatabaseAsync( connection, plainDb );
         await DropDatabaseAsync( connection, annDb );
      }

      return lines.ToArray();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads the four targets' index states in the order of <see cref="NAMES"/>.
   /// </summary>
   /// <param name="stem">Collection name.</param>
   /// <param name="connection">Server connection string.</param>
   /// <param name="plainDb">Database of the plain SQL target.</param>
   /// <param name="server">Qdrant server helper.</param>
   /// <param name="hnsw">The HNSW sink.</param>
   /// <param name="ann">The DiskANN sink.</param>
   /// <returns>The states.</returns>
   private static async Task<IndexState[]> ReadStatesAsync( string stem, string connection, string plainDb, QdrantServer server, QdrantHnswSink hnsw, SqlDiskAnnSink ann )
   {
      return new[]
      {
         await hnsw.GetIndexStateAsync( stem, CancellationToken.None ),
         await server.StateAsync( $"gvb_{stem}", SearchExpectation.ScanEveryVector, CancellationToken.None ),
         await ann.GetIndexStateAsync( stem, CancellationToken.None ),
         await SqlProbe.ExactStateAsync( connection, plainDb, $"gvb_{stem}", CancellationToken.None ),
      };
   }

   /// <summary>
   /// Creates a control collection with the given optimizer indexing threshold, loads it, and
   /// waits until the server is idle.
   /// </summary>
   /// <param name="client">Qdrant client.</param>
   /// <param name="server">Qdrant server helper.</param>
   /// <param name="name">Qdrant collection name.</param>
   /// <param name="dimension">Dimension.</param>
   /// <param name="data">Vectors.</param>
   /// <param name="indexingKb">indexing_threshold_kb.</param>
   /// <param name="requireGraph">Whether to wait for every vector to sit in a graph.</param>
   /// <returns>indexed_vectors_count once idle.</returns>
   private static async Task<long> LoadControlAsync( QdrantClient client, QdrantServer server, string name, int dimension, VectorRecord[] data, ulong indexingKb, bool requireGraph )
   {
      await client.CreateCollectionAsync( name, new VectorParams { Size = (ulong)dimension, Distance = Distance.Cosine },
         optimizersConfig: new OptimizersConfigDiff { IndexingThreshold = indexingKb } );
      List<PointStruct> points = data.Select( r => new PointStruct { Id = r.Chunk.ChunkId, Vectors = r.Vector } ).ToList();
      await client.UpsertAsync( name, points, wait: true );
      await server.WaitUntilSettledAsync( "control", name, requireGraph, CancellationToken.None );
      return (long)( await client.GetCollectionInfoAsync( name ) ).IndexedVectorsCount;
   }

   /// <summary>
   /// Runs a call and describes how it ended in one plain line.
   /// </summary>
   /// <typeparam name="T">Result type, ignored.</typeparam>
   /// <param name="call">The call.</param>
   /// <returns>The exception type and message, or "returned: " and the result text.</returns>
   private static async Task<string> Describe<T>( Func<Task<T>> call )
   {
      var clock = Stopwatch.StartNew();
      try
      {
         T result = await call();
         return $"returned: {result} ({clock.Elapsed.TotalSeconds:0.0} s)";
      }
      catch( Exception ex )
      {
         return $"{ex.GetType().Name}: {ex.Message} ({clock.Elapsed.TotalSeconds:0.0} s)";
      }
   }

   /// <summary>
   /// Runs one search and says whether it succeeded.
   /// </summary>
   /// <param name="call">The search.</param>
   /// <returns>True when it returned.</returns>
   private static async Task<bool> TryAsync( Func<Task<IReadOnlyList<SearchHit>>> call )
   {
      try
      {
         await call();
         return true;
      }
      catch( Exception ex ) when( ex is SqlException or RpcException or InvalidOperationException )
      {
         return false;
      }
   }

   /// <summary>
   /// Deletes a Qdrant collection when it exists.
   /// </summary>
   /// <param name="client">Qdrant client.</param>
   /// <param name="name">Qdrant collection name.</param>
   private static async Task DropIfExistsAsync( QdrantClient client, string name )
   {
      if( await client.CollectionExistsAsync( name ) )
      {
         await client.DeleteCollectionAsync( name );
      }
   }

   /// <summary>
   /// Drops a throwaway database that a test created, when it exists and holds no table.
   /// </summary>
   /// <param name="connection">Server connection string.</param>
   /// <param name="database">Database name (always starts with GvbBenchTgt).</param>
   private static async Task DropDatabaseAsync( string connection, string database )
   {
      if( !database.StartsWith( "GvbBenchTgt", StringComparison.Ordinal ) )
      {
         throw new InvalidOperationException( $"Refusing to drop {database}: only GvbBenchTgt* databases belong to these tests." );
      }

      SqlConnection.ClearAllPools();
      await using var open = new SqlConnection( connection );
      await open.OpenAsync();
      await using var command = new SqlCommand( $"IF DB_ID( N'{database}' ) IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END", open ) { CommandTimeout = 300 };
      await command.ExecuteNonQueryAsync();
   }

   /// <summary>
   /// Random unit vectors with stable ids, so a stored vector used as a query has itself as its nearest neighbour.
   /// </summary>
   /// <param name="count">Vectors.</param>
   /// <param name="dimension">Dimension.</param>
   /// <param name="seed">Random seed.</param>
   /// <returns>The records.</returns>
   private static VectorRecord[] Make( int count, int dimension, int seed )
   {
      var random = new Random( seed );
      var records = new VectorRecord[count];
      for( int i = 0; i < count; i++ )
      {
         float[] vector = new float[dimension];
         double norm = 0;
         for( int d = 0; d < dimension; d++ )
         {
            vector[d] = (float)( random.NextDouble() * 2 - 1 );
            norm += vector[d] * vector[d];
         }

         for( int d = 0; d < dimension; d++ )
         {
            vector[d] /= (float)Math.Sqrt( norm );
         }

         var document = new Document( $"file{i % 50}.cs", "files", "targets-test", $"text {i}", "h", new Dictionary<string, string>() );
         records[i] = new VectorRecord( document, new Chunk( new Guid( i + 1, 0, 0, new byte[8] ), document.DocKey, i, $"chunk text {i}" ), vector );
      }

      return records;
   }

   /// <summary>
   /// Lists a parsed config as sorted "key=value" lines.
   /// </summary>
   /// <param name="config">The parsed config.</param>
   /// <returns>The lines and the skipped count.</returns>
   private static ParsedLines ToLines( ParsedConfig config )
   {
      return new ParsedLines( config.Values.OrderBy( p => p.Key, StringComparer.Ordinal ).Select( p => $"{p.Key}={p.Value}" ).ToArray(), config.SkippedLines );
   }

   /// <summary>
   /// Reads a segment spec "type:vectors:indexed:bytes:fullScanKb:hnsw:plain:exact".
   /// </summary>
   /// <param name="spec">The spec.</param>
   /// <returns>The segment.</returns>
   private static SegmentFacts ToSegment( string spec )
   {
      string[] p = spec.Split( ':' );
      return new SegmentFacts( p[0], long.Parse( p[1] ), long.Parse( p[2] ), long.Parse( p[3] ), p[4] == "-" ? null : long.Parse( p[4] ), long.Parse( p[5] ), long.Parse( p[6] ), long.Parse( p[7] ) );
   }

   /// <summary>
   /// Builds a plan run from operator specs "PhysicalOp|index|indexKind|rows" joined by ";".
   /// </summary>
   /// <param name="operators">The specs.</param>
   /// <param name="hits">Hits the search returned.</param>
   /// <returns>The run.</returns>
   private static PlanRun ToRun( string operators, int hits )
   {
      List<PlanOperator> list = operators.Split( ';', StringSplitOptions.RemoveEmptyEntries ).Select( o => o.Split( '|' ) )
         .Select( p => new PlanOperator( p[0], "Db", "t", p[1].Length == 0 ? null : p[1], p[2].Length == 0 ? null : p[2], long.Parse( p[3] ) ) ).ToList();
      return new PlanRun( new PlanFacts( list ), hits );
   }

   #endregion Private Methods
}

/// <summary>A parsed config as sorted "key=value" lines.</summary>
/// <param name="Lines">The lines.</param>
/// <param name="Skipped">Lines the reader did not understand.</param>
public sealed record ParsedLines( string[] Lines, int Skipped );

/// <summary>A plan reduced to what the benchmark asks of it.</summary>
/// <param name="Operators">"op|database|table|index|kind|rows" per operator.</param>
/// <param name="VectorSeek">Whether it has a vector index operator.</param>
/// <param name="Scan">Whether it has a table scan operator.</param>
/// <param name="Describe">The plain-English summary.</param>
public sealed record PlanSummary( string[] Operators, bool VectorSeek, bool Scan, string Describe );

/// <summary>What the live Qdrant scenario measured.</summary>
/// <param name="FinishNote">Note FinishLoadAsync returned.</param>
/// <param name="FinishSeconds">Seconds FinishLoadAsync took.</param>
/// <param name="AfterLoad">HNSW target's state after the index step.</param>
/// <param name="AfterSearch">HNSW target's state after searches.</param>
/// <param name="Top1Hits">Default searches whose first hit was the stored query vector itself.</param>
/// <param name="Queries">Default searches run.</param>
/// <param name="DefaultIndexed">indexed_vectors_count of a collection with server defaults after it went idle.</param>
/// <param name="DefaultPoints">points_count of that collection.</param>
/// <param name="ExactState">Exact target's state after its searches.</param>
/// <param name="ZeroIndexed">indexed_vectors_count of a collection created with indexing_threshold_kb 0.</param>
/// <param name="OneKbBefore">State of a collection with only indexing_threshold_kb 1, before any search.</param>
/// <param name="OneKbAfter">The same, after default searches.</param>
public sealed record QdrantLiveResult( string FinishNote, double FinishSeconds, IndexState AfterLoad, IndexState AfterSearch, int Top1Hits, int Queries, long DefaultIndexed, long DefaultPoints,
   IndexState ExactState, long ZeroIndexed, IndexState OneKbBefore, IndexState OneKbAfter );

/// <summary>What the live SQL Server scenario measured.</summary>
/// <param name="FinishNote">Note FinishLoadAsync returned.</param>
/// <param name="FinishSeconds">Seconds FinishLoadAsync took.</param>
/// <param name="AnnState">DiskANN target's state.</param>
/// <param name="PlainState">Plain SQL target's state.</param>
/// <param name="Overlap">Hits the DiskANN search and the exact search on the same table share, over all queries.</param>
/// <param name="ExactTop1">Exact searches whose first hit was the stored query vector.</param>
/// <param name="PossibleHits">Hits asked for over all queries.</param>
/// <param name="Counted">Row count the DiskANN target reports.</param>
/// <param name="IndexDescription">The target's index description after the build.</param>
public sealed record SqlLiveResult( string FinishNote, double FinishSeconds, IndexState AnnState, IndexState PlainState, int Overlap, int ExactTop1, int PossibleHits, long Counted, string IndexDescription );
#endif
