using Bonsai;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetMQ;
using Newtonsoft.Json.Linq;
using OpenCV.Net;
using System;
using System.Reactive.Linq;

namespace OpenRigs.Devices.Tests
{
    /// <summary>
    /// A camera's frame of another shape than its tile is cut to the tile's shape about its centre, then shrunk, never
    /// stretched; the header says where it was cut, so a viewer can map a click back to the frame. Run through
    /// StreamDevices' real Camera template, on ports of their own, with a stand-in camera.
    /// </summary>
    [TestClass]
    public class LiveViewTrimTests
    {
        [TestMethod]
        public void AWiderFrameIsCutAboutItsCentreToTheTileNotStretched()
        {
            NetMQMessage message = null;
            LiveViewDeviceEndTests.WithRig(root => message = LiveViewDeviceEndTests.AssertSeen(root, true, "Camera-Balance.video", w =>
                LiveViewDeviceEndTests.Publish(w, "Camera-Balance.Data", new WideStandInCamera())));

            var header = JObject.Parse(message[1].ConvertToString());
            Assert.AreEqual("jpeg", (string)header["enc"], "the fields every message has are kept: " + header);
            Assert.AreEqual("Camera-Balance.video", (string)header["stream"], header.ToString());
            Assert.AreEqual("[1936,1216]", header["sourceSize"].ToString(Newtonsoft.Json.Formatting.None), header.ToString());
            Assert.AreEqual("[157,0,1621,1216]", header["trim"].ToString(Newtonsoft.Json.Formatting.None), header.ToString());

            using (var image = CV.DecodeImage(Mat.FromArray(message[2].ToByteArray()), LoadImageFlags.Grayscale))
            {
                Assert.AreEqual(320, image.Width);
                Assert.AreEqual(240, image.Height);
                // The black square, 400 x 400 at the frame's centre, is about 79 x 79 at the tile's centre: 66 wide
                // had it been stretched to the tile.
                int left = int.MaxValue, right = -1, top = int.MaxValue, bottom = -1;
                for (var y = 0; y < image.Height; y++)
                {
                    for (var x = 0; x < image.Width; x++)
                    {
                        if (image.GetReal(y, x) > 64) continue;
                        left = Math.Min(left, x); right = Math.Max(right, x);
                        top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    }
                }
                int width = right - left + 1, height = bottom - top + 1;
                Assert.IsTrue(Math.Abs(width - 79) <= 2 && Math.Abs(height - 79) <= 2, "the square is " + width + " x " + height + ", not 79 x 79");
                Assert.IsTrue(Math.Abs((left + right) / 2.0 - 159.5) <= 1.5 && Math.Abs((top + bottom) / 2.0 - 119.5) <= 1.5,
                    "the square is centred at " + (left + right) / 2.0 + ", " + (top + bottom) / 2.0 + ", not the tile's centre");
                // The white strips at the frame's sides, outside the part kept, are not shown.
                for (var y = 0; y < image.Height; y += 8)
                {
                    Assert.IsTrue(image.GetReal(y, 0) < 192 && image.GetReal(y, image.Width - 1) < 192, "a side strip shows at row " + y);
                }
            }
        }

        [TestMethod]
        public void AFrameOfTheTilesShapeIsShownWhole()
        {
            NetMQMessage message = null;
            LiveViewDeviceEndTests.WithRig(root => message = LiveViewDeviceEndTests.AssertSeen(root, true, "Camera-Balance.video", w =>
                LiveViewDeviceEndTests.Publish(w, "Camera-Balance.Data", new StandInCamera())));
            var header = JObject.Parse(message[1].ConvertToString());
            Assert.AreEqual("[0,0,64,48]", header["trim"].ToString(Newtonsoft.Json.Formatting.None), header.ToString());
        }

        // The Zapit camera's whole frame (Fit): every pixel kept on a black canvas of the tile's shape, the header's trim
        // with negative x or y by the black added, so a viewer maps a click as for a cut frame.
        [TestMethod]
        public void AFittedFrameIsKeptWholeOnABlackCanvasOfTheTilesShape()
        {
            using (var frame = new IplImage(new Size(480, 640), IplDepth.U8, 1))
            {
                frame.Set(Scalar.All(200));
                var fitted = new OpenRigs.LiveView.TrimToTile { Fit = true }.Process(Observable.Return(frame)).Wait();
                Assert.AreEqual("-186,0,853,640", string.Join(",", fitted.Trim.X, fitted.Trim.Y, fitted.Trim.Width, fitted.Trim.Height));
                Assert.AreEqual(853, fitted.Image.Width);
                Assert.AreEqual(640, fitted.Image.Height);
                Assert.AreEqual(0, fitted.Image.GetReal(320, 0), "black at the left");
                Assert.AreEqual(200, fitted.Image.GetReal(320, 426), "the frame in the middle");
                Assert.AreEqual(0, fitted.Image.GetReal(320, 852), "black at the right");
            }
        }
    }

    /// <summary>
    /// A camera of Camera-Zapit's whole frame, 1936 x 1216, every 50 ms: grey, a 400 x 400 black square at its centre,
    /// and white strips 150 wide at its sides, inside the 157 a 4:3 tile leaves out.
    /// </summary>
    public class WideStandInCamera : Source<IplImage>
    {
        /// <summary>The frames.</summary>
        /// <returns>The same frame every 50 ms.</returns>
        public override IObservable<IplImage> Generate()
        {
            return Observable.Interval(TimeSpan.FromMilliseconds(50)).Select(_ =>
            {
                var frame = new IplImage(new Size(1936, 1216), IplDepth.U8, 3);
                frame.Set(Scalar.All(128));
                CV.Rectangle(frame, new Rect(0, 0, 150, 1216), Scalar.All(255), -1, LineFlags.Connected8, 0);
                CV.Rectangle(frame, new Rect(1786, 0, 150, 1216), Scalar.All(255), -1, LineFlags.Connected8, 0);
                CV.Rectangle(frame, new Rect(768, 408, 400, 400), Scalar.All(0), -1, LineFlags.Connected8, 0);
                return frame;
            });
        }
    }
}
