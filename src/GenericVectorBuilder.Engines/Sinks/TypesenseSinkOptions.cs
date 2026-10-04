namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Settings for <see cref="TypesenseSink"/>. The defaults point at the local Docker container
/// started by deploy/engines/typesense.compose.yaml.
/// Why the API key is not a constructor default: it is a password, so it is read from the shared
/// secrets file only when the first request is made, and never appears in code, logs or errors.
/// </summary>
/// <param name="BaseUrl">Root URL of the node, e.g. http://127.0.0.1:8108.</param>
/// <param name="Ef">Size of the HNSW search beam (ef): how many candidates the graph walk keeps while
/// looking for the nearest neighbours. A larger value finds more of the true neighbours and costs more
/// time. The server default is only 10, which finds about a third of the true top 10 on random 1024
/// dimension data, so the sink sets its own.</param>
/// <param name="ApiKey">The API key, or null to read TYPESENSE_API_KEY from <paramref name="SecretsFile"/>.</param>
/// <param name="SecretsFile">KEY=VALUE file shared by every engine container.</param>
/// <param name="Timeout">Time limit for one HTTP call, including a large import.</param>
/// <param name="FinishTimeout">Time limit for the whole index step after a load (waiting for the write queue
/// to empty and the graph to hold every vector). Why a limit of its own: it must end with a plain failure
/// message instead of waiting forever.</param>
/// <param name="Progress">Receives a line about every ten seconds while the index step runs; when null
/// the lines go to standard error. Why: a silent wait of minutes looks like a hang.</param>
public sealed record TypesenseSinkOptions( string BaseUrl = "http://127.0.0.1:8108", int Ef = 100, string? ApiKey = null,
   string SecretsFile = "/home/dan/gvb-data/engines/secrets.env", TimeSpan? Timeout = null, TimeSpan? FinishTimeout = null, Action<string>? Progress = null )
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
