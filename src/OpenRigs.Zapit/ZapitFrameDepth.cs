using System;
using OpenCV.Net;

namespace OpenRigs.Zapit
{
    /// <summary>
    /// The Zapit camera's frame as 8 bits, whatever the session .pfs's PixelFormat. Bonsai.Pylon gives a Mono12 frame as a
    /// 16-bit image holding the camera's raw 0-4095 counts, unshifted (measured on an acA1920-40um: every
    /// pixel equal to the grab's own Mono12 buffer); recording (FfmpegRecording), the live view's JPEG and the setup PNGs
    /// all take 8 bits. A Mono8 frame passes through untouched, so a rig whose .pfs stays Mono8 sees no change.
    /// <para>Session frames are stretched from 0 to StretchTop (the frame's own 97.3rd percentile, at least MinTop counts)
    /// to 0-255, saturating, so the skull sits near 80 of 255. A dim scene is brought back to normal brightness too (2 %
    /// light shows at the usual mean), so a lighting failure does not show as a dark picture; MinTop stops the
    /// amplification only for a frame below about 0.4 % of full scale (lights off). A grid
    /// run's frames (its exposure and gain set) are scaled to Mono8's range (counts / 16), so the beam keeps the
    /// unsaturated peak the spot detection's 90 %-of-peak threshold and 500 px size limit were tuned on (a beam saturated
    /// at 255 is a wide flat top). Crop cuts the viewed frame to a crop that fits inside it; the recorded frame too when the crop is recorded.</para>
    /// </summary>
    internal static class ZapitFrameDepth
    {
        /// <summary>The 16-bit count that is Mono8's 1: a 12-bit count over 16.</summary>
        public const double Mono12PerMono8 = 16;

        /// <summary>The share of the frame, averaged 8 x 8, at or below the stretch's white: the brightest 2.7 % (the head
        /// bars, ring and glints) go white, so the skull sits near 80 of 255 (an acA1920-40um at 30 ms; the brightest
        /// 8 x 8 average puts it near 24).</summary>
        public const double WhitePercentile = 97.3;

        /// <summary>The least StretchTop, in counts (about 0.4 % of Mono12's 4095): the stretch's gain is at most 255 / 16, so
        /// only a frame darker than that (lights off) stays dark rather than noise amplified up to 255 times; any brighter
        /// frame is stretched to full brightness.</summary>
        public const double MinTop = 16;

        /// <summary>The 16-bit count a session frame's stretch puts at 255: the frame averaged over 8 x 8 pixels, about what
        /// the live view shows, at its WhitePercentile, so no glint or noisy pixel sets it (stretched to
        /// each frame's single brightest pixel, the view brightens and dims frame to frame).</summary>
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

        /// <summary>A grabbed frame as the viewed image (stretched, cropped), the recorded one (stretched, whole) and the
        /// still Zapit's setup saves (whole; Mono8's scale in a grid run), each null for a null frame.</summary>
        public static void Split(IplImage? image, bool grid, Rect crop, out IplImage? shown, out IplImage? still, out string? cropRefusal, out IplImage? recorded)
        {
            shown = still = recorded = image;
            cropRefusal = null;
            if (image == null) return;
            if (image.Depth != IplDepth.U8)
            {
                shown = ToEightBit(image, false, StretchTop(image));
                still = grid ? ToEightBit(image, true, 0) : shown;
                recorded = shown;
            }
            shown = Crop(shown!, crop, out cropRefusal);
        }

        /// <summary>The Zapit camera's frame for a grab (ZapitPylonCapture's, or a test's): <paramref name="image"/> split
        /// (<paramref name="grid"/>: a grid run's exposure is on the camera) and cut to <paramref name="crop"/>'s crop, carrying
        /// the crop's answer when this frame is the first cut to it. The recorded frame is the cropped one when
        /// <paramref name="recordCrop"/> (rig.yml's zapit.camera.recordCrop), else whole.</summary>
        public static ZapitFrame Frame(IplImage? image, bool grid, ZapitCropState crop, long frameId, long timestamp, bool recordCrop = true)
        {
            var cut = crop.Current;
            Split(image, grid, cut, out var shown, out var still, out var refusal, out var recorded);
            return new ZapitFrame { View = shown, Image = recordCrop ? shown : recorded, Still = still, CropReports = crop.Finish(cut, refusal), FrameID = frameId, Timestamp = timestamp };
        }

