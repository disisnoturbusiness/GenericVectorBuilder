using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GenericVectorBuilder.Engines.Tests.Bench;

/// <summary>
/// The fixed words of the report: every template of ConsolidateText holds no cause word (P5's banned
/// list), no provenance, purpose, default or "only" word, and no dash; the why framing is the
/// design's fixed text; the client-CPU line of a run page says what the figure is and never splits
/// the latency; and the headline's three forms (none, one, several named engines, Redis with its
/// in-memory clause) read as the design says and fit 34 words.
/// </summary>
public sealed class ConsolidateTextTests : IDisposable
{
   #region Data Members

   /// <summary>Provenance, purpose, default and "only" words no template may hold.</summary>
   private static readonly Regex FORBIDDEN = new( @"(?<![A-Za-z0-9_@])(only|default|defaults|purpose|in order to|so that|intended|designed to|set by its license)(?![A-Za-z0-9_@])", RegexOptions.IgnoreCase | RegexOptions.Compiled );

   private readonly SyntheticBench _bench = new();

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Every template is free of banned, provenance, purpose, default and "only" words and of dashes.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Templates_HoldNoForbiddenWordOrDash()
   {
      await ConsolidateHarness.Timed( () =>
      {
         var templates = (string[])ConsolidateHarness.Call( "Templates" )!;
         Assert.True( templates.Length > 60, $"{templates.Length} templates" );
         foreach( string entry in templates )
         {
            string text = entry[( entry.IndexOf( '=' ) + 1 )..];
            Assert.Empty( (string[])ConsolidateHarness.Call( "Banned", text )! );
            Assert.False( FORBIDDEN.IsMatch( text ), entry );
            Assert.DoesNotContain( '\u2014', text );
            Assert.DoesNotContain( '\u2013', text );
         }
      } );
   }

   /// <summary>
   /// The why framing is the design's fixed three sentences.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task WhyFraming_IsTheDesignsText()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string[] framing = ( (string[])ConsolidateHarness.Call( "Templates" )! ).Where( t => t.StartsWith( "WHY_FRAMING=", StringComparison.Ordinal ) ).Select( t => t["WHY_FRAMING=".Length..] ).ToArray();
         Assert.Equal( new[] { "CPU per search is cost summed over every thread.", "It can exceed the time per search, so it is not a split of the latency.", "This test did not isolate causes." }, framing );
      } );
   }

   /// <summary>
   /// The client-CPU line of a run page drops "where it is close to the latency" and "overhead", and the retired words are listed for the page to mark.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task ClientCpuLine_NeverSplitsTheLatency()
   {
      await ConsolidateHarness.Timed( () =>
      {
         string line = (string)ConsolidateHarness.Call( "Framing", "CLIENT_CPU_LINE" )!;
         Assert.DoesNotContain( "close to the latency", line );
         Assert.Empty( (string[])ConsolidateHarness.Call( "Banned", line )! );
         Assert.Contains( "can exceed the time per search", line );
      } );
   }

   /// <summary>
   /// One engine ahead of every other: the one-engine headline. Redis named: its in-memory clause. Several named: in order.
   /// None named: the none headline. Each fits 34 words.
   /// </summary>
   /// <returns>A task.</returns>
   [Fact]
   public async Task Headline_NamesTheEnginesAheadOfEveryOther()
   {
      await ConsolidateHarness.Timed( () =>
      {
         Assert.Equal( "redis had the lowest p50 latency: every other engine took at least 1.35 times as long, in each session; redis holds its data in memory.",
            Headline( ( "redis", 1.0 ), ( "alpha", 2.0 ), ( "beta", 2.1 ) ) );
         Assert.Equal( "redis, alpha and beta had the lowest p50 latencies, in that order: each engine further down took at least 1.35 times as long, in each session; redis holds its data in memory.",
            Headline( ( "redis", 1.0 ), ( "alpha", 1.5 ), ( "beta", 2.2 ), ( "gamma", 3.2 ), ( "delta", 3.3 ) ) );
         Assert.Equal( "No engine had a p50 latency at least 1.35 times lower than every other engine in each session; the p50 table shows which pairs this test separated.",
            Headline( ( "alpha", 1.0 ), ( "beta", 1.1 ), ( "gamma", 3.0 ) ) );
      } );
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// Consolidates two sessions of targets at the given p50s (QPS follows) in a fresh scratch bench and returns the headline, checking its word count.
   /// </summary>
   /// <param name="targets">Name and p50 pairs.</param>
   /// <returns>The headline.</returns>
   private static string Headline( params (string Name, double P50)[] targets )
   {
      using var bench = new SyntheticBench();
      IEnumerable<JsonObject> Make( int i ) => targets.Select( t => SyntheticBench.Target( t.Name, t.P50 * ( 1 + 0.002 * i ), 1000 / t.P50, 4000 / t.P50 ) );
      string v7 = bench.WriteSession( "2026-10-06", 701, Make );
      string v8 = bench.WriteSession( "2026-10-08", 801, Make );
      ( int exit, JsonElement? json, _ ) = bench.Consolidate( "--session", "v7=" + v7, "--session", "v8=" + v8 );
      Assert.True( exit == 0, bench.LogText() );
      string headline = json!.Value.GetProperty( "sentences" ).EnumerateArray().First( s => s.GetProperty( "slot" ).GetString() == "headline" ).GetProperty( "text" ).GetString()!;
      Assert.True( (int)ConsolidateHarness.Call( "Words", headline )! <= 34, headline );
      return headline;
   }

   #endregion Private Methods

   #region IDisposable

   /// <summary>
   /// Removes the scratch folders.
   /// </summary>
   public void Dispose()
   {
      _bench.Dispose();
   }

   #endregion IDisposable
}
