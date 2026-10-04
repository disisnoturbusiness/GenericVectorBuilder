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
/// <param name="FinishTimeout">Time limit for the whole index step after a load (refresh, force merge,
/// waiting for the HNSW graph). Why a limit of its own: a merge of a big collection takes minutes,
/// far longer than one HTTP call, but it must still end with a plain failure message instead of waiting forever.</param>
/// <param name="Progress">Receives a line about every ten seconds while the index step runs; when null
/// the lines go to standard error. Why: a silent wait of minutes looks like a hang.</param>
public sealed record ElasticsearchSinkOptions( string BaseUrl = "http://127.0.0.1:9200", int NumCandidates = 100, TimeSpan? Timeout = null,
   TimeSpan? FinishTimeout = null, Action<string>? Progress = null )
{
   /// <summary>
   /// The time limit to use: the given one, or five minutes.
   /// </summary>
   public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromMinutes( 5 );

   /// <summary>
   /// The index step time limit to use: the given one, or thirty minutes.
   /// </summary>
   public TimeSpan EffectiveFinishTimeout => FinishTimeout ?? TimeSpan.FromMinutes( 30 );
}
