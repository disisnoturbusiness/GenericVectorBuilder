using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Sinks;
using Xunit.Abstractions;

namespace GenericVectorBuilder.Engines.Tests;

/// <summary>
/// The concurrent-search tests (<see cref="EmbeddedSinkConcurrencyTests"/>) run against DuckDB
/// with the vss extension, in a throwaway folder.
/// </summary>
[Trait( "Category", "Live" )]
[Collection( "EmbeddedConcurrency" )]
public sealed class DuckDbConcurrencyTests : EmbeddedSinkConcurrencyTests
{
   #region Constructor

   /// <summary>
   /// Creates the test with xunit's output sink.
   /// </summary>
   /// <param name="output">Where the evidence is printed.</param>
   public DuckDbConcurrencyTests( ITestOutputHelper output ) : base( output, "duck" )
   {
   }

   #endregion Constructor

   #region Overrides

   /// <inheritdoc />
   protected override ISink CreateSink( string folder, bool serialized, int maxSearchConnections, int waitSeconds )
   {
      return new DuckDbSink( new DuckDbSinkOptions
      {
         DirectoryPath = folder,
         SerializeSearches = serialized,
         MaxSearchConnections = maxSearchConnections,
         ConnectionWaitSeconds = waitSeconds
      } );
   }

   /// <inheritdoc />
   protected override int ConnectionsOpened( ISink sink, string collection )
   {
      return ( (DuckDbSink)sink ).SearchConnectionsOpened( collection );
   }

   /// <inheritdoc />
   protected override int LongUpsertCount => 4000;

   /// <inheritdoc />
   protected override string FileOf( string folder, string collection )
   {
      return Path.Combine( folder, $"gvb_{collection}.duckdb" );
   }

   #endregion Overrides
}
