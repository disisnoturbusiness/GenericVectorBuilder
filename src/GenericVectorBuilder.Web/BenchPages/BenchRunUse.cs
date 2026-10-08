using System.Text.Json;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// A note a consolidated set prints beside an engine's name, with what it is bound to.
/// </summary>
/// <param name="Text">The note.</param>
/// <param name="Sources">Its sources (a saved page, a repository file); empty when it has none.</param>
public sealed record BenchRowNote( string Text, IReadOnlyList<BenchSource> Sources );

/// <summary>
/// One run a consolidated folder uses, with the role it plays there.
/// </summary>
/// <param name="Run">The run's folder name (checked safe).</param>
/// <param name="Role">The session's name for a run of a compared session; "basis" or "basis" and the session's name for a run that feeds the threshold.</param>
/// <param name="Session">The session the run belongs to, or null when the file gives none for a basis run.</param>
/// <param name="IsBasis">True for a run that feeds the threshold, false for a run of a compared session.</param>
public sealed record BenchRunRef( string Run, string Role, string? Session = null, bool IsBasis = false );

/// <summary>
/// What the page knows about one consolidated folder without drawing it: what shape its file is in, how many
/// runs it uses, which, the notes it gives on why those runs are reused, and the clock warnings it dropped.
/// Why a record apart from the model: the run list and every run page need this for every folder, and
/// reading the whole page model for each of them on each request would be wasted work; a file that cannot
/// be read is a record with an error, so one bad folder never takes the list down.
/// </summary>
/// <param name="Folder">The folder's name.</param>
/// <param name="Kind">What its name says it is.</param>
/// <param name="Shape">The shape of its file, or null when it could not be read.</param>
/// <param name="EngineCount">How many engines its tables hold (0 when unknown).</param>
/// <param name="Runs">The runs it uses.</param>
/// <param name="Reuse">The sentences of its "runs.reuse" slots with their sources, one per session it says a word about; empty when it has none.</param>
/// <param name="Dropped">The clock warnings it dropped.</param>
/// <param name="Error">Why the file could not be read, or null.</param>
/// <param name="RowNotes">The notes its metric rows carry beside an engine's name (for example that an engine holds its data in memory) with their sources, by target; null for a file with none.</param>
/// <param name="RunNotes">The recorded statements of its runs that it marks as false or misleading, each stamped with this folder as its origin; null for a file with none.</param>
public sealed record BenchFolderFacts( string Folder, BenchFolderKind Kind, BenchShape? Shape, int EngineCount, IReadOnlyList<BenchRunRef> Runs, IReadOnlyList<BenchSentence> Reuse,
   IReadOnlyList<BenchDroppedWarning> Dropped, string? Error, IReadOnlyDictionary<string, BenchRowNote>? RowNotes = null, IReadOnlyList<BenchRunNote>? RunNotes = null );

/// <summary>
/// Finds which consolidated folders use which runs.
/// Why: a run page and the run list must say that a run was used by a published set, and that a run a
/// blocked set used is reused by the published one with the published folder's own note on why; the
/// only place that knows is the consolidated file, so the pages read it here.
/// </summary>
public static class BenchRunUse
{
   #region Data Members

   /// <summary>The slot every reuse sentence starts with ("runs.reuse.SESSION": why a published set reuses the runs of a session that a blocked set used).</summary>
   public const string REUSE_SLOT = BenchSlots.RUNS + ".reuse";

