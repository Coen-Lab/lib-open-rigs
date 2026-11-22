using System;

namespace OpenRigs.Zapit
{
    /// <summary>What a <see cref="ZapitDaqCommand"/> asks <see cref="ZapitDaq"/> to do.</summary>
    public enum ZapitDaqCommandKind
    {
        /// <summary>Load and check a bank (<see cref="ZapitDaqCommand.Path"/>); trials need one.</summary>
        LoadBank,
        /// <summary>Arm a trial as finite AO waiting on the trigger line.</summary>
        Arm,
        /// <summary>End the armed trial early: its ramp, then laser and LED at 0 V.</summary>
        EarlyStop,
        /// <summary>Stop at once and write 0 V on every line.</summary>
        Zero,
        /// <summary>Calibration: write galvo X, galvo Y and laser volts on demand (LED 0 V).</summary>
        Point,
        /// <summary>Calibration: play laser levels on the AO clock and read the photodiode on the same clock.</summary>
        Staircase,
    }

    /// <summary>One command to <see cref="ZapitDaq"/>. Fields a kind does not use are ignored.</summary>
    public class ZapitDaqCommand
    {
        /// <summary>What to do.</summary>
        public ZapitDaqCommandKind Kind { get; set; }

        /// <summary>LoadBank: the bank's index, <c>Settings_ZapitBank_&lt;DateTime&gt;.json</c>.</summary>
        public string? Path { get; set; }

        /// <summary>LoadBank: the session's conditions file copy, whose SHA-256 the bank must carry (unchecked when empty).</summary>
        public string? ConditionsPath { get; set; }

        /// <summary>LoadBank: the scanner fit hash the bank must carry (unchecked when empty).</summary>
        public string? ScannerFitHash { get; set; }

        /// <summary>LoadBank: the laser curve hash the bank must carry (unchecked when empty).</summary>
        public string? LaserCurveHash { get; set; }

        /// <summary>LoadBank: the sample calibration hash the bank must carry (unchecked when empty).</summary>
        public string? SampleHash { get; set; }

        /// <summary>Arm, EarlyStop: the trial's number.</summary>
        public int Trial { get; set; }

        /// <summary>Arm: the condition, from 1.</summary>
        public int Condition { get; set; }

        /// <summary>Arm: whether the laser fires.</summary>
        public bool LaserOn { get; set; }

        /// <summary>Arm: the power, in mW, one the bank holds.</summary>
        public double PowerMw { get; set; }

        /// <summary>Arm: the stimulus before its ramp, in seconds; 0 runs until the outcome, finite at maxTrialSeconds.</summary>
        public double DurationSeconds { get; set; }

        /// <summary>Point, Staircase: galvo X, in volts.</summary>
        public double GalvoXVolts { get; set; }

        /// <summary>Point, Staircase: galvo Y, in volts.</summary>
        public double GalvoYVolts { get; set; }

        /// <summary>Point: the laser's control volts.</summary>
        public double LaserVolts { get; set; }

        /// <summary>Staircase: the laser's control volts at each step.</summary>
        public double[]? LaserLevelsVolts { get; set; }

        /// <summary>Staircase: the samples each step lasts.</summary>
        public int SamplesPerLevel { get; set; }

        /// <inheritdoc/>
        public override string ToString() => Kind switch
        {
            ZapitDaqCommandKind.LoadBank => $"LoadBank({Path})",
            ZapitDaqCommandKind.Arm => $"Arm(trial={Trial}, condition={Condition}, laserOn={LaserOn}, power={PowerMw}, duration={DurationSeconds})",
            ZapitDaqCommandKind.EarlyStop => $"EarlyStop(trial={Trial})",
            ZapitDaqCommandKind.Point => $"Point({GalvoXVolts}, {GalvoYVolts}, laser={LaserVolts})",
            ZapitDaqCommandKind.Staircase => $"Staircase({LaserLevelsVolts?.Length} levels x {SamplesPerLevel})",
            _ => Kind.ToString(),
        };
    }

    /// <summary>What a <see cref="ZapitDaqEvent"/> reports.</summary>
    public enum ZapitDaqEventKind
    {
        /// <summary>A bank was loaded and checked (<see cref="ZapitDaqEvent.Bank"/>).</summary>
        BankLoaded,
        /// <summary>A trial's buffer was written and the task started, waiting on the trigger (<see cref="ZapitDaqEvent.ArmSeconds"/>).</summary>
        Armed,
        /// <summary>The trigger came and the first samples were generated.</summary>
        Started,
        /// <summary>The trial played to its end; the lines were zeroed.</summary>
        Ended,
        /// <summary>The trial was stopped early (after its ramp) or before its trigger; the lines were zeroed.</summary>
        Stopped,
        /// <summary>A Zero command stopped any task and wrote 0 V.</summary>
        Zeroed,
        /// <summary>A Point command was written.</summary>
        Pointed,
        /// <summary>A staircase played; <see cref="ZapitDaqEvent.AnalogIn"/> holds the photodiode, one value per AO sample.</summary>
        StaircaseRead,
    }

    /// <summary>One event from <see cref="ZapitDaq"/>.</summary>
    public class ZapitDaqEvent
    {
        /// <summary>What happened.</summary>
        public ZapitDaqEventKind Kind { get; set; }

        /// <summary>When the node saw it, host clock.</summary>
        public DateTimeOffset Time { get; set; }

        /// <summary>The trial's number (trial events).</summary>
        public int Trial { get; set; }

        /// <summary>The trial's condition.</summary>
        public int Condition { get; set; }

        /// <summary>Whether the trial's laser fires.</summary>
        public bool LaserOn { get; set; }

        /// <summary>The power asked for, in mW.</summary>
        public double PowerMw { get; set; }

        /// <summary>The power delivered after the laser maximum and the ceiling, in mW.</summary>
        public double CappedPowerMw { get; set; }

        /// <summary>Whether a limit lowered the power.</summary>
        public bool PowerCapped { get; set; }

        /// <summary>The trial's samples per channel.</summary>
        public long Samples { get; set; }

        /// <summary>The samples per channel generated when the event was seen.</summary>
        public long SamplesGenerated { get; set; }

        /// <summary>Armed: how long building, writing and starting the task took, in seconds.</summary>
        public double ArmSeconds { get; set; }

        /// <summary>BankLoaded: the bank.</summary>
        public ZapitBank? Bank { get; set; }

        /// <summary>StaircaseRead: the photodiode volts, one per AO sample.</summary>
        public double[]? AnalogIn { get; set; }

        /// <inheritdoc/>
        public override string ToString() => $"{Kind}(trial={Trial}, samples={SamplesGenerated}/{Samples})";
    }
}
