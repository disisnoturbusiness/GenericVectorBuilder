using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Core.Embedding;
using GenericVectorBuilder.Core.Sinks;
using GenericVectorBuilder.Core.State;
using Qdrant.Client;

namespace GenericVectorBuilder.Core.Configuration;

/// <summary>
/// Builds and owns the long-lived pieces (HTTP client, Qdrant client, sinks, state store) from
/// settings, so the web app and the live tests wire things up the same way.
/// Also answers search requests, since search needs the embedder a pipeline was built with.
/// Besides SQL Server and Qdrant it can hold optional destinations (the engine containers),
/// which are usually stopped. Why they are treated differently on reset: a reset that tried
/// every stopped engine would never succeed, so an optional destination is dropped only when
/// state says it holds the pipeline.
/// </summary>
public sealed class GvbServices : IDisposable
{
   #region Data Members

   private static readonly TimeSpan EMBED_TIMEOUT = TimeSpan.FromMinutes( 10 );
   private static readonly TimeSpan SEARCH_TIMEOUT = TimeSpan.FromMinutes( 2 );

   private readonly HttpClient _embedHttp;
   private readonly QdrantClient? _qdrant;
   private readonly Dictionary<string, ISink> _sinks;
   private readonly HashSet<string> _optional = new( StringComparer.OrdinalIgnoreCase );
   private readonly List<IDisposable> _owned = new();

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the services from settings, with SQL Server and Qdrant only.
   /// </summary>
   /// <param name="settings">Settings.</param>
   public GvbServices( GvbSettings settings ) : this( settings, Array.Empty<ISink>() )
   {
   }

   /// <summary>
   /// Creates the services from settings, with SQL Server, Qdrant and the given optional
   /// destinations (the web app passes every engine sink). The services dispose them.
   /// </summary>
   /// <param name="settings">Settings.</param>
   /// <param name="optionalSinks">Extra destinations, each with a name unlike every other.</param>
   /// <exception cref="ArgumentException">Two destinations share a name.</exception>
   public GvbServices( GvbSettings settings, IEnumerable<ISink> optionalSinks )
   {
      Settings = settings;
      _embedHttp = new HttpClient { BaseAddress = new Uri( settings.EmbedUrl.TrimEnd( '/' ) + "/" ), Timeout = EMBED_TIMEOUT };
      _qdrant = new QdrantClient( settings.QdrantHost, settings.QdrantGrpcPort );
      var sql = new SqlVectorSink( settings.BuildSqlConnectionString(), settings.SqlDatabase );
      var qdrant = new QdrantSink( _qdrant );
      _sinks = new Dictionary<string, ISink>( StringComparer.OrdinalIgnoreCase ) { [sql.Name] = sql, [qdrant.Name] = qdrant };
      State = new SqliteStateStore( GvbSettings.Expand( settings.StatePath ) );
      AddOptional( optionalSinks );
   }

