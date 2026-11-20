using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenRigs.Trials
{
    /// <summary>
    /// When a trial's stimulus ends (open_rigs.core.task StimulusTiming, the task file's stimulus block): the earlier of its
    /// onset plus stimulus.durationSeconds and the outcome plus stimulus.responseToStimulusOffSeconds (null: the response does not end
    /// it). The response is the trial's outcome: on a miss, the response window's end. A task gives both in its TrialTiming (StimulusDurationSeconds, ResponseToStimulusOffSeconds); the loop then emits one
    /// StimulusOff event per trial whose stimulus came on, and its TrialTimes.StimulusOffTime is the row's stimulusOffTime.
    /// </summary>
    public static class StimulusTiming
    {
        /// <summary>The stimulus's off time: the earlier of onsetTime + durationSeconds and outcomeTime + responseToStimulusOffSeconds
        /// (unless null); NaN with no onset (NaN).</summary>
        public static double Time(double onsetTime, double durationSeconds, double outcomeTime, double? responseToStimulusOffSeconds)
        {
            if (double.IsNaN(onsetTime)) return double.NaN;
            var off = onsetTime + durationSeconds;
            return responseToStimulusOffSeconds.HasValue ? Math.Min(off, outcomeTime + responseToStimulusOffSeconds.Value) : off;
        }

        /// <summary>One combination's stimulus block against its trial structure: durationSeconds a finite number above 0;
        /// responseToStimulusOffSeconds null or a finite number, 0 or more; durationSeconds at most itiMinSeconds (off before the next
        /// trial's stimulus) and at most stimulusToResponseEndSeconds, the stimulus to the response window's close (infinity for no
        /// limit); and responseToStimulusOffSeconds at most minTimeoutSeconds (the shortest timeout after an outcome, 0 when a
        /// rewarded trial has none) plus itiMinSeconds, so a stimulus run on past the response into the timeout and interval
        /// never reaches the next trial's stimulus.</summary>
        public static List<string> Problems(double durationSeconds, double? responseToStimulusOffSeconds, double itiMinSeconds, double stimulusToResponseEndSeconds, double minTimeoutSeconds)
        {
            var problems = new List<string>();
            if (!(durationSeconds > 0) || double.IsInfinity(durationSeconds)) problems.Add("stimulus.durationSeconds must be a finite number above 0");
            var after = responseToStimulusOffSeconds;
            if (after.HasValue && (!(after.Value >= 0) || double.IsInfinity(after.Value))) problems.Add("stimulus.responseToStimulusOffSeconds must be null or a finite number, 0 or more");
            if (durationSeconds > itiMinSeconds + 1e-9) problems.Add(string.Format("stimulus.durationSeconds ({0} s) is longer than itiMinSeconds ({1} s): the stimulus would still be on when the next trial's comes", durationSeconds, itiMinSeconds));
            if (durationSeconds > stimulusToResponseEndSeconds + 1e-9) problems.Add(string.Format("stimulus.durationSeconds ({0} s) is longer than the stimulus to the response window's close ({1} s)", durationSeconds, stimulusToResponseEndSeconds));
            if (after >= 0 && !double.IsInfinity(after.Value)) problems.AddRange(AfterResponseProblems(after, itiMinSeconds, minTimeoutSeconds, ""));
            return problems;
        }

        /// <summary>Between a trial and the next, each drawn from any weighted combination: the longest stimulus.durationSeconds
        /// at most the shortest itiMinSeconds, and the longest responseToStimulusOffSeconds (nulls skipped) at most the shortest
        /// timeout (minTimeoutSeconds) plus the shortest itiMinSeconds.</summary>
        public static List<string> BetweenTrialsProblems(IEnumerable<double> durationSeconds, IEnumerable<double?> responseToStimulusOffSeconds, IEnumerable<double> itiMinSeconds, double minTimeoutSeconds)
        {
            var problems = new List<string>();
            var durations = durationSeconds.ToList();
            var iti = itiMinSeconds.ToList();
            if (iti.Count == 0) return problems;
            if (durations.Count > 0 && durations.Max() > iti.Min() + 1e-9) problems.Add(string.Format("stimulus.durationSeconds {0} s exceeds itiMinSeconds {1} s of a combination that may come next", durations.Max(), iti.Min()));
            var after = responseToStimulusOffSeconds.Where(a => a.HasValue).ToList();
            if (after.Count > 0) problems.AddRange(AfterResponseProblems(after.Max(), iti.Min(), minTimeoutSeconds, " of a combination that may come next"));
            return problems;
        }

        static IEnumerable<string> AfterResponseProblems(double? after, double itiMinSeconds, double minTimeoutSeconds, string where)
        {
            if (after.HasValue && after.Value > minTimeoutSeconds + itiMinSeconds + 1e-9)
            {
                yield return string.Format("stimulus.responseToStimulusOffSeconds ({0} s) is longer than the shortest timeout ({1} s) plus itiMinSeconds ({2} s){3}: the stimulus could still be on at the next trial's", after.Value, minTimeoutSeconds, itiMinSeconds, where);
            }
        }
    }
}
