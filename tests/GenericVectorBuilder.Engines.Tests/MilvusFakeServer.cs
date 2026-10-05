using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// One segment as the fake query node lists it (/api/v1/_qn/segments).
/// </summary>
/// <param name="Id">Segment id.</param>
/// <param name="State">Sealed or Growing.</param>
/// <param name="Rows">loaded_insert_row_count.</param>
/// <param name="IndexLoaded">True when the segment lists a loaded index.</param>
internal sealed record FakeQueryNodeSegment( string Id, string State, long Rows, bool IndexLoaded );

/// <summary>
/// One segment as the fake data coordinator lists it (/api/v1/_dc/segments).
/// </summary>
/// <param name="Id">Segment id.</param>
/// <param name="State">Flushed, Dropped or Growing.</param>
/// <param name="Level">L0 or L1.</param>
/// <param name="Rows">num_of_rows (0 for a level-zero segment).</param>
/// <param name="Indexed">is_indexed.</param>
/// <param name="Compacted">compacted.</param>
internal sealed record FakeDatacoordSegment( string Id, string State, string Level, long Rows, bool Indexed, bool Compacted );

/// <summary>
/// A scripted stand-in for Milvus 2.6's REST API v2 and management port, on a loopback port, so the
/// Milvus sink's waiting and judging can be tested without the container (and without waiting for
/// real compactions). It answers only what the sink asks during FinishLoadAsync and
/// GetIndexStateAsync, with the same JSON shapes as the real server (copied from the 2.6.25 answers
/// of 2026-10-04). A test scripts the passing of time through <see cref="OnManagementRead"/>, which
/// runs on every query node read, and through <see cref="OnCompact"/>.
/// Why a fake server and not a mock of the sink: the behaviour under test (what the sink asks, in
/// which order, how long it waits and what it concludes from the answers) lives in the HTTP
/// conversation.
/// </summary>
internal sealed class MilvusFakeServer : IDisposable
{
   #region Data Members

