using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive.Linq;
using Bonsai;
using Bonsai.Expressions;

namespace OpenRigs.Core
{
    /// <summary>
    /// Projects a collection of calibration points into (x, y) pairs for <see cref="FitCalibrationCurve"/>,
    /// reading two members of each point by name. The default member names match the
    /// <c>CalibrationPoint</c> class a rig's generated schema emits.
    /// </summary>
    [DefaultProperty("XSelector")]
    [Description("Turns a collection of calibration points, such as the Points of a deserialized CalibrationCurve artefact, into (x, y) pairs for FitCalibrationCurve by reading the members named by XSelector and YSelector from each point. Pairs already in Tuple form pass through unchanged.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class SelectCalibrationPoints : SingleArgumentExpressionBuilder
    {
        static readonly Type PairType = typeof(Tuple<double, double>);

        /// <summary>
        /// Gets or sets the member of each point holding the controlled quantity, x.
        /// </summary>
        [Description("The member of each point holding the quantity the rig controls, x. Nested members use dots.")]
        public string XSelector { get; set; } = "X";

        /// <summary>
        /// Gets or sets the member of each point holding the measured quantity, y.
        /// </summary>
        [Description("The member of each point holding the measured quantity, y. Nested members use dots.")]
        public string YSelector { get; set; } = "Y";

        /// <inheritdoc/>
        public override Expression Build(IEnumerable<Expression> arguments)
        {
            var source = arguments.First();
            var collectionType = source.Type.GetGenericArguments()[0];
            var elementType = GetElementType(collectionType);
            if (elementType == null)
            {
                throw new InvalidOperationException(string.Format(
                    "SelectCalibrationPoints needs a collection of points as input, not {0}.", collectionType.Name));
            }

            var point = Expression.Parameter(elementType, "point");
            Expression selector;
            if (elementType == PairType)
            {
                selector = point;
            }
            else
            {
                try
                {
                    var x = Expression.Convert(ExpressionHelper.MemberAccess(point, XSelector), typeof(double));
                    var y = Expression.Convert(ExpressionHelper.MemberAccess(point, YSelector), typeof(double));
                    selector = Expression.New(PairType.GetConstructors().Single(), x, y);
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidOperationException(string.Format(
                        "SelectCalibrationPoints could not read '{0}' and '{1}' from points of type {2}: {3}",
                        XSelector, YSelector, elementType, ex.Message), ex);
                }
            }

            var selectorLambda = Expression.Lambda(selector, point);
            return Expression.Call(
                typeof(SelectCalibrationPoints),
                nameof(Process),
                new[] { collectionType, elementType },
                source,
                selectorLambda);
        }

        static Type? GetElementType(Type collectionType)
        {
            var enumerable = new[] { collectionType }.Concat(collectionType.GetInterfaces())
                .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            return enumerable == null ? null : enumerable.GetGenericArguments()[0];
        }

        static IObservable<IEnumerable<Tuple<double, double>>> Process<TCollection, TElement>(
            IObservable<TCollection> source,
            Func<TElement, Tuple<double, double>> selector)
            where TCollection : IEnumerable<TElement>
        {
            return source.Select(points => (IEnumerable<Tuple<double, double>>)points.Select(selector).ToArray());
        }
    }
}
