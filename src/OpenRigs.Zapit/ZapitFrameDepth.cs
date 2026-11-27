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

        /// <summary><paramref name="image"/> itself when it is already 8-bit; else (16-bit, one channel) a new 8-bit image of
        /// its counts saturating at 255, or with <paramref name="mono8Scale"/> its counts over 16 (rounded, as Mono8).</summary>
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
