/*
UCLAV4.cs

Description:
  This class provides a source for capturing data from the UCLA Miniscope V4. It captures frames, BNO055 quaternion values,
  and frame numbers, and provides configuration controls for LED brightness, focus, gain, and frame rate.

Author:
  Clément Bourguignon
  Brandon Lab @ McGill University
  2026

Notes:
  - OpenCV.NET takes a very long time to retrieve the IMU data. This node uses both OpenCvSharp for faster access to the camera
    registers and OpenCV.NET for creating legacy IplImages to use in Bonsai
  - All relevant data besides the frame come from the camera registers:
    - Saturation   -> Quaternion W and start/stop acquisition
    - Hue          -> Quaternion X
    - Gain         -> Quaternion Y
    - Brightness   -> Quaternion Z
    - Gamma        -> Inverted state of Trigger Input (3.3V -> Gamma = 0, 0V -> Gamma != 0)
    - Contrast     -> DAQ Frame number
  - No matter how I try tweaking process priority, only setting the power plan to "High Performance" gets a stable framerate.
  - I didn't add a buffer as in the miniscope software, that would complicate things and most recent enough computers don't need one,
    but that's something that can be considered.

Dependencies:
  - OpenCV.Net
  - OpenCvSharp

MIT License
Copyright (c) 2026 Clément Bourguignon

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Reactive.Disposables;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using System.Diagnostics;
using OpenCvSharp;
using OpenCV.Net;
using Bonsai;
using UCLAMiniscope.Helpers;

namespace UCLAMiniscope
{
    /// <summary>
    /// Produces a data sequence from a UCLA Miniscope V4, capturing 608×608 grayscale frames with BNO055 IMU quaternion data.
    /// Provides real-time controls for LED brightness, focus, gain, and frame rate.
    /// </summary>
    [Description("Produces a data sequence from a UCLA Miniscope V4.")]
    public class UCLAV4 : Source<FrameIMUV4>
    {
        // Frame size
        const int Width = 608;
        const int Height = 608;

        // 1 quaterion = 2^14 bits
        const float QuatConvFactor = 1.0f / (1 << 14);

        // Miniscope properties
        private int ledBrightness = 0;
        private int fps = 30;
        private int focus = 0;
        private GainV4 gain = GainV4.Low;

        // connection status
        private int connectionTries = 0;

        // Properties subscriptions
        private CompositeDisposable disposables;
        private readonly Subject<int> ledBrightnessSubject = new();
        private readonly Subject<int> fpsSubject = new();
        private readonly Subject<int> focusSubject = new();
        private readonly Subject<GainV4> gainSubject = new();

        /// <summary>
        /// Gets or sets the LED brightness (0-100%). Changes apply in real-time during capture.
        /// </summary>
        [Description("Adjusts the intensity of the miniscope's LED (0-100%).")]
        [Range(0, 100)]
        [Editor(DesignTypes.SliderEditor, DesignTypes.UITypeEditor)]
        public int LEDBrightness
        {
            get => ledBrightness;
            set { ledBrightness = value; ledBrightnessSubject.OnNext(255 - value * 255 / 100); }
        }

        /// <summary>
        /// Gets or sets the frame rate (10-30 FPS). Changes apply in real-time during capture.
        /// </summary>
        [Description("Sets the framerate.")]
        [Range(10, 30)]
        public int FPS
        {
            get => fps;
            set { fps = value; fpsSubject.OnNext(value); }
        }

        /// <summary>
        /// Gets or sets the electrowetting lens (EWL) focus (-127 to 127). Changes apply in real-time during capture.
        /// </summary>
        [Description("Sets the EWL focus.")]
        [Range(-127, 127)]
        [Editor(DesignTypes.SliderEditor, DesignTypes.UITypeEditor)]
        public int Focus
        {
            get => focus;
            set { focus = value; focusSubject.OnNext(value); }
        }

        /// <summary>
        /// Gets or sets the image sensor gain. Changes apply in real-time during capture.
        /// </summary>
        [Description("Sets the gain of the image sensor.")]
        public GainV4 Gain
        {
            get => gain;
            set { gain = value; gainSubject.OnNext(value); }
        }

        /// <summary>
        /// Gets or sets whether the LED is controlled by an external trigger input.
        /// When true, LED turns off when trigger input is low. Note: the trigger pin is low by default.
        /// </summary>
        [Description("Turns off the LED when the trigger input is low. " +
            "Note that this pin is low by default. Therefore, if it is not driven and " +
            "this option is set to true, the LED will not turn on.")]
        public bool Triggered { get; set; } = false;

        /// <summary>
        /// Gets or sets the camera index to capture from (for multiple V4 devices).
        /// </summary>
        [Description("Index of the miniscope to capture from.")]
        public int CameraIndex { get; set; } = 0;

        /// <summary>
        /// Gets or sets whether to wait for reconnection if the camera disconnects.
        /// If false, the workflow will stop on disconnection.
        /// </summary>
        [Description("True if you want to wait for reconnection, false if you want the workflow to stop.")]
        public bool WaitForReconnection { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to capture IMU data from the BNO055 sensor.
        /// </summary>
        [Description("Grab IMU data")]
        public bool GrabIMU { get; set; } = true;

        [Description("Optional display name for this device. Defaults to the device ID if blank.")]
        public string DeviceName { get; set; } = "";

        readonly IObservable<FrameIMUV4> source;
        readonly object captureLock = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="UCLAV4"/> class.
        /// </summary>
        public UCLAV4()
        {
            source = Observable.Create<FrameIMUV4>((observer, cancellationToken) =>
            {
                return Task.Factory.StartNew(() =>
                {
                    // Make sure this runs fast, probably not super useful but can't hurt
                    Process currentProcess = Process.GetCurrentProcess();
                    currentProcess.PriorityClass = ProcessPriorityClass.High;
                    Thread.CurrentThread.Priority = ThreadPriority.Highest;

                    string deviceId = $"V4_{CameraIndex}";

                    RecordingMetadataService.Metadata = new RecordingMetadata
                    {
                        ROI = new Roi
                        {
                            height = 608,
                            leftEdge = 0,
                            topEdge = 0,
                            width = 608
                        },
                        deviceID = CameraIndex,
                        deviceName = string.IsNullOrWhiteSpace(DeviceName) ? deviceId : DeviceName,
                        deviceType = "Miniscope_V4_BNO",
                        ewl = focus,
                        frameRate = FPS,
                        gain = gain,
                        led0 = LEDBrightness
                    };
                    DeviceMetadataRegistry.Register(deviceId, RecordingMetadataService.Metadata);

                    // Initialize timing service if not already initialized by another source (e.g., MiniCam)
                    TimingService.TryInitialize();

                    Vector4 q = new(0, 0, 0, 0);

                    lock (captureLock)
                    {
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            try
                            {
                                using VideoCapture capture = new(CameraIndex, VideoCaptureAPIs.DSHOW);
                                if (!capture.IsOpened())
                                {
                                    throw new NoMiniscopeException();
                                }

                                // Register with capture service
                                CaptureService.RegisterCapture(deviceId, capture, "V4");

                                OpenCvSharp.Mat frame = new();
                                OpenCvSharp.Mat grayFrame = new();

                                ulong frameNumber = 0;
                                ushort contrast;
                                ushort lastContrast;

                                try
                                {
                                    Hardware.V4.Initialize(capture);

                                    Thread.Sleep(10); // to let everything settle

                                    // Set frame size
                                    capture.Set(VideoCaptureProperties.FrameWidth, Width);
                                    capture.Set(VideoCaptureProperties.FrameHeight, Height);

                                    // Subscribe to property changes
                                    disposables =
                                    [
                                        ledBrightnessSubject
                                                .DistinctUntilChanged()
                                                .Subscribe(LEDBrightness =>
                                                {
                                                    Hardware.V4.SetLEDBrightness(capture, LEDBrightness);
                                                    RecordingMetadataService.Metadata.led0 = LEDBrightness;
                                                }),
                                            fpsSubject
                                                .DistinctUntilChanged()
                                                .Subscribe(FPS =>
                                                {
                                                    Hardware.V4.SetFPS(capture, FPS);
                                                    RecordingMetadataService.Metadata.frameRate = FPS;
                                                }),
                                            focusSubject
                                                .DistinctUntilChanged()
                                                .Subscribe(Focus =>
                                                {
                                                    Hardware.V4.SetFocus(capture, Focus);
                                                    RecordingMetadataService.Metadata.ewl = Focus;
                                                }),
                                            gainSubject
                                                .DistinctUntilChanged()
                                                .Subscribe(Gain =>
                                                {
                                                    Hardware.V4.SetGain(capture, Gain);
                                                    RecordingMetadataService.Metadata.gain = Gain;
                                                }),
                                        ];

                                    // Set propoerties to initial values
                                    Hardware.V4.SetTriggerMode(capture, Triggered);
                                    Hardware.V4.SetFPS(capture, FPS);
                                    Hardware.V4.SetGain(capture, gain);
                                    Hardware.V4.SetFocus(capture, 127 * focus / 100);
                                    Hardware.V4.SetLEDBrightness(capture, 255 - LEDBrightness * 255 / 100); // This needs to be set last

                                    lastContrast = (ushort)capture.Get(VideoCaptureProperties.Contrast);
                                    CaptureService.SetFrameOffset(deviceId, -lastContrast);

                                    if (connectionTries == 0)  // keep the timer if the camera has to restart
                                    {
                                        TimingService.Stopwatch.Restart();
                                    }

                                    connectionTries = 0;

                                    while (!cancellationToken.IsCancellationRequested)
                                    {
                                        // Capture frame
                                        capture.Read(frame);

                                        if (frame.Empty())
                                        {
                                            // Do we want it to throw an error instead of trying to reconnect?
                                            Console.WriteLine("Camera might be disconnected, let's try to reconnect");
                                            break;
                                        }

                                        var timestamp = TimingService.Stopwatch.ElapsedMilliseconds;

                                        // Get latest hardware frame count
                                        contrast = (ushort)capture.Get(VideoCaptureProperties.Contrast);

                                        if (contrast < lastContrast)
                                        {
                                            CaptureService.SetFrameOffset(deviceId, (int)(frameNumber + 1));
                                        }

                                        lastContrast = contrast;

                                        frameNumber = (uint)(contrast + CaptureService.GetFrameOffset(deviceId));

                                        if (GrabIMU)
                                        {
                                            q.W = QuatConvFactor * (float)capture.Get(VideoCaptureProperties.Saturation);
                                            q.X = QuatConvFactor * (float)capture.Get(VideoCaptureProperties.Hue);
                                            q.Y = QuatConvFactor * (float)capture.Get(VideoCaptureProperties.Gain);
                                            q.Z = QuatConvFactor * (float)capture.Get(VideoCaptureProperties.Brightness);

                                            // sometimes the BNO055 loses power and has to be reset. 0 is very unlikely to be a valid value
                                            if (Math.Abs(q.W) < 1e-6f)
                                            {
                                                Hardware.SendI2C(capture, 0x50, 0x41, 0b00001001, 0b00000101); // Remap BNO axes and signs
                                                Hardware.SendI2C(capture, 0x50, 0x3D, 0b00001100); // Set BNO operation mode to NDOF
                                            }
                                        }                                        

                                        // Create grayscale IplImage
                                        Cv2.CvtColor(frame, grayFrame, ColorConversionCodes.BGR2GRAY);

                                        // Create IplImage from OpenCvSharp Mat
                                        IplImage image = new(new OpenCV.Net.Size(grayFrame.Cols, grayFrame.Rows), IplDepth.U8, 1, grayFrame.Data);

                                        // Notify observer with the new frame
                                        observer.OnNext(new FrameIMUV4(image, q, frameNumber, timestamp));
                                    }
                                }
                                catch (Exception e)
                                {
                                    throw new WorkflowException(e.Message);
                                }
                                finally
                                {
                                    CaptureService.UnregisterCapture(deviceId);
                                    DeviceMetadataRegistry.Unregister(deviceId);
                                    Hardware.V4.SetLEDBrightness(capture, 255);
                                    //capture.Set(VideoCaptureProperties.Saturation, 0);  // handled in StartRecording node
                                    //RecordingService.IsRecording = false;
                                    frame.Dispose();
                                    grayFrame.Dispose();
                                    Thread.Sleep(10); // to let the LED switch off before we dispose of capture
                                    disposables.Dispose();
                                    TimingService.Release(); // Release timing service reference when workflow stops
                                }
                            }
                            catch (NoMiniscopeException)
                            {
                                if (WaitForReconnection)
                                {
                                    Console.WriteLine($"Miniscope couldn't be reached ({connectionTries})");
                                    connectionTries++;
                                    var delay = Math.Min(100 * Math.Pow(2, Math.Min(connectionTries, 6)), 10000); // Exponential backoff with a cap at 10 seconds
                                    Thread.Sleep((int)delay);
                                }
                                else
                                {
                                    throw new WorkflowException("No miniscope or wrong index");
                                }
                            }
                        }
                    }
                },
                    cancellationToken,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            })
            .PublishReconnectable()
            .RefCount();
        }

        /// <summary>
        /// Generates an observable sequence of Miniscope V4 frames with IMU data.
        /// </summary>
        /// <returns>An observable sequence of <see cref="FrameIMUV4"/> objects.</returns>
        public override IObservable<FrameIMUV4> Generate()
        {
            return source;
        }
    }
}
