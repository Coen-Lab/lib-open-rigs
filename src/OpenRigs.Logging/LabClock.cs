using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using Bonsai;
using Microsoft.Win32;
using YamlDotNet.Serialization;

namespace OpenRigs.Logging
{
    /// <summary>
    /// The lab's clock rules for naming data (specs/logging.md, section 2): the machine is on the lab's Windows time
    /// zone, follows daylight saving, and its time was synced within maxSyncAgeDays, all read from
    /// &lt;ConfigRoot&gt;\shared\lab.yml. The judgements are functions of what was read, so each state is tested
    /// without changing a machine's clock.
    /// </summary>
    public static class LabClock
    {
        /// <summary>The lab settings file, relative to ConfigRoot (beside each machine's folder).</summary>
        public static readonly string LabFile = Path.Combine("shared", "lab.yml");

        /// <summary>The lab's time zone and sync age, as lab.yml gives them.</summary>
        public class Settings
        {
            /// <summary>The Windows time zone id every acquisition machine must be on.</summary>
            public string TimeZone;

            /// <summary>The oldest Windows time sync, in days, that still allows a recording.</summary>
            public double MaxSyncAgeDays;
        }

        /// <summary>Reads lab.yml under ConfigRoot, throwing with what is missing.</summary>
        public static Settings ReadSettings(string configRoot)
        {
            var path = Path.GetFullPath(Path.Combine(configRoot, LabFile));
            Dictionary<string, string> values;
            try
            {
                values = new DeserializerBuilder().Build().Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                    ?? new Dictionary<string, string>();
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("Recording refused: the lab settings " + path + " could not be read (" + e.Message +
                    "). It is the lab's shared lab.yml, kept beside each machine's folder under ConfigRoot, with timeZone and maxSyncAgeDays.", e);
            }

            string zone, age;
            double maxAge;
            values.TryGetValue("timeZone", out zone);
            values.TryGetValue("maxSyncAgeDays", out age);
            if (string.IsNullOrWhiteSpace(zone) || !double.TryParse(age, NumberStyles.Float, CultureInfo.InvariantCulture, out maxAge))
            {
                throw new InvalidOperationException("Recording refused: " + path + " needs timeZone (a Windows time zone id) and maxSyncAgeDays (a number of days).");
            }

            return new Settings { TimeZone = zone.Trim(), MaxSyncAgeDays = maxAge };
        }

        /// <summary>What is wrong with the zone: its id differs from the lab's, or daylight saving is switched off.</summary>
        public static List<string> ZoneFaults(string id, bool daylightSavingDisabled, string wantId)
        {
            var wrong = new List<string>();
            if (id != wantId) wrong.Add("the time zone is '" + id + "', not the lab's '" + wantId + "'");
            // Both named, so neither hides the other: a pinned GMT reads the right zone name all summer.
            if (daylightSavingDisabled) wrong.Add("automatic daylight saving is switched off, so the clock stays on winter time all summer");
            return wrong;
        }

        /// <summary>What Windows says of its last sync.</summary>
        public class SyncState
        {
            /// <summary>Whether the last sync is known: a date, or a definite never (AgeDays null).</summary>
            public bool Known;

            /// <summary>Days since the last sync, one decimal place; null when it has never synced.</summary>
            public double? AgeDays;

            /// <summary>Why the state is unknown, or where it was read from.</summary>
            public string Detail = string.Empty;
        }

        /// <summary>
        /// Judges the last sync from what was read: whether w32tm /query /status answered (else <paramref name="failure"/>
        /// says why), its lines, and w32time's LastKnownGoodTime from the registry (null when absent).
        /// </summary>
        /// <remarks>
        /// A trigger-started w32time on a machine joined to nothing is often stopped, which makes w32tm fail, and in the
        /// minutes after it starts again says "unspecified" before its first sync of that run: in both cases the
        /// registry's LastKnownGoodTime (which survives the service stopping) gives the last sync. "unspecified" with no
        /// registry value is never synced; a w32tm that failed with no registry value is unknown, not never synced.
        /// The date is parsed in <paramref name="culture"/>, the culture w32tm formats it in.
        /// </remarks>
        public static SyncState JudgeSync(bool answered, string failure, IEnumerable<string> lines, DateTime? lastKnownGood, DateTime now, CultureInfo culture)
        {
            var state = new SyncState();
            Func<DateTime, double> age = last => Math.Round((now - last).TotalDays, 1);
            if (!answered)
            {
                state.Detail = failure ?? "w32tm did not answer";
                if (lastKnownGood.HasValue)
                {
                    state.Known = true;
                    state.AgeDays = age(lastKnownGood.Value);
                    state.Detail += "; last sync read from the registry instead";
                }

                return state;
            }

            foreach (var line in lines ?? Enumerable.Empty<string>())
            {
                var match = Regex.Match(line, @"^\s*Last Successful Sync Time:\s*(.+?)\s*$");
                if (!match.Success) continue;
                var raw = match.Groups[1].Value;
                if (Regex.IsMatch(raw, "^unspecified", RegexOptions.IgnoreCase))
                {
                    state.Known = true;
                    if (lastKnownGood.HasValue)
                    {
                        state.AgeDays = age(lastKnownGood.Value);
                        state.Detail = "w32tm has not synced since the service started; last sync read from the registry instead";
                    }
                }
                else
                {
                    DateTime last;
                    if (DateTime.TryParse(raw, culture, DateTimeStyles.None, out last))
                    {
                        state.Known = true;
                        state.AgeDays = age(last);
                    }
                    else
                    {
                        state.Detail = "w32tm reported its last sync as '" + raw + "', not a readable date";
                    }
                }
            }

            if (!state.Known && state.Detail.Length == 0) state.Detail = "w32tm's status has no Last Successful Sync Time";
            return state;
        }

