using Bonsai;
using Bonsai.Expressions;
using Bonsai.Shaders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace OpenRigs.Devices.Tests
{
    /// <summary>
    /// A shader window loads on its own render thread, which hands the window to each subscriber of
    /// Bonsai.Shaders' window source while holding the source's replay lock, and BonVision's operators
    /// subscribe the source again from there, which takes its connection lock. A subscription to the
    /// source from the workflow's own subscription at that moment takes the connection lock first and
    /// then waits for the replay lock: neither thread moves, the workflow never finishes starting, and
    /// no error can stop it (a rig, 16 Nov 2026: two launches in ten, Bonsai left in Starting until
    /// killed). ProjectorWindow, the window of ProjectorDisplay and RawProjectorDisplay, is therefore created only once the workflow has subscribed.
    /// </summary>
    [TestClass]
    public class ProjectorWindowTests
    {
        static readonly XNamespace Bonsai = "https://bonsai-rx.org/2018/workflow";
        static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
        static readonly XNamespace Shaders = "clr-namespace:Bonsai.Shaders;assembly=Bonsai.Shaders";

        // ProjectorWindow as it creates the window, small and windowed, then a node that subscribes the
        // window source while the window loads, as the stimuli a task draws do.
        [TestMethod]
        public void TheWorkflowFinishesSubscribing_WhileItsWindowLoads()
        {
            WindowLoadOverlap.Loaded.Reset();
            WorkflowBuilder builder;
            using (var reader = XmlReader.Create(new StringReader(TestWorkflow().ToString())))
            {
                builder = (WorkflowBuilder)WorkflowBuilder.Serializer.Deserialize(reader);
            }

            IDisposable subscription = null;
            Exception error = null;
            var subscribe = new Thread(() =>
            {
                try { subscription = builder.Workflow.BuildObservable().Subscribe(_ => { }, e => error = e); }
                catch (Exception e) { error = e; }
            }) { IsBackground = true };
            subscribe.Start();
            var subscribed = subscribe.Join(TimeSpan.FromSeconds(20));
            var loaded = subscribed && WindowLoadOverlap.Loaded.Wait(TimeSpan.FromSeconds(20));
            if (subscription != null) subscription.Dispose();

            Assert.IsTrue(subscribed, "The workflow's subscription and the window's load deadlocked.");
            if (!loaded) Assert.Inconclusive("No shader window loaded here, so nothing was tested: " + error);
            Assert.IsNull(error, "The workflow stopped: " + error);
        }

        static XElement TestWorkflow()
        {
            var assembly = typeof(OpenRigs.Vision.DisposeOnRenderThread).Assembly;
            XDocument display;
            using (var stream = assembly.GetManifestResourceStream("OpenRigs.Vision.ProjectorWindow.bonsai"))
            {
                Assert.IsNotNull(stream, "ProjectorWindow.bonsai is not embedded in OpenRigs.Vision.");
                display = XDocument.Load(stream);
            }

            // The window and every node from it up to BonVision's resources, as ProjectorWindow has them.
            var window = display.Descendants(Bonsai + "Workflow").Single(w => w.Element(Bonsai + "Nodes").Elements()
                .Any(n => IsCreateWindow(n)));
            var nodes = window.Element(Bonsai + "Nodes").Elements(Bonsai + "Expression").ToList();
            var edges = window.Element(Bonsai + "Edges").Elements(Bonsai + "Edge").ToList();
            var chain = new[] { nodes.FindIndex(IsCreateWindow) }.ToList();
            while (true)
            {
                var next = edges.Where(e => (int)e.Attribute("From") == chain.Last()).Select(e => (int)e.Attribute("To")).ToList();
                Assert.AreEqual(1, next.Count, "a single node after node " + chain.Last());
                if ((string)nodes[next[0]].Attribute(Xsi + "type") == "IncludeWorkflow") break;
                chain.Add(next[0]);
            }

            var copies = chain.Select(i => new XElement(nodes[i])).ToList();
            var settings = copies[0].Element(Bonsai + "Combinator");
            settings.Element(Shaders + "WindowState").Value = "Normal";
            settings.Element(Shaders + "Width").Value = "64";
            settings.Element(Shaders + "Height").Value = "64";

            var probe = new XElement(Bonsai + "Expression", new XAttribute(Xsi + "type", "Combinator"),
                new XElement(Bonsai + "Combinator", new XAttribute(Xsi + "type", "t:" + nameof(WindowLoadOverlap))));
            return new XElement(Bonsai + "WorkflowBuilder",
                new XAttribute("Version", "2.9.0"),
                display.Root.Attributes().Where(a => a.IsNamespaceDeclaration),
                new XAttribute(XNamespace.Xmlns + "t", "clr-namespace:OpenRigs.Devices.Tests;assembly=OpenRigs.Devices.Tests"),
                new XElement(Bonsai + "Workflow",
                    new XElement(Bonsai + "Nodes", copies, probe),
                    new XElement(Bonsai + "Edges", Enumerable.Range(0, copies.Count - 1).Select(i => new XElement(Bonsai + "Edge",
                        new XAttribute("From", i), new XAttribute("To", i + 1), new XAttribute("Label", "Source1"))))));
        }

        static bool IsCreateWindow(XElement node)
        {
            var combinator = node.Element(Bonsai + "Combinator");
            return combinator != null && (string)combinator.Attribute(Xsi + "type") is string type
                && type.EndsWith(":CreateWindow") && combinator.Name.Namespace == Bonsai;
        }
    }

    /// <summary>
    /// Subscribes Bonsai.Shaders' window source when subscribed and, once the window is loading, again,
    /// from the workflow's thread; on the render thread, holding the source's replay lock, it waits half a
    /// second and subscribes the source again, as BonVision's operators do.
    /// </summary>
    [Combinator(MethodName = nameof(Generate))]
    [WorkflowElementCategory(ElementCategory.Source)]
    public class WindowLoadOverlap
    {
        internal static readonly ManualResetEventSlim Loaded = new ManualResetEventSlim();

        public IObservable<Unit> Generate()
        {
            return Observable.Create<Unit>(observer =>
            {
                var loading = new ManualResetEventSlim();
                var subscriptions = new CompositeDisposable();
                subscriptions.Add(ShaderManager.WindowSource.Subscribe(window =>
                {
                    loading.Set();
                    Thread.Sleep(500);
                    subscriptions.Add(ShaderManager.WindowSource.Subscribe(_ => { }));
                    Loaded.Set();
                }));
                loading.Wait(TimeSpan.FromSeconds(3));
                subscriptions.Add(ShaderManager.WindowSource.Subscribe(_ => { }));
                return subscriptions;
            });
        }
    }
}
