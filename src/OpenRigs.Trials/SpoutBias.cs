using OpenRigs.Devices;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenRigs.Trials
{
    /// <summary>A finished trial as <see cref="SpoutBias"/> counts it: the task's trial or outcome gives it, in the task file's words.</summary>
    public interface ISpoutBiasResponse
    {
        /// <summary>Gets the trial's target: left or right is counted; anything else (a probe, go/no-go) is not.</summary>
        string Target { get; }

        /// <summary>Gets the mouse's choice: left or right is counted; anything else (no response) is not.</summary>
        string Choice { get; }

        /// <summary>Gets whether only the target's spout was offered: not counted.</summary>
        bool SingleSpoutOffered { get; }

        /// <summary>Gets the trial's repeat count, 0 for a new draw: a repeat is not counted, so the correction does not fight the repeat rule.</summary>
        int RepeatCount { get; }

        /// <summary>Gets whether the laser was on: not counted.</summary>
        bool LaserOn { get; }
    }

    /// <summary>
    /// The task's spout bias correction (spouts.biasCorrection): the last <see cref="TrialsPerSide"/> counted results of
    /// left-target trials and of right-target trials, each correct or not. Once both are full, after every counted response,
    /// the bias is the left fraction correct less the right's: above <see cref="Threshold"/> the mouse favours the left spout,
    /// and the spouts move one <see cref="StepSteps"/> toward its left (BiasLeft: the right spout toward the midline); below
    /// minus the threshold, toward its right (BiasRight); else nothing, and the offset stays where the mouse is balanced.
    /// At most one move a trial, none past <see cref="MaxSteps"/> either way. A new one starts at offset 0 with empty buffers.
    /// The task sends each move to LickSpoutStage.Commands at the time <see cref="SpoutCommands"/> gives it (the outcome's
    /// <see cref="ISpoutBiasOutcome.SpoutBiasCommand"/>), and <see cref="StageSettings"/> once to LickSpoutStage.BiasSettings.
    /// </summary>
    public class SpoutBias
    {
        /// <summary>The furthest maxSteps may be, in steps (open_rigs.core.task BIAS_CORRECTION_MAX_STEPS).</summary>
        public const int MaxStepsCeiling = 1000;

        /// <summary>The words of a side target and choice.</summary>
        public const string Left = "left", Right = "right";

        readonly Queue<bool> left = new Queue<bool>();
        readonly Queue<bool> right = new Queue<bool>();

        /// <summary>The rule with the task's spouts.biasCorrection; throws naming any value <see cref="Problems"/> refuses.</summary>
        public SpoutBias(int trialsPerSide, double threshold, int stepSteps, int maxSteps)
        {
            var problems = Problems(trialsPerSide, threshold, stepSteps, maxSteps);
            if (problems.Count > 0) throw new ArgumentException(string.Join("; ", problems));
            TrialsPerSide = trialsPerSide;
            Threshold = threshold;
            StepSteps = stepSteps;
            MaxSteps = maxSteps;
        }

        /// <summary>Gets each buffer's length: the last this many counted trials of each target side.</summary>
        public int TrialsPerSide { get; private set; }

        /// <summary>Gets the bias above which (or below minus which) the spouts move.</summary>
        public double Threshold { get; private set; }

        /// <summary>Gets the motor steps one move shifts the base.</summary>
        public int StepSteps { get; private set; }

        /// <summary>Gets the furthest the offset may go each way, in steps.</summary>
        public int MaxSteps { get; private set; }

        /// <summary>Gets the offset the moves so far add up to, in steps, positive toward the mouse's left, as LickSpoutStage holds it.</summary>
        public int OffsetSteps { get; private set; }

        /// <summary>Gets the last bias worked out (left fraction correct less right), or null until both buffers are full.</summary>
        public double? Bias { get; private set; }

        /// <summary>Gets the step and limit for LickSpoutStage.BiasSettings.</summary>
        public SpoutBiasSettings StageSettings
        {
            get { return new SpoutBiasSettings { StepSteps = StepSteps, MaxSteps = MaxSteps }; }
        }

        /// <summary>Whether a response is counted: a left or right target, both spouts offered, a left or right choice, not a repeat, laser off.</summary>
        public static bool Counts(ISpoutBiasResponse response)
        {
            return response != null && IsSide(response.Target) && IsSide(response.Choice)
                && !response.SingleSpoutOffered && response.RepeatCount == 0 && !response.LaserOn;
        }

        /// <summary>Takes one finished trial: BiasLeft or BiasRight when the spouts are to move, else null.</summary>
        public LickSpoutCommand? Respond(ISpoutBiasResponse response)
        {
            if (!Counts(response)) return null;
            var buffer = response.Target == Left ? left : right;
            buffer.Enqueue(response.Choice == response.Target);
            if (buffer.Count > TrialsPerSide) buffer.Dequeue();
            if (left.Count < TrialsPerSide || right.Count < TrialsPerSide) return null;

            // Both buffers hold TrialsPerSide, so the bias is (correct left - correct right) / TrialsPerSide, compared in counts.
            var difference = left.Count(correct => correct) - right.Count(correct => correct);
            Bias = (double)difference / TrialsPerSide;
            var limit = Threshold * TrialsPerSide + 1e-9;
            var command = difference > limit ? LickSpoutCommand.BiasLeft : difference < -limit ? LickSpoutCommand.BiasRight : (LickSpoutCommand?)null;
            if (!command.HasValue) return null;
            var moved = Math.Max(-MaxSteps, Math.Min(MaxSteps, OffsetSteps + (command == LickSpoutCommand.BiasLeft ? StepSteps : -StepSteps)));
            if (moved == OffsetSteps) return null;
            OffsetSteps = moved;
            return command;
        }

        /// <summary>spouts.biasCorrection's values against their bounds, as the task schema has them: trialsPerSide 1 or more,
        /// threshold above 0 and at most 1, maxSteps 1 to <see cref="MaxStepsCeiling"/>, stepSteps 1 to maxSteps.</summary>
        public static List<string> Problems(int trialsPerSide, double threshold, int stepSteps, int maxSteps)
        {
            var problems = new List<string>();
            if (trialsPerSide < 1) problems.Add(string.Format("spouts.biasCorrection.trialsPerSide ({0}) must be 1 or more", trialsPerSide));
            if (!(threshold > 0 && threshold <= 1)) problems.Add(string.Format("spouts.biasCorrection.threshold ({0}) must be above 0 and at most 1", threshold));
            if (maxSteps < 1 || maxSteps > MaxStepsCeiling) problems.Add(string.Format("spouts.biasCorrection.maxSteps ({0}) must be 1 to {1} steps", maxSteps, MaxStepsCeiling));
            if (stepSteps < 1) problems.Add(string.Format("spouts.biasCorrection.stepSteps ({0}) must be 1 or more", stepSteps));
            else if (stepSteps > maxSteps) problems.Add(string.Format("spouts.biasCorrection.stepSteps ({0}) is above maxSteps ({1}): one move would go past the furthest", stepSteps, maxSteps));
            return problems;
        }

        static bool IsSide(string word)
        {
            return word == Left || word == Right;
        }
    }
}
