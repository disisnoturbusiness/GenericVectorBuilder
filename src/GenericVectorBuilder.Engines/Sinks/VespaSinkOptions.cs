namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Settings for <see cref="VespaSink"/>. The defaults point at the local Docker container started
/// by deploy/engines/vespa.compose.yaml.
/// </summary>
/// <param name="QueryUrl">Vespa's query and feed port (8080 inside the container, 8090 on the host).</param>
/// <param name="ConfigUrl">The config server port, where the application package is deployed.</param>
/// <param name="EfSearch">Size of the HNSW search beam (ef): how many candidates the graph walk keeps while
/// looking for the nearest neighbours. Vespa has no such setting by name: it explores targetHits plus
/// hnsw.exploreAdditionalHits, so the sink sets exploreAdditionalHits to EfSearch minus the hits wanted.
/// With no extra hits the beam would be only as wide as the result list, which is too narrow
/// for 1024 dimension data.</param>
/// <param name="FeedConcurrency">How many documents are in flight at once while writing. Vespa takes one
/// document per request, so speed comes from many requests sharing a few HTTP/2 connections.</param>
/// <param name="Timeout">Time limit for one HTTP call.</param>
/// <param name="FinishTimeout">Time limit for the whole index step after a load (waiting until every fed
/// document is searchable and the HNSW index returns all of them). Why a limit of its own: it must end with
/// a plain failure message instead of waiting forever.</param>
/// <param name="Progress">Receives a line about every ten seconds while the index step runs; when null
/// the lines go to standard error. Why: a silent wait of minutes looks like a hang.</param>
public sealed record VespaSinkOptions( string QueryUrl = "http://127.0.0.1:8090", string ConfigUrl = "http://127.0.0.1:19071", int EfSearch = 100,
   int FeedConcurrency = 64, TimeSpan? Timeout = null, TimeSpan? FinishTimeout = null, Action<string>? Progress = null )
{
   /// <summary>
   /// The time limit to use: the given one, or five minutes.
   /// </summary>
   public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromMinutes( 5 );

   /// <summary>
   /// The index step time limit to use: the given one, or ten minutes.
   /// </summary>
   public TimeSpan EffectiveFinishTimeout => FinishTimeout ?? TimeSpan.FromMinutes( 10 );
}
