using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Bench.Report;
using S = GenericVectorBuilder.Bench.Stats.ConsolidateSources;
using T = GenericVectorBuilder.Bench.Stats.ConsolidateText;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The durability disclosures: each engine's recorded durability text printed clause by clause with how each clause is backed (see <see cref="ClauseWriter"/>), the saved
/// logs behind the measurements those texts cite, the engines whose texts cite a measurement that no saved log backs, and, in place of a clause a repository file
/// contradicts, the sentence that says so.
/// Why clause by clause: a durability text mixes what the engine reported, what a compose file sets, what a saved strace log shows and what the program only typed, and the
/// recorded value of the text is part of the engine's identity (a changed text makes it a different setup), so the sinks stay as they are and the report classifies each clause.
/// Weaviate's text says its compose file sets no persistence variable, and that file sets PERSISTENCE_DATA_PATH: the clause is not printed, and one sentence names the file.
/// Why saved logs: the numbers in those texts (writes counted, syncs counted, rows found after a kill) were typed into the sinks on 2026-10-04 from strace and kill runs; the
/// logs that exist are kept in the repository with their hashes, and the report says which engines have one and which cite a measurement without one.
/// </summary>
public static class ConsolidateTextDurability
{
   #region Data Members

   /// <summary>The repository folder of the saved logs.</summary>
   public const string LOGS_FOLDER = "design/engine-docs/durability-logs-2026-10-04";

   /// <summary>The index of the saved logs.</summary>
   public const string LOGS_INDEX = LOGS_FOLDER + "/index.json";

   /// <summary>The file in the folder that lists each log's hash.</summary>
   public const string HASH_FILE = "SHA256SUMS";

   private const string FOLDER_TOKEN = "\"folder\": \"" + LOGS_FOLDER + "\"";
   private const string HASHES_TOKEN = "\"hashes\": \"" + HASH_FILE + "\"";

   /// <summary>How many characters of a clause the sentence that names its engine cites.</summary>
   private const int TOKEN_CHARS = 48;

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Writes the durability sentences: the intro, the saved logs, the engines that cite a measurement with no saved log, then each target's clauses.
   /// </summary>
   /// <param name="w">The writer.</param>
   public static void Write( Writer w )
   {
      RunResult run = w.Newest.Runs[0];
      w.Add( "disclosure.durability", T.Fill( T.DISCLOSURE_DURABILITY, run.Name ), w.RunSource( run.Name ) );
      List<string> logged = Logs( w );
      NoLogs( w, run, logged );
      foreach( string target in w.Report.Targets )
      {
         string? text = run.Find( target )?.Durability;
         if( text == null )
         {
            continue;
         }

         string slot = $"disclosure.durability.{target}";
         w.Add( slot, T.Fill( T.DURABILITY_HEAD, target ), w.RunSource( run.Name ) );
         new ClauseWriter( w, slot ).Add( run.Name, $"targets[{target}].durability", w.Input.Classes.Split( target, "durability", text ) );
      }
   }

   /// <summary>
   /// The variables a file sets whose names start with a prefix, as a YAML environment entry "NAME:" outside comments.
   /// </summary>
   /// <param name="repoRoot">Repository root.</param>
   /// <param name="file">Repository path of the file.</param>
   /// <param name="prefix">The name prefix.</param>
   /// <returns>The names in file order, each once; empty when the file sets none.</returns>
   public static List<string> Variables( string repoRoot, string file, string prefix )
   {
      var names = new List<string>();
      var entry = new Regex( @"^\s*(?<name>" + Regex.Escape( prefix ) + @"[A-Z0-9_]*)\s*:", RegexOptions.CultureInvariant, TimeSpan.FromSeconds( 5 ) );
      foreach( string line in File.ReadAllLines( Path.Combine( repoRoot, file ) ) )
      {
         int hash = line.IndexOf( '#' );
         Match m = entry.Match( hash < 0 ? line : line[..hash] );
         if( m.Success && !names.Contains( m.Groups["name"].Value, StringComparer.Ordinal ) )
         {
            names.Add( m.Groups["name"].Value );
         }
      }

      return names;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The sentence that says which engines have saved logs, from the index file of the folder.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <returns>The reported targets that have a saved log.</returns>
   private static List<string> Logs( Writer w )
   {
      string path = Path.Combine( w.RepoRoot, LOGS_INDEX );
      if( !File.Exists( path ) )
      {
         return new List<string>();
      }

      List<string> indexed;
      try
      {
         using JsonDocument document = JsonDocument.Parse( File.ReadAllText( path ) );
         indexed = document.RootElement.GetProperty( "logs" ).EnumerateArray().Select( l => l.GetProperty( "target" ).GetString()! ).ToList();
      }
      catch( Exception ex ) when( ex is JsonException or KeyNotFoundException or InvalidOperationException )
      {
         throw new ConsolidateRefusal( $"{LOGS_INDEX} is not an index of saved logs (an object with a logs list of targets): {ex.Message}" );
      }

      List<string> logged = indexed.Where( t => w.Report.Targets.Contains( t ) ).ToList();
      if( logged.Count == 0 )
      {
         return logged;
      }

      var sources = new List<SentenceSource> { S.File( LOGS_INDEX, FOLDER_TOKEN ), S.File( LOGS_INDEX, HASHES_TOKEN ) };
      sources.AddRange( logged.Select( t => S.File( LOGS_INDEX, $"\"target\": \"{t}\"" ) ) );
      w.Add( "disclosure.durability.logs", T.Fill( T.DISCLOSURE_DURABILITY_LOGS, S.List( logged ), LOGS_FOLDER, HASH_FILE ), sources );
      return logged;
   }

   /// <summary>
   /// The sentence that names the engines whose durability text cites a measurement or a reading that no saved source backs and that have no saved log.
   /// Why from the classes and not from words in the text: a clause is unverified and cites a measurement when the classes list says so, so a reading that no saved file holds
   /// (a Typesense flag page, a SHOW VARIABLES of 2026-10-04) is named whether or not its text says "measured".
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="run">The run the texts are quoted from.</param>
   /// <param name="logged">The targets with a saved log.</param>
   private static void NoLogs( Writer w, RunResult run, List<string> logged )
   {
      var missing = new List<( string Target, string Token )>();
      foreach( string target in w.Report.Targets.Where( t => !logged.Contains( t ) ) )
      {
         string? text = run.Find( target )?.Durability;
         TextPiece? cited = text == null ? null : w.Input.Classes.Split( target, "durability", text ).FirstOrDefault( p => p.Span.Cites != null && p.Span.Class == TextClass.UNVERIFIED );
         if( cited != null )
         {
            missing.Add( ( target, cited.Text.Length <= TOKEN_CHARS ? cited.Text : cited.Text[..TOKEN_CHARS] ) );
         }
      }

      if( missing.Count > 0 )
      {
         w.Add( "disclosure.durability.nologs", T.Fill( T.DISCLOSURE_DURABILITY_NOLOGS, S.List( missing.Select( m => m.Target ).ToList() ) ),
            missing.Select( m => S.Token( run.Name, $"targets[{m.Target}].durability", m.Token ) ) );
      }
   }

   #endregion Private Methods
}
