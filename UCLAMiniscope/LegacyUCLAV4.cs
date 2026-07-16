// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

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
    /// Produces a data sequence from a UCLA Miniscope V4 using legacy DAQ firmware, capturing
    /// 608×608 grayscale frames with BNO055 IMU quaternion data.
    /// </summary>
    /// <remarks>
    /// Legacy firmware exposes quaternion components, input state, and frame number through UVC
    /// camera properties. OpenCvSharp is used for efficient property access, while OpenCV.Net
    /// provides the Bonsai image output. Stable frame rates may require the Windows high-performance
    /// power plan on some systems.
    /// </remarks>
    [Description("Produces a data sequence from a UCLA Miniscope V4 using legacy DAQ firmware.")]
    public class LegacyUCLAV4 : Source<LegacyFrameIMUV4>
    {
        // Frame size
        const int Width = 608;
        const int Height = 608;

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
        /// Gets or sets the position of the U.FL connector relative to the animal.
        /// This determines the BNO055 axis mapping.
        /// </summary>
        [Description("Position of the U.FL connector relative to the animal. Determines the IMU axis mapping.")]
        public UFLConnectorLocation UFLConnectorLocation { get; set; } = UFLConnectorLocation.FrontLeft;

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

        /// <summary>
        /// Gets or sets whether to capture the input state.
        /// </summary>
        [Description("Grab Input state")]
        public bool GrabInputState { get; set; } = false;

        /// <summary>
        /// Gets or sets the optional display name stored in recording metadata.
        /// </summary>
        [Description("Optional display name for this device. Defaults to the device ID if blank.")]
        public string DeviceName { get; set; } = "";

        readonly IObservable<LegacyFrameIMUV4> source;
        readonly object captureLock = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="LegacyUCLAV4"/> class.
        /// </summary>
        public LegacyUCLAV4()
        {
            source = Observable.Create<LegacyFrameIMUV4>((observer, cancellationToken) =>
            {
                return Task.Factory.StartNew(() =>
                {
                    // Make sure this runs fast, probably not super useful but can't hurt
                    Process currentProcess = Process.GetCurrentProcess();
                    currentProcess.PriorityClass = ProcessPriorityClass.High;
                    Thread.CurrentThread.Priority = ThreadPriority.Highest;

                    string deviceId = $"V4_{CameraIndex}";

                    var recordingMetadata = new RecordingMetadata
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
                        firmwareGeneration = "legacy",
                        firmwareVersion = "unreported",
                        ewl = focus,
                        frameRate = FPS,
                        gain = gain.ToString(),
                        led0 = LEDBrightness
                    };
                    RecordingMetadataService.Metadata = recordingMetadata;
                    DeviceMetadataRegistry.Register(deviceId, recordingMetadata);

                    // Initialize timing service if not already initialized by another source (e.g., MiniCam)
                    TimingService.TryInitialize();

                    Quaternion q = new(0, 0, 0, 0);

                    try
                    {
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

                                var deviceControls = new LegacyDeviceControls(capture);

                                // Register with capture service
                                CaptureService.RegisterCapture(deviceId, capture, deviceControls, "LegacyV4");

                                OpenCvSharp.Mat frame = new();
                                OpenCvSharp.Mat grayFrame = new();

                                ulong frameNumber = 0;
                                ushort contrast;
                                ushort lastContrast;
                                bool inputState = false;

                                try
                                {
                                    MiniscopeV4Commands.Initialize(deviceControls, UFLConnectorLocation);

                                    Thread.Sleep(500); // to let everything settle, increased for lower spec machines

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
                                                    MiniscopeV4Commands.SetLEDBrightness(deviceControls, LEDBrightness);
                                                    recordingMetadata.led0 = LEDBrightness;
                                                }),
                                            fpsSubject
                                                .DistinctUntilChanged()
                                                .Subscribe(FPS =>
                                                {
                                                    MiniscopeV4Commands.SetFPS(deviceControls, FPS);
                                                    recordingMetadata.frameRate = FPS;
                                                }),
                                            focusSubject
                                                .DistinctUntilChanged()
                                                .Subscribe(Focus =>
                                                {
                                                    MiniscopeV4Commands.SetFocus(deviceControls, Focus);
                                                    recordingMetadata.ewl = Focus;
                                                }),
                                            gainSubject
                                                .DistinctUntilChanged()
                                                .Subscribe(Gain =>
                                                {
                                                    MiniscopeV4Commands.SetGain(deviceControls, Gain);
                                                    recordingMetadata.gain = Gain.ToString();
                                                }),
                                        ];

                                    // Set propoerties to initial values, with delays for slower machines
                                    MiniscopeV4Commands.SetLegacyTriggerMode(deviceControls, Triggered);
                                    Thread.Sleep(50);
                                    MiniscopeV4Commands.SetFPS(deviceControls, FPS);
                                    Thread.Sleep(50);
                                    MiniscopeV4Commands.SetGain(deviceControls, gain);
                                    Thread.Sleep(50);
                                    MiniscopeV4Commands.SetFocus(deviceControls, 127 * focus / 100);
                                    Thread.Sleep(50);
                                    MiniscopeV4Commands.SetLEDBrightness(deviceControls, 255 - LEDBrightness * 255 / 100); // This needs to be set last
                                    Thread.Sleep(50);

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
                                            var candidate = QuaternionHelper.Read(capture);
                                            bool candidateIsValid = QuaternionHelper.IsValid(candidate, out float candidateNormSquared);

                                            // A corrupt or torn read must not replace the last good orientation.
                                            if (candidateIsValid)
                                            {
                                                q = candidate;
                                            }

                                            // A near-zero norm cannot represent an orientation and indicates that
                                            // the BNO055 has stopped after a brief power or coax interruption.
                                            // This is the only check we do, if the BNO was starting to send constant
                                            // garbage we would need to go back to CONFIG mode and reinitialize it,
                                            // but that has not happened in practice yet, so let's not do it for now.
                                            if (candidateNormSquared < QuaternionHelper.FailureNormSquaredThreshold)
                                            {
                                                MiniscopeV4Commands.ReinitializeImu(deviceControls, UFLConnectorLocation);
                                            }
                                        }

                                        if (GrabInputState)
                                        {
                                            // Get input state from inverted Gamma register
                                            inputState = capture.Get(VideoCaptureProperties.Gamma) != 0;
                                        }

                                        // Create grayscale IplImage
                                        Cv2.CvtColor(frame, grayFrame, ColorConversionCodes.BGR2GRAY);

                                        // Create IplImage from OpenCvSharp Mat
                                        IplImage image = new(new OpenCV.Net.Size(grayFrame.Cols, grayFrame.Rows), IplDepth.U8, 1, grayFrame.Data);

                                        // Notify observer with the new frame
                                        observer.OnNext(new LegacyFrameIMUV4(image, q, frameNumber, timestamp, inputState));
                                    }
                                }
                                catch (Exception e)
                                {
                                    throw new WorkflowException(e.Message);
                                }
                                finally
                                {
                                    CaptureService.UnregisterCapture(deviceId);
                                    MiniscopeV4Commands.SetLEDBrightness(deviceControls, 255);
                                    //capture.Set(VideoCaptureProperties.Saturation, 0);  // handled in StartRecording node
                                    //RecordingService.IsRecording = false;
                                    frame.Dispose();
                                    grayFrame.Dispose();
                                    Thread.Sleep(10); // to let the LED switch off before we dispose of capture
                                    disposables.Dispose();
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
                    }
                    finally
                    {
                        DeviceMetadataRegistry.Unregister(deviceId);
                        TimingService.Release();
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
        /// <returns>An observable sequence of <see cref="LegacyFrameIMUV4"/> objects.</returns>
        public override IObservable<LegacyFrameIMUV4> Generate()
        {
            return source;
        }
    }
}
