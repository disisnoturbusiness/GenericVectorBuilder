using System.Globalization;
using GenericVectorBuilder.Bench.Report;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// Builds the sources a sentence carries, in the reference grammar of P5's
/// <see cref="SourceResolver"/>, and formats the numbers a sentence prints so that the audit's
/// rounding agrees with the printing (decimal, half away from zero).
/// Why one place: a sentence and its source must print a number the same way, and a reference
/// typed in many places would drift from the grammar the resolver reads.
/// </summary>
public static class ConsolidateSources
{
   #region Public Methods

   /// <summary>A value of consolidated.json.</summary>
   /// <param name="path">JSON path in consolidated.json.</param>
   /// <param name="value">The value as the sentence prints it.</param>
   /// <param name="conversion">"bp-pct", "bp-ratio" or null.</param>
   /// <returns>The source.</returns>
   public static SentenceSource Consolidated( string path, string value, string? conversion = null )
   {
      return new SentenceSource( SourceKinds.CONSOLIDATED, "consolidated:" + path + ( conversion == null ? string.Empty : "|" + conversion ), value );
   }

   /// <summary>A single value of one run's results.json.</summary>
   /// <param name="folder">Run folder name.</param>
   /// <param name="path">JSON path in results.json.</param>
   /// <param name="value">The value as the sentence prints it.</param>
   /// <returns>The source.</returns>
   public static SentenceSource Result( string folder, string path, string value )
   {
      return new SentenceSource( SourceKinds.RESULTS, $"results@{folder}:{path}", value );
   }

   /// <summary>A single value every claim run's results.json holds alike.</summary>
   /// <param name="path">JSON path in results.json.</param>
   /// <param name="value">The value as the sentence prints it.</param>
   /// <returns>The source.</returns>
   public static SentenceSource ResultAll( string path, string value )
   {
      return new SentenceSource( SourceKinds.RESULTS, $"results:{path}", value );
   }

   /// <summary>A token that one run's field holds.</summary>
   /// <param name="folder">Run folder name.</param>
   /// <param name="path">JSON path in results.json.</param>
   /// <param name="token">The token.</param>
   /// <returns>The source.</returns>
   public static SentenceSource Token( string folder, string path, string token )
   {
      return new SentenceSource( SourceKinds.RESULTS, $"results@{folder}:{path}#{token}", token );
   }

   /// <summary>A token every claim run's field holds.</summary>
   /// <param name="path">JSON path in results.json.</param>
   /// <param name="token">The token.</param>
   /// <returns>The source.</returns>
   public static SentenceSource TokenAll( string path, string token )
   {
      return new SentenceSource( SourceKinds.RESULTS, $"results:{path}#{token}", token );
   }

   /// <summary>A token on a code line of a repository file.</summary>
   /// <param name="path">Repository path.</param>
   /// <param name="token">The token.</param>
   /// <returns>The source.</returns>
   public static SentenceSource File( string path, string token )
   {
      return new SentenceSource( SourceKinds.FILE, $"{path}#{token}", token );
   }

   /// <summary>A quote from a saved page or text file of the repository.</summary>
   /// <param name="path">Repository path.</param>
   /// <param name="quote">The quote, inside one text node of the file.</param>
   /// <returns>The source.</returns>
   public static SentenceSource Doc( string path, string quote )
   {
      return new SentenceSource( SourceKinds.DOC, $"doc:{path}#{quote}", quote );
   }

   /// <summary>A verbatim quote of one run's recorded text field.</summary>
   /// <param name="folder">Run folder name.</param>
   /// <param name="path">JSON path in results.json.</param>
   /// <param name="text">The field's text, which is also the sentence's text.</param>
   /// <returns>The source.</returns>
   public static SentenceSource Quote( string folder, string path, string text )
   {
      return new SentenceSource( SourceKinds.QUOTE, $"results@{folder}:{path}", text );
   }

   /// <summary>The source of a fact row, as P5's resolver builds it from the sheet's source string.</summary>
   /// <param name="fact">The fact.</param>
   /// <returns>The source.</returns>
   public static SentenceSource Fact( WhyFact fact )
   {
      return SourceResolver.FromFactSource( fact.Source );
   }

   /// <summary>
   /// A number rounded half away from zero to a number of decimals, from its round-trip text, as the audit rounds a source value.
   /// </summary>
   /// <param name="value">The number.</param>
   /// <param name="decimals">Decimals shown.</param>
   /// <returns>The text.</returns>
   public static string Number( double value, int decimals )
   {
      decimal exact = decimal.Parse( value.ToString( "R", CultureInfo.InvariantCulture ), NumberStyles.Float, CultureInfo.InvariantCulture );
      return decimal.Round( exact, decimals, MidpointRounding.AwayFromZero ).ToString( "F" + decimals.ToString( CultureInfo.InvariantCulture ), CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// A number as written with the decimals it has and no more, up to four: 0.14 for 0.14, 0.2095 for 0.2095.
   /// Why: a median of recorded three-decimal figures may end in a half, and a fixed number of decimals would round it away.
   /// </summary>
   /// <param name="value">The number.</param>
   /// <returns>The text.</returns>
   public static string Exact( double value )
   {
      decimal exact = decimal.Parse( value.ToString( "R", CultureInfo.InvariantCulture ), NumberStyles.Float, CultureInfo.InvariantCulture );
      return decimal.Round( exact, 4, MidpointRounding.AwayFromZero ).ToString( "0.####", CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// A whole number as text.
   /// </summary>
   /// <param name="value">The number.</param>
   /// <returns>The text.</returns>
   public static string Whole( long value )
   {
      return value.ToString( CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// Basis points as a percentage without the sign: "35" for 3500, "29.08" for 2908.
   /// </summary>
   /// <param name="bp">Basis points.</param>
   /// <returns>The text.</returns>
   public static string Percent( int bp )
   {
      return bp % 100 == 0 ? Whole( bp / 100 ) : ( bp / 100m ).ToString( "0.00", CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// Fractional basis points (as the observer writes them) as a percentage with two decimals.
   /// </summary>
   /// <param name="bp">Basis points.</param>
   /// <returns>The text.</returns>
   public static string Percent( double bp )
   {
      return Number( bp / 100.0, 2 );
   }

   /// <summary>
   /// Basis points above level as a ratio with two decimals: "1.35" for 3500.
   /// </summary>
   /// <param name="bp">Basis points.</param>
   /// <returns>The text.</returns>
   public static string Ratio( int bp )
   {
      return ( 1m + bp / 10000m ).ToString( "0.00", CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// Names joined as "a", "a and b" or "a, b and c".
   /// </summary>
   /// <param name="names">Names.</param>
   /// <returns>The text.</returns>
   public static string List( IReadOnlyList<string> names )
   {
      return names.Count switch
      {
         0 => string.Empty,
         1 => names[0],
         _ => string.Join( ", ", names.Take( names.Count - 1 ) ) + " and " + names[^1],
      };
   }

   #endregion Public Methods
}