        /// <summary>Why <paramref name="crop"/> cannot be cut from a frame of <paramref name="frame"/>'s size, or null when it
        /// fits: x and y from 0, width and height above 0, all inside the frame (compared as width > frame - x, so a huge x
        /// cannot overflow the sum). An empty crop (no width or height) is the caller's "no crop" and is not asked.</summary>
        public static string? Refusal(Size frame, Rect crop) =>
            crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0 || crop.Width > frame.Width - crop.X || crop.Height > frame.Height - crop.Y
                ? $"the crop {crop.X},{crop.Y},{crop.Width},{crop.Height} does not fit the camera's {frame.Width} x {frame.Height} frame"
                : null;

        /// <summary><paramref name="image"/> cut to <paramref name="crop"/> (pixels of the frame), a new image, exactly as
        /// asked; <paramref name="image"/> itself for no crop (an empty one), the whole frame as the crop, or a crop that does
        /// not fit (never clamped: <paramref name="refusal"/> says why). The Zapit camera's viewed frame: the window cropped
        /// to the brain; the mp4 and the stills stay whole, so every fit stays in the whole frame's pixels.</summary>
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

    /// <summary>The Zapit camera's crop and what it has answered: set by a Zapit-Main.Crop command, reported only from a frame
    /// cut to it (Finish), so the metric never says Cropped while the stream is whole.</summary>
    internal sealed class ZapitCropState
    {
        readonly object gate = new object();
        Rect crop;
        bool pending;
        string? rejection;
        int number;            // the request number of the crop set
        int rejectionNumber;   // and of the rejected request

        /// <summary>A new crop (an empty one is the whole frame), to be answered by the next frame.</summary>
        public void Set(Rect request, int requestNumber) { lock (gate) { crop = request; number = requestNumber; pending = true; rejection = null; } }

        /// <summary>A request that was no crop: the crop in force stays, and the next frame answers Refused with it, after the
        /// answer to a crop set before it that no frame has answered yet.</summary>
        public void Reject(string reason, int requestNumber) { lock (gate) { rejection = reason; rejectionNumber = requestNumber; } }

        /// <summary>Takes <paramref name="settings"/> when it is a crop request: a crop (an empty one the whole frame) or the
        /// rejection of one. False for any other settings (a grid run's exposure), which it leaves alone.</summary>
        public bool Apply(ZapitCameraSettings settings)
        {
            if (!settings.SetCrop) return false;
            if (settings.RejectReason != null) Reject(settings.RejectReason, settings.CropRequest);
            else Set(settings.CropWidth > 0 && settings.CropHeight > 0 ? new Rect(settings.CropX, settings.CropY, settings.CropWidth, settings.CropHeight) : default, settings.CropRequest);
            return true;
        }

        /// <summary>No crop and nothing to answer (the camera closed).</summary>
        public void Reset() { lock (gate) { crop = default; number = 0; pending = false; rejection = null; } }

        /// <summary>The crop to cut the next frame to.</summary>
        public Rect Current { get { lock (gate) return crop; } }

        /// <summary>Every answer carries the number of the request it answers: the crop the frame was cut to, or the rejected request.
        /// The answer for a frame cut to <paramref name="cut"/> (Current when it was taken), with its
        /// <paramref name="refusal"/> or none: Refused (a crop outside the frame is dropped, once; a request that was no crop
        /// is Rejected), each saying which crop is now in force (Now Full Frame, or Now Cropped x,y,w,h); Cropped or Full
        /// Frame for the first frame cut to a new crop, else null. Null too when the crop changed since: the newer request is answered by its own frame.</summary>
        public ZapitSetupStep[]? Finish(Rect cut, string? refusal)
        {
            lock (gate)
            {
                if (!crop.Equals(cut)) return null;
                var answers = new System.Collections.Generic.List<ZapitSetupStep>();
                if (pending || refusal != null)   // the crop set, answered first
                {
                    pending = false;
                    if (refusal != null)
                    {
                        crop = default;   // never a clamped crop: the whole frame
                        answers.Add(Refused(number, refusal));
                    }
                    else answers.Add(cut.Width <= 0 ? new ZapitSetupStep { Stage = "Full Frame", Detail = $"#{number}" }
                        : new ZapitSetupStep { Stage = "Cropped", Detail = $"#{number} {cut.X},{cut.Y},{cut.Width},{cut.Height}" });
                }
                if (rejection != null)   // then the request that was no crop, with the crop in force after the first
                {
                    answers.Add(Refused(rejectionNumber, rejection));
                    rejection = null;
                }
                return answers.Count == 0 ? null : answers.ToArray();
            }
        }

        ZapitSetupStep Refused(int asked, string why) => new ZapitSetupStep
        {
            Stage = "Refused",
            Detail = $"#{asked} {why}; Now " + (crop.Width <= 0 ? "Full Frame" : $"Cropped {crop.X},{crop.Y},{crop.Width},{crop.Height}"),
        };
    }
}
