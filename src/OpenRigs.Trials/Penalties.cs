#nullable disable
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

    /// <summary>What a penalised trial costs from a task file's penalty block (open_rigs.core.task Penalty): a penalised
    /// choice the block's own timeoutSeconds and sound.levelDbfs, a penalised miss its noResponse override, each null field
    /// the choice's. Which trials are penalised, and which are misses, is the task's.</summary>
    public static class Penalties
    {
        /// <summary>The cost of a penalised trial: <paramref name="timeoutSeconds"/> and <paramref name="levelDbfs"/>
        /// (penalty.timeoutSeconds, penalty.sound.levelDbfs), or with <paramref name="noResponse"/> (the task's miss)
        /// <paramref name="missTimeoutSeconds"/> and <paramref name="missLevelDbfs"/> (penalty.noResponse's) where not null.
        /// A level is a number or mute (<see cref="SoundLevels.Parse"/>, NaN for mute).</summary>
        public static ResolvedPenalty Resolve(double timeoutSeconds, object levelDbfs, double? missTimeoutSeconds, object missLevelDbfs, bool noResponse)
        {
            return new ResolvedPenalty
            {
                TimeoutSeconds = noResponse && missTimeoutSeconds.HasValue ? missTimeoutSeconds.Value : timeoutSeconds,
                LevelDbfs = SoundLevels.Parse(noResponse && missLevelDbfs != null ? missLevelDbfs : levelDbfs),
            };
        }
    }
}
