using GenericVectorBuilder.Core.Chunking;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Mapping;
using GenericVectorBuilder.Core.Pipeline;
using GenericVectorBuilder.Core.Sinks;
using GenericVectorBuilder.Core.Sources.Files;
using GenericVectorBuilder.Core.State;
using GenericVectorBuilder.Tests.Fakes;
using Grpc.Core;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Document = GenericVectorBuilder.Core.Contracts.Document;

namespace GenericVectorBuilder.Tests.Unit;

/// <summary>
/// Regression tests for the 2026-10-03 pipeline gaps: the mass-delete guard counting this
/// run's own writes, a sink emptied while its collection still exists, a key column that is
/// unique per file but not across files, and Qdrant searches that were approximate. Each
/// regression test failed on the code before the fix; the guard tests next to them pin down
/// the cases the fixes must NOT disturb (a cancelled write, a sink that cannot count, tables
/// whose keys are unique).
/// </summary>
public class PipelineGap2Tests : IDisposable
{
   #region Data Members

   private const string PIPELINE = "gap2";

   private readonly TempFolder _data = new();
   private readonly TempFolder _other = new();
   private readonly TempFolder _stateDir = new();
   private readonly SqliteStateStore _state;
   private readonly FakeEmbedder _embedder = new();
   private readonly MemorySink _a = new( "a" );
   private readonly MemorySink _b = new( "b" );

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Sets up a fresh state store per test.
   /// </summary>
   public PipelineGap2Tests()
   {
      _state = new SqliteStateStore( Path.Combine( _stateDir.Path, "state.db" ) );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// Gap 1: the "more than half" test counted this run's writes, so pointing the pipeline at a
   /// different folder of similar size saw 10 of 22 instead of 10 of 10 and deleted everything
   /// it held. It must refuse, end Partial and say why.
   /// </summary>
   [Fact]
   public async Task DifferentFolderOfSimilarSize_DeletesAreRefused()
   {
      _data.Write( "p.csv", Rows( 1, 10, "Tool" ) );
      await RunAsync( _data.Path );
      _other.Write( "other.csv", Rows( 101, 12, "Part" ) );

      RunSnapshot run = await RunAsync( _other.Path );

      Assert.Equal( 0, run.DocsDeleted );
      Assert.Equal( 22, _a.Records.Count );
      Assert.Equal( RunStatus.Partial, run.Status );
      Assert.Contains( "10 of the 10 rows a held before the run started", run.Message );
      Assert.Contains( "right folder", run.Message );
   }

   /// <summary>
   /// Gap 2: a sink emptied while its collection still exists (a truncated table) stayed empty
   /// forever, because state said every row was unchanged. It must be rewritten, the run must
   /// say so, and the healthy sink must not be touched.
   /// </summary>
   [Fact]
   public async Task EmptiedSink_IsWrittenAgain()
   {
      _data.Write( "p.csv", Rows( 1, 3, "Tool" ) );
      await RunAsync( _data.Path );
      _a.Records.Clear();
      _embedder.Embedded.Clear();

      RunSnapshot run = await RunAsync( _data.Path );

      Assert.Equal( 3, _a.Records.Count );
      Assert.Equal( 3, _b.Records.Count );
      Assert.Equal( 3, run.ChunksWritten.GetValueOrDefault( "a" ) );
      Assert.Equal( 0, run.ChunksWritten.GetValueOrDefault( "b" ) );
      Assert.Contains( run.Errors, e => e.StartsWith( "a: holds 0 chunk(s)", StringComparison.Ordinal ) && e.Contains( "written to it again", StringComparison.Ordinal ) );
      Assert.Equal( RunStatus.Completed, run.Status );
   }

   /// <summary>
   /// Gap 2, the other direction: a sink holding chunks state has no record of gets a warning,
   /// and nothing is deleted or rewritten.
   /// </summary>
   [Fact]
   public async Task SinkHoldingUnknownChunks_IsWarnedAndLeftAlone()
   {
      _data.Write( "p.csv", Rows( 1, 3, "Tool" ) );
      await RunAsync( _data.Path );
      VectorRecord any = _a.Records.Values.First();
      Guid stranger = Guid.NewGuid();
      _a.Records[stranger] = any with { Chunk = any.Chunk with { ChunkId = stranger, Text = "written by hand" } };
      _embedder.Embedded.Clear();

      RunSnapshot run = await RunAsync( _data.Path );

      Assert.Contains( "written by hand", _a.Texts() );
      Assert.Empty( _embedder.Embedded );
      Assert.Empty( _a.Deleted );
      Assert.Contains( run.Errors, e => e.StartsWith( "a: holds 4 chunk(s), 1 more than", StringComparison.Ordinal ) );
      Assert.Equal( RunStatus.Completed, run.Status );
   }

   /// <summary>
   /// Guard for gap 2: after a failed write, state lists more chunk ids than the sink holds.
   /// That is normal bookkeeping, not an emptied sink, so only the one changed row is written
   /// again, not the whole pipeline.
   /// </summary>
   [Fact]
   public async Task FailedWrite_DoesNotTriggerAFullRewrite()
   {
      _data.Write( "p.csv", Rows( 1, 3, "Tool" ) );
      await RunAsync( _data.Path );
      _data.Write( "p.csv", Rows( 1, 3, "Tool" ).Replace( "1,Tool 1", "1,Claw Tool 1", StringComparison.Ordinal ) );
      _a.FailUpserts = true;
      await RunAsync( _data.Path );
      _a.FailUpserts = false;
      _embedder.Embedded.Clear();

      RunSnapshot run = await RunAsync( _data.Path );

      Assert.Equal( new[] { "id: 1\nname: Claw Tool 1" }, _embedder.Embedded );
      Assert.DoesNotContain( run.Errors, e => e.Contains( "written to it again", StringComparison.Ordinal ) );
      Assert.Equal( RunStatus.Completed, run.Status );
   }

   /// <summary>
   /// Guard for gap 2: a sink that does not implement the count keeps working exactly as
   /// before; the check is skipped instead of failing the sink.
   /// </summary>
   [Fact]
   public async Task SinkThatCannotCount_SkipsTheCheck()
   {
      _data.Write( "p.csv", Rows( 1, 3, "Tool" ) );
      var plain = new UncountableSink( _a );
      await RunAsync( _data.Path, sinks: new ISink[] { plain } );
      _embedder.Embedded.Clear();

      RunSnapshot run = await RunAsync( _data.Path, sinks: new ISink[] { plain } );

      Assert.Equal( RunStatus.Completed, run.Status );
      Assert.Equal( 3, run.DocsUnchanged );
      Assert.Empty( run.SinkErrors );
      await Assert.ThrowsAsync<NotSupportedException>( () => ( (ISink)plain ).CountAsync( PIPELINE, CancellationToken.None ) );
   }

   /// <summary>
   /// Gap 3: key 5 is in a.csv and in b.csv. Suffixed by read order, b's row took a's key the
   /// moment a.csv was rejected, overwrote a's kept row and deleted it. Qualified by origin,
   /// a's copy survives the reject untouched, and a third run with a.csv repaired ends with
   /// exactly one copy of each row.
   /// </summary>
   [Fact]
   public async Task KeySharedAcrossFiles_RejectedFileKeepsItsCopy()
   {
      var shared = new Dictionary<string, IReadOnlySet<string>>( StringComparer.OrdinalIgnoreCase ) { ["id"] = new HashSet<string> { "5" } };
      var none = new Dictionary<string, IReadOnlySet<string>>( StringComparer.OrdinalIgnoreCase );
      _data.Write( "a.csv", "id,name\n5,Hammer\n1,Saw\n" );
      _data.Write( "b.csv", "id,name\n5,Drill\n2,Level\n" );
      await RunAsync( _data.Path, shared: shared );
      Assert.Equal( 4, _a.Records.Count );

      // a.csv no longer parses, so the scan rejects it and 5 is in one accepted file only.
      _data.Write( "a.csv", "id,name\n5,\"Hammer never closed\n1,Saw\n" );
      RunSnapshot run = await RunAsync( _data.Path, shared: none );

      Assert.Contains( run.Rejected, r => r.Origin == "a.csv" );
      Assert.Contains( "id: 5\nname: Hammer", _a.Texts() );
      Assert.Contains( "id: 1\nname: Saw", _a.Texts() );
      Assert.Contains( "id: 5\nname: Drill", _a.Texts() );
      Dictionary<string, DocState> states = await _state.LoadAsync( PIPELINE, "a", CancellationToken.None );
      Assert.Equal( 2, states.Values.Count( s => s.Origin == "a.csv" ) );

      _data.Write( "a.csv", "id,name\n5,Hammer\n1,Saw\n" );
      await RunAsync( _data.Path, shared: shared );

      Assert.Equal( new[] { "id: 1\nname: Saw", "id: 2\nname: Level", "id: 5\nname: Drill", "id: 5\nname: Hammer" }, _a.Texts() );
   }

   /// <summary>
   /// Gap 3, the safety half: a table whose key values are unique gets document keys that are
   /// byte for byte the ones the code before the fix produced (literals below), including the
   /// within-file duplicate suffix, the hashed long key and the content key. Shared values of
   /// other tables and of values not present change nothing.
   /// </summary>
   [Fact]
   public void UniqueKeys_DocKeysMatchTheStartingTree()
   {
      var mapper = new RowDocumentMapper( new Dictionary<string, TableMapping> { ["t123"] = new TableMapping( "id", null ) } );
      mapper.SetCrossOriginDuplicates( "t123", "orders", new Dictionary<string, IReadOnlySet<string>> { ["ID"] = new HashSet<string> { "99" }, ["name"] = new HashSet<string> { "Saw" } } );
      mapper.SetCrossOriginDuplicates( "t999", "other", new Dictionary<string, IReadOnlySet<string>> { ["id"] = new HashSet<string> { "5", "7" } } );
      var columns = new[] { "id", "name" };
      var rows = new (string Origin, string? Id, string Name)[]
      {
         ("f.csv", "5", "Hammer"), ("f.csv", "5", "Again"), ("g.csv", "7", "Saw"), ("g.csv", new string( 'x', 300 ), "Long"), ("f.csv", null, "Blank key"),
      };

      string[] keys = rows.Select( ( r, i ) => mapper.Map( new SourceRecord( "orders", r.Origin, i + 1, columns, new[] { r.Id, r.Name }, "t123" ) )!.DocKey ).ToArray();

      Assert.Equal( new[]
      {
         "t123|5",
         "t123|5#2",
         "t123|7",
         "t123|k:beed82c17cf24c843b9654523b452cd50ac9c1d4261abaae6dcd8c93da1ca9e2",
         "t123|h:2ef813d3cc466ff7",
      }, keys );
   }

   /// <summary>
   /// Gap 3, the shape of a qualified key: a shared value gets "@" plus 10 hex digits of its
   /// origin's hash, found by table id or, for a record without one, by table name; the key
   /// metadata keeps the raw value; repeats inside one file are numbered per file.
   /// </summary>
   [Fact]
   public void SharedKey_IsQualifiedByOrigin()
   {
      var mapper = new RowDocumentMapper( new Dictionary<string, TableMapping> { ["t123"] = new TableMapping( "id", null ), ["orders"] = new TableMapping( "id", null ) } );
      mapper.SetCrossOriginDuplicates( "t123", "orders", new Dictionary<string, IReadOnlySet<string>> { ["ID"] = new HashSet<string> { "5" } } );
      var columns = new[] { "id", "name" };

      Document a1 = mapper.Map( new SourceRecord( "orders", "a.csv", 1, columns, new string?[] { "5", "Hammer" }, "t123" ) )!;
      Document a2 = mapper.Map( new SourceRecord( "orders", "a.csv", 2, columns, new string?[] { "5", "Again" }, "t123" ) )!;
      Document b1 = mapper.Map( new SourceRecord( "orders", "b.csv", 1, columns, new string?[] { "5", "Drill" }, "t123" ) )!;
      Document byName = mapper.Map( new SourceRecord( "orders", "b.csv", 2, columns, new string?[] { "5", "Level" } ) )!;

      Assert.Equal( "t123|5@32f29f33d5", a1.DocKey );
      Assert.Equal( "t123|5@32f29f33d5#2", a2.DocKey );
      Assert.Equal( "t123|5@010326a323", b1.DocKey );
      Assert.Equal( "orders|5@010326a323", byName.DocKey );
      Assert.Equal( "5", a1.Metadata["key"] );
   }

   /// <summary>
   /// Gap 4: Qdrant searches walked the HNSW graph with the server's default beam, which on the
   /// 760,479-point collection agreed with exact search on the top hit only 93 to 96% of the
   /// time. Every search must now ask for an exact search.
   /// </summary>
   [Fact]
   public async Task QdrantSearch_IsExact()
   {
      var calls = new CapturingCallInvoker();
      var sink = new QdrantSink( new QdrantClient( new QdrantGrpcClient( calls ) ) );

      await sink.SearchAsync( PIPELINE, new float[] { 0.1f, 0.2f }, 5, CancellationToken.None );

      SearchPoints search = Assert.Single( calls.Requests.OfType<SearchPoints>() );
      Assert.Equal( "gvb_" + PIPELINE, search.CollectionName );
      Assert.True( search.Params?.Exact ?? false, "the search did not ask Qdrant for an exact search" );
   }

   /// <summary>
   /// Gap 2, Qdrant side: the count asks for an exact count of the pipeline's collection.
   /// </summary>
   [Fact]
   public async Task QdrantCount_IsExact()
   {
      var calls = new CapturingCallInvoker { Count = 42 };
      ISink sink = new QdrantSink( new QdrantClient( new QdrantGrpcClient( calls ) ) );

      long count = await sink.CountAsync( PIPELINE, CancellationToken.None );

      CountPoints request = Assert.Single( calls.Requests.OfType<CountPoints>() );
      Assert.Equal( 42, count );
      Assert.Equal( "gvb_" + PIPELINE, request.CollectionName );
      Assert.True( request.Exact );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Scans a folder and runs the pipeline keyed on "id", optionally telling the mapper which
   /// key values every table shares across files.
   /// </summary>
   /// <param name="root">Folder to scan.</param>
   /// <param name="shared">Shared key values applied to every table, or null for none.</param>
   /// <param name="sinks">Sinks to write to; both memory sinks when null.</param>
   /// <returns>The final snapshot.</returns>
   private async Task<RunSnapshot> RunAsync( string root, IReadOnlyDictionary<string, IReadOnlySet<string>>? shared = null, IReadOnlyList<ISink>? sinks = null )
   {
      FolderScanResult scan = new FolderScanner().Scan( root, CancellationToken.None );
      var mapper = new RowDocumentMapper( scan.Tables.ToDictionary( t => t.Name, _ => new TableMapping( "id", null ) ) );
      foreach( ScannedTable table in scan.Tables.Where( _ => shared != null ) )
      {
         mapper.SetCrossOriginDuplicates( FolderSource.TableId( table ), table.Name, shared! );
      }

      var runner = new PipelineRunner( _embedder, sinks ?? new ISink[] { _a, _b }, _state, new TextChunker() );
      var progress = new RunProgress( "test", PIPELINE );
      await runner.RunAsync( PIPELINE, new FolderSource( scan ), mapper, progress, CancellationToken.None );
      return progress.Snapshot();
   }

   /// <summary>
   /// Builds "id,name" CSV text with rows "n,{prefix} n".
   /// </summary>
   /// <param name="first">First id.</param>
   /// <param name="count">Number of rows.</param>
   /// <param name="prefix">Text before each row number.</param>
   /// <returns>The CSV text.</returns>
   private static string Rows( int first, int count, string prefix )
   {
      return "id,name\n" + string.Concat( Enumerable.Range( first, count ).Select( i => $"{i},{prefix} {i}\n" ) );
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Deletes the temp folders.
   /// </summary>
   public void Dispose()
   {
      _data.Dispose();
      _other.Dispose();
      _stateDir.Dispose();
   }

   #endregion IDisposable
}

/// <summary>
/// A sink that forwards to a memory sink but does not implement the chunk count, like a sink
/// written before the count existed. Proves the pipeline skips the check for it.
/// </summary>
internal sealed class UncountableSink : ISink
{
   #region Data Members

   private readonly MemorySink _inner;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Wraps a memory sink.
   /// </summary>
   /// <param name="inner">The sink that stores the records.</param>
   public UncountableSink( MemorySink inner )
   {
      _inner = inner;
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => _inner.Name;

   /// <inheritdoc />
   public Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct ) => _inner.EnsureCollectionAsync( collection, dimension, ct );

   /// <inheritdoc />
   public Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct ) => _inner.UpsertAsync( collection, records, ct );

   /// <inheritdoc />
   public Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct ) => _inner.DeleteAsync( collection, chunkIds, ct );

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct ) => _inner.SearchAsync( collection, vector, top, ct );