   private readonly HttpListener _listener = new();
   private readonly CancellationTokenSource _stop = new();
   private readonly Task _loop;
   private readonly object _gate = new();
   private int _managementReads;
   private int _compactCalls;
   private int _compactionStateCalls;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Starts the server on a free loopback port.
   /// </summary>
   public MilvusFakeServer()
   {
      int port = FreePort();
      Url = $"http://127.0.0.1:{port}";
      _listener.Prefixes.Add( Url + "/" );
      _listener.Start();
      _loop = Task.Run( () => ServeAsync( _stop.Token ) );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Base address, which is both the REST API and the management port.</summary>
   public string Url { get; }

   /// <summary>Rows the Strong count(*) returns.</summary>
   public long Stored { get; set; } = 524;

   /// <summary>The index description's state.</summary>
   public string IndexState { get; set; } = "Finished";

   /// <summary>What the query node lists. Change it under <see cref="Update"/>.</summary>
   public List<FakeQueryNodeSegment> QueryNode { get; } = new();

   /// <summary>What the data coordinator lists. Change it under <see cref="Update"/>.</summary>
   public List<FakeDatacoordSegment> Datacoord { get; } = new();

   /// <summary>Called on every query node read (before the answer is built), with the reads so far including this one.</summary>
   public Action<MilvusFakeServer>? OnManagementRead { get; set; }

   /// <summary>Called when the sink asks for a compaction; returns the job id (-1 for "nothing to compact").</summary>
   public Func<MilvusFakeServer, long>? OnCompact { get; set; }

   /// <summary>Returns the job state for the n-th get_compaction_state call (1 based); the default is Completed.</summary>
   public Func<int, string>? CompactionState { get; set; }

   /// <summary>How many times the query node was read.</summary>
   public int ManagementReads
   {
      get
      {
         lock( _gate )
         {
            return _managementReads;
         }
      }
   }

   /// <summary>How many times the sink asked for a compaction.</summary>
   public int CompactCalls
   {
      get
      {
         lock( _gate )
         {
            return _compactCalls;
         }
      }
   }

   /// <summary>
   /// Changes the scripted state while no request is being answered.
   /// </summary>
   /// <param name="change">The change.</param>
   public void Update( Action<MilvusFakeServer> change )
   {
      lock( _gate )
      {
         change( this );
      }
   }

   /// <summary>
   /// Stops the server.
   /// </summary>
   public void Dispose()
   {
      _stop.Cancel();
      _listener.Close();
      try
      {
         _loop.Wait( TimeSpan.FromSeconds( 5 ) );
      }
      catch( AggregateException )
      {
         // The loop ends with an exception when the listener closes; nothing failed.
      }

      _stop.Dispose();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Finds a free loopback port.
   /// </summary>
   /// <returns>The port.</returns>
   private static int FreePort()
   {
      var probe = new TcpListener( IPAddress.Loopback, 0 );
      probe.Start();
      int port = ( (IPEndPoint)probe.LocalEndpoint ).Port;
      probe.Stop();
      return port;
   }

   /// <summary>
   /// Answers requests until stopped.
   /// </summary>
   /// <param name="stop">Cancelled on dispose.</param>
   private async Task ServeAsync( CancellationToken stop )
   {
      while( !stop.IsCancellationRequested )
      {
         HttpListenerContext context;
         try
         {
            context = await _listener.GetContextAsync();
         }
         catch( Exception ex ) when( ex is HttpListenerException or ObjectDisposedException or InvalidOperationException )
         {
            return;
         }

         string body;
         lock( _gate )
         {
            body = Answer( context.Request.Url!.AbsolutePath );
         }

         byte[] bytes = Encoding.UTF8.GetBytes( body );
         context.Response.ContentType = "application/json";
         context.Response.ContentLength64 = bytes.Length;
         await context.Response.OutputStream.WriteAsync( bytes, stop );
         context.Response.Close();
      }
   }

   /// <summary>
   /// Builds the answer for one path.
   /// </summary>
   /// <param name="path">Absolute path of the request.</param>
   /// <returns>The body.</returns>
   private string Answer( string path )
   {
      switch( path )
      {
         case "/v2/vectordb/entities/query":
            return Ok( new[] { new Dictionary<string, object> { ["count(*)"] = Stored } } );
         case "/v2/vectordb/indexes/describe":
            return Ok( new[] { IndexDescription() } );
         case "/v2/vectordb/collections/get_load_state":
            return Ok( new { loadProgress = 100, loadState = "LoadStateLoaded" } );
         case "/v2/vectordb/collections/describe":
            return Ok( new { collectionID = 4242 } );
         case "/v2/vectordb/collections/flush":
            return Ok( new { } );
         case "/v2/vectordb/collections/compact":
            _compactCalls++;
            return Ok( new { compactionID = OnCompact?.Invoke( this ) ?? -1 } );
         case "/v2/vectordb/collections/get_compaction_state":
            _compactionStateCalls++;
            return Ok( new { compactionID = 7, state = CompactionState?.Invoke( _compactionStateCalls ) ?? "Completed", executingPlanNumber = 0, completedPlanNumber = 1, timeoutPlanNumber = 0 } );
         case "/api/v1/_qn/segments":
            _managementReads++;
            OnManagementRead?.Invoke( this );
            return QueryNodeJson();
         case "/api/v1/_dc/segments":
            return DatacoordJson();
         case "/metrics":
            return string.Empty;
         default:
            return JsonSerializer.Serialize( new { code = 404, message = $"the fake Milvus does not know {path}" } );
      }
   }

   /// <summary>
   /// The index description, with the rows computed from what the data coordinator lists.
   /// </summary>
   /// <returns>One index entry.</returns>
   private Dictionary<string, object> IndexDescription()
   {
      long total = Datacoord.Where( s => s.State == "Flushed" && s.Level == "L1" ).Sum( s => s.Rows );
      long indexed = Datacoord.Where( s => s.State == "Flushed" && s.Level == "L1" && s.Indexed ).Sum( s => s.Rows );
      return new Dictionary<string, object>
      {
         ["indexName"] = "vector_hnsw",
         ["indexState"] = IndexState,
         ["indexType"] = "HNSW",
         ["metricType"] = "COSINE",
         ["totalRows"] = total,
         ["indexedRows"] = indexed,
         ["pendingRows"] = total - indexed,
      };
   }

   /// <summary>
   /// The query node's segment list.
   /// </summary>
   /// <returns>A JSON array.</returns>
   private string QueryNodeJson()
   {
      var list = new List<Dictionary<string, object>>();
      foreach( FakeQueryNodeSegment s in QueryNode )
      {
         var item = new Dictionary<string, object> { ["segment_id"] = s.Id, ["state"] = s.State, ["loaded_insert_row_count"] = s.Rows.ToString() };
         if( s.IndexLoaded )
         {
            item["index_fields"] = new[] { new Dictionary<string, object> { ["field_id"] = "101", ["build_id"] = "build-" + s.Id, ["is_loaded"] = "true" } };
         }

         list.Add( item );
      }

      return JsonSerializer.Serialize( list );
   }

   /// <summary>
   /// The data coordinator's segment list.
   /// </summary>
   /// <returns>A JSON array.</returns>
   private string DatacoordJson()
   {
      var list = new List<Dictionary<string, object>>();
      foreach( FakeDatacoordSegment s in Datacoord )
      {
         var item = new Dictionary<string, object> { ["segment_id"] = s.Id, ["state"] = s.State, ["level"] = s.Level };
         if( s.Rows > 0 )
         {
            item["num_of_rows"] = s.Rows.ToString();
         }

         if( s.Indexed )
         {
            item["is_indexed"] = true;
         }

         if( s.Compacted )
         {
            item["compacted"] = true;
         }

         list.Add( item );
      }

      return list.Count == 0 ? "null" : JsonSerializer.Serialize( list );
   }

   /// <summary>
   /// Wraps a payload in Milvus's answer envelope.
   /// </summary>
   /// <param name="data">The payload.</param>
   /// <returns>The body.</returns>
   private static string Ok( object data )
   {
      return JsonSerializer.Serialize( new { code = 0, data } );
   }

   #endregion Private Methods
}
