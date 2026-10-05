using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Sinks;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// How to build each compose-hosted engine's sink so it connects to the container's own
/// address: which containers to ask Docker about, and a builder that starts from the sink's
/// default options (the ones that name 127.0.0.1 and a published host port) and replaces only
/// the address.
/// Why the options are the sink's own defaults and not copies of them: every other setting (index
/// parameters, search effort, passwords read from the secrets file) stays exactly what the sink
/// would use by itself, so switching the route cannot change what is measured.
/// Why a table and not reflection: an engine added later with no entry here is refused by
/// <see cref="TargetFactory"/> with a plain message instead of silently running through
/// docker-proxy, and a test checks every compose-hosted engine has an entry.
/// </summary>
public static class ContainerRoutes
{
   #region Data Members

   private static readonly Dictionary<string, EngineRoute> ROUTES = new( StringComparer.OrdinalIgnoreCase )
   {
      ["chroma"] = new( new[] { "gvb-chroma" }, Chroma ),
      ["clickhouse"] = new( new[] { "gvb-clickhouse" }, ClickHouse ),
      ["elasticsearch"] = new( new[] { "gvb-elasticsearch" }, Elasticsearch ),
      ["mariadb"] = new( new[] { "gvb-mariadb" }, MariaDb ),
      ["milvus"] = new( new[] { "gvb-milvus" }, Milvus ),
      ["mongodb"] = new( new[] { "gvb-mongodb" }, MongoDb ),
      ["opensearch"] = new( new[] { "gvb-opensearch" }, OpenSearch ),
      ["oracle"] = new( new[] { "gvb-oracle" }, Oracle ),
      ["pgvector"] = new( new[] { "gvb-pgvector" }, PgVector ),
      ["redis"] = new( new[] { "gvb-redis" }, Redis ),
      ["typesense"] = new( new[] { "gvb-typesense" }, Typesense ),
      ["vespa"] = new( new[] { "gvb-vespa" }, Vespa ),
      ["weaviate"] = new( new[] { "gvb-weaviate" }, Weaviate ),
   };

   #endregion Data Members

   #region Public Methods

   /// <summary>Names of every sink that has a route.</summary>
   public static IReadOnlyCollection<string> Names => ROUTES.Keys;

