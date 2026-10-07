using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The threshold basis (design section 1), against the v8c prototype basis.py: on the 9 runs of
/// v5 to v7 with machine control on, the 6 earlier eShopOnWeb runs without it and the repository's
/// basis-exclusions.json, the C# port must give every move, its target or pair, its two runs and
/// every recorded difference between them, figure for figure, and the threshold 3500 bp from the
/// largest move 2908 bp. The expected values are basis.json's (2026-10-07), copied here so the
/// test reads nothing outside the repository (gap G34); labels differ only where the port names
/// them better ("build" for "binary", "not recorded" for "None").
/// The runs are read in place from bench-results and never written.
/// </summary>
public sealed class ConsolidateBasisTests
{
   #region Data Members

   /// <summary>The v5 to v7 runs with machine control on.</summary>
   public static readonly string[] BASIS_RUNS =
   {
      "20261005-023459-eshoponweb", "20261005-031556-eshoponweb", "20261005-035711-eshoponweb", "20261005-073329-eshoponweb", "20261005-085448-eshoponweb",
      "20261005-101815-eshoponweb", "20261006-130619-eshoponweb", "20261006-142724-eshoponweb", "20261006-154837-eshoponweb",
   };

   /// <summary>The earlier eShopOnWeb runs, without machine control.</summary>
   public static readonly string[] OTHER_RUNS =
   {
      "20261003-183955-eshoponweb", "20261003-184207-eshoponweb", "20261003-184445-eshoponweb", "20261003-234838-eshoponweb", "20261004-130858-eshoponweb", "20261004-132735-eshoponweb",
   };

