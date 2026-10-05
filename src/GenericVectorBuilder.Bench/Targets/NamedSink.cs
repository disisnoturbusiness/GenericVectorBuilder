using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Bench.Targets;

/// <summary>
/// A sink under a benchmark target name of its own, with the facts the report prints beside its
/// numbers (engine, index, durability), around one of the builder's own sinks.
/// Why a wrapper: the builder's SQL Server and Qdrant sinks call themselves "sql" and "qdrant" and
/// describe nothing, and the benchmark now measures each of them twice, in its own container (the
/// default targets "sql" and "qdrant") and as the native service ("sql-native", "qdrant-native").
/// The wrapper gives each its target name and its description without changing the builder's sinks.
/// Why the inner sink is made by a function: a container target's sink can only be built once the
/// container's address is known, and a sink whose database was dropped is built again (the
/// builder's SQL sink remembers that its database exists and would not create it a second time).
/// </summary>
public sealed class NamedSink : ISink, IEngineDescription
{
   #region Data Members

   private readonly Func<ISink> _create;
   private readonly SinkText _text;
   private readonly Func<string, CancellationToken, Task<bool>>? _afterDrop;
   private readonly object _gate = new();
   private ISink? _inner;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates the wrapper. Nothing is built or connected here.
   /// </summary>
   /// <param name="name">Target name, e.g. "sql-native".</param>
   /// <param name="create">Builds the sink that does the work; called on first use and again after a drop that reset it.</param>
   /// <param name="text">What the report prints about the engine.</param>
   /// <param name="afterDrop">Runs after each dropped collection; returning true throws the inner sink away so the next use builds a new one. Null for nothing.</param>
   public NamedSink( string name, Func<ISink> create, SinkText text, Func<string, CancellationToken, Task<bool>>? afterDrop = null )
   {
      Name = name;
      _create = create;
      _text = text;
      _afterDrop = afterDrop;
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name { get; }

   /// <inheritdoc />
   public string Engine => _text.Engine();

   /// <inheritdoc />
   public string IndexDescription => _text.Index();

   /// <inheritdoc />
   public string ComposeFile => _text.ComposeFile;

   /// <inheritdoc />
   public string Durability => _text.Durability();

   /// <inheritdoc />
   public Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      return Inner.EnsureCollectionAsync( collection, dimension, ct );
   }

   /// <inheritdoc />
   public Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      return Inner.UpsertAsync( collection, records, ct );
   }

   /// <inheritdoc />
   public Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      return Inner.DeleteAsync( collection, chunkIds, ct );
   }

   /// <inheritdoc />
   public Task<long> CountAsync( string collection, CancellationToken ct )
   {
      return Inner.CountAsync( collection, ct );
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      return Inner.SearchAsync( collection, vector, top, ct );
   }

   /// <summary>
   /// Drops the collection, then runs the after-drop step (for the container SQL target: drop the
   /// benchmark database once it holds no table) and builds a new inner sink next time if it asks.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   public async Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      await Inner.DropCollectionAsync( collection, ct );
      if( _afterDrop != null && await _afterDrop( collection, ct ) )
      {
         lock( _gate )
         {
            _inner = null;
         }
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>The sink that does the work, built once under a lock so concurrent searchers share one.</summary>
   private ISink Inner
   {
      get
      {
         lock( _gate )
         {
            return _inner ??= _create();
         }
      }
   }

   #endregion Private Methods
}

/// <summary>
/// What the report prints about a wrapped sink. Functions, not strings, so a container target can
/// say what it read from the server once it has reached it.
/// </summary>
/// <param name="Engine">Engine name, version and where it runs.</param>
/// <param name="Index">How the default search works.</param>
/// <param name="ComposeFile">Compose file that starts the engine, or "always-on".</param>
/// <param name="Durability">What a crash can lose.</param>
public sealed record SinkText( Func<string> Engine, Func<string> Index, string ComposeFile, Func<string> Durability );
