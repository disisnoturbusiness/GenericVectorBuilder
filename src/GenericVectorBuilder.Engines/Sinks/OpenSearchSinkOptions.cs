namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Settings for <see cref="OpenSearchSink"/>. The defaults point at the local Docker container
/// started by deploy/engines/opensearch.compose.yaml.
/// Why a record with defaults: the benchmark builds the same sink with different query settings
/// (more candidates for higher recall) without any other code changing.
/// </summary>
/// <param name="BaseUrl">Root URL of the node, e.g. http://127.0.0.1:9201.</param>
/// <param name="EfSearch">Size of the HNSW search beam (ef_search): how many candidates the graph walk keeps
/// while looking for the nearest neighbours. A larger value finds more of the true neighbours and costs
/// more time. 100 is the OpenSearch default; it is passed with every query so the report can state it.</param>
/// <param name="Timeout">Time limit for one HTTP call, including a large bulk write.</param>
public sealed record OpenSearchSinkOptions( string BaseUrl = "http://127.0.0.1:9201", int EfSearch = 100, TimeSpan? Timeout = null )
{
   /// <summary>
   /// The time limit to use: the given one, or five minutes.
   /// </summary>
   public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromMinutes( 5 );
}
