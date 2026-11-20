using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenRigs.Trials
{
    /// <summary>
    /// When a trial's stimulus ends (open_rigs.core.task StimulusTiming, the task file's stimulus block): the earlier of its
    /// onset plus stimulus.durationSeconds (null: no cap) and the outcome plus stimulus.responseToStimulusOffSeconds (null: the
    /// response does not end it); both null, no off is timed. The response is the trial's outcome: on a miss, the response window's
    /// end. A task gives both in its TrialTiming (StimulusDurationSeconds, NaN for null; ResponseToStimulusOffSeconds); the loop then emits one
    /// StimulusOff event per trial whose stimulus came on, and its TrialTimes.StimulusOffTime is the row's stimulusOffTime.
    /// </summary>
    public static class StimulusTiming
    {
        /// <summary>The stimulus's off time: the earlier of onsetTime + durationSeconds and outcomeTime + responseToStimulusOffSeconds
        /// (unless null), durationSeconds NaN for no cap; NaN with no onset (NaN), or neither.</summary>
        public static double Time(double onsetTime, double durationSeconds, double outcomeTime, double? responseToStimulusOffSeconds)
        {
            if (double.IsNaN(onsetTime)) return double.NaN;
            var off = onsetTime + durationSeconds;
            if (!responseToStimulusOffSeconds.HasValue) return off;
            var afterResponse = outcomeTime + responseToStimulusOffSeconds.Value;
            return double.IsNaN(off) ? afterResponse : Math.Min(off, afterResponse);
        }

        /// <summary>One combination's stimulus block against its trial structure: durationSeconds null (no cap: its checks skipped)
        /// or a finite number above 0, not null with responseToStimulusOffSeconds null too; responseToStimulusOffSeconds null or a
        /// finite number, 0 or more; the stimulus off before the next trial's stimulus can come; and durationSeconds at most
        /// stimulusToResponseEndSeconds, the stimulus to the response window's close (infinity for no limit).
        /// The next stimulus comes at the soonest stimulusToEarliestOutcomeSeconds (the stimulus to the soonest outcome: the response
        /// window's opening, 0 when unknown) plus minOutcomeToItiSeconds plus itiMinSeconds after this one: the interval runs from
        /// its start to the next stimulus itself, so the next trial's lead (laser or spouts) moves its start, never its stimulus.
        /// minOutcomeToItiSeconds is the shortest outcome to next interval over the outcomes a trial can have (ITrialOutcome): a
        /// penalised one's timeout, a rewarded one's reward.consumptionSeconds, 0 for one neither (a probe). So durationSeconds
        /// is at most stimulusToEarliestOutcomeSeconds + minOutcomeToItiSeconds + itiMinSeconds, and responseToStimulusOffSeconds
        /// at most minOutcomeToItiSeconds + itiMinSeconds.</summary>
        public static List<string> Problems(double? durationSeconds, double? responseToStimulusOffSeconds, double itiMinSeconds, double stimulusToResponseEndSeconds, double stimulusToEarliestOutcomeSeconds, double minOutcomeToItiSeconds)
        {
            var problems = new List<string>();
            if (durationSeconds.HasValue && (!(durationSeconds.Value > 0) || double.IsInfinity(durationSeconds.Value))) problems.Add("stimulus.durationSeconds must be null or a finite number above 0");
            var after = responseToStimulusOffSeconds;
            if (!durationSeconds.HasValue && !after.HasValue) problems.Add("stimulus.durationSeconds and stimulus.responseToStimulusOffSeconds are both null: the stimulus would never end");
            if (after.HasValue && (!(after.Value >= 0) || double.IsInfinity(after.Value))) problems.Add("stimulus.responseToStimulusOffSeconds must be null or a finite number, 0 or more");
            problems.AddRange(DurationProblems(durationSeconds - stimulusToEarliestOutcomeSeconds, durationSeconds, itiMinSeconds, minOutcomeToItiSeconds, ""));
            if (durationSeconds > stimulusToResponseEndSeconds + 1e-9) problems.Add(string.Format("stimulus.durationSeconds ({0} s) is longer than the stimulus to the response window's close ({1} s)", durationSeconds, stimulusToResponseEndSeconds));
            if (after >= 0 && !double.IsInfinity(after.Value)) problems.AddRange(AfterResponseProblems(after, itiMinSeconds, minOutcomeToItiSeconds, ""));
            return problems;
        }

        /// <summary>Between a trial and the next, each drawn from any weighted combination, the rules of Problems with the
        /// shortest itiMinSeconds: each combination's durationSeconds (nulls skipped) less its own stimulusToEarliestOutcomeSeconds
        /// (the two lists paired by combination) at most minOutcomeToItiSeconds plus the shortest itiMinSeconds, and the longest
        /// responseToStimulusOffSeconds (nulls skipped) at most minOutcomeToItiSeconds plus the shortest itiMinSeconds.</summary>
        public static List<string> BetweenTrialsProblems(IEnumerable<double?> durationSeconds, IEnumerable<double> stimulusToEarliestOutcomeSeconds, IEnumerable<double?> responseToStimulusOffSeconds, IEnumerable<double> itiMinSeconds, double minOutcomeToItiSeconds)
        {
            var problems = new List<string>();
            var iti = itiMinSeconds.ToList();
            if (iti.Count == 0) return problems;
            var past = durationSeconds.Zip(stimulusToEarliestOutcomeSeconds, (d, e) => new { Duration = d, Past = d - e }).Where(x => x.Duration.HasValue).OrderByDescending(x => x.Past).FirstOrDefault();
            if (past != null) problems.AddRange(DurationProblems(past.Past, past.Duration, iti.Min(), minOutcomeToItiSeconds, " of a combination that may come next"));
            var after = responseToStimulusOffSeconds.Where(a => a.HasValue).ToList();
            if (after.Count > 0) problems.AddRange(AfterResponseProblems(after.Max(), iti.Min(), minOutcomeToItiSeconds, " of a combination that may come next"));
            return problems;
        }

        // durationSeconds less the stimulus to the soonest outcome: how long the stimulus runs past it.
        static IEnumerable<string> DurationProblems(double? pastOutcome, double? durationSeconds, double itiMinSeconds, double minOutcomeToItiSeconds, string where)
        {
            if (pastOutcome.HasValue && pastOutcome.Value > minOutcomeToItiSeconds + itiMinSeconds + 1e-9)
            {
                yield return string.Format("stimulus.durationSeconds ({0} s) runs {1} s past the soonest outcome, longer than the shortest outcome to next interval ({2} s: timeout or reward consumption) plus itiMinSeconds ({3} s){4}: the stimulus could still be on at the next trial's", durationSeconds.Value, pastOutcome.Value, minOutcomeToItiSeconds, itiMinSeconds, where);
            }
        }

        static IEnumerable<string> AfterResponseProblems(double? after, double itiMinSeconds, double minOutcomeToItiSeconds, string where)
        {
            if (after.HasValue && after.Value > minOutcomeToItiSeconds + itiMinSeconds + 1e-9)
            {
                yield return string.Format("stimulus.responseToStimulusOffSeconds ({0} s) is longer than the shortest outcome to next interval ({1} s: timeout or reward consumption) plus itiMinSeconds ({2} s){3}: the stimulus could still be on at the next trial's", after.Value, minOutcomeToItiSeconds, itiMinSeconds, where);
            }
        }
    }
}
