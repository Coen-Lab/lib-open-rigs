using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenRigs.Core;
using OpenRigs.Devices.Rigs;

namespace OpenRigs.Trials.Tests
{
    public enum Kind { [System.Runtime.Serialization.EnumMember(Value = "visual")] Visual }

    public class Condition : ITrialCondition
    {
        [YamlDotNet.Serialization.YamlMember(Alias = "weight")][Newtonsoft.Json.JsonProperty("weight")] public int Weight { get; set; }
        [YamlDotNet.Serialization.YamlMember(Alias = "pairs")][Newtonsoft.Json.JsonProperty("pairs")] public List<List<string>> Pairs { get; set; } = new List<List<string>>();
        [YamlDotNet.Serialization.YamlMember(Alias = "set")][Newtonsoft.Json.JsonProperty("set")] public object Set { get; set; }
    }

    public class Stage : ICurriculum
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public string Version { get; set; } = "";
        public string Path { get; set; } = "";
        public string Label { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    public class Opto
    {
        [YamlDotNet.Serialization.YamlMember(Alias = "enabled")][Newtonsoft.Json.JsonProperty("enabled")] public bool Enabled { get; set; }
        [YamlDotNet.Serialization.YamlMember(Alias = "power")][Newtonsoft.Json.JsonProperty("power")] public double Power { get; set; }
    }

    public class Params : IConditionedTask
    {
        [YamlDotNet.Serialization.YamlMember(Alias = "rngSeed")][Newtonsoft.Json.JsonProperty("rngSeed")] public double? RngSeed { get; set; }
        [YamlDotNet.Serialization.YamlMember(Alias = "trialConditions")][Newtonsoft.Json.JsonProperty("trialConditions")] public List<Condition> TrialConditions { get; set; } = new List<Condition>();
        [YamlDotNet.Serialization.YamlMember(Alias = "contrasts")][Newtonsoft.Json.JsonProperty("contrasts")] public List<double> Contrasts { get; set; } = new List<double>();
        [YamlDotNet.Serialization.YamlMember(Alias = "delaySeconds")][Newtonsoft.Json.JsonProperty("delaySeconds")] public double DelaySeconds { get; set; }
        [YamlDotNet.Serialization.YamlMember(Alias = "optogenetics")][Newtonsoft.Json.JsonProperty("optogenetics")] public Opto Optogenetics { get; set; } = new Opto();
        IList<ITrialCondition> IConditionedTask.Conditions { get { return TrialConditions.Cast<ITrialCondition>().ToList(); } }
        ICurriculum IConditionedTask.Stage { get { return null; } }
        bool IConditionedTask.OptogeneticsEnabled { get { return Optogenetics.Enabled; } }
    }

    public class Logic { [YamlDotNet.Serialization.YamlMember(Alias = "taskParameters")] public Params TaskParameters { get; set; } = new Params(); }

    public class CostSound
    {
        [YamlDotNet.Serialization.YamlMember(Alias = "levelDbfs")][Newtonsoft.Json.JsonProperty("levelDbfs")] public object LevelDbfs { get; set; }
    }

    public class Cost
    {
        [YamlDotNet.Serialization.YamlMember(Alias = "timeoutSeconds")][Newtonsoft.Json.JsonProperty("timeoutSeconds")] public double? TimeoutSeconds { get; set; }
        [YamlDotNet.Serialization.YamlMember(Alias = "sound")][Newtonsoft.Json.JsonProperty("sound")] public CostSound Sound { get; set; } = new CostSound();
    }

    public class PenaltyBlock
    {
        [YamlDotNet.Serialization.YamlMember(Alias = "timeoutSeconds")][Newtonsoft.Json.JsonProperty("timeoutSeconds")] public double TimeoutSeconds { get; set; }
        [YamlDotNet.Serialization.YamlMember(Alias = "sound")][Newtonsoft.Json.JsonProperty("sound")] public CostSound Sound { get; set; } = new CostSound { LevelDbfs = -35.0 };
        [YamlDotNet.Serialization.YamlMember(Alias = "noResponse")][Newtonsoft.Json.JsonProperty("noResponse")] public Cost NoResponse { get; set; } = new Cost();
    }

    public class PenaltyParams : IConditionedTask
    {
        public double? RngSeed { get; set; }
        [YamlDotNet.Serialization.YamlMember(Alias = "trialConditions")][Newtonsoft.Json.JsonProperty("trialConditions")] public List<Condition> TrialConditions { get; set; } = new List<Condition>();
        [YamlDotNet.Serialization.YamlMember(Alias = "penalty")][Newtonsoft.Json.JsonProperty("penalty")] public PenaltyBlock Penalty { get; set; } = new PenaltyBlock();
        IList<ITrialCondition> IConditionedTask.Conditions { get { return TrialConditions.Cast<ITrialCondition>().ToList(); } }
        ICurriculum IConditionedTask.Stage { get { return null; } }
        bool IConditionedTask.OptogeneticsEnabled { get { return false; } }
    }

    public class PenaltyLogic { [YamlDotNet.Serialization.YamlMember(Alias = "taskParameters")] public PenaltyParams TaskParameters { get; set; } = new PenaltyParams(); }

    [TestClass]
    public class TrialConditionsTests
    {
        const string Types = @"[{""trialType"":""visual"",""if"":{""properties"":{""contrasts"":{""contains"":{""exclusiveMinimum"":0}}},""required"":[""contrasts""]}},{""trialType"":""neutral""}]";
        static readonly ConditionsSchema Schema = new ConditionsSchema(new[] { "rngSeed", "trialConditions", "optogenetics.enabled" }, new[] { "contrasts" }, 8, Types);

        static string Text(string conditions)
        {
            return "taskParameters:\n  rngSeed: 3\n  contrasts: [0.5, 1]\n  delaySeconds: 0\n  optogenetics:\n    enabled: false\n    power: 1\n  trialConditions:\n" + conditions;
        }

        static List<BuiltTrialCondition<Params>> Build(string text, List<string> problems)
        {
            var task = TaskYaml.Read<Logic>(text).TaskParameters;
            return TrialConditions.Build(text, task, t => TaskYaml.Read<Logic>(t).TaskParameters, Schema, problems);
        }

        [TestMethod]
        public void EachConditionExpandsIntoItsCombinationsFirstCellFastest()
        {
            var problems = new List<string>();
            var built = Build(Text("  - {weight: 2, set: {delaySeconds: [1, 2]}}\n  - {weight: 1}\n"), problems);
            CollectionAssert.AreEqual(new string[0], problems);
            var first = built[0].Combinations;
            CollectionAssert.AreEqual(new[] { "1 0.5", "1 1", "2 0.5", "2 1" }, first.Select(m => m.Task.DelaySeconds + " " + m.Task.Contrasts.Single()).ToArray());
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5 }, built.SelectMany(c => c.Combinations).Select(m => m.Id).ToArray());
            Assert.AreEqual("trialConditions[0] (visual)", built[0].Where);
            Assert.AreEqual("contrasts [0.5], delaySeconds 1", first[0].Picks);
            Assert.AreEqual(0, first[0].Task.TrialConditions.Count, "a combination is read without the conditions");
        }

