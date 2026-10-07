using System.Text.Json;
using System.Text.RegularExpressions;
using GenericVectorBuilder.Bench.Report;
using S = GenericVectorBuilder.Bench.Stats.ConsolidateSources;
using T = GenericVectorBuilder.Bench.Stats.ConsolidateText;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The durability disclosures: each engine's recorded durability text quoted as the run recorded it, the saved logs behind
/// the measurements those texts cite, the engines whose cited measurements have no saved log, and a correction where a
/// repository file contradicts a recorded sentence.
/// Why quoted and corrected apart: a durability text is the tool's own wording and its recorded value is part of the engine's
/// identity (a changed text makes the engine a different setup), so the report prints it as recorded and puts what the files
/// show beside it. Weaviate's text says its compose file sets no persistence variable, and that file sets PERSISTENCE_DATA_PATH;
/// changing the sink would change the recorded identity between v7 and v8, so the report corrects it in its own words.
/// Why saved logs: the numbers in those texts (writes counted, syncs counted, rows found after a kill) were typed into the
/// sinks on 2026-10-04 from strace and kill runs; the logs are kept in the repository with their hashes, and the report says
/// which engines have one and which do not.
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

   /// <summary>Words that mark a durability text as citing a measurement.</summary>
   private static readonly string[] MEASURED_WORDS = { "measured", "Measured" };

   /// <summary>A recorded sentence a compose file contradicts: the target, the words, the file and the variable-name prefix whose presence contradicts them.</summary>
   private static readonly (string Target, string Words, string File, string Prefix)[] CORRECTIONS =
   {
      ( "weaviate", "sets no persistence variable", "deploy/engines/weaviate.compose.yaml", "PERSISTENCE_" ),
   };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Writes the durability sentences: the intro, the saved logs, the engines without one, then each target's quote with its correction.
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

         w.Add( $"disclosure.durability.{target}", text, S.Quote( run.Name, $"targets[{target}].durability", text ) );
         foreach( ( string t, string words, string file, string prefix ) in CORRECTIONS.Where( c => c.Target == target && text.Contains( c.Words, StringComparison.Ordinal ) ) )
         {
            Correct( w, run, t, words, file, prefix );
         }
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
   /// The sentence that names the engines whose durability text cites a measurement and that have no saved log.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="run">The run the texts are quoted from.</param>
   /// <param name="logged">The targets with a saved log.</param>
   private static void NoLogs( Writer w, RunResult run, List<string> logged )
   {
      var missing = new List<(string Target, string Word)>();
      foreach( string target in w.Report.Targets.Where( t => !logged.Contains( t ) ) )
      {
         string? text = run.Find( target )?.Durability;
         string? word = text == null ? null : MEASURED_WORDS.FirstOrDefault( m => text.Contains( m, StringComparison.Ordinal ) );
         if( word != null )
         {
            missing.Add( ( target, word ) );
         }
      }

      if( missing.Count > 0 )
      {
         w.Add( "disclosure.durability.nologs", T.Fill( T.DISCLOSURE_DURABILITY_NOLOGS, S.List( missing.Select( m => m.Target ).ToList() ) ),
            missing.Select( m => S.Token( run.Name, $"targets[{m.Target}].durability", m.Word ) ) );
      }
   }

   /// <summary>
   /// The correction sentence for one recorded sentence a compose file contradicts; nothing when the file sets no such variable (the recorded words then hold).
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="run">The run the text is quoted from.</param>
   /// <param name="target">Target.</param>
   /// <param name="words">The recorded words the file contradicts.</param>
   /// <param name="file">The compose file.</param>
   /// <param name="prefix">The variable-name prefix.</param>
   private static void Correct( Writer w, RunResult run, string target, string words, string file, string prefix )
   {
      if( !File.Exists( Path.Combine( w.RepoRoot, file ) ) )
      {
         throw new ConsolidateRefusal( $"{file}, which corrects the recorded {target} durability text, does not exist under {w.RepoRoot}" );
      }

      List<string> names = Variables( w.RepoRoot, file, prefix );
      if( names.Count == 0 )
      {
         return;
      }

      var sources = names.Select( n => S.File( file, $"{n}:" ) ).ToList();
      sources.Add( S.Token( run.Name, $"targets[{target}].durability", words ) );
      w.Add( $"disclosure.durability.{target}.correction", T.Fill( T.DISCLOSURE_CORRECTION, target, file, S.List( names ), words ), sources );
   }

   #endregion Private Methods
}
