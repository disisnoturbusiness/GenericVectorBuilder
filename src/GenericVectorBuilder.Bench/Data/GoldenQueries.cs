using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GenericVectorBuilder.Core.Configuration;
using GenericVectorBuilder.Core.Contracts;

namespace GenericVectorBuilder.Bench.Data;

/// <summary>
/// One labelled question from evalkit's questions_golden.json.
/// </summary>
/// <param name="Id">Question id.</param>
/// <param name="Question">The question text that gets embedded.</param>
/// <param name="Relevant">Files a good answer points at, with grades (3 best).</param>
public sealed record GoldenQuestion( string Id, string Question, List<GoldenRelevant> Relevant );

/// <summary>
/// One labelled relevant file.
/// </summary>
/// <param name="FilePath">Path relative to the repository root, e.g. "src/Web/Program.cs".</param>
/// <param name="Grade">Relevance grade, 1 to 3.</param>
public sealed record GoldenRelevant( string FilePath, int Grade );

/// <summary>
/// The golden queries and the hash of the file they were read from.
/// </summary>
/// <param name="Queries">The query set.</param>
/// <param name="FileSha256">SHA-256 of the bytes of the questions file the queries were made from, lower-case hex.</param>
public sealed record GoldenLoad( QuerySet Queries, string FileSha256 );

/// <summary>
/// Turns labelled questions into query vectors with the embedder the pipeline was built with.
/// Why the model comes from the stored fingerprint and the whole fingerprint is compared: a
/// query embedded with a different model or query format lands in the wrong place and scores
/// badly without any error, which would be blamed on the engines.
/// Why the vectors are cached on disk (keyed by fingerprint and question text): the GPU
/// service is shared, so a repeated benchmark makes no embedding calls at all. The first run
/// makes one probe call (it learns the dimension and proves the fingerprint) plus one call per
/// question.
/// </summary>
public static class GoldenQueries
{
   #region Data Members

   private static readonly JsonSerializerOptions JSON = new() { PropertyNameCaseInsensitive = true };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Loads the questions and their vectors.
   /// </summary>
   /// <param name="questionsFile">Path of questions_golden.json.</param>
   /// <param name="pipeline">Pipeline whose embedder to use.</param>
   /// <param name="settings">Builder settings (embedding service, state store).</param>
   /// <param name="cacheFolder">Folder for cached query vectors.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The query set.</returns>
   public static async Task<QuerySet> LoadAsync( string questionsFile, string pipeline, GvbSettings settings, string cacheFolder, CancellationToken ct )
   {
      return ( await LoadWithHashAsync( questionsFile, pipeline, settings, cacheFolder, ct ) ).Queries;
   }

   /// <summary>
   /// Loads the questions and their vectors and hashes the file they came from.
   /// Why the hash is taken from the same bytes the questions are read from: a hash of the file made
   /// before or after a separate read could describe a file that changed in between, and the run's
   /// record must name the file its queries were made from.
   /// Why the queries description is unchanged: it is part of the identity that sessions are matched
   /// on (the consolidation refuses to pool runs whose queries text differs), so the hash is returned
   /// beside the set and never written into its text.
   /// </summary>
   /// <param name="questionsFile">Path of questions_golden.json.</param>
   /// <param name="pipeline">Pipeline whose embedder to use.</param>
   /// <param name="settings">Builder settings (embedding service, state store).</param>
   /// <param name="cacheFolder">Folder for cached query vectors.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The query set and the file's SHA-256.</returns>
   public static async Task<GoldenLoad> LoadWithHashAsync( string questionsFile, string pipeline, GvbSettings settings, string cacheFolder, CancellationToken ct )
   {
      byte[] bytes = await File.ReadAllBytesAsync( questionsFile, ct );
      string hash = Sha256Hex( bytes );
      List<GoldenQuestion> questions = JsonSerializer.Deserialize<List<GoldenQuestion>>( Decode( bytes ), JSON )
         ?? throw new InvalidOperationException( $"{questionsFile} holds no questions." );
      using var services = new GvbServices( settings );
      string fingerprint = await services.State.GetFingerprintAsync( pipeline, ct )
         ?? throw new InvalidOperationException( $"Pipeline '{pipeline}' has no stored embedder fingerprint, so its questions cannot be embedded the same way." );
      string cacheFile = Path.Combine( cacheFolder, $"golden-{pipeline}-{CacheKey( fingerprint, questions )}.json" );
      float[][]? vectors = await ReadCacheAsync( cacheFile, questions.Count, ct );
      string source = $"cached in {cacheFile}, no embedding calls";
      if( vectors == null )
      {
         vectors = await EmbedAsync( services, fingerprint, pipeline, questions, ct );
         await WriteCacheAsync( cacheFolder, cacheFile, vectors, ct );
         source = $"embedded now: 1 probe + {questions.Count} query calls to the GPU service";
      }

      IReadOnlyDictionary<string, int>[] grades = questions.Select( q => (IReadOnlyDictionary<string, int>)q.Relevant
         .GroupBy( r => r.FilePath ).ToDictionary( g => g.Key, g => g.Max( r => r.Grade ) ) ).ToArray();
      string description = $"golden: {questions.Count} labelled questions from {questionsFile}, embedded with {GvbServices.ModelFromFingerprint( fingerprint )} ({source})";
      var queries = new QuerySet( description, vectors.Select( QuerySet.Normalize ).ToArray(), Enumerable.Repeat( -1, questions.Count ).ToArray(),
         questions.Select( q => q.Id ).ToArray(), grades );
      return new GoldenLoad( queries, hash );
   }

