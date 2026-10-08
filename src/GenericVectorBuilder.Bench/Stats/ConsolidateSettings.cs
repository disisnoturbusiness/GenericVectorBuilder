using System.Text.RegularExpressions;
using GenericVectorBuilder.Bench.Report;
using S = GenericVectorBuilder.Bench.Stats.ConsolidateSources;
using T = GenericVectorBuilder.Bench.Stats.ConsolidateText;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// Each reported engine's recorded search and index settings (the searchSettings object of its results), the data
/// for consolidated.json and the sentences that put them in the text, with the clause of the engine's recorded index
/// text that states its per-query effort when that clause says more than a number.
/// Why: the orders hold at the settings each run recorded, and those settings are not equal across engines (ClickHouse 256
/// candidates, MongoDB 20 times the hits, Weaviate a dynamic ef, Qdrant its server value, 100 for most), so a reader needs
/// them beside the order. MariaDB's ef was raised from its own default of 20 to 100 and its recall falls as the set grows;
/// the run recorded that in its index text, and the report quotes it instead of dropping it.
/// Every setting is a source of its sentence, resolved again against the raw results.json, so no setting is typed here.
/// </summary>
public static class ConsolidateSettings
{
   #region Data Members

   /// <summary>The key names that state how hard one query searches (not the build parameters).</summary>
   private static readonly Regex EFFORT_KEY = new( @"^(m?hnsw[._])?ef(_search|_runtime|search)?$|^num_?candidates$|candidate_list_size_for_search$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds( 5 ) );

   /// <summary>
   /// Engine settings whose recorded key says more than the value is: the key the report prints instead, by target and recorded key.
   /// Why: Chroma's HTTP API answers GET /api/v2/version with its API version (1.0.0), and the row was recorded as "chroma_version", which a reader takes for the product version; the image that ran
   /// says 1.4.4 (checked with the pinned image's own binary). The row stays, labelled for what it is.
   /// </summary>
   public static readonly IReadOnlyDictionary<(string Target, string Key), string> RELABELED_KEYS = new Dictionary<(string, string), string>
   {
      [("chroma", "chroma_version")] = "chroma_version (API version reported)",
   };

   /// <summary>What replaces a statement the run-page notes list marks, in the clause the file keeps.</summary>
   public const string WITHHELD = "[a statement the run-page notes list marks]";

   /// <summary>A value that is only a number.</summary>
   private static readonly Regex PLAIN_NUMBER = new( @"^\d+$", RegexOptions.Compiled, TimeSpan.FromSeconds( 5 ) );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The settings of every reported target, from the newest claim session's first run that recorded any for it.
   /// </summary>
   /// <param name="sessions">Claim sessions, oldest first.</param>
   /// <param name="targets">Reported targets.</param>
   /// <returns>One row per target that recorded settings, in target order.</returns>
   public static List<SearchSettingsRow> Build( IReadOnlyList<ClaimSession> sessions, IReadOnlyList<string> targets )
   {
      var rows = new List<SearchSettingsRow>();
      foreach( string target in targets )
      {
         RunResult? run = sessions.Reverse().SelectMany( s => s.Runs ).FirstOrDefault( r => r.Find( target )?.SearchSettingsEntries.Count > 0 );
         if( run == null )
         {
            continue;
         }

         TargetResult t = run.Find( target )!;
         List<SearchSettingEntry> entries = t.SearchSettingsEntries.Select( p => new SearchSettingEntry { Key = p.Key, Value = p.Value } ).ToList();
         rows.Add( new SearchSettingsRow { Target = target, Run = run.Name, Settings = entries, Clause = Clause( t.Index, entries ) } );
      }

      return rows;
   }

   /// <summary>
   /// The key the report prints for an engine setting: the recorded key, or its relabelled form.
   /// </summary>
   /// <param name="target">The target.</param>
   /// <param name="key">The recorded key.</param>
   /// <returns>The key to print.</returns>
   public static string KeyShown( string target, string key )
   {
      return RELABELED_KEYS.TryGetValue( (target, key), out string? shown ) ? shown : key;
   }

   /// <summary>
   /// The recorded clause of an index text that states the per-query effort, when it carries more than a number.
   /// It runs from the effort setting as recorded ("ef=-1") to the end of the clause: through a parenthesis that opens before the
   /// next comma or semicolon, else to that comma or semicolon.
   /// </summary>
   /// <param name="index">The recorded index text, or null.</param>
   /// <param name="settings">The recorded settings.</param>
   /// <returns>The clause, or null when no effort setting is found in the text or the clause holds a plain number and nothing in parentheses.</returns>
   public static string? Clause( string? index, IReadOnlyList<SearchSettingEntry> settings )
   {
      if( index == null )
      {
         return null;
      }

      foreach( SearchSettingEntry effort in settings.Where( s => EFFORT_KEY.IsMatch( s.Key ) ) )
      {
         int start = index.IndexOf( $"{effort.Key}={effort.Value}", StringComparison.Ordinal );
         if( start < 0 )
         {
            continue;
         }

         string clause = ClauseFrom( index, start );
         return clause.Contains( '(', StringComparison.Ordinal ) || !PLAIN_NUMBER.IsMatch( effort.Value ) ? clause : null;
      }

      return null;
   }

   /// <summary>
   /// Replaces, in the recorded effort clause each row carries, every statement the run-page notes list marks, so the consolidated file does not hold a marked statement as plain data either.
   /// Why after the sentences: <see cref="Write"/> finds the clause in the recorded text by its words, and the clause printed is the part of the text the classes allow; this only changes the copy
   /// the file keeps, which the page leaves out on purpose.
   /// </summary>
   /// <param name="rows">The settings rows of the report.</param>
   /// <param name="notes">The run-page notes list.</param>
   public static void WithholdMarked( IEnumerable<SearchSettingsRow> rows, RunPageNoteList notes )
   {
      foreach( SearchSettingsRow row in rows.Where( r => r.Clause != null ) )
      {
         foreach( RunPageNote note in notes.MarkedIn( row.Clause! ).ToList() )
         {
            row.Clause = row.Clause!.Replace( note.Statement, WITHHELD, StringComparison.Ordinal );
         }
      }
   }

   /// <summary>
   /// Writes the settings sentences: one per target with its recorded settings, the recorded effort clause where there is one, and a
   /// statement for an approximate engine that recorded no effort setting.
   /// </summary>
   /// <param name="w">The writer.</param>
   public static void Write( Writer w )
   {
      foreach( WhyRow why in w.Report.Why )
      {
         SearchSettingsRow? row = w.Report.SearchSettings.FirstOrDefault( r => r.Target == why.Target );
         if( row == null )
         {
            continue;
         }

         List<SentenceSource> sources = row.Settings.Select( s => S.Token( row.Run, $"targets[{row.Target}].searchSettings", $"{s.Key}={s.Value}" ) ).ToList();
         string pairs = string.Join( ", ", row.Settings.Select( s => $"{s.Key}={s.Value}" ) );
         w.Add( $"why.settings.{row.Target}", T.Fill( T.WHY_SETTINGS, row.Target, pairs ), sources );
         if( row.Clause != null )
         {
            Effort( w, row );
         }
         else if( !row.Settings.Any( s => EFFORT_KEY.IsMatch( s.Key ) ) && w.Fact( row.Target, "search" )?.Mode == "approximate" )
         {
            w.Add( $"why.noeffort.{row.Target}", T.Fill( T.WHY_NO_EFFORT, row.Target ), S.Token( row.Run, $"targets[{row.Target}].searchSettings", $"{row.Settings[0].Key}={row.Settings[0].Value}" ) );
         }
      }
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The recorded effort clause of an engine, printed clause by clause with how each part is backed: the head sentence, then the parts of the clause that lie in the recorded index text.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="row">The settings row whose clause is printed.</param>
   private static void Effort( Writer w, SearchSettingsRow row )
   {
      string index = w.Sessions.SelectMany( s => s.Runs ).First( r => r.Name == row.Run ).Find( row.Target )!.Index!;
      string collapsed = RecordedTextClasses.Collapse( index );
      string clause = RecordedTextClasses.Collapse( row.Clause! );
      int start = collapsed.IndexOf( clause, StringComparison.Ordinal );
      string slot = $"why.effort.{row.Target}";
      w.Add( slot, T.Fill( T.EFFORT_HEAD, row.Target ), w.RunSource( row.Run ) );
      new ClauseWriter( w, slot ).Add( row.Run, $"targets[{row.Target}].index", w.Input.Classes.Split( row.Target, "index", index ), start < 0 ? null : ( start, start + clause.Length ) );
   }

   /// <summary>
   /// The clause that starts at a position of the text.
   /// </summary>
   /// <param name="text">The text.</param>
   /// <param name="start">Where the clause starts.</param>
   /// <returns>The clause, trimmed.</returns>
   private static string ClauseFrom( string text, int start )
   {
      int depth = 0;
      int end = text.Length;
      for( int i = start; i < text.Length; i++ )
      {
         char c = text[i];
         if( c == '(' )
         {
            depth++;
         }
         else if( c == ')' )
         {
            depth = Math.Max( 0, depth - 1 );
         }
         else if( depth == 0 && c is ',' or ';' )
         {
            end = i;
            break;
         }
      }

      return text[start..end].Trim();
   }

   #endregion Private Methods
}
