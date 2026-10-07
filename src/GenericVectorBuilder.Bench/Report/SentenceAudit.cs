using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Bench.Report;

/// <summary>
/// One thing the audit found wrong with a sentence.
/// </summary>
/// <param name="Slot">The sentence's slot.</param>
/// <param name="Rule">Which rule failed: structure, source, number, banned, length or dash.</param>
/// <param name="Message">What is wrong, with the offending text or reference.</param>
public sealed record AuditFailure( string Slot, string Rule, string Message );

/// <summary>
/// Checks every sentence the report prints before anything is written: each source reads again
/// and agrees with what the sentence says, every number is bound to a source, no banned word
/// appears, and no sentence is too long.
/// Why: the first v8 design died on hand-typed sentences that were false (a method fact stated
/// wrongly, a cause nobody measured). The rule is structural now: prose exists only as sentences
/// with sources, and this audit re-derives every claim from the raw files. A failure stops the
/// report; nothing is patched around it.
/// Rules, in the order the design lists them:
///   (a) source: each source resolves again and equals the value the sentence carries (numbers
///       may be rounded to the decimals shown; file, doc and absent sources must match the token or quote).
///   (b) number: every number in the text (digits with . , % x) equals a source value as printed or
///       is a whole number inside a source's token or quote; a token with digits that is not a plain
///       number (a date, a folder name, 0x1e1e, a version) must equal or sit inside a source value,
///       except the metric and session names p50, QPS@8, v7 and the like.
///   (c) banned: no word of <see cref="BannedWords"/>.
///   (d) length: at most 34 words in the headline and 35 in every other sentence.
///   (e) dash: no em or en dash anywhere.
/// A sentence with one source of kind quote is a verbatim quote of a recorded field: rule (a)
/// checks that the text lies inside the raw field, and rules (b) to (d) do not apply, because
/// recorded text is the tool's wording and runs from 12 to over 300 words.
/// </summary>
public static class SentenceAudit
{
   #region Data Members

   /// <summary>The slot whose word limit is shorter; a slot is the headline when it equals this or starts with it and a dot.</summary>
   public const string HEADLINE_SLOT = "headline";

   /// <summary>Most words in a headline.</summary>
   public const int HEADLINE_MAX_WORDS = 34;

   /// <summary>Most words in any other sentence.</summary>
   public const int SENTENCE_MAX_WORDS = 35;

   private const string RULE_STRUCTURE = "structure";
   private const string RULE_SOURCE = "source";
   private const string RULE_NUMBER = "number";
   private const string RULE_BANNED = "banned";
   private const string RULE_LENGTH = "length";
   private const string RULE_DASH = "dash";

