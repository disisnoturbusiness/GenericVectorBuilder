using System.Net.Sockets;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Sinks;

namespace GenericVectorBuilder.Web.Destinations;

/// <summary>
/// How to tell, without starting anything, whether an engine destination is running.
/// Why a plain TCP connect: the engines are Docker containers that are normally stopped, and a
/// stopped container's port refuses at once, so a connect within a short limit answers "running
/// or not" without logging in, sending a query or waking anything up. An engine that runs inside
/// this app (DuckDB, sqlite-vec) has nothing to connect to; it counts as running when the folder
/// it keeps its files in can be made.
/// Why the addresses are taken from each engine's default options: the engine catalog creates
/// every sink with its parameterless constructor, which uses exactly those defaults.
/// Why an engine with no probe here is never offered: a destination whose state cannot be
/// checked would let a run start against something that may not be there. A test fails when an
/// engine is added without a probe.
/// </summary>
public static class EngineProbes
{
   #region Public Methods

   /// <summary>
   /// The probe for an engine sink, or null when none is known for its type.
   /// </summary>
   /// <param name="sink">An engine sink from the engine catalog.</param>
   /// <returns>A check that throws with a plain reason when the engine is not running, or null.</returns>
   public static Func<CancellationToken, Task>? For( ISink sink )
   {
      return sink switch
      {
         ChromaSink => Url( new ChromaSinkOptions().BaseUrl ),
         ElasticsearchSink => Url( new ElasticsearchSinkOptions().BaseUrl ),
         MilvusSink => Url( new MilvusSinkOptions().BaseUrl ),
         OpenSearchSink => Url( new OpenSearchSinkOptions().BaseUrl ),
         TypesenseSink => Url( new TypesenseSinkOptions().BaseUrl ),
         VespaSink => Url( new VespaSinkOptions().QueryUrl ),
         WeaviateSink => Url( new WeaviateSinkOptions().BaseUrl ),
         ClickHouseSink => Tcp( new ClickHouseSinkOptions().Host, new ClickHouseSinkOptions().Port ),
         MariaDbSink => Tcp( new MariaDbSinkOptions().Host, new MariaDbSinkOptions().Port ),
         MongoDbSink => Tcp( new MongoDbSinkOptions().Host, new MongoDbSinkOptions().Port ),
         OracleSink => Tcp( new OracleSinkOptions().Host, new OracleSinkOptions().Port ),
         PgVectorSink => Tcp( new PgVectorSinkOptions().Host, new PgVectorSinkOptions().Port ),
         RedisSink => Tcp( new RedisSinkOptions().Host, new RedisSinkOptions().Port ),
         DuckDbSink => InProcess( new DuckDbSinkOptions().DirectoryPath ),
         SqliteVecSink => InProcess( new SqliteVecSinkOptions().DirectoryPath ),
         _ => null,
      };
   }

   /// <summary>
   /// A probe that connects to a host and port. Exposed so tests can point one at a port that
   /// nothing listens on.
   /// </summary>
   /// <param name="host">Host name or address.</param>
   /// <param name="port">TCP port.</param>
   /// <returns>The probe; it throws <see cref="InvalidOperationException"/> naming the address when nothing answers.</returns>
   public static Func<CancellationToken, Task> Tcp( string host, int port )
   {
      return async ct =>
      {
         using var client = new TcpClient();
         try
         {
            await client.ConnectAsync( host, port, ct );
         }
         catch( SocketException ex )
         {
            throw new InvalidOperationException( $"nothing answered at {host}:{port} ({ex.SocketErrorCode})", ex );
         }
      };
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A probe that connects to the host and port of a URL.
   /// </summary>
   /// <param name="url">The engine's base address, e.g. http://127.0.0.1:8000.</param>
   /// <returns>The probe.</returns>
   private static Func<CancellationToken, Task> Url( string url )
   {
      var uri = new Uri( url );
      return Tcp( uri.Host, uri.Port );
   }

   /// <summary>
   /// A probe for an engine that runs inside this app: fine when its data folder exists, or
   /// when the nearest folder above it exists so the folder can be made on first use.
   /// </summary>
   /// <param name="directory">The engine's data folder.</param>
   /// <returns>The probe.</returns>
   private static Func<CancellationToken, Task> InProcess( string directory )
   {
      return _ =>
      {
         string? existing = directory;
         while( existing != null && !Directory.Exists( existing ) )
         {
            existing = Path.GetDirectoryName( existing );
         }

         return existing != null ? Task.CompletedTask : throw new InvalidOperationException( $"its data folder {directory} cannot be made" );
      };
   }

   #endregion Private Methods
}
