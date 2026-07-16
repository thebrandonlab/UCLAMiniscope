// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

using OpenCV.Net;
using OpenCvSharp;
using System;
using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Bonsai;
using UCLAMiniscope.Helpers;

namespace UCLAMiniscope
{
    /// <summary>
    /// Produces <see cref="LegacyFrameV4"/> objects from the UCLA Miniscope V4 using legacy DAQ firmware.
    /// Use this source when IMU data is not needed, or when IMU data is captured separately at a higher rate.
    /// </summary>
    [Description("Produces LegacyFrameV4 objects from the UCLA Miniscope V4 using legacy DAQ firmware, useful if IMU data is captured separately.")]
    public class LegacyUCLAV4Frame : Source<LegacyFrameV4>
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
        private bool triggered = false;
        private GainV4 gain = GainV4.Low;

        // connection status
        private int connectionTries = 0;

        // Properties subscriptions
        private CompositeDisposable disposables;
        private readonly Subject<int> ledBrightnessSubject = new Subject<int>();
        private readonly Subject<int> fpsSubject = new Subject<int>();
        private readonly Subject<int> focusSubject = new Subject<int>();
        private readonly Subject<bool> triggeredSubject = new Subject<bool>();
        private readonly Subject<GainV4> gainSubject = new Subject<GainV4>();

        /// <summary>
        /// Gets or sets the intensity of the miniscope's excitation LED (0–100%).
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
        /// Gets or sets the capture frame rate in frames per second (10–30).
        /// </summary>
        [Description("Sets the framerate.")]
        [Range(10, 30)]
        public int FPS
        {
            get => fps;
            set { fps = value; fpsSubject.OnNext(value); }
        }

        /// <summary>
        /// Gets or sets the electrowetting lens (EWL) focus value (-127 to 127).
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
        /// Gets or sets the gain level of the image sensor.
        /// </summary>
        [Description("Sets the gain of the image sensor.")]
        public GainV4 Gain
        {
            get => gain;
            set { gain = value; gainSubject.OnNext(value); }
        }

        /// <summary>
        /// Gets or sets a value indicating whether the LED is gated by the trigger input.
        /// When <see langword="true"/>, the LED turns off while the trigger pin is low.
        /// Note that the pin is low by default, so the LED will not turn on unless the pin is driven high.
        /// </summary>
        [Description("Turns off the LED when the trigger input is low. " +
            "Note that this pin is low by default. Therefore, if it is not driven and " +
            "this option is set to true, the LED will not turn on.")]
        public bool Triggered
        {
            get => triggered;
            set { triggered = value; triggeredSubject.OnNext(value); }
        }

        /// <summary>
        /// Gets or sets the position of the U.FL connector relative to the animal.
        /// This determines the BNO055 axis mapping used by a companion IMU source.
        /// </summary>
        [Description("Position of the U.FL connector relative to the animal. Determines the IMU axis mapping.")]
        public UFLConnectorLocation UFLConnectorLocation { get; set; } = Helpers.UFLConnectorLocation.FrontLeft;

        /// <summary>
        /// Gets or sets whether to capture the input state.
        /// </summary>
        [Description("Grab Input state")]
        public bool GrabInputState { get; set; } = false;

        /// <summary>
        /// Gets or sets the zero-based index of the miniscope camera to capture from.
        /// </summary>
        [Description("Index of the miniscope to capture from.")]
        public int CameraIndex { get; set; } = 0;

        /// <summary>
        /// Gets or sets a value indicating whether the source should keep retrying after a disconnection.
        /// When <see langword="false"/>, the workflow stops immediately on connection failure.
        /// </summary>
        [Description("True if you want to wait for reconnection, false if you want the workflow to stop.")]
        public bool WaitForReconnection { get; set; } = true;