   private const string CONSOLIDATED_JSON = "consolidated.json";

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The facts of every folder under the root that holds a consolidated.json (a published, candidate, withdrawn or blocked set, or a folder
   /// of any other name, which counts as no set).
   /// </summary>
   /// <param name="root">Results root.</param>
   /// <returns>One record per folder, in name order; empty when the root cannot be read.</returns>
   public static IReadOnlyList<BenchFolderFacts> Scan( string root )
   {
      try
      {
         return new DirectoryInfo( root ).GetDirectories()
            .Where( d => BenchFolderNames.IsSafe( d.Name ) && File.Exists( Path.Combine( d.FullName, CONSOLIDATED_JSON ) ) )
            .OrderBy( d => d.Name, StringComparer.Ordinal ).Select( d => Facts( d.Name, Path.Combine( d.FullName, CONSOLIDATED_JSON ) ) ).ToList();
      }
      catch( Exception ex ) when( ex is IOException or UnauthorizedAccessException )
      {
         return Array.Empty<BenchFolderFacts>();
      }
   }

   /// <summary>
   /// The folders that use a run, as marks for the run list and the run page: published, candidate and
   /// withdrawn folders always, a blocked folder only when no other folder uses the run.
   /// Why: the blocked set's runs are reused by the published set, and the page that publishes them
   /// must not call them blocked.
   /// </summary>
   /// <param name="facts">Every folder's facts.</param>
   /// <param name="run">The run's folder name.</param>
   /// <returns>The folders and the role the run plays in each, best first.</returns>
   public static IReadOnlyList<( BenchFolderFacts Folder, string Role )> MarksFor( IReadOnlyList<BenchFolderFacts> facts, string run )
   {
      List<( BenchFolderFacts Folder, string Role )> all = facts.Where( f => f.Kind != BenchFolderKind.Run )
         .SelectMany( f => f.Runs.Where( r => r.Run == run ).Select( r => ( Folder: f, r.Role ) ) ).ToList();
      List<( BenchFolderFacts Folder, string Role )> kept = all.Where( m => m.Folder.Kind != BenchFolderKind.Blocked ).ToList();
      return ( kept.Count > 0 ? kept : all ).OrderBy( m => Priority( m.Folder.Kind ) ).ThenBy( m => m.Folder.Folder, StringComparer.Ordinal ).ToList();
   }

   /// <summary>
   /// The dropped clock warnings that name a run, from every folder that is not blocked.
   /// </summary>
   /// <param name="facts">Every folder's facts.</param>
   /// <param name="run">The run's folder name.</param>
   /// <returns>The warnings with the folder that dropped each.</returns>
   public static IReadOnlyList<( string Folder, BenchDroppedWarning Warning )> DroppedFor( IReadOnlyList<BenchFolderFacts> facts, string run )
   {
      return facts.Where( f => f.Kind is not ( BenchFolderKind.Blocked or BenchFolderKind.Run ) )
         .SelectMany( f => f.Dropped.Where( w => BenchFolderNames.LastSegment( w.Run ) == run ).Select( w => ( f.Folder, w ) ) ).ToList();
   }

   /// <summary>
   /// The reuse sentences that belong on the page of a run: for each folder the run is marked with, the sentences whose slot names the session the run belongs to in
   /// that folder, in the part the run plays there (a run of a compared session, or a run that feeds the threshold). A run that a folder does not use gets
   /// none of that folder's sentences, and a run of another session gets none of this session's.
   /// Why by the slot: a sentence written for the runs of one session (for example that a blocked report used them) is false on the page of a run of another session.
   /// </summary>
   /// <param name="facts">Every folder's facts.</param>
   /// <param name="run">The run's folder name.</param>
   /// <returns>The sentences with the folder each comes from, a sentence once per folder.</returns>
   public static IReadOnlyList<( BenchFolderFacts Folder, BenchSentence Sentence )> ReuseFor( IReadOnlyList<BenchFolderFacts> facts, string run )
   {
      var found = new List<( BenchFolderFacts Folder, BenchSentence Sentence )>();
      foreach( BenchFolderFacts folder in MarksFor( facts, run ).Select( m => m.Folder ).Distinct() )
      {
         foreach( BenchRunRef use in folder.Runs.Where( r => r.Run == run ) )
         {
            foreach( BenchSentence sentence in folder.Reuse.Where( s => BenchSlots.ReuseTarget( s.Slot ) is { } t && t.Session == use.Session && ( t.Role == null || t.Role == ( use.IsBasis ? BenchSlots.ROLE_BASIS : BenchSlots.ROLE_CLAIM ) ) ) )
            {
               if( !found.Any( f => f.Folder == folder && f.Sentence == sentence ) )
               {
                  found.Add( ( folder, sentence ) );
               }
            }
         }
      }

      return found;
   }

   /// <summary>
   /// The notes the folders that use a run print beside an engine's name, by target: from the folder the run is marked with first (a published
   /// folder before a candidate or a withdrawn one), and a blocked folder's only when no other folder uses the run. The run page prints them
   /// beside the same engines, so a fact a set states about an engine (for example that it holds its data in memory) is not lost on the page of
   /// the run it was measured in.
   /// </summary>
   /// <param name="facts">Every folder's facts.</param>
   /// <param name="run">The run's folder name.</param>
   /// <returns>Target to note; empty when no folder that uses the run has one.</returns>
   public static IReadOnlyDictionary<string, BenchRowNote> NotesFor( IReadOnlyList<BenchFolderFacts> facts, string run )
   {
      var notes = new Dictionary<string, BenchRowNote>( StringComparer.Ordinal );
      foreach( ( BenchFolderFacts folder, string _ ) in MarksFor( facts, run ) )
      {
         foreach( KeyValuePair<string, BenchRowNote> note in folder.RowNotes ?? new Dictionary<string, BenchRowNote>() )
         {
            notes.TryAdd( note.Key, note.Value );
         }
      }

      return notes;
   }

   /// <summary>
   /// The corrections that belong on the page of a run: the entries of every folder that uses the run (a published, candidate or withdrawn folder, and a
   /// blocked folder only when no other folder uses the run) that name the run, then the entries of the list file that name it. An entry that says the same
   /// thing about the same statement of the same engine twice is listed once, from the folder first.
   /// Why the folders' entries and not only the published ones: a correction of a recorded statement is true of the run whichever set found it.
   /// </summary>
   /// <param name="facts">Every folder's facts.</param>
   /// <param name="run">The run's folder name.</param>
   /// <param name="fileNotes">The entries of the list file, or none.</param>
   /// <returns>The corrections, in the order the page numbers them.</returns>
   public static IReadOnlyList<BenchRunNote> RunNotesFor( IReadOnlyList<BenchFolderFacts> facts, string run, IReadOnlyList<BenchRunNote>? fileNotes = null )
   {
      IEnumerable<BenchRunNote> fromFolders = MarksFor( facts, run ).Select( m => m.Folder ).Distinct().SelectMany( f => f.RunNotes ?? Array.Empty<BenchRunNote>() );
      var notes = new List<BenchRunNote>();
      foreach( BenchRunNote note in fromFolders.Concat( fileNotes ?? Array.Empty<BenchRunNote>() ).Where( n => n.Runs.Contains( run, StringComparer.Ordinal ) ) )
      {
         if( !notes.Any( n => n.Target == note.Target && n.Statement == note.Statement && n.Text == note.Text ) )
         {
            notes.Add( note );
         }
      }

      return notes;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The facts of one folder; a file that cannot be read is a record with its error.
   /// </summary>
   /// <param name="folder">Folder name.</param>
   /// <param name="path">consolidated.json path.</param>
   /// <returns>The record.</returns>
   private static BenchFolderFacts Facts( string folder, string path )
   {
      BenchFolderKind kind = BenchFolderNames.KindOf( folder );
      try
      {
         string json = BenchRunList.ReadCapped( path );
         return BenchConsolidatedReader.Detect( json ) switch
         {
            BenchShape.Consolidated => FromConsolidated( folder, kind, BenchConsolidatedReader.Read( json ) ),
            BenchShape.Report => FromReport( folder, kind, json ),
            _ => new BenchFolderFacts( folder, kind, BenchShape.KeyedByEngine, EngineCount( json ), Array.Empty<BenchRunRef>(), Array.Empty<BenchSentence>(), Array.Empty<BenchDroppedWarning>(), null ),
         };
      }
      catch( Exception ex ) when( ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException )
      {
         return new BenchFolderFacts( folder, kind, null, 0, Array.Empty<BenchRunRef>(), Array.Empty<BenchSentence>(), Array.Empty<BenchDroppedWarning>(), ex.Message );
      }
   }

   /// <summary>
   /// The facts of a v8 file.
   /// </summary>
   /// <param name="folder">Folder name.</param>
   /// <param name="kind">Its kind.</param>
   /// <param name="model">The file, read.</param>
   /// <returns>The record.</returns>
   private static BenchFolderFacts FromConsolidated( string folder, BenchFolderKind kind, BenchConsolidated model )
   {
      var runs = new List<BenchRunRef>();
      foreach( BenchUsedRun used in model.UsedRunRefs )
      {
         string run = BenchFolderNames.LastSegment( used.Folder );
         if( BenchFolderNames.IsSafe( run ) && !runs.Any( r => r.Run == run && r.Role == used.Role ) )
         {
            runs.Add( new BenchRunRef( run, used.Role, used.Session, used.IsBasis ) );
         }
      }

      List<BenchSentence> reuse = model.Sentences.Where( s => s.Slot == REUSE_SLOT || s.Slot.StartsWith( BenchSlots.REUSE_PREFIX, StringComparison.Ordinal ) ).ToList();
      int engines = model.Metrics.SelectMany( m => m.Rows.Select( r => r.Target ) ).Distinct( StringComparer.Ordinal ).Count();
      Dictionary<string, BenchRowNote> notes = model.Metrics.SelectMany( m => m.Rows ).Where( r => !string.IsNullOrWhiteSpace( r.Note ) )
         .GroupBy( r => r.Target, StringComparer.Ordinal ).ToDictionary( g => g.Key, g => new BenchRowNote( g.First().Note!, g.First().NoteSourceList ), StringComparer.Ordinal );
      List<BenchRunNote> runNotes = model.RunNoteList.Select( n => n with { Origin = folder } ).ToList();
      return new BenchFolderFacts( folder, kind, BenchShape.Consolidated, engines, runs, reuse, model.Clock?.Dropped ?? new List<BenchDroppedWarning>(), null, notes, runNotes );
   }

   /// <summary>
   /// The facts of a file in the shape of the consolidate command before v8: its runs are named in runs[].name.
   /// </summary>
   /// <param name="folder">Folder name.</param>
   /// <param name="kind">Its kind.</param>
   /// <param name="json">File text.</param>
   /// <returns>The record.</returns>
   private static BenchFolderFacts FromReport( string folder, BenchFolderKind kind, string json )
   {
      using JsonDocument doc = JsonDocument.Parse( json );
      JsonElement root = doc.RootElement;
      var runs = new List<BenchRunRef>();
      if( root.TryGetProperty( "runs", out JsonElement list ) && list.ValueKind == JsonValueKind.Array )
      {
         foreach( JsonElement r in list.EnumerateArray().Where( r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty( "name", out JsonElement n ) && n.ValueKind == JsonValueKind.String ) )
         {
            string run = BenchFolderNames.LastSegment( r.GetProperty( "name" ).GetString()! );
            if( BenchFolderNames.IsSafe( run ) )
            {
               runs.Add( new BenchRunRef( run, "runs" ) );
            }
         }
      }

      int engines = root.TryGetProperty( "targetSummaries", out JsonElement summaries ) && summaries.ValueKind == JsonValueKind.Array ? summaries.GetArrayLength() : 0;
      return new BenchFolderFacts( folder, kind, BenchShape.Report, engines, runs, Array.Empty<BenchSentence>(), Array.Empty<BenchDroppedWarning>(), null );
   }

   /// <summary>
   /// How many engines an old keyed file holds.
   /// </summary>
   /// <param name="json">File text.</param>
   /// <returns>The count.</returns>
   private static int EngineCount( string json )
   {
      using JsonDocument doc = JsonDocument.Parse( json );
      return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.EnumerateObject().Count() : 0;
   }

   /// <summary>
   /// Where a kind sits among the marks of a run: published first.
   /// </summary>
   /// <param name="kind">The kind.</param>
   /// <returns>The position.</returns>
   private static int Priority( BenchFolderKind kind )
   {
      return kind switch
      {
         BenchFolderKind.Published => 0,
         BenchFolderKind.Candidate => 1,
         BenchFolderKind.Withdrawn => 2,
         _ => 3,
      };
   }

   #endregion Private Methods
}
