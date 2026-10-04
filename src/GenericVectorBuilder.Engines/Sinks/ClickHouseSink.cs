using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Core.Contracts;
using GenericVectorBuilder.Engines.Common;

namespace GenericVectorBuilder.Engines.Sinks;

/// <summary>
/// Writes vectors to ClickHouse over its HTTP interface: one MergeTree table per pipeline named
/// gvb.gvb_{pipeline}, ordered by chunk id, with the payload in plain columns (doc_key, table,
/// origin, ordinal, text, meta as a Map) and the vector in an Array(Float32) column.
/// Default search: a vector_similarity skip index (HNSW, cosineDistance, quantization from
/// <see cref="ClickHouseSinkOptions.Quantization"/>, M 16, ef_construction 128) queried with
/// ORDER BY cosineDistance(...) LIMIT n and ClickHouse's own beam width
/// (hnsw_candidate_list_size_for_search, default 256).
/// Why bf16 quantization and no rescoring (bf16 is the documented default; rescoring 0 is what
/// system.settings shows on this server): measured 2026-10-03 on this
/// box, 30,000 clustered vectors of 1024 dimensions (300 centres plus noise), 20 queries,
/// recall@10 against brute force, 6 data parts as loaded and 1 part after OPTIMIZE FINAL:
///   setting             as loaded recall / ms p50    merged recall / ms p50
///   bf16, no rescoring  0.985 / 105                  1.00 / 30
///   f32, no rescoring   0.995 / 55                   1.00 / 15
///   bf16, rescoring     1.00 / 309                   1.00 / 138
/// bf16 gave up nothing measurable, and rescoring cost four to five times the latency.
/// On 100,000 uniform random vectors (the worst case, used by the scale test, loaded as 100
/// inserts of 1,000 rows): as loaded recall 0.61 at p50 173 ms, merged 0.15 at 26 ms, exact
/// scan 554 ms. Many small graphs searched together find more than one large graph at the same
/// beam width, and cost more per query.
/// Exact search: the same query with skip indexes switched off, so every row's distance is
/// computed and sorted.
/// Why RowBinary: it ships the vector as raw float32 bytes. JSON would send each float as text,
/// roughly three times the bytes and a text parse on the server for every dimension.
/// Why each insert is its own HNSW index: ClickHouse builds a vector index per data part, and
/// every INSERT makes a part. Parts are merged in the background (which rebuilds the index), so
/// a freshly loaded table is searched through many small graphs until the merges finish.
/// <see cref="OptimizeAsync"/> forces them into one, for a steady-state measurement.
/// Why upserts check before they write: a MergeTree has no unique key. The sink counts which of
/// a batch's chunk ids already exist, deletes only those rows with a lightweight DELETE, then
/// inserts, so loading new data never pays for deletes and re-sending a batch never duplicates.
/// Rows removed that way stay in their old parts (and old indexes) until a merge, and the
/// vector index may then return fewer than the requested number of hits for a search.
/// </summary>
public sealed class ClickHouseSink : ISink, IExactSearchSink, IEngineDescription, IDisposable
{
   #region Data Members

   private const int DELETE_BATCH = 1000;
   private const int MAX_ATTEMPTS = 3;
   private static readonly string[] QUANTIZATIONS = { "f32", "f16", "bf16", "i8", "b1" };
   private static readonly Regex DIMENSION_PATTERN = new( @"vector_similarity\(\s*'hnsw'\s*,\s*'cosineDistance'\s*,\s*(\d+)", RegexOptions.Compiled );

   private readonly ClickHouseSinkOptions _options;
   private readonly HttpClient _http;
   private bool _databaseReady;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a sink for the local ClickHouse container with the password from the secrets file.
   /// </summary>
   public ClickHouseSink() : this( ClickHouseSinkOptions.LocalDefaults() )
   {
   }

