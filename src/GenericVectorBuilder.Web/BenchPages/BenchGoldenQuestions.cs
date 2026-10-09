using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// One question of the questions file, as the Golden Questions page shows it.
/// </summary>
/// <param name="Id">The question's id in the file.</param>
/// <param name="Question">The question's text.</param>
/// <param name="Category">The question's type, the file's Category.</param>
/// <param name="Note">The file's Notes for the question when they are short and not internal (see <see cref="BenchGoldenQuestions.NoteToShow"/>); otherwise null.</param>
/// <param name="Files">The files a correct answer should return, the file's Relevant list in the file's order.</param>
internal sealed record GoldenQuestion( string Id, string Question, string Category, string? Note, IReadOnlyList<string> Files );

/// <summary>
/// The body of the Golden Questions page: the questions the speed test ran, read from the questions file that the published set's runs recorded, as a table
/// of the question, its type and the files a correct answer should return.
/// Why read from the file and not typed in: the page states no fact of its own. The path is the one the set records for its queries (the path the header of
/// the Vector search benchmark page prints), the questions are the file's, in the file's order, and the number in the first sentence is the number of
/// questions read.
/// Why the file is held to what the runs recorded: the page says these are the questions every engine answered, which is true only of the file the runs
/// read. When the set records the hash of that file and the file on disk has another, or the set records another number of queries than the file holds, the
/// page shows an error notice and no table, so a file that was edited after the runs is never shown as what they ran.
/// Why a failure is a notice and never an empty table: a missing, unreadable or malformed file must be seen as such; the notice names the file or the field.
/// </summary>
public static class BenchGoldenQuestions
{
   #region Data Members

   /// <summary>The longest note, in characters, the page prints under a question.</summary>
   public const int MAX_NOTE_CHARS = 100;

   private const string CLASS_ERRORS = "errors";
   private const string COPY_HASH_FIELD = "copySha256";
   private const int SHORT_HASH = 12;

