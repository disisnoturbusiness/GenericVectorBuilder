using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Bench.Report;
using S = GenericVectorBuilder.Bench.Stats.ConsolidateSources;
using T = GenericVectorBuilder.Bench.Stats.ConsolidateText;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// Writes the sentences that come from a piece of evidence saved in the repository and not from the runs: the recall of MariaDB at its search effort in the saved re-run
/// of its effort test (it replaces a recorded clause whose figures no saved log backed), and the saved check of the benchmark's tie rule against this data (it says whether
/// the recorded reason for the rule applies here). Every figure is read from the saved file and cited by the line or the key it comes from; when the saved file does
/// not match the runs (another effort, another data set) the sentence says so and gives no figure.
/// </summary>
public static class ConsolidateTextEvidence
{
   #region Data Members

   /// <summary>The evidence name of the saved re-run of MariaDB's effort test.</summary>
   public const string MARIADB_RECALL = "mariadbRecallLog";

   /// <summary>The evidence name of the saved tie-rule check.</summary>
   public const string TIE_CHECK = "tieCheck";

   /// <summary>The target whose recall the saved re-run measured.</summary>
   public const string MARIADB = "mariadb";

   /// <summary>The setting that holds MariaDB's search effort in a run.</summary>
   public const string EFFORT_KEY = "mhnsw_ef_search";

   /// <summary>Largest saved evidence file read.</summary>
   public const long MAX_BYTES = 8L * 1024 * 1024;

   private static readonly TimeSpan MATCH_TIMEOUT = TimeSpan.FromSeconds( 5 );

