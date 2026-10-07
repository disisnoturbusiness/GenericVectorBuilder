using System.Globalization;
using System.Net;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// How the pages print a number or a list of names, in one place.
/// Why one place: the same figure appears in several tables and must read the same in each, and a
/// server whose culture uses a comma for the decimal point must not change a page's numbers, so
/// every format here is invariant.
/// </summary>
public static class BenchFormat
{
   #region Public Methods

   /// <summary>
   /// HTML-encodes text for element content and attribute values.
   /// </summary>
   /// <param name="text">Text.</param>
   /// <returns>Encoded text.</returns>
   public static string Enc( string text )
   {
      return WebUtility.HtmlEncode( text );
   }

   /// <summary>
   /// A searches-per-second figure: whole, with thousands separators.
   /// </summary>
   /// <param name="value">Searches per second.</param>
   /// <returns>E.g. "4,558".</returns>
   public static string Count( double value )
   {
      return Math.Round( value, MidpointRounding.AwayFromZero ).ToString( "N0", CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// A latency in milliseconds with two decimals, or "-" when missing.
   /// </summary>
   /// <param name="ms">Milliseconds or null.</param>
   /// <returns>E.g. "0.89".</returns>
   public static string Ms( double? ms )
   {
      return ms == null ? "-" : ms.Value.ToString( "0.00", CultureInfo.InvariantCulture );
   }

   /// <summary>
   /// A figure of a metric: latency with two decimals, searches per second whole.
   /// </summary>
   /// <param name="value">The figure.</param>
   /// <param name="lowerIsBetter">True for a latency metric.</param>
   /// <returns>The text.</returns>
   public static string Figure( double value, bool lowerIsBetter )
   {
      return lowerIsBetter ? Ms( value ) : Count( value );
   }

   /// <summary>
   /// A move in basis points as a percentage with two decimals.
   /// </summary>
   /// <param name="bp">Basis points (100 is 1%).</param>
   /// <returns>E.g. "29.08%".</returns>
   public static string Percent( double bp )
   {
      return ( bp / 100.0 ).ToString( "0.00", CultureInfo.InvariantCulture ) + "%";
   }

   /// <summary>
   /// "a", "a and b" or "a, b and c".
   /// </summary>
   /// <param name="names">Names.</param>
   /// <returns>The joined text.</returns>
   public static string Join( IEnumerable<string> names )
   {
      List<string> list = names.ToList();
      return list.Count <= 1 ? string.Join( string.Empty, list ) : string.Join( ", ", list.Take( list.Count - 1 ) ) + " and " + list[^1];
   }

   /// <summary>
   /// A container image id without its "sha256:" prefix, cut to twelve characters, as a person reads it.
   /// </summary>
   /// <param name="id">The full id.</param>
   /// <returns>The short form; the id itself when it is already short.</returns>
   public static string ShortId( string id )
   {
      string bare = id.StartsWith( "sha256:", StringComparison.Ordinal ) ? id["sha256:".Length..] : id;
      return bare.Length > 12 ? bare[..12] : bare;
   }

   #endregion Public Methods
}
