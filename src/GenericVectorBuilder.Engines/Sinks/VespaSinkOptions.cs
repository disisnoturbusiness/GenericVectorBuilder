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
public sealed record VespaSinkOptions( string QueryUrl = "http://127.0.0.1:8090", string ConfigUrl = "http://127.0.0.1:19071", int EfSearch = 100,
   int FeedConcurrency = 64, TimeSpan? Timeout = null )
{
   /// <summary>
   /// The time limit to use: the given one, or five minutes.
   /// </summary>
   public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromMinutes( 5 );
}
