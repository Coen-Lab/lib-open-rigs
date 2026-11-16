using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Tests;

namespace OpenRigs.Core.Tests
{
    /// <summary>
    /// Pieces announce their start-up stages into one shared ReadyStages ReplaySubject, run by headless
    /// Bonsai as a rig runs it. Bonsai 2.9 passes a multicast source's completion on to the subject, so
    /// the first announcer that completes closes it for every other piece; AnnounceStage never completes.
    /// </summary>
    [TestClass]
    public class ReadyStagesHeadlessTests
    {
        // A at once, B after 0.5 s, C after 1.5 s, each through AnnounceStage: Ready comes (the test
        // workflow stops itself with READY, as announcers never complete).
        [TestMethod]
        public void AnnouncedStages_AllReachTheWatchdog()
        {
            var result = BonsaiWorkflowRunner.Run("ReadyStagesAnnounced.bonsai", timeout: System.TimeSpan.FromSeconds(60));
            StringAssert.Contains(result.StandardError + result.StandardOutput, "READY after C", result.Describe());
        }

        // The same stages announced by a source that completes after its one value: A's completion
        // closes ReadyStages, B and C never reach the watchdog, and it stops the workflow naming them.
        [TestMethod]
        public void StagesFromCompletingSources_CloseReadyStagesAndTheWatchdogNamesTheRest()
        {
            var result = BonsaiWorkflowRunner.Run("ReadyStagesClosedByATake.bonsai", timeout: System.TimeSpan.FromSeconds(60));
            StringAssert.Contains(result.StandardError + result.StandardOutput, "Never arrived: B, C", result.Describe());
        }

        // A source that completes after its one value (HarpClockSet's Take(1)), multicast into an AsyncSubject and
        // then announced: AnnounceStage must not cut its source short, or the source's completion never reaches
        // the subject and everything waiting on it (every Harp device's log) waits forever (rig test,
        // 16 Nov 2026: no .bin in any device folder).
        [TestMethod]
        public void AnnouncingAfterAMulticast_LetsTheSourceCompleteItsSubject()
        {
            var result = BonsaiWorkflowRunner.Run("AnnouncedAsyncSubjectCompletes.bonsai", timeout: System.TimeSpan.FromSeconds(60));
            StringAssert.Contains(result.StandardError + result.StandardOutput, "CLOCKSET 0", result.Describe());
        }
    }
}
