using System.Text.RegularExpressions;
using GenericVectorBuilder.Bench.Report;
using S = GenericVectorBuilder.Bench.Stats.ConsolidateSources;
using T = GenericVectorBuilder.Bench.Stats.ConsolidateText;

namespace GenericVectorBuilder.Bench.Stats;

/// <summary>
/// The disclosures of design section 4, each a sentence with a source: governor and partition,
/// boot, images, the Redis in-memory fact, durability (quoted), engine settings, CPU caps and
/// measured scans from the fact sheet, data-folder start state, segment layouts, the observer and
/// its clock check, systemd timers, C-states, and how dockerd's CPU was counted (hole H2: stated from the
/// code lines that do it, and quoted from the rule text the run recorded, which the code generates from what it did).
/// The durability quotes, their corrections and the saved logs are in <see cref="ConsolidateTextDurability"/>.
/// </summary>
public static class ConsolidateTextDisclosures
{
   #region Data Members

   /// <summary>The P1 test that holds v8's outside-load accounting to v7's.</summary>
   private const string ACCOUNTING_TEST_FILE = "tests/GenericVectorBuilder.Engines.Tests/Bench/MachineControlV8Tests.cs";

   /// <summary>Its method name, the token cited.</summary>
   private const string ACCOUNTING_TEST = "OutsideLoad_IsTheV7AccountingBitForBit";

   /// <summary>Where a run records how engine CPU and dockerd were counted.</summary>
   private const string ENGINE_CPU_RULE = "conditions.engineCpu.rule";

   /// <summary>The file whose code line adds dockerd's cgroup to the cgroups a compose engine's turn follows.</summary>
   private const string DOCKERD_FILE = "src/GenericVectorBuilder.Bench/Running/MachineControlPinning.cs";

   /// <summary>That line.</summary>
   private const string DOCKERD_TOKEN = "AddGroup( dockerd );";

   /// <summary>The file whose code line takes the followed cgroups' CPU out of the box's busy CPU.</summary>
   private const string OUTSIDE_FILE = "src/GenericVectorBuilder.Bench/Running/MachineControlSampler.cs";

   /// <summary>That line.</summary>
   private const string OUTSIDE_TOKEN = "double outside = ( reading.Busy - _last.Busy ) - ( reading.Self - _last.Self ) - engine;";

