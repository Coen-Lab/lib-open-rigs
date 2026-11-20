#nullable disable
using System.Collections.Generic;

namespace OpenRigs.Trials
{
    /// <summary>A penalised trial's cost: its timeout and its penalty sound's level.</summary>
    public class ResolvedPenalty
    {
        /// <summary>The timeout after the trial, in seconds.</summary>
        public double TimeoutSeconds;
        /// <summary>The penalty sound's level in dBFS RMS; NaN when mute (the sound then never plays).</summary>
        public double LevelDbfs;
    }

    /// <summary>The kind of penalised outcome, each with its own block in the task file's penalty (open_rigs.core.task Penalty).</summary>
    public enum PenaltyKind
    {
        /// <summary>penalty.incorrect: a wrong choice, a go/no-go false alarm, a probe's tails; the task's choice.</summary>
        Incorrect,
        /// <summary>penalty.noResponse: a miss; null in a task file whose misses are never penalised.</summary>
        NoResponse
    }

    /// <summary>What a penalised trial costs from a task file's penalty block (open_rigs.core.task Penalty): its kind's own
    /// timeoutSeconds and sound.levelDbfs, nothing inherited. Which trials are penalised, and of which kind, is the task's.</summary>
    public static class Penalties
    {
        /// <summary>The cost of a penalised trial of <paramref name="kind"/>: penalty.incorrect's
        /// (<paramref name="incorrectTimeoutSeconds"/>, <paramref name="incorrectLevelDbfs"/>) or penalty.noResponse's
        /// (<paramref name="noResponseTimeoutSeconds"/>, <paramref name="noResponseLevelDbfs"/>, both null when the block is
        /// null). A level is a number or mute (<see cref="SoundLevels.Parse"/>, NaN for mute). Throws on a penalised miss
        /// with penalty.noResponse null, which the task's Go check refuses first (<see cref="Problems"/>).</summary>
        public static ResolvedPenalty For(PenaltyKind kind, double incorrectTimeoutSeconds, object incorrectLevelDbfs, double? noResponseTimeoutSeconds, object noResponseLevelDbfs)
        {
            if (kind == PenaltyKind.Incorrect) return new ResolvedPenalty { TimeoutSeconds = incorrectTimeoutSeconds, LevelDbfs = SoundLevels.Parse(incorrectLevelDbfs) };
            if (!noResponseTimeoutSeconds.HasValue) throw new System.InvalidOperationException("A miss was penalised, but the task file's penalty.noResponse is null (misses never penalised).");
            return new ResolvedPenalty { TimeoutSeconds = noResponseTimeoutSeconds.Value, LevelDbfs = SoundLevels.Parse(noResponseLevelDbfs) };
        }

        /// <summary>One combination's penalty block against the task's rules: a task that penalises misses
        /// (<paramref name="missesPenalised"/>) needs penalty.noResponse (<paramref name="hasNoResponse"/>) not null.</summary>
        public static List<string> Problems(bool missesPenalised, bool hasNoResponse)
        {
            var problems = new List<string>();
            if (missesPenalised && !hasNoResponse) problems.Add("penalty.noResponse is null, but this task penalises a miss: give penalty.noResponse its timeoutSeconds and sound");
            return problems;
        }
    }
}
