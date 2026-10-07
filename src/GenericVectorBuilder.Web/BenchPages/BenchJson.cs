using System.Text.Json;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// Small readers for the JSON the benchmark writes, shared by every page reader here.
/// Why one helper: each reader needs the same questions (is this a finite number, is this a
/// string) and the same failure (a named path in the file, never a silent default), so the answers
/// live in one place and a malformed file always fails with the name of the field that is wrong.
/// Every failure is an <see cref="InvalidDataException"/>, which the endpoints catch and show as an
/// error block instead of a 500.
/// </summary>
internal static class BenchJson
{
   #region Data Members

   /// <summary>Largest whole number a double holds exactly (2 to the power 53), so converting a fractionless number to a long loses nothing.</summary>
   private const double MAX_EXACT = 9007199254740992.0;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// A property of an object, or null when the parent is not an object, the property is absent or it is JSON null.
   /// </summary>
   /// <param name="parent">The object.</param>
   /// <param name="name">Property name (exact case).</param>
   /// <returns>The value, or null.</returns>
   public static JsonElement? Get( JsonElement parent, string name )
   {
      return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty( name, out JsonElement value ) && value.ValueKind != JsonValueKind.Null ? value : null;
   }

   /// <summary>
   /// A required object property; fails loud when it is absent or not an object.
   /// </summary>
   /// <param name="parent">The parent.</param>
   /// <param name="name">Property name.</param>
   /// <param name="path">Where the parent sits in the file, for the message.</param>
   /// <returns>The object.</returns>
   public static JsonElement RequireObject( JsonElement parent, string name, string path )
   {
      return Get( parent, name ) is { ValueKind: JsonValueKind.Object } value ? value : throw Bad( path, name, "an object" );
   }

   /// <summary>
   /// A required array property; fails loud when it is absent or not an array.
   /// </summary>
   /// <param name="parent">The parent.</param>
   /// <param name="name">Property name.</param>
   /// <param name="path">Where the parent sits in the file, for the message.</param>
   /// <returns>The array element.</returns>
   public static JsonElement RequireArray( JsonElement parent, string name, string path )
   {
      return Get( parent, name ) is { ValueKind: JsonValueKind.Array } value ? value : throw Bad( path, name, "an array" );
   }

   /// <summary>
   /// An optional array property; empty when absent, loud when present but not an array.
   /// </summary>
   /// <param name="parent">The parent.</param>
   /// <param name="name">Property name.</param>
   /// <param name="path">Where the parent sits in the file, for the message.</param>
   /// <returns>The elements, possibly none.</returns>
   public static IReadOnlyList<JsonElement> Items( JsonElement parent, string name, string path )
   {
      return Get( parent, name ) switch
      {
         null => Array.Empty<JsonElement>(),
         { ValueKind: JsonValueKind.Array } list => list.EnumerateArray().ToList(),
         _ => throw Bad( path, name, "an array" ),
      };
   }

   /// <summary>
   /// A required, non-empty string property.
   /// </summary>
   /// <param name="parent">The parent.</param>
   /// <param name="name">Property name.</param>
   /// <param name="path">Where the parent sits in the file, for the message.</param>
   /// <returns>The text.</returns>
   public static string RequireText( JsonElement parent, string name, string path )
   {
      return Text( parent, name, path ) is { Length: > 0 } text ? text : throw Bad( path, name, "a non-empty string" );
   }

   /// <summary>
   /// An optional string property; loud when present with another type.
   /// </summary>
   /// <param name="parent">The parent.</param>
   /// <param name="name">Property name.</param>
   /// <param name="path">Where the parent sits in the file, for the message.</param>
   /// <returns>The text, or null when absent.</returns>
   public static string? Text( JsonElement parent, string name, string path )
   {
      return Get( parent, name ) switch
      {
         null => null,
         { ValueKind: JsonValueKind.String } value => value.GetString(),
         _ => throw Bad( path, name, "a string" ),
      };
   }

   /// <summary>
   /// An optional finite, non-negative number; loud when present with another type or value.
   /// </summary>
   /// <param name="parent">The parent.</param>
   /// <param name="name">Property name.</param>
   /// <param name="path">Where the parent sits in the file, for the message.</param>
   /// <returns>The number, or null when absent.</returns>
   public static double? Number( JsonElement parent, string name, string path )
   {
      return Get( parent, name ) switch
      {
         null => null,
         { ValueKind: JsonValueKind.Number } value when value.TryGetDouble( out double d ) && double.IsFinite( d ) && d >= 0 => d,
         _ => throw Bad( path, name, "a finite number of at least zero" ),
      };
   }

   /// <summary>
   /// A required finite, non-negative number.
   /// </summary>
   /// <param name="parent">The parent.</param>
   /// <param name="name">Property name.</param>
   /// <param name="path">Where the parent sits in the file, for the message.</param>
   /// <returns>The number.</returns>
   public static double RequireNumber( JsonElement parent, string name, string path )
   {
      return Number( parent, name, path ) ?? throw Bad( path, name, "a finite number of at least zero" );
   }

   /// <summary>
   /// An optional whole number (a number written with a fraction of zero, such as 13508.0, counts); loud when present and not a whole number.
   /// </summary>
   /// <param name="parent">The parent.</param>
   /// <param name="name">Property name.</param>
   /// <param name="path">Where the parent sits in the file, for the message.</param>
   /// <returns>The number, or null when absent.</returns>
   public static long? Whole( JsonElement parent, string name, string path )
   {
      return Get( parent, name ) switch
      {
         null => null,
         { ValueKind: JsonValueKind.Number } value when value.TryGetInt64( out long n ) => n,
         { ValueKind: JsonValueKind.Number } value when value.TryGetDouble( out double d ) && double.IsFinite( d ) && d == Math.Floor( d ) && Math.Abs( d ) < MAX_EXACT => (long)d,
         _ => throw Bad( path, name, "a whole number" ),
      };
   }

   /// <summary>
   /// An optional true or false; loud when present with another type.
   /// </summary>
   /// <param name="parent">The parent.</param>
   /// <param name="name">Property name.</param>
   /// <param name="path">Where the parent sits in the file, for the message.</param>
   /// <returns>The value, or null when absent.</returns>
   public static bool? Flag( JsonElement parent, string name, string path )
   {
      return Get( parent, name ) switch
      {
         null => null,
         { ValueKind: JsonValueKind.True } => true,
         { ValueKind: JsonValueKind.False } => false,
         _ => throw Bad( path, name, "true or false" ),
      };
   }

   /// <summary>
   /// An optional array of strings; loud when an element is not a string.
   /// </summary>
   /// <param name="parent">The parent.</param>
   /// <param name="name">Property name.</param>
   /// <param name="path">Where the parent sits in the file, for the message.</param>
   /// <returns>The strings, possibly none.</returns>
   public static IReadOnlyList<string> Strings( JsonElement parent, string name, string path )
   {
      return Items( parent, name, path ).Select( ( e, i ) => e.ValueKind == JsonValueKind.String ? e.GetString()! : throw Bad( path, $"{name}[{i}]", "a string" ) ).ToList();
   }

   /// <summary>
   /// The failure every reader throws: which field of which object is not what the page needs.
   /// </summary>
   /// <param name="path">Where the parent sits in the file.</param>
   /// <param name="name">The field.</param>
   /// <param name="expected">What it should have been.</param>
   /// <returns>The exception to throw.</returns>
   public static InvalidDataException Bad( string path, string name, string expected )
   {
      return new InvalidDataException( $"{path}.{name} is missing or is not {expected}." );
   }

   #endregion Public Methods
}