        /// <summary>What is wrong with the sync: the service disabled, never synced, too old, or a clock behind its own last sync.</summary>
        public static List<string> SyncFaults(bool serviceDisabled, SyncState state, double maxAgeDays)
        {
            var wrong = new List<string>();
            if (serviceDisabled) wrong.Add("the Windows Time service is disabled, so the clock is never corrected");
            if (!state.Known) wrong.Add("the clock's sync state could not be read: " + state.Detail);
            else if (!state.AgeDays.HasValue) wrong.Add("Windows has never reported a successful time sync");
            else if (state.AgeDays.Value > maxAgeDays) wrong.Add("Windows last synced the clock " + state.AgeDays.Value.ToString(CultureInfo.InvariantCulture) + " days ago (the lab allows " + maxAgeDays.ToString(CultureInfo.InvariantCulture) + ")");
            // A last sync in the future is a clock gone backwards since (a flat CMOS battery); an hour of slack, so a
            // correction a sync itself made does not trip it.
            else if (state.AgeDays.Value < -1.0 / 24) wrong.Add("the clock is behind its own last sync by " + Math.Round(-state.AgeDays.Value, 1).ToString(CultureInfo.InvariantCulture) + " days");
            return wrong;
        }

        /// <summary>
        /// Checks this machine against lab.yml under ConfigRoot, throwing with every fault and how to fix it.
        /// </summary>
        public static void Check(string configRoot)
        {
            var settings = ReadSettings(configRoot);
            var faults = ZoneFaults(TimeZoneInfo.Local.Id, DaylightSavingDisabled(), settings.TimeZone);
            var fixes = new List<string>();
            if (faults.Count > 0) fixes.Add("set the time zone to '" + settings.TimeZone + "' with 'Adjust for daylight saving time automatically' on (Settings, Time & language, Date & time)");

            string failure;
            var lines = W32tmStatus(out failure);
            var sync = JudgeSync(lines != null, failure, lines, LastKnownGoodSync(), DateTime.Now, CultureInfo.CurrentCulture);
            var syncFaults = SyncFaults(W32timeDisabled(), sync, settings.MaxSyncAgeDays);
            if (syncFaults.Count > 0) fixes.Add("start the Windows Time service and resync the clock (as administrator: net start w32time, then w32tm /resync)");
            faults.AddRange(syncFaults);
            if (faults.Count > 0)
            {
                throw new InvalidOperationException("Recording refused, the clock that names the data cannot be trusted: " +
                    string.Join("; ", faults) + ". To fix: " + string.Join("; ", fixes) + ", then start again.");
            }
        }

        static bool DaylightSavingDisabled()
        {
            var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\TimeZoneInformation", "DynamicDaylightTimeDisabled", 0);
            return value is int && (int)value != 0;
        }

        static bool W32timeDisabled()
        {
            var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\W32Time", "Start", null);
            return value is int && (int)value == 4;
        }

        static DateTime? LastKnownGoodSync()
        {
            try
            {
                var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\W32Time\Config", "LastKnownGoodTime", null);
                var ticks = value is long ? (long)value : value is int ? (int)value : 0L;
                return ticks > 0 ? DateTime.FromFileTime(ticks) : (DateTime?)null;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        // w32tm /query /status's lines, or null with why it did not answer (a stopped service makes it exit non-zero).
        static string[] W32tmStatus(out string failure)
        {
            failure = null;
            try
            {
                var start = new ProcessStartInfo("w32tm", "/query /status")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(start))
                {
                    var output = process.StandardOutput.ReadToEndAsync();
                    var error = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(15000))
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { }
                        failure = "w32tm did not answer within 15 seconds";
                        return null;
                    }

                    if (process.ExitCode != 0)
                    {
                        failure = "w32tm /query /status failed: " + (output.Result + " " + error.Result).Trim();
                        return null;
                    }

                    return output.Result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                }
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                failure = "w32tm could not be run: " + e.Message;
                return null;
            }
        }
    }

    /// <summary>
    /// Represents an operator that checks the machine's clock against the lab's rules (LabClock) when subscribed and
    /// stops the workflow, saying what is wrong and how to fix it, before anything is named. With no ConfigRoot it
    /// checks nothing.
    /// </summary>
    [Combinator]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    [System.Xml.Serialization.XmlType(Namespace = "clr-namespace:OpenRigs.Logging;assembly=OpenRigs.Logging")]
    [Description("Checks the clock before any data is named: the time zone and daylight saving against ConfigRoot\\shared\\lab.yml, and Windows' last time sync. Stops the workflow, saying what is wrong and how to fix it. With no ConfigRoot it checks nothing.")]
    public class CheckLabClock
    {
        /// <summary>Gets or sets the root holding each machine's folder and shared\lab.yml; empty checks nothing.</summary>
        [Description("The root holding each machine's folder and shared\\lab.yml (ReadLabRig's ConfigRoot). Empty: nothing is checked.")]
        public string ConfigRoot { get; set; }

        /// <summary>Checks the clock before passing each element on.</summary>
        public IObservable<TSource> Process<TSource>(IObservable<TSource> source)
        {
            return source.Do(_ =>
            {
                if (!string.IsNullOrWhiteSpace(ConfigRoot)) LabClock.Check(ConfigRoot);
            });
        }
    }
}
