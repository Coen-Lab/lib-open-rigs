using System;
using OpenCV.Net;

namespace OpenRigs.Zapit
{
    /// <summary>
    /// The Zapit camera's frame as 8 bits, whatever the session .pfs's PixelFormat. Bonsai.Pylon gives a Mono12 frame as a
    /// 16-bit image holding the camera's raw 0-4095 counts, unshifted (measured on B2's acA1920-40um, 27 Nov 2026: every
    /// pixel equal to the grab's own Mono12 buffer); recording (FfmpegRecording), the live view's JPEG and the setup PNGs
    /// all take 8 bits. A Mono8 frame passes through untouched, so a rig whose .pfs stays Mono8 sees no change.
    /// <para>Session frames are stretched from 0 to StretchTop (the frame's own 97.3rd percentile, at least MinTop counts)
    /// to 0-255, saturating, so the skull sits near 80 of 255 and a near-dark frame is not amplified to noise. A grid
    /// run's frames (its exposure and gain set) are scaled to Mono8's range (counts / 16), so the beam keeps the
    /// unsaturated peak the spot detection's 90 %-of-peak threshold and 500 px size limit were tuned on (a beam saturated
    /// at 255 is a wide flat top). Crop cuts the shown frame to a crop that fits inside it.</para>
    /// </summary>
    internal static class ZapitFrameDepth
    {
        /// <summary>The 16-bit count that is Mono8's 1: a 12-bit count over 16.</summary>
        public const double Mono12PerMono8 = 16;

        /// <summary>The share of the frame, averaged 8 x 8, at or below the stretch's white: the brightest 2.7 % (the head
        /// bars, ring and glints) go white, so the skull sits near 80 of 255 (B2 at 30 ms, 27 Nov 2026; the brightest
        /// 8 x 8 average put it near 24).</summary>
        public const double WhitePercentile = 97.3;

        /// <summary>The least StretchTop, in counts: a near-dark frame (lights off) keeps its low counts, not noise amplified up to 255 times.</summary>
        public const double MinTop = 16;

        /// <summary>The 16-bit count a session frame's stretch puts at 255: the frame averaged over 8 x 8 pixels, about what
        /// the live view shows, at its WhitePercentile, so no glint or noisy pixel sets it (27 Nov 2026: stretched to
        /// each frame's single brightest pixel, B2's view brightened and dimmed frame to frame).</summary>
        public static double StretchTop(IplImage image)
        {
            if (image.Depth != IplDepth.U16 || image.Channels != 1) return 0;   // ToEightBit refuses it, naming PixelFormat
            using var small = new IplImage(new Size(Math.Max(1, image.Width / 8), Math.Max(1, image.Height / 8)), IplDepth.U16, 1);
            CV.Resize(image, small, SubPixelInterpolation.Area);
            var raw = new short[small.Width * small.Height];
            for (int y = 0; y < small.Height; y++)   // a row at a time: the indexer per pixel is too slow at 30 frames/s
                System.Runtime.InteropServices.Marshal.Copy(small.ImageData + y * small.WidthStep, raw, y * small.Width, small.Width);
            var values = Array.ConvertAll(raw, v => (ushort)v);
            Array.Sort(values);
            return Math.Max(MinTop, values[(int)Math.Round((values.Length - 1) * WhitePercentile / 100.0)]);
        }

        /// <summary><paramref name="image"/> itself when it is already 8-bit; else (16-bit, one channel) a new 8-bit image:
        /// with <paramref name="mono8Scale"/> its counts over 16 (rounded, as Mono8); else 0 to <paramref name="top"/>
        /// stretched to 0-255 (saturating), or its raw counts when <paramref name="top"/> is 0 or less.</summary>
        public static IplImage ToEightBit(IplImage image, bool mono8Scale, double top)
        {
            if (image == null || image.Depth == IplDepth.U8) return image!;
            if (image.Depth != IplDepth.U16 || image.Channels != 1)
                throw new InvalidOperationException(
                    $"The Zapit camera gave a {image.Depth} image with {image.Channels} channels; it must be Mono8 or Mono12 (set PixelFormat in its .pfs).");
            var eight = new IplImage(image.Size, IplDepth.U8, 1);
            CV.ConvertScale(image, eight, mono8Scale ? 1.0 / Mono12PerMono8 : top > 0 ? 255.0 / top : 1.0, 0);   // saturates at 255
            return eight;
        }

        /// <summary>Why <paramref name="crop"/> cannot be cut from a frame of <paramref name="frame"/>'s size, or null when it
        /// fits: x and y from 0, width and height above 0, all inside the frame. An empty crop (no width or height) is the
        /// caller's "no crop" and is not asked.</summary>
        public static string? Refusal(Size frame, Rect crop) =>
            crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0 || crop.X + crop.Width > frame.Width || crop.Y + crop.Height > frame.Height
                ? $"the crop {crop.X},{crop.Y},{crop.Width},{crop.Height} does not fit the camera's {frame.Width} x {frame.Height} frame"
                : null;

        /// <summary><paramref name="image"/> cut to <paramref name="crop"/> (pixels of the frame), a new image, exactly as
        /// asked; <paramref name="image"/> itself for no crop (an empty one), the whole frame as the crop, or a crop that does
        /// not fit (never clamped: <paramref name="refusal"/> says why). The Zapit camera's shown frame: the window and the
        /// mp4 cropped to the brain; the stills stay whole, so every fit stays in the whole frame's pixels.</summary>
        public static IplImage Crop(IplImage image, Rect crop, out string? refusal)
        {
            refusal = null;
            if (image == null || (crop.Width == 0 && crop.Height == 0 && crop.X == 0 && crop.Y == 0)) return image!;
            refusal = Refusal(image.Size, crop);
            if (refusal != null || (crop.X == 0 && crop.Y == 0 && crop.Width == image.Width && crop.Height == image.Height)) return image;
            var cut = new IplImage(new Size(crop.Width, crop.Height), image.Depth, image.Channels);
            CV.Copy(image.GetSubRect(crop), cut);
            return cut;
        }
    }
}
