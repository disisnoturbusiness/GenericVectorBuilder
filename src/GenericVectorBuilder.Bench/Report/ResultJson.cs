using System.Globalization;
using System.Text.Json;

namespace GenericVectorBuilder.Bench.Report;

/// <summary>
/// Forgiving readers over a results.json element: a property that is absent, null, or of the
/// wrong kind reads as null instead of throwing.
/// Why: result folders from older harness versions lack fields, and NaN is written as the text
/// "NaN"; the consolidate command must load them all and show what is missing.
/// Names are matched exactly first, then ignoring case, so a writer that used PascalCase still
/// reads.
/// </summary>
public static class ResultJson
{
   #region Data Members

   /// <summary>The start of a default-search pass name ("default@8"); what follows is the number of searchers.</summary>
   private const string PASS_PREFIX = "default@";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// A child property, or null when absent or JSON null.
   /// </summary>
   /// <param name="element">An object.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The child, or null.</returns>
   public static JsonElement? Child( JsonElement element, string name )
   {
      if( element.ValueKind != JsonValueKind.Object )
      {
         return null;
      }

      if( element.TryGetProperty( name, out JsonElement exact ) )
      {
         return exact.ValueKind == JsonValueKind.Null ? null : exact;
      }

      foreach( JsonProperty property in element.EnumerateObject() )
      {
         if( string.Equals( property.Name, name, StringComparison.OrdinalIgnoreCase ) )
         {
            return property.Value.ValueKind == JsonValueKind.Null ? null : property.Value;
         }
      }

      return null;
   }

   /// <summary>
   /// A string property, or null.
   /// </summary>
   /// <param name="element">An object.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The text, or null.</returns>
   public static string? Text( JsonElement element, string name )
   {
      return Child( element, name ) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
   }

   /// <summary>
   /// A number property, or null when absent, not a number, or NaN/infinity (written as text).
   /// </summary>
   /// <param name="element">An object.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The number, or null.</returns>
   public static double? Number( JsonElement element, string name )
   {
      return Child( element, name ) is JsonElement value ? AsNumber( value ) : null;
   }

   /// <summary>
   /// A whole-number property, or null.
   /// </summary>
   /// <param name="element">An object.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The number, or null.</returns>
   public static int? Int( JsonElement element, string name )
   {
      return Child( element, name ) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32( out int number ) ? number : null;
   }

   /// <summary>
   /// A 64-bit whole-number property, or null.
   /// </summary>
   /// <param name="element">An object.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The number, or null.</returns>
   public static long? Long( JsonElement element, string name )
   {
      return Child( element, name ) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64( out long number ) ? number : null;
   }

   /// <summary>
   /// A true/false property, or null.
   /// </summary>
   /// <param name="element">An object.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The value, or null.</returns>
   public static bool? Bool( JsonElement element, string name )
   {
      return Child( element, name ) is JsonElement value && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
   }

   /// <summary>
   /// An array of strings, or null when absent. Non-string items are skipped.
   /// </summary>
   /// <param name="element">An object.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The strings, or null.</returns>
   public static IReadOnlyList<string>? Texts( JsonElement element, string name )
   {
      return Child( element, name ) is { ValueKind: JsonValueKind.Array } array
         ? array.EnumerateArray().Where( i => i.ValueKind == JsonValueKind.String ).Select( i => i.GetString()! ).ToList()
         : null;
   }

   /// <summary>
   /// An array of whole numbers, or null when absent.
   /// </summary>
   /// <param name="element">An object.</param>
   /// <param name="name">Property name.</param>
   /// <returns>The numbers, or null.</returns>
   public static IReadOnlyList<int>? Ints( JsonElement element, string name )
   {
      return Child( element, name ) is { ValueKind: JsonValueKind.Array } array
         ? array.EnumerateArray().Where( i => i.ValueKind == JsonValueKind.Number && i.TryGetInt32( out _ ) ).Select( i => i.GetInt32() ).ToList()
         : null;
   }

   /// <summary>
   /// An object keyed by concurrency level ("1", "8"; "default@8" counts as 8) holding numbers,
   /// e.g. search.qps.
   /// </summary>
   /// <param name="element">An object.</param>
   /// <param name="name">Property name.</param>
   /// <returns>Value by level; empty when absent.</returns>
   public static Dictionary<int, double> LevelMap( JsonElement element, string name )
   {
      var map = new Dictionary<int, double>();
      if( Child( element, name ) is not { ValueKind: JsonValueKind.Object } obj )
      {
         return map;
      }

      foreach( JsonProperty property in obj.EnumerateObject() )
      {
         string key = property.Name.StartsWith( PASS_PREFIX, StringComparison.Ordinal ) ? property.Name[PASS_PREFIX.Length..] : property.Name;
         if( int.TryParse( key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int level ) && AsNumber( property.Value ) is double value )
         {
            map[level] = value;
         }
      }

      return map;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// A finite number from a JSON number, or null (NaN and infinity are written as text and read as null).
   /// </summary>
   /// <param name="value">The element.</param>
   /// <returns>The number, or null.</returns>
   private static double? AsNumber( JsonElement value )
   {
      return value.ValueKind == JsonValueKind.Number && value.TryGetDouble( out double number ) && double.IsFinite( number ) ? number : null;
   }

   #endregion Private Methods
}
