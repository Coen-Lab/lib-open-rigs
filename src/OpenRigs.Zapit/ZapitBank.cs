using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace OpenRigs.Zapit
{
    /// <summary>
    /// A bank lib_zapit built for one session (<c>Settings_ZapitBank_&lt;DateTime&gt;.json</c> and its <c>.bin</c>, index
    /// version 2): every scan and laser cycle and ramp tail a task can draw, so a trial is only selected and concatenated
    /// here, as lib_zapit's <c>bank.assemble</c> does it. Everything is read through the index's offsets and lengths; a
    /// .bin that does not match the index's hash, or a trial that would not end with laser and LED at 0 V, is refused.
    /// </summary>
    public sealed class ZapitBank
    {
        /// <summary>The index version this reader takes.</summary>
        public const int IndexVersion = 2;

        /// <summary>The NI AO range every sample must stay inside, in volts.</summary>
        public const double RangeVolts = 10;

        static readonly string[] ScanChannels = { "galvo_x", "galvo_y", "led" };
        readonly float[] scan;
        readonly float[] laser;

        ZapitBank(string jsonPath, JObject index, float[] scan, float[] laser)
        {
            JsonPath = jsonPath;
            this.scan = scan;
            this.laser = laser;
            ConditionsName = (string)index["conditions_name"]!;
            ConditionsSha256 = (string)index["conditions_sha256"]!;
            SampleRate = (double)index["sample_rate"]!;
            CeilingMw = (double)index["ceiling_mw"]!;
            Hashes = ((JObject)index["hashes"]!).Properties().ToDictionary(p => p.Name, p => (string)p.Value!);
            Conditions = index["conditions"]!.Select(c => new ZapitBankCondition(c)).ToList();
        }

        /// <summary>The index file the bank was read from.</summary>
        public string JsonPath { get; }

        /// <summary>The conditions file the bank was built from.</summary>
        public string ConditionsName { get; }

        /// <summary>The SHA-256 of the conditions file the bank was built from.</summary>
        public string ConditionsSha256 { get; }

        /// <summary>The AO sample rate the bank was built for, in samples per second.</summary>
        public double SampleRate { get; }

        /// <summary>The laser ceiling the bank was built under, in mW (rig.yml's zapit.ceilingMw then; 0 is laser disabled).</summary>
        public double CeilingMw { get; }

        /// <summary>The index's hashes: <c>scanner_fit</c>, <c>laser_curve</c>, <c>sample</c> and <c>bin</c>.</summary>
        public IReadOnlyDictionary<string, string> Hashes { get; }

        /// <summary>The conditions, condition 1 first.</summary>
        public IReadOnlyList<ZapitBankCondition> Conditions { get; }

        /// <summary>The longest laser ramp-down of any laser-on variant, in seconds: its tail cycles at the bank's sample rate
        /// (open_rigs.components.zapit max_ramp_down_seconds, the same rule).</summary>
        public double LongestRampSeconds => Conditions.SelectMany(c => c.Variants.Where(v => v.LaserOn).Select(v => v.TailCycles * c.ScanCycle.Length / SampleRate)).DefaultIfEmpty(0).Max();

        /// <summary>Reads and checks the bank whose index is at <paramref name="jsonPath"/>.</summary>
        public static ZapitBank Load(string jsonPath)
        {
            var index = JObject.Parse(File.ReadAllText(jsonPath));
            var name = Path.GetFileName(jsonPath);
            if ((int?)index["version"] != IndexVersion)
                throw new InvalidOperationException($"{name} is a version {index["version"]} Zapit bank; this ZapitDaq reads version {IndexVersion}.");
            if (!index["scan_channels"]!.Select(c => (string)c!).SequenceEqual(ScanChannels))
                throw new InvalidOperationException($"{name}'s scan channels are not {string.Join(", ", ScanChannels)}.");
            var binName = (string)index["bin"]!;
            var raw = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!, binName));
            using (var sha = SHA256.Create())
            {
                if (Hex(sha.ComputeHash(raw)) != (string)index["hashes"]!["bin"]!)
                    throw new InvalidOperationException($"{binName} does not match the hash in {name}.");
            }
            int scanRows = (int)index["scan_rows"]!, laserRows = (int)index["laser_rows"]!;
            if (raw.Length != (scanRows * ScanChannels.Length + laserRows) * sizeof(float))
                throw new InvalidOperationException($"{binName} does not match {name}'s scan_rows and laser_rows.");
            var floats = new float[raw.Length / sizeof(float)];
            Buffer.BlockCopy(raw, 0, floats, 0, raw.Length);
            if (floats.Any(v => !(Math.Abs(v) <= RangeVolts)))
                throw new InvalidOperationException($"{binName} holds a value outside +/-{RangeVolts} V.");
            var bank = new ZapitBank(jsonPath, index, floats.Take(scanRows * ScanChannels.Length).ToArray(), floats.Skip(scanRows * ScanChannels.Length).ToArray());
            bank.Check(name, scanRows, laserRows);
            return bank;
        }

        void Check(string name, int scanRows, int laserRows)
        {
            for (int i = 0; i < Conditions.Count; i++)
            {
                var c = Conditions[i];
                if (c.Condition != i + 1) throw new InvalidOperationException($"{name}'s condition {i + 1} is numbered {c.Condition}.");
                Within(c.ScanCycle, scanRows, name, c.Condition);
                Within(c.ScanEnd, scanRows, name, c.Condition);
                if (c.ScanEnd.Length != 1 || scan[c.ScanEnd.Offset * 3 + 2] != 0)
                    throw new InvalidOperationException($"{name}'s condition {c.Condition} does not end with the LED at 0 V.");
                foreach (var v in c.Variants)
                {
                    if (v.TailCycles < 0 || v.TailReplaces != (v.TailCycles == 0 ? 1 : 0))
                        throw new InvalidOperationException($"{name}'s condition {c.Condition} has tail_cycles {v.TailCycles} with tail_replaces {v.TailReplaces}.");
                    v.TailRows = v.TailCycles == 0 ? 1 : v.TailCycles * c.ScanCycle.Length;
                    if (v.LaserCycle is ZapitBankBlock cycle)
                    {
                        Within(cycle, laserRows, name, c.Condition);
                        if (cycle.Length != c.ScanCycle.Length)
                            throw new InvalidOperationException($"{name}'s condition {c.Condition} has a laser cycle unlike its scan cycle in length.");
                    }
                    if (v.LaserTail is ZapitBankBlock tail)
                    {
                        Within(tail, laserRows, name, c.Condition);
                        if (tail.Length != v.TailRows)
                            throw new InvalidOperationException($"{name}'s condition {c.Condition} has a laser tail unlike its tail_cycles in length.");
                        if (laser[tail.Offset + tail.Length - 1] != 0)
                            throw new InvalidOperationException($"{name}'s condition {c.Condition} at {v.PowerMw} mW does not end with the laser at 0 V.");
                    }
                }
            }
        }

        static void Within(ZapitBankBlock block, int rows, string name, int condition)
        {
            if (block.Offset < 0 || block.Length < 1 || block.Offset + block.Length > rows)
                throw new InvalidOperationException($"{name}'s condition {condition} has a block outside its section.");
        }

        /// <summary>Refuses the bank unless it was built from the conditions file at <paramref name="conditionsPath"/> and
        /// with the scanner fit, laser curve and sample hashes given (each checked only when not empty).</summary>
        public void Verify(string? conditionsPath, string? scannerFitHash, string? laserCurveHash, string? sampleHash)
        {
            var name = Path.GetFileName(JsonPath);
            if (!string.IsNullOrEmpty(conditionsPath))
            {
                using var sha = SHA256.Create();
                if (Hex(sha.ComputeHash(File.ReadAllBytes(conditionsPath))) != ConditionsSha256)
                    throw new InvalidOperationException($"{name} was not built from {Path.GetFileName(conditionsPath)}: its conditions hash differs.");
            }
            foreach (var (key, expected) in new[] { ("scanner_fit", scannerFitHash), ("laser_curve", laserCurveHash), ("sample", sampleHash) })
            {
                if (!string.IsNullOrEmpty(expected) && Hashes[key] != expected)
                    throw new InvalidOperationException($"{name}'s {key} hash is not the one given: the bank was built from another {key.Replace('_', ' ')}.");
            }
        }

        /// <summary>The variant of <paramref name="condition"/> with the laser on at <paramref name="powerMw"/>, or with the laser off.</summary>
        public ZapitBankVariant Variant(int condition, bool laserOn, double powerMw)
        {
            if (condition < 1 || condition > Conditions.Count)
                throw new InvalidOperationException($"The Zapit bank has no condition {condition}; it has {Conditions.Count}.");
            return Conditions[condition - 1].Variants.FirstOrDefault(v => v.LaserOn == laserOn && (!laserOn || v.PowerMw == powerMw))
                ?? throw new InvalidOperationException($"The Zapit bank has no condition {condition} with the laser {(laserOn ? "on at " + powerMw + " mW" : "off")}.");
        }

        /// <summary>The rows of a trial of <paramref name="cycles"/> cycles: the body, then the ramp tail.</summary>
        public int TrialRows(int condition, ZapitBankVariant variant, int cycles) =>
            BodyRows(condition, variant, cycles) + variant.TailRows;

        /// <summary>The rows before the ramp tail of a trial of <paramref name="cycles"/> cycles.</summary>
        public int BodyRows(int condition, ZapitBankVariant variant, int cycles) =>
            cycles * Conditions[condition - 1].ScanCycle.Length - variant.TailReplaces;

        /// <summary>The cycles of a trial lasting <paramref name="durationSeconds"/> before its ramp, or, for 0 (until the
        /// outcome), the most cycles whose trial fits <paramref name="maxTrialSeconds"/>; a longer trial is refused.</summary>
        public int Cycles(int condition, ZapitBankVariant variant, double durationSeconds, double maxTrialSeconds)
        {
            int cycleRows = Conditions[condition - 1].ScanCycle.Length;
            var capRows = (long)Math.Floor(maxTrialSeconds * SampleRate + 1e-6);
            if (durationSeconds < 0 || double.IsNaN(durationSeconds))
                throw new InvalidOperationException($"A Zapit trial cannot last {durationSeconds} s.");
            var cycles = durationSeconds > 0
                ? (int)Math.Ceiling(durationSeconds * SampleRate / cycleRows - 1e-9)
                : (int)((capRows + variant.TailReplaces - variant.TailRows) / cycleRows);
            if (cycles < 1 || TrialRows(condition, variant, cycles) > capRows)
                throw new InvalidOperationException(durationSeconds > 0
                    ? $"A Zapit trial of {durationSeconds} s with its ramp is longer than maxTrialSeconds ({maxTrialSeconds} s)."
                    : $"Condition {condition}'s ramp does not fit maxTrialSeconds ({maxTrialSeconds} s).");
            return cycles;
        }

        /// <summary>The trial's samples from <paramref name="firstRow"/> to its end, one row per channel (galvo X, galvo Y,
        /// laser, LED) as NI's writer takes them: the scan cycle repeated with its last row the condition's scan end, the
        /// laser cycle repeated over the body, then the laser tail.</summary>
        public double[,] Assemble(int condition, ZapitBankVariant variant, int cycles, int firstRow = 0)
        {
            var c = Conditions[condition - 1];
            int body = BodyRows(condition, variant, cycles), total = body + variant.TailRows;
            if (firstRow < 0 || firstRow >= total) throw new ArgumentOutOfRangeException(nameof(firstRow));
            var samples = new double[4, total - firstRow];
            for (int row = firstRow; row < total; row++)
            {
                var s = (row == total - 1 ? c.ScanEnd.Offset : c.ScanCycle.Offset + row % c.ScanCycle.Length) * 3;
                int i = row - firstRow;
                samples[0, i] = scan[s];
                samples[1, i] = scan[s + 1];
                samples[3, i] = scan[s + 2];
                var block = row < body ? variant.LaserCycle : variant.LaserTail;   // a null block is zeros
                if (block is ZapitBankBlock b) samples[2, i] = laser[b.Offset + (row < body ? row % c.ScanCycle.Length : row - body)];
            }
            return samples;
        }

        static string Hex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));
    }

    /// <summary>One condition of a <see cref="ZapitBank"/>.</summary>
    public sealed class ZapitBankCondition
    {
        internal ZapitBankCondition(JToken c)
        {
            Condition = (int)c["condition"]!;
            PointsMm = c["points_mm"]!.Select(p => p.Select(v => (double)v).ToArray()).ToArray();
            StereotaxicMm = c["stereotaxic_mm"]!.Select(p => p.Select(v => (double)v).ToArray()).ToArray();
            LaserPointsMm = string.Join(";", StereotaxicMm.Select(p => p[1].ToString(CultureInfo.InvariantCulture) + " " + p[0].ToString(CultureInfo.InvariantCulture)));
            PointCount = (int)c["n_points"]!;
            ScanCycle = new ZapitBankBlock(c["scan_cycle"]!);
            ScanEnd = new ZapitBankBlock(c["scan_end"]!);
            Variants = c["variants"]!.Select(v => new ZapitBankVariant(v)).ToList();
        }

        /// <summary>The condition's number, from 1.</summary>
        public int Condition { get; }

        /// <summary>The condition's points on the sample, in mm, one pair per point in lib_zapit's order.</summary>
        public double[][] PointsMm { get; }

        /// <summary>The condition's points in the conditions file, in mm from bregma, one [ML, AP] pair per point.</summary>
        public double[][] StereotaxicMm { get; }

        /// <summary>The condition's points as the trial log's laserPointsMm: "AP ML" per point, joined by ";".</summary>
        public string LaserPointsMm { get; }

        /// <summary>The number of points the beam visits.</summary>
        public int PointCount { get; }

        /// <summary>One cycle of galvo X, galvo Y and LED.</summary>
        public ZapitBankBlock ScanCycle { get; }

        /// <summary>The trial's last scan row: the galvos held, the LED at 0 V.</summary>
        public ZapitBankBlock ScanEnd { get; }

        /// <summary>Laser on at each power the task can draw, and laser off.</summary>
        public IReadOnlyList<ZapitBankVariant> Variants { get; }
    }

    /// <summary>A condition's laser column at one power (or off) in a <see cref="ZapitBank"/>.</summary>
    public sealed class ZapitBankVariant
    {
        internal ZapitBankVariant(JToken v)
        {
            LaserOn = (bool)v["laser_on"]!;
            PowerMw = (double)v["power_mw"]!;
            CappedPowerMw = (double)v["capped_power_mw"]!;
            PowerCapped = (bool)v["power_capped"]!;
            LaserCycle = Block(v["laser_cycle"]);
            LaserTail = Block(v["laser_tail"]);
            TailCycles = (int)v["tail_cycles"]!;
            TailReplaces = (int)v["tail_replaces"]!;
        }

        /// <summary>Whether the laser fires.</summary>
        public bool LaserOn { get; }

        /// <summary>The power the task asked for, in mW (0 with the laser off).</summary>
        public double PowerMw { get; }

        /// <summary>The power delivered after the laser maximum and the rig's ceiling, in mW.</summary>
        public double CappedPowerMw { get; }

        /// <summary>Whether a limit lowered the power.</summary>
        public bool PowerCapped { get; }

        static ZapitBankBlock? Block(JToken? b) => b == null || b.Type == JTokenType.Null ? null : new ZapitBankBlock(b);

        /// <summary>One cycle of the laser's control volts; null is zeros.</summary>
        public ZapitBankBlock? LaserCycle { get; }

        /// <summary>The ramp tail, ending at 0 V; null is zeros.</summary>
        public ZapitBankBlock? LaserTail { get; }

        /// <summary>The ramp's length in cycles; 0 is no ramp, a one-row tail at 0 V.</summary>
        public int TailCycles { get; }

        /// <summary>The body's last rows the tail replaces: 1 without a ramp, else 0.</summary>
        public int TailReplaces { get; }

        /// <summary>The tail's rows: <see cref="TailCycles"/> cycles, or 1 without a ramp.</summary>
        public int TailRows { get; internal set; }
    }

    /// <summary>Rows of one section of a bank's .bin.</summary>
    public readonly struct ZapitBankBlock
    {
        internal ZapitBankBlock(JToken b)
        {
            Offset = (int)b["offset"]!;
            Length = (int)b["length"]!;
        }

        /// <summary>The first row.</summary>
        public int Offset { get; }

        /// <summary>The number of rows.</summary>
        public int Length { get; }
    }
}