   /// <summary>basis.json, move for move.</summary>
   private static readonly ExpectedMove[] EXPECTED =
   {
      new( "p50Ms", "oneEngineAll", 0.1527, "redis", "20261005-035711-eshoponweb", "20261006-154837-eshoponweb", new[]
         {
            "turbo: turbo not pinned (no no_turbo change recorded) vs turbo off (no_turbo 0 -> 1)",
            "uncore: uncore not pinned vs uncore pinned (MSR 0x620 = 0x1e1e)",
            "warmup: 20-search warm-up vs settle check with level test",
            "rehearsal: rehearsal 5 s vs rehearsal 30 s",
            "build: /home/dan/gvb-work/lanes/v5-final vs /home/dan/gvb-work/lanes/v7-final",
            "searchSettings recorded in one run only",
         } ),
      new( "p50Ms", "pairAll", 0.2633, "mongodb/redis", "20261005-035711-eshoponweb", "20261006-154837-eshoponweb", new[]
         {
            "turbo: turbo not pinned (no no_turbo change recorded) vs turbo off (no_turbo 0 -> 1)",
            "uncore: uncore not pinned vs uncore pinned (MSR 0x620 = 0x1e1e)",
            "warmup: 20-search warm-up vs settle check with level test",
            "rehearsal: rehearsal 5 s vs rehearsal 30 s",
            "build: /home/dan/gvb-work/lanes/v5-final vs /home/dan/gvb-work/lanes/v7-final",
            "searchSettings recorded in one run only",
         } ),
      new( "p50Ms", "oneEngineSame", 0.1201, "mongodb", "20261006-130619-eshoponweb", "20261006-154837-eshoponweb", Array.Empty<string>() ),
      new( "p50Ms", "pairSame", 0.1478, "mongodb/sql-diskann", "20261006-130619-eshoponweb", "20261006-154837-eshoponweb", Array.Empty<string>() ),
      new( "p50Ms", "oneEngineDefectsKept", 0.1527, "redis", "20261005-035711-eshoponweb", "20261006-154837-eshoponweb", new[]
         {
            "turbo: turbo not pinned (no no_turbo change recorded) vs turbo off (no_turbo 0 -> 1)",
            "uncore: uncore not pinned vs uncore pinned (MSR 0x620 = 0x1e1e)",
            "warmup: 20-search warm-up vs settle check with level test",
            "rehearsal: rehearsal 5 s vs rehearsal 30 s",
            "build: /home/dan/gvb-work/lanes/v5-final vs /home/dan/gvb-work/lanes/v7-final",
            "searchSettings recorded in one run only",
         } ),
      new( "p50Ms", "pairDefectsKept", 0.2633, "mongodb/redis", "20261005-035711-eshoponweb", "20261006-154837-eshoponweb", new[]
         {
            "turbo: turbo not pinned (no no_turbo change recorded) vs turbo off (no_turbo 0 -> 1)",
            "uncore: uncore not pinned vs uncore pinned (MSR 0x620 = 0x1e1e)",
            "warmup: 20-search warm-up vs settle check with level test",
            "rehearsal: rehearsal 5 s vs rehearsal 30 s",
            "build: /home/dan/gvb-work/lanes/v5-final vs /home/dan/gvb-work/lanes/v7-final",
            "searchSettings recorded in one run only",
         } ),
      new( "p50Ms", "oneEngineClickhouseV5Kept", 0.1561, "clickhouse", "20261005-035711-eshoponweb", "20261005-101815-eshoponweb", new[]
         {
            "warmup: 20-search warm-up vs settle check",
            "rehearsal: rehearsal 5 s vs rehearsal 30 s",
            "build: /home/dan/gvb-work/lanes/v5-final vs /home/dan/gvb-work/lanes/v6-final",
            "clickhouse pass median MHz engine/client: (3592, 3591) vs (3590, 3590)",
            "searchSettings recorded in one run only",
         } ),
      new( "p50Ms", "pairClickhouseV5Kept", 0.2633, "mongodb/redis", "20261005-035711-eshoponweb", "20261006-154837-eshoponweb", new[]
         {
            "turbo: turbo not pinned (no no_turbo change recorded) vs turbo off (no_turbo 0 -> 1)",
            "uncore: uncore not pinned vs uncore pinned (MSR 0x620 = 0x1e1e)",
            "warmup: 20-search warm-up vs settle check with level test",
            "rehearsal: rehearsal 5 s vs rehearsal 30 s",
            "build: /home/dan/gvb-work/lanes/v5-final vs /home/dan/gvb-work/lanes/v7-final",
            "searchSettings recorded in one run only",
         } ),
      new( "p50Ms", "oneEngineNoMachineControl", 1.1282, "typesense", "20261004-130858-eshoponweb", "20261004-132735-eshoponweb", Array.Empty<string>() ),
      new( "p50Ms", "pairNoMachineControl", 1.4881, "mariadb/typesense", "20261004-130858-eshoponweb", "20261004-132735-eshoponweb", Array.Empty<string>() ),
      new( "qps1", "oneEngineAll", 0.1149, "mongodb", "20261006-130619-eshoponweb", "20261006-154837-eshoponweb", Array.Empty<string>() ),
      new( "qps1", "pairAll", 0.1932, "mongodb/pgvector", "20261005-085448-eshoponweb", "20261006-154837-eshoponweb", new[]
         {
            "turbo: turbo not pinned (no no_turbo change recorded) vs turbo off (no_turbo 0 -> 1)",
            "uncore: uncore not pinned vs uncore pinned (MSR 0x620 = 0x1e1e)",
            "warmup: settle check vs settle check with level test",
            "build: /home/dan/gvb-work/lanes/v6-final vs /home/dan/gvb-work/lanes/v7-final",
         } ),
      new( "qps1", "oneEngineSame", 0.1149, "mongodb", "20261006-130619-eshoponweb", "20261006-154837-eshoponweb", Array.Empty<string>() ),
      new( "qps1", "pairSame", 0.1298, "mariadb/mongodb", "20261006-142724-eshoponweb", "20261006-154837-eshoponweb", Array.Empty<string>() ),
      new( "qps1", "oneEngineDefectsKept", 0.1149, "mongodb", "20261006-130619-eshoponweb", "20261006-154837-eshoponweb", Array.Empty<string>() ),
      new( "qps1", "pairDefectsKept", 0.1932, "mongodb/pgvector", "20261005-085448-eshoponweb", "20261006-154837-eshoponweb", new[]
         {
            "turbo: turbo not pinned (no no_turbo change recorded) vs turbo off (no_turbo 0 -> 1)",
            "uncore: uncore not pinned vs uncore pinned (MSR 0x620 = 0x1e1e)",
            "warmup: settle check vs settle check with level test",
            "build: /home/dan/gvb-work/lanes/v6-final vs /home/dan/gvb-work/lanes/v7-final",
         } ),
      new( "qps1", "oneEngineClickhouseV5Kept", 0.2181, "clickhouse", "20261005-035711-eshoponweb", "20261005-073329-eshoponweb", new[]
         {
            "warmup: 20-search warm-up vs settle check",
            "rehearsal: rehearsal 5 s vs rehearsal 30 s",
            "build: /home/dan/gvb-work/lanes/v5-final vs /home/dan/gvb-work/lanes/v6-final",
            "clickhouse pass median MHz engine/client: (3592, 3591) vs (3574, 3585)",
            "searchSettings recorded in one run only",
         } ),
      new( "qps1", "pairClickhouseV5Kept", 0.2814, "clickhouse/redis", "20261005-035711-eshoponweb", "20261005-101815-eshoponweb", new[]
         {
            "warmup: 20-search warm-up vs settle check",
            "rehearsal: rehearsal 5 s vs rehearsal 30 s",
            "build: /home/dan/gvb-work/lanes/v5-final vs /home/dan/gvb-work/lanes/v6-final",
            "clickhouse pass median MHz engine/client: (3592, 3591) vs (3590, 3590)",
            "searchSettings recorded in one run only",
         } ),
      new( "qps1", "oneEngineNoMachineControl", 0.4245, "typesense", "20261003-184445-eshoponweb", "20261004-132735-eshoponweb", Array.Empty<string>() ),
      new( "qps1", "pairNoMachineControl", 0.9455, "mongodb/typesense", "20261003-184445-eshoponweb", "20261004-132735-eshoponweb", Array.Empty<string>() ),
      new( "qps8", "oneEngineAll", 0.1884, "oracle", "20261005-031556-eshoponweb", "20261006-142724-eshoponweb", new[]
         {
            "turbo: turbo not pinned (no no_turbo change recorded) vs turbo off (no_turbo 0 -> 1)",
            "uncore: uncore not pinned vs uncore pinned (MSR 0x620 = 0x1e1e)",
            "warmup: 20-search warm-up vs settle check with level test",
            "rehearsal: rehearsal 5 s vs rehearsal 30 s",
            "build: /home/dan/gvb-work/lanes/v5-final vs /home/dan/gvb-work/lanes/v7-final",
            "searchSettings recorded in one run only",
         } ),
      new( "qps8", "pairAll", 0.2908, "mongodb/oracle", "20261005-035711-eshoponweb", "20261006-154837-eshoponweb", new[]
         {
            "turbo: turbo not pinned (no no_turbo change recorded) vs turbo off (no_turbo 0 -> 1)",
            "uncore: uncore not pinned vs uncore pinned (MSR 0x620 = 0x1e1e)",
            "warmup: 20-search warm-up vs settle check with level test",
            "rehearsal: rehearsal 5 s vs rehearsal 30 s",
            "build: /home/dan/gvb-work/lanes/v5-final vs /home/dan/gvb-work/lanes/v7-final",
            "searchSettings recorded in one run only",
         } ),
      new( "qps8", "oneEngineSame", 0.1432, "clickhouse", "20261006-130619-eshoponweb", "20261006-142724-eshoponweb", Array.Empty<string>() ),
      new( "qps8", "pairSame", 0.2183, "mongodb/sql-diskann", "20261006-130619-eshoponweb", "20261006-154837-eshoponweb", Array.Empty<string>() ),
      new( "qps8", "oneEngineDefectsKept", 0.7441, "vespa", "20261005-031556-eshoponweb", "20261005-101815-eshoponweb", new[]
         {
            "warmup: 20-search warm-up vs settle check",
            "rehearsal: rehearsal 5 s vs rehearsal 30 s",
            "build: /home/dan/gvb-work/lanes/v5-final vs /home/dan/gvb-work/lanes/v6-final",
            "vespa pass median MHz engine/client: (3501, 3497) vs (3492, 3493)",
            "searchSettings recorded in one run only",
         } ),
      new( "qps8", "pairDefectsKept", 0.9564, "oracle/vespa", "20261005-031556-eshoponweb", "20261006-142724-eshoponweb", new[]
         {
            "turbo: turbo not pinned (no no_turbo change recorded) vs turbo off (no_turbo 0 -> 1)",
            "uncore: uncore not pinned vs uncore pinned (MSR 0x620 = 0x1e1e)",
            "warmup: 20-search warm-up vs settle check with level test",
            "rehearsal: rehearsal 5 s vs rehearsal 30 s",
            "build: /home/dan/gvb-work/lanes/v5-final vs /home/dan/gvb-work/lanes/v7-final",
            "searchSettings recorded in one run only",
            "vespa pass median MHz engine/client: (3501, 3497) vs (3492, 3492)",
         } ),
      new( "qps8", "oneEngineClickhouseV5Kept", 0.2001, "clickhouse", "20261005-035711-eshoponweb", "20261005-073329-eshoponweb", new[]
         {
            "warmup: 20-search warm-up vs settle check",
            "rehearsal: rehearsal 5 s vs rehearsal 30 s",
            "build: /home/dan/gvb-work/lanes/v5-final vs /home/dan/gvb-work/lanes/v6-final",
            "clickhouse pass median MHz engine/client: (3554, 3564) vs (3542, 3552)",
            "searchSettings recorded in one run only",
         } ),
      new( "qps8", "pairClickhouseV5Kept", 0.3071, "clickhouse/oracle", "20261005-035711-eshoponweb", "20261005-073329-eshoponweb", new[]
         {
            "warmup: 20-search warm-up vs settle check",
            "rehearsal: rehearsal 5 s vs rehearsal 30 s",
            "build: /home/dan/gvb-work/lanes/v5-final vs /home/dan/gvb-work/lanes/v6-final",
            "clickhouse pass median MHz engine/client: (3554, 3564) vs (3542, 3552)",
            "searchSettings recorded in one run only",
         } ),
      new( "qps8", "oneEngineNoMachineControl", 0.8427, "chroma", "20261003-184445-eshoponweb", "20261004-132735-eshoponweb", Array.Empty<string>() ),
      new( "qps8", "pairNoMachineControl", 1.4788, "chroma/oracle", "20261003-184445-eshoponweb", "20261004-130858-eshoponweb", Array.Empty<string>() ),
      new( "exactP50Ms", "oneEngineAll", 0.2022, "redis", "20261005-035711-eshoponweb", "20261006-142724-eshoponweb", new[]
         {
            "turbo: turbo not pinned (no no_turbo change recorded) vs turbo off (no_turbo 0 -> 1)",
            "uncore: uncore not pinned vs uncore pinned (MSR 0x620 = 0x1e1e)",
            "warmup: 20-search warm-up vs settle check with level test",
            "rehearsal: rehearsal 5 s vs rehearsal 30 s",
            "build: /home/dan/gvb-work/lanes/v5-final vs /home/dan/gvb-work/lanes/v7-final",
            "searchSettings recorded in one run only",
         } ),
      new( "exactP50Ms", "pairAll", 0.1965, "qdrant-hnsw/redis", "20261005-035711-eshoponweb", "20261006-142724-eshoponweb", new[]
         {
            "turbo: turbo not pinned (no no_turbo change recorded) vs turbo off (no_turbo 0 -> 1)",
            "uncore: uncore not pinned vs uncore pinned (MSR 0x620 = 0x1e1e)",
            "warmup: 20-search warm-up vs settle check with level test",
            "rehearsal: rehearsal 5 s vs rehearsal 30 s",
            "build: /home/dan/gvb-work/lanes/v5-final vs /home/dan/gvb-work/lanes/v7-final",
            "searchSettings recorded in one run only",
         } ),
      new( "exactP50Ms", "oneEngineSame", 0.0529, "redis", "20261005-023459-eshoponweb", "20261005-035711-eshoponweb", Array.Empty<string>() ),
      new( "exactP50Ms", "pairSame", 0.0731, "pgvector/redis", "20261005-023459-eshoponweb", "20261005-035711-eshoponweb", Array.Empty<string>() ),
      new( "exactP50Ms", "oneEngineDefectsKept", 0.2022, "redis", "20261005-035711-eshoponweb", "20261006-142724-eshoponweb", new[]
         {
            "turbo: turbo not pinned (no no_turbo change recorded) vs turbo off (no_turbo 0 -> 1)",
            "uncore: uncore not pinned vs uncore pinned (MSR 0x620 = 0x1e1e)",
            "warmup: 20-search warm-up vs settle check with level test",
            "rehearsal: rehearsal 5 s vs rehearsal 30 s",
            "build: /home/dan/gvb-work/lanes/v5-final vs /home/dan/gvb-work/lanes/v7-final",
            "searchSettings recorded in one run only",
         } ),
      new( "exactP50Ms", "pairDefectsKept", 0.1965, "qdrant-hnsw/redis", "20261005-035711-eshoponweb", "20261006-142724-eshoponweb", new[]
         {
            "turbo: turbo not pinned (no no_turbo change recorded) vs turbo off (no_turbo 0 -> 1)",
            "uncore: uncore not pinned vs uncore pinned (MSR 0x620 = 0x1e1e)",
            "warmup: 20-search warm-up vs settle check with level test",
            "rehearsal: rehearsal 5 s vs rehearsal 30 s",
            "build: /home/dan/gvb-work/lanes/v5-final vs /home/dan/gvb-work/lanes/v7-final",
            "searchSettings recorded in one run only",
         } ),
      new( "exactP50Ms", "oneEngineClickhouseV5Kept", 0.2022, "redis", "20261005-035711-eshoponweb", "20261006-142724-eshoponweb", new[]
         {
            "turbo: turbo not pinned (no no_turbo change recorded) vs turbo off (no_turbo 0 -> 1)",
            "uncore: uncore not pinned vs uncore pinned (MSR 0x620 = 0x1e1e)",
            "warmup: 20-search warm-up vs settle check with level test",
            "rehearsal: rehearsal 5 s vs rehearsal 30 s",
            "build: /home/dan/gvb-work/lanes/v5-final vs /home/dan/gvb-work/lanes/v7-final",
            "searchSettings recorded in one run only",
         } ),
      new( "exactP50Ms", "pairClickhouseV5Kept", 0.1965, "qdrant-hnsw/redis", "20261005-035711-eshoponweb", "20261006-142724-eshoponweb", new[]
         {
            "turbo: turbo not pinned (no no_turbo change recorded) vs turbo off (no_turbo 0 -> 1)",
            "uncore: uncore not pinned vs uncore pinned (MSR 0x620 = 0x1e1e)",
            "warmup: 20-search warm-up vs settle check with level test",
            "rehearsal: rehearsal 5 s vs rehearsal 30 s",
            "build: /home/dan/gvb-work/lanes/v5-final vs /home/dan/gvb-work/lanes/v7-final",
            "searchSettings recorded in one run only",
         } ),
      new( "exactP50Ms", "oneEngineNoMachineControl", 0.6968, "typesense", "20261004-130858-eshoponweb", "20261004-132735-eshoponweb", Array.Empty<string>() ),
      new( "exactP50Ms", "pairNoMachineControl", 1.239, "duckdb/typesense", "20261004-130858-eshoponweb", "20261004-132735-eshoponweb", Array.Empty<string>() ),

   };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Every move of basis.json, its target or pair, its runs and its differences, and the threshold.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Basis_ReproducesThePrototype_FigureForFigure()
   {
      await ConsolidateHarness.Timed( () =>
      {
         JsonElement basis = Basis( Path.Combine( ConsolidateHarness.RepoRoot(), "deploy", "bench", "basis-exclusions.json" ) );
         Assert.Equal( 2908, basis.GetProperty( "maxBp" ).GetInt32() );
         Assert.Equal( 3500, basis.GetProperty( "tBp" ).GetInt32() );
         Assert.Equal( "mongodb/oracle", basis.GetProperty( "maxMove" ).GetProperty( "pair" ).GetString() );
         foreach( ExpectedMove e in EXPECTED )
         {
            JsonElement move = Move( basis, e.Metric, e.Key );
            string label = $"{e.Metric} {e.Key}";
            Assert.True( Math.Abs( move.GetProperty( "move" ).GetDouble() - e.Move ) <= 0.00005, $"{label}: move {move.GetProperty( "move" ).GetDouble()} against {e.Move}" );
            Assert.Equal( e.Who, move.TryGetProperty( "target", out JsonElement t ) && t.ValueKind == JsonValueKind.String ? t.GetString() : move.GetProperty( "pair" ).GetString() );
            Assert.Equal( new[] { e.RunA, e.RunB }, move.GetProperty( "runs" ).EnumerateArray().Select( r => r.GetString() ).ToArray() );
            Assert.Equal( e.Differences, move.GetProperty( "differences" ).EnumerateArray().Select( d => d.GetString() ).ToArray() );
         }
      } );
   }

