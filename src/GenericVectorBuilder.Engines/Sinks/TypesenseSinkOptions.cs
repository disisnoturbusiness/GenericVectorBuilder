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
public sealed record TypesenseSinkOptions( string BaseUrl = "http://127.0.0.1:8108", int Ef = 100, string? ApiKey = null,
   string SecretsFile = "/home/dan/gvb-data/engines/secrets.env", TimeSpan? Timeout = null )
{
   /// <summary>
   /// The time limit to use: the given one, or five minutes.
   /// </summary>
   public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromMinutes( 5 );
}
