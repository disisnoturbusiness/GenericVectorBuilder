using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The run's own results.md and results.json (ResultsWriter): the exact-mode line counts searches
/// (the older text said "queries"), the framing bullet carries the end-to-end client wording,
/// and the client's CPU per search is written and shown only when a run recorded it.
/// Why the sources are compiled here with Roslyn: the benchmark is a console project this test
/// project does not reference. Only the report writer, the report types and the framing strings are
/// compiled, with two stand-ins for the benchmark types they touch (a memory reading and the shell
/// helper), because the rest of the benchmark is not under test here.
/// </summary>
public sealed class ConsolidateWriterTests : IDisposable
{
   #region Data Members

   private const string WRITER_TYPE = "GenericVectorBuilder.Bench.Report.ResultsWriter";
   private const string REPORT_TYPE = "GenericVectorBuilder.Bench.Report.BenchReport";
   private static readonly Lazy<Assembly> COMPILED = new( Compile );
   private static readonly string[] SOURCES = { "Report/ResultsWriter.cs", "Report/BenchReport.cs", "Report/MachineFacts.cs", "Stats/ConsolidateFraming.cs" };

   /// <summary>The two benchmark types the writer touches, reduced to what it calls.</summary>
   private const string STAND_INS = @"
namespace GenericVectorBuilder.Bench.Targets
{
   public sealed record Measurement( long? Bytes, string Text )
   {
      public static string Format( long bytes ) { return bytes + "" B""; }
   }

   public static class Shell
   {
      public static Task<string?> TryOutputAsync( string file, string[] args, CancellationToken ct ) { return Task.FromResult<string?>( null ); }
   }
}";

