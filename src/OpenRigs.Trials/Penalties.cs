#nullable disable
using Bonsai;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;

namespace OpenRigs.Trials
{
    /// <summary>A penalised trial's cost: its timeout, its penalty sound's level and its flash.</summary>
    public class ResolvedPenalty
    {
        /// <summary>The timeout after the trial, in seconds.</summary>
        public double TimeoutSeconds;
        /// <summary>The penalty sound's level in dBFS RMS; NaN when mute (the sound then never plays).</summary>
        public double LevelDbfs;
        /// <summary>The penalty flash (penalty.&lt;kind&gt;.flash); its Brightness 0 when none plays.</summary>
        public PenaltyFlash Flash;
    }

    /// <summary>
    /// A penalty flash (open_rigs.core.task PenaltyFlash, a kind's penalty.&lt;kind&gt;.flash): the whole projector field at
    /// Brightness of full white (after gamma correction, 1 the projector's full white), on for the first half of each
    /// 1/RateHz cycle and the normal display the other half, from the outcome for the penalty sound's length (OpenRigs.Vision
    /// PlayPenaltyFlash). Brightness 0: no flash, as the sound's mute.
    /// </summary>
    public class PenaltyFlash
    {
        /// <summary>The highest rate, half ProjectorDisplay's 60 Hz render loop (its TargetRenderFrequency, with VSync on): the
        /// lib knows no display's own refresh, so every on and off half is at least one frame.</summary>
        public const double MaxRateHz = 30;

        /// <summary>The lab's default rate, in Hz.</summary>
        public const double DefaultRateHz = 10;

        /// <summary>No flash.</summary>
        public static readonly PenaltyFlash None = new PenaltyFlash(0, DefaultRateHz);

        /// <summary>A flash at <paramref name="brightness"/> (0 to 1 of full white) and <paramref name="rateHz"/>.</summary>
        public PenaltyFlash(double brightness, double rateHz)
        {
            Brightness = brightness;
            RateHz = rateHz;
        }

        /// <summary>Gets the brightness, 0 to 1 of the projector's full white after gamma correction; 0 when none plays.</summary>
        public double Brightness { get; private set; }

        /// <summary>Gets the flash rate, in Hz.</summary>
        public double RateHz { get; private set; }

        /// <summary>Gets whether a flash plays (Brightness above 0).</summary>
        public bool Plays { get { return Brightness > 0; } }

        /// <summary>Gets one cycle, 1/RateHz, to the tick (TimeSpan.FromSeconds would round it to the millisecond).</summary>
        public TimeSpan Period { get { return TimeSpan.FromTicks((long)Math.Round(TimeSpan.TicksPerSecond / RateHz)); } }

        /// <summary>Gets the on half of each cycle, 1/(2 RateHz).</summary>
        public TimeSpan OnTime { get { return TimeSpan.FromTicks((long)Math.Round(TimeSpan.TicksPerSecond / (2 * RateHz))); } }

        /// <summary>A flash block's values against the lab's rules (open_rigs.core.task PenaltyFlash, the same): brightness
        /// finite, 0 to 1; rateHz finite, above 0 and at most <see cref="MaxRateHz"/>. <paramref name="block"/> names it
        /// (penalty.incorrect.flash).</summary>
        public static List<string> Problems(string block, double brightness, double rateHz)
        {
            var problems = new List<string>();
            if (double.IsNaN(brightness) || brightness < 0 || brightness > 1)
                problems.Add(string.Format(CultureInfo.InvariantCulture, "{0}.brightness is {1}, not 0 to 1 (of the projector's full white; 0: no flash)", block, brightness));
            if (double.IsNaN(rateHz) || double.IsInfinity(rateHz) || rateHz <= 0 || rateHz > MaxRateHz)
                problems.Add(string.Format(CultureInfo.InvariantCulture, "{0}.rateHz is {1}, not above 0 and at most {2} (half the projector's {3} Hz render loop)", block, rateHz, MaxRateHz, 2 * MaxRateHz));
            return problems;
        }
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
    /// timeoutSeconds, sound.levelDbfs and flash, nothing inherited. Which trials are penalised, and of which kind, is the task's.</summary>
    public static class Penalties
    {
        /// <summary>One kind's cost: its timeoutSeconds, its sound.levelDbfs (a number or mute, <see cref="SoundLevels.Parse"/>,
        /// NaN for mute) and its flash.brightness and flash.rateHz. Rules are checked at Go (<see cref="PenaltyFlashRules"/>).</summary>
        public static ResolvedPenalty Of(double timeoutSeconds, object levelDbfs, double flashBrightness, double flashRateHz)
        {
            return new ResolvedPenalty { TimeoutSeconds = timeoutSeconds, LevelDbfs = SoundLevels.Parse(levelDbfs), Flash = new PenaltyFlash(flashBrightness, flashRateHz) };
        }