   /// <summary>
   /// The SHA-256 of a questions file, for a run that reads no queries (a settings-only run) but must still say which file it
   /// would have used. Same hash, same bytes rule as <see cref="LoadWithHashAsync"/>.
   /// </summary>
   /// <param name="questionsFile">Path of questions_golden.json.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>64 lower-case hex digits.</returns>
   /// <exception cref="FileNotFoundException">The file does not exist.</exception>
   public static async Task<string> HashFileAsync( string questionsFile, CancellationToken ct )
   {
      return Sha256Hex( await File.ReadAllBytesAsync( questionsFile, ct ) );
   }

   /// <summary>
   /// The SHA-256 of some bytes as lower-case hex (what results.json records as queriesFileSha256).
   /// </summary>
   /// <param name="bytes">The bytes.</param>
   /// <returns>64 hex digits.</returns>
   public static string Sha256Hex( byte[] bytes )
   {
      return Convert.ToHexString( SHA256.HashData( bytes ) ).ToLowerInvariant();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Embeds every question after proving the embedder matches the stored fingerprint.
   /// </summary>
   /// <param name="services">Builder services.</param>
   /// <param name="fingerprint">Stored fingerprint.</param>
   /// <param name="pipeline">Pipeline name, for the error message.</param>
   /// <param name="questions">Questions.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>One vector per question.</returns>
   private static async Task<float[][]> EmbedAsync( GvbServices services, string fingerprint, string pipeline, List<GoldenQuestion> questions, CancellationToken ct )
   {
      IEmbedder embedder = services.CreateEmbedder( GvbServices.ModelFromFingerprint( fingerprint ) );
      await embedder.PreflightAsync( ct );
      if( embedder.Fingerprint != fingerprint )
      {
         throw new InvalidOperationException( $"Pipeline '{pipeline}' was built with different embedding settings than the embedder gives today, so its questions would be embedded wrongly. (Built with: {fingerprint}. Now: {embedder.Fingerprint}.)" );
      }

      var vectors = new float[questions.Count][];
      for( int i = 0; i < questions.Count; i++ )
      {
         vectors[i] = await embedder.EmbedQueryAsync( questions[i].Question, ct );
      }

      return vectors;
   }

   /// <summary>
   /// Decodes the file's bytes as text the way File.ReadAllText does (UTF-8, a byte order mark honoured),
   /// so reading the bytes once for the hash gives the same questions the older read gave.
   /// </summary>
   /// <param name="bytes">The file's bytes.</param>
   /// <returns>The text.</returns>
   private static string Decode( byte[] bytes )
   {
      using var reader = new StreamReader( new MemoryStream( bytes ), Encoding.UTF8, detectEncodingFromByteOrderMarks: true );
      return reader.ReadToEnd();
   }

   /// <summary>
   /// Writes the cache file: the text is built first, written to a temporary file in the same folder
   /// and moved over the name, so a crash never leaves a half-written cache that a later run would
   /// read as "no embedding calls" (a cache that fails to parse is an error, not a reason to embed
   /// again silently).
   /// </summary>
   /// <param name="folder">Cache folder (created if missing).</param>
   /// <param name="file">Cache file.</param>
   /// <param name="vectors">One vector per question.</param>
   /// <param name="ct">Cancellation.</param>
   private static async Task WriteCacheAsync( string folder, string file, float[][] vectors, CancellationToken ct )
   {
      string text = JsonSerializer.Serialize( vectors );
      Directory.CreateDirectory( folder );
      string temporary = file + ".tmp";
      try
      {
         await File.WriteAllTextAsync( temporary, text, ct );
         File.Move( temporary, file, true );
      }
      finally
      {
         if( File.Exists( temporary ) )
         {
            File.Delete( temporary );
         }
      }
   }

   /// <summary>
   /// Reads cached vectors when the file exists and holds one vector per question.
   /// </summary>
   /// <param name="file">Cache file.</param>
   /// <param name="expected">Number of questions.</param>
   /// <param name="ct">Cancellation.</param>
   /// <returns>The vectors, or null.</returns>
   private static async Task<float[][]?> ReadCacheAsync( string file, int expected, CancellationToken ct )
   {
      if( !File.Exists( file ) )
      {
         return null;
      }

      float[][]? vectors = JsonSerializer.Deserialize<float[][]>( await File.ReadAllTextAsync( file, ct ) );
      return vectors != null && vectors.Length == expected ? vectors : null;
   }

   /// <summary>
   /// First 16 hex characters of a SHA-256 over the fingerprint and every question text, so a
   /// changed question or embedder never reuses stale vectors.
   /// </summary>
   /// <param name="fingerprint">Stored fingerprint.</param>
   /// <param name="questions">Questions.</param>
   /// <returns>The key.</returns>
   private static string CacheKey( string fingerprint, List<GoldenQuestion> questions )
   {
      string text = fingerprint + "\n" + string.Join( "\n", questions.Select( q => q.Question ) );
      return Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( text ) ) )[..16].ToLowerInvariant();
   }

   #endregion Private Methods
}