   private static readonly TimeSpan MATCH_TIMEOUT = TimeSpan.FromSeconds( 5 );
   private static readonly Regex PLAIN_NUMBER = new( @"^\d[\d,]*(\.\d+)?[%x]?$", RegexOptions.Compiled, MATCH_TIMEOUT );
   private static readonly Regex METRIC_NAME = new( @"^(p(50|95|99)|qps@\d+|@\d+|v\d+|recall@\d+|ndcg@\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase, MATCH_TIMEOUT );
   private static readonly char[] TOKEN_EDGES = { '(', ')', '[', ']', '"', '\'', ',', ';', ':', '.', '!', '?' };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Audits sentences against their sources.
   /// </summary>
   /// <param name="sentences">Every sentence the report will print.</param>
   /// <param name="resolver">Reads a source reference again from the raw files.</param>
   /// <returns>Every failure found, in sentence order; empty when all hold.</returns>
   /// <exception cref="ArgumentNullException">A parameter is null.</exception>
   public static IReadOnlyList<AuditFailure> Check( IEnumerable<Sentence> sentences, ISourceResolver resolver )
   {
      ArgumentNullException.ThrowIfNull( sentences );
      ArgumentNullException.ThrowIfNull( resolver );
      var failures = new List<AuditFailure>();
      foreach( Sentence sentence in sentences )
      {
         if( !CheckStructure( sentence, failures ) )
         {
            continue;
         }

         bool quote = sentence.Sources.Any( s => s.Kind == SourceKinds.QUOTE );
         CheckSources( sentence, quote, resolver, failures );
         CheckDashes( sentence, failures );
         if( quote )
         {
            continue;
         }

         CheckNumbers( sentence, failures );
         foreach( string word in BannedWords.Find( sentence.Text ) )
         {
            failures.Add( new AuditFailure( sentence.Slot, RULE_BANNED, $"'{word}' is banned in generated text: {sentence.Text}" ) );
         }

         int limit = IsHeadline( sentence.Slot ) ? HEADLINE_MAX_WORDS : SENTENCE_MAX_WORDS;
         int words = WordCount( sentence.Text );
         if( words > limit )
         {
            failures.Add( new AuditFailure( sentence.Slot, RULE_LENGTH, $"{words} words, the limit for this slot is {limit}: {sentence.Text}" ) );
         }
      }

      return failures;
   }

   /// <summary>
   /// How many words a sentence has: white-space separated pieces that hold a letter or a digit.
   /// </summary>
   /// <param name="text">The sentence.</param>
   /// <returns>The word count.</returns>
   public static int WordCount( string text )
   {
      ArgumentNullException.ThrowIfNull( text );
      return text.Split( new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries ).Count( piece => piece.Any( char.IsLetterOrDigit ) );
   }

   /// <summary>
   /// Whether a value read from a raw file agrees with the value a sentence prints: equal text, or
   /// the same number once the raw one is rounded to the decimals the sentence shows (half away
   /// from zero). Thousands commas and a trailing % or x are ignored.
   /// </summary>
   /// <param name="resolved">The value read from the source.</param>
   /// <param name="shown">The value the sentence prints.</param>
   /// <returns>True when they agree.</returns>
   public static bool Agrees( string resolved, string shown )
   {
      ArgumentNullException.ThrowIfNull( resolved );
      ArgumentNullException.ThrowIfNull( shown );
      if( string.Equals( resolved, shown, StringComparison.Ordinal ) )
      {
         return true;
      }

      if( !TryNumber( shown, out decimal shownNumber, out int decimals ) || !TryNumber( resolved, out decimal resolvedNumber, out _ ) )
      {
         return false;
      }

      return decimal.Round( resolvedNumber, decimals, MidpointRounding.AwayFromZero ) == shownNumber;
   }

   /// <summary>
   /// The failures as one line each, for a log or an exception message.
   /// </summary>
   /// <param name="failures">The failures.</param>
   /// <returns>Lines of "slot [rule] message".</returns>
   public static string Format( IEnumerable<AuditFailure> failures )
   {
      ArgumentNullException.ThrowIfNull( failures );
      return string.Join( Environment.NewLine, failures.Select( f => $"{f.Slot} [{f.Rule}] {f.Message}" ) );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Whether a slot is the headline.
   /// </summary>
   /// <param name="slot">The slot.</param>
   /// <returns>True for "headline" and "headline.something".</returns>
   private static bool IsHeadline( string slot )
   {
      return slot.Equals( HEADLINE_SLOT, StringComparison.OrdinalIgnoreCase ) || slot.StartsWith( HEADLINE_SLOT + ".", StringComparison.OrdinalIgnoreCase );
   }

   /// <summary>
   /// Checks the shape of a sentence: a slot, a text, a source list, only known kinds and a value on every source.
   /// </summary>
   /// <param name="sentence">The sentence.</param>
   /// <param name="failures">Where failures are added.</param>
   /// <returns>True when the shape is sound enough for the other rules to run.</returns>
   private static bool CheckStructure( Sentence sentence, List<AuditFailure> failures )
   {
      string slot = string.IsNullOrWhiteSpace( sentence?.Slot ) ? "(no slot)" : sentence!.Slot;
      if( sentence is null || string.IsNullOrWhiteSpace( sentence.Slot ) || string.IsNullOrWhiteSpace( sentence.Text ) || sentence.Sources is null )
      {
         failures.Add( new AuditFailure( slot, RULE_STRUCTURE, "a sentence needs a slot, a text and a source list" ) );
         return false;
      }

      bool sound = true;
      foreach( SentenceSource source in sentence.Sources )
      {
         if( source is null || !SourceKinds.ALL.Contains( source.Kind ) || string.IsNullOrWhiteSpace( source.Ref ) || source.Value is null )
         {
            failures.Add( new AuditFailure( slot, RULE_STRUCTURE, $"a source needs a kind from {string.Join( ", ", SourceKinds.ALL )}, a reference and a value: {source?.Kind} {source?.Ref}" ) );
            sound = false;
         }
      }

      if( sound && sentence.Sources.Any( s => s.Kind == SourceKinds.QUOTE ) && ( sentence.Sources.Count != 1 || sentence.Sources[0].Value != sentence.Text ) )
      {
         failures.Add( new AuditFailure( slot, RULE_STRUCTURE, "a quote sentence has exactly one source, of kind quote, and its value is the text" ) );
         sound = false;
      }

      return sound;
   }

   /// <summary>
   /// Rule (a): every source resolves again and equals what the sentence carries.
   /// </summary>
   /// <param name="sentence">The sentence.</param>
   /// <param name="quote">True for a quote sentence.</param>
   /// <param name="resolver">Reads the sources.</param>
   /// <param name="failures">Where failures are added.</param>
   private static void CheckSources( Sentence sentence, bool quote, ISourceResolver resolver, List<AuditFailure> failures )
   {
      foreach( SentenceSource source in sentence.Sources )
      {
         SourceResolution resolution = resolver.Resolve( source );
         if( !resolution.Found || resolution.Value is null )
         {
            failures.Add( new AuditFailure( sentence.Slot, RULE_SOURCE, $"{source.Kind} {source.Ref} does not resolve: {resolution.Detail}" ) );
            continue;
         }

         bool agrees = source.Kind switch
         {
            SourceKinds.RESULTS or SourceKinds.CONSOLIDATED => Agrees( resolution.Value, source.Value ),
            SourceKinds.QUOTE => Collapse( resolution.Value ).Contains( Collapse( source.Value ), StringComparison.Ordinal ),
            _ => string.Equals( resolution.Value, source.Value, StringComparison.Ordinal ),
         };
         if( !agrees )
         {
            string said = quote ? "the recorded field does not contain the quoted text" : $"the source says '{resolution.Value}', the sentence says '{source.Value}'";
            failures.Add( new AuditFailure( sentence.Slot, RULE_SOURCE, $"{source.Kind} {source.Ref} is stale or wrong: {said}" ) );
         }
      }
   }

   /// <summary>
   /// Rule (e): no em or en dash, which the owner strips from everything by hand.
   /// </summary>
   /// <param name="sentence">The sentence.</param>
   /// <param name="failures">Where failures are added.</param>
   private static void CheckDashes( Sentence sentence, List<AuditFailure> failures )
   {
      if( sentence.Text.Contains( '\u2014' ) || sentence.Text.Contains( '\u2013' ) )
      {
         failures.Add( new AuditFailure( sentence.Slot, RULE_DASH, $"an em or en dash is not allowed: {sentence.Text}" ) );
      }
   }

   /// <summary>
   /// Rule (b): every number, and every token with digits, in the text is bound to a source.
   /// </summary>
   /// <param name="sentence">The sentence.</param>
   /// <param name="failures">Where failures are added.</param>
   private static void CheckNumbers( Sentence sentence, List<AuditFailure> failures )
   {
      foreach( string piece in sentence.Text.Split( new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries ) )
      {
         string token = piece.Trim( TOKEN_EDGES );
         if( !token.Any( char.IsDigit ) )
         {
            continue;
         }

         bool bound = PLAIN_NUMBER.IsMatch( token ) ? IsNumberBound( token, sentence.Sources ) : IsIdentifierBound( token, sentence.Sources );
         if( !bound )
         {
            failures.Add( new AuditFailure( sentence.Slot, RULE_NUMBER, $"'{token}' is not bound to any source of the sentence: {sentence.Text}" ) );
         }
      }
   }

   /// <summary>
   /// Whether a plain number equals a source value or sits inside a source's token or quote.
   /// </summary>
   /// <param name="number">The number as printed, such as 35%, 1.35x or 1,043.</param>
   /// <param name="sources">The sentence's sources.</param>
   /// <returns>True when bound.</returns>
   private static bool IsNumberBound( string number, IReadOnlyList<SentenceSource> sources )
   {
      string plain = number.Replace( ",", string.Empty ).TrimEnd( '%', 'x' );
      foreach( SentenceSource source in sources )
      {
         if( TryNumber( source.Value, out decimal sourceNumber, out _ ) && decimal.TryParse( plain, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal printed ) && sourceNumber == printed )
         {
            return true;
         }

         string haystack = source.Value.Replace( ",", string.Empty );
         if( Regex.IsMatch( haystack, @"(?<![0-9.])" + Regex.Escape( plain ) + @"(?![0-9]|\.[0-9])", RegexOptions.CultureInvariant, MATCH_TIMEOUT ) )
         {
            return true;
         }
      }

      return false;
   }

   /// <summary>
   /// Whether a token with digits that is not a plain number is a metric or session name or sits in a source value.
   /// </summary>
   /// <param name="token">The token.</param>
   /// <param name="sources">The sentence's sources.</param>
   /// <returns>True when allowed.</returns>
   private static bool IsIdentifierBound( string token, IReadOnlyList<SentenceSource> sources )
   {
      return METRIC_NAME.IsMatch( token ) || sources.Any( s => s.Value.Contains( token, StringComparison.OrdinalIgnoreCase ) );
   }

   /// <summary>
   /// Reads a printed number: thousands commas and a trailing % or x are ignored.
   /// </summary>
   /// <param name="text">The text.</param>
   /// <param name="number">The value.</param>
   /// <param name="decimals">How many digits follow the decimal point as written.</param>
   /// <returns>True when the text is a number.</returns>
   private static bool TryNumber( string text, out decimal number, out int decimals )
   {
      string plain = text.Trim().Replace( ",", string.Empty ).TrimEnd( '%', 'x' );
      decimals = 0;
      if( !decimal.TryParse( plain, NumberStyles.Float, CultureInfo.InvariantCulture, out number ) )
      {
         return false;
      }

      int point = plain.IndexOf( '.' );
      int exponent = plain.IndexOfAny( new[] { 'e', 'E' } );
      decimals = point < 0 ? 0 : ( exponent < 0 ? plain.Length : exponent ) - point - 1;
      return true;
   }

   /// <summary>
   /// Collapses white space to single spaces, so a recorded field and a quote compare the same way.
   /// </summary>
   /// <param name="text">The text.</param>
   /// <returns>The collapsed text.</returns>
   private static string Collapse( string text )
   {
      var builder = new StringBuilder( text.Length );
      bool space = false;
      foreach( char c in text.Trim() )
      {
         if( char.IsWhiteSpace( c ) )
         {
            space = true;
            continue;
         }

         if( space )
         {
            builder.Append( ' ' );
            space = false;
         }

         builder.Append( c );
      }

      return builder.ToString();
   }

   #endregion Private Methods
}
