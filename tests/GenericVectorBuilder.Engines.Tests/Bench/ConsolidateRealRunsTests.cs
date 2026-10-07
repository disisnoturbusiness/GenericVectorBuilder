using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The consolidation on the real runs, read in place from bench-results and never written: the three
/// v7 runs in one-session mode with v5 and v6 as the basis, as the P3 brief's trial; and the
/// two-session path rehearsed with a pseudo-v8 session (gap G31), copies of the v7 runs in a scratch
/// folder with every v8 contract field added and every timed figure moved by a fixed few percent.
/// The facts and exclusions are the repository's own files, so the fact sheet is proven against the
/// real runs here too.
/// </summary>
public sealed class ConsolidateRealRunsTests : IDisposable
{
   #region Data Members

   /// <summary>The v7 runs.</summary>
   public static readonly string[] V7 = { "20261006-130619-eshoponweb", "20261006-142724-eshoponweb", "20261006-154837-eshoponweb" };

   /// <summary>The v5 runs.</summary>
   public static readonly string[] V5 = { "20261005-023459-eshoponweb", "20261005-031556-eshoponweb", "20261005-035711-eshoponweb" };

   /// <summary>The v6 runs.</summary>
   public static readonly string[] V6 = { "20261005-073329-eshoponweb", "20261005-085448-eshoponweb", "20261005-101815-eshoponweb" };

   /// <summary>The pseudo-v8 folder names and start times.</summary>
   private static readonly (string Folder, string Started)[] PSEUDO =
   {
      ( "20261008-090000-eshoponweb", "2026-10-08T09:00:00Z" ), ( "20261008-103000-eshoponweb", "2026-10-08T10:30:00Z" ), ( "20261008-120000-eshoponweb", "2026-10-08T12:00:00Z" ),
   };

   /// <summary>Words no generated sentence may hold besides P5's banned list: provenance, purpose, default and "only" claims.</summary>
   private static readonly Regex FORBIDDEN = new( @"(?<![A-Za-z0-9_@])(only|default|defaults|purpose|in order to|so that|intended|designed to)(?![A-Za-z0-9_@])", RegexOptions.IgnoreCase | RegexOptions.Compiled );