   /// <inheritdoc />
   public Task DropCollectionAsync( string collection, CancellationToken ct ) => _inner.DropCollectionAsync( collection, ct );

   #endregion Public Methods
}

/// <summary>
/// A gRPC call invoker that records every request and answers with an empty response, so the
/// Qdrant sink's requests can be checked without a Qdrant server.
/// </summary>
internal sealed class CapturingCallInvoker : CallInvoker
{
   #region Public Methods

   /// <summary>Every request sent, in order.</summary>
   public List<object> Requests { get; } = new();

   /// <summary>The count a count request answers with.</summary>
   public ulong Count { get; init; }

   /// <inheritdoc />
   public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>( Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request )
   {
      Requests.Add( request );
      TResponse response = request is CountPoints
         ? (TResponse)(object)new CountResponse { Result = new CountResult { Count = Count } }
         : Activator.CreateInstance<TResponse>();
      return new AsyncUnaryCall<TResponse>( Task.FromResult( response ), Task.FromResult( new Metadata() ), () => Status.DefaultSuccess, () => new Metadata(), () => { } );
   }

   /// <inheritdoc />
   public override TResponse BlockingUnaryCall<TRequest, TResponse>( Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request )
   {
      throw new NotSupportedException( "Only asynchronous unary calls are expected." );
   }

   /// <inheritdoc />
   public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>( Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request )
   {
      throw new NotSupportedException( "Only asynchronous unary calls are expected." );
   }

   /// <inheritdoc />
   public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>( Method<TRequest, TResponse> method, string? host, CallOptions options )
   {
      throw new NotSupportedException( "Only asynchronous unary calls are expected." );
   }

   /// <inheritdoc />
   public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>( Method<TRequest, TResponse> method, string? host, CallOptions options )
   {
      throw new NotSupportedException( "Only asynchronous unary calls are expected." );
   }

   #endregion Public Methods
}
