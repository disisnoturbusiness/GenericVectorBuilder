using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Bench.Report;

/// <summary>
/// The settings that set how an engine's index was built and how hard it searches, as a sorted list
/// of name and value, taken from the engine's own index description.
/// Why from the description: every sink builds that sentence from the options it really applies (the
/// build parameters and the effort per query, such as "m=16 ef_construction=128, hnsw.ef_search=100
/// per query"), so it cannot say one thing while the engine does another, and a run made at another
/// effort writes another description. The consolidated report compares these settings between runs
/// and refuses to average runs that differ; until run-all wrote them, that check compared "missing"
/// with "missing" and checked nothing.
/// What counts as a setting: a name and a value written together as name=value with no space around
/// the equals sign (so "exact mode = full scan" is prose, not a setting), and a quoted pair such as
/// "StartId":"306" (the build record SQL Server's DiskANN prints). A description that states none
/// (SQL Server's exact scan, sqlite-vec's brute force) is recorded whole under "description", so the
/// target is never recorded as having no settings.
/// </summary>
public static class TargetSearchSettings
{
   #region Data Members

   /// <summary>The name used when the description states no setting of its own.</summary>
   public const string DESCRIPTION_KEY = "description";

   private const int MAX_DESCRIPTION = 400;
   private static readonly Regex EQUALS_PAIR = new( @"(?<![\w.@-])(?<key>[A-Za-z][\w.@-]*)=(?<value>""[^""]*""|[^\s,;()""]+)", RegexOptions.Compiled );
   private static readonly Regex QUOTED_PAIR = new( @"""(?<key>[A-Za-z]\w*)""\s*:\s*""(?<value>[^""]*)""", RegexOptions.Compiled );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Reads the settings out of an index description.
   /// </summary>
   /// <param name="index">The target's index description, as the engine states it after the load.</param>
   /// <returns>Name and value pairs sorted by name; one "description" entry when the text states none; empty for an empty description.</returns>
   public static SortedDictionary<string, string> From( string? index )
   {
      var settings = new SortedDictionary<string, string>( StringComparer.Ordinal );
      if( string.IsNullOrWhiteSpace( index ) )
      {
         return settings;
      }

      foreach( Match m in EQUALS_PAIR.Matches( index ) )
      {
         settings.TryAdd( m.Groups["key"].Value, m.Groups["value"].Value.Trim( '"' ).TrimEnd( '.', ':' ) );
      }

      foreach( Match m in QUOTED_PAIR.Matches( index ) )
      {
         settings.TryAdd( m.Groups["key"].Value, m.Groups["value"].Value );
      }

      if( settings.Count == 0 )
      {
         string text = index.Trim();
         settings[DESCRIPTION_KEY] = text.Length > MAX_DESCRIPTION ? text[..MAX_DESCRIPTION] + "..." : text;
      }

      return settings;
   }

   #endregion Public Methods
}
