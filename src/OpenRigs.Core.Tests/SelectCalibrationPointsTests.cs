using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenRigs.Core.Tests
{
    [TestClass]
    public class SelectCalibrationPointsTests
    {
        // Shapes a rig's sgen-generated CalibrationPoint and a wrapper around it.
        public class Point
        {
            public double X { get; set; }
            public double Y { get; set; }
        }

        public class IntPoint
        {
            public int Width { get; set; }
            public float Volume { get; set; }
        }

        public class Wrapper
        {
            public Point Value { get; set; }
        }

        static IEnumerable<Tuple<double, double>> Run<TCollection>(SelectCalibrationPoints builder, TCollection points)
        {
            var source = Expression.Constant(Observable.Return(points), typeof(IObservable<TCollection>));
            var built = builder.Build(new[] { source });
            var sequence = Expression.Lambda<Func<IObservable<IEnumerable<Tuple<double, double>>>>>(built).Compile()();
            return sequence.Wait();
        }

        [TestMethod]
        public void ProjectsXAndYByDefault()
        {
            var points = new List<Point> { new Point { X = 20, Y = 1.1 }, new Point { X = 40, Y = 2.1 } };
            var pairs = Run(new SelectCalibrationPoints(), points).ToArray();
            Assert.AreEqual(2, pairs.Length);
            Assert.AreEqual(Tuple.Create(20.0, 1.1), pairs[0]);
            Assert.AreEqual(Tuple.Create(40.0, 2.1), pairs[1]);
        }

        [TestMethod]
        public void ConvertsNumericMembersToDouble()
        {
            var points = new[] { new IntPoint { Width = 20, Volume = 1.5f } };
            var builder = new SelectCalibrationPoints { XSelector = "Width", YSelector = "Volume" };
            var pair = Run(builder, points).Single();
            Assert.AreEqual(20.0, pair.Item1);
            Assert.AreEqual(1.5, pair.Item2);
        }

        [TestMethod]
        public void FollowsNestedSelectors()
        {
            var points = new[] { new Wrapper { Value = new Point { X = 1, Y = 2 } } };
            var builder = new SelectCalibrationPoints { XSelector = "Value.X", YSelector = "Value.Y" };
            var pair = Run(builder, points).Single();
            Assert.AreEqual(Tuple.Create(1.0, 2.0), pair);
        }

        [TestMethod]
        public void PassesTuplePairsThrough()
        {
            var points = new[] { Tuple.Create(20.0, 1.1), Tuple.Create(40.0, 2.1) };
            var pairs = Run(new SelectCalibrationPoints(), points).ToArray();
            CollectionAssert.AreEqual(points, pairs);
        }

        [TestMethod]
        public void RejectsNonCollectionInput()
        {
            var source = Expression.Constant(Observable.Return(42), typeof(IObservable<int>));
            Assert.ThrowsException<InvalidOperationException>(() => new SelectCalibrationPoints().Build(new[] { source }));
        }

        [TestMethod]
        public void FeedsFitCalibrationCurve()
        {
            var points = new List<Point>
            {
                new Point { X = 20, Y = 1.1 }, new Point { X = 40, Y = 2.1 }, new Point { X = 60, Y = 3.1 }
            };
            var pairs = Run(new SelectCalibrationPoints(), points);
            var curve = new FitCalibrationCurve().Process(Observable.Return(pairs)).Wait();
            Assert.AreEqual(0.05, curve.Slope, 1e-12);
        }

        [TestMethod]
        public void ProjectsTupleMembersFromFloatArray()
        {
            var points = new[] { Tuple.Create(20f, 1.1f), Tuple.Create(40f, 2.1f) };
            var builder = new SelectCalibrationPoints { XSelector = "Item1", YSelector = "Item2" };
            var pairs = Run(builder, points).ToArray();
            Assert.AreEqual(20.0, pairs[0].Item1);
            Assert.AreEqual(2.1f, pairs[1].Item2, 1e-6);
        }
    }
}
