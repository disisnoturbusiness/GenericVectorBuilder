namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// Where the benchmark's "mariadb" target runs: its own container gvbbench-mariadb, started from
/// deploy/engines/mariadb-bench.compose.yaml, and never gvb-mariadb, the daily container of
/// mariadb.compose.yaml.
/// Why a container of its own: gvb-mariadb is long-running and sized for all 8 CPUs, so measuring it
/// meant moving it onto the engine CPUs with "docker update" after it had started (sized for 8 CPUs,
/// then running on 4) and putting it back afterwards, which also loaded and changed a long-running
/// server. A benchmark container is created on the engine CPUs like every other engine,
/// started by run-all at the target's turn and removed after it; the daily container is never named,
/// asked about, connected to or changed by a run.
/// Why the names live in one place: the compose file, the container route, the target's compose path
/// and its data folder must all say the same thing, and the tests compare each of them with these.
/// </summary>
public static class MariaBench
{
   #region Data Members

   /// <summary>Name of the target in --targets and in the results; the sink's own name, so reports and consolidations keep working.</summary>
   public const string TARGET = "mariadb";

   /// <summary>The benchmark's own container (container_name in the compose file).</summary>
   public const string CONTAINER = "gvbbench-mariadb";

   /// <summary>Compose file under deploy/engines that starts <see cref="CONTAINER"/>.</summary>
   public const string COMPOSE_FILE = "mariadb-bench.compose.yaml";

   /// <summary>Folder under the engine data root that holds the container's data (the volume in the compose file).</summary>
   public const string DATA_FOLDER = "mariadb-bench";

   /// <summary>
   /// The host port the compose file publishes (127.0.0.1 only). The daily container publishes 3306, which
   /// is also the sink's default, so the route asks the container's port table for this one instead.
   /// The benchmark itself connects to the container's own address and port 3306, never to this port.
   /// </summary>
   public const int HOST_PORT = 13306;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// True for the sink whose target runs in <see cref="CONTAINER"/>.
   /// </summary>
   /// <param name="sinkName">The sink's <see cref="GenericVectorBuilder.Core.Contracts.ISink.Name"/>.</param>
   /// <returns>True for MariaDB.</returns>
   public static bool Owns( string sinkName )
   {
      return string.Equals( sinkName, TARGET, StringComparison.OrdinalIgnoreCase );
   }

   #endregion Public Methods
}