   /// <summary>The two sizes of a recorded disk note "N added by this load (SIZE (whole engine data folder), SIZE before)".</summary>
   private static readonly Regex DISK_SIZES = new( @"^.+? added by this load \((?<after>.+?) \(whole engine data folder\), (?<before>.+?) before\)$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds( 5 ) );

   #endregion Data Members

   #region Public Methods

   /// <summary>
   /// Writes every disclosure.
   /// </summary>
   /// <param name="w">The writer.</param>
   public static void Write( Writer w )
   {
      Governor( w );
      Boot( w );
      Images( w );
      Memory( w );
      ConsolidateTextDurability.Write( w );
      Settings( w );
      Facts( w );
      DataFolders( w );
      Layouts( w );
      Observer( w );
      Cstates( w );
      Dockerd( w );
      Scope( w );
   }

   /// <summary>
   /// The save and appendonly settings a target's compose file sets, from its set-by-setup facts (Redis: when a docs quote says it holds its data in memory,
   /// the reader needs what the compose file sets beside it).
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="target">Target.</param>
   /// <returns>The facts, in file order; empty when the target has none.</returns>
   public static List<WhyFact> ComposeSettings( Writer w, string target )
   {
      return w.Why( target )?.Facts.Where( f => f.Kind == "set-by-setup" && ( f.Text.StartsWith( "save", StringComparison.Ordinal ) || f.Text.StartsWith( "appendonly", StringComparison.Ordinal ) ) ).ToList() ?? new List<WhyFact>();
   }

   #endregion Public Methods

   #region Private Methods

   /// <summary>
   /// The governor, the client's CPUs and where each engine ran.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Governor( Writer w )
   {
      RunConditions c = w.Newest.Runs[0].Conditions;
      if( c.Governor == null || c.ClientCpus == null )
      {
         return;
      }

      w.Add( "disclosure.governor", T.Fill( T.DISCLOSURE_GOVERNOR, c.Governor, c.ClientCpus ), S.ResultAll( "conditions.governor", c.Governor ), S.ResultAll( "conditions.clientCpus", c.ClientCpus ) );
      Engines( w, c );
   }

   /// <summary>
   /// The CPUs each group of engines ran on, from each run's record of where each engine ran: container engines on the engine CPUs, embedded engines inside the client process on the client CPUs.
   /// Why per target and not the partition: the partition says where the engines were meant to run, and an embedded engine runs on the client's CPUs, so a sentence that says every engine ran on the engine CPUs is false for it.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="c">The newest session's first run's conditions.</param>
   private static void Engines( Writer w, RunConditions c )
   {
      List<EnginePinFacts> pins = w.Report.Targets.Select( t => c.Engines.FirstOrDefault( e => e.Target == t ) ).Where( e => e is { Cpus: not null } ).Select( e => e! ).ToList();
      if( pins.Count == 0 )
      {
         if( c.EngineCpus != null )
         {
            w.Add( "disclosure.engines.unknown", T.Fill( T.DISCLOSURE_ENGINES_UNKNOWN, c.EngineCpus ), S.ResultAll( "conditions.engineCpus", c.EngineCpus ) );
         }

         return;
      }

      int n = 0;
      foreach( IGrouping<( string? Hosting, string? Cpus ), EnginePinFacts> group in pins.GroupBy( e => ( e.Hosting, e.Cpus ) ) )
      {
         bool embedded = group.Key.Hosting == ConsolidateWhy.EMBEDDED;
         string noun = group.Key.Hosting == "compose" ? "container" : group.Key.Hosting ?? ConsolidateIdentity.NOT_RECORDED;
         var sources = new List<SentenceSource>();
         foreach( EnginePinFacts pin in group )
         {
            sources.Add( S.ResultAll( $"conditions.engines[target={pin.Target}].cpus", pin.Cpus! ) );
            sources.Add( S.ResultAll( $"conditions.engines[target={pin.Target}].hosting", pin.Hosting ?? string.Empty ) );
         }

         w.Add( $"disclosure.engines.{n++}", T.Fill( T.DISCLOSURE_ENGINES, noun, S.List( group.Select( g => g.Target ).ToList() ), embedded ? "inside the client process " : string.Empty, group.Key.Cpus! ), sources );
      }
   }

   /// <summary>
   /// The boot the newest session recorded, against the first session's first start.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Boot( Writer w )
   {
      List<RunResult> runs = w.Newest.Runs.ToList();
      if( !w.TwoSessions || runs.Any( r => r.Conditions.BootId == null || r.Conditions.BootTimeUtc == null )
         || runs.Select( r => r.Conditions.BootId ).Distinct().Count() != 1 || runs.Select( r => r.Conditions.BootTimeUtc ).Distinct().Count() != 1 )
      {
         return;
      }

      RunResult first = w.Sessions[0].Runs[0];
      string id = runs[0].Conditions.BootId!;
      string time = runs[0].Conditions.BootTimeUtc!;
      bool before = Consolidator.Before( time, first.StartedUtc ) == true;
      var sources = runs.Select( r => S.Result( r.Name, "conditions.boot.bootId", id ) ).Concat( runs.Select( r => S.Result( r.Name, "conditions.boot.bootTimeUtc", time ) ) ).ToList();
      sources.Add( S.Result( first.Name, "startedUtc", first.StartedUtc! ) );
      w.Add( "disclosure.boot", T.Fill( before ? T.DISCLOSURE_BOOT : T.DISCLOSURE_BOOT_LATER, w.Newest.Name, id, time, w.Sessions[0].Name, first.StartedUtc! ), sources );
   }

   /// <summary>
   /// The images: one id per target equal to its pin, and when each was last tagged.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Images( Writer w )
   {
      if( w.Report.Images.Count == 0 )
      {
         return;
      }

      string n = S.Whole( w.Report.ImageTargets );
      w.Add( "disclosure.images", T.Fill( T.DISCLOSURE_IMAGES, n, w.Newest.Name ), new[] { S.Consolidated( "imageTargets", n ) }
         .Concat( w.Report.Images.Select( i => S.Consolidated( $"images[target={i.Target}].id", i.Id ) ) ) );
      RunResult first = w.Sessions[0].Runs[0];
      if( w.Report.Images.All( i => i.BeforeFirstV7Start == true ) )
      {
         w.Add( "disclosure.images.tagged", T.Fill( T.DISCLOSURE_IMAGES_BEFORE, w.Sessions[0].Name, first.StartedUtc! ), new[] { S.Result( first.Name, "startedUtc", first.StartedUtc! ) }
            .Concat( w.Report.Images.Select( i => S.Consolidated( $"images[target={i.Target}].beforeFirstV7Start", "true" ) ) ) );
         return;
      }

      foreach( ImageInfo image in w.Report.Images.Where( i => i.BeforeFirstV7Start != true ) )
      {
         string tagged = image.LastTagTimeUtc ?? ConsolidateIdentity.NOT_RECORDED;
         w.Add( $"disclosure.images.tagged.{image.Target}", T.Fill( T.DISCLOSURE_IMAGES_AFTER, image.Target, tagged, w.Sessions[0].Name, first.StartedUtc! ),
            S.Consolidated( $"images[target={image.Target}].lastTagTimeUtc", tagged ), S.Result( first.Name, "startedUtc", first.StartedUtc! ) );
      }
   }

   /// <summary>
   /// The in-memory disclosure of each reported target whose storage fact documents it, with its set-by-setup save and appendonly facts.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Memory( Writer w )
   {
      foreach( string target in w.Report.Targets )
      {
         WhyFact? storage = ConsolidateHeadline.InMemoryFact( w, target );
         List<WhyFact> setup = ComposeSettings( w, target );
         if( storage == null || setup.Count != 2 )
         {
            continue;
         }

         SentenceSource doc = S.Fact( storage );
         w.Add( $"disclosure.memory.{target}", T.Fill( T.DISCLOSURE_REDIS, target, doc.Value, setup[0].Text, setup[1].Text ), new[] { doc }.Concat( setup.Select( S.Fact ) ) );
      }
   }

   /// <summary>
   /// The engine settings disclosure, when the newest session recorded settings.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Settings( Writer w )
   {
      if( w.Report.EngineSettings.Count > 0 )
      {
         RunResult run = w.Newest.Runs[0];
         w.Add( "disclosure.settings", T.Fill( T.DISCLOSURE_SETTINGS, run.Name ), w.RunSource( run.Name ) );
      }
   }

   /// <summary>
   /// The CPU cap facts and the measured exact-search facts of the reported targets.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Facts( Writer w )
   {
      foreach( WhyRow row in w.Report.Why )
      {
         foreach( WhyFact cap in row.Facts.Where( f => f.Kind == "cap" ) )
         {
            w.Add( $"disclosure.cap.{row.Target}", T.Fill( T.DISCLOSURE_CAP, row.Target, cap.Text ), S.Fact( cap ) );
         }

         foreach( WhyFact scan in row.Facts.Where( f => f.Kind == "search" && f.Mode == "exact" && f.Confidence == "measured" ) )
         {
            w.Add( $"disclosure.scan.{row.Target}", T.Fill( T.DISCLOSURE_SCAN, row.Target, scan.Text ), S.Fact( scan ) );
         }
      }

      ConsolidateTextGraph.Write( w );
   }

   /// <summary>
   /// Data-folder start state of every target whose folder was reset (v8: ClickHouse's log tables), per run; and the
   /// recorded disk note of those targets in runs before v8, quoted.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void DataFolders( Writer w )
   {
      List<string> reset = w.Report.Targets.Where( t => w.Sessions.SelectMany( s => s.Runs ).Any( r => r.Find( t )?.DataFolder?.Reset != null ) ).ToList();
      foreach( string target in reset )
      {
         foreach( RunResult run in w.Sessions.SelectMany( s => s.Runs ) )
         {
            TargetResult t = run.Find( target )!;
            DataFolderValue? folder = t.DataFolder;
            if( folder?.BytesAtStart != null && folder.BytesAtEnd != null )
            {
               Folder( w, run.Name, target, folder );
            }
            else if( t.DiskText != null )
            {
               DiskNote( w, run.Name, target, t.DiskText );
            }
         }
      }
   }

   /// <summary>
   /// The recorded disk note of one run whose data folder has no structured fields: the note itself, quoted, when the run-page notes list marks nothing in it; otherwise the two sizes the line
   /// records and a pointer to the correction the run's page prints.
   /// Why not the quote: the list marks "N added by this load" as misleading (the figure is the growth of the whole data folder, not what the load wrote), and a summary that printed it bare would
   /// contradict the page of the run that corrects it. The list entry is cited by the reference it holds to this run's disk note, never by its statement, so the page's list of sources does not print
   /// the marked words either.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="run">Run folder.</param>
   /// <param name="target">Target.</param>
   /// <param name="text">The recorded disk note.</param>
   private static void DiskNote( Writer w, string run, string target, string text )
   {
      string slot = $"disclosure.disk.{target}.{run}";
      string path = $"targets[{target}].disk.text";
      RunPageNote? marked = w.Input.RunPageNotes.Marking( run, target, text );
      if( marked == null )
      {
         w.Add( slot, text, S.Quote( run, path, text ) );
         return;
      }

      var sources = new List<SentenceSource> { S.File( RunPageNoteList.FILE, $"results@{run}:targets[{target}].disk.text" ), w.RunSource( run ) };
      Match sizes = DISK_SIZES.Match( text );
      if( !sizes.Success )
      {
         w.Add( slot, T.Fill( T.DISCLOSURE_DISK_WITHHELD, run, target ), sources );
         return;
      }

      string after = sizes.Groups["after"].Value;
      string before = sizes.Groups["before"].Value;
      sources.Add( S.Token( run, path, $"{after} (whole engine data folder), {before} before" ) );
      w.Add( slot, T.Fill( T.DISCLOSURE_DISK_CORRECTED, run, target, after, before ), sources );
   }

   /// <summary>
   /// One run's data-folder sentence.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="run">Run folder.</param>
   /// <param name="target">Target.</param>
   /// <param name="folder">Its data folder record.</param>
   private static void Folder( Writer w, string run, string target, DataFolderValue folder )
   {
      string at = $"targets[{target}].dataFolder";
      string start = S.Whole( folder.BytesAtStart!.Value );
      string end = S.Whole( folder.BytesAtEnd!.Value );
      if( folder.Reset != null && folder.BytesAfterReset != null )
      {
         ResetText reset = ResetText.Read( run, folder.Reset );
         string after = S.Whole( folder.BytesAfterReset.Value );
         string slot = $"disclosure.dataFolder.{target}.{run}";
         w.Add( slot, T.Fill( reset.Stopped ? T.DISCLOSURE_DATA_FOLDER : T.DISCLOSURE_DATA_FOLDER_STILL, run, target, start, after, end ), w.RunSource( run ),
            S.Result( run, at + ".bytesAtStart", start ), S.Result( run, at + ".bytesAfterReset", after ), S.Result( run, at + ".bytesAtEnd", end ), S.Token( run, at + ".reset", reset.Settled ) );
         w.Add( slot + ".tables", T.Fill( T.DISCLOSURE_DATA_FOLDER_TABLES, run, reset.TruncatedCount, target, reset.ActiveBefore, reset.ActiveAfter ), w.RunSource( run ),
            S.Token( run, at + ".reset", reset.Truncated ), S.Token( run, at + ".reset", reset.ActiveBefore ), S.Token( run, at + ".reset", reset.ActiveAfter ) );
         return;
      }

      w.Add( $"disclosure.dataFolder.{target}.{run}", T.Fill( T.DISCLOSURE_DATA_FOLDER_PLAIN, run, target, start, end ), w.RunSource( run ), S.Result( run, at + ".bytesAtStart", start ), S.Result( run, at + ".bytesAtEnd", end ) );
   }

   /// <summary>
   /// The targets whose segment layout differed between runs.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Layouts( Writer w )
   {
      List<string> differing = w.Report.Metrics.SelectMany( m => m.Rows ).Where( r => r.Flags.Any( f => f.Code == ConsolidateFlags.SEGMENT_LAYOUT ) ).Select( r => r.Target ).Distinct( StringComparer.Ordinal ).ToList();
      if( differing.Count > 0 )
      {
         w.Add( "disclosure.layouts", T.Fill( T.DISCLOSURE_LAYOUTS, S.List( differing ) ) );
      }
   }

   /// <summary>
   /// The observer, its clock check and the timers, for the newest session and for every older session an observer summary covers; or that no observer summary was given.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Observer( Writer w )
   {
      ObserverInfo? o = w.Report.Observer;
      if( o == null )
      {
         w.Add( "disclosure.observer", T.DISCLOSURE_NO_OBSERVER );
         return;
      }

      ObserverSentences( w, o, "observer", string.Empty, "observer" );
      for( int i = 0; i < w.Report.ObserverOthers.Count; i++ )
      {
         ObserverInfo other = w.Report.ObserverOthers[i];
         ObserverSentences( w, other, $"observerOthers[{i}]", $".{other.Session}", "observer" );
      }
   }

   /// <summary>
   /// The observer sentences of one session.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="o">The observer block of the session.</param>
   /// <param name="at">Its path in consolidated.json.</param>
   /// <param name="suffix">The slot suffix: empty for the newest session, ".name" for an older one.</param>
   /// <param name="slotName">The slot name.</param>
   private static void ObserverSentences( Writer w, ObserverInfo o, string at, string suffix, string slotName )
   {
      if( o.CpuMax is double cpu )
      {
         string max = S.Number( cpu, 3 );
         w.Add( $"disclosure.{slotName}{suffix}", T.Fill( T.DISCLOSURE_OBSERVER, o.Session, max, o.Copy ), S.Consolidated( at + ".cpuMax", max ), S.Consolidated( at + ".copy", o.Copy ) );
      }

      if( o.Msr620ValuesSeen.Count > 0 && o.CpuWorst is ObserverCpuDeviation worst && o.PinnedMhz is int pin )
      {
         ObserverClock( w, o, at, suffix, slotName, worst, pin );
      }
      else if( o.AperfWorstDeviationBp is double bp && o.Msr620ValuesSeen.Count > 0 )
      {
         string pct = S.Percent( bp );
         string values = S.List( o.Msr620ValuesSeen );
         w.Add( $"disclosure.{slotName}.clock{suffix}", T.Fill( T.DISCLOSURE_OBSERVER_CLOCK, pct, T.Msr(), values ), new[] { S.Consolidated( at + ".aperfWorstDeviationBp", pct, "bp-pct" ), S.File( T.MSR_FILE, T.MSR_TOKEN ) }
            .Concat( o.Msr620ValuesSeen.Select( ( v, i ) => S.Consolidated( $"{at}.msr620ValuesSeen[{i}]", v ) ) ) );
      }

      if( o.Placement is ObserverPlacement placement )
      {
         ObserverPlacementSentence( w, o, at, suffix, slotName, placement );
      }

      if( o.TimersCovered )
      {
         string fired = S.Whole( o.PassesWithTimerFired );
         w.Add( $"disclosure.timers{suffix}", T.Fill( T.DISCLOSURE_TIMERS, fired, o.Session ), S.Consolidated( at + ".passesWithTimerFired", fired ) );
      }
      else if( o.Runs.All( r => r.TimersCovered != true ) )
      {
         w.Add( $"disclosure.timers{suffix}", T.Fill( T.DISCLOSURE_TIMERS_NONE, o.Session ), o.Runs.Select( ( r, i ) => ( r, i ) ).Where( x => x.r.TimersCovered == false ).Select( x => S.Consolidated( $"{at}.runs[{x.i}].timersCovered", "false" ) ) );
      }
      else
      {
         w.Add( $"disclosure.timers{suffix}", T.Fill( T.DISCLOSURE_TIMERS_NOT_COVERED, o.Session ) );
      }
   }

   /// <summary>
   /// The observer's per-CPU clock check of one session: the CPU furthest from the pin, the MSR value, the pass that ran under the pin on its CPUs, and the passes whose mean a mean rule flags.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="o">The observer block.</param>
   /// <param name="at">Its path in consolidated.json.</param>
   /// <param name="suffix">The slot suffix.</param>
   /// <param name="slotName">The slot name.</param>
   /// <param name="worst">The CPU furthest from the pin.</param>
   /// <param name="pin">The pinned clock, MHz.</param>
   private static void ObserverClock( Writer w, ObserverInfo o, string at, string suffix, string slotName, ObserverCpuDeviation worst, int pin )
   {
      string pct = S.Percent( worst.DeviationBp );
      string mhz = S.Number( worst.Mhz, 1 );
      string cpu = S.Whole( worst.Cpu );
      w.Add( $"disclosure.{slotName}.clock{suffix}", T.Fill( T.DISCLOSURE_OBSERVER_CPU, pct, worst.Target, T.PassLabel( worst.Pass ), cpu, mhz, S.Whole( pin ) ),
         S.Consolidated( at + ".cpuWorst.deviationBp", pct, "bp-pct" ), S.Consolidated( at + ".cpuWorst.target", worst.Target ), S.Consolidated( at + ".cpuWorst.cpu", cpu ),
         S.Consolidated( at + ".cpuWorst.mhz", mhz ), S.Consolidated( at + ".pinnedMhz", S.Whole( pin ) ) );
      w.Add( $"disclosure.{slotName}.msr{suffix}", T.Fill( T.DISCLOSURE_OBSERVER_MSR, T.Msr(), S.List( o.Msr620ValuesSeen ) ),
         new[] { S.File( T.MSR_FILE, T.MSR_TOKEN ) }.Concat( o.Msr620ValuesSeen.Select( ( v, i ) => S.Consolidated( $"{at}.msr620ValuesSeen[{i}]", v ) ) ) );
      if( o.Dip is ObserverDip dip )
      {
         ObserverDipSentence( w, o, at, suffix, slotName, dip );
      }

      for( int i = 0; i < o.MeanDips.Count; i++ )
      {
         ObserverMeanDip d = o.MeanDips[i];
         if( w.Report.Clock.DroppedWarnings.Any( x => x.Run == d.Run && x.Target == d.Target && x.Pass == d.Pass ) || MedianRuleFlags( w, d ) )
         {
            continue;
         }

         string engine = S.Number( d.EngineMeanMhz, 1 );
         string client = S.Number( d.ClientMeanMhz, 1 );
         var sources = new List<SentenceSource>
         {
            S.Consolidated( $"{at}.meanDips[{i}].run", d.Run ), S.Consolidated( $"{at}.meanDips[{i}].engineMeanMhz", engine ), S.Consolidated( $"{at}.meanDips[{i}].clientMeanMhz", client ),
         };
         List<string> earlier = w.Sessions.Where( s => s.Name != o.Session && s.Runs.Any( r => w.Report.Clock.DroppedWarnings.Any( x => x.Run == r.Name && x.Target == d.Target && x.Pass == d.Pass ) ) ).Select( s => s.Name ).ToList();
         if( earlier.Count == 0 )
         {
            w.Add( $"disclosure.{slotName}.meandip{suffix}.{i}", T.Fill( T.DISCLOSURE_MEAN_DIP, d.Run, d.Target, T.PassLabel( d.Pass ), engine, client ), sources );
            continue;
         }

         sources.AddRange( earlier.Select( n => S.Consolidated( $"sessions[name={n}].name", n ) ) );
         w.Add( $"disclosure.{slotName}.meandip{suffix}.{i}", T.Fill( T.DISCLOSURE_MEAN_DIP_AS, d.Run, d.Target, T.PassLabel( d.Pass ), engine, client, S.List( earlier ) ), sources );
      }
   }

   /// <summary>
   /// Whether the median rule flags a pass whose sampled mean lay outside the tolerance.
   /// Why: the mean-dip sentence says the median rule does not flag the pass, and a pass the median rule does flag has its own flag text and no such sentence.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="dip">The pass.</param>
   /// <returns>True when the median rule puts the pass off the pinned clock.</returns>
   private static bool MedianRuleFlags( Writer w, ObserverMeanDip dip )
   {
      RunResult? run = w.Sessions.SelectMany( s => s.Runs ).FirstOrDefault( r => r.Name == dip.Run );
      PassFacts? pass = run?.Conditions.Passes.LastOrDefault( p => p.Target == dip.Target && p.Pass == dip.Pass );
      return run != null && pass != null && ConsolidateClock.Recompute( run, pass ).Off;
   }

   /// <summary>
   /// The sentence about the pass that ran under the pin on its CPUs.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="o">The observer block.</param>
   /// <param name="at">Its path in consolidated.json.</param>
   /// <param name="suffix">The slot suffix.</param>
   /// <param name="slotName">The slot name.</param>
   /// <param name="dip">The pass.</param>
   private static void ObserverDipSentence( Writer w, ObserverInfo o, string at, string suffix, string slotName, ObserverDip dip )
   {
      string runs = S.Whole( dip.Runs );
      string other = S.Percent( dip.OtherWorstBp );
      string high = S.Percent( dip.MaxBp );
      var sources = new List<SentenceSource>
      {
         S.Consolidated( at + ".dip.target", dip.Target ), S.Consolidated( at + ".dip.runs", runs ), S.Consolidated( at + ".dip.maxBp", high, "bp-pct" ),
         S.Consolidated( at + ".dip.otherWorstBp", other, "bp-pct" ), S.Consolidated( at + ".session", o.Session ),
      };
      if( dip.EveryCpuUnder )
      {
         string low = S.Percent( dip.MinBp );
         sources.Add( S.Consolidated( at + ".dip.minBp", low, "bp-pct" ) );
         w.Add( $"disclosure.{slotName}.dip{suffix}", T.Fill( T.DISCLOSURE_OBSERVER_DIP, dip.Target, T.PassLabel( dip.Pass ), low, high, runs, o.Session, other ), sources );
         return;
      }

      string under = S.Whole( dip.CpusUnder );
      string readings = S.Whole( dip.CpuReadings );
      sources.Add( S.Consolidated( at + ".dip.cpusUnder", under ) );
      sources.Add( S.Consolidated( at + ".dip.cpuReadings", readings ) );
      w.Add( $"disclosure.{slotName}.dip{suffix}", T.Fill( T.DISCLOSURE_OBSERVER_DIP_SOME, dip.Target, T.PassLabel( dip.Pass ), under, readings, runs, o.Session, high, other ), sources );
   }

   /// <summary>
   /// The sentence that says the observer was not pinned, with where its threads ran, and the sentence that says the most CPU it used by cgroup averaged over a run, each figure a quote of the saved analysis of its run.
   /// </summary>
   /// <param name="w">The writer.</param>
   /// <param name="o">The observer block.</param>
   /// <param name="at">Its path in consolidated.json.</param>
   /// <param name="suffix">The slot suffix.</param>
   /// <param name="slotName">The slot name.</param>
   /// <param name="placement">The placement.</param>
   private static void ObserverPlacementSentence( Writer w, ObserverInfo o, string at, string suffix, string slotName, ObserverPlacement placement )
   {
      string cpus = S.List( placement.Runs.Select( r => r.Cpus ).Distinct( StringComparer.Ordinal ).ToList() );
      var sources = new List<SentenceSource> { S.Consolidated( at + ".session", o.Session ), S.Doc( placement.Runs[0].File, placement.NotPinnedQuote ) };
      var cgroup = new List<SentenceSource> { S.Consolidated( at + ".session", o.Session ) };
      foreach( ObserverPlacementRun run in placement.Runs )
      {
         sources.Add( S.Doc( run.File, run.PlacementQuote ) );
         cgroup.Add( S.Doc( run.File, run.CgroupQuote ) );
      }

      w.Add( $"disclosure.{slotName}.placement{suffix}", T.Fill( T.DISCLOSURE_OBSERVER_PLACEMENT, o.Session, S.List( placement.Runs.Select( r => r.Percent ).ToList() ), cpus ), sources );
      w.Add( $"disclosure.{slotName}.cgroup{suffix}", T.Fill( T.DISCLOSURE_OBSERVER_CGROUP, o.Session, placement.MaxCgroupCpu ), cgroup );
   }

   /// <summary>
   /// The C-state disclosure.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Cstates( Writer w )
   {
      RunConditions c = w.Newest.Runs[0].Conditions;
      if( c.CpuIdleDriver != null && c.CpuIdleGovernor != null )
      {
         w.Add( "disclosure.cstates", T.Fill( T.DISCLOSURE_CSTATES, c.CpuIdleDriver, c.CpuIdleGovernor ),
            S.ResultAll( "conditions.cpuIdle.driver", c.CpuIdleDriver ), S.ResultAll( "conditions.cpuIdle.governor", c.CpuIdleGovernor ) );
      }
   }

   /// <summary>
   /// The search-settings scope of every order (checklist O13) and the routes the runs recorded (checklist O11).
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Scope( Writer w )
   {
      w.Add( "disclosure.searchSettings", T.DISCLOSURE_SEARCH_SETTINGS );
      List<string> routes = w.Sessions.SelectMany( s => s.Runs ).Select( r => r.Conditions.Routes ).Aggregate( ( a, b ) => a.Intersect( b, StringComparer.Ordinal ).ToList() );
      for( int i = 0; i < routes.Count; i++ )
      {
         w.Add( $"disclosure.route.{i}", T.Fill( T.DISCLOSURE_ROUTE, routes[i] ), S.TokenAll( "conditions.connections", routes[i] ) );
      }
   }

   /// <summary>
   /// How dockerd's CPU was counted (hole H2): the recorded rule quoted per session that recorded it, plus the parity test; or that a session did not record it.
   /// </summary>
   /// <param name="w">The writer.</param>
   private static void Dockerd( Writer w )
   {
      w.Add( "disclosure.busy", T.DISCLOSURE_BUSY, S.File( DOCKERD_FILE, DOCKERD_TOKEN ), S.File( OUTSIDE_FILE, OUTSIDE_TOKEN ) );
      foreach( ClaimSession session in w.Sessions )
      {
         RunResult run = session.Runs[0];
         string? rule = run.Conditions.EngineCpuRule;
         if( rule == null )
         {
            w.Add( $"disclosure.dockerd.{session.Name}", T.Fill( T.DISCLOSURE_DOCKERD_NONE, session.Name ) );
            continue;
         }

         w.Add( $"disclosure.dockerd.{session.Name}", rule, S.Quote( run.Name, ENGINE_CPU_RULE, rule ) );
      }

      if( w.Sessions.Any( s => s.Runs[0].Conditions.EngineCpuRule != null ) )
      {
         w.Add( "disclosure.dockerd.test", T.Fill( T.DISCLOSURE_DOCKERD_TEST, ACCOUNTING_TEST ), S.File( ACCOUNTING_TEST_FILE, ACCOUNTING_TEST ) );
      }
   }

   #endregion Private Methods
}