   private readonly string _scratch;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the scratch folder under the test binaries.
   /// </summary>
   public ConsolidateRealRunsTests()
   {
      _scratch = Path.Combine( AppContext.BaseDirectory, "consolidate-tests", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( _scratch );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The three real v7 runs, one session, v5 and v6 as basis: the v7-alone orders 141/171, 143/171, 139/171 and
   /// 73/91; the threshold 3500 bp from 2908 bp; the three mean-based clock warnings dropped at 3492 MHz;
   /// Elasticsearch's search fact exact from its measured state (hole H1); the retired client-CPU line found on
   /// line 13 of each run page (hole H4); every sentence audited, none with a cause word, a provenance or "only" word, a dash or too many words.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task RealV7_OneSession_WithTheV5AndV6Basis()
   {
      await ConsolidateHarness.Timed( () =>
      {
         ( int exit, string log, JsonElement root, string md ) = Run( ConsolidateHarness.BenchResults(), "--session", "v7=" + string.Join( ",", V7 ) );
         Assert.True( exit == 0, log );
         Assert.Equal( "one-session", root.GetProperty( "mode" ).GetString() );
         int[] ordered = root.GetProperty( "metrics" ).EnumerateArray().Select( m => m.GetProperty( "orderedPairs" ).GetInt32() ).ToArray();
         Assert.Equal( new[] { 141, 143, 139, 73 }, ordered );
         Assert.Equal( 3500, root.GetProperty( "threshold" ).GetProperty( "tBp" ).GetInt32() );
         Assert.Equal( 2908, root.GetProperty( "basis" ).GetProperty( "maxBp" ).GetInt32() );
         Assert.Equal( 6, root.GetProperty( "basis" ).GetProperty( "leftOut" ).EnumerateArray().Count( l => l.GetProperty( "reason" ).GetString() == "machineControl not recorded" ) );
         JsonElement dropped = root.GetProperty( "clock" ).GetProperty( "droppedWarnings" );
         Assert.Equal( 3, dropped.GetArrayLength() );
         Assert.All( dropped.EnumerateArray(), d => Assert.Equal( ( "oracle", "default@8", 3492, 3492 ), ( d.GetProperty( "target" ).GetString(), d.GetProperty( "pass" ).GetString(), d.GetProperty( "engineMedianMhz" ).GetInt32(), d.GetProperty( "clientMedianMhz" ).GetInt32() ) ) );
         JsonElement v7 = root.GetProperty( "clock" ).GetProperty( "perSession" ).GetProperty( "v7" );
         Assert.Equal( ( 156, 0, 0, 3500, 3600 ), ( v7.GetProperty( "passesEvaluated" ).GetInt32(), v7.GetProperty( "clockOffPasses" ).GetInt32(), v7.GetProperty( "clockUnreadPasses" ).GetInt32(), v7.GetProperty( "pinnedMhz" ).GetInt32(), v7.GetProperty( "ceilingBeforeMhz" ).GetInt32() ) );
         JsonElement es = root.GetProperty( "why" ).EnumerateArray().First( w => w.GetProperty( "target" ).GetString() == "elasticsearch" );
         Assert.Equal( "exact", es.GetProperty( "facts" ).EnumerateArray().First( f => f.GetProperty( "kind" ).GetString() == "search" ).GetProperty( "mode" ).GetString() );
         Assert.All( root.GetProperty( "sessions" )[0].GetProperty( "runs" ).EnumerateArray(), r => Assert.Equal( 13, r.GetProperty( "staleLines" )[0].GetProperty( "line" ).GetInt32() ) );
         SentencesHold( root, md );
      } );
   }

   /// <summary>
   /// The two-session path on real-shaped data (gap G31): real v7 and a pseudo-v8 session. The headline names
   /// Redis with its in-memory clause, the boot, image, observer and dockerd disclosures are written, drift is
   /// present, no guard fires, and every sentence holds.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task PseudoV8_TwoSessions_Rehearsal()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string results = Path.Combine( _scratch, "results" );
         string summary = Pseudo( results );
         ( int exit, string log, JsonElement root, string md ) = Run( results, "--session", "v7=" + string.Join( ",", V7 ), "--session", "v8=" + string.Join( ",", PSEUDO.Select( p => p.Folder ) ), "--observer", summary );
         Assert.True( exit == 0, log );
         Assert.Equal( "two-session", root.GetProperty( "mode" ).GetString() );
         Assert.Equal( "complete", root.GetProperty( "status" ).GetString() );
         Assert.Empty( root.GetProperty( "guards" ).GetProperty( "g3" ).GetProperty( "targets" ).EnumerateArray() );
         string headline = Slot( root, "headline" );
         Assert.StartsWith( "redis had the lowest p50 latency", headline );
         Assert.EndsWith( "in each session; redis holds its data in memory.", headline );
         Assert.Contains( "so v7 ran on that boot too", Slot( root, "disclosure.boot" ) );
         Assert.StartsWith( "Each of the 17 container targets ran one image id", Slot( root, "disclosure.images" ) );
         Assert.Contains( "dockerd", Slot( root, "disclosure.dockerd.v8" ) );
         Assert.Contains( "observer/summary.json", Slot( root, "disclosure.observer" ) );
         Assert.True( root.GetProperty( "drift" ).GetProperty( "perTarget" ).GetArrayLength() > 0 );
         Assert.Equal( "The runs of v8 read a question file with the same SHA256 hash as design/bench-inputs/questions_golden.json.", Slot( root, "subtitle.questions" ) );
         SentencesHold( root, md );
      } );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Runs the command against a results folder with the repository's facts and exclusions and v5 and v6 as basis.
   /// </summary>
   /// <param name="results">Results folder.</param>
   /// <param name="args">The session arguments.</param>
   /// <returns>Exit code, log, consolidated.json and consolidated.md.</returns>
   private (int Exit, string Log, JsonElement Root, string Md) Run( string results, params string[] args )
   {
      string repo = ConsolidateHarness.RepoRoot();
      string output = Path.Combine( _scratch, "out-" + Guid.NewGuid().ToString( "N" ) );
      string[] full = args.Concat( new[]
      {
         "--basis-session", "v5=" + string.Join( ",", V5 ), "--basis-session", "v6=" + string.Join( ",", V6 ), "--results-dir", results, "--repo", repo,
         "--facts", Path.Combine( repo, "src", "GenericVectorBuilder.Bench", "Report", "engine-facts.json" ), "--exclusions", Path.Combine( repo, "deploy", "bench", "basis-exclusions.json" ),
         "--out", output,
      } ).ToArray();
      var result = (string[])ConsolidateHarness.Call( "Consolidate", (object)full )!;
      string log = string.Join( Environment.NewLine, result.Skip( 1 ) );
      string json = Path.Combine( output, "consolidated.json" );
      Assert.True( File.Exists( json ), log );
      return ( int.Parse( result[0] ), log, JsonDocument.Parse( File.ReadAllText( json ) ).RootElement.Clone(), File.ReadAllText( Path.Combine( output, "consolidated.md" ) ) );
   }