   private readonly string _root;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a private folder under the test binaries for the files a test writes.
   /// </summary>
   public ConsolidateWriterTests()
   {
      _root = Path.Combine( AppContext.BaseDirectory, "consolidate-writer-tests", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( _root );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The details of a target count the exact mode in searches ("120 searches"), never "queries":
   /// each timed exact search is one search, and the older text miscounted them as queries.
   /// </summary>
   [Fact]
   public void Markdown_CountsExactModeInSearches_NotQueries()
   {
      string md = Markdown( Report( withClientCpu: false ) );

      Assert.Contains( "- Exact mode: 120 searches, p50 2.50 ms, p95 3.10 ms, recall 1.000", md );
      Assert.DoesNotContain( "120 queries", md );
      Assert.DoesNotContain( " queries, p50", md );
   }

   /// <summary>
   /// The framing bullet under the run's header says it is request speed on a small collection,
   /// measured end to end through the client code the benchmark uses for each engine (a vendor library
   /// for some, its own HttpClient REST code for others), reflecting per-request cost including that
   /// client code and not index scaling. It does not call every client a .NET client: eight of the
   /// 19 engines are reached through the benchmark's own REST code (verdict v8i, B2).
   /// </summary>
   [Fact]
   public void Markdown_CarriesTheFramingLine()
   {
      string md = Markdown( Report( withClientCpu: false ) );

      Assert.Contains( "- Request speed on a small collection (524 vectors): Measured end to end through the client code the benchmark uses for each engine (a vendor library for some engines, the benchmark's own HttpClient REST code for others); at this size it reflects per-request cost including that client code, not index scaling.", md );
      Assert.DoesNotContain( ".NET client; at this size", md );
      Assert.DoesNotContain( "\u2014", md );
   }

   /// <summary>
   /// When a target carries the client's CPU per search the table has a column for each level it was
   /// recorded at, the target's details give the figures, and the definition line is printed once;
   /// a target without it shows "-".
   /// </summary>
   [Fact]
   public void Markdown_ShowsClientCpu_WhenTheRunRecordedIt()
   {
      string md = Markdown( Report( withClientCpu: true ) );

      Assert.Contains( "| QPS@1 | QPS@8 | client CPU ms/search@1 | client CPU ms/search@8 | recall@10 |", md );
      Assert.Contains( "| 800.0 | 3000.0 | 0.49 | 0.30 | 1.000 |", md );
      Assert.Contains( "| 700.0 | 2500.0 | - | - | 1.000 |", md );
      Assert.Contains( "- Client CPU per search: 1 searcher 0.49 ms, 8 searchers 0.30 ms", md );
      Assert.Equal( 1, md.Split( "- " + CLIENT_CPU_LINE ).Length - 1 );
      Assert.DoesNotContain( "close to the latency", md );
   }

   /// <summary>
   /// A run that recorded no client CPU writes no column, no line and no field: results.md has
   /// nothing about it and results.json has no clientCpuMsPerSearch, so older readers see no change;
   /// with it recorded the field sits in the target's search section, keyed by level.
   /// </summary>
   [Fact]
   public void Json_HasClientCpuOnlyWhenRecorded()
   {
      string plain = Write( Report( withClientCpu: false ), "plain" );
      Assert.DoesNotContain( "clientCpuMsPerSearch", plain );
      Assert.DoesNotContain( "client CPU", File.ReadAllText( Path.Combine( _root, "plain", "results.md" ) ) );

      string carried = Write( Report( withClientCpu: true ), "carried" );
      JsonElement cpu = JsonDocument.Parse( carried ).RootElement.GetProperty( "targets" )[0].GetProperty( "search" ).GetProperty( "clientCpuMsPerSearch" );
      Assert.Equal( 0.49, cpu.GetProperty( "1" ).GetDouble(), 9 );
      Assert.Equal( 0.30, cpu.GetProperty( "8" ).GetDouble(), 9 );
      Assert.DoesNotContain( "clientCpuMsPerSearch", JsonDocument.Parse( carried ).RootElement.GetProperty( "targets" )[1].GetProperty( "search" ).ToString() );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>The client-CPU line, read from the compiled ConsolidateFraming (gap G23: no hand-kept copy that can drift from the source).</summary>
   private static string CLIENT_CPU_LINE => (string)COMPILED.Value.GetType( "GenericVectorBuilder.Bench.Stats.ConsolidateFraming" )!.GetField( "CLIENT_CPU_LINE" )!.GetValue( null )!;

   /// <summary>
   /// A report of two targets (the first with an exact mode of 120 searches) built from JSON, so the
   /// compiled types are filled the way a results.json would fill them.
   /// </summary>
   /// <param name="withClientCpu">True to give the first target a client CPU per search at levels 1 and 8.</param>
   /// <returns>The BenchReport object.</returns>
   private static object Report( bool withClientCpu )
   {
      string cpu = withClientCpu ? ", \"clientCpuMsPerSearch\": { \"1\": 0.49, \"8\": 0.30 }" : string.Empty;
      string json = "{ \"startedUtc\": \"2026-10-05T10:00:00Z\", \"command\": \"run-all\", \"commandLine\": \"run-all\", \"pipeline\": \"eshoponweb\", \"collection\": \"gvbbench_eshoponweb\", \"rows\": 524, \"dimension\": 1024, "
         + "\"source\": \"s\", \"queries\": \"golden: 20\", \"queryCount\": 20, \"top\": 10, \"concurrency\": [1, 8], \"secondsPerLevel\": 20, \"targets\": [ "
         + "{ \"name\": \"fast\", \"engine\": \"E 1\", \"index\": \"I\", \"hosting\": \"compose\", \"search\": { \"latencySamples\": 100, \"p50Ms\": 1.2, \"p95Ms\": 1.9, \"p99Ms\": 2.2, \"qps\": { \"1\": 800, \"8\": 3000 }, \"recall\": 1, "
         + "\"errors\": 0, \"exactQueries\": 120, \"exactP50Ms\": 2.5, \"exactP95Ms\": 3.1, \"exactRecall\": 1" + cpu + " } }, "
         + "{ \"name\": \"plain\", \"engine\": \"E 2\", \"index\": \"I\", \"hosting\": \"compose\", \"search\": { \"latencySamples\": 90, \"p50Ms\": 1.4, \"p95Ms\": 2.0, \"p99Ms\": 2.4, \"qps\": { \"1\": 700, \"8\": 2500 }, \"recall\": 1, \"errors\": 0 } } ] }";
      Type type = COMPILED.Value.GetType( REPORT_TYPE )!;
      var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
      return JsonSerializer.Deserialize( json, type, options )!;
   }

   /// <summary>
   /// Renders the report with ResultsWriter.Markdown.
   /// </summary>
   /// <param name="report">The BenchReport object.</param>
   /// <returns>The markdown.</returns>
   private static string Markdown( object report )
   {
      return (string)COMPILED.Value.GetType( WRITER_TYPE )!.GetMethod( "Markdown" )!.Invoke( null, new[] { report } )!;
   }

   /// <summary>
   /// Writes the report with ResultsWriter.Write into a folder of this test and reads results.json back.
   /// </summary>
   /// <param name="report">The BenchReport object.</param>
   /// <param name="name">Folder name under the test's private folder.</param>
   /// <returns>The results.json text.</returns>
   private string Write( object report, string name )
   {
      string folder = Path.Combine( _root, name );
      COMPILED.Value.GetType( WRITER_TYPE )!.GetMethod( "Write" )!.Invoke( null, new[] { report, folder } );
      return File.ReadAllText( Path.Combine( folder, "results.json" ) );
   }

   /// <summary>
   /// Compiles the writer, the report types and the framing strings from the repository into an
   /// in-memory assembly, with the stand-ins for the two benchmark types they touch.
   /// </summary>
   /// <returns>The assembly.</returns>
   private static Assembly Compile()
   {
      string bench = Path.Combine( RepoRoot(), "src", "GenericVectorBuilder.Bench" );
      var trees = SOURCES.Select( s => CSharpSyntaxTree.ParseText( File.ReadAllText( Path.Combine( bench, s ) ), path: s ) ).ToList();
      trees.Add( CSharpSyntaxTree.ParseText( STAND_INS ) );
      trees.Add( CSharpSyntaxTree.ParseText( "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; global using System.Threading; global using System.Threading.Tasks;" ) );
      IEnumerable<MetadataReference> references = ( (string)AppContext.GetData( "TRUSTED_PLATFORM_ASSEMBLIES" )! )
         .Split( Path.PathSeparator ).Select( p => MetadataReference.CreateFromFile( p ) );
      var options = new CSharpCompilationOptions( OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable );
      CSharpCompilation compilation = CSharpCompilation.Create( "ResultsWriterUnderTest", trees, references, options );
      using var image = new MemoryStream();
      Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit( image );
      Assert.True( result.Success, string.Join( Environment.NewLine, result.Diagnostics.Where( d => d.Severity == DiagnosticSeverity.Error ) ) );
      return Assembly.Load( image.ToArray() );
   }

   /// <summary>
   /// Walks up from the test binary to the folder holding the solution file.
   /// </summary>
   /// <returns>The repository root.</returns>
   private static string RepoRoot()
   {
      var dir = new DirectoryInfo( AppContext.BaseDirectory );
      while( dir != null && !File.Exists( Path.Combine( dir.FullName, "GenericVectorBuilder.slnx" ) ) )
      {
         dir = dir.Parent;
      }

      return dir?.FullName ?? throw new InvalidOperationException( "Repository root not found." );
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Removes this test's private folder.
   /// </summary>
   public void Dispose()
   {
      try
      {
         Directory.Delete( _root, recursive: true );
      }
      catch( IOException )
      {
         // A leftover folder under the test binaries is harmless; the next build's clean removes it.
      }
   }

   #endregion IDisposable
}
