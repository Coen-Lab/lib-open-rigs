using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenRigs.Manifest
{
    /// <summary>
    /// The running OpenRigs.Control experiment (ExperimentControl's ControlState.Current), read by name,
    /// so that OpenRigs.Manifest does not depend on OpenRigs.Control: a workflow can have the
    /// manifest without the experiment controller's package, and then there is no experiment to read.
    /// src\OpenRigs.Manifest.Tests runs against the real ControlState, and fails if a member
    /// named here is renamed there.
    /// </summary>
    class ControlExperiment
    {
        /// <summary>The ControlState members read here, for the test to check against OpenRigs.Control.</summary>
        internal static readonly string[] StateMembers = { "Current", "SubjectId", "SessionId", "Go", "StopReason", "FinishedAt", "LaunchedBy", "GoBy", "StoppedBy" };

        /// <summary>The Person members read here (OpenRigs.Control 0.1.0-local.9).</summary>
        internal static readonly string[] PersonMembers = { "Id", "Name" };

        /// <summary>The ControlEvent members of the Go event read here.</summary>
        internal static readonly string[] GoMembers = { "SubjectId", "SessionId", "Time" };

        readonly object state;

        ControlExperiment(object state)
        {
            this.state = state;
        }

        /// <summary>The running ControlServer's experiment, or null when OpenRigs.Control is not loaded or no ControlServer runs.</summary>
        public static ControlExperiment Current()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name == "OpenRigs.Control")
                .Select(a => a.GetType("OpenRigs.Control.ControlState"))
                .FirstOrDefault(t => t != null);
            var property = type != null ? type.GetProperty("Current") : null;
            var state = property != null ? property.GetValue(null, null) : null;
            return state != null ? new ControlExperiment(state) : null;
        }

        /// <summary>The subject ID ControlServer was given.</summary>
        public string SubjectId { get { return Read(state, "SubjectId") as string; } }

        /// <summary>The session ID ControlServer was given.</summary>
        public string SessionId { get { return Read(state, "SessionId") as string; } }

        /// <summary>The why of a controller's stop; null unless the controller stopped the experiment.</summary>
        public string StopReason { get { return Read(state, "StopReason") as string; } }

        /// <summary>When the workflow reported Finished; null if it never did.</summary>
        public DateTime? FinishedAt { get { return Read(state, "FinishedAt") as DateTime?; } }

        /// <summary>Who the controller said launched the experiment, as (ID, name); null if it named
        /// nobody, or OpenRigs.Control is older than 0.1.0-local.9.</summary>
        public KeyValuePair<string, string>? LaunchedBy { get { return Person("LaunchedBy"); } }

        /// <summary>Who the controller said sent Go, as (ID, name); null if it named nobody.</summary>
        public KeyValuePair<string, string>? GoBy { get { return Person("GoBy"); } }

        /// <summary>Who the controller said sent the stop, as (ID, name); null if it named nobody.</summary>
        public KeyValuePair<string, string>? StoppedBy { get { return Person("StoppedBy"); } }

        KeyValuePair<string, string>? Person(string member)
        {
            var person = Read(state, member);
            if (person == null) return null;
            return new KeyValuePair<string, string>(Read(person, "Id") as string ?? "", Read(person, "Name") as string ?? "");
        }

        /// <summary>Whether Go has been taken.</summary>
        public bool HasGo { get { return Read(state, "Go") != null; } }

        /// <summary>The subject ID on the Go event, or ControlServer's before Go.</summary>
        public string GoSubjectId { get { return HasGo ? Read(Read(state, "Go"), "SubjectId") as string : SubjectId; } }

        /// <summary>The session ID on the Go event, or ControlServer's before Go.</summary>
        public string GoSessionId { get { return HasGo ? Read(Read(state, "Go"), "SessionId") as string : SessionId; } }

        /// <summary>When Go was taken, in UTC; null before Go.</summary>
        public DateTime? GoTime { get { return HasGo ? Read(Read(state, "Go"), "Time") as DateTime? : null; } }

        static object Read(object target, string member)
        {
            if (target == null) return null;
            var property = target.GetType().GetProperty(member);
            return property != null ? property.GetValue(target, null) : null;
        }
    }
}