        /// <summary>
        /// Starts the miniscope capture loop and produces a sequence of <see cref="LegacyFrameV4"/> frames.
        /// Automatically reconnects when <see cref="WaitForReconnection"/> is <see langword="true"/>.
        /// </summary>
        /// <returns>An observable sequence of <see cref="LegacyFrameV4"/> frames captured from the miniscope.</returns>
        public override IObservable<LegacyFrameV4> Generate()
        {
            return Observable.Create<LegacyFrameV4>((observer, cancellationToken) =>
            {
                return Task.Factory.StartNew(() =>
                {
                    TimingService.TryInitialize();
                    try
                    {
                        using (var capture = new VideoCapture(CameraIndex, VideoCaptureAPIs.DSHOW))
                        {

                            while (!cancellationToken.IsCancellationRequested)
                            {
                                try
                                {

                                if (!capture.IsOpened())
                                {
                                    throw new NoMiniscopeException();
                                }

                                var deviceControls = new LegacyDeviceControls(capture);

                                // Register capture with CaptureService
                                string deviceId = $"V4_{CameraIndex}";
                                CaptureService.RegisterCapture(deviceId, capture, deviceControls, "LegacyV4");

                                var frame = new OpenCvSharp.Mat();
                                var grayFrame = new OpenCvSharp.Mat();
                                bool inputState = false;

                                try
                                {
                                    MiniscopeV4Commands.Initialize(deviceControls, UFLConnectorLocation);

                                    // Set frame size
                                    capture.Set(VideoCaptureProperties.FrameWidth, Width);
                                    capture.Set(VideoCaptureProperties.FrameHeight, Height);

                                    // Start Digital Output switching
                                    deviceControls.SetFrameOutputEnabled(true);

                                    Thread.Sleep(10); // to let everything settle

                                    // Subscribe to property changes
                                    disposables = new CompositeDisposable
                                    {
                                        ledBrightnessSubject.DistinctUntilChanged().Subscribe(LEDBrightness => MiniscopeV4Commands.SetLEDBrightness(deviceControls, LEDBrightness)),
                                        fpsSubject.DistinctUntilChanged().Subscribe(FPS => MiniscopeV4Commands.SetFPS(deviceControls, FPS)),
                                        focusSubject.DistinctUntilChanged().Subscribe(Focus => MiniscopeV4Commands.SetFocus(deviceControls, Focus)),
                                        triggeredSubject.DistinctUntilChanged().Subscribe(Triggered => MiniscopeV4Commands.SetLegacyTriggerMode(deviceControls, Triggered)),
                                        gainSubject.DistinctUntilChanged().Subscribe(Gain => MiniscopeV4Commands.SetGain(deviceControls, Gain)),
                                    };

                                    // Set propoerties to initial values
                                    MiniscopeV4Commands.SetLegacyTriggerMode(deviceControls, Triggered);
                                    MiniscopeV4Commands.SetFPS(deviceControls, FPS);
                                    MiniscopeV4Commands.SetGain(deviceControls, gain);
                                    MiniscopeV4Commands.SetFocus(deviceControls, 127 * focus / 100);
                                    MiniscopeV4Commands.SetLEDBrightness(deviceControls, 255 - LEDBrightness * 255 / 100); // This needs to be set last

                                    // Initialize frame offset
                                    ushort lastContrast = (ushort)capture.Get(VideoCaptureProperties.Contrast);
                                    int frameOffset = -lastContrast;
                                    CaptureService.SetFrameOffset(deviceId, -lastContrast);

                                    ulong frameNumber = 0;

                                    while (!cancellationToken.IsCancellationRequested)
                                    {
                                        capture.Read(frame);
                                        long timestamp = TimingService.Stopwatch.ElapsedMilliseconds;

                                        if (frame.Empty())
                                        {
                                            // Do we want it to throw an error instead of trying to reconnect?
                                            Console.WriteLine("Camera might be disconnected, let's try to reconnect");
                                            break;
                                        }

                                        ushort contrast = (ushort)capture.Get(VideoCaptureProperties.Contrast);

                                        if (contrast < lastContrast)
                                        {
                                            frameOffset = (int)(frameNumber + 1);
                                            CaptureService.SetFrameOffset(deviceId, frameOffset);
                                        }

                                        lastContrast = contrast;

                                        frameNumber = (ulong)(contrast + frameOffset);

                                        if (GrabInputState)
                                        {
                                            inputState = capture.Get(VideoCaptureProperties.Gamma) != 0;
                                        }

                                        Cv2.CvtColor(frame, grayFrame, ColorConversionCodes.BGR2GRAY);
                                        var image = new IplImage(new OpenCV.Net.Size(grayFrame.Cols, grayFrame.Rows), IplDepth.U8, 1, grayFrame.Data);
                                        observer.OnNext(new LegacyFrameV4(image, frameNumber, timestamp, inputState));
                                    }
                                }
                                catch
                                {
                                }
                                finally
                                {
                                    CaptureService.UnregisterCapture(deviceId);
                                    deviceControls.SetFrameOutputEnabled(false);
                                    MiniscopeV4Commands.SetLEDBrightness(deviceControls, 255);
                                    Thread.Sleep(10); // to let the LED switch off
                                    frame.Dispose();
                                    grayFrame.Dispose();
                                    capture.Release();
                                    capture.Dispose();
                                    disposables.Dispose();
                                }
                            }
                                catch (NoMiniscopeException)
                                {
                                    if (WaitForReconnection)
                                    {

                                        Console.WriteLine($"miniscope couldn't be reached ({connectionTries})");
                                        connectionTries++;
                                        Thread.Sleep(1000);
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
                        TimingService.Release();
                    }
                }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            })
            .PublishReconnectable()
            .RefCount();
        }
    }
}