   /// <summary>
   /// Every sentence holds the rules: audited, no banned word (P5's list), no provenance, purpose, default or "only" word, no dash, within its word limit,
   /// none with the retired latency-split words; quotes are exempt from the word rules; the markdown prints every sentence.
   /// </summary>
   /// <param name="root">consolidated.json.</param>
   /// <param name="md">consolidated.md.</param>
   private static void SentencesHold( JsonElement root, string md )
   {
      JsonElement sentences = root.GetProperty( "sentences" );
      JsonElement audit = root.GetProperty( "audit" );
      List<JsonElement> rows = root.GetProperty( "metrics" ).EnumerateArray().SelectMany( m => m.GetProperty( "rows" ).EnumerateArray() ).ToList();
      int onRows = rows.Sum( r => r.GetProperty( "flags" ).GetArrayLength() + ( r.TryGetProperty( "note", out JsonElement n ) && n.ValueKind == JsonValueKind.String ? 1 : 0 ) );
      Assert.Equal( onRows, audit.GetProperty( "rowSentencesChecked" ).GetInt32() );
      Assert.Equal( sentences.GetArrayLength() + onRows, audit.GetProperty( "sentencesChecked" ).GetInt32() );
      Assert.All( rows.SelectMany( r => r.GetProperty( "flags" ).EnumerateArray() ), f => Assert.True( f.GetProperty( "sources" ).GetArrayLength() > 0 ) );
      Assert.All( rows.Where( r => r.GetProperty( "note" ).ValueKind == JsonValueKind.String ), r => Assert.True( r.GetProperty( "noteSources" ).GetArrayLength() > 0 ) );
      Assert.DoesNotContain( sentences.EnumerateArray(), s => s.GetProperty( "slot" ).GetString()!.StartsWith( "flag.", StringComparison.Ordinal ) );
      Assert.DoesNotContain( '\u2014', md );
      Assert.DoesNotContain( '\u2013', md );
      Assert.DoesNotContain( "close to the latency", md );
      foreach( JsonElement s in sentences.EnumerateArray() )
      {
         string slot = s.GetProperty( "slot" ).GetString()!;
         string text = s.GetProperty( "text" ).GetString()!;
         Assert.Contains( text, md );
         bool quote = s.GetProperty( "sources" ).EnumerateArray().Any( x => x.GetProperty( "kind" ).GetString() == "quote" );
         if( quote )
         {
            continue;
         }

         Assert.Empty( (string[])ConsolidateHarness.Call( "Banned", text )! );
         Assert.False( FORBIDDEN.IsMatch( text ), $"{slot}: {text}" );
         Assert.True( (int)ConsolidateHarness.Call( "Words", text )! <= ( slot == "headline" ? 34 : 35 ), $"{slot}: {text}" );
      }
   }

   /// <summary>
   /// The text of a slot's sentence.
   /// </summary>
   /// <param name="root">consolidated.json.</param>
   /// <param name="slot">Slot.</param>
   /// <returns>The text.</returns>
   private static string Slot( JsonElement root, string slot )
   {
      return root.GetProperty( "sentences" ).EnumerateArray().First( s => s.GetProperty( "slot" ).GetString() == slot ).GetProperty( "text" ).GetString()!;
   }

   /// <summary>
   /// Builds the scratch results folder: copies of the real v5, v6, v7 and earlier runs' results.json (copies, not links, so removing the
   /// scratch folder can never reach bench-results) and three pseudo-v8 runs; writes an observer summary.
   /// </summary>
   /// <param name="results">The scratch results folder.</param>
   /// <returns>The observer summary path.</returns>
   private static string Pseudo( string results )
   {
      Directory.CreateDirectory( results );
      string real = ConsolidateHarness.BenchResults();
      foreach( string folder in V5.Concat( V6 ).Concat( V7 ).Concat( ConsolidateBasisTests.OTHER_RUNS ) )
      {
         Directory.CreateDirectory( Path.Combine( results, folder ) );
         File.Copy( Path.Combine( real, folder, "results.json" ), Path.Combine( results, folder, "results.json" ) );
      }

      JsonObject pins = JsonNode.Parse( File.ReadAllText( Path.Combine( ConsolidateHarness.RepoRoot(), "deploy", "bench", "image-pins.json" ) ) )!.AsObject();
      string sha = Convert.ToHexString( SHA256.HashData( File.ReadAllBytes( Path.Combine( ConsolidateHarness.RepoRoot(), "design", "bench-inputs", "questions_golden.json" ) ) ) ).ToLowerInvariant();
      for( int i = 0; i < 3; i++ )
      {
         JsonObject run = JsonNode.Parse( File.ReadAllText( Path.Combine( real, V7[i], "results.json" ) ) )!.AsObject();
         PseudoRun( run, i, pins, sha );
         Directory.CreateDirectory( Path.Combine( results, PSEUDO[i].Folder ) );
         File.WriteAllText( Path.Combine( results, PSEUDO[i].Folder, "results.json" ), run.ToJsonString() );
      }

      var runs = new JsonArray( PSEUDO.Select( p => (JsonNode)new JsonObject
      {
         ["folder"] = p.Folder,
         ["checks"] = new JsonObject
         {
            ["observerCpu"] = new JsonArray( 0.06, 0.08, 0.11 ), ["aperf"] = new JsonObject { ["worstDeviationBp"] = 42.5 },
            ["msr620"] = new JsonObject { ["valuesSeen"] = new JsonArray( "1e1e" ) }, ["timers"] = new JsonObject { ["covered"] = true, ["passesWithTimerFired"] = new JsonArray() },
         },
      } ).ToArray() );
      string summary = Path.Combine( results, "..", "observer-summary.json" );
      File.WriteAllText( summary, new JsonObject { ["schema"] = "v8-observer-summary-1", ["runs"] = runs }.ToJsonString() );
      return Path.GetFullPath( summary );
   }

   /// <summary>
   /// Turns a copy of a v7 run into a pseudo-v8 run: new start and seed, the v8 fields of P1 and P2, and every timed figure moved by a fixed factor per target.
   /// </summary>
   /// <param name="run">The copy.</param>
   /// <param name="i">Run index.</param>
   /// <param name="pins">image-pins.json.</param>
   /// <param name="sha">SHA-256 of the copied question file.</param>
   private static void PseudoRun( JsonObject run, int i, JsonObject pins, string sha )
   {
      run["startedUtc"] = PSEUDO[i].Started;
      run["runSeed"] = 801 + i;
      run["queriesFileSha256"] = sha;
      JsonObject c = run["conditions"]!.AsObject();
      c["build"] = new JsonObject { ["informationalVersion"] = "1.0.0+" + new string( '0', 40 ), ["commit"] = new string( '0', 40 ) };
      c["boot"] = new JsonObject { ["bootId"] = "4f6c2b8e-0d3a-4a51-9a63-2b1f0c7d5e11", ["bootTimeUtc"] = "2026-10-03T15:53:39Z" };
      c["clock"] = new JsonObject
      {
         ["pinned"] = true, ["pinnedMhz"] = 3500, ["ceilingBeforeMhz"] = 3600, ["toleranceBp"] = 100,
         ["noTurbo"] = new JsonObject { ["before"] = 0, ["during"] = 1 }, ["uncoreMsr620"] = new JsonObject { ["before"] = "0xc1e", ["during"] = "0x1e1e" },
      };
      c["engineCpu"] = new JsonObject { ["rule"] = "dockerd's cgroup is left out of engine CPU per search; it is subtracted from outside load as benchmark work during the turns of compose targets." };
      foreach( JsonNode? p in c["passes"]!.AsArray() )
      {
         p!["clockRead"] = true;
         p["clockOff"] = false;
         p["engineCpuMsPerSearch"] = 1.0;
      }

      foreach( JsonNode? t in run["targets"]!.AsArray() )
      {
         Move( t!.AsObject(), i, pins );
      }
   }

   /// <summary>
   /// Moves one target's timed figures by its fixed factor and adds its v8 fields.
   /// </summary>
   /// <param name="t">The target.</param>
   /// <param name="i">Run index.</param>
   /// <param name="pins">image-pins.json.</param>
   private static void Move( JsonObject t, int i, JsonObject pins )
   {
      string name = (string)t["name"]!;
      double f = 1 + ( ( name.Sum( ch => ch ) % 41 ) - 20 ) / 1000.0 + i * 0.001;
      JsonObject s = t["search"]!.AsObject();
      foreach( string key in new[] { "p50Ms", "exactP50Ms" }.Where( k => s[k] != null ) )
      {
         s[key] = (double)s[key]! * f;
      }

      JsonObject qps = s["qps"]!.AsObject();
      foreach( string level in qps.Select( q => q.Key ).ToList() )
      {
         qps[level] = (double)qps[level]! / f;
      }

      s["recallHits"] = (int)Math.Round( (double)s["recall"]! * 200 );
      t["engineSettings"] = new JsonArray( new JsonObject { ["key"] = "HostConfig.CpusetCpus", ["value"] = "2-3,6-7", ["how"] = "read: docker inspect" } );
      if( pins[name] is JsonObject pin )
      {
         t["image"] = new JsonObject { ["ref"] = (string)pin["ref"]!, ["id"] = (string)pin["id"]!, ["pinnedId"] = (string)pin["id"]!, ["lastTagTimeUtc"] = "2026-10-01T00:00:00Z" };
      }
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Removes the scratch folder (it holds copies only).
   /// </summary>
   public void Dispose()
   {
      Directory.Delete( _scratch, recursive: true );
   }

   #endregion IDisposable
}