   /// <summary>
   /// Words that mark a note as the labelling team's own record (who graded, which model, what was overridden) and not something for a reader of the page.
   /// </summary>
   private static readonly Regex INTERNAL_NOTE = new( @"\b(label(?:l)?ed|labelling|labeling|grade[sd]?|graders?|judge[sd]?|author|blind|sonnet|opus|haiku|claude|gpt|overrid\w*|pooled|stored runs)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled );

   /// <summary>
   /// A refusal the page prints as the sentence it carries: the set records no file, the file is not the file the runs read, or it holds another number of
   /// questions than the runs ran.
   /// </summary>
   private sealed class Refusal : Exception
   {
      /// <summary>
      /// Creates a refusal.
      /// </summary>
      /// <param name="message">The sentence to print.</param>
      public Refusal( string message ) : base( message )
      {
      }
   }

   /// <summary>The questions read, the file they were read from, and whether the file's hash was checked against the one the runs recorded.</summary>
   private sealed record Loaded( string Source, IReadOnlyList<GoldenQuestion> Questions, bool HashChecked );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The link at the top of the page back to the Vector search benchmark page.
   /// </summary>
   /// <returns>HTML fragment: one paragraph.</returns>
   public static string BackLink()
   {
      return $"<p class=\"bench-golden-back\"><a href=\"{BenchRoutes.BENCHMARK}\">{BenchFormat.Enc( BenchLegends.L_GOLDEN_BACK )} {BenchFormat.Enc( BenchRoutes.BENCHMARK_NAME )}</a></p>";
   }

   /// <summary>
   /// What the page shows under its heading for a consolidated.json: the first sentence, the path the questions were read from, and the table; or an error
   /// notice, with no table, when the set is not in the v8 shape, records no questions file, the file is missing, unreadable or malformed, the file is not the
   /// file the runs read, or it holds another number of questions than the runs ran.
   /// </summary>
   /// <param name="json">The text of the published set's consolidated.json.</param>
   /// <returns>HTML fragment.</returns>
   public static string Block( string json )
   {
      try
      {
         return Table( Load( json ) );
      }
      catch( Refusal refusal )
      {
         return Error( refusal.Message );
      }
      catch( Exception ex ) when( ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException )
      {
         return Error( $"{BenchLegends.L_GOLDEN_UNREADABLE}: {ex.Message}" );
      }
   }

   /// <summary>
   /// An error notice in the page's own markup, for a message that explains why the questions are not shown.
   /// </summary>
   /// <param name="message">The message, as plain text.</param>
   /// <returns>HTML fragment: one paragraph.</returns>
   public static string Error( string message )
   {
      return $"<p class=\"{CLASS_ERRORS}\" data-notice=\"golden-questions\">{BenchFormat.Enc( message )}</p>";
   }

   /// <summary>
   /// The note to print under a question: the file's Notes when they are short (at most <see cref="MAX_NOTE_CHARS"/> characters) and carry none of the words of
   /// the labelling record (who graded, which model, what was overridden); otherwise null.
   /// </summary>
   /// <param name="notes">The file's Notes, or null.</param>
   /// <returns>The note, trimmed, or null.</returns>
   public static string? NoteToShow( string? notes )
   {
      string trimmed = ( notes ?? string.Empty ).Trim();
      return trimmed.Length == 0 || trimmed.Length > MAX_NOTE_CHARS || INTERNAL_NOTE.IsMatch( trimmed ) ? null : trimmed;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Reads the questions for a set: finds the questions file the set's runs recorded, checks it against what the runs recorded of it, and parses it.
   /// </summary>
   /// <param name="json">The text of the published set's consolidated.json.</param>
   /// <returns>The questions and where they came from.</returns>
   private static Loaded Load( string json )
   {
      if( BenchConsolidatedReader.Detect( json ) != BenchShape.Consolidated )
      {
         throw new Refusal( BenchLegends.OLDER_FORMAT );
      }

      BenchConsolidated model = BenchConsolidatedReader.Read( json );
      using JsonDocument set = JsonDocument.Parse( json );
      string? recorded = BenchHeader.RecordedQueries( set.RootElement );
      if( !BenchHeader.IsGolden( recorded ) || BenchHeader.QueryFileOf( recorded ) is not { } path )
      {
         throw new Refusal( BenchLegends.L_GOLDEN_NO_FILE );
      }

      if( !Path.IsPathRooted( path ) || path.IndexOfAny( Path.GetInvalidPathChars() ) >= 0 )
      {
         throw new InvalidDataException( $"The set records {path} as the questions file, which is not a full path." );
      }

      string? recordedHash = BenchJson.Get( set.RootElement, "queries" ) is { ValueKind: JsonValueKind.Object } queries ? BenchJson.Text( queries, COPY_HASH_FIELD, "queries" ) : null;
      byte[] bytes = ReadCapped( path );
      string hash = Convert.ToHexString( SHA256.HashData( bytes ) ).ToLowerInvariant();
      if( !string.IsNullOrEmpty( recordedHash ) && !string.Equals( hash, recordedHash, StringComparison.OrdinalIgnoreCase ) )
      {
         throw new Refusal( $"{BenchLegends.L_GOLDEN_CHANGED_A} {hash[..SHORT_HASH]} {BenchLegends.L_GOLDEN_CHANGED_B} {Short( recordedHash )}." );
      }

      IReadOnlyList<GoldenQuestion> questions = Parse( bytes );
      if( questions.Count == 0 )
      {
         throw new Refusal( BenchLegends.L_GOLDEN_EMPTY );
      }

      string? count = model.Sentences.FirstOrDefault( s => s.Slot == BenchHeader.DATA_SLOT )?.Sources.FirstOrDefault( s => s.Ref == BenchHeader.REF_QUERY_COUNT ) is { Value.Length: > 0 } source ? source.Value : null;
      if( count != null && count != questions.Count.ToString( CultureInfo.InvariantCulture ) )
      {
         throw new Refusal( $"{BenchLegends.L_GOLDEN_COUNT_A} {questions.Count.ToString( CultureInfo.InvariantCulture )} {BenchLegends.L_GOLDEN_COUNT_B} {count} {BenchLegends.L_GOLDEN_COUNT_C}" );
      }

      return new Loaded( path, questions, !string.IsNullOrEmpty( recordedHash ) );
   }

   /// <summary>
   /// Reads a file's bytes, refusing a file over the size the pages read.
   /// </summary>
   /// <param name="path">The file's path.</param>
   /// <returns>The bytes.</returns>
   private static byte[] ReadCapped( string path )
   {
      long size = new FileInfo( path ).Length;
      if( size > BenchRunList.MAX_FILE_BYTES )
      {
         throw new InvalidDataException( $"{Path.GetFileName( path )} is {size:N0} bytes, over the {BenchRunList.MAX_FILE_BYTES:N0} byte limit." );
      }

      return File.ReadAllBytes( path );
   }

   /// <summary>
   /// Parses the questions file: a JSON array whose items each hold an Id, a Question, a Category and a Relevant list of objects with a FilePath, and,
   /// optionally, Notes. A field that is missing or of another type fails with its place in the file.
   /// </summary>
   /// <param name="bytes">The file's bytes.</param>
   /// <returns>The questions in the file's order.</returns>
   private static IReadOnlyList<GoldenQuestion> Parse( byte[] bytes )
   {
      ReadOnlyMemory<byte> utf8 = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes.AsMemory( 3 ) : bytes;
      using JsonDocument doc = JsonDocument.Parse( utf8 );
      if( doc.RootElement.ValueKind != JsonValueKind.Array )
      {
         throw new InvalidDataException( "The questions file is not a list of questions." );
      }

      var questions = new List<GoldenQuestion>();
      int index = 0;
      foreach( JsonElement item in doc.RootElement.EnumerateArray() )
      {
         string place = $"questions[{index}]";
         JsonElement relevant = BenchJson.RequireArray( item, "Relevant", place );
         var files = new List<string>();
         int fileIndex = 0;
         foreach( JsonElement file in relevant.EnumerateArray() )
         {
            files.Add( BenchJson.RequireText( file, "FilePath", $"{place}.Relevant[{fileIndex++}]" ) );
         }

         if( files.Count == 0 )
         {
            throw new InvalidDataException( $"{place}.Relevant lists no file." );
         }

         questions.Add( new GoldenQuestion( BenchJson.RequireText( item, "Id", place ), BenchJson.RequireText( item, "Question", place ), BenchJson.RequireText( item, "Category", place ),
            NoteToShow( BenchJson.Text( item, "Notes", place ) ), files ) );
         index++;
      }

      return questions;
   }

   /// <summary>
   /// The first sentence, the line with the path the questions were read from, and the table of the questions.
   /// </summary>
   /// <param name="loaded">The questions read.</param>
   /// <returns>HTML fragment.</returns>
   private static string Table( Loaded loaded )
   {
      var html = new StringBuilder();
      html.Append( $"<p class=\"bench-golden-lead\">{BenchFormat.Enc( BenchLegends.L_GOLDEN_INTRO_A )} <span class=\"bench-golden-count\">{loaded.Questions.Count.ToString( CultureInfo.InvariantCulture )}</span> {BenchFormat.Enc( BenchLegends.L_GOLDEN_INTRO_B )}</p>" );
      string match = loaded.HashChecked ? " " + BenchFormat.Enc( BenchLegends.L_GOLDEN_HASH_MATCH ) : string.Empty;
      html.Append( $"<p class=\"bench-header-from muted\">{BenchFormat.Enc( BenchLegends.L_GOLDEN_FROM )} <code>{BenchFormat.Enc( loaded.Source )}</code>.{match}</p>" );
      html.Append( "<div class=\"preview\"><table class=\"bench-table bench-golden-table\"><thead><tr>" );
      html.Append( $"<th class=\"n\">{BenchFormat.Enc( BenchLegends.L_GOLDEN_COL_NUMBER )}</th><th>{BenchFormat.Enc( BenchLegends.L_GOLDEN_COL_QUESTION )}</th>" );
      html.Append( $"<th>{BenchFormat.Enc( BenchLegends.L_GOLDEN_COL_TYPE )}</th><th>{BenchFormat.Enc( BenchLegends.L_GOLDEN_COL_FILES )}</th></tr></thead><tbody>" );
      for( int i = 0; i < loaded.Questions.Count; i++ )
      {
         html.Append( Row( i + 1, loaded.Questions[i] ) );
      }

      return html.Append( "</tbody></table></div>" ).ToString();
   }

   /// <summary>
   /// One question's row: its number, its text (with its note under it when it has one to show), its type and the files a correct answer should return.
   /// Why the type and files cells carry their column heading as data-label: at phone width the stylesheet stacks each row into a card and prints the label
   /// before the cell, so a narrow column is never left to break a path at every few characters.
   /// </summary>
   /// <param name="number">The question's place in the file, from one.</param>
   /// <param name="question">The question.</param>
   /// <returns>HTML fragment.</returns>
   private static string Row( int number, GoldenQuestion question )
   {
      string note = question.Note == null ? string.Empty : $" <small class=\"muted bench-golden-note\">{BenchFormat.Enc( question.Note )}</small>";
      string files = string.Concat( question.Files.Select( f => $"<li><code>{BenchFormat.Enc( f )}</code></li>" ) );
      string typeLabel = BenchFormat.Enc( BenchLegends.L_GOLDEN_COL_TYPE );
      string filesLabel = BenchFormat.Enc( BenchLegends.L_GOLDEN_COL_FILES );
      return $"<tr class=\"bench-golden-row\" data-id=\"{BenchFormat.Enc( question.Id )}\"><td class=\"n\">{number.ToString( CultureInfo.InvariantCulture )}</td>"
         + $"<td class=\"bench-golden-question\">{BenchFormat.Enc( question.Question )}{note}</td><td class=\"bench-golden-type\" data-label=\"{typeLabel}\">{BenchFormat.Enc( question.Category )}</td>"
         + $"<td class=\"bench-golden-filecell\" data-label=\"{filesLabel}\"><ul class=\"bench-golden-files\">{files}</ul></td></tr>";
   }

   /// <summary>
   /// The first characters of a hash, as a person reads it.
   /// </summary>
   /// <param name="hash">The hash.</param>
   /// <returns>Up to twelve characters.</returns>
   private static string Short( string hash )
   {
      return hash.Length > SHORT_HASH ? hash[..SHORT_HASH] : hash;
   }

   #endregion Private Methods
}