   /// <summary>
   /// Creates a sink with explicit settings.
   /// </summary>
   /// <param name="options">Connection and index settings.</param>
   public ClickHouseSink( ClickHouseSinkOptions options )
   {
      if( !QUANTIZATIONS.Contains( options.Quantization ) )
      {
         throw new ArgumentException( $"Quantization must be one of {string.Join( ", ", QUANTIZATIONS )}." );
      }

      _options = options;
      _http = new HttpClient { BaseAddress = new Uri( $"http://{options.Host}:{options.Port}/" ), Timeout = TimeSpan.FromHours( 1 ) };
      _http.DefaultRequestHeaders.Add( "X-ClickHouse-User", options.User );
      if( !string.IsNullOrEmpty( options.Password ) )
      {
         _http.DefaultRequestHeaders.Add( "X-ClickHouse-Key", options.Password );
      }
   }

   #endregion Constructor

   #region Public Methods

   /// <inheritdoc />
   public string Name => "clickhouse";

   /// <inheritdoc />
   public string Engine => "ClickHouse 26.3.39.7 (MergeTree + vector_similarity index)";

   /// <inheritdoc />
   public string IndexDescription =>
      $"vector_similarity HNSW cosineDistance, quantization {_options.Quantization}, M={_options.HnswM} ef_construction={_options.HnswEfConstruction}, "
      + $"hnsw_candidate_list_size_for_search={_options.HnswEfSearch}, rescoring {( _options.Rescoring == 1 ? "on" : "off" )}; exact mode = full scan with skip indexes off";

   /// <inheritdoc />
   public string ComposeFile => "clickhouse.compose.yaml";

   /// <inheritdoc />
   public async Task<bool> EnsureCollectionAsync( string collection, int dimension, CancellationToken ct )
   {
      await EnsureDatabaseAsync( ct );
      string definition = await QueryAsync(
         $"SELECT create_table_query FROM system.tables WHERE database = {Literal( _options.Database )} AND name = {Literal( TableName( collection ) )} FORMAT TSVRaw", ct );
      if( definition.Length > 0 )
      {
         Match match = DIMENSION_PATTERN.Match( definition );
         int existing = match.Success ? int.Parse( match.Groups[1].Value, CultureInfo.InvariantCulture ) : 0;
         if( existing != dimension )
         {
            throw new InvalidOperationException( $"ClickHouse table {TableName( collection )} holds {existing}-dim vectors but the embedder produces {dimension}. Reset the pipeline or use a new name." );
         }

         return false;
      }

      await ExecuteAsync( CreateTableSql( collection, dimension ), ct );
      return true;
   }

   /// <inheritdoc />
   public async Task UpsertAsync( string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct )
   {
      string table = Table( collection );
      IEnumerable<VectorRecord> latest = records.Reverse().DistinctBy( r => r.Chunk.ChunkId ).Reverse();
      foreach( VectorRecord[] batch in latest.Chunk( _options.UpsertBatch ) )
      {
         await WithRetryAsync( () => WriteBatchAsync( table, batch, ct ), ct );
      }
   }

   /// <inheritdoc />
   public async Task DeleteAsync( string collection, IReadOnlyList<Guid> chunkIds, CancellationToken ct )
   {
      string table = Table( collection );
      foreach( Guid[] batch in chunkIds.Chunk( DELETE_BATCH ) )
      {
         await WithRetryAsync( () => ExecuteAsync( $"DELETE FROM {table} WHERE chunk_id IN ({IdList( batch )})", ct ), ct );
      }
   }

   /// <inheritdoc />
   public async Task<long> CountAsync( string collection, CancellationToken ct )
   {
      string count = await QueryAsync( $"SELECT count() FROM {Table( collection )} FORMAT TSVRaw", ct );
      return long.Parse( count, CultureInfo.InvariantCulture );
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      string settings = $"hnsw_candidate_list_size_for_search = {_options.HnswEfSearch}, vector_search_with_rescoring = {_options.Rescoring}";
      return RunSearchAsync( collection, vector, top, settings, ct );
   }