   /// <summary>
   /// TBp is the floor or the first multiple of 500 at least 500 above the largest move, in whole
   /// basis points; a move is rounded up, so a move a hair above a step lifts the threshold.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Threshold_IsTheFloorOrTheFirstStepAtLeastTheMarginAbove()
   {
      await ConsolidateHarness.Timed( () =>
      {
         int[] maxima = { 0, 2908, 3000, 3001, 4500, 4501, 9564 };
         int[] expected = { 3500, 3500, 3500, 4000, 5000, 5500, 10500 };
         Assert.Equal( expected, maxima.Select( m => (int)ConsolidateHarness.Call( "TBpFor", m )! ).ToArray() );
         Assert.Equal( 2908, (int)ConsolidateHarness.Call( "MoveBp", 1.29076, 1.0 )! );
         Assert.Equal( 3001, (int)ConsolidateHarness.Call( "MoveBp", 1.30001, 1.0 )! );
         Assert.Equal( 13500, (int)ConsolidateHarness.Call( "RatioBp", 1.35, 1.0 )! );
         Assert.Equal( 13499, (int)ConsolidateHarness.Call( "RatioBp", 1.3499999, 1.0 )! );
      } );
   }

   /// <summary>
   /// An exclusion row that removes no cell (a mistyped seed) stops the basis: a defect left in the
   /// basis by a typo would never be seen otherwise.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task ExclusionRow_ThatMatchesNoCell_IsRefused()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string folder = Path.Combine( AppContext.BaseDirectory, "consolidate-tests", Guid.NewGuid().ToString( "N" ) );
         Directory.CreateDirectory( folder );
         try
         {
            string path = Path.Combine( folder, "exclusions.json" );
            File.WriteAllText( path, new JsonArray( new JsonObject { ["target"] = "vespa", ["seeds"] = new JsonArray( 999 ), ["metric"] = "qps8", ["kind"] = "method defect", ["why"] = "x", ["source"] = "x" } ).ToJsonString() );
            InvalidDataException refused = Assert.Throws<InvalidDataException>( () => Basis( path ) );
            Assert.Contains( "matches no cell", refused.Message );
         }
         finally
         {
            Directory.Delete( folder, recursive: true );
         }
      } );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Computes the basis over the real runs.
   /// </summary>
   /// <param name="exclusions">Exclusions file.</param>
   /// <returns>The basis as JSON.</returns>
   private static JsonElement Basis( string exclusions )
   {
      string results = ConsolidateHarness.BenchResults();
      string[] basis = BASIS_RUNS.Select( r => Path.Combine( results, r ) ).ToArray();
      string[] other = OTHER_RUNS.Select( r => Path.Combine( results, r ) ).ToArray();
      return JsonDocument.Parse( (string)ConsolidateHarness.Call( "Basis", basis, other, exclusions )! ).RootElement.Clone();
   }

   /// <summary>
   /// One move of the basis by the prototype's key.
   /// </summary>
   /// <param name="basis">The basis.</param>
   /// <param name="metric">Metric id.</param>
   /// <param name="key">oneEngineAll, pairSame, oneEngineDefectsKept, pairClickhouseV5Kept, oneEngineNoMachineControl and the like.</param>
   /// <returns>The move object.</returns>
   private static JsonElement Move( JsonElement basis, string metric, string key )
   {
      JsonElement m = basis.GetProperty( "perMetric" ).GetProperty( metric );
      bool pair = key.StartsWith( "pair", StringComparison.Ordinal );
      string side = pair ? "pair" : "oneEngine";
      return key[( pair ? 4 : 9 )..] switch
      {
         "All" => m.GetProperty( side ),
         "Same" => m.GetProperty( side + "SameSettings" ),
         "DefectsKept" => Kept( m, side, "method defect" ),
         "ClickhouseV5Kept" => Kept( m, side, "unrecorded config change" ),
         "NoMachineControl" => m.GetProperty( side + "NoMachineControl" ),
         _ => throw new ArgumentException( key ),
      };
   }

   /// <summary>
   /// A move with one exclusion kind kept in.
   /// </summary>
   /// <param name="m">The metric's basis.</param>
   /// <param name="side">"oneEngine" or "pair".</param>
   /// <param name="kind">The kind kept in.</param>
   /// <returns>The move.</returns>
   private static JsonElement Kept( JsonElement m, string side, string kind )
   {
      return m.GetProperty( side + "ExclusionsKept" ).EnumerateArray().First( k => k.GetProperty( "kind" ).GetString() == kind ).GetProperty( side );
   }

   #endregion Private Methods
}

/// <summary>One expected move of basis.json.</summary>
/// <param name="Metric">Metric id.</param>
/// <param name="Key">The prototype's key.</param>
/// <param name="Move">The move, four decimals.</param>
/// <param name="Who">Target or pair.</param>
/// <param name="RunA">First run.</param>
/// <param name="RunB">Second run.</param>
/// <param name="Differences">Every recorded difference.</param>
public sealed record ExpectedMove( string Metric, string Key, double Move, string Who, string RunA, string RunB, string[] Differences );
