using System.Numerics.Tensors;
using System.Text.Json;
using GenericVectorBuilder.Core.Contracts;

namespace GenericVectorBuilder.Bench.Data;

/// <summary>
/// Every vector of one pipeline held in memory: one contiguous float buffer (row-major,
/// L2-normalized) plus each row's id and payload.
/// Why in memory: the ground truth is a brute force over ALL vectors, and every target must
/// be loaded from exactly the same rows. Holding them once means the source table is read
/// once, load timings measure only the target's writes, and 760,479 x 1024 floats (3.1 GB)
/// fits easily on this box.
/// Why normalized: cosine similarity of normalized vectors is a plain dot product, which is
/// what the brute force computes, and engines that only offer inner product then agree.
/// </summary>
public sealed class PipelineData
{
   #region Data Members

   private const string CODE_KEY_PREFIX = "code|";

   private readonly float[] _vectors;
   private readonly Guid[] _ids;
   private readonly string[] _docKeys;
   private readonly string[] _tables;
   private readonly string[] _origins;
   private readonly int[] _ordinals;
   private readonly string[] _texts;
   private readonly string?[] _metadata;
   private readonly Dictionary<Guid, int> _rowById;
   private int _count;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Allocates room for a known number of rows.
   /// </summary>
   /// <param name="pipeline">Pipeline name.</param>
   /// <param name="capacity">Rows to allocate.</param>
   /// <param name="dimension">Vector length.</param>
   public PipelineData( string pipeline, int capacity, int dimension )
   {
      if( (long)capacity * dimension > Array.MaxLength )
      {
         throw new InvalidOperationException( $"{capacity:N0} rows of {dimension} floats is more than one buffer can hold. Use --limit." );
      }

      Pipeline = pipeline;
      Dimension = dimension;
      _vectors = new float[(long)capacity * dimension];
      _ids = new Guid[capacity];
      _docKeys = new string[capacity];
      _tables = new string[capacity];
      _origins = new string[capacity];
      _ordinals = new int[capacity];
      _texts = new string[capacity];
      _metadata = new string?[capacity];
      _rowById = new Dictionary<Guid, int>( capacity );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>Pipeline the rows came from.</summary>
   public string Pipeline { get; }

   /// <summary>Vector length.</summary>
   public int Dimension { get; }

   /// <summary>Rows held.</summary>
   public int Count => _count;

   /// <summary>Whether chunk text was loaded (needed to replicate, not to benchmark).</summary>
   public bool HasText { get; set; }

   /// <summary>
   /// Appends one row, normalizing its vector. Strings repeated on many rows (table, origin)
   /// should be interned by the caller.
   /// </summary>
   /// <param name="id">Chunk id.</param>
   /// <param name="docKey">Document key.</param>
   /// <param name="table">Logical table.</param>
   /// <param name="origin">Origin file.</param>
   /// <param name="ordinal">Chunk ordinal.</param>
   /// <param name="text">Chunk text, or empty when not loaded.</param>
   /// <param name="metadataJson">Metadata as stored (a JSON object of strings), or null.</param>
   /// <param name="vector">The vector.</param>
   public void Add( Guid id, string docKey, string table, string origin, int ordinal, string text, string? metadataJson, ReadOnlySpan<float> vector )
   {
      if( vector.Length != Dimension )
      {
         throw new InvalidOperationException( $"Row {id} has a {vector.Length}-dim vector; the table holds {Dimension}-dim vectors." );
      }

      Span<float> target = _vectors.AsSpan( (int)( (long)_count * Dimension ), Dimension );
      float norm = TensorPrimitives.Norm( vector );
      if( norm <= 0 || float.IsNaN( norm ) )
      {
         throw new InvalidOperationException( $"Row {id} has an all-zero or NaN vector." );
      }

      TensorPrimitives.Divide( vector, norm, target );
      _ids[_count] = id;
      _docKeys[_count] = docKey;
      _tables[_count] = table;
      _origins[_count] = origin;
      _ordinals[_count] = ordinal;
      _texts[_count] = text;
      _metadata[_count] = metadataJson;
      _rowById[id] = _count;
      _count++;
   }

   /// <summary>
   /// One row's normalized vector, without copying.
   /// </summary>
   /// <param name="row">Row number.</param>
   /// <returns>The vector.</returns>
   public ReadOnlySpan<float> Vector( int row )
   {
      return _vectors.AsSpan( (int)( (long)row * Dimension ), Dimension );
   }

   /// <summary>
   /// A block of consecutive rows as one span, for the brute force.
   /// </summary>
   /// <param name="from">First row.</param>
   /// <param name="count">Rows.</param>
   /// <returns>The vectors, row-major.</returns>
   public ReadOnlySpan<float> Block( int from, int count )
   {
      return _vectors.AsSpan( (int)( (long)from * Dimension ), count * Dimension );
   }

   /// <summary>Chunk id of a row.</summary>
   /// <param name="row">Row number.</param>
   /// <returns>The id.</returns>
   public Guid Id( int row )
   {
      return _ids[row];
   }

   /// <summary>
   /// Finds the row of a chunk id.
   /// </summary>
   /// <param name="id">Chunk id.</param>
   /// <param name="row">The row when found.</param>
   /// <returns>True when the id is one of the loaded rows.</returns>
   public bool TryGetRow( Guid id, out int row )
   {
      return _rowById.TryGetValue( id, out row );
   }

   /// <summary>
   /// Exact cosine similarity of a query to a stored chunk.
   /// </summary>
   /// <param name="query">Normalized query vector.</param>
   /// <param name="id">Chunk id.</param>
   /// <returns>The similarity, or null when the id is not a loaded row.</returns>
   public double? Similarity( float[] query, Guid id )
   {
      return TryGetRow( id, out int row ) ? TensorPrimitives.Dot( query, Vector( row ) ) : null;
   }

   /// <summary>
   /// The source file a row came from, for file-level nDCG: the "path" metadata a code
   /// pipeline stores, else the path inside a "code|" document key, else the origin.
   /// </summary>
   /// <param name="row">Row number.</param>
   /// <returns>The file path.</returns>
   public string FilePath( int row )
   {
      string? json = _metadata[row];
      if( json != null && json.Contains( "\"path\"", StringComparison.Ordinal ) )
      {
         Dictionary<string, string>? meta = JsonSerializer.Deserialize<Dictionary<string, string>>( json );
         if( meta != null && meta.TryGetValue( "path", out string? path ) && !string.IsNullOrEmpty( path ) )
         {
            return path;
         }
      }

      string key = _docKeys[row];
      return key.StartsWith( CODE_KEY_PREFIX, StringComparison.Ordinal ) ? key[CODE_KEY_PREFIX.Length..] : _origins[row];
   }

   /// <summary>
   /// Builds the records for a range of rows, in the same shape the pipeline writes, so a
   /// target receives exactly what a real run would send it.
   /// </summary>
   /// <param name="from">First row.</param>
   /// <param name="count">Rows.</param>
   /// <returns>The records.</returns>
   public IReadOnlyList<VectorRecord> Records( int from, int count )
   {
      if( !HasText )
      {
         throw new InvalidOperationException( "Chunk text was not loaded, so the rows cannot be copied to a target." );
      }

      var records = new List<VectorRecord>( count );
      for( int row = from; row < from + count; row++ )
      {
         IReadOnlyDictionary<string, string> meta = _metadata[row] == null ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>( _metadata[row]! ) ?? new Dictionary<string, string>();
         var document = new Document( _docKeys[row], _tables[row], _origins[row], _texts[row], string.Empty, meta );
         var chunk = new Chunk( _ids[row], _docKeys[row], _ordinals[row], _texts[row] );
         records.Add( new VectorRecord( document, chunk, Vector( row ).ToArray() ) );
      }

      return records;
   }

   #endregion Public Methods
}
