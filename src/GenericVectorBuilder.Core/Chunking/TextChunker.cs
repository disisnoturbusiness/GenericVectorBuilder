using System.Security.Cryptography;
using System.Text;
using GenericVectorBuilder.Core.Contracts;

namespace GenericVectorBuilder.Core.Chunking;

/// <summary>
/// Splits document text into chunks the embedder can take whole, and gives each chunk a
/// deterministic id.
/// Why 1,800 characters: the GPU service truncates at 2,048 tokens, and the bge-code-v1 setup
/// appends an end-of-text token that MUST survive truncation (measured: without it, top-1 on
/// table rows fell from 14/14 to 5/14). Qwen-family tokenizers split numbers into single
/// digits, so a number-heavy row can approach one token per character; 1,800 characters keeps
/// even that case under the limit.
/// Why ids come from content: an unchanged chunk keeps its id across runs, so a re-run upsert
/// overwrites rather than duplicates, and ids no longer produced can be deleted precisely.
/// It is the default <see cref="IChunker"/>; the code chunker falls back to it for files it
/// cannot split by structure.
/// </summary>
public sealed class TextChunker : IChunker
{
   #region Data Members

   /// <summary>Maximum characters per chunk.</summary>
   public const int MAX_CHUNK_CHARS = 1800;

   private const int MAX_CONTEXT_LINE_CHARS = 200;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Splits a document. Short documents (almost every table row) become one chunk. Longer ones
   /// split on line boundaries, and every later chunk repeats the first line as context, so a
   /// continuation chunk still says which record it belongs to.
   /// </summary>
   /// <param name="pipeline">Pipeline name, part of the chunk id so pipelines never collide.</param>
   /// <param name="document">The document.</param>
   /// <returns>Chunks in order.</returns>
   public IReadOnlyList<Chunk> Split( string pipeline, Document document )
   {
      List<string> pieces = SplitText( document.Text );
      var chunks = new List<Chunk>( pieces.Count );
      var seen = new Dictionary<string, int>( StringComparer.Ordinal );
      for( int i = 0; i < pieces.Count; i++ )
      {
         // Identical pieces inside one document get distinct ids via an occurrence counter.
         int occurrence = seen.TryGetValue( pieces[i], out int n ) ? n + 1 : 1;
         seen[pieces[i]] = occurrence;
         chunks.Add( new Chunk( ChunkId( pipeline, document.DocKey, pieces[i], occurrence ), document.DocKey, i, pieces[i] ) );
      }

      return chunks;
   }

   /// <summary>
   /// Builds a deterministic GUID from SHA-256 of the pipeline, document key, chunk text and
   /// occurrence. A GUID is a valid key in SQL Server, Qdrant and Azure AI Search alike.
   /// </summary>
   /// <param name="pipeline">Pipeline name.</param>
   /// <param name="docKey">Document key.</param>
   /// <param name="text">Chunk text.</param>
   /// <param name="occurrence">1-based occurrence of this exact text in the document.</param>
   /// <returns>The chunk id.</returns>
   public static Guid ChunkId( string pipeline, string docKey, string text, int occurrence )
   {
      byte[] hash = SHA256.HashData( Encoding.UTF8.GetBytes( $"{pipeline}\u001F{docKey}\u001F{occurrence}\u001F{text}" ) );
      return new Guid( hash.AsSpan( 0, 16 ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Packs lines into pieces of at most <see cref="MAX_CHUNK_CHARS"/>, hard-splitting any
   /// single line that is longer than that.
   /// </summary>
   /// <param name="text">Document text.</param>
   /// <returns>The pieces.</returns>
   private static List<string> SplitText( string text )
   {
      if( text.Length <= MAX_CHUNK_CHARS )
      {
         return new List<string> { text };
      }

      string[] lines = text.Split( '\n' );
      string context = lines[0].Length <= MAX_CONTEXT_LINE_CHARS ? lines[0] : string.Empty;
      int budget = MAX_CHUNK_CHARS - ( context.Length + 1 );
      var pieces = new List<string>();
      var current = new StringBuilder();
      foreach( string line in lines.SelectMany( l => HardSplit( l, budget ) ) )
      {
         if( current.Length > 0 && current.Length + 1 + line.Length > budget )
         {
            pieces.Add( current.ToString() );
            current.Clear();
         }

         current.Append( current.Length > 0 ? "\n" : string.Empty ).Append( line );
      }

      if( current.Length > 0 )
      {
         pieces.Add( current.ToString() );
      }

      // Repeat the record's first line on every piece after the first, as context.
      return pieces.Select( ( p, i ) => i == 0 || context.Length == 0 ? p : $"{context}\n{p}" ).ToList();
   }

   /// <summary>
   /// Cuts one over-long line into pieces no longer than the budget, preferring to break at a
   /// space so words stay whole.
   /// </summary>
   /// <param name="line">The line.</param>
   /// <param name="budget">Maximum piece length.</param>
   /// <returns>One or more pieces.</returns>
   private static IEnumerable<string> HardSplit( string line, int budget )
   {
      int start = 0;
      while( line.Length - start > budget )
      {
         int cut = line.LastIndexOf( ' ', start + budget - 1, budget );
         int end = cut > start ? cut : start + budget;
         yield return line[start..end];
         start = cut > start ? end + 1 : end;
      }

      yield return line[start..];
   }

   #endregion Private Methods
}
