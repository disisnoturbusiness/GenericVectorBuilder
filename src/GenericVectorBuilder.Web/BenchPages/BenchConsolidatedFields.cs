using System.Text.Json;

namespace GenericVectorBuilder.Web.BenchPages;

/// <summary>
/// Which fields of a consolidated.json the page draws, which it leaves out on purpose, and the check that finds the ones it does neither with.
/// Why a list of fields: the page draws the file's fields one by one, so a field the consolidate command adds and the page does not read is
/// silently missing from the page while the report still prints it. That happened to the pairs on the line and to the session an unconfirmed order
/// held in: both were in the file and in the report and not on the page, and the report's own sentences said they were listed. A field is therefore
/// either read (drawn), or left out on purpose (its words are in the file's sentences, or it is a count or a rule the sentences already state), or it
/// is listed on the page under "Fields this page does not read", where nobody can miss it.
/// Why the page is not refused instead: the numbers are right and the rest of the page is whole; a new field is a reason to update this list and
/// the page, and the list on the page says which.
/// </summary>
public static class BenchConsolidatedFields
{
   #region Data Members

   private static readonly Dictionary<string, ( string[] Read, string[] LeftOut )> LEVELS = new( StringComparer.Ordinal )
   {
      [string.Empty] = ( new[] { "sessions", "threshold", "basis", "metrics", "guards", "drift", "recall", "clock", "machine", "images", "engineSettings", "why", "searchSettings", "searchEffort", "sentences", "audit", "status", "runPageNotes", "recordedTexts", "queries", "build" },
         new[] { "format", "createdUtc", "commandLine", "mode", "stopReasons", "targets", "runsPerSession", "runsShown", "claimRunCount", "imageTargets", "headlineNamedCount", "reuse", "notInReport", "observer", "observerOthers", "notHeld", "recallDerivedSessions", "targetCount", "httpClientCount" } ),
      ["sessions[]"] = ( new[] { "name", "runs" }, Array.Empty<string>() ),
      ["sessions[].runs[]"] = ( new[] { "folder", "seed", "startedUtc", "resultsSha256" }, new[] { "staleLines" } ),
      ["threshold"] = ( new[] { "tBp", "ratio" }, new[] { "rule" } ),
      ["metrics[]"] = ( new[] { "metric", "lowerIsBetter", "rows" }, new[] { "noExactPass", "orderedPairs", "comparedPairs" } ),
      ["metrics[].rows[]"] = ( new[] { "target", "display", "status", "perSession", "median", "notSeparatedFrom", "flags", "note", "noteSources", "searchMode" }, new[] { "searchConfidence" } ),
      ["searchSettings[]"] = ( new[] { "target", "run", "settings", "source" }, new[] { "clause" } ),
      ["searchSettings[].settings[]"] = ( new[] { "key", "value" }, Array.Empty<string>() ),
      ["why[]"] = ( new[] { "target", "facts", "costs", "searchEffort", "searchSettings" }, Array.Empty<string>() ),
      ["why[].facts[]"] = ( new[] { "kind", "text", "source", "confidence", "mode", "where" }, new[] { "class" } ),
      ["why[].costs"] = ( new[] { "engineCpuMsPerSearch", "clientCpuMsPerSearch", "engineCpusBusyAt8" }, new[] { "clientIncludesEngine", "engineCpuNullReason", "session" } ),
      ["basis"] = ( new[] { "runs", "leftOut", "exclusions", "perMetric", "maxMove", "exclusionsKept", "noMachineControl", "maxBp", "tBp", "setupSplits", "setupSplitCount" },
         new[] { "floorBp", "stepBp", "marginBp", "tBpRule", "moveRule", "runCount", "leftOutCount", "noMachineControlCount", "maxMoveNotHeld", "maxMoveWithout", "clockHeld" } ),
      ["basis.runs[]"] = ( new[] { "folder", "seed", "session", "conditions", "startedUtc", "resultsSha256" }, new[] { "staleLines" } ),
      ["basis.setupSplits[]"] = ( new[] { "target", "changes", "before", "after", "oneEngine", "pair", "tBpIfCounted" }, new[] { "fields", "perMetric" } ),
      ["basis.setupSplits[].changes[]"] = ( new[] { "field", "before", "after" }, Array.Empty<string>() ),
      ["basis.setupSplits[].before"] = ( new[] { "sessions", "runs" }, Array.Empty<string>() ),
      ["basis.setupSplits[].after"] = ( new[] { "sessions", "runs" }, Array.Empty<string>() ),
      ["basis.setupSplits[].oneEngine"] = ( new[] { "metric", "moveBp", "target", "pair", "runs", "values", "differences" }, new[] { "move", "runsWithBoth", "differenceNames", "seeds", "moveBpExact" } ),
      ["basis.setupSplits[].pair"] = ( new[] { "metric", "moveBp", "target", "pair", "runs", "values", "differences" }, new[] { "move", "runsWithBoth", "differenceNames", "seeds", "moveBpExact" } ),
      ["drift"] = ( new[] { "from", "to", "perTarget", "medianAbsMoveBp", "largest", "unconfirmedOrders", "closeToLine", "onLine" },
         new[] { "largestAbsMoveBp", "closeFromBp", "closeCount", "onLineBp", "onLineCount", "unconfirmedFromFirst", "unconfirmedFromSecond", "sessionDifferences", "outsideLoad", "excluded" } ),
      ["drift.perTarget[]"] = ( new[] { "target", "metric", "moveBp" }, Array.Empty<string>() ),
      ["drift.unconfirmedOrders[]"] = ( new[] { "metric", "a", "b", "session" }, Array.Empty<string>() ),
      ["drift.closeToLine[]"] = ( new[] { "metric", "a", "b", "minRatioBp" }, Array.Empty<string>() ),
      ["drift.onLine[]"] = ( new[] { "metric", "a", "b", "minRatioBp" }, Array.Empty<string>() ),
      ["guards"] = ( new[] { "g1", "g2", "g3" }, Array.Empty<string>() ),
      ["guards.g2"] = ( new[] { "stopped", "pairs" }, Array.Empty<string>() ),
      ["guards.g3"] = ( new[] { "stopped", "targets" }, new[] { "changedCount", "limit" } ),
      ["guards.g3.targets[]"] = ( new[] { "target", "fields" }, Array.Empty<string>() ),
      ["clock"] = ( new[] { "perSession", "droppedWarnings" }, Array.Empty<string>() ),
      ["clock.droppedWarnings[]"] = ( new[] { "run", "text", "engineMedianMhz", "clientMedianMhz", "target", "pass" }, Array.Empty<string>() ),
      ["recall[]"] = ( new[] { "target", "hits", "of", "differs" }, Array.Empty<string>() ),
      ["images[]"] = ( new[] { "target", "ref", "id", "lastTagTimeUtc", "beforeFirstV7Start" }, Array.Empty<string>() ),
      ["engineSettings[]"] = ( new[] { "target", "session", "settings" }, Array.Empty<string>() ),
      ["engineSettings[].settings[]"] = ( new[] { "key", "value", "how" }, Array.Empty<string>() ),
      ["recordedTexts[]"] = ( new[] { "target", "field", "run", "note", "clauses" }, Array.Empty<string>() ),
      ["recordedTexts[].clauses[]"] = ( new[] { "text", "class", "basis", "printed", "notPrinted" }, new[] { "length", "unlisted" } ),
      ["runPageNotes[]"] = ( new[] { "runs", "run", "target", "field", "statement", "kind", "note", "sources" }, Array.Empty<string>() ),
      ["runPageNotes[].sources[]"] = ( new[] { "kind", "ref", "value" }, Array.Empty<string>() ),
      ["sentences[]"] = ( new[] { "slot", "text", "sources", "quote", "kind" }, Array.Empty<string>() ),
      ["sentences[].sources[]"] = ( new[] { "kind", "ref", "value" }, Array.Empty<string>() ),
      ["audit"] = ( new[] { "sentencesChecked", "failures", "factsSha256", "exclusionsSha256", "observerSha256", "factsChecked", "rowSentencesChecked" }, new[] { "observerSha256Others", "classesSha256", "runPageNotesSha256", "unlistedClauses" } ),
   };

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// The fields of a file that are neither read by the page nor left out on purpose, as paths ("drift.newField", "metrics[].rows[].newField"), once each,
   /// in the order the file holds them.
   /// </summary>
   /// <param name="root">The file's root object.</param>
   /// <returns>The paths; none when the page reads the file whole.</returns>
   public static IReadOnlyList<string> Unread( JsonElement root )
   {
      var found = new List<string>();
      Visit( root, string.Empty, found );
      return found;
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Checks one object against its level, then walks into each member that holds a level of its own (an object, or the elements of a list).
   /// </summary>
   /// <param name="element">The value.</param>
   /// <param name="path">Its level's path.</param>
   /// <param name="found">Receives the unread fields.</param>
   private static void Visit( JsonElement element, string path, List<string> found )
   {
      if( element.ValueKind != JsonValueKind.Object )
      {
         return;
      }

      if( LEVELS.TryGetValue( path, out ( string[] Read, string[] LeftOut ) level ) )
      {
         foreach( string name in element.EnumerateObject().Select( p => p.Name ).Where( n => !level.Read.Contains( n, StringComparer.Ordinal ) && !level.LeftOut.Contains( n, StringComparer.Ordinal ) ) )
         {
            string field = path.Length == 0 ? name : $"{path}.{name}";
            if( !found.Contains( field, StringComparer.Ordinal ) )
            {
               found.Add( field );
            }
         }
      }

      foreach( JsonProperty member in element.EnumerateObject() )
      {
         string child = path.Length == 0 ? member.Name : $"{path}.{member.Name}";
         if( member.Value.ValueKind == JsonValueKind.Array && HasLevel( child + "[]" ) )
         {
            member.Value.EnumerateArray().ToList().ForEach( item => Visit( item, child + "[]", found ) );
         }
         else if( member.Value.ValueKind == JsonValueKind.Object && HasLevel( child ) )
         {
            Visit( member.Value, child, found );
         }
      }
   }

   /// <summary>
   /// True when the list has a level at this path or below it.
   /// </summary>
   /// <param name="path">The path.</param>
   /// <returns>True when the walk must go in.</returns>
   private static bool HasLevel( string path )
   {
      return LEVELS.Keys.Any( k => k == path || k.StartsWith( path + ".", StringComparison.Ordinal ) || k.StartsWith( path + "[]", StringComparison.Ordinal ) );
   }

   #endregion Private Methods
}
