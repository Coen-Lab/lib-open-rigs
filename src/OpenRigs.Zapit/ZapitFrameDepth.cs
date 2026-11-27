using System;
using OpenCV.Net;

namespace OpenRigs.Zapit
{
    /// <summary>
    /// The Zapit camera's frame as 8 bits, whatever the session .pfs's PixelFormat. Bonsai.Pylon gives a Mono12 frame as a
    /// 16-bit image holding the camera's raw 0-4095 counts, unshifted (measured on B2's acA1920-40um, 27 Nov 2026: every
    /// pixel equal to the grab's own Mono12 buffer); recording (FfmpegRecording), the live view's JPEG and the setup PNGs
    /// all take 8 bits. A Mono8 frame passes through untouched, so a rig whose .pfs stays Mono8 sees no change.
    /// <para>Session frames keep the raw counts, saturating at 255: B2's dim brain view at 3 ms peaks near 46 counts, so
    /// every dark level MATLAB Zapit's 12-bit picture had is kept (Mono8 gives that scene 3-12 levels), and the app's
    /// min-to-max stretch gives MATLAB's mat2gray picture. A grid run's frames (its exposure and gain set) are scaled to
    /// Mono8's range (counts / 16), so the beam keeps the unsaturated peak the spot detection's 90 %-of-peak threshold and
    /// 500 px size limit were tuned on in Mono8 (a beam saturated at 255 is a wide flat top).</para>
    /// </summary>
    internal static class ZapitFrameDepth
    {
        /// <summary>The 16-bit count that is Mono8's 1: a 12-bit count over 16.</summary>
        public const double Mono12PerMono8 = 16;

        /// <summary>The share of the frame, averaged 8 x 8, at or below the stretch's white: the brightest 2.7 % (the head
        /// bars, ring and glints) go white, so the skull sits near 80 of 255 (Pip, 27 Nov 2026, B2 at 30 ms; the brightest
        /// 8 x 8 average put it near 24).</summary>
        public const double WhitePercentile = 97.3;

        /// <summary>The 16-bit count a session frame's stretch puts at 255: the frame averaged over 8 x 8 pixels, about what
        /// the live view shows, at its WhitePercentile, so no glint or noisy pixel sets it (Pip, 27 Nov 2026: stretched to
        /// each frame's single brightest pixel, B2's view brightened and dimmed frame to frame).</summary>
        public static double StretchTop(IplImage image)
        {
            var small = new IplImage(new Size(Math.Max(1, image.Width / 8), Math.Max(1, image.Height / 8)), IplDepth.F32, 1);
            var wide = new IplImage(image.Size, IplDepth.F32, 1);
            CV.Convert(image, wide);
            CV.Resize(wide, small, SubPixelInterpolation.Area);
            var values = new float[small.Width * small.Height];
            for (int y = 0; y < small.Height; y++)   // a row at a time: the indexer per pixel is too slow at 30 frames/s
                System.Runtime.InteropServices.Marshal.Copy(small.ImageData + y * small.WidthStep, values, y * small.Width, small.Width);
            Array.Sort(values);
            return values[(int)Math.Round((values.Length - 1) * WhitePercentile / 100.0)];
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

        /// <summary><paramref name="image"/> cut to <paramref name="crop"/> (pixels of the frame), a new image: the crop kept
        /// inside the frame and its size even (an mp4's frame must be), else <paramref name="image"/> itself for no crop
        /// (an empty one) or one that leaves nothing. The Zapit camera's shown frame (Pip, 27 Nov 2026: the window and the
        /// mp4 cropped to the brain; the stills stay whole, so every fit stays in the whole frame's pixels).</summary>
        public static IplImage Crop(IplImage image, Rect crop)
        {
            if (image == null || crop.Width <= 0 || crop.Height <= 0) return image!;
            var x = Math.Max(0, Math.Min(crop.X, image.Width - 2));
            var y = Math.Max(0, Math.Min(crop.Y, image.Height - 2));
            var w = Math.Min(crop.Width, image.Width - x) & ~1;
            var h = Math.Min(crop.Height, image.Height - y) & ~1;
            if (w < 2 || h < 2 || (x == 0 && y == 0 && w == image.Width && h == image.Height)) return image;
            var cut = new IplImage(new Size(w, h), image.Depth, image.Channels);
            CV.Copy(image.GetSubRect(new Rect(x, y, w, h)), cut);
            return cut;
        }

        /// <summary>As ToEightBit with a top, a session frame stretched to its own brightest pixel (tests).</summary>
        public static IplImage ToEightBit(IplImage image, bool mono8Scale)
        {
            if (image == null || image.Depth == IplDepth.U8) return image!;
            if (image.Depth != IplDepth.U16 || image.Channels != 1)
                throw new InvalidOperationException(
                    $"The Zapit camera gave a {image.Depth} image with {image.Channels} channels; it must be Mono8 or Mono12 (set PixelFormat in its .pfs).");
            var eight = new IplImage(image.Size, IplDepth.U8, 1);
            if (mono8Scale)
            {
                CV.ConvertScale(image, eight, 1.0 / Mono12PerMono8, 0);   // saturates at 255
                return eight;
            }
            // Session frames: stretched darkest to 0, brightest to 255, as MATLAB Zapit's mat2gray, before anything shrinks
            // or encodes them (Pip, 27 Nov 2026: sent as raw counts, B2's dim view had ~23 grey levels at the live view's
            // 320 x 240, flat patches however the window stretched them). A flat frame is its raw counts.
            double min, max;
            Point minAt, maxAt;
            CV.MinMaxLoc(image, out min, out max, out minAt, out maxAt);
            if (max > min) CV.ConvertScale(image, eight, 255.0 / (max - min), -min * 255.0 / (max - min));
            else CV.ConvertScale(image, eight, 1.0, 0);
            return eight;
        }
    }
}