   /// <inheritdoc />
   public Task<IReadOnlyList<SearchHit>> SearchExactAsync( string collection, float[] vector, int top, CancellationToken ct )
   {
      return RunSearchAsync( collection, vector, top, "use_skip_indexes = 0, query_plan_try_use_vector_search = 0", ct );
   }

   /// <inheritdoc />
   public Task DropCollectionAsync( string collection, CancellationToken ct )
   {
      return ExecuteAsync( $"DROP TABLE IF EXISTS {Table( collection )} SYNC", ct );
   }

   /// <summary>
   /// Merges every data part of the table into one, which rebuilds the vector index as a single
   /// graph. Not part of the sink contract; the scale test uses it to time search on a
   /// fully merged table next to the freshly loaded one.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="ct">Cancellation.</param>
   public Task OptimizeAsync( string collection, CancellationToken ct )
   {
      return ExecuteAsync( $"OPTIMIZE TABLE {Table( collection )} FINAL", ct );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Runs one nearest-neighbour query and converts the rows to hits.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="vector">Query vector.</param>
   /// <param name="top">Hits wanted.</param>
   /// <param name="settings">SETTINGS clause body for this query.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>Hits, best first, with cosine similarity.</returns>
   private async Task<IReadOnlyList<SearchHit>> RunSearchAsync( string collection, float[] vector, int top, string settings, CancellationToken ct )
   {
      string sql = $"WITH {VectorLiteral( vector )} AS q SELECT toString( chunk_id ) AS id, doc_key, `table` AS tbl, text, cosineDistance( embedding, q ) AS dist "
         + $"FROM {Table( collection )} ORDER BY dist ASC LIMIT {top} SETTINGS {settings} FORMAT JSONEachRow";
      string body = await QueryAsync( sql, ct );
      var hits = new List<SearchHit>( top );
      foreach( string line in body.Split( '\n', StringSplitOptions.RemoveEmptyEntries ) )
      {
         using JsonDocument row = JsonDocument.Parse( line );
         JsonElement root = row.RootElement;
         hits.Add( new SearchHit( Guid.Parse( root.GetProperty( "id" ).GetString()! ), root.GetProperty( "doc_key" ).GetString()!,
            root.GetProperty( "tbl" ).GetString()!, root.GetProperty( "text" ).GetString()!, 1.0 - root.GetProperty( "dist" ).GetDouble() ) );
      }

      return hits;
   }

   /// <summary>
   /// Writes one batch: delete the rows that already exist, then insert the whole batch.
   /// </summary>
   /// <param name="table">Quoted table name.</param>
   /// <param name="batch">Records to write.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task WriteBatchAsync( string table, VectorRecord[] batch, CancellationToken ct )
   {
      string ids = IdList( batch.Select( r => r.Chunk.ChunkId ) );
      string existing = await QueryAsync( $"SELECT count() FROM {table} WHERE chunk_id IN ({ids}) FORMAT TSVRaw", ct );
      if( existing != "0" )
      {
         await ExecuteAsync( $"DELETE FROM {table} WHERE chunk_id IN ({ids})", ct );
      }

      string insert = $"INSERT INTO {table} ( chunk_id, doc_key, `table`, origin, ordinal, text, meta, embedding ) FORMAT RowBinary";
      using var content = new ByteArrayContent( ToRowBinary( batch ) );
      await SendAsync( "?query=" + Uri.EscapeDataString( insert ), content, ct );
   }

   /// <summary>
   /// Encodes records in ClickHouse's RowBinary format, column order as in the INSERT.
   /// </summary>
   /// <param name="batch">Records.</param>
   /// <returns>The request body.</returns>
   private static byte[] ToRowBinary( VectorRecord[] batch )
   {
      using var stream = new MemoryStream( batch.Length * ( batch[0].Vector.Length * 4 + 512 ) );
      foreach( VectorRecord r in batch )
      {
         WriteUuid( stream, r.Chunk.ChunkId );
         WriteString( stream, r.Document.DocKey );
         WriteString( stream, r.Document.Table );
         WriteString( stream, r.Document.Origin );
         stream.Write( BitConverter.GetBytes( r.Chunk.Ordinal ) );
         WriteString( stream, r.Chunk.Text );
         WriteVarint( stream, (ulong)r.Document.Metadata.Count );
         foreach( KeyValuePair<string, string> pair in r.Document.Metadata )
         {
            WriteString( stream, pair.Key );
            WriteString( stream, pair.Value );
         }

         WriteVarint( stream, (ulong)r.Vector.Length );
         stream.Write( MemoryMarshal.AsBytes( r.Vector.AsSpan() ) );
      }

      return stream.ToArray();
   }

   /// <summary>
   /// Writes a UUID the way RowBinary stores it: the two 8-byte halves of the canonical byte
   /// order, each reversed.
   /// </summary>
   /// <param name="stream">Destination.</param>
   /// <param name="id">The id.</param>
   private static void WriteUuid( MemoryStream stream, Guid id )
   {
      byte[] bytes = id.ToByteArray( bigEndian: true );
      Array.Reverse( bytes, 0, 8 );
      Array.Reverse( bytes, 8, 8 );
      stream.Write( bytes );
   }

   /// <summary>
   /// Writes a RowBinary string: length as a varint, then the UTF-8 bytes.
   /// </summary>
   /// <param name="stream">Destination.</param>
   /// <param name="value">The text.</param>
   private static void WriteString( MemoryStream stream, string value )
   {
      byte[] bytes = Encoding.UTF8.GetBytes( value );
      WriteVarint( stream, (ulong)bytes.Length );
      stream.Write( bytes );
   }

   /// <summary>
   /// Writes an unsigned LEB128 integer.
   /// </summary>
   /// <param name="stream">Destination.</param>
   /// <param name="value">The number.</param>
   private static void WriteVarint( MemoryStream stream, ulong value )
   {
      while( value >= 0x80 )
      {
         stream.WriteByte( (byte)( value | 0x80 ) );
         value >>= 7;
      }

      stream.WriteByte( (byte)value );
   }

   /// <summary>
   /// Builds the CREATE TABLE statement for a pipeline.
   /// </summary>
   /// <param name="collection">Collection name.</param>
   /// <param name="dimension">Vector length.</param>
   /// <returns>The statement.</returns>
   private string CreateTableSql( string collection, int dimension )
   {
      return $@"CREATE TABLE {Table( collection )} (
   chunk_id UUID,
   doc_key String,
   `table` String,
   origin String,
   ordinal Int32,
   text String,
   meta Map(String, String),
   embedding Array(Float32),
   INDEX vec_idx embedding TYPE vector_similarity( 'hnsw', 'cosineDistance', {dimension}, '{_options.Quantization}', {_options.HnswM}, {_options.HnswEfConstruction} )
) ENGINE = MergeTree ORDER BY chunk_id";
   }