   private static readonly Regex RECALL_LINE = new( @"RECALL@(?<top>\d+) on (?<n>\d+) random (?<dim>\d+)-dim vectors, (?<q>\d+) queries: (?<rest>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant, MATCH_TIMEOUT );

   private static readonly Regex EFFORT_PAIR = new( @"ef (?<ef>\d+) (?<recall>\d+\.\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant, MATCH_TIMEOUT );

   private static readonly Regex NOTE_INDEX = new( @"^notes\[(?<i>\d+)\]$", RegexOptions.Compiled | RegexOptions.CultureInvariant, MATCH_TIMEOUT );

   private static readonly Regex TIE_WITHIN = new( @"within (?<eps>\d+(?:\.\d+)?e-\d+)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant, MATCH_TIMEOUT );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Writes the sentences of a named piece of evidence.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="slot">The slot prefix of the text the evidence belongs to.</param>
   /// <param name="name">The evidence's name in the classes list.</param>
   /// <param name="run">The run the text is quoted from.</param>
   /// <param name="path">The text's path in that run's results.json.</param>
   /// <exception cref="ConsolidateRefusal">The name is not known, or the saved file cannot be read.</exception>
   public static void Write( Writer w, string slot, string name, string run, string path )
   {
      string relative = w.Input.Classes.Evidence.TryGetValue( name, out string? found ) ? found : throw new ConsolidateRefusal( $"the classes list names no file for the evidence '{name}'" );
      string full = Path.Combine( w.RepoRoot, relative );
      if( !File.Exists( full ) || new FileInfo( full ).Length > MAX_BYTES )
      {
         throw new ConsolidateRefusal( $"the saved evidence {relative} is missing or over {MAX_BYTES} bytes" );
      }

      switch( name )
      {
         case MARIADB_RECALL:
            Recall( w, slot, run, relative, File.ReadAllLines( full ) );
            break;
         case TIE_CHECK:
            Tie( w, slot, run, path, relative, File.ReadAllText( full ) );
            break;
         default:
            throw new ConsolidateRefusal( $"the evidence '{name}' is not one this report can write" );
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The recall of MariaDB at the effort its runs recorded, per collection size, from the saved re-run: the lowest and highest of the repeats.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="slot">Slot prefix.</param>
   /// <param name="run">The run.</param>
   /// <param name="relative">The log's repository path.</param>
   /// <param name="lines">The log's lines.</param>
   private static void Recall( Writer w, string slot, string run, string relative, string[] lines )
   {
      string effort = w.Newest.Runs[0].Find( MARIADB )?.SearchSettingsEntries.FirstOrDefault( e => e.Key == EFFORT_KEY ).Value ?? string.Empty;
      var found = new List<( string Line, Match Head, double Recall, string Text )>();
      foreach( string raw in lines )
      {
         Match head = RECALL_LINE.Match( raw );
         Match pair = head.Success ? EFFORT_PAIR.Matches( head.Groups["rest"].Value ).FirstOrDefault( m => m.Groups["ef"].Value == effort ) ?? Match.Empty : Match.Empty;
         if( pair.Success )
         {
            found.Add( ( head.Value, head, double.Parse( pair.Groups["recall"].Value, CultureInfo.InvariantCulture ), pair.Groups["recall"].Value ) );
         }
      }

      if( found.Count == 0 )
      {
         w.Add( slot + ".recall", T.Fill( T.RECALL_LOG_NONE, MARIADB, effort ), S.Result( run, $"targets[{MARIADB}].searchSettings.{EFFORT_KEY}", effort ) );
         return;
      }

      foreach( IGrouping<string, ( string Line, Match Head, double Recall, string Text )> size in found.GroupBy( f => f.Head.Groups["n"].Value ).OrderBy( g => int.Parse( g.Key, CultureInfo.InvariantCulture ) ) )
      {
         var low = size.OrderBy( f => f.Recall ).First();
         var high = size.OrderByDescending( f => f.Recall ).First();
         var sources = new List<SentenceSource> { S.File( relative, low.Line ) };
         if( high.Line != low.Line )
         {
            sources.Add( S.File( relative, high.Line ) );
         }

         w.Add( $"{slot}.recall.{size.Key}", T.Fill( T.RECALL_LOG, MARIADB, low.Head.Groups["top"].Value, effort, size.Key, low.Head.Groups["dim"].Value, low.Text, high.Text ), sources );
      }
   }

   /// <summary>
   /// Whether the tie rule can have added a hit in this data, from the saved check, when the check was made on the same data and with the same tolerance.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="slot">Slot prefix.</param>
   /// <param name="run">The run.</param>
   /// <param name="path">The note's path, notes[i].</param>
   /// <param name="relative">The check's repository path.</param>
   /// <param name="json">The check.</param>
   private static void Tie( Writer w, string slot, string run, string path, string relative, string json )
   {
      RunResult first = w.Newest.Runs.First( r => r.Name == run );
      Match index = NOTE_INDEX.Match( path );
      string note = index.Success ? first.Notes[int.Parse( index.Groups["i"].Value, CultureInfo.InvariantCulture )] : string.Empty;
      Match within = TIE_WITHIN.Match( note );
      using JsonDocument doc = JsonDocument.Parse( json );
      JsonElement r = doc.RootElement;
      long Number( string key ) => r.TryGetProperty( key, out JsonElement v ) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64( out long n ) ? n : -1;
      bool same = within.Success && r.TryGetProperty( "epsilon", out JsonElement eps ) && eps.ValueKind == JsonValueKind.Number
         && Math.Abs( eps.GetDouble() - double.Parse( within.Groups["eps"].Value, CultureInfo.InvariantCulture ) ) < TOLERANCE_MATCH
         && Number( "rows" ) == first.Rows && Number( "dimension" ) == first.Dimension && Number( "queries" ) == first.QueryCount && Number( "top" ) == first.Top;
      if( !same )
      {
         w.Add( slot + ".tie", T.TIE_CHECK_NONE, S.File( relative, "\"epsilon\":" ) );
         return;
      }

      string pairs = Number( "duplicatePairs" ).ToString( CultureInfo.InvariantCulture );
      string tied = Number( "queriesWithRowsOutsideTopKWithinEpsilon" ).ToString( CultureInfo.InvariantCulture );
      string queries = Number( "queries" ).ToString( CultureInfo.InvariantCulture );
      string top = Number( "top" ).ToString( CultureInfo.InvariantCulture );
      w.Add( $"{slot}.tie", T.Fill( T.TIE_CHECK, pairs, tied, queries, top ), S.File( relative, $"\"duplicatePairs\": {pairs}" ), S.File( relative, $"\"queriesWithRowsOutsideTopKWithinEpsilon\": {tied}" ),
         S.File( relative, $"\"queries\": {queries}" ), S.File( relative, $"\"top\": {top}" ), S.Token( run, path, $"within {within.Groups["eps"].Value}" ) );
   }

   /// <summary>How close two tolerances must be to count as the same one.</summary>
   private const double TOLERANCE_MATCH = 1e-12;

   #endregion Private Methods
}
