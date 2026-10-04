namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Settings for <see cref="ElasticsearchSink"/>. The defaults point at the local Docker container
/// started by deploy/engines/elasticsearch.compose.yaml.
/// Why a record with defaults: the benchmark builds the same sink with different query settings
/// (more candidates for higher recall) without any other code changing.
/// </summary>
/// <param name="BaseUrl">Root URL of the node, e.g. http://127.0.0.1:9200.</param>
/// <param name="NumCandidates">How many nearest candidates each shard collects before the best
/// <c>k</c> are returned. It is Elasticsearch's name for the HNSW search beam (ef_search): a larger value
/// finds more of the true neighbours and costs more time. The server default is only 1.5 times k,
/// about 15 for k = 10, which is too narrow for 1024-dimension data, so the sink sets its own.</param>
/// <param name="Timeout">Time limit for one HTTP call, including a large bulk write.</param>
public sealed record ElasticsearchSinkOptions( string BaseUrl = "http://127.0.0.1:9200", int NumCandidates = 100, TimeSpan? Timeout = null )
{
   /// <summary>
   /// The time limit to use: the given one, or five minutes.
   /// </summary>
   public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromMinutes( 5 );
}
