using System;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Bonsai.Expressions;

namespace OpenRigs.Shared
{
    /// <summary>
    /// What a node built as a group with settings of its own needs (StreamDevices,
    /// SessionManifest): the subjects the workflow publishes before it, and its settings kept as
    /// a node in its inner workflow.
    /// </summary>
    internal static class GroupNode
    {
        static readonly PropertyInfo BuildContextProperty = typeof(WorkflowExpressionBuilder).GetProperty(
            "BuildContext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly MethodInfo GetVariableMethod = typeof(ExpressionBuilder).Assembly
            .GetType("Bonsai.Expressions.IBuildContext").GetMethod("GetVariable");

        /// <summary>
        /// The subject the enclosing workflow publishes under this name, as the expression a
        /// SubscribeSubject would use, or null if it publishes none. Bonsai has no public lookup:
        /// this asks its internal build context (written against Bonsai.Core 2.9.0), which only
        /// knows subjects built before the node, so such a node goes last.
        /// </summary>
        public static Expression Subject(WorkflowExpressionBuilder node, string name)
        {
            var context = BuildContextProperty.GetValue(node, null);
            if (context == null) return null;
            try
            {
                return (Expression)GetVariableMethod.Invoke(context, new object[] { name });
            }
            catch (TargetInvocationException)
            {
                return null;
            }
        }

        /// <summary>The element type of the subject published under this name, or null if there is none.</summary>
        public static Type PublishedType(WorkflowExpressionBuilder node, string name)
        {
            var subject = Subject(node, name);
            return subject != null ? ObservedType(subject.Type) : null;
        }

        /// <summary>The element type of an observable type; object if it is not one.</summary>
        public static Type ObservedType(Type type)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IObservable<>))
                return type.GetGenericArguments()[0];
            return type.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IObservable<>))
                .Select(i => i.GetGenericArguments()[0])
                .FirstOrDefault() ?? typeof(object);
        }

        /// <summary>
        /// The settings node in a group's inner workflow, added on first use. Bonsai copies a group
        /// whenever the editor opens a workflow, the player shows a layout, or a workflow is saved,
        /// and the copy keeps only the group's name, description and inner workflow, so settings
        /// kept anywhere else would be lost.
        /// </summary>
        public static T Settings<T>(ExpressionBuilderGraph workflow) where T : ExpressionBuilder, new()
        {
            foreach (var node in workflow)
            {
                var settings = Unwrap(node.Value) as T;
                if (settings != null) return settings;
            }
            var added = new T();
            workflow.Add(added);
            return added;
        }

        /// <summary>A node as built, from under the editor's InspectBuilder and any DisableBuilder.</summary>
        public static ExpressionBuilder Unwrap(ExpressionBuilder builder)
        {
            builder = ExpressionBuilder.Unwrap(builder);
            var disabled = builder as DisableBuilder;
            return disabled != null ? ExpressionBuilder.Unwrap(disabled.Builder) : builder;
        }
    }

    /// <summary>
    /// Shows a group node's own settings in the Properties panel and to an ExternalizedMapping. A
    /// group's default descriptor shows only the externalized properties of its inner workflow,
    /// which for these nodes has none.
    /// </summary>
    internal abstract class ShownPropertiesProvider : TypeDescriptionProvider
    {
        readonly string[] shown;

        protected ShownPropertiesProvider(params string[] shown)
            : base(TypeDescriptor.GetProvider(typeof(object)))
        {
            this.shown = shown;
        }

        public override ICustomTypeDescriptor GetTypeDescriptor(Type objectType, object instance)
        {
            return new Descriptor(base.GetTypeDescriptor(objectType, instance), shown);
        }

        class Descriptor : CustomTypeDescriptor
        {
            readonly string[] shown;

            public Descriptor(ICustomTypeDescriptor parent, string[] shown) : base(parent)
            {
                this.shown = shown;
            }

            public override PropertyDescriptorCollection GetProperties()
            {
                return Filter(base.GetProperties());
            }

            public override PropertyDescriptorCollection GetProperties(Attribute[] attributes)
            {
                return Filter(base.GetProperties(attributes));
            }

            PropertyDescriptorCollection Filter(PropertyDescriptorCollection properties)
            {
                return new PropertyDescriptorCollection(properties.Cast<PropertyDescriptor>()
                    .Where(p => p.Name == "Name" || p.Name == "Description" || shown.Contains(p.Name)).ToArray());
            }
        }
    }
}