   /// <summary>
   /// Finds the route of a sink.
   /// </summary>
   /// <param name="sinkName">The sink's <see cref="ISink.Name"/>.</param>
   /// <returns>The route, or null when the sink has none.</returns>
   public static EngineRoute? Find( string sinkName )
   {
      return ROUTES.TryGetValue( sinkName, out EngineRoute? route ) ? route : null;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>Chroma on its container address.</summary>
   /// <param name="router">Addresses read from Docker.</param>
   /// <returns>The sink.</returns>
   private static ISink Chroma( ContainerRouter router )
   {
      var options = new ChromaSinkOptions();
      return new ChromaSink( options with { BaseUrl = router.Url( "gvb-chroma", options.BaseUrl ) } );
   }

   /// <summary>ClickHouse on its container address.</summary>
   /// <param name="router">Addresses read from Docker.</param>
   /// <returns>The sink.</returns>
   private static ISink ClickHouse( ContainerRouter router )
   {
      ClickHouseSinkOptions options = ClickHouseSinkOptions.LocalDefaults();
      ContainerAddress address = router.Address( "gvb-clickhouse", options.Port );
      options.Host = address.Ip;
      options.Port = address.ContainerPort;
      return new ClickHouseSink( options );
   }

   /// <summary>Elasticsearch on its container address.</summary>
   /// <param name="router">Addresses read from Docker.</param>
   /// <returns>The sink.</returns>
   private static ISink Elasticsearch( ContainerRouter router )
   {
      var options = new ElasticsearchSinkOptions();
      return new ElasticsearchSink( options with { BaseUrl = router.Url( "gvb-elasticsearch", options.BaseUrl ) } );
   }

   /// <summary>MariaDB on its container address.</summary>
   /// <param name="router">Addresses read from Docker.</param>
   /// <returns>The sink.</returns>
   private static ISink MariaDb( ContainerRouter router )
   {
      MariaDbSinkOptions options = MariaDbSinkOptions.LocalDefaults();
      ContainerAddress address = router.Address( "gvb-mariadb", options.Port );
      options.Host = address.Ip;
      options.Port = address.ContainerPort;
      return new MariaDbSink( options );
   }

   /// <summary>Milvus on its container address, both its API port and its management port.</summary>
   /// <param name="router">Addresses read from Docker.</param>
   /// <returns>The sink.</returns>
   private static ISink Milvus( ContainerRouter router )
   {
      var options = new MilvusSinkOptions();
      return new MilvusSink( options with { BaseUrl = router.Url( "gvb-milvus", options.BaseUrl ), ManagementUrl = router.Url( "gvb-milvus", options.ManagementUrl ) } );
   }

   /// <summary>MongoDB on its container address.</summary>
   /// <param name="router">Addresses read from Docker.</param>
   /// <returns>The sink.</returns>
   private static ISink MongoDb( ContainerRouter router )
   {
      MongoDbSinkOptions options = MongoDbSinkOptions.LocalDefaults();
      ContainerAddress address = router.Address( "gvb-mongodb", options.Port );
      options.Host = address.Ip;
      options.Port = address.ContainerPort;
      return new MongoDbSink( options );
   }

   /// <summary>OpenSearch on its container address (host port 9201 is container port 9200).</summary>
   /// <param name="router">Addresses read from Docker.</param>
   /// <returns>The sink.</returns>
   private static ISink OpenSearch( ContainerRouter router )
   {
      var options = new OpenSearchSinkOptions();
      return new OpenSearchSink( options with { BaseUrl = router.Url( "gvb-opensearch", options.BaseUrl ) } );
   }

   /// <summary>Oracle on its container address.</summary>
   /// <param name="router">Addresses read from Docker.</param>
   /// <returns>The sink.</returns>
   private static ISink Oracle( ContainerRouter router )
   {
      OracleSinkOptions options = OracleSinkOptions.LocalDefaults();
      ContainerAddress address = router.Address( "gvb-oracle", options.Port );
      options.Host = address.Ip;
      options.Port = address.ContainerPort;
      return new OracleSink( options );
   }

   /// <summary>pgvector on its container address.</summary>
   /// <param name="router">Addresses read from Docker.</param>
   /// <returns>The sink.</returns>
   private static ISink PgVector( ContainerRouter router )
   {
      PgVectorSinkOptions options = PgVectorSinkOptions.LocalDefaults();
      ContainerAddress address = router.Address( "gvb-pgvector", options.Port );
      options.Host = address.Ip;
      options.Port = address.ContainerPort;
      return new PgVectorSink( options );
   }

   /// <summary>Redis on its container address.</summary>
   /// <param name="router">Addresses read from Docker.</param>
   /// <returns>The sink.</returns>
   private static ISink Redis( ContainerRouter router )
   {
      RedisSinkOptions options = RedisSinkOptions.LocalDefaults();
      ContainerAddress address = router.Address( "gvb-redis", options.Port );
      options.Host = address.Ip;
      options.Port = address.ContainerPort;
      return new RedisSink( options );
   }

   /// <summary>Typesense on its container address.</summary>
   /// <param name="router">Addresses read from Docker.</param>
   /// <returns>The sink.</returns>
   private static ISink Typesense( ContainerRouter router )
   {
      var options = new TypesenseSinkOptions();
      return new TypesenseSink( options with { BaseUrl = router.Url( "gvb-typesense", options.BaseUrl ) } );
   }

   /// <summary>Vespa on its container address, both its query port (host 8090 is container 8080) and its config port.</summary>
   /// <param name="router">Addresses read from Docker.</param>
   /// <returns>The sink.</returns>
   private static ISink Vespa( ContainerRouter router )
   {
      var options = new VespaSinkOptions();
      return new VespaSink( options with { QueryUrl = router.Url( "gvb-vespa", options.QueryUrl ), ConfigUrl = router.Url( "gvb-vespa", options.ConfigUrl ) } );
   }

   /// <summary>Weaviate on its container address (host port 8085 is container port 8080).</summary>
   /// <param name="router">Addresses read from Docker.</param>
   /// <returns>The sink.</returns>
   private static ISink Weaviate( ContainerRouter router )
   {
      var options = new WeaviateSinkOptions();
      return new WeaviateSink( options with { BaseUrl = router.Url( "gvb-weaviate", options.BaseUrl ) } );
   }

   #endregion Private Methods
}

/// <summary>
/// One engine's way onto its container: the containers to read and the builder of the sink.
/// </summary>
/// <param name="Containers">Names of the containers the sink talks to (from the compose file's container_name).</param>
/// <param name="Build">Builds the sink from the addresses read for those containers.</param>
public sealed record EngineRoute( IReadOnlyList<string> Containers, Func<ContainerRouter, ISink> Build )
{
   #region Public Methods

   /// <summary>
   /// Builds the sink asynchronously; used instead of <see cref="Build"/> when set. Why: the
   /// benchmark's own SQL Server and Qdrant containers are asked for their build, CPUs and
   /// durability settings the moment the target first reaches them, which needs a round trip.
   /// </summary>
   public Func<ContainerRouter, CancellationToken, Task<ISink>>? BuildAsync { get; init; }

   /// <summary>
   /// A route whose sink is built asynchronously.
   /// </summary>
   /// <param name="containers">Container names.</param>
   /// <param name="build">The asynchronous builder.</param>
   /// <returns>The route; its synchronous <see cref="Build"/> refuses with a plain message.</returns>
   public static EngineRoute Async( IReadOnlyList<string> containers, Func<ContainerRouter, CancellationToken, Task<ISink>> build )
   {
      return new EngineRoute( containers, _ => throw new InvalidOperationException( $"The sink for {string.Join( ", ", containers )} is built asynchronously; use BuildAsync." ) ) { BuildAsync = build };
   }

   #endregion Public Methods
}
