using System;
using System.Linq.Expressions;
using System.Reflection;
using OpenCV.Net;

namespace OpenRigs.Video
{
    /// <summary>
    /// Reads a camera frame of any type as a <see cref="VideoDataFrame"/>, by member name: its
    /// <c>Image</c> (an IplImage) and its <c>ChunkData</c>'s <c>FrameID</c> and <c>Timestamp</c>, and
    /// <c>ExposureTime</c> and <c>Gain</c> where it has them (NaN where not). So the writer takes
    /// what LogVideo takes: OpenRigs.Devices' TriggeredSpinnaker include gives an anonymous type of
    /// those members, not a VideoDataFrame.
    /// </summary>
    internal static class FrameReader<TFrame>
    {
        static readonly Lazy<Func<TFrame, VideoDataFrame?>> read = new Lazy<Func<TFrame, VideoDataFrame?>>(Build);

        /// <summary>The frame as a VideoDataFrame, null for a null frame; throws naming a member the type lacks.</summary>
        public static Func<TFrame, VideoDataFrame?> Read => read.Value;

        static Func<TFrame, VideoDataFrame?> Build()
        {
            if (typeof(TFrame) == typeof(VideoDataFrame)) return frame => (VideoDataFrame?)(object?)frame;
            var frame = Expression.Parameter(typeof(TFrame), "frame");
            var image = Member(frame, "Image", typeof(IplImage));
            var chunk = Member(frame, "ChunkData", null);
            var chunkData = Expression.New(
                typeof(VideoChunkData).GetConstructor(new[] { typeof(long), typeof(long), typeof(double), typeof(double) })!,
                Member(chunk, "FrameID", typeof(long)),
                Member(chunk, "Timestamp", typeof(long)),
                OptionalMember(chunk, "ExposureTime"),
                OptionalMember(chunk, "Gain"));
            Expression body = Expression.New(
                typeof(VideoDataFrame).GetConstructor(new[] { typeof(IplImage), typeof(VideoChunkData) })!, image, chunkData);
            if (!typeof(TFrame).IsValueType)
            {
                body = Expression.Condition(Expression.Equal(frame, Expression.Constant(null, typeof(TFrame))),
                    Expression.Constant(null, typeof(VideoDataFrame)), body);
            }
            return Expression.Lambda<Func<TFrame, VideoDataFrame?>>(body, frame).Compile();
        }

        // A public property or field, converted to a type when one is given; throws naming it when missing.
        static Expression Member(Expression owner, string name, Type? type)
        {
            var member = Find(owner.Type, name) ?? throw new InvalidOperationException(
                "FfmpegVideoWriter takes camera frames with Image and ChunkData (FrameID, Timestamp), but " +
                Describe(owner.Type) + " has no " + name + ".");
            Expression value = Expression.MakeMemberAccess(owner, member);
            if (type == null || value.Type == type) return value;
            try { return Expression.Convert(value, type); }
            catch (InvalidOperationException)
            {
                throw new InvalidOperationException("FfmpegVideoWriter needs " + name + " as " + type.Name + ", but " +
                    Describe(owner.Type) + "'s is " + value.Type.Name + ".");
            }
        }

        static Expression OptionalMember(Expression owner, string name)
        {
            return Find(owner.Type, name) == null ? Expression.Constant(double.NaN) : Member(owner, name, typeof(double));
        }

        static MemberInfo? Find(Type type, string name)
        {
            return (MemberInfo?)type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) ??
                   type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        }

        static string Describe(Type type) => type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false) || type.Name.Contains("<")
            ? "the frame type (" + type.Name + ")" : type.FullName ?? type.Name;
    }
}