   /// <summary>
   /// Creates the database on first use.
   /// </summary>
   /// <param name="ct">Cancellation.</param>
   private async Task EnsureDatabaseAsync( CancellationToken ct )
   {
      if( !_databaseReady )
      {
         await ExecuteAsync( $"CREATE DATABASE IF NOT EXISTS {Quote( _options.Database )}", ct );
         _databaseReady = true;
      }
   }

   /// <summary>
   /// Runs a statement and returns its text result, trimmed.
   /// </summary>
   /// <param name="sql">Statement.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The response body.</returns>
   private async Task<string> QueryAsync( string sql, CancellationToken ct )
   {
      using var content = new StringContent( sql, Encoding.UTF8, "text/plain" );
      return ( await SendAsync( string.Empty, content, ct ) ).Trim();
   }

   /// <summary>
   /// Runs a statement that returns nothing.
   /// </summary>
   /// <param name="sql">Statement.</param>
   /// <param name="ct">Cancellation.</param>
   private async Task ExecuteAsync( string sql, CancellationToken ct )
   {
      await QueryAsync( sql, ct );
   }

   /// <summary>
   /// Posts a request and returns the body, turning an error response into a plain message
   /// (the first line of ClickHouse's explanation, never the credentials).
   /// </summary>
   /// <param name="query">Query string, with its leading question mark, or empty.</param>
   /// <param name="content">Request body.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The response body.</returns>
   private async Task<string> SendAsync( string query, HttpContent content, CancellationToken ct )
   {
      using HttpResponseMessage response = await _http.PostAsync( query, content, ct );
      string body = await response.Content.ReadAsStringAsync( ct );
      if( !response.IsSuccessStatusCode )
      {
         string firstLine = body.Split( '\n', 2 )[0];
         throw new InvalidOperationException( $"ClickHouse refused the request ({(int)response.StatusCode}): {firstLine[..Math.Min( firstLine.Length, 400 )]}" );
      }

      return body;
   }