        [TestMethod]
        public void EachBadConditionIsRefusedNamingIt()
        {
            var cases = new Dictionary<string, string>
            {
                { "  - {weight: 1, set: {rngSeed: 1}}\n", "trialConditions[0] (visual) sets or pairs rngSeed, a session field (x-session-fields: rngSeed)" },
                { "  - {weight: 1, set: {optogenetics: {enabled: true}}}\n", "trialConditions[0] (visual) sets or pairs optogenetics, a session field (x-session-fields: optogenetics.enabled)" },
                { "  - {weight: 1, set: {nope: 1}}\n", "trialConditions[0] (visual) sets or pairs nope, which is not a task field" },
                { "  - {weight: 1, set: {optogenetics.power: 2}}\n", "trialConditions[0] (visual) sets optogenetics.power with optogenetics off" },
                { "  - {}\n", "trialConditions[0] (visual): it has no weight" },
                { "  - {weight: 1, set: 3}\n", "trialConditions[0] (visual): its set is not a map" },
                { "  - {weight: 1, pairs: [[contrasts]]}\n", "trialConditions[0] (visual): each pairs group needs two paths or more, each path in one group" },
                { "  - {weight: 1, set: {delaySeconds: [1, 2, 3]}}\n", "trialConditions[0] (visual) expands into 6 combinations, more than 8 (x-max-combinations)" },
                { "  - {weight: 0}\n", "trialConditions needs a condition with a weight above 0" },
            };
            foreach (var c in cases)
            {
                var problems = new List<string>();
                var built = Build(Text(c.Key), problems);
                if (c.Value.Contains("expands"))
                {
                    Assert.IsNotNull(built, c.Key);
                    continue;
                }
                Assert.IsNull(built, c.Key);
                CollectionAssert.Contains(problems, c.Value, string.Join(" | ", problems));
            }
        }

