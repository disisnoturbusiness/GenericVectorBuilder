using System.Text.Json;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The sentence audit and the source resolver behind it, run on the three real v7 results.json
/// files, the repository tree and a small synthetic consolidated document: a sound sentence
/// passes, and each of a stale value, an unbound number, a banned word, a 35-word headline and a
/// wrong quote fails.
/// Why: the audit is what stops a false sentence from reaching the report, so every rule is
/// shown to fail on the thing it exists to catch, not only to pass on a good sentence.
/// Why the source is compiled with Roslyn: see <see cref="ReportSourcesHarness"/>.
/// </summary>
public sealed class SentenceAuditTests : IDisposable
{
   #region Data Members

   private const string FIRST_RUN = "20261006-130619-eshoponweb";
   private const string CONSOLIDATED = "{\"threshold\":{\"tBp\":3500},\"counts\":{\"vectors\":1043},\"clock\":{\"uncore\":\"0x1e1e\",\"from\":\"2026-10-06\"},\"metrics\":[{\"metric\":\"p50Ms\",\"rows\":[{\"target\":\"oracle\",\"median\":4.97305},{\"target\":\"redis\",\"median\":1.5}]}],\"version\":\"26.3.39.7\"}";

   private readonly string _root;

   #endregion Data Members

   #region Constructor

   /// <summary>
   /// Creates a private folder for this test's synthetic files, under the test binaries, removed
   /// again in <see cref="Dispose"/>.
   /// </summary>
   public SentenceAuditTests()
   {
      _root = Path.Combine( AppContext.BaseDirectory, "sentence-audit-tests", Guid.NewGuid().ToString( "N" ) );
      Directory.CreateDirectory( _root );
   }

   #endregion Constructor

   #region Public Methods

