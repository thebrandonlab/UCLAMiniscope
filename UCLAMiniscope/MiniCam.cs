/*
MiniCam.cs

Description:
  This class provides a source for capturing data from the UCLA Minicam. It captures frames and provides 
  configuration controls for resolution, gain, binning, frame rate, and the optional ring LED brightness.
  Uses OpenCvSharp to reuse the V4 DAQ communication.

Author:
  Clément Bourguignon
  Brandon Lab @ McGill University
  2026

Dependencies:
  - OpenCvSharp
  - OpenCV.Net

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
using OpenCvSharp;
using OpenCV.Net;
using Bonsai;
using UCLAMiniscope.Helpers;

namespace UCLAMiniscope
{
    /// <summary>
    /// Produces a data sequence from a UCLA MiniCam with configurable resolution, binning, LED brightness, FPS, and gain.
    /// </summary>
    [Description("Produces a data sequence from a UCLA Minicam.")]
    public class MiniCam : Source<FrameMiniCam>
    {
        // Minicam properties
        private ResolutionPreset resolution = ResolutionPreset.R1024x768;
        private BinningEnum binning = BinningEnum.x2;
        private int ledBrightness = 0;
        private int fps = 30;
        private GainMiniCam gain = GainMiniCam.X1;

        // connection status
        private int connectionTries = 0;

        // Properties subscriptions
        private CompositeDisposable disposables;
        private readonly Subject<int> ledBrightnessSubject = new();
        private readonly Subject<int> fpsSubject = new();
        private readonly Subject<GainMiniCam> gainSubject = new();

        /// <summary>
        /// Gets or sets the camera index to capture from (for multiple MiniCam devices).
        /// </summary>
        [Description("Index of the minicam to capture from.")]
        public int CameraIndex { get; set; } = 0;

        /// <summary>
        /// Gets or sets the resolution preset supported by the DAQ firmware.
        /// </summary>
        [Description("Select the resolution supported by the DAQ firmware.")]
        public ResolutionPreset Resolution
        {
            get => resolution;
            set => resolution = value;
        }

        /// <summary>
        /// Gets or sets the binning factor.
        /// </summary>
        [Description("Set the binning factor. x1: no binning")]
        public BinningEnum Binning
        {
            get => binning;
            set => binning = value;
        }

        /// <summary>
        /// Gets or sets the LED ring brightness (0-100%).
        /// </summary>
        [Description("Adjusts the intensity of the minicam's LED ring (0-100%).")]
        [Range(0, 100)]
        [Editor(DesignTypes.SliderEditor, DesignTypes.UITypeEditor)]
        public int LEDBrightness
        {
            get => ledBrightness;
            set { ledBrightness = value; ledBrightnessSubject.OnNext(255 - value * 255 / 100); }
        }

        /// <summary>
        /// Gets or sets the frame rate (10-30 FPS).
        /// </summary>
        [Description("Sets the framerate.")]
        [Range(10, 30)]
        public int FPS
        {
            get => fps;
            set { fps = value; fpsSubject.OnNext(value); }
        }

        /// <summary>
        /// Gets or sets the gain of the image sensor.
        /// </summary>
        [Description("Sets the gain of the image sensor.")]
        public GainMiniCam Gain
        {
            get => gain;
            set { gain = value; gainSubject.OnNext(value); }
        }

        /// <summary>
        /// Gets or sets whether to trigger the exposure of a new frame by pulling GPIO0 low.
        /// If false (default), the camera will run in free-run mode.
        /// </summary>
        [Description("Makes the exposure of a new frame trigerrable by pulling Input Trigger low. ONLY WORKS WITH MODIFIED DAQ FIRWARE")]
        public bool Triggered { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to wait for reconnection if the camera disconnects.
        /// If false, the workflow will stop on disconnection.
        /// </summary>
        [Description("True if you want to wait for reconnection, false if you want the workflow to stop.")]
        public bool WaitForReconnection { get; set; } = true;

        /// <summary>
        /// Gets or sets the optional display name for this device. If not specified, the device ID is used as the
        /// default name.
        /// </summary>
        /// <remarks>This property allows users to assign a user-friendly name to the device, which can
        /// improve clarity in user interfaces and logs.</remarks>
        [Description("Optional display name for this device. Defaults to the device ID if blank.")]
        public string DeviceName { get; set; } = "";

        readonly IObservable<FrameMiniCam> source;
        readonly object captureLock = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="MiniCam"/> class.
        /// </summary>
        public MiniCam()
        {
            source = Observable.Create<FrameMiniCam>((observer, cancellationToken) =>
            {
                return Task.Factory.StartNew(() =>
                {
                    string deviceId = $"MiniCam_{CameraIndex}";
                    RecordingMetadataService.Metadata = new RecordingMetadata
                    {
                        deviceID = CameraIndex,
                        deviceName = string.IsNullOrWhiteSpace(DeviceName) ? deviceId : DeviceName,
                        deviceType = "MiniCam",
                        frameRate = FPS,
                        led0 = LEDBrightness
                    };
                    DeviceMetadataRegistry.Register(deviceId, RecordingMetadataService.Metadata);

                    // Initialize timing service if not already initialized by another source (e.g., UCLAV4)
                    TimingService.TryInitialize();

                    lock (captureLock)
                    {
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            try
                            {
                                using var capture = new VideoCapture(CameraIndex, VideoCaptureAPIs.DSHOW);
                                if (!capture.IsOpened())
                                {
                                    throw new NoMiniscopeException();
                                }

                                // Register with capture service
                                CaptureService.RegisterCapture(deviceId, capture, "MiniCam");

                                // Register with sensor config service
                                MiniCamConfigService.Register(deviceId, new MiniCamConfigService.SensorConfig
                                {
                                    PixelClockHz = 96_000_000.0
                                });

                                var frame = new OpenCvSharp.Mat();
                                var grayFrame = new OpenCvSharp.Mat();
                                ulong frameNumber = 0;
                                ushort contrast;
                                ushort lastContrast;

                                try
                                {
                                    Hardware.MiniCam.SetTriggerMode(capture, Triggered);

                                    Hardware.MiniCam.Initialize(capture);

                                    Thread.Sleep(10); // to let everything settle

                                    // Get width and height from the resolution preset
                                    (int width, int height) = Hardware.GetResolutionDimensions(Resolution);

                                    // Request the resolution from the driver
                                    capture.Set(VideoCaptureProperties.FrameWidth, width);
                                    capture.Set(VideoCaptureProperties.FrameHeight, height);

                                    Hardware.MiniCam.SetResolution(capture, width, height, binning: (int)Binning, deviceId: deviceId);
                                    Hardware.MiniCam.SetFPS(capture, FPS, deviceId);

                                    // Subscribe to property changes
                                    disposables =
                                    [
                                        ledBrightnessSubject
                                            .DistinctUntilChanged()
                                            .Subscribe(LEDBrightness =>
                                            {
                                                Hardware.MiniCam.SetLEDBrightness(capture, LEDBrightness);
                                                RecordingMetadataService.Metadata.led0 = LEDBrightness;
                                            }),
                                        fpsSubject
                                            .DistinctUntilChanged()
                                            .Subscribe(FPS =>
                                            {
                                                Hardware.MiniCam.SetFPS(capture, FPS, deviceId);
                                                RecordingMetadataService.Metadata.frameRate = FPS;
                                            }),
                                        gainSubject
                                            .DistinctUntilChanged()
                                            .Subscribe(Gain =>
                                            {
                                                Hardware.MiniCam.SetGain(capture, (int)Gain);
                                                MiniCamConfigService.UpdateGain(deviceId, (int)Gain);
                                            }),
                                    ];

                                    // Set properties to initial values
                                    Hardware.MiniCam.SetFPS(capture, FPS, deviceId);
                                    Hardware.MiniCam.SetGain(capture, (int)gain);
                                    MiniCamConfigService.UpdateGain(deviceId, (int)gain);
                                    Hardware.MiniCam.SetLEDBrightness(capture, 255 - LEDBrightness * 255 / 100);

                                    lastContrast = (ushort)capture.Get(VideoCaptureProperties.Contrast);
                                    CaptureService.SetFrameOffset(deviceId, -lastContrast);

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

                                        //var timestamp = RecordingTimerService.startTimeUtc + Stopwatch.GetTimestamp();
                                        var timestamp = TimingService.Stopwatch.ElapsedMilliseconds;

                                        // Get latest hardware frame count
                                        contrast = (ushort)capture.Get(VideoCaptureProperties.Contrast);

                                        if (contrast < lastContrast)
                                        {
                                            CaptureService.SetFrameOffset(deviceId, (int)frameNumber + 1);
                                        }

                                        lastContrast = contrast;

                                        frameNumber = (uint)(contrast + CaptureService.GetFrameOffset(deviceId));

                                        // Create grayscale IplImage
                                        Cv2.CvtColor(frame, grayFrame, ColorConversionCodes.BGR2GRAY);

                                        // Create IplImage from OpenCVSharp Mat
                                        var image = new IplImage(new OpenCV.Net.Size(grayFrame.Cols, grayFrame.Rows), IplDepth.U8, 1, grayFrame.Data);

                                        // Notify observer with the new frame
                                        observer.OnNext(new FrameMiniCam(image, frameNumber, timestamp));
                                    }
                                }
                                catch (Exception e)
                                {
                                    throw new WorkflowException(e.Message);
                                }
                                finally
                                {
                                    CaptureService.UnregisterCapture($"MiniCam_{CameraIndex}");
                                    DeviceMetadataRegistry.Unregister(deviceId);
                                    MiniCamConfigService.Unregister(deviceId);
                                    frame.Dispose();
                                    grayFrame.Dispose();
                                    Thread.Sleep(10); // to let the LED switch off
                                    disposables.Dispose();
                                    TimingService.Release(); // Release timing service reference when workflow stops
                                }
                            }
                            catch (NoMiniscopeException)
                            {
                                if (WaitForReconnection)
                                {

                                    Console.WriteLine($"minicam couldn't be reached ({connectionTries})");
                                    connectionTries++;
                                    Thread.Sleep(1000);
                                }
                                else
                                {
                                    throw new WorkflowException("No minicam or wrong index");
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
        /// Generates an observable sequence of MiniCam frames.
        /// </summary>
        /// <returns>An observable sequence of <see cref="FrameMiniCam"/> objects.</returns>
        public override IObservable<FrameMiniCam> Generate()
        {
            return source;
        }
    }
}