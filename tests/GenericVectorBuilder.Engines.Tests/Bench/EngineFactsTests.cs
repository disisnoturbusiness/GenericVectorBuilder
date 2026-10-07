using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// Compiles the report's fact and sentence sources once and exposes them through a small facade
/// of strings and string arrays, shared by <see cref="EngineFactsTests"/> and <see cref="SentenceAuditTests"/>.
/// Why Roslyn: the benchmark is a console project this test project does not reference (its
/// project file is not this lane's to change), so the real files under
/// src/GenericVectorBuilder.Bench/Report are compiled and run, not copies. The four files use only
/// the base class library, so nothing else needs compiling with them.
/// Why a facade: the tests then need no reflection over records and lists, only string calls.
/// </summary>
public static class ReportSourcesHarness
{
   #region Data Members

   /// <summary>Longest any one test body may run before it is reported as hung.</summary>
   public static readonly TimeSpan TEST_DEADLINE = TimeSpan.FromSeconds( 120 );

   private static readonly string[] SOURCES = { "Report/EngineFacts.cs", "Report/Sentence.cs", "Report/SentenceAudit.cs", "Report/BannedWords.cs" };
   private static readonly Lazy<Assembly> COMPILED = new( Compile );

   private const string FACADE = @"
global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; global using System.Threading; global using System.Threading.Tasks;
using System.Text.Json;
using GenericVectorBuilder.Bench.Report;

namespace Facade;

public static class Api
{
   private static List<FactRun> Open( string[] runPaths )
   {
      return runPaths.Select( FactRun.Load ).ToList();
   }

   public static string[] Facts( string factsPath, string[] runPaths, string repoRoot )
   {
      List<FactRun> runs = Open( runPaths );
      try
      {
         IReadOnlyList<EngineFactRow> rows = EngineFactSheet.Load( factsPath );
         FactValidation outcome = EngineFactSheet.Validate( rows, runs, repoRoot );
         return new[] { $""rows={rows.Count} resolved={outcome.Resolved.Count}"" }.Concat( outcome.Problems ).ToArray();
      }
      finally
      {
         runs.ForEach( r => r.Dispose() );
      }
   }

   public static string Listing( string factsPath, string[] runPaths, string repoRoot )
   {
      List<FactRun> runs = Open( runPaths );
      try
      {
         return EngineFactSheet.ValidateOrThrow( EngineFactSheet.Load( factsPath ), runs, repoRoot ).Listing();
      }
      finally
      {
         runs.ForEach( r => r.Dispose() );
      }
   }

   public static string Throws( string factsPath, string[] runPaths, string repoRoot )
   {
      List<FactRun> runs = Open( runPaths );
      try
      {
         EngineFactSheet.ValidateOrThrow( EngineFactSheet.Load( factsPath ), runs, repoRoot );
         return string.Empty;
      }
      catch( Exception ex )
      {
         return ex.GetType().Name + "": "" + ex.Message;
      }
      finally
      {
         runs.ForEach( r => r.Dispose() );
      }
   }

   public static string LoadMessage( string path )
   {
      try
      {
         EngineFactSheet.Load( path );
         return string.Empty;
      }
      catch( Exception ex )
      {
         return ex.GetType().Name + "": "" + ex.Message;
      }
   }

   public static string[] Audit( string sentencesJson, string[] runPaths, string repoRoot, string consolidatedPath )
   {
      List<FactRun> runs = Open( runPaths );
      JsonDocument? consolidated = consolidatedPath.Length == 0 ? null : JsonDocument.Parse( File.ReadAllText( consolidatedPath ) );
      try
      {
         var resolver = new SourceResolver( repoRoot, runs, consolidated?.RootElement );
         var sentences = new List<Sentence>();
         using( JsonDocument document = JsonDocument.Parse( sentencesJson ) )
         {
            foreach( JsonElement item in document.RootElement.EnumerateArray() )
            {
               var sources = item.GetProperty( ""sources"" ).EnumerateArray()
                  .Select( s => new SentenceSource( s.GetProperty( ""kind"" ).GetString(), s.GetProperty( ""ref"" ).GetString(), s.GetProperty( ""value"" ).GetString() ) ).ToList();
               sentences.Add( new Sentence( item.GetProperty( ""slot"" ).GetString(), item.GetProperty( ""text"" ).GetString(), sources ) );
            }
         }

         return SentenceAudit.Check( sentences, resolver ).Select( f => f.Slot + ""|"" + f.Rule + ""|"" + f.Message ).ToArray();
      }
      finally
      {
         runs.ForEach( r => r.Dispose() );
         consolidated?.Dispose();
      }
   }

