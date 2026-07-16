// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

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
    /// Produces a data sequence from a UCLA MiniCam using legacy DAQ firmware.
    /// </summary>
    [Description("Produces a data sequence from a UCLA MiniCam using legacy DAQ firmware.")]
    public class LegacyMiniCam : Source<LegacyFrameMiniCam>
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
            set { ledBrightness = value; ledBrightnessSubject.OnNext(value); }
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
        /// Gets or sets whether to capture the input state.
        /// </summary>
        [Description("Grab Input state")]
        public bool GrabInputState { get; set; } = false;

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

        readonly IObservable<LegacyFrameMiniCam> source;
        readonly object captureLock = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="LegacyMiniCam"/> class.
        /// </summary>
        public LegacyMiniCam()
        {
            source = Observable.Create<LegacyFrameMiniCam>((observer, cancellationToken) =>
            {
                return Task.Factory.StartNew(() =>
                {
                    string deviceId = $"LegacyMiniCam_{CameraIndex}";
                    RecordingMetadataService.Metadata = new RecordingMetadata
                    {
                        deviceID = CameraIndex,
                        deviceName = string.IsNullOrWhiteSpace(DeviceName) ? deviceId : DeviceName,
                        deviceType = "MiniCam",
                        firmwareGeneration = "legacy",
                        firmwareVersion = "unreported",
                        frameRate = FPS,
                        gain = Gain.ToString(),
                        led0 = LEDBrightness
                    };
                    DeviceMetadataRegistry.Register(deviceId, RecordingMetadataService.Metadata);

                    // Initialize timing service if not already initialized by another source (e.g., LegacyUCLAV4)
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

                                var deviceControls = new LegacyDeviceControls(capture);

                                // Register with capture service
                                CaptureService.RegisterCapture(deviceId, capture, deviceControls, "MiniCam");

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
                                bool inputState = false;

                                try
                                {
                                    MiniCamCommands.SetLegacyTriggerMode(deviceControls, Triggered);

                                    MiniCamCommands.Initialize(deviceControls);

                                    Thread.Sleep(10); // to let everything settle

                                    // Get width and height from the resolution preset
                                    (int width, int height) = MiniCamCommands.GetResolutionDimensions(Resolution);

                                    // Request the resolution from the driver
                                    capture.Set(VideoCaptureProperties.FrameWidth, width);
                                    capture.Set(VideoCaptureProperties.FrameHeight, height);

                                    MiniCamCommands.SetResolution(deviceControls, width, height, binning: (int)Binning, deviceId: deviceId);
                                    MiniCamCommands.SetFPS(deviceControls, FPS, deviceId);

                                    // Subscribe to property changes
                                    disposables =
                                    [
                                        ledBrightnessSubject
                                            .DistinctUntilChanged()
                                            .Subscribe(value =>
                                            {
                                                MiniCamCommands.SetLEDBrightness(deviceControls, 255 - value * 255 / 100);
                                                RecordingMetadataService.Metadata.led0 = value;
                                            }),
                                        fpsSubject
                                            .DistinctUntilChanged()
                                            .Subscribe(FPS =>
                                            {
                                                MiniCamCommands.SetFPS(deviceControls, FPS, deviceId);
                                                RecordingMetadataService.Metadata.frameRate = FPS;
                                            }),
                                        gainSubject
                                            .DistinctUntilChanged()
                                            .Subscribe(Gain =>
                                            {
                                                MiniCamCommands.SetGain(deviceControls, (int)Gain);
                                                MiniCamConfigService.UpdateGain(deviceId, (int)Gain);
                                                RecordingMetadataService.Metadata.gain = Gain.ToString();
                                            }),
                                    ];

                                    // Set properties to initial values
                                    MiniCamCommands.SetFPS(deviceControls, FPS, deviceId);
                                    MiniCamCommands.SetGain(deviceControls, (int)gain);
                                    MiniCamConfigService.UpdateGain(deviceId, (int)gain);
                                    MiniCamCommands.SetLEDBrightness(deviceControls, 255 - LEDBrightness * 255 / 100);

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

                                        if (GrabInputState)
                                        {
                                            // Get input state from inverted Gamma register
                                            inputState = capture.Get(VideoCaptureProperties.Gamma) != 0;
                                        }

                                        // Create grayscale IplImage
                                        Cv2.CvtColor(frame, grayFrame, ColorConversionCodes.BGR2GRAY);

                                        // Create IplImage from OpenCVSharp Mat
                                        var image = new IplImage(new OpenCV.Net.Size(grayFrame.Cols, grayFrame.Rows), IplDepth.U8, 1, grayFrame.Data);

                                        // Notify observer with the new frame
                                        observer.OnNext(new LegacyFrameMiniCam(image, frameNumber, timestamp, inputState));
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
        /// Generates an observable sequence of legacy-firmware MiniCam frames.
        /// </summary>
        /// <returns>An observable sequence of <see cref="LegacyFrameMiniCam"/> objects.</returns>
        public override IObservable<LegacyFrameMiniCam> Generate()
        {
            return source;
        }
    }
}