   /// <summary>
   /// Creates the services from ready-made pieces. Tests use this to swap in fake sinks, a
   /// stub embedding service and an in-memory state store without touching SQL Server or
   /// Qdrant. There is no Qdrant client in this form, so the health check reports it as not
   /// configured.
   /// </summary>
   /// <param name="settings">Settings.</param>
   /// <param name="embedHttp">HTTP client for the embedding service; disposed with the services.</param>
   /// <param name="sinks">Sinks, found by their <see cref="ISink.Name"/>.</param>
   /// <param name="state">State store.</param>
   /// <param name="optionalSinks">Optional destinations, treated like the engine sinks; none when null.</param>
   internal GvbServices( GvbSettings settings, HttpClient embedHttp, IEnumerable<ISink> sinks, IStateStore state, IEnumerable<ISink>? optionalSinks = null )
   {
      Settings = settings;
      _embedHttp = embedHttp;
      _sinks = sinks.ToDictionary( s => s.Name, s => s, StringComparer.OrdinalIgnoreCase );
      State = state;
      AddOptional( optionalSinks ?? Array.Empty<ISink>() );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Settings the services were built from.</summary>
   public GvbSettings Settings { get; }

   /// <summary>The state store.</summary>
   public IStateStore State { get; }

   /// <summary>Names of the available sinks.</summary>
   public IReadOnlyCollection<string> SinkNames => _sinks.Keys;

   /// <summary>
   /// True for an optional destination (an engine sink), false for SQL Server, Qdrant and any
   /// unknown name.
   /// </summary>
   /// <param name="name">Sink name.</param>
   /// <returns>True when optional.</returns>
   public bool IsOptional( string name )
   {
      return _optional.Contains( name );
   }

   /// <summary>
   /// Creates an embedder for a model key.
   /// </summary>
   /// <param name="modelKey">Model key, or null for the default.</param>
   /// <returns>The embedder.</returns>
   public IEmbedder CreateEmbedder( string? modelKey )
   {
      return new GpuServiceEmbedder( _embedHttp, EmbedderProfile.Get( modelKey ?? Settings.DefaultEmbedModel ) );
   }

   /// <summary>
   /// Resolves sink names to sinks.
   /// </summary>
   /// <param name="names">Sink names, e.g. "sql", "qdrant".</param>
   /// <returns>The sinks.</returns>
   /// <exception cref="ArgumentException">Unknown sink name.</exception>
   public IReadOnlyList<ISink> GetSinks( IEnumerable<string> names )
   {
      return names.Distinct( StringComparer.OrdinalIgnoreCase )
         .Select( n => _sinks.TryGetValue( n, out ISink? s ) ? s : throw new ArgumentException( $"Unknown destination '{n}'." ) )
         .ToList();
   }

   /// <summary>
   /// Searches a pipeline in each requested sink, embedding the query with the same model the
   /// pipeline was built with (read from its stored fingerprint). The whole stored fingerprint
   /// must match what this server would build with today (document suffix, query format and
   /// dimension, not just the model), because a changed convention makes the query vector
   /// land in the wrong place and retrieval degrades without any error.
   /// </summary>
   /// <param name="pipeline">Sanitized pipeline name.</param>
   /// <param name="query">The question.</param>
   /// <param name="sinkNames">Sinks to search.</param>
   /// <param name="top">Hits per sink.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits per sink name; a sink that errors returns its message instead.</returns>
   /// <exception cref="InvalidOperationException">The pipeline is unknown or was built with different embedding settings.</exception>
   /// <exception cref="ArgumentException">An unknown destination was named.</exception>
   /// <exception cref="EmbedderUnavailableException">The embedding service is down, too slow or failing.</exception>
   public async Task<Dictionary<string, object>> SearchAsync( string pipeline, string query, IEnumerable<string> sinkNames, int top, CancellationToken ct )
   {
      string stored = await State.GetFingerprintAsync( pipeline, ct )
         ?? throw new InvalidOperationException( $"Pipeline '{pipeline}' has not been built yet." );
      IReadOnlyList<ISink> sinks = GetSinks( sinkNames );
      using var limit = CancellationTokenSource.CreateLinkedTokenSource( ct );
      limit.CancelAfter( SEARCH_TIMEOUT );
      float[] vector = await EmbedQueryChecked( pipeline, stored, query, limit.Token, ct );

      var results = new Dictionary<string, object>( StringComparer.Ordinal );
      foreach( ISink sink in sinks )
      {
         try
         {
            results[sink.Name] = await sink.SearchAsync( pipeline, vector, top, limit.Token );
         }
         catch( OperationCanceledException ) when( !ct.IsCancellationRequested )
         {
            results[sink.Name] = new { error = $"{sink.Name} did not answer in time." };
         }
         catch( Exception ex ) when( ex is not OperationCanceledException )
         {
            results[sink.Name] = new { error = ex.Message };
         }
      }

      return results;
   }

   /// <summary>
   /// Drops a pipeline from every sink that may hold it and forgets its state. Built so that a
   /// failure part way through never leaves the state store claiming data that is gone (the
   /// state is forgotten FIRST, and a leftover collection is harmless because chunk ids are
   /// deterministic and the next run overwrites them). Every sink is tried even when one
   /// fails. If any sink could not be dropped, the pipeline's fingerprint is put back so it
   /// stays listed and the reset can be repeated, and <see cref="ResetIncompleteException"/>
   /// says which destinations are left.
   /// SQL Server and Qdrant are always dropped. An optional destination is dropped only when
   /// state says it holds the pipeline, so a stopped engine that never held it cannot block the
   /// reset; when its drop fails (or never ran), its state is put back so the next reset still
   /// knows to drop it.
   /// The web layer refuses a reset while the pipeline has a queued or running job.
   /// </summary>
   /// <param name="pipeline">Sanitized pipeline name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <exception cref="ResetIncompleteException">At least one destination could not be dropped.</exception>
   public async Task ResetPipelineAsync( string pipeline, CancellationToken ct )
   {
      string? fingerprint = await State.GetFingerprintAsync( pipeline, ct );
      List<( ISink Sink, IReadOnlyList<DocState> Held )> targets = await ResetTargetsAsync( pipeline, ct );
      await State.ForgetPipelineAsync( pipeline, ct );
      var failures = new List<string>();
      var dropped = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
      bool complete = false;
      try
      {
         foreach( ( ISink sink, _ ) in targets )
         {
            try
            {
               await sink.DropCollectionAsync( pipeline, ct );
               dropped.Add( sink.Name );
            }
            catch( Exception ex ) when( ex is not OperationCanceledException )
            {
               failures.Add( $"{sink.Name} ({ex.Message})" );
            }
         }

         complete = failures.Count == 0;
      }
      finally
      {
         if( !complete )
         {
            await RestoreAfterIncompleteResetAsync( pipeline, fingerprint, targets.Where( t => !dropped.Contains( t.Sink.Name ) ).ToList() );
         }
      }

      if( !complete )
      {
         throw new ResetIncompleteException( $"Could not remove '{pipeline}' from: {string.Join( "; ", failures )}. Everything else was removed. Press reset again when that destination is back." );
      }
   }

   /// <summary>
   /// Checks each dependency once, for the status line at the top of the page. Every check is
   /// independent so one dead service never hides the state of the others.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   /// <returns>"ok" or the error message per dependency.</returns>
   public async Task<Dictionary<string, string>> HealthAsync( CancellationToken ct )
   {
      var health = new Dictionary<string, string>( StringComparer.Ordinal );
      health["embedder"] = await CheckAsync( async () =>
      {
         using HttpResponseMessage response = await _embedHttp.GetAsync( "health", ct );
         response.EnsureSuccessStatusCode();
      } );
      health["sql"] = await CheckAsync( async () =>
      {
         await using var connection = new Microsoft.Data.SqlClient.SqlConnection( Settings.BuildSqlConnectionString() );
         await connection.OpenAsync( ct );
      } );
      health["qdrant"] = _qdrant == null ? "not configured" : await CheckAsync( async () => await _qdrant.ListCollectionsAsync( ct ) );
      return health;
   }

   /// <summary>
   /// Pulls the model key out of a stored fingerprint ("gpu-service|model|dims=..").
   /// </summary>
   /// <param name="fingerprint">Stored fingerprint.</param>
   /// <returns>The model key.</returns>
   public static string ModelFromFingerprint( string fingerprint )
   {
      string[] parts = fingerprint.Split( '|' );
      return parts.Length > 1 ? parts[1] : throw new InvalidOperationException( $"Unreadable fingerprint '{fingerprint}'." );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Registers optional destinations and takes ownership of the disposable ones.
   /// </summary>
   /// <param name="sinks">The optional destinations.</param>
   /// <exception cref="ArgumentException">A name is already taken.</exception>
   private void AddOptional( IEnumerable<ISink> sinks )
   {
      foreach( ISink sink in sinks )
      {
         if( !_sinks.TryAdd( sink.Name, sink ) )
         {
            throw new ArgumentException( $"Two destinations are named '{sink.Name}'. Every destination needs a name of its own." );
         }

         _optional.Add( sink.Name );
         if( sink is IDisposable owned )
         {
            _owned.Add( owned );
         }
      }
   }

   /// <summary>
   /// The sinks a reset must drop, each with the state it held for the pipeline: every
   /// non-optional sink (with no state kept, since it is always dropped), and every optional
   /// sink whose state says it holds the pipeline.
   /// </summary>
   /// <param name="pipeline">Pipeline name.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The sinks to drop, in registration order.</returns>
   private async Task<List<( ISink Sink, IReadOnlyList<DocState> Held )>> ResetTargetsAsync( string pipeline, CancellationToken ct )
   {
      var targets = new List<( ISink Sink, IReadOnlyList<DocState> Held )>();
      foreach( ISink sink in _sinks.Values )
      {
         if( !_optional.Contains( sink.Name ) )
         {
            targets.Add( ( sink, Array.Empty<DocState>() ) );
            continue;
         }

         Dictionary<string, DocState> held = await State.LoadAsync( pipeline, sink.Name, ct );
         if( held.Count > 0 )
         {
            targets.Add( ( sink, held.Values.ToList() ) );
         }
      }

      return targets;
   }

   /// <summary>
   /// After a reset that did not finish: puts the fingerprint back so the pipeline stays listed,
   /// and puts back the state of every optional sink that was not dropped, so a repeated reset
   /// still knows it holds the pipeline. Runs without the caller's cancellation, because leaving
   /// this half done is exactly what it prevents.
   /// </summary>
   /// <param name="pipeline">Pipeline name.</param>
   /// <param name="fingerprint">The fingerprint before the reset, or null.</param>
   /// <param name="notDropped">Targets whose drop failed or never ran.</param>
   private async Task RestoreAfterIncompleteResetAsync( string pipeline, string? fingerprint, IReadOnlyList<( ISink Sink, IReadOnlyList<DocState> Held )> notDropped )
   {
      if( fingerprint != null )
      {
         await State.SetFingerprintAsync( pipeline, fingerprint, CancellationToken.None );
      }

      foreach( ( ISink sink, IReadOnlyList<DocState> held ) in notDropped.Where( t => _optional.Contains( t.Sink.Name ) && t.Held.Count > 0 ) )
      {
         await State.SaveAsync( pipeline, sink.Name, held, CancellationToken.None );
      }
   }

   /// <summary>
   /// Embeds a search query after proving the embedder would build exactly what the pipeline
   /// was built with. A stalled service ends as <see cref="EmbedderUnavailableException"/>
   /// instead of hanging for the full HTTP timeout.
   /// </summary>
   /// <param name="pipeline">Pipeline name, for the error message.</param>
   /// <param name="stored">The fingerprint stored for the pipeline.</param>
   /// <param name="query">The question.</param>
   /// <param name="limited">Cancellation that also fires when the search takes too long.</param>
   /// <param name="ct">The caller's own cancellation, to tell a timeout from the user leaving.</param>
   /// <returns>The query vector.</returns>
   private async Task<float[]> EmbedQueryChecked( string pipeline, string stored, string query, CancellationToken limited, CancellationToken ct )
   {
      IEmbedder embedder = CreateEmbedder( ModelFromFingerprint( stored ) );
      try
      {
         await embedder.PreflightAsync( limited );
         if( embedder.Fingerprint != stored )
         {
            throw new InvalidOperationException( $"Pipeline '{pipeline}' was built with different embedding settings than this server uses now, so searching it would give poor results. Reset it and build it again, or build under a new name. (Built with: {stored}. Now: {embedder.Fingerprint}.)" );
         }

         return await embedder.EmbedQueryAsync( query, limited );
      }
      catch( OperationCanceledException ) when( !ct.IsCancellationRequested )
      {
         throw new EmbedderUnavailableException( $"The embedding service did not answer within {SEARCH_TIMEOUT.TotalMinutes:0} minutes." );
      }
   }

   /// <summary>
   /// Runs one health check with a short timeout.
   /// </summary>
   /// <param name="check">The check.</param>
   /// <returns>"ok", or the failure message.</returns>
   private static async Task<string> CheckAsync( Func<Task> check )
   {
      try
      {
         await check().WaitAsync( TimeSpan.FromSeconds( 10 ) );
         return "ok";
      }
      catch( Exception ex )
      {
         return ex.Message;
      }
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Disposes the HTTP and Qdrant clients and the optional destinations.
   /// </summary>
   public void Dispose()
   {
      _embedHttp.Dispose();
      _qdrant?.Dispose();
      foreach( IDisposable owned in _owned )
      {
         owned.Dispose();
      }
   }

   #endregion IDisposable
}