   /// <summary>
   /// The banned list is exactly the design's twelve entries, in one place.
   /// </summary>
   [Fact]
   public Task BannedList_IsTheTwelveWordsOfTheDesign()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string[] expected = { "because", "due to", "since", "bottleneck", "overhead", "waiting", "share of", "accounts for", "explains", "percent of the latency", "thanks to", "leads to" };
         Assert.Equal( expected, (string[])ReportSourcesHarness.Call( "BannedList" )! );
      } );
   }

   /// <summary>
   /// The one rule: whole words, case-insensitive, a space in a phrase matches any run of white
   /// space. Every entry is found; look-alikes that merely contain one are not.
   /// </summary>
   [Fact]
   public Task BannedRule_IsWholeWordsCaseInsensitive()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         foreach( string word in (string[])ReportSourcesHarness.Call( "BannedList" )! )
         {
            Assert.Equal( new[] { word }, (string[])ReportSourcesHarness.Call( "Banned", $"It is slower, {word.ToUpperInvariant()} the client." )! );
            Assert.Equal( new[] { word }, (string[])ReportSourcesHarness.Call( "Banned", $"{word}." )! );
         }

         Assert.Equal( new[] { "due to" }, (string[])ReportSourcesHarness.Call( "Banned", "Slower due\n   to the client." )! );
         Assert.Equal( new[] { "because", "since" }, (string[])ReportSourcesHarness.Call( "Banned", "Because it ran, since then." )! );
         Assert.Empty( (string[])ReportSourcesHarness.Call( "Banned", "A sincere answer from an awaiting client, no bottlenecks, overheads or waitings." )! );
         Assert.Empty( (string[])ReportSourcesHarness.Call( "Banned", "Redis holds its data in memory." )! );
      } );
   }

   /// <summary>
   /// Both found at once come back in list order.
   /// </summary>
   [Fact]
   public Task BannedRule_ReturnsEachEntryOnceInListOrder()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         Assert.Equal( new[] { "because", "since" }, (string[])ReportSourcesHarness.Call( "Banned", "since then, because since" )! );
      } );
   }

   /// <summary>
   /// Words are counted the way a reader counts them: pieces that hold a letter or a digit.
   /// </summary>
   [Fact]
   public Task WordCount_CountsPiecesWithALetterOrDigit()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         Assert.Equal( 0, ReportSourcesHarness.Call( "Words", "" ) );
         Assert.Equal( 3, ReportSourcesHarness.Call( "Words", "  one   two\tthree " ) );
         Assert.Equal( 4, ReportSourcesHarness.Call( "Words", "1.35 times - as long" ) );
         Assert.Equal( 2, ReportSourcesHarness.Call( "Words", "a\nb" ) );
      } );
   }

   /// <summary>
   /// A sentence whose number comes from a raw run field passes, and every number in it is checked.
   /// </summary>
   [Fact]
   public Task SoundSentence_BoundToARawRunField_Passes()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string raw = RawP50( "clickhouse" );
         string shown = Math.Round( double.Parse( raw, System.Globalization.CultureInfo.InvariantCulture ), 2 ).ToString( "0.00", System.Globalization.CultureInfo.InvariantCulture );
         string[] failures = Audit( ( "findings", $"ClickHouse took {shown} ms at p50 in the first run.", new[] { Source( "results", $"results@{FIRST_RUN}:targets[clickhouse].search.p50Ms", shown ) } ) );
         Assert.Empty( failures );
         Assert.Contains( ".", raw );
      } );
   }

   /// <summary>
   /// A stale value: the sentence carries a value that is not what the raw field says, and a
   /// sentence whose text says one number while its source says another.
   /// </summary>
   [Fact]
   public Task StaleValue_AndMismatchedText_Fail()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string raw = RawP50( "clickhouse" );
         string stale = ( double.Parse( raw, System.Globalization.CultureInfo.InvariantCulture ) + 1 ).ToString( "0.00", System.Globalization.CultureInfo.InvariantCulture );
         string[] failures = Audit( ( "findings", $"ClickHouse took {stale} ms at p50.", new[] { Source( "results", $"results@{FIRST_RUN}:targets[clickhouse].search.p50Ms", stale ) } ) );
         string failure = Assert.Single( failures );
         Assert.StartsWith( "findings|source|results", failure );
         Assert.Contains( "is stale or wrong", failure );
         Assert.Contains( $"the sentence says '{stale}'", failure );

         string shown = Math.Round( double.Parse( raw, System.Globalization.CultureInfo.InvariantCulture ), 2 ).ToString( "0.00", System.Globalization.CultureInfo.InvariantCulture );
         string[] text = Audit( ( "findings", $"ClickHouse took {stale} ms at p50.", new[] { Source( "results", $"results@{FIRST_RUN}:targets[clickhouse].search.p50Ms", shown ) } ) );
         Assert.Single( text );
         Assert.Contains( $"'{stale}' is not bound to any source", text[0] );
      } );
   }

   /// <summary>
   /// A number with no source at all is unbound, however plausible.
   /// </summary>
   [Fact]
   public Task UnboundNumber_Fails()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string[] failures = Audit( ( "findings", "Redis answered 2.5 times as fast.", Array.Empty<object>() ) );
         string failure = Assert.Single( failures );
         Assert.StartsWith( "findings|number|", failure );
         Assert.Contains( "'2.5'", failure );
         Assert.Empty( Audit( ( "findings", "Redis answered quickly.", Array.Empty<object>() ) ) );
      } );
   }

   /// <summary>
   /// Printed forms are matched to their source: a percent to basis points, a ratio to basis points,
   /// a thousands comma to a plain count, a rounded figure to its raw value. A wrong threshold bound
   /// to a real reference is refused (the reference exists, the value is wrong).
   /// </summary>
   [Fact]
   public Task PrintedForms_AreBoundToConsolidatedValues()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string consolidated = WriteConsolidated();
         string[] good = Audit( consolidated, ( "threshold", "A pair needs 35% and 1.35x across 1,043 vectors, and Oracle's median was 4.97 ms.", new object[]
         {
            Source( "consolidated", "consolidated:threshold.tBp|bp-pct", "35" ),
            Source( "consolidated", "consolidated:threshold.tBp|bp-ratio", "1.35" ),
            Source( "consolidated", "consolidated:counts.vectors", "1043" ),
            Source( "consolidated", "consolidated:metrics[metric=p50Ms].rows[target=oracle].median", "4.97" ),
         } ) );
         Assert.Empty( good );

         string[] wrong = Audit( consolidated, ( "threshold", "A pair needs 40%.", new object[] { Source( "consolidated", "consolidated:threshold.tBp|bp-pct", "40" ) } ) );
         string failure = Assert.Single( wrong );
         Assert.Contains( "the source says '35', the sentence says '40'", failure );

         string[] noDocument = Audit( ( "threshold", "A pair needs 35%.", new object[] { Source( "consolidated", "consolidated:threshold.tBp|bp-pct", "35" ) } ) );
         Assert.Contains( "no consolidated document was given", Assert.Single( noDocument ) );
         string[] badPath = Audit( consolidated, ( "threshold", "A pair needs 35%.", new object[] { Source( "consolidated", "consolidated:threshold.nope|bp-pct", "35" ) } ) );
         Assert.Contains( "no value at 'nope'", Assert.Single( badPath ) );
         string[] badConversion = Audit( consolidated, ( "threshold", "A pair needs 35%.", new object[] { Source( "consolidated", "consolidated:threshold.tBp|percent", "35" ) } ) );
         Assert.Contains( "unknown conversion 'percent'", Assert.Single( badConversion ) );
      } );
   }

   /// <summary>
   /// Rounding is to the decimals the sentence shows, half away from zero; a figure off by one in
   /// the last shown place is wrong; thousands commas, a percent sign and an x are ignored.
   /// </summary>
   [Fact]
   public Task Agreement_RoundsToTheDecimalsShown()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         Assert.True( (bool)ReportSourcesHarness.Call( "Agrees", "4.97305", "4.97" )! );
         Assert.True( (bool)ReportSourcesHarness.Call( "Agrees", "4.97305", "5.0" )! );
         Assert.True( (bool)ReportSourcesHarness.Call( "Agrees", "4.97305", "5" )! );
         Assert.True( (bool)ReportSourcesHarness.Call( "Agrees", "2.5", "3" )! );
         Assert.True( (bool)ReportSourcesHarness.Call( "Agrees", "1.005", "1.01" )! );
         Assert.True( (bool)ReportSourcesHarness.Call( "Agrees", "1043", "1,043" )! );
         Assert.True( (bool)ReportSourcesHarness.Call( "Agrees", "35", "35%" )! );
         Assert.True( (bool)ReportSourcesHarness.Call( "Agrees", "1.35", "1.350" )! );
         Assert.True( (bool)ReportSourcesHarness.Call( "Agrees", "NO HNSW GRAPH", "NO HNSW GRAPH" )! );
         Assert.False( (bool)ReportSourcesHarness.Call( "Agrees", "4.97305", "4.98" )! );
         Assert.False( (bool)ReportSourcesHarness.Call( "Agrees", "4.97305", "4.9" )! );
         Assert.False( (bool)ReportSourcesHarness.Call( "Agrees", "1.35", "1.36" )! );
         Assert.False( (bool)ReportSourcesHarness.Call( "Agrees", "abc", "abd" )! );
         Assert.False( (bool)ReportSourcesHarness.Call( "Agrees", "0x1e1e", "0x1e1f" )! );
      } );
   }

   /// <summary>
   /// Names with digits in them are not numbers but are not free either: metric and session names
   /// (p50, QPS@8, v7) pass; a hex value, a date, a version and a folder name must equal or sit in a source value.
   /// </summary>
   [Fact]
   public Task IdentifiersWithDigits_NeedASourceExceptMetricNames()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string consolidated = WriteConsolidated();
         Assert.Empty( Audit( ( "findings", "In v7 and v8 the p50 and QPS@8 rows differ.", Array.Empty<object>() ) ) );
         string[] hex = Audit( ( "disclosure", "The uncore was held at 0x1e1e.", Array.Empty<object>() ) );
         Assert.Contains( "'0x1e1e' is not bound", Assert.Single( hex ) );
         string[] bound = Audit( consolidated, ( "disclosure", "The uncore was held at 0x1e1e from 2026-10-06 on 26.3.39.7.", new object[]
         {
            Source( "consolidated", "consolidated:clock.uncore", "0x1e1e" ),
            Source( "consolidated", "consolidated:clock.from", "2026-10-06" ),
            Source( "consolidated", "consolidated:version", "26.3.39.7" ),
         } ) );
         Assert.Empty( bound );
         string[] wrongHex = Audit( consolidated, ( "disclosure", "The uncore was held at 0x1e1f.", new object[] { Source( "consolidated", "consolidated:clock.uncore", "0x1e1e" ) } ) );
         Assert.Contains( "'0x1e1f' is not bound", Assert.Single( wrongHex ) );
         string[] folder = Audit( ( "runs", $"Run {FIRST_RUN} was used.", new object[] { Source( "results", $"results@{FIRST_RUN}:runSeed", "701" ) } ) );
         Assert.Contains( $"'{FIRST_RUN}' is not bound", Assert.Single( folder ) );
      } );
   }

   /// <summary>
   /// A number sitting inside a token or quote source is bound by it, and one that does not is not.
   /// </summary>
   [Fact]
   public Task NumberInsideATokenSource_IsBound()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         object redis = Source( "file", "deploy/engines/redis.compose.yaml#--save \"300 1\"", "--save \"300 1\"" );
         Assert.Empty( Audit( ( "disclosure", "Redis is set to save \"300 1\".", new[] { redis } ) ) );
         string[] wrong = Audit( ( "disclosure", "Redis is set to save \"301 1\".", new[] { redis } ) );
         Assert.Contains( "'301' is not bound", Assert.Single( wrong ) );
         object es = Source( "results", "results:targets[elasticsearch].load.indexNote#NO HNSW GRAPH, searches scan all 524 vectors", "NO HNSW GRAPH, searches scan all 524 vectors" );
         Assert.Empty( Audit( ( "why", "Elasticsearch scanned all 524 vectors.", new[] { es } ) ) );
         Assert.Single( Audit( ( "why", "Elasticsearch scanned all 5240 vectors.", new[] { es } ) ) );
      } );
   }

   /// <summary>
   /// A sentence with a banned word fails, for every word of the list, and a clean one does not.
   /// </summary>
   [Fact]
   public Task BannedWordInASentence_Fails()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         foreach( string word in (string[])ReportSourcesHarness.Call( "BannedList" )! )
         {
            string failure = Assert.Single( Audit( ( "why", $"Redis was faster, {word} the client.", Array.Empty<object>() ) ) );
            Assert.StartsWith( "why|banned|", failure );
            Assert.Contains( $"'{word}'", failure );
         }

         Assert.Empty( Audit( ( "why", "Redis was faster in each session.", Array.Empty<object>() ) ) );
      } );
   }

   /// <summary>
   /// Word limits: the headline holds 34 words and a 35-word headline fails; any other sentence
   /// holds 35 and a 36-word one fails; a slot that begins "headline." is a headline.
   /// </summary>
   [Fact]
   public Task WordLimits_Are34ForTheHeadlineAnd35ForTheRest()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         Assert.Empty( Audit( ( "headline", Words( 34 ), Array.Empty<object>() ) ) );
         string headline = Assert.Single( Audit( ( "headline", Words( 35 ), Array.Empty<object>() ) ) );
         Assert.StartsWith( "headline|length|35 words, the limit for this slot is 34", headline );
         Assert.Single( Audit( ( "headline.redis", Words( 35 ), Array.Empty<object>() ) ) );
         Assert.Empty( Audit( ( "threshold", Words( 35 ), Array.Empty<object>() ) ) );
         string other = Assert.Single( Audit( ( "threshold", Words( 36 ), Array.Empty<object>() ) ) );
         Assert.StartsWith( "threshold|length|36 words, the limit for this slot is 35", other );
      } );
   }

   /// <summary>
   /// A recorded text is quoted, not rewritten: a long recorded durability note that holds banned
   /// words passes as a quote and fails when it is reworded or sits in an ordinary sentence.
   /// </summary>
   [Fact]
   public Task RecordedQuote_IsCheckedAgainstTheRawFieldAndExemptFromTheTextRules()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         (string target, string text) = LongDurabilityWithABannedWord();
         Assert.True( (int)ReportSourcesHarness.Call( "Words", text )! > 35 );
         Assert.NotEmpty( (string[])ReportSourcesHarness.Call( "Banned", text )! );
         string reference = $"results@{FIRST_RUN}:targets[{target}].durability";
         Assert.Empty( Audit( ( "durability.quote", text, new[] { Source( "quote", reference, text ) } ) ) );

         string reworded = text.Replace( "the", "a" );
         string failure = Assert.Single( Audit( ( "durability.quote", reworded, new[] { Source( "quote", reference, reworded ) } ) ) );
         Assert.Contains( "the recorded field does not contain the quoted text", failure );

         string[] ordinary = Audit( ( "durability.note", text, new[] { Source( "results", reference + "#" + text[..20], text[..20] ) } ) );
         Assert.Contains( ordinary, f => f.Contains( "|banned|" ) );
         Assert.Contains( ordinary, f => f.Contains( "|length|" ) );

         string[] mixed = Audit( ( "durability.quote", text, new[] { Source( "quote", reference, text ), Source( "results", reference + "#" + text[..20], text[..20] ) } ) );
         Assert.Contains( "a quote sentence has exactly one source", Assert.Single( mixed ) );
         string[] different = Audit( ( "durability.quote", text, new[] { Source( "quote", reference, text[..40] ) } ) );
         Assert.Contains( "its value is the text", Assert.Single( different ) );
      } );
   }

   /// <summary>
   /// Em and en dashes are refused in every sentence, a quote included.
   /// </summary>
   [Fact]
   public Task Dashes_AreRefused()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         Assert.Contains( "|dash|", Assert.Single( Audit( ( "why", "Redis \u2014 in memory.", Array.Empty<object>() ) ) ) );
         Assert.Contains( "|dash|", Assert.Single( Audit( ( "why", "Redis \u2013 in memory.", Array.Empty<object>() ) ) ) );
      } );
   }

   /// <summary>
   /// Shape: a missing slot, an empty text, an unknown kind and an empty reference are structure
   /// failures, and the other rules do not then run on a sentence that cannot be read.
   /// </summary>
   [Fact]
   public Task MalformedSentences_AreStructureFailures()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         Assert.Contains( "|structure|", Assert.Single( Audit( ( "", "Text.", Array.Empty<object>() ) ) ) );
         Assert.Contains( "|structure|", Assert.Single( Audit( ( "why", " ", Array.Empty<object>() ) ) ) );
         Assert.Contains( "|structure|", Assert.Single( Audit( ( "why", "Text 5.", new[] { Source( "invented", "x", "5" ) } ) ) ) );
         Assert.Contains( "|structure|", Assert.Single( Audit( ( "why", "Text 5.", new[] { Source( "results", "", "5" ) } ) ) ) );
      } );
   }

   /// <summary>
   /// Results references: one run by "@folder", all runs when none is named (they must agree), a
   /// value that differs between runs refused, a token that must be in every run, selectors by name,
   /// index and key=value, and malformed paths reported as findings.
   /// </summary>
   [Fact]
   public Task ResultsReferences_ResolveAsDocumented()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string[] runs = ReportSourcesHarness.V7Runs();
         string root = ReportSourcesHarness.RepoRoot();
         string Resolve( string reference ) => (string)ReportSourcesHarness.Call( "Resolve", "results", reference, runs, root )!;
         Assert.StartsWith( "FOUND 701", Resolve( $"results@{FIRST_RUN}:runSeed" ) );
         Assert.StartsWith( "MISSING", Resolve( "results:runSeed" ) );
         Assert.Contains( "differs between the runs", Resolve( "results:runSeed" ) );
         Assert.StartsWith( "FOUND 524", Resolve( "results:rows" ) );
         Assert.StartsWith( "FOUND NO HNSW GRAPH", Resolve( "results:targets[elasticsearch].load.indexNote#NO HNSW GRAPH" ) );
         Assert.StartsWith( "FOUND elasticsearch", Resolve( "results:targets[name=elasticsearch].name" ) );
         Assert.StartsWith( "FOUND elasticsearch", Resolve( $"results@{FIRST_RUN}:targets[3].name" ) );
         Assert.Contains( "nothing matches [nope]", Resolve( "results:targets[nope].name" ) );
         Assert.Contains( "nothing matches [99]", Resolve( "results:targets[99].name" ) );
         Assert.Contains( "token not in", Resolve( "results:targets[elasticsearch].index#absent words" ) );
         Assert.Contains( "run 'nope' is not among the runs given", Resolve( "results@nope:rows" ) );
         Assert.Contains( "missing ']'", Resolve( "results:targets[3.name" ) );
         Assert.Contains( "empty path segment", Resolve( "results:targets..name" ) );
         Assert.Contains( "is not a single value", Resolve( "results:targets[elasticsearch].load" ) );
         Assert.Contains( "no value at 'indexNote'", Resolve( "results:targets[sql].load.indexNote" ) );
         Assert.Contains( "no runs were given", (string)ReportSourcesHarness.Call( "Resolve", "results", "results:rows", Array.Empty<string>(), root )! );
      } );
   }

   /// <summary>
   /// A recorded setting is cited as a "name=value" token on its object: the pair must be in every run (a name with a dot, which a path cannot reach,
   /// is found too), a wrong value or name is not, and a sentence that prints a setting passes the audit with it and fails with a wrong one.
   /// </summary>
   [Fact]
   public Task SettingsPairTokens_ResolveOnTheSearchSettingsObject()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string[] runs = ReportSourcesHarness.V7Runs();
         string root = ReportSourcesHarness.RepoRoot();
         string Resolve( string reference ) => (string)ReportSourcesHarness.Call( "Resolve", "results", reference, runs, root )!;
         Assert.StartsWith( "FOUND mhnsw_ef_search=100", Resolve( "results:targets[mariadb].searchSettings#mhnsw_ef_search=100" ) );
         Assert.StartsWith( "FOUND hnsw.ef_search=100", Resolve( "results:targets[pgvector].searchSettings#hnsw.ef_search=100" ) );
         Assert.StartsWith( "FOUND numCandidates=20x", Resolve( "results:targets[mongodb].searchSettings#numCandidates=20x" ) );
         Assert.StartsWith( "MISSING", Resolve( "results:targets[mariadb].searchSettings#mhnsw_ef_search=20" ) );
         Assert.StartsWith( "MISSING", Resolve( "results:targets[mariadb].searchSettings#ef_search=100" ) );
         Assert.StartsWith( "MISSING", Resolve( "results:targets[mariadb].searchSettings#mhnsw_ef_search=1000" ) );
         string good = JsonSerializer.Serialize( new[] { new { slot = "why", text = "mariadb recorded mhnsw_ef_search=100.", sources = new[] { new { kind = "results", @ref = "results:targets[mariadb].searchSettings#mhnsw_ef_search=100", value = "mhnsw_ef_search=100" } } } } );
         Assert.Empty( (string[])ReportSourcesHarness.Call( "Audit", good, runs, root, string.Empty )! );
         string wrong = good.Replace( "searchSettings#mhnsw_ef_search=100", "searchSettings#mhnsw_ef_search=20", StringComparison.Ordinal );
         Assert.Contains( "|source|", Assert.Single( (string[])ReportSourcesHarness.Call( "Audit", wrong, runs, root, string.Empty )! ) );
      } );
   }

   /// <summary>
   /// Every row of the real fact sheet becomes a sentence of its own text with its own source and
   /// passes the audit: the bridge a report generator uses to cite a fact without retyping it.
   /// </summary>
   [Fact]
   public Task EveryRealFactRow_PassesTheAuditAsASentence()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         string facts = Path.Combine( ReportSourcesHarness.RepoRoot(), "src", "GenericVectorBuilder.Bench", "Report", "engine-facts.json" );
         using JsonDocument document = JsonDocument.Parse( File.ReadAllText( facts ) );
         var sentences = new List<( string, string, object[] )>();
         foreach( JsonElement row in document.RootElement.EnumerateArray() )
         {
            string source = row.GetProperty( "source" ).GetString()!;
            string kind = source.StartsWith( "results", StringComparison.Ordinal ) ? "results" : source.StartsWith( "doc:", StringComparison.Ordinal ) ? "doc" : source.StartsWith( "absent:", StringComparison.Ordinal ) ? "absent" : "file";
            string token = source[( source.IndexOf( '#' ) + 1 )..];
            sentences.Add( ( "why." + row.GetProperty( "target" ).GetString(), row.GetProperty( "text" ).GetString()!, new object[] { Source( kind, source, token ) } ) );
         }

         string[] failures = Audit( sentences.ToArray() );
         Assert.Empty( failures );
         Assert.True( sentences.Count >= 19 * 4 );
      } );
   }

   /// <summary>
   /// Sources that do not resolve are findings: a file token that is not in the file, a doc quote
   /// that is not in the page and an absence check that the runs contradict.
   /// </summary>
   [Fact]
   public Task SourcesThatDoNotResolve_AreFindings()
   {
      return ReportSourcesHarness.Timed( () =>
      {
         Assert.Contains( "token not on a code line", Assert.Single( Audit( ( "why", "Redis sets save.", new[] { Source( "file", "deploy/engines/redis.compose.yaml#--save never", "--save never" ) } ) ) ) );
         Assert.Contains( "quote not inside one text node", Assert.Single( Audit( ( "why", "Redis is a database.", new[] { Source( "doc", "doc:design/engine-docs/redis-faq-2026-10-07.html#not on the page", "not on the page" ) } ) ) ) );
         Assert.Contains( "occurs in targets[vespa]", Assert.Single( Audit( ( "why", "not recorded", new[] { Source( "absent", "absent:results:targets[vespa]#in-memory", "in-memory" ) } ) ) ) );
         Assert.Contains( "stale or wrong", Assert.Single( Audit( ( "why", "Redis sets save.", new[] { Source( "file", "deploy/engines/redis.compose.yaml#--save \"300 1\"", "--save \"60 1\"" ) } ) ) ) );
      } );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Builds a source object for the facade's JSON.
   /// </summary>
   /// <param name="kind">The source kind.</param>
   /// <param name="reference">The reference.</param>
   /// <param name="value">The value the sentence carries.</param>
   /// <returns>An object that serialises to a source.</returns>
   private static object Source( string kind, string reference, string value )
   {
      return new { kind, @ref = reference, value };
   }

   /// <summary>
   /// Audits sentences against the three real v7 runs and the repository tree.
   /// </summary>
   /// <param name="sentences">Tuples of slot, text and sources.</param>
   /// <returns>The failures as "slot|rule|message".</returns>
   private static string[] Audit( params ( string Slot, string Text, object[] Sources )[] sentences )
   {
      return AuditWith( string.Empty, sentences );
   }

   /// <summary>
   /// Audits sentences with a consolidated document as well.
   /// </summary>
   /// <param name="consolidated">The consolidated document's path.</param>
   /// <param name="sentences">Tuples of slot, text and sources.</param>
   /// <returns>The failures as "slot|rule|message".</returns>
   private static string[] Audit( string consolidated, params ( string Slot, string Text, object[] Sources )[] sentences )
   {
      return AuditWith( consolidated, sentences );
   }

   /// <summary>
   /// Serialises the sentences and calls the facade.
   /// </summary>
   /// <param name="consolidated">The consolidated document's path, or empty.</param>
   /// <param name="sentences">Tuples of slot, text and sources.</param>
   /// <returns>The failures as "slot|rule|message".</returns>
   private static string[] AuditWith( string consolidated, ( string Slot, string Text, object[] Sources )[] sentences )
   {
      string json = JsonSerializer.Serialize( sentences.Select( s => new { slot = s.Slot, text = s.Text, sources = s.Sources } ) );
      return (string[])ReportSourcesHarness.Call( "Audit", json, ReportSourcesHarness.V7Runs(), ReportSourcesHarness.RepoRoot(), consolidated )!;
   }

   /// <summary>
   /// The raw p50 of a target in the first v7 run, read independently of the resolver.
   /// </summary>
   /// <param name="target">The target name.</param>
   /// <returns>The number as written in the file.</returns>
   private static string RawP50( string target )
   {
      using JsonDocument document = JsonDocument.Parse( File.ReadAllText( ReportSourcesHarness.V7Runs()[0] ) );
      return document.RootElement.GetProperty( "targets" ).EnumerateArray().Single( t => t.GetProperty( "name" ).GetString() == target ).GetProperty( "search" ).GetProperty( "p50Ms" ).GetRawText();
   }

   /// <summary>
   /// A recorded durability note of the first v7 run that runs past 35 words and holds a banned word.
   /// </summary>
   /// <returns>The target and the note.</returns>
   private static (string Target, string Text) LongDurabilityWithABannedWord()
   {
      using JsonDocument document = JsonDocument.Parse( File.ReadAllText( ReportSourcesHarness.V7Runs()[0] ) );
      foreach( JsonElement target in document.RootElement.GetProperty( "targets" ).EnumerateArray() )
      {
         string text = target.GetProperty( "durability" ).GetString()!;
         if( (int)ReportSourcesHarness.Call( "Words", text )! > 35 && ( (string[])ReportSourcesHarness.Call( "Banned", text )! ).Length > 0 )
         {
            return ( target.GetProperty( "name" ).GetString()!, text );
         }
      }

      throw new InvalidOperationException( "No recorded durability note is over 35 words and holds a banned word." );
   }

   /// <summary>
   /// A text of exactly the given number of words.
   /// </summary>
   /// <param name="count">How many words.</param>
   /// <returns>The text.</returns>
   private static string Words( int count )
   {
      return string.Join( ' ', Enumerable.Repeat( "word", count ) );
   }

   /// <summary>
   /// Writes the synthetic consolidated document.
   /// </summary>
   /// <returns>Its path.</returns>
   private string WriteConsolidated()
   {
      string path = Path.Combine( _root, $"consolidated-{Guid.NewGuid():N}.json" );
      File.WriteAllText( path, CONSOLIDATED );
      return path;
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Removes this test's synthetic files.
   /// </summary>
   public void Dispose()
   {
      try
      {
         Directory.Delete( _root, true );
      }
      catch( IOException )
      {
         // A leftover folder under the test binaries is harmless; the next build clears it.
      }
   }

   #endregion IDisposable
}