        // P9: a condition's trialType is derived from its set over the task's values, never stored.
        [TestMethod]
        public void EachConditionsTrialTypeIsDerivedFromItsValues()
        {
            var problems = new List<string>();
            var built = Build(Text("  - {weight: 1}\n  - {weight: 1, set: {contrasts: [0]}}\n"), problems);
            CollectionAssert.AreEqual(new string[0], problems);
            CollectionAssert.AreEqual(new[] { "visual", "neutral" }, built.Select(c => c.TrialType).ToArray());
            Assert.AreEqual("trialConditions[1] (neutral)", built[1].Where);
            StringAssert.Contains(TrialConditions.CombinationsJson(TaskYaml.Read<Logic>(Text("  - {weight: 1}\n")).TaskParameters, built, Schema), "\"trialType\": \"neutral\"");
        }

        [TestMethod]
        public void AStoredTrialTypeIsAnUnknownKey()
        {
            Assert.ThrowsException<YamlDotNet.Core.YamlException>(() => TaskYaml.Read<Logic>(Text("  - {trialType: visual, weight: 1}\n")));
        }

        [TestMethod]
        public void TrialTypesFollowThePythonRule()
        {
            var types = Newtonsoft.Json.Linq.JArray.Parse(@"[{""trialType"":""conflict"",""if"":{""properties"":{""v"":{""contains"":{""exclusiveMinimum"":0}}},""required"":[""v""]},""mirrored"":[""v"",""a""]},{""trialType"":""sound"",""if"":{""properties"":{""level"":{""contains"":{""type"":""number""}}},""required"":[""level""]}},{""trialType"":""neutral""}]");
            Func<object, Newtonsoft.Json.Linq.JToken> j = Newtonsoft.Json.Linq.JToken.FromObject;
            var task = new Dictionary<string, Newtonsoft.Json.Linq.JToken> { { "v", j(new[] { 60, -60 }) }, { "a", j(new[] { -60.0, 60.0 }) }, { "level", j("mute") } };
            var paired = new List<List<string>> { new List<string> { "v", "a" } };
            var none = new Dictionary<string, Newtonsoft.Json.Linq.JToken>();
            Assert.AreEqual("conflict", TrialTypes.Derive(types, task, none, paired));
            Assert.AreEqual("neutral", TrialTypes.Derive(types, task, none, new List<List<string>>()), "mirrored needs the pair");
            Assert.AreEqual("sound", TrialTypes.Derive(types, task, new Dictionary<string, Newtonsoft.Json.Linq.JToken> { { "level", j(-40) } }, new List<List<string>>()));
            Assert.IsTrue(TrialTypes.Matches(Newtonsoft.Json.Linq.JToken.Parse(@"{""const"":1}"), j(1.0)));
            Assert.IsFalse(TrialTypes.Matches(Newtonsoft.Json.Linq.JToken.Parse(@"{""const"":1}"), j(true)));
            var unread = Newtonsoft.Json.Linq.JArray.Parse(@"[{""trialType"":""x"",""if"":{""properties"":{""level"":{""pattern"":""m""}},""required"":[""level""]}},{""trialType"":""y""}]");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => TrialTypes.Derive(unread, task, none, paired)).Message, "keyword pattern");
            var missing = Newtonsoft.Json.Linq.JArray.Parse(@"[{""trialType"":""x"",""if"":{""properties"":{""nope"":{}},""required"":[""nope""]}},{""trialType"":""y""}]");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => TrialTypes.Derive(missing, task, none, paired)).Message, "names nope, not in the values");
            var problems = new List<string>();
            var schema = new ConditionsSchema(Schema.SessionFields, Schema.ListFields, 8, @"[{""trialType"":""x"",""if"":{""properties"":{""contrasts"":{""items"":{""const"":9}}},""required"":[""contrasts""]}},{""trialType"":""y"",""mirrored"":[""contrasts"",""delaySeconds""]}]");
            var text = Text("  - {weight: 1}\n");
            Assert.IsNull(TrialConditions.Build(text, TaskYaml.Read<Logic>(text).TaskParameters, t => TaskYaml.Read<Logic>(t).TaskParameters, schema, problems));
            CollectionAssert.Contains(problems, "trialConditions[0]: no x-trial-types entry matches the condition's values");
        }

        [TestMethod]
        public void TooManyCombinationsAreRefused()
        {
            var problems = new List<string>();
            Assert.IsNull(Build(Text("  - {weight: 1, set: {delaySeconds: [1, 2, 3, 4, 5]}}\n"), problems));
            CollectionAssert.Contains(problems, "trialConditions[0] (visual) expands into 10 combinations, more than 8 (x-max-combinations)");
        }

        [TestMethod]
        public void SettingsCombinationsKeepWhatVariesAndNoSessionField()
        {
            var problems = new List<string>();
            var text = Text("  - {weight: 2, set: {delaySeconds: 4}}\n");
            var task = TaskYaml.Read<Logic>(text).TaskParameters;
            var built = TrialConditions.Build(text, task, t => TaskYaml.Read<Logic>(t).TaskParameters, Schema, problems);
            var json = Newtonsoft.Json.Linq.JObject.Parse(TrialConditions.CombinationsJson(task, built, Schema));
            var rows = (Newtonsoft.Json.Linq.JArray)json["rows"];
            Assert.AreEqual(2, rows.Count);
            CollectionAssert.AreEqual(new[] { "contrasts", "delaySeconds" }, ((Newtonsoft.Json.Linq.JObject)rows[0]["values"]).Properties().Select(p => p.Name).ToArray());
            Assert.AreEqual(1, (int)rows[1]["trialCombinationId"]);
            Assert.AreEqual(2, (int)rows[1]["conditionWeight"]);
        }

        [TestMethod]
        public void TheMaxIsTheWeightedCombinationsHighest()
        {
            var problems = new List<string>();
            var built = Build(Text("  - {weight: 1, set: {delaySeconds: [1, 3]}}\n  - {weight: 0, set: {delaySeconds: 9}}\n"), problems);
            var max = MaxSoundLevel.Of(built, t => t.DelaySeconds);
            Assert.AreEqual(3, max.LevelDbfs);
            Assert.AreEqual("trialConditions[0] (visual) with contrasts [0.5], delaySeconds 3", max.Where);
            Assert.AreEqual(1, MaxSoundLevel.Of(built, t => t.DelaySeconds < 2 ? (object)t.DelaySeconds : SoundLevels.Mute).LevelDbfs);   // mute plays nothing
            Assert.IsTrue(double.IsNaN(MaxSoundLevel.Of(built, t => SoundLevels.Mute).LevelDbfs));
        }

        // A condition sets a penalised miss's level by its three-level path, though the task file leaves the block out (its leaves null).
        [TestMethod]
        public void AConditionSetsAPenalisedMisssLevelAndTheMaxCountsIt()
        {
            var text = "taskParameters:\n  penalty:\n    timeoutSeconds: 4\n  trialConditions:\n  - {weight: 1}\n  - {weight: 1, set: {penalty.noResponse.sound.levelDbfs: -20}}\n";
            var problems = new List<string>();
            var schema = new ConditionsSchema(new[] { "trialConditions" }, new string[0], 8, @"[{""trialType"":""any""}]");
            var built = TrialConditions.Build(text, TaskYaml.Read<PenaltyLogic>(text).TaskParameters, t => TaskYaml.Read<PenaltyLogic>(t).TaskParameters, schema, problems);
            CollectionAssert.AreEqual(new string[0], problems);
            Assert.IsNull(built[0].Combinations[0].Task.Penalty.NoResponse.Sound.LevelDbfs);
            var max = MaxSoundLevel.Of(built, t => t.Penalty.Sound.LevelDbfs, t => t.Penalty.NoResponse.Sound.LevelDbfs);
            Assert.AreEqual(-20, max.LevelDbfs);
            Assert.AreEqual("trialConditions[1] (any)", max.Where);
            Assert.AreEqual(-35, MaxSoundLevel.Of(built, t => t.Penalty.Sound.LevelDbfs, t => (object)null).LevelDbfs, "a null level is skipped");
            Assert.ThrowsException<InvalidOperationException>(() => MaxSoundLevel.Of(built, t => t.Penalty.NoResponse.Sound.LevelDbfs), "one selector refuses a null");
        }

        [TestMethod]
        public void APenalisedMissCostsItsOverrideAndANullFieldTheChoices()
        {
            var choice = Penalties.Resolve(4, -35.0, 1, SoundLevels.Mute, false);
            Assert.AreEqual(4, choice.TimeoutSeconds);
            Assert.AreEqual(-35, choice.LevelDbfs);
            var miss = Penalties.Resolve(4, -35.0, 1, SoundLevels.Mute, true);
            Assert.AreEqual(1, miss.TimeoutSeconds);
            Assert.IsTrue(double.IsNaN(miss.LevelDbfs), "mute is NaN");
            var inherited = Penalties.Resolve(4, "mute", null, null, true);
            Assert.AreEqual(4, inherited.TimeoutSeconds);
            Assert.IsTrue(double.IsNaN(inherited.LevelDbfs));
            Assert.AreEqual(-40, Penalties.Resolve(4, -35.0, null, "-40", true).LevelDbfs);
        }

        [TestMethod]
        public void SoundLevelsReadNumbersOrMute()
        {
            Assert.AreEqual(-45.0, SoundLevels.Parse(-45));
            Assert.AreEqual(-45.0, SoundLevels.Parse("-45"));
            Assert.IsTrue(double.IsNaN(SoundLevels.Parse("mute")));
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => SoundLevels.Parse("off")).Message, "number of dBFS RMS or mute, not off");
            Assert.ThrowsException<InvalidOperationException>(() => SoundLevels.Parse(false));
        }

        [TestMethod]
        public void SoundAzimuthsReadNumbersOrAllAndFindTheNearestSpeaker()
        {
            Assert.AreEqual(-60.0, SoundAzimuths.Parse("-60"));
            Assert.IsTrue(double.IsNaN(SoundAzimuths.Parse("all").Value));
            Assert.IsNull(SoundAzimuths.Parse(true));
            Assert.IsNull(SoundAzimuths.Parse("left"));
            var speakers = new EqualisedSpeakerArray { Speakers = new Dictionary<string, Speaker>
            {
                { "L", new Speaker { Position = new SphericalPosition { Azimuth = -60, Elevation = 0 } } },
                { "R", new Speaker { Position = new SphericalPosition { Azimuth = 60, Elevation = 0 } } },
                { "Up", new Speaker { Position = new SphericalPosition { Azimuth = 0, Elevation = 80 } } },
            } };
            Assert.AreEqual("L", SoundAzimuths.NearestSpeaker(speakers, -50, 0).Key);
            Assert.AreEqual("Up", SoundAzimuths.NearestSpeaker(speakers, 10, 60).Key);
            Assert.ThrowsException<InvalidOperationException>(() => SoundAzimuths.NearestSpeaker(new EqualisedSpeakerArray(), 0, 0));
        }

        [TestMethod]
        public void CurriculumEnumNamesAndTaskBlocks()
        {
            Assert.AreEqual("is empty", CurriculumCheck.Problem(null));
            Assert.IsNull(CurriculumCheck.Problem(new Stage { Id = 1, Code = "S03A", Version = "k7f2", Path = "A", Label = "x", Sha256 = "y" }));
            StringAssert.StartsWith(CurriculumCheck.Problem(new Stage { Id = -1 }), "is malformed (id -1 is below 0; code '' is not like S03A");
            Assert.AreEqual("visual", EnumNames.Name(Kind.Visual));
            CollectionAssert.AreEqual(new[] { "sizeUl must be above 0 and at most 20 ul" }, TaskBlocks.RewardSizeProblems(0, 20));
            CollectionAssert.AreEqual(new[] { "sizeUl (8 ul) x largeMultiplier (3) is 24 ul, more than 20 ul, the most one reward may ask for" }, TaskBlocks.LargeRewardProblems(8, 3, 0.1, 20));
            CollectionAssert.AreEqual(new[] { "largeMultiplier is 1, not above 1" }, TaskBlocks.LargeRewardProblems(2, 1, 0, 20));
            Assert.AreEqual(0, TaskBlocks.LargeRewardProblems(8, 3, 0, 20).Count);
            CollectionAssert.AreEqual(new[] { "laserOnProbability must be in [0, 1]" }, TaskBlocks.LaserOnProbabilityProblems(1.5));
            CollectionAssert.AreEqual(new[] { "visualStimulus.contrasts must each be a finite number, 0 or more, at least one", "auditoryStimulus.levelDbfs must be mute or a number of dBFS RMS from -120 to -1" }, TaskBlocks.StimulusProblems(new List<double>(), 0));
            Assert.AreEqual(0, TaskBlocks.StimulusProblems(new List<double> { 0, 1 }, -40).Count);
            Assert.AreEqual(0, TaskBlocks.StimulusProblems(new List<double> { 1 }, double.NaN).Count);
        }
    }
}