   public static string Resolve( string kind, string reference, string[] runPaths, string repoRoot )
   {
      List<FactRun> runs = Open( runPaths );
      try
      {
         SourceResolution r = new SourceResolver( repoRoot, runs ).Resolve( new SentenceSource( kind, reference, """" ) );
         return ( r.Found ? ""FOUND "" : ""MISSING "" ) + r.Value + "" | "" + r.Detail;
      }
      finally
      {
         runs.ForEach( x => x.Dispose() );
      }
   }

   public static string[] Banned( string text ) { return BannedWords.Find( text ).ToArray(); }

   public static string[] BannedList() { return BannedWords.LIST.ToArray(); }

   public static int Words( string text ) { return SentenceAudit.WordCount( text ); }

   public static bool Agrees( string resolved, string shown ) { return SentenceAudit.Agrees( resolved, shown ); }

   public static string StateSays( string snapshotJson )
   {
      using JsonDocument document = JsonDocument.Parse( snapshotJson );
      return EngineFactSheet.StateSays( document.RootElement );
   }

   public static string Sha256( string path ) { return EngineFactSheet.FileSha256( path ); }
}
";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Calls a static method of the compiled facade.
   /// </summary>
   /// <param name="method">The facade method.</param>
   /// <param name="args">Its arguments.</param>
   /// <returns>Its result.</returns>
   public static object? Call( string method, params object?[] args )
   {
      MethodInfo info = COMPILED.Value.GetType( "Facade.Api" )!.GetMethod( method )!;
      try
      {
         return info.Invoke( null, args );
      }
      catch( TargetInvocationException ex ) when( ex.InnerException is not null )
      {
         System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture( ex.InnerException ).Throw();
         throw;
      }
   }

   /// <summary>
   /// Runs a test body on another thread and fails the test when it does not finish in time.
   /// Why: xunit's own Timeout is honoured for async tests only, and these bodies are synchronous.
   /// </summary>
   /// <param name="body">The test body.</param>
   /// <returns>A task that completes with the body, or throws a timeout.</returns>
   public static async Task Timed( Action body )
   {
      await Task.Run( body ).WaitAsync( TEST_DEADLINE );
   }

   /// <summary>
   /// Walks up from the test binary to the folder holding the solution file.
   /// </summary>
   /// <returns>The repository root.</returns>
   public static string RepoRoot()
   {
      var dir = new DirectoryInfo( AppContext.BaseDirectory );
      while( dir != null && !File.Exists( Path.Combine( dir.FullName, "GenericVectorBuilder.slnx" ) ) )
      {
         dir = dir.Parent;
      }

      return dir?.FullName ?? throw new InvalidOperationException( "Repository root not found." );
   }

   /// <summary>
   /// The three v7 results.json files the fact sheet is proven against, read-only, in the repository's bench-results.
   /// </summary>
   /// <returns>The three paths.</returns>
   public static string[] V7Runs()
   {
      string results = Path.Combine( RepoRoot(), "bench-results" );
      string[] runs = { "20261006-130619-eshoponweb", "20261006-142724-eshoponweb", "20261006-154837-eshoponweb" };
      return runs.Select( r => Path.Combine( results, r, "results.json" ) ).ToArray();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Compiles the four report sources and the facade.
   /// </summary>
   /// <returns>The assembly.</returns>
   private static Assembly Compile()
   {
      string bench = Path.Combine( RepoRoot(), "src", "GenericVectorBuilder.Bench" );
      var trees = SOURCES.Select( s => CSharpSyntaxTree.ParseText( File.ReadAllText( Path.Combine( bench, s ) ), path: s ) ).ToList();
      trees.Add( CSharpSyntaxTree.ParseText( FACADE, path: "Facade.cs" ) );
      IEnumerable<MetadataReference> references = ( (string)AppContext.GetData( "TRUSTED_PLATFORM_ASSEMBLIES" )! )
         .Split( Path.PathSeparator ).Select( p => MetadataReference.CreateFromFile( p ) );
      var options = new CSharpCompilationOptions( OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable );
      CSharpCompilation compilation = CSharpCompilation.Create( "ReportSourcesUnderTest", trees, references, options );
      using var image = new MemoryStream();
      Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit( image );
      Assert.True( result.Success, string.Join( Environment.NewLine, result.Diagnostics.Where( d => d.Severity == DiagnosticSeverity.Error ) ) );
      return Assembly.Load( image.ToArray() );
   }

   #endregion Private Methods
}

/// <summary>
/// The engine fact sheet (engine-facts.json), the basis exclusions (deploy/bench/basis-exclusions.json)
/// and the copied verdicts, golden questions and saved documentation pages, checked against the
/// three real v7 results.json files and the repository tree.
/// Why these checks: the page that says why some engines beat others may state only what can be
/// traced. Each test either proves a real fact row resolves, or plants one thing that is wrong (a
/// false fact, a stale token, an unbound word, a comment in place of code, a wrong compose file) and
/// proves it is refused.
/// </summary>
public sealed class EngineFactsTests : IDisposable
{
   #region Data Members

   private const string FACTS_PATH = "src/GenericVectorBuilder.Bench/Report/engine-facts.json";
   private const string EXCLUSIONS_PATH = "deploy/bench/basis-exclusions.json";
   private const string ES_STATE_FAILED = "{\"ready\":false,\"indexedVectors\":0,\"totalVectors\":524,\"detail\":\"NO HNSW GRAPH, searches scan all 524 vectors\"}";
   private const string GRAPH_STATE = "{\"ready\":true,\"indexedVectors\":524,\"totalVectors\":524,\"detail\":\"HNSW graph returns 524 of 524 vectors\"}";
   private static readonly string FORBIDDEN_NAMES = @"(?i)\b" + "bs" + "&?a\\b|" + "fal" + "con|" + "dwe" + "aver";
   private const string SYNTH = "synth";
   private const string SYNTH_NOTE = "Engine files (SHA-256, first 12 hex digits): [synth.compose.yaml 0123456789ab]. This run created the engine from these files.";

   private readonly string _root;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a private folder for this test's synthetic repository and runs, under the test
   /// binaries, removed again in <see cref="Dispose"/>.
   /// </summary>
   public EngineFactsTests()
   {
      _root = Path.Combine( AppContext.BaseDirectory, "engine-facts-tests", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( _root );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Every row of the real fact sheet resolves against the three real v7 runs and the repository
   /// tree: results tokens in every run, code tokens on code lines, the saved page, the absence checks.
   /// </summary>
   [Fact]
   public Task EveryRealFactRow_ResolvesAgainstThe3V7RunsAndTheTree()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string[] outcome = RealFacts( FactsFile() );
         Assert.Single( outcome );
         Assert.Matches( @"^rows=\d+ resolved=\d+$", outcome[0] );
         string[] parts = outcome[0].Split( ' ' );
         Assert.Equal( parts[0][5..], parts[1][9..] );
         Assert.True( int.Parse( parts[0][5..] ) >= 19 * 4 );
      } );
   }

   /// <summary>
   /// Every target of the v7 runs (19) has an index, search, storage and protocol row, and the
   /// sheet names no target the runs do not have.
   /// </summary>
   [Fact]
   public Task EveryOneOf19Targets_HasIndexSearchStorageAndProtocolRows()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         List<JsonElement> rows = ReadRows( FactsFile() );
         string[] targets = V7Targets();
         Assert.Equal( 19, targets.Length );
         foreach( string target in targets )
         {
            foreach( string kind in new[] { "index", "search", "storage", "protocol" } )
            {
               Assert.True( rows.Any( r => r.GetProperty( "target" ).GetString() == target && r.GetProperty( "kind" ).GetString() == kind ), $"{target} has no {kind} row" );
            }
         }

         Assert.All( rows, r => Assert.Contains( r.GetProperty( "target" ).GetString(), targets ) );
      } );
   }

   /// <summary>
   /// The real sheet's rows obey the text rules: under 12 words, no banned or forbidden word, no
   /// dash, a valid confidence, and a source on every row.
   /// </summary>
   [Fact]
   public Task RealRows_ObeyTheTextRules()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string[] forbidden = { "only", "default", "defaults", "defaulted", "cause", "causes", "caused" };
         foreach( JsonElement row in ReadRows( FactsFile() ) )
         {
            string text = row.GetProperty( "text" ).GetString()!;
            string label = row.GetProperty( "target" ).GetString() + "/" + row.GetProperty( "kind" ).GetString();
            Assert.True( (int)ReportSourcesHarness.Call( "Words", text )! < 12, $"{label}: {text}" );
            Assert.Empty( (string[])ReportSourcesHarness.Call( "Banned", text )! );
            foreach( string word in forbidden )
            {
               Assert.False( Regex.IsMatch( text, @"(?<![A-Za-z0-9_])" + word + @"(?![A-Za-z0-9_])", RegexOptions.IgnoreCase ), $"{label} uses '{word}': {text}" );
            }

            Assert.DoesNotContain( '\u2014', text );
            Assert.DoesNotContain( '\u2013', text );
            Assert.Contains( row.GetProperty( "confidence" ).GetString(), new[] { "recorded", "measured", "documented", "set-by-code", "checked" } );
            Assert.Contains( "#", row.GetProperty( "source" ).GetString() );
         }
      } );
   }

   /// <summary>
   /// H1: the Elasticsearch search row takes exact from the measured state (load.indexNote says no
   /// graph exists and every search scans all 524 vectors), and no run's index text or state is
   /// quoted as the reason. The recorded index text says HNSW with k and num_candidates.
   /// </summary>
   [Fact]
   public Task Elasticsearch_SearchRow_IsExactFromTheMeasuredState()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         JsonElement row = ReadRows( FactsFile() ).Single( r => r.GetProperty( "target" ).GetString() == "elasticsearch" && r.GetProperty( "kind" ).GetString() == "search" );
         Assert.Equal( "exact", row.GetProperty( "mode" ).GetString() );
         Assert.Equal( "measured", row.GetProperty( "confidence" ).GetString() );
         Assert.StartsWith( "results:targets[elasticsearch].load.indexNote#", row.GetProperty( "source" ).GetString() );
         foreach( string run in ReportSourcesHarness.V7Runs() )
         {
            using JsonDocument document = JsonDocument.Parse( File.ReadAllText( run ) );
            JsonElement target = document.RootElement.GetProperty( "targets" ).EnumerateArray().Single( t => t.GetProperty( "name" ).GetString() == "elasticsearch" );
            Assert.Contains( "NO HNSW GRAPH", target.GetProperty( "load" ).GetProperty( "indexNote" ).GetString() );
            Assert.False( target.GetProperty( "indexState" ).GetProperty( "afterLoad" ).GetProperty( "ready" ).GetBoolean() );
            Assert.Contains( "HNSW float32", target.GetProperty( "index" ).GetString() );
         }
      } );
   }

   /// <summary>
   /// H1 plant: a row that calls Elasticsearch approximate, built from its static index text,
   /// is refused because every run's own indexState says no graph exists.
   /// </summary>
   [Fact]
   public Task PlantedApproximateElasticsearchRow_IsRefusedByTheMeasuredState()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string facts = WriteFacts( Replace( "elasticsearch", "search", new
         {
            target = "elasticsearch", kind = "search", text = "HNSW m=16 ef_construction=128", mode = "approximate",
            source = "results:targets[elasticsearch].index#HNSW float32, no quantization, m=16, ef_construction=128", confidence = "recorded",
         } ) );
         string[] outcome = RealFacts( facts );
         Assert.Contains( outcome, p => p.StartsWith( "elasticsearch/search: the row says approximate but " ) && p.Contains( "indexState.afterLoad says exact" ) );
         Assert.Equal( 3, outcome.Count( p => p.Contains( "indexState.afterLoad says exact" ) ) );
         Assert.Equal( 3, outcome.Count( p => p.Contains( "indexState.afterSearch says exact" ) ) );
      } );
   }

   /// <summary>
   /// The rule is general, shown on synthetic runs: a sheet whose rows agree with the measured state
   /// is accepted; the same sheet with the search row called exact, while each run's state says a
   /// graph is searched, is refused once per run and snapshot.
   /// </summary>
   [Fact]
   public Task SyntheticSheet_IsAcceptedWhenModeAgreesAndRefusedWhenItDoesNot()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string repo = SynthRepo( "src/GenericVectorBuilder.Engines/Sinks/SynthSink.cs", "using Synth.Client;\n" );
         string[] graphRuns = WriteSynthRuns( new[] { "HNSW m=16 graph", "HNSW m=16 graph", "HNSW m=16 graph" } );
         string good = WriteFacts( SynthRows( "HNSW m=16 graph", "HNSW", "approximate" ) );
         string[] accepted = (string[])ReportSourcesHarness.Call( "Facts", good, graphRuns, repo )!;
         Assert.Equal( new[] { "rows=4 resolved=4" }, accepted );

         string[] scanRuns = WriteSynthRuns( new[] { "no index used, exact scan by design", "no index used, exact scan by design", "no index used, exact scan by design" } );
         string bad = WriteFacts( SynthRows( "no index used, exact scan by design", "exact scan", "exact" ) );
         string[] refused = (string[])ReportSourcesHarness.Call( "Facts", bad, scanRuns, repo )!;
         Assert.Equal( 6, refused.Count( p => p.StartsWith( "synth/search: the row says exact but run-" ) && p.Contains( " says graph:" ) ) );
         Assert.Equal( 7, refused.Length );
      } );
   }

   /// <summary>
   /// What a snapshot says: not ready or nothing indexed or a scan phrase is exact; ready with vectors indexed is a graph; counts that are absent say unknown.
   /// </summary>
   [Fact]
   public Task StateSays_ReadsExactGraphAndUnknown()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         Assert.Equal( "exact", ReportSourcesHarness.Call( "StateSays", ES_STATE_FAILED ) );
         Assert.Equal( "graph", ReportSourcesHarness.Call( "StateSays", GRAPH_STATE ) );
         Assert.Equal( "exact", ReportSourcesHarness.Call( "StateSays", "{\"ready\":true,\"indexedVectors\":0,\"totalVectors\":524,\"detail\":\"x\"}" ) );
         Assert.Equal( "exact", ReportSourcesHarness.Call( "StateSays", "{\"ready\":true,\"totalVectors\":524,\"detail\":\"no index, exact scan by design\"}" ) );
         Assert.Equal( "unknown", ReportSourcesHarness.Call( "StateSays", "{\"ready\":true,\"totalVectors\":524,\"detail\":\"Chroma exposes no index status\"}" ) );
         Assert.Equal( "unknown", ReportSourcesHarness.Call( "StateSays", "[]" ) );
      } );
   }

   /// <summary>
   /// A planted false fact: a token that no run holds (m=32 where the runs say m=16) is refused with
   /// the reference named.
   /// </summary>
   [Fact]
   public Task PlantedFalseFact_IsRefused()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string facts = WriteFacts( Replace( "pgvector", "index", new
         {
            target = "pgvector", kind = "index", text = "HNSW vector_cosine_ops m=32", confidence = "recorded",
            source = "results:targets[pgvector].index#HNSW vector_cosine_ops m=32",
         } ) );
         string[] outcome = RealFacts( facts );
         Assert.Contains( outcome, p => p.StartsWith( "pgvector/index: the source does not resolve" ) && p.Contains( "targets[pgvector].index" ) );
      } );
   }

   /// <summary>
   /// A stale value: a token that was true in one run and not in the others fails, because every
   /// run given must hold it. Here the MongoDB layout count in the index note differs between runs.
   /// </summary>
   [Fact]
   public Task TokenHeldByOnlyOneRun_IsRefused()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string[] runs = WriteSynthRuns( new[] { "alpha beta", "alpha beta", "alpha gamma" } );
         string repo = SynthRepo( "src/GenericVectorBuilder.Engines/Sinks/SynthSink.cs", "using Synth.Client;\n" );
         string facts = WriteFacts( SynthRows( "beta", "beta", "approximate" ) );
         string[] outcome = (string[])ReportSourcesHarness.Call( "Facts", facts, runs, repo )!;
         Assert.Contains( outcome, p => p.StartsWith( "synth/index: the source does not resolve" ) && p.Contains( "run-2: token not in targets[synth].index" ) );
      } );
   }

   /// <summary>
   /// The text may not say more than its token: a text with "approximate" over a token without it, or a number the token lacks, is refused.
   /// </summary>
   [Fact]
   public Task TextWithAWordOrNumberItsTokenLacks_IsRefused()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string facts = WriteFacts( Replace( "pgvector", "index", new
         {
            target = "pgvector", kind = "index", text = "approximate HNSW vector_cosine_ops m=16 ef_construction=200", confidence = "recorded",
            source = "results:targets[pgvector].index#HNSW vector_cosine_ops m=16 ef_construction=128",
         } ) );
         string[] outcome = RealFacts( facts );
         string problem = Assert.Single( outcome, p => p.StartsWith( "pgvector/index: the text uses words its source does not have" ) );
         Assert.Contains( "approximate", problem );
         Assert.Contains( "200", problem );
      } );
   }

   /// <summary>
   /// A banned word, the word "only", the word "default" and a text of 12 words are each refused in a row's text.
   /// </summary>
   [Fact]
   public Task BannedForbiddenAndLongTexts_AreRefused()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         foreach( (string text, string expected) in new[]
         {
            ( "HNSW m=16 because ef_construction=128", "banned word" ),
            ( "HNSW only", "banned word" ),
            ( "HNSW default", "banned word" ),
            ( "HNSW vector_cosine_ops m=16 ef_construction=128 a b c d e f g h", "limit is 11" ),
         } )
         {
            string facts = WriteFacts( Replace( "pgvector", "index", new
            {
               target = "pgvector", kind = "index", text, confidence = "recorded",
               source = "results:targets[pgvector].index#HNSW vector_cosine_ops m=16 ef_construction=128",
            } ) );
            string[] outcome = RealFacts( facts );
            Assert.Contains( outcome, p => p.StartsWith( "pgvector/index:" ) && p.Contains( expected ) );
         }
      } );
   }

   /// <summary>
   /// A token that sits only in a comment is not evidence: the MariaDB buffer pool size is in a
   /// comment of mariadb-bench.compose.yaml as "innodb-buffer-pool-size 2G" and is set on a code line as
   /// "--innodb-buffer-pool-size=2G"; only the code line resolves. The same holds for a C# comment.
   /// </summary>
   [Fact]
   public Task CommentLines_AreNotEvidence()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string comment = Resolve( "deploy/engines/mariadb-bench.compose.yaml#innodb-buffer-pool-size 2G" );
         string code = Resolve( "deploy/engines/mariadb-bench.compose.yaml#--innodb-buffer-pool-size=2G" );
         Assert.StartsWith( "MISSING", comment );
         Assert.StartsWith( "FOUND", code );
         Assert.Contains( "deploy/engines/mariadb-bench.compose.yaml:47", code );
         string repo = SynthRepo( "src/GenericVectorBuilder.Engines/Sinks/SynthSink.cs",
            "// using Hidden.Client;\n/* using Block.Client;\n   using Block2.Client; */\nusing Real.Client; // using Trailing.Client;\nstring s = \"http://x\"; // note\n" );
         string[] none = Array.Empty<string>();
         Assert.StartsWith( "MISSING", (string)ReportSourcesHarness.Call( "Resolve", "file", "src/GenericVectorBuilder.Engines/Sinks/SynthSink.cs#using Hidden.Client;", none, repo )! );
         Assert.StartsWith( "MISSING", (string)ReportSourcesHarness.Call( "Resolve", "file", "src/GenericVectorBuilder.Engines/Sinks/SynthSink.cs#using Block2.Client;", none, repo )! );
         Assert.StartsWith( "MISSING", (string)ReportSourcesHarness.Call( "Resolve", "file", "src/GenericVectorBuilder.Engines/Sinks/SynthSink.cs#using Trailing.Client;", none, repo )! );
         Assert.StartsWith( "FOUND", (string)ReportSourcesHarness.Call( "Resolve", "file", "src/GenericVectorBuilder.Engines/Sinks/SynthSink.cs#using Real.Client;", none, repo )! );
         Assert.StartsWith( "FOUND", (string)ReportSourcesHarness.Call( "Resolve", "file", "src/GenericVectorBuilder.Engines/Sinks/SynthSink.cs#\"http://x\"", none, repo )! );
      } );
   }

   /// <summary>
   /// A file source cannot leave the repository, and a missing file is a finding and never a skip.
   /// </summary>
   [Fact]
   public Task FileSources_StayInsideTheRepoAndMustExist()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         Assert.Contains( "must be a relative path inside the repository", Resolve( "../etc/passwd#root" ) );
         Assert.Contains( "must be a relative path inside the repository", Resolve( "/etc/passwd#root" ) );
         Assert.Contains( "does not exist", Resolve( "deploy/engines/no-such.compose.yaml#mem_limit: 8g" ) );
         Assert.Contains( "a file reference is <repo path>#<token>", Resolve( "deploy/engines/redis.compose.yaml" ) );
      } );
   }

   /// <summary>
   /// The protocol and set-by-setup rules tie a file to its target: a sink file of another target, a
   /// file outside src/ for a protocol row, and a compose file the run did not create the engine from
   /// (mariadb.compose.yaml for the bench target, which uses mariadb-bench.compose.yaml) are refused.
   /// </summary>
   [Fact]
   public Task WrongFileForTheTarget_IsRefused()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string wrongSink = WriteFacts( Replace( "opensearch", "protocol", new
         {
            target = "opensearch", kind = "protocol", text = "HttpClient", confidence = "set-by-code",
            source = "src/GenericVectorBuilder.Engines/Sinks/ElasticsearchRest.cs#new HttpClient { BaseAddress = new Uri( _baseUrl + \"/\" )",
         } ) );
         Assert.Contains( RealFacts( wrongSink ), p => p.StartsWith( "opensearch/protocol: the file is not a sink of target 'opensearch'" ) );

         string sqlVsSqlite = WriteFacts( Replace( "sql", "protocol", new
         {
            target = "sql", kind = "protocol", text = "Microsoft.Data.Sqlite", confidence = "set-by-code",
            source = "src/GenericVectorBuilder.Engines/Sinks/SqliteVecSink.cs#using Microsoft.Data.Sqlite;",
         } ) );
         Assert.Contains( RealFacts( sqlVsSqlite ), p => p.StartsWith( "sql/protocol: the file is not a sink of target 'sql'" ) );

         string wrongCompose = WriteFacts( Replace( "mariadb", "set-by-setup", new
         {
            target = "mariadb", kind = "set-by-setup", text = "innodb-buffer-pool-size=2G", confidence = "set-by-code",
            source = "deploy/engines/mariadb.compose.yaml#--innodb-buffer-pool-size=2G",
         } ) );
         string[] outcome = RealFacts( wrongCompose );
         Assert.Equal( 3, outcome.Count( p => p.Contains( "did not create mariadb from mariadb.compose.yaml" ) ) );
      } );
   }

   /// <summary>
   /// The confidence label must match the kind of source: a documented row on a results source, a
   /// measured row on a static field and a recorded row on a state field are each refused.
   /// </summary>
   [Fact]
   public Task ConfidenceMustMatchTheSourceKind()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string measuredOnStatic = WriteFacts( Replace( "pgvector", "index", new
         {
            target = "pgvector", kind = "index", text = "HNSW vector_cosine_ops m=16 ef_construction=128", confidence = "measured",
            source = "results:targets[pgvector].index#HNSW vector_cosine_ops m=16 ef_construction=128",
         } ) );
         Assert.Contains( RealFacts( measuredOnStatic ), p => p.StartsWith( "pgvector/index: the confidence is 'measured' but a source of this kind is 'recorded'" ) );

         string recordedOnState = WriteFacts( Replace( "sql", "search", new
         {
            target = "sql", kind = "search", text = "no index used, exact scan by design", confidence = "recorded", mode = "exact",
            source = "results:targets[sql].indexState.afterLoad.detail#no index used, exact scan by design",
         } ) );
         Assert.Contains( RealFacts( recordedOnState ), p => p.StartsWith( "sql/search: the confidence is 'recorded' but a source of this kind is 'measured'" ) );

         string documentedOnResults = WriteFacts( Replace( "typesense", "storage", new
         {
            target = "typesense", kind = "storage", text = "index held in memory", confidence = "documented",
            source = "results:targets[typesense].index#index held in memory",
         } ) );
         Assert.Contains( RealFacts( documentedOnResults ), p => p.StartsWith( "typesense/storage: the confidence is 'documented'" ) );
      } );
   }

   /// <summary>
   /// A results source must read the row's own target, and a fact kind must rest on its own kind of source.
   /// </summary>
   [Fact]
   public Task SourceMustBelongToTheRowsTargetAndKind()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string otherTarget = WriteFacts( Replace( "pgvector", "index", new
         {
            target = "pgvector", kind = "index", text = "HNSW m=16 ef_construction=128", confidence = "recorded",
            source = "results:targets[mariadb].index#VECTOR INDEX (HNSW variant) DISTANCE=cosine, M=16",
         } ) );
         Assert.Contains( RealFacts( otherTarget ), p => p.StartsWith( "pgvector/index: the source must read targets[pgvector]" ) );

         string protocolFromResults = WriteFacts( Replace( "pgvector", "protocol", new
         {
            target = "pgvector", kind = "protocol", text = "HNSW", confidence = "recorded",
            source = "results:targets[pgvector].index#HNSW",
         } ) );
         Assert.Contains( RealFacts( protocolFromResults ), p => p.StartsWith( "pgvector/protocol: a protocol fact cannot rest on a results source" ) );
      } );
   }

   /// <summary>
   /// Storage: a row that says "not recorded" for an engine whose recorded text does say where its
   /// vectors are held (Vespa: "vectors held in memory") is refused, so the absence is real; and an
   /// absence row may say nothing but "not recorded".
   /// </summary>
   [Fact]
   public Task NotRecordedStorageRow_IsRefusedWhenTheRunsDoRecordIt()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string vespa = WriteFacts( Replace( "vespa", "storage", new
         {
            target = "vespa", kind = "storage", text = "not recorded", confidence = "checked",
            source = "absent:results:targets[vespa]#in memory|in-memory|memory pool|RAM",
         } ) );
         Assert.Contains( RealFacts( vespa ), p => p.StartsWith( "vespa/storage: the source does not resolve" ) && p.Contains( "occurs in targets[vespa]" ) );

         string wordy = WriteFacts( Replace( "clickhouse", "storage", new
         {
            target = "clickhouse", kind = "storage", text = "on disk", confidence = "checked",
            source = "absent:results:targets[clickhouse]#in memory|in-memory|memory pool|RAM",
         } ) );
         Assert.Contains( RealFacts( wordy ), p => p.StartsWith( "clickhouse/storage: an absence check supports only" ) );
      } );
   }

   /// <summary>
   /// Storage facts the runs do record: Vespa, Typesense and Oracle name the in-memory placement in
   /// their own text; Redis rests on the saved redis.io page. Every other target says "not recorded".
   /// </summary>
   [Fact]
   public Task StorageRows_AreRecordedForVespaTypesenseOracleAndRedisOnly()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         Dictionary<string, string> storage = ReadRows( FactsFile() ).Where( r => r.GetProperty( "kind" ).GetString() == "storage" )
            .ToDictionary( r => r.GetProperty( "target" ).GetString()!, r => r.GetProperty( "text" ).GetString()! );
         Assert.Equal( 19, storage.Count );
         Assert.Equal( "vectors held in memory", storage["vespa"] );
         Assert.Equal( "index held in memory", storage["typesense"] );
         Assert.Equal( "HNSW in-memory neighbor graph", storage["oracle"] );
         Assert.Equal( "Redis is an in-memory but persistent on disk database", storage["redis"] );
         Assert.Equal( 15, storage.Values.Count( t => t == "not recorded" ) );
      } );
   }

   /// <summary>
   /// An absence check has a confidence of its own: every real "not recorded" storage row says "checked", and a row that says
   /// "recorded" for an absence is refused, so a page never reads "not recorded" beside "recorded" (dry check fix-with).
   /// </summary>
   [Fact]
   public Task AbsenceRows_SayChecked_AndAreRefusedWhenTheyClaimRecorded()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         List<JsonElement> absent = ReadRows( FactsFile() ).Where( r => r.GetProperty( "source" ).GetString()!.StartsWith( "absent:", StringComparison.Ordinal ) ).ToList();
         Assert.True( absent.Count >= 13, $"{absent.Count} absence rows" );
         Assert.All( absent, r => Assert.Equal( "checked", r.GetProperty( "confidence" ).GetString() ) );
         IEnumerable<object> others = RealRows().Where( r => !( r.Target == "mariadb" && r.Kind == "storage" ) ).Select( r => r.Row );
         string planted = WriteFacts( others.Append( new { target = "mariadb", kind = "storage", text = "not recorded", confidence = "recorded", source = "absent:results:targets[mariadb]#in memory|in-memory|memory pool|RAM" } ) );
         Assert.Contains( RealFacts( planted ), p => p.StartsWith( "mariadb/storage: the confidence is 'recorded' but a source of this kind is 'checked'", StringComparison.Ordinal ) );
      } );
   }

   /// <summary>
   /// A fact text with an unbalanced bracket is refused (the real sheet once held "CAST( @q AS VECTOR"), and a set-by-setup or protocol row whose
   /// token stops where the code computes the value (DuckDB's "SET memory_limit = '" before the interpolated limit) is refused, so no row prints a
   /// setting without its value.
   /// </summary>
   [Fact]
   public Task FactRows_AreRefusedWithAnUnbalancedBracketOrNoValue()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         List<object> rows = RealRows().Select( r => r.Row ).ToList();
         rows.Add( new
         {
            target = "sql", kind = "protocol", text = "CAST( @q AS VECTOR", confidence = "set-by-code",
            source = "src/GenericVectorBuilder.Core/Sinks/SqlVectorSink.cs#CAST( @q AS VECTOR(",
         } );
         Assert.Contains( RealFacts( WriteFacts( rows ) ), p => p.StartsWith( "sql/protocol:", StringComparison.Ordinal ) && p.Contains( "unbalanced", StringComparison.Ordinal ) );
         rows.RemoveAt( rows.Count - 1 );
         rows.Add( new
         {
            target = "duckdb", kind = "set-by-setup", text = "SET memory_limit", confidence = "set-by-code",
            source = "src/GenericVectorBuilder.Engines/Sinks/DuckDbSink.cs#Run( connection, $\"SET memory_limit = '",
         } );
         Assert.Contains( RealFacts( WriteFacts( rows ) ), p => p.StartsWith( "duckdb/set-by-setup:", StringComparison.Ordinal ) && p.Contains( "where a value begins", StringComparison.Ordinal ) );
      } );
   }

   /// <summary>
   /// The real sheet holds DuckDB's two settings with their values and the SQL cast with its brackets closed.
   /// </summary>
   [Fact]
   public Task RealSheet_HoldsDuckDbSettingsWithValues_AndTheClosedCast()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         List<JsonElement> rows = ReadRows( FactsFile() );
         List<string> duck = rows.Where( r => r.GetProperty( "target" ).GetString() == "duckdb" && r.GetProperty( "kind" ).GetString() == "set-by-setup" ).Select( r => r.GetProperty( "text" ).GetString()! ).ToList();
         Assert.Contains( duck, t => t.Contains( "MemoryLimit", StringComparison.Ordinal ) && t.Contains( "8GB", StringComparison.Ordinal ) );
         Assert.Contains( duck, t => t.Contains( "CheckpointThreshold", StringComparison.Ordinal ) && t.Contains( "256MB", StringComparison.Ordinal ) );
         string cast = rows.First( r => r.GetProperty( "target" ).GetString() == "sql" && r.GetProperty( "text" ).GetString()!.Contains( "CAST(", StringComparison.Ordinal ) ).GetProperty( "text" ).GetString()!;
         Assert.Equal( cast.Count( c => c == '(' ), cast.Count( c => c == ')' ) );
      } );
   }

   /// <summary>
   /// The Redis in-memory fact is a quote inside one text node of the saved redis.io page; a
   /// changed quote, a quote spanning two nodes and a quote only in a script are refused; entities in a page are decoded before comparing.
   /// </summary>
   [Fact]
   public Task DocQuote_MustSitInsideOneTextNode()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string page = "design/engine-docs/redis-faq-2026-10-07.html";
         Assert.StartsWith( "FOUND", Resolve( $"doc:{page}#Redis is an in-memory but persistent on disk database" ) );
         Assert.StartsWith( "MISSING", Resolve( $"doc:{page}#Redis is an in-memory database" ) );
         Assert.StartsWith( "MISSING", Resolve( $"doc:{page}#Redis FAQ Redis is an in-memory" ) );
         string html = "<html><head><script>var a = 'only in a script';</script></head><body><p>It can&#39;t   be\n  larger than <b>memory</b>.</p><p>Second &amp; last</p></body></html>";
         string repo = SynthRepo( "design/engine-docs/synth.html", html );
         string[] none = Array.Empty<string>();
         Assert.StartsWith( "FOUND", (string)ReportSourcesHarness.Call( "Resolve", "doc", "doc:design/engine-docs/synth.html#It can't be larger than", none, repo )! );
         Assert.StartsWith( "FOUND", (string)ReportSourcesHarness.Call( "Resolve", "doc", "doc:design/engine-docs/synth.html#Second & last", none, repo )! );
         Assert.StartsWith( "MISSING", (string)ReportSourcesHarness.Call( "Resolve", "doc", "doc:design/engine-docs/synth.html#than memory", none, repo )! );
         Assert.StartsWith( "MISSING", (string)ReportSourcesHarness.Call( "Resolve", "doc", "doc:design/engine-docs/synth.html#only in a script", none, repo )! );
      } );
   }

   /// <summary>
   /// The saved page is a raw copy: no em or en dash (the house rule and the freeze gate), no
   /// former-employer name, and it holds the claim the Redis row quotes.
   /// </summary>
   [Fact]
   public Task SavedDocsPage_IsRawAndFreeOfDashes()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string folder = Path.Combine( ReportSourcesHarness.RepoRoot(), "design", "engine-docs" );
         string[] files = Directory.GetFiles( folder, "*.html" );
         Assert.NotEmpty( files );
         string sources = File.ReadAllText( Path.Combine( folder, "SOURCES.txt" ) );
         foreach( string file in files )
         {
            Assert.Contains( Path.GetFileName( file ) + "  https://", sources );
            Assert.Matches( @"^[a-z0-9]+(-[a-z0-9]+)*-2026-10-07\.html$", Path.GetFileName( file ) );
            string text = File.ReadAllText( file );
            Assert.DoesNotContain( '\u2014', text );
            Assert.DoesNotContain( '\u2013', text );
            Assert.DoesNotMatch( FORBIDDEN_NAMES, text );
            Assert.Contains( "<html", text );
         }
      } );
   }

   /// <summary>
   /// The saved durability logs are raw copies kept with an index, a hash list and a statement of where they came from: every file is listed with its hash,
   /// the index names only files that exist, and no file holds an em or en dash or the former employer's name (the freeze gate's rules).
   /// </summary>
   [Fact]
   public Task SavedDurabilityLogs_AreListedHashedAndFreeOfDashesAndNames()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string folder = Path.Combine( ReportSourcesHarness.RepoRoot(), "design", "engine-docs", "durability-logs-2026-10-04" );
         Assert.True( Directory.Exists( folder ), folder );
         string[] listed = File.ReadAllLines( Path.Combine( folder, "SHA256SUMS" ) ).Where( l => l.Trim().Length > 0 ).Select( l => l.Split( "  ", 2 )[1] ).ToArray();
         string[] saved = Directory.GetFiles( folder ).Select( Path.GetFileName ).Where( n => n is not ( "SHA256SUMS" or "SOURCES.txt" or "index.json" ) ).Select( n => n! ).ToArray();
         Assert.Equal( saved.OrderBy( n => n, StringComparer.Ordinal ).ToArray(), listed.OrderBy( n => n, StringComparer.Ordinal ).ToArray() );
         using JsonDocument index = JsonDocument.Parse( File.ReadAllText( Path.Combine( folder, "index.json" ) ) );
         Assert.Equal( "design/engine-docs/durability-logs-2026-10-04", index.RootElement.GetProperty( "folder" ).GetString() );
         foreach( string file in index.RootElement.GetProperty( "logs" ).EnumerateArray().SelectMany( l => l.GetProperty( "files" ).EnumerateArray().Select( f => f.GetString()! ) ) )
         {
            Assert.Contains( file, saved );
         }

         foreach( string path in Directory.GetFiles( folder ) )
         {
            string text = File.ReadAllText( path );
            Assert.False( text.Contains( '\u2014' ) || text.Contains( '\u2013' ), $"{path} holds an em or en dash" );
            Assert.DoesNotMatch( FORBIDDEN_NAMES, text );
         }

         Assert.Contains( "SHA256SUMS", File.ReadAllText( Path.Combine( folder, "SOURCES.txt" ) ) );
      } );
   }

   /// <summary>
   /// A missing or broken fact sheet fails loud: a missing file, bad JSON, an unknown field (a typo), a missing field and an empty list are errors with the path named, and never an empty sheet.
   /// </summary>
   [Fact]
   public Task MissingOrBrokenFactSheet_FailsLoud()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         Assert.StartsWith( "FileNotFoundException", (string)ReportSourcesHarness.Call( "LoadMessage", Path.Combine( _root, "nope.json" ) )! );
         string broken = Path.Combine( _root, "broken.json" );
         File.WriteAllText( broken, "[ { \"target\": " );
         Assert.StartsWith( "EngineFactsException", (string)ReportSourcesHarness.Call( "LoadMessage", broken )! );
         string typo = Path.Combine( _root, "typo.json" );
         File.WriteAllText( typo, "[ { \"target\": \"a\", \"kind\": \"index\", \"text\": \"x\", \"source\": \"s#t\", \"confidance\": \"recorded\" } ]" );
         Assert.Contains( "unknown field 'confidance'", (string)ReportSourcesHarness.Call( "LoadMessage", typo )! );
         string missing = Path.Combine( _root, "missing.json" );
         File.WriteAllText( missing, "[ { \"target\": \"a\", \"kind\": \"index\", \"text\": \"x\", \"source\": \"s#t\" } ]" );
         Assert.Contains( "no text for 'confidence'", (string)ReportSourcesHarness.Call( "LoadMessage", missing )! );
         string empty = Path.Combine( _root, "empty.json" );
         File.WriteAllText( empty, "[]" );
         Assert.Contains( "no fact rows", (string)ReportSourcesHarness.Call( "LoadMessage", empty )! );
         string notArray = Path.Combine( _root, "obj.json" );
         File.WriteAllText( notArray, "{}" );
         Assert.Contains( "must hold a JSON array", (string)ReportSourcesHarness.Call( "LoadMessage", notArray )! );
      } );
   }

   /// <summary>
   /// A target with no storage row, a row for a target no run has, and a duplicate source are refused, and ValidateOrThrow lists every problem in one exception.
   /// </summary>
   [Fact]
   public Task CoverageAndDuplicates_AreRefused()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         List<object> rows = RealRows().Where( r => !( r.Target == "oracle" && r.Kind == "storage" ) ).Select( r => r.Row ).ToList();
         rows.Add( new { target = "ghost", kind = "index", text = "x", confidence = "recorded", source = "results:targets[ghost].index#x" } );
         string facts = WriteFacts( rows );
         string[] outcome = RealFacts( facts );
         Assert.Contains( "oracle has no storage fact", outcome );
         Assert.Contains( "facts name target 'ghost', which no run has", outcome );
         string thrown = (string)ReportSourcesHarness.Call( "Throws", facts, ReportSourcesHarness.V7Runs(), ReportSourcesHarness.RepoRoot() )!;
         Assert.StartsWith( "EngineFactsException: ", thrown );
         Assert.Contains( "oracle has no storage fact", thrown );
         Assert.Contains( "facts name target 'ghost'", thrown );

         List<object> twice = RealRows().Select( r => r.Row ).ToList();
         twice.Add( twice[0] );
         Assert.Contains( RealFacts( WriteFacts( twice ) ), p => p.Contains( "the same source is used twice" ) );
      } );
   }

   /// <summary>
   /// A search row needs a mode, no other row may have one, and the mode must be what its own source says: a source naming an index cannot be called exact.
   /// </summary>
   [Fact]
   public Task SearchMode_IsRequiredAndMustMatchTheSource()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string noMode = WriteFacts( Replace( "milvus", "search", new
         {
            target = "milvus", kind = "search", text = "approximate, no exact mode", confidence = "recorded",
            source = "results:targets[milvus].index#approximate only (no exact mode)",
         } ) );
         Assert.Contains( RealFacts( noMode ), p => p.StartsWith( "milvus/search: a search row needs a mode" ) );

         string wrongMode = WriteFacts( Replace( "milvus", "search", new
         {
            target = "milvus", kind = "search", text = "approximate, no exact mode", confidence = "recorded", mode = "exact",
            source = "results:targets[milvus].index#approximate only (no exact mode)",
         } ) );
         Assert.Contains( RealFacts( wrongMode ), p => p.StartsWith( "milvus/search: the row says exact but its source text says approximate" ) );

         string modeOnIndex = WriteFacts( Replace( "milvus", "index", new
         {
            target = "milvus", kind = "index", text = "HNSW M=16 efConstruction=128", confidence = "recorded", mode = "approximate",
            source = "results:targets[milvus].index#HNSW M=16 efConstruction=128",
         } ) );
         Assert.Contains( RealFacts( modeOnIndex ), p => p.StartsWith( "milvus/index: a search row needs a mode" ) );

         string neither = WriteFacts( Replace( "milvus", "search", new
         {
            target = "milvus", kind = "search", text = "Strong consistency searches", confidence = "recorded", mode = "approximate",
            source = "results:targets[milvus].index#Strong consistency searches",
         } ) );
         Assert.Contains( RealFacts( neither ), p => p.StartsWith( "milvus/search: the mode cannot be derived" ) );
      } );
   }

   /// <summary>
   /// Two search rows of one target that disagree on exact or approximate are refused, even when each is true to its own source.
   /// </summary>
   [Fact]
   public Task SearchRowsOfOneTarget_MustAgree()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         List<object> rows = RealRows().Select( r => r.Row ).ToList();
         rows.Add( new
         {
            target = "pgvector", kind = "search", text = "sequential scan", confidence = "recorded", mode = "exact",
            source = "results:targets[pgvector].index#exact mode = same query with index scans off (sequential scan)",
         } );
         string[] outcome = RealFacts( WriteFacts( rows ) );
         Assert.Contains( "pgvector has search rows that disagree on exact or approximate", outcome );
      } );
   }

   /// <summary>
   /// The real sheet's search modes: the engines whose recorded or measured text says every vector is
   /// scanned are exact (sql, sqlitevec, qdrant, elasticsearch) and the other 15 are approximate.
   /// </summary>
   [Fact]
   public Task RealSearchModes_AreFourExactAndFifteenApproximate()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         Dictionary<string, string> modes = ReadRows( FactsFile() ).Where( r => r.GetProperty( "kind" ).GetString() == "search" )
            .ToDictionary( r => r.GetProperty( "target" ).GetString()!, r => r.GetProperty( "mode" ).GetString()! );
         Assert.Equal( 19, modes.Count );
         Assert.Equal( new[] { "elasticsearch", "qdrant", "sql", "sqlitevec" }, modes.Where( m => m.Value == "exact" ).Select( m => m.Key ).OrderBy( k => k ).ToArray() );
         Assert.Equal( 15, modes.Count( m => m.Value == "approximate" ) );
      } );
   }

   /// <summary>
   /// The fixed facts the brief names are on the sheet with the right source: Redis save and appendonly
   /// from its compose line, the heaps, the MariaDB buffer pool, GOMEMLIMIT, one mem_limit per container target, the pgvector SET LOCAL
   /// line, the Oracle CPU cap from the recorded index text, and the protocol rows of the hand-written HttpClient helpers.
   /// </summary>
   [Fact]
   public Task BriefedFacts_AreOnTheSheet()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         List<JsonElement> rows = ReadRows( FactsFile() );
         bool Has( string target, string kind, string source ) => rows.Any( r => r.GetProperty( "target" ).GetString() == target && r.GetProperty( "kind" ).GetString() == kind && r.GetProperty( "source" ).GetString() == source );
         Assert.True( Has( "redis", "set-by-setup", "deploy/engines/redis.compose.yaml#--save \"300 1\"" ) );
         Assert.True( Has( "redis", "set-by-setup", "deploy/engines/redis.compose.yaml#--appendonly no" ) );
         Assert.True( Has( "elasticsearch", "set-by-setup", "deploy/engines/elasticsearch.compose.yaml#-Xms2g -Xmx2g" ) );
         Assert.True( Has( "opensearch", "set-by-setup", "deploy/engines/opensearch.compose.yaml#-Xms2g -Xmx2g" ) );
         Assert.True( Has( "mariadb", "set-by-setup", "deploy/engines/mariadb-bench.compose.yaml#--innodb-buffer-pool-size=2G" ) );
         Assert.True( Has( "weaviate", "set-by-setup", "deploy/engines/weaviate.compose.yaml#GOMEMLIMIT: 6GiB" ) );
         Assert.True( Has( "pgvector", "set-by-setup", "src/GenericVectorBuilder.Engines/Sinks/PgVectorSink.cs#SET LOCAL enable_seqscan = off; SET LOCAL jit = off" ) );
         Assert.True( Has( "oracle", "cap", "results:targets[oracle].index#Oracle Free caps itself at 2 CPUs" ) );
         Assert.Equal( 17, rows.Count( r => r.GetProperty( "kind" ).GetString() == "set-by-setup" && r.GetProperty( "source" ).GetString()!.Contains( "#mem_limit: " ) ) );
         foreach( string target in new[] { "elasticsearch", "opensearch", "typesense", "vespa" } )
         {
            Assert.Contains( rows, r => r.GetProperty( "target" ).GetString() == target && r.GetProperty( "kind" ).GetString() == "protocol" && r.GetProperty( "text" ).GetString()!.StartsWith( "HttpClient" ) );
         }

         JsonElement typesense = rows.Single( r => r.GetProperty( "target" ).GetString() == "typesense" && r.GetProperty( "text" ).GetString() == "vector as string vectorQuery" );
         Assert.Contains( "TypesenseSink.cs#", typesense.GetProperty( "source" ).GetString() );
      } );
   }

   /// <summary>
   /// File sources carry a token and never a line number, because other pieces edit those files and line numbers move.
   /// </summary>
   [Fact]
   public Task FileSources_NeverCarryALineNumber()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         foreach( JsonElement row in ReadRows( FactsFile() ) )
         {
            string source = row.GetProperty( "source" ).GetString()!;
            if( source.StartsWith( "results", StringComparison.Ordinal ) || source.StartsWith( "absent:", StringComparison.Ordinal ) || source.StartsWith( "doc:", StringComparison.Ordinal ) )
            {
               continue;
            }

            Assert.DoesNotMatch( @"\.(cs|yaml|yml|sh|json):\d+", source.Split( '#' )[0] );
            Assert.Matches( @"^(src|deploy)/[A-Za-z0-9_./-]+\.(cs|yaml|sh)#.+$", source );
         }
      } );
   }

   /// <summary>
   /// The fact sheet, its exclusions and the engine-facts hash a consolidated result records: the
   /// hash helper agrees with sha256sum of the file and a missing file is an error.
   /// </summary>
   [Fact]
   public Task FileSha256_MatchesTheFileBytes()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string path = FactsFile();
         string expected = Convert.ToHexString( SHA256.HashData( File.ReadAllBytes( path ) ) ).ToLowerInvariant();
         Assert.Equal( expected, ReportSourcesHarness.Call( "Sha256", path ) );
         Assert.Throws<FileNotFoundException>( () => ReportSourcesHarness.Call( "Sha256", Path.Combine( _root, "missing.bin" ) ) );
      } );
   }

   /// <summary>
   /// basis-exclusions.json holds exactly the three rows of the basis prototype (Vespa QPS@8 seeds
   /// 502 and 503, Elasticsearch QPS@8 seed 503, ClickHouse every metric seeds 501 to 503), each
   /// with a kind, a reason and a source file under design/verdicts.
   /// </summary>
   [Fact]
   public Task BasisExclusions_AreTheThreePrototypeRows()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         using JsonDocument document = JsonDocument.Parse( File.ReadAllText( Path.Combine( ReportSourcesHarness.RepoRoot(), EXCLUSIONS_PATH ) ) );
         JsonElement[] rows = document.RootElement.EnumerateArray().ToArray();
         Assert.Equal( 3, rows.Length );
         (string target, int[] seeds, string metric, string kind)[] expected =
         {
            ( "vespa", new[] { 502, 503 }, "qps8", "method defect" ),
            ( "elasticsearch", new[] { 503 }, "qps8", "method defect" ),
            ( "clickhouse", new[] { 501, 502, 503 }, "*", "unrecorded config change" ),
         };
         for( int i = 0; i < 3; i++ )
         {
            Assert.Equal( expected[i].target, rows[i].GetProperty( "target" ).GetString() );
            Assert.Equal( expected[i].seeds, rows[i].GetProperty( "seeds" ).EnumerateArray().Select( s => s.GetInt32() ).ToArray() );
            Assert.Equal( expected[i].metric, rows[i].GetProperty( "metric" ).GetString() );
            Assert.Equal( expected[i].kind, rows[i].GetProperty( "kind" ).GetString() );
            Assert.False( string.IsNullOrWhiteSpace( rows[i].GetProperty( "why" ).GetString() ) );
            Assert.StartsWith( "design/verdicts/", rows[i].GetProperty( "source" ).GetString() );
         }
      } );
   }

   /// <summary>
   /// Each exclusion's source file exists, holds the cited item (a numbered line "N." in a numbered
   /// list, with the evidence inside that item's lines), names the excluded seeds, and its text is
   /// free of banned words. The evidence is the item number and the seeds, not a bare word.
   /// </summary>
   [Fact]
   public Task EachExclusion_IsSupportedByItsVerdictItem()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string root = ReportSourcesHarness.RepoRoot();
         using JsonDocument document = JsonDocument.Parse( File.ReadAllText( Path.Combine( root, EXCLUSIONS_PATH ) ) );
         string v5 = File.ReadAllText( Path.Combine( root, "design", "verdicts", "v5-verdict.txt" ) );
         foreach( JsonElement row in document.RootElement.EnumerateArray() )
         {
            string source = Path.Combine( root, row.GetProperty( "source" ).GetString()! );
            Assert.True( File.Exists( source ), source );
            string[] lines = File.ReadAllLines( source );
            int item = row.GetProperty( "item" ).GetInt32();
            int start = Array.FindIndex( lines, l => Regex.IsMatch( l, $@"^{item}\. " ) );
            Assert.True( start >= 0, $"no item {item} in {source}" );
            int next = Array.FindIndex( lines, start + 1, l => Regex.IsMatch( l, @"^\d+\. " ) );
            string block = string.Join( "\n", lines.Skip( start ).Take( ( next < 0 ? lines.Length : next ) - start ) );
            foreach( JsonElement evidence in row.GetProperty( "evidence" ).EnumerateArray() )
            {
               Assert.Contains( evidence.GetString()!, block );
            }

            string seedEvidence = row.GetProperty( "seedEvidence" ).GetString()!;
            Assert.Contains( seedEvidence, block );
            Assert.Contains( row.GetProperty( "evidence" ).EnumerateArray().Select( e => e.GetString()! ), e => e.Contains( seedEvidence, StringComparison.Ordinal ) );
            foreach( int seed in row.GetProperty( "seeds" ).EnumerateArray().Select( s => s.GetInt32() ) )
            {
               Assert.Contains( seed.ToString(), v5 );
            }

            string why = row.GetProperty( "why" ).GetString()!;
            Assert.Empty( (string[])ReportSourcesHarness.Call( "Banned", why )! );
            Assert.True( (int)ReportSourcesHarness.Call( "Words", why )! <= 35, why );
            Assert.DoesNotContain( '\u2014', why );
            foreach( Match number in Regex.Matches( why, @"\d+(\.\d+)?" ) )
            {
               Assert.Contains( number.Value, string.Join( "\n", lines ) );
            }
         }
      } );
   }

   /// <summary>
   /// The three verdict copies, the golden question list and the saved documentation pages are
   /// byte-identical to their sources: the SHA256SUMS in the repository lists every copy and matches
   /// it, and where the original still exists on this machine it matches the copy too.
   /// </summary>
   [Fact]
   public Task CopiesAreByteIdenticalToTheirSources()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string root = ReportSourcesHarness.RepoRoot();
         string verdicts = Path.Combine( root, "design", "verdicts" );
         Dictionary<string, string> listed = File.ReadAllLines( Path.Combine( verdicts, "SHA256SUMS" ) ).Where( l => l.Length > 0 )
            .ToDictionary( l => l[66..].Trim(), l => l[..64] );
         string[] docs = Directory.GetFiles( Path.Combine( root, "design", "engine-docs" ), "*.html" ).Select( f => "../engine-docs/" + Path.GetFileName( f ) ).ToArray();
         string[] expectedNames = new[] { "v5-verdict.txt", "v6-verdict.md", "v7-verdict.md", "../bench-inputs/questions_golden.json" }.Concat( docs ).OrderBy( k => k, StringComparer.Ordinal ).ToArray();
         Assert.Equal( expectedNames, listed.Keys.OrderBy( k => k, StringComparer.Ordinal ).ToArray() );
         foreach( KeyValuePair<string, string> entry in listed )
         {
            Assert.Equal( entry.Value, Sha( Path.GetFullPath( Path.Combine( verdicts, entry.Key ) ) ) );
         }

         (string copy, string original)[] pairs =
         {
            ( "design/verdicts/v5-verdict.txt", "/home/dan/gvb-work/v8-design/blockers/sources/v5-verdict.txt" ),
            ( "design/verdicts/v6-verdict.md", "/home/dan/gvb-work/v6-verdict.md" ),
            ( "design/verdicts/v7-verdict.md", "/home/dan/gvb-work/v7-verdict.md" ),
            ( "design/bench-inputs/questions_golden.json", "/home/dan/ForClaude/evalkit/questions_golden.json" ),
         };
         foreach( (string copy, string original) in pairs )
         {
            Assert.True( File.Exists( Path.Combine( root, copy ) ), copy );
            if( File.Exists( original ) )
            {
               Assert.Equal( File.ReadAllBytes( original ), File.ReadAllBytes( Path.Combine( root, copy ) ) );
            }
         }

         Assert.Equal( "e3a917ec8c5e687d2a2d6b83bc8ffd7eb901f0ffac8d8b277995b86898e9a712", Sha( Path.Combine( root, "design", "bench-inputs", "questions_golden.json" ) ) );
      } );
   }

   /// <summary>
   /// The new data and source files hold no em or en dash and no former-employer name.
   /// </summary>
   [Fact]
   public Task NewFiles_HaveNoDashesOrForbiddenNames()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string root = ReportSourcesHarness.RepoRoot();
         string[] files =
         {
            FACTS_PATH, EXCLUSIONS_PATH, "design/verdicts/SHA256SUMS",
            "src/GenericVectorBuilder.Bench/Report/EngineFacts.cs", "src/GenericVectorBuilder.Bench/Report/Sentence.cs",
            "src/GenericVectorBuilder.Bench/Report/SentenceAudit.cs", "src/GenericVectorBuilder.Bench/Report/BannedWords.cs",
            "tests/GenericVectorBuilder.Engines.Tests/Bench/EngineFactsTests.cs", "tests/GenericVectorBuilder.Engines.Tests/Bench/SentenceAuditTests.cs",
         };
         foreach( string file in files )
         {
            string text = File.ReadAllText( Path.Combine( root, file ) );
            Assert.False( text.Contains( '\u2014' ) || text.Contains( '\u2013' ), $"{file} contains an em or en dash" );
            Assert.False( Regex.IsMatch( text, FORBIDDEN_NAMES ), $"{file} names the former employer" );
         }
      } );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Path of the real fact sheet.
   /// </summary>
   /// <returns>The path.</returns>
   private static string FactsFile()
   {
      return Path.Combine( ReportSourcesHarness.RepoRoot(), FACTS_PATH );
   }

   /// <summary>
   /// Validates a fact sheet file against the three real v7 runs and the repository tree.
   /// </summary>
   /// <param name="facts">The sheet.</param>
   /// <returns>The facade's lines: the counts first, then the problems.</returns>
   private static string[] RealFacts( string facts )
   {
      return (string[])ReportSourcesHarness.Call( "Facts", facts, ReportSourcesHarness.V7Runs(), ReportSourcesHarness.RepoRoot() )!;
   }

   /// <summary>
   /// Resolves a file or doc reference against the real repository tree with no runs.
   /// </summary>
   /// <param name="reference">The reference ("path#token" or "doc:path#quote").</param>
   /// <returns>The facade's "FOUND ..." or "MISSING ..." line.</returns>
   private static string Resolve( string reference )
   {
      string kind = reference.StartsWith( "doc:", StringComparison.Ordinal ) ? "doc" : "file";
      return (string)ReportSourcesHarness.Call( "Resolve", kind, reference, Array.Empty<string>(), ReportSourcesHarness.RepoRoot() )!;
   }

   /// <summary>
   /// Reads the rows of a fact sheet as JSON elements that stay valid after the call.
   /// </summary>
   /// <param name="path">The sheet.</param>
   /// <returns>The rows.</returns>
   private static List<JsonElement> ReadRows( string path )
   {
      return JsonDocument.Parse( File.ReadAllText( path ) ).RootElement.EnumerateArray().ToList();
   }

   /// <summary>
   /// The 19 target names of the first real v7 run.
   /// </summary>
   /// <returns>The names.</returns>
   private static string[] V7Targets()
   {
      using JsonDocument document = JsonDocument.Parse( File.ReadAllText( ReportSourcesHarness.V7Runs()[0] ) );
      return document.RootElement.GetProperty( "targets" ).EnumerateArray().Select( t => t.GetProperty( "name" ).GetString()! ).ToArray();
   }

   /// <summary>
   /// The real sheet's rows, each with its target, kind and a plain object that serialises to the same row.
   /// </summary>
   /// <returns>The rows in file order.</returns>
   private static List<(string Target, string Kind, object Row)> RealRows()
   {
      return ReadRows( FactsFile() ).Select( r => ( r.GetProperty( "target" ).GetString()!, r.GetProperty( "kind" ).GetString()!, (object)JsonSerializer.Deserialize<Dictionary<string, string>>( r.GetRawText() )! ) ).ToList();
   }

   /// <summary>
   /// The real sheet with the first row of one target and kind replaced by a planted row.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="kind">The kind.</param>
   /// <param name="planted">The row to put in its place.</param>
   /// <returns>All rows, with the planted one in place.</returns>
   private static List<object> Replace( string target, string kind, object planted )
   {
      var rows = new List<object>();
      bool done = false;
      foreach( (string t, string k, object row) in RealRows() )
      {
         if( !done && t == target && k == kind )
         {
            rows.Add( planted );
            done = true;
         }
         else
         {
            rows.Add( row );
         }
      }

      Assert.True( done, $"the real sheet has no {target}/{kind} row to replace" );
      return rows;
   }

   /// <summary>
   /// Writes rows to a fact sheet file in this test's folder.
   /// </summary>
   /// <param name="rows">The rows.</param>
   /// <returns>The path.</returns>
   private string WriteFacts( IEnumerable<object> rows )
   {
      string path = Path.Combine( _root, $"facts-{Guid.NewGuid():N}.json" );
      File.WriteAllText( path, JsonSerializer.Serialize( rows ) );
      return path;
   }

   /// <summary>
   /// Writes one synthetic run per index text, each with a single target "synth" whose index state says a graph is searched.
   /// </summary>
   /// <param name="indexTexts">The index text of target synth in each run.</param>
   /// <returns>The paths of the results.json files; the folders are named run-0, run-1 and so on.</returns>
   private string[] WriteSynthRuns( string[] indexTexts )
   {
      var paths = new List<string>();
      string group = Guid.NewGuid().ToString( "N" );
      for( int i = 0; i < indexTexts.Length; i++ )
      {
         string folder = Path.Combine( _root, group, $"run-{i}" );
         Directory.CreateDirectory( folder );
         var target = new
         {
            name = SYNTH, index = indexTexts[i], notes = new[] { SYNTH_NOTE },
            load = new { indexNote = "ready" },
            indexState = new { afterLoad = JsonSerializer.Deserialize<JsonElement>( GRAPH_STATE ), afterSearch = JsonSerializer.Deserialize<JsonElement>( GRAPH_STATE ) },
         };
         string path = Path.Combine( folder, "results.json" );
         File.WriteAllText( path, JsonSerializer.Serialize( new { targets = new[] { target } } ) );
         paths.Add( path );
      }

      return paths.ToArray();
   }

   /// <summary>
   /// Builds a one-file synthetic repository.
   /// </summary>
   /// <param name="relative">The file's path inside it.</param>
   /// <param name="content">The file's text.</param>
   /// <returns>The repository root.</returns>
   private string SynthRepo( string relative, string content )
   {
      string repo = Path.Combine( _root, $"repo-{Guid.NewGuid():N}" );
      string full = Path.Combine( repo, relative );
      Directory.CreateDirectory( Path.GetDirectoryName( full )! );
      File.WriteAllText( full, content );
      return repo;
   }

   /// <summary>
   /// The four required rows for the synthetic target: an index row and a search row that both read the index text, an absence-checked storage row and a protocol row.
   /// </summary>
   /// <param name="indexToken">The token of the index row (and its text).</param>
   /// <param name="searchToken">The token of the search row (and its text).</param>
   /// <param name="mode">The search row's mode.</param>
   /// <returns>The rows.</returns>
   private static List<object> SynthRows( string indexToken, string searchToken, string mode )
   {
      return new List<object>
      {
         new { target = SYNTH, kind = "index", text = indexToken, source = $"results:targets[{SYNTH}].index#{indexToken}", confidence = "recorded" },
         new { target = SYNTH, kind = "search", text = searchToken, source = $"results:targets[{SYNTH}].index#{searchToken}", confidence = "recorded", mode },
         new { target = SYNTH, kind = "storage", text = "not recorded", source = $"absent:results:targets[{SYNTH}]#in memory|in-memory", confidence = "checked" },
         new { target = SYNTH, kind = "protocol", text = "Synth.Client", source = "src/GenericVectorBuilder.Engines/Sinks/SynthSink.cs#using Synth.Client;", confidence = "set-by-code" },
      };
   }

   /// <summary>
   /// SHA-256 of a file as lower-case hex.
   /// </summary>
   /// <param name="path">The file.</param>
   /// <returns>The hash.</returns>
   private static string Sha( string path )
   {
      return Convert.ToHexString( SHA256.HashData( File.ReadAllBytes( path ) ) ).ToLowerInvariant();
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Removes this test's synthetic folders.
   /// </summary>
   public void Dispose()
   {
      try
      {
         Directory.Delete( _root, true );
      }
      catch( IOException )
      {
         // A leftover folder under the test binaries is harmless; the next build clears it.
      }
   }

   #endregion IDisposable
}