        /// <summary>The cost of a penalised trial of <paramref name="kind"/>: penalty.incorrect's (timeoutSeconds,
        /// sound.levelDbfs, flash.brightness, flash.rateHz) or penalty.noResponse's (all null when the block is null), as
        /// <see cref="Of"/>. Throws on a penalised miss with penalty.noResponse null, which the task's Go check refuses first
        /// (<see cref="Problems"/>).</summary>
        public static ResolvedPenalty For(PenaltyKind kind, double incorrectTimeoutSeconds, object incorrectLevelDbfs, double incorrectFlashBrightness, double incorrectFlashRateHz,
            double? noResponseTimeoutSeconds, object noResponseLevelDbfs, double? noResponseFlashBrightness, double? noResponseFlashRateHz)
        {
            if (kind == PenaltyKind.Incorrect) return Of(incorrectTimeoutSeconds, incorrectLevelDbfs, incorrectFlashBrightness, incorrectFlashRateHz);
            if (!noResponseTimeoutSeconds.HasValue) throw new InvalidOperationException("A miss was penalised, but the task file's penalty.noResponse is null (misses never penalised).");
            return Of(noResponseTimeoutSeconds.Value, noResponseLevelDbfs, noResponseFlashBrightness.GetValueOrDefault(), noResponseFlashRateHz.GetValueOrDefault(PenaltyFlash.DefaultRateHz));
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

    /// <summary>
    /// The penalty flashes a task's weighted conditions can draw, for PlayPenaltyFlash's check at Go: each flash block
    /// against the rules (<see cref="PenaltyFlash.Problems"/>) and the shortest timeout of a kind whose flash plays, which
    /// the penalty sound's length (the flash's) must fit in. A task's TrialRules exposes it as PenaltyFlashes.
    /// </summary>
    public class PenaltyFlashRules
    {
        /// <summary>The shortest timeoutSeconds of a kind whose flash plays; +infinity when no flash plays.</summary>
        public double ShortestTimeoutSeconds = double.PositiveInfinity;
        /// <summary>The block and condition with that timeout, for a message.</summary>
        public string Where = "no condition";
        /// <summary>Each flash block that breaks the rules, once each, naming its condition.</summary>
        public List<string> Problems = new List<string>();

        /// <summary>Gets whether any trial can flash.</summary>
        public bool Plays { get { return !double.IsPositiveInfinity(ShortestTimeoutSeconds); } }

        /// <summary>The flashes of the weighted conditions' combinations: <paramref name="incorrect"/> and
        /// <paramref name="noResponse"/> each give a combination's kind (<see cref="Penalties.Of"/>; null skipped, penalty.noResponse null).</summary>
        public static PenaltyFlashRules Of<TTask>(IEnumerable<BuiltTrialCondition<TTask>> conditions, Func<TTask, ResolvedPenalty> incorrect, Func<TTask, ResolvedPenalty> noResponse)
        {
            var rules = new PenaltyFlashRules();
            var kinds = new[] { Tuple.Create("penalty.incorrect", incorrect), Tuple.Create("penalty.noResponse", noResponse) };
            foreach (var condition in conditions.Where(c => c.Weight > 0))
            {
                foreach (var combination in condition.Combinations)
                {
                    var where = condition.Where + (string.IsNullOrEmpty(combination.Picks) ? "" : " with " + combination.Picks);
                    foreach (var kind in kinds)
                    {
                        var penalty = kind.Item2(combination.Task);
                        if (penalty == null) continue;
                        foreach (var problem in PenaltyFlash.Problems(kind.Item1 + ".flash", penalty.Flash.Brightness, penalty.Flash.RateHz))
                        {
                            var named = problem + " (" + where + ")";
                            if (!rules.Problems.Contains(named)) rules.Problems.Add(named);
                        }
                        if (penalty.Flash.Plays && penalty.TimeoutSeconds < rules.ShortestTimeoutSeconds)
                        {
                            rules.ShortestTimeoutSeconds = penalty.TimeoutSeconds;
                            rules.Where = kind.Item1 + ".timeoutSeconds in " + where;
                        }
                    }
                }
            }
            return rules;
        }

        /// <summary>What refuses these flashes with a penalty sound of <paramref name="soundSeconds"/> (NaN: rig.yml names
        /// none): the rule problems, then, when a flash plays, a sound longer than the shortest flashed timeout (the flash
        /// lasts the sound) or none at all.</summary>
        public List<string> ProblemsWith(double soundSeconds)
        {
            var problems = new List<string>(Problems);
            if (!Plays) return problems;
            if (double.IsNaN(soundSeconds)) problems.Add("a penalty flash plays, but rig.yml's speakerArray names no penaltySound, whose length the flash lasts");
            else if (soundSeconds > ShortestTimeoutSeconds + 1e-9)
                problems.Add(string.Format(CultureInfo.InvariantCulture, "the penalty sound lasts {0:0.###} s, longer than {1} ({2:g} s) with a flash: the flash lasts the sound, so it must fit in the timeout", soundSeconds, Where, ShortestTimeoutSeconds));
            return problems;
        }
    }

    /// <summary>Checks a task's penalty flashes at Go against the penalty sound (<see cref="PenaltyFlashRules.ProblemsWith"/>).</summary>
    [Combinator]
    [Description("Checks the task's penalty flashes at Go (TrialRules' PenaltyFlashes) against the penalty sound's length (OpenRigs.Audio PenaltySoundSeconds) and gives that length, how long each flash lasts. Stops the workflow naming every problem: a flash block's brightness not 0 to 1 or rateHz not above 0 and at most 30 Hz, and, when a flash plays, a sound longer than the shortest timeout with a flash. Input: (the flashes, the sound's length in seconds).")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class CheckPenaltyFlashes
    {
        /// <summary>The flash's length for each input; throws naming every problem.</summary>
        public IObservable<TimeSpan> Process(IObservable<Tuple<PenaltyFlashRules, double>> source)
        {
            return source.Select(input =>
            {
                var problems = input.Item1.ProblemsWith(input.Item2);
                if (problems.Count > 0) throw new InvalidOperationException("The task's penalty flash is refused: " + string.Join("; ", problems) + ".");
                return double.IsNaN(input.Item2) ? TimeSpan.Zero : TimeSpan.FromTicks((long)Math.Round(input.Item2 * TimeSpan.TicksPerSecond));
            });
        }
    }
}
