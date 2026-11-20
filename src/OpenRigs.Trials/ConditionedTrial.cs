using System;

namespace OpenRigs.Trials
{
    /// <summary>
    /// A conditioned task's trial as every such task draws it (open_rigs.rigs.trials CONDITIONS_COLUMNS, STIMULUS_COLUMNS,
    /// AUTO_REWARD_COLUMNS and the laser): a task's trial derives from it and adds its own fields. The field names are the trial
    /// log's columns, so a task's row copies them by name. A laser trial is an IZapitTrial through it.
    /// </summary>
    public class ConditionedTrial : IZapitTrial
    {
        int ITrial.TrialNumber { get { return trialNumber; } }
        int IZapitTrial.LaserCondition { get { return laserCondition; } }
        bool IZapitTrial.LaserOn { get { return laserOn; } }
        double IZapitTrial.LaserPowerMw { get { return laserPowerMw; } }

        /// <summary>The trial's number, from 1, repeats counted.</summary>
        public int trialNumber;
        /// <summary>Its condition's trialType.</summary>
        public string trialType;
        /// <summary>The 0-based index of its condition in the task's trialConditions.</summary>
        public int trialConditionId;
        /// <summary>Its combination's row in Settings_Combinations (TrialConditionCombination.Id).</summary>
        public int trialCombinationId;
        /// <summary>The interval drawn for it, in seconds: its start to the stimulus, with a quiet mouse.</summary>
        public double itiSeconds;
        /// <summary>What a reward on it gives, in microlitres, on every trial; 0 where none is on offer.</summary>
        public double rewardOfferedUl;
        /// <summary>The grating's contrast as the trial starts; 0 for a grey screen.</summary>
        public double visualInitialContrast;
        /// <summary>The grating's phase, in radians (DrawVisualPhase).</summary>
        public double visualPhaseRad;
        /// <summary>The sound's level as the trial starts, in dBFS RMS (SoundLevels.Parse); NaN when mute.</summary>
        public double auditoryInitialLevelDbfs;
        /// <summary>The mixer channel of rig.yml's speaker nearest the initial azimuth drawn; -1 when mute, SoundAzimuths.AllSpeakersChannel at "all".</summary>
        public int speakerChannel;
        /// <summary>That speaker's azimuth, in degrees; NaN when mute or at "all".</summary>
        public double auditoryInitialAzimuthDeg;
        /// <summary>That speaker's elevation as the trial starts, in degrees; NaN when mute or at "all".</summary>
        public double auditoryInitialElevationDeg;
        /// <summary>Its Zapit condition, from 1; 0 without optogenetics.</summary>
        public int laserCondition;
        /// <summary>Whether the laser fires on it.</summary>
        public bool laserOn;
        /// <summary>The laser power on a laser-on trial, in mW; 0 with the laser off.</summary>
        public double laserPowerMw;
        /// <summary>Whether it is an auto-reward trial.</summary>
        public bool autoReward;

        /// <summary>Draws the grating's phase uniformly from 0 to 2 pi from the task's one seeded stream, once for every trial.</summary>
        public void DrawVisualPhase(Random random)
        {
            visualPhaseRad = 2 * Math.PI * random.NextDouble();
        }
    }
}
