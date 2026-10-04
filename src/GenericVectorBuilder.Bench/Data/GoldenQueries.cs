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
      List<GoldenQuestion> questions = JsonSerializer.Deserialize<List<GoldenQuestion>>( await File.ReadAllTextAsync( questionsFile, ct ), JSON )
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
         Directory.CreateDirectory( cacheFolder );
         await File.WriteAllTextAsync( cacheFile, JsonSerializer.Serialize( vectors ), ct );
         source = $"embedded now: 1 probe + {questions.Count} query calls to the GPU service";
      }

      IReadOnlyDictionary<string, int>[] grades = questions.Select( q => (IReadOnlyDictionary<string, int>)q.Relevant
         .GroupBy( r => r.FilePath ).ToDictionary( g => g.Key, g => g.Max( r => r.Grade ) ) ).ToArray();
      string description = $"golden: {questions.Count} labelled questions from {questionsFile}, embedded with {GvbServices.ModelFromFingerprint( fingerprint )} ({source})";
      return new QuerySet( description, vectors.Select( QuerySet.Normalize ).ToArray(), Enumerable.Repeat( -1, questions.Count ).ToArray(),
         questions.Select( q => q.Id ).ToArray(), grades );
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
