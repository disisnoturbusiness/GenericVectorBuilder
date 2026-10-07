using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Bench.Report;

/// <summary>
/// The one list of words and phrases that generated report text must not contain, and the one
/// rule for finding them: whole words, case-insensitive, with a space inside a phrase matching any
/// run of white space.
/// Why one list and one rule: the audit that gates the report, the tests and the fact checks
/// must agree on what is banned. The words are the ones that turn a measurement into a cause
/// (because, due to, since, bottleneck, overhead, waiting, share of, accounts for, explains,
/// percent of the latency, thanks to, leads to). This test measured request cost end to end and
/// did not isolate causes, so no generated sentence may claim one.
/// Where the rule does not apply: text the tool recorded and quotes of it are checked against
/// their source, not against this list, because a recorded durability note may legitimately say
/// "because".
/// </summary>
public static class BannedWords
{
   #region Data Members

   /// <summary>The banned words and phrases, lower case, in the order the design lists them.</summary>
   public static readonly IReadOnlyList<string> LIST = new[]
   {
      "because", "due to", "since", "bottleneck", "overhead", "waiting", "share of", "accounts for", "explains",
      "percent of the latency", "thanks to", "leads to",
   };

   /// <summary>Longest a single pattern match may take, so a pathological input fails loud instead of hanging the audit.</summary>
   private static readonly TimeSpan MATCH_TIMEOUT = TimeSpan.FromSeconds( 5 );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Every banned word or phrase that occurs in the text.
   /// </summary>
   /// <param name="text">Text to search.</param>
   /// <returns>The banned entries found, each once, in list order; empty when the text is clean.</returns>
   public static IReadOnlyList<string> Find( string text )
   {
      return FindIn( text, LIST );
   }

   /// <summary>
   /// True when the text holds at least one banned word or phrase.
   /// </summary>
   /// <param name="text">Text to search.</param>
   /// <returns>True when <see cref="Find"/> would return something.</returns>
   public static bool Contains( string text )
   {
      return Find( text ).Count > 0;
   }

   /// <summary>
   /// The rule itself, for any list of words or phrases: whole words, case-insensitive. A match
   /// must not be glued to a letter, digit or underscore on either side, so "since" does not match
   /// "sincere" and "waiting" does not match "awaiting". The fact checks use it with a longer list.
   /// </summary>
   /// <param name="text">Text to search.</param>
   /// <param name="words">Words or phrases to look for.</param>
   /// <returns>The entries of <paramref name="words"/> found, each once, in list order.</returns>
   /// <exception cref="ArgumentNullException">The text or the list is null.</exception>
   public static IReadOnlyList<string> FindIn( string text, IEnumerable<string> words )
   {
      ArgumentNullException.ThrowIfNull( text );
      ArgumentNullException.ThrowIfNull( words );
      var found = new List<string>();
      foreach( string word in words )
      {
         if( word.Length == 0 )
         {
            throw new ArgumentException( "A banned-word list holds an empty entry." );
         }

         string pattern = @"(?<![A-Za-z0-9_])" + string.Join( @"\s+", word.Split( ' ', StringSplitOptions.RemoveEmptyEntries ).Select( Regex.Escape ) ) + @"(?![A-Za-z0-9_])";
         if( Regex.IsMatch( text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MATCH_TIMEOUT ) )
         {
            found.Add( word );
         }
      }

      return found;
   }

   #endregion Public Methods
}