   /// <summary>
   /// Retries a call on network failures and timeouts with a short backoff.
   /// </summary>
   /// <param name="call">The call. Must be safe to repeat.</param>
   /// <param name="ct">Cancellation.</param>
   private static async Task WithRetryAsync( Func<Task> call, CancellationToken ct )
   {
      for( int attempt = 1; ; attempt++ )
      {
         try
         {
            await call();
            return;
         }
         catch( Exception ex ) when( attempt < MAX_ATTEMPTS && ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested )
         {
            await Task.Delay( TimeSpan.FromSeconds( attempt * 2 ), ct );
         }
      }
   }

   /// <summary>
   /// Formats a vector as a ClickHouse array literal, round-trip float formatting with the
   /// invariant culture so no locale can turn 0.5 into "0,5".
   /// </summary>
   /// <param name="vector">The vector.</param>
   /// <returns>The literal, e.g. [0.1,0.2].</returns>
   private static string VectorLiteral( float[] vector )
   {
      var text = new StringBuilder( vector.Length * 12 ).Append( '[' );
      for( int i = 0; i < vector.Length; i++ )
      {
         text.Append( i == 0 ? string.Empty : "," ).Append( vector[i].ToString( "R", CultureInfo.InvariantCulture ) );
      }

      return text.Append( ']' ).ToString();
   }

   /// <summary>
   /// Formats chunk ids as a comma-separated list of quoted UUIDs.
   /// </summary>
   /// <param name="ids">The ids.</param>
   /// <returns>The list body.</returns>
   private static string IdList( IEnumerable<Guid> ids )
   {
      return string.Join( ",", ids.Select( id => $"'{id:D}'" ) );
   }

   /// <summary>
   /// Quotes a string as a ClickHouse literal.
   /// </summary>
   /// <param name="value">The text.</param>
   /// <returns>The quoted text.</returns>
   private static string Literal( string value )
   {
      return "'" + value.Replace( "\\", "\\\\" ).Replace( "'", "\\'" ) + "'";
   }

   /// <summary>
   /// Backtick-quotes an identifier, escaping any backtick or backslash in it.
   /// </summary>
   /// <param name="identifier">The identifier.</param>
   /// <returns>The quoted identifier.</returns>
   private static string Quote( string identifier )
   {
      return "`" + identifier.Replace( "\\", "\\\\" ).Replace( "`", "\\`" ) + "`";
   }

   /// <summary>
   /// Fully qualified, quoted table name for a pipeline.
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>database.table, quoted.</returns>
   private string Table( string collection )
   {
      return $"{Quote( _options.Database )}.{Quote( TableName( collection ) )}";
   }

   /// <summary>
   /// Table name for a pipeline. Pipeline names are already sanitized to [a-z0-9_].
   /// </summary>
   /// <param name="collection">Pipeline collection name.</param>
   /// <returns>The table name.</returns>
   private static string TableName( string collection )
   {
      return $"gvb_{collection}";
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Releases the HTTP client.
   /// </summary>
   public void Dispose()
   {
      _http.Dispose();
   }

   #endregion IDisposable
}
