using OpenCV.Net;

namespace OpenRigs.Video
{
    public class VideoDataFrame
    {
        public VideoDataFrame(IplImage image, long frameID, long timestamp)
            : this(image, new VideoChunkData(frameID, timestamp))
        {
        }

        public VideoDataFrame(IplImage image, VideoChunkData chunkData)
        {
            Image = image;
            ChunkData = chunkData;
        }

        public IplImage Image { get; }

        public VideoChunkData ChunkData { get; }
    }

    public struct VideoChunkData
    {
        public VideoChunkData(long frameID, long timestamp)
            : this(frameID, timestamp, double.NaN, double.NaN)
        {
        }

        public VideoChunkData(long frameID, long timestamp, double exposureTime, double gain)
        {
            FrameID = frameID;
            Timestamp = timestamp;
            ExposureTime = exposureTime;
            Gain = gain;
        }

        public long FrameID { get; }

        public long Timestamp { get; }

        // As the camera reports them for this frame; NaN where not known.
        public double ExposureTime { get; }

        public double Gain { get; }
    }
}
