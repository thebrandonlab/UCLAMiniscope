// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

using Bonsai;
using System;
using System.ComponentModel;
using System.Numerics;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using UCLAMiniscope.Helpers;

namespace UCLAMiniscope
{
    /// <summary>
    /// Produces frames, embedded IMU data, and digital input state from a UCLA Miniscope V4
    /// running current Open Ephys DAQ firmware.
    /// </summary>
    /// <remarks>
    /// Uses the attributed current-firmware protocol helpers documented in THIRD_PARTY_NOTICES.md.
    /// The node's reactive acquisition and recording integration are local.
    /// </remarks>
    [Description("Produces a data sequence from a UCLA Miniscope V4 using current Open Ephys DAQ firmware.")]
    public class UCLAV4 : Source<FrameIMUV4>
    {
        const int Width = 608;
        const int Height = 608;
        const int ReconnectDelayMilliseconds = 1000;

        readonly Subject<int> ledBrightnessSubject = new Subject<int>();
        readonly Subject<int> fpsSubject = new Subject<int>();
        readonly Subject<int> focusSubject = new Subject<int>();
        readonly Subject<GainV4> gainSubject = new Subject<GainV4>();
        readonly Subject<bool> triggeredSubject = new Subject<bool>();
        readonly IObservable<FrameIMUV4> source;

        int ledBrightness;
        int fps = 30;
        int focus;
        GainV4 gain = GainV4.Low;
        bool triggered;

        /// <summary>
        /// Gets or sets the LED brightness as a percentage of maximum.
        /// </summary>
        [Description("Adjusts the intensity of the miniscope's LED (0-100%).")]
        [Range(0, 100)]
        [Editor(DesignTypes.SliderEditor, DesignTypes.UITypeEditor)]
        public int LEDBrightness
        {
            get => ledBrightness;
            set
            {
                ledBrightness = value;
                ledBrightnessSubject.OnNext(value);
            }
        }

        /// <summary>
        /// Gets or sets the acquisition frame rate.
        /// </summary>
        [Description("Sets the framerate.")]
        [Range(10, 30)]
        public int FPS
        {
            get => fps;
            set
            {
                fps = value;
                fpsSubject.OnNext(value);
            }
        }

        /// <summary>
        /// Gets or sets the electrowetting lens focus adjustment.
        /// </summary>
        [Description("Sets the EWL focus.")]
        [Range(-127, 127)]
        [Editor(DesignTypes.SliderEditor, DesignTypes.UITypeEditor)]
        public int Focus
        {
            get => focus;
            set
            {
                focus = value;
                focusSubject.OnNext(value);
            }
        }

        /// <summary>
        /// Gets or sets the Python480 image sensor gain.
        /// </summary>
        [Description("Sets the gain of the image sensor.")]
        public GainV4 Gain
        {
            get => gain;
            set
            {
                gain = value;
                gainSubject.OnNext(value);
            }
        }

        /// <summary>
        /// Gets or sets whether digital input 0 gates the excitation LED.
        /// </summary>
        [Description("Turns off the LED while digital input 0 is low. The input is low when undriven.")]
        public bool Triggered
        {
            get => triggered;
            set
            {
                triggered = value;
                triggeredSubject.OnNext(value);
            }
        }

        /// <summary>
        /// Gets or sets the position of the U.FL connector relative to the animal.
        /// This determines the BNO055 axis mapping.
        /// </summary>
        [Description("Position of the U.FL connector relative to the animal. Determines the IMU axis mapping.")]
        public UFLConnectorLocation UFLConnectorLocation { get; set; } = Helpers.UFLConnectorLocation.FrontLeft;

        /// <summary>
        /// Gets or sets the index within the connected current-firmware Miniscope DAQ list.
        /// </summary>
        [Description("Index of the current-firmware miniscope DAQ to capture from.")]
        public int CameraIndex { get; set; }

        /// <summary>
        /// Gets or sets whether acquisition waits and reconnects after a device failure.
        /// </summary>
        [Description("True to wait for reconnection after a device failure; false to stop the workflow.")]
        public bool WaitForReconnection { get; set; } = true;

        /// <summary>
        /// Gets or sets how Windows handles frames that arrive while the previous frame is being processed.
        /// </summary>
        [Description("Realtime delivers the latest frame; Buffered preserves frames in order while system memory is available.")]
        public FrameAcquisitionMode AcquisitionMode { get; set; } = FrameAcquisitionMode.Realtime;

        /// <summary>
        /// Gets or sets an optional display name used in recording metadata.
        /// </summary>
        [Description("Optional display name for this device. Defaults to the device ID if blank.")]
        public string DeviceName { get; set; } = "";

        /// <summary>
        /// Gets the firmware version reported by the connected DAQ extension unit.
        /// </summary>
        [Description("Firmware version reported by the connected DAQ.")]
        [XmlIgnore]
        public Version FirmwareVersion { get; private set; } = new Version(0, 0, 0);

        /// <summary>
        /// Initializes a new current-firmware UCLA Miniscope V4 source.
        /// </summary>
        public UCLAV4()
        {
            source = Observable.Create<FrameIMUV4>((observer, cancellationToken) =>
                Task.Factory.StartNew(
                    () => RunAcquisition(observer, cancellationToken),
                    cancellationToken,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default))
                .PublishReconnectable()
                .RefCount();
        }

        void RunAcquisition(IObserver<FrameIMUV4> observer, CancellationToken cancellationToken)
        {
            string deviceId = $"V4_{CameraIndex}";
            var sequenceState = new FrameSequenceState();

            var recordingMetadata = new RecordingMetadata
            {
                ROI = new Roi
                {
                    height = Height,
                    leftEdge = 0,
                    topEdge = 0,
                    width = Width
                },
                deviceID = CameraIndex,
                deviceName = string.IsNullOrWhiteSpace(DeviceName) ? deviceId : DeviceName,
                deviceType = "Miniscope_V4_BNO",
                firmwareGeneration = "current",
                firmwareVersion = "unavailable",
                ewl = Focus,
                frameRate = FPS,
                gain = Gain.ToString(),
                led0 = LEDBrightness
            };

            RecordingMetadataService.Metadata = recordingMetadata;
            DeviceMetadataRegistry.Register(deviceId, recordingMetadata);
            TimingService.TryInitialize();

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        RunCaptureSession(observer, deviceId, recordingMetadata, sequenceState, cancellationToken);
                        if (!cancellationToken.IsCancellationRequested)
                            throw new InvalidOperationException("The Miniscope frame stream ended unexpectedly.");
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (!WaitForReconnection || IsFatalConfigurationError(ex))
                        {
                            observer.OnError(ex);
                            return;
                        }

                        Console.WriteLine($"[UCLAV4] {ex.Message} Waiting for reconnection...");
                        if (cancellationToken.WaitHandle.WaitOne(ReconnectDelayMilliseconds)) break;
                    }
                }
            }
            finally
            {
                CaptureService.UnregisterCapture(deviceId);
                DeviceMetadataRegistry.Unregister(deviceId);
                TimingService.Release();
            }
        }

        void RunCaptureSession(
            IObserver<FrameIMUV4> observer,
            string deviceId,
            RecordingMetadata recordingMetadata,
            FrameSequenceState sequenceState,
            CancellationToken cancellationToken)
        {
            ExtensionUnitMediaCapture capture = null;
            ExtensionUnitDeviceControls controls = null;
            CompositeDisposable controlSubscriptions = null;
            Action<ExtensionUnitRawFrame> frameArrived = null;
            bool registered = false;

            try
            {
                capture = ExtensionUnitMediaCapture.Create(CameraIndex);
                controls = capture.DeviceControls;
                FirmwareVersion = capture.FirmwareVersion;
                recordingMetadata.firmwareVersion = FirmwareVersion.ToString();

                CaptureService.RegisterCapture(deviceId, controls, "V4");
                registered = true;

                MiniscopeV4Commands.Initialize(controls, UFLConnectorLocation);

                var digitalInputSubject = new Subject<byte>();
                controlSubscriptions = new CompositeDisposable();
                controlSubscriptions.Add(digitalInputSubject);
                SubscribeToControls(controls, digitalInputSubject, recordingMetadata, controlSubscriptions);

                bool frameOriginInitialized = false;
                bool imuRecoveryIssued = false;
                Quaternion lastQuaternion = default;

                frameArrived = rawFrame =>
                {
                    var metadata = ExtensionUnitFrameDecoder.DecodeMetadata(rawFrame);
                    controls.UpdateFrameNumber(metadata.FrameNumber);

                    if (!frameOriginInitialized)
                    {
                        long desiredFrameNumber = sequenceState.NextFrameNumber ?? 0;
                        CaptureService.SetFrameOffset(deviceId, desiredFrameNumber - metadata.FrameNumber);
                        frameOriginInitialized = true;
                    }

                    digitalInputSubject.OnNext(metadata.DigitalInputs);

                    if (QuaternionHelper.IsValid(metadata.Quaternion, out float normSquared))
                    {
                        lastQuaternion = metadata.Quaternion;
                        imuRecoveryIssued = false;
                    }
                    else if (normSquared < QuaternionHelper.FailureNormSquaredThreshold && !imuRecoveryIssued)
                    {
                        MiniscopeV4Commands.ReinitializeImu(controls, UFLConnectorLocation);
                        imuRecoveryIssued = true;
                    }

                    long adjustedFrameNumber = (long)metadata.FrameNumber + CaptureService.GetFrameOffset(deviceId);
                    if (adjustedFrameNumber < 0) adjustedFrameNumber = 0;
                    sequenceState.NextFrameNumber = adjustedFrameNumber == long.MaxValue
                        ? long.MaxValue
                        : adjustedFrameNumber + 1;

                    var image = ExtensionUnitFrameDecoder.CreateGrayscaleImage(rawFrame);
                    bool delivered = false;
                    try
                    {
                        observer.OnNext(new FrameIMUV4(
                            image,
                            lastQuaternion,
                            (ulong)adjustedFrameNumber,
                            rawFrame.ReceiveTimestamp,
                            metadata.HardwareTime,
                            metadata.DigitalInputs));
                        delivered = true;
                    }
                    finally
                    {
                        if (!delivered) image.Dispose();
                    }
                };

                capture.FrameArrived += frameArrived;
                capture.Start(Width, Height, AcquisitionMode, cancellationToken);
                capture.Wait(cancellationToken);
            }
            finally
            {
                if (capture != null)
                {
                    if (frameArrived != null)
                        capture.FrameArrived -= frameArrived;
                    capture.Stop();
                }

                controlSubscriptions?.Dispose();

                if (controls != null)
                {
                    try
                    {
                        controls.SetFrameOutputEnabled(false);
                        MiniscopeV4Commands.SetLEDBrightness(controls, 255);
                    }
                    catch (Exception)
                    {
                        // Cleanup commonly runs after the USB device has already disappeared.
                    }
                }

                if (capture != null)
                    capture.Dispose();

                if (registered)
                    CaptureService.UnregisterCapture(deviceId);
            }
        }

        void SubscribeToControls(
            ExtensionUnitDeviceControls controls,
            IObservable<byte> digitalInputs,
            RecordingMetadata recordingMetadata,
            CompositeDisposable subscriptions)
        {
            var brightness = ledBrightnessSubject
                .StartWith(LEDBrightness)
                .Select(value => Math.Max(0, Math.Min(100, value)))
                .DistinctUntilChanged();

            var triggerEnabled = triggeredSubject
                .StartWith(Triggered)
                .DistinctUntilChanged();

            var inputHigh = digitalInputs
                .StartWith((byte)0)
                .Select(value => (value & 0x01) != 0)
                .DistinctUntilChanged();

            subscriptions.Add(
                brightness
                    .CombineLatest(
                        triggerEnabled,
                        (value, isTriggered) => new { Brightness = value, IsTriggered = isTriggered })
                    .CombineLatest(
                        inputHigh,
                        (led, isInputHigh) => new
                        {
                            led.Brightness,
                            EncodedBrightness = led.IsTriggered && !isInputHigh
                                ? (byte)255
                                : (byte)(255 - led.Brightness * 255 / 100)
                        })
                    .DistinctUntilChanged()
                    .Subscribe(value =>
                    {
                        MiniscopeV4Commands.SetLEDBrightness(controls, value.EncodedBrightness);
                        recordingMetadata.led0 = value.Brightness;
                    }));

            subscriptions.Add(
                fpsSubject
                    .StartWith(FPS)
                    .DistinctUntilChanged()
                    .Subscribe(value =>
                    {
                        if (value < 10 || value > 30)
                            throw new ArgumentOutOfRangeException(nameof(FPS), "FPS must be between 10 and 30.");

                        MiniscopeV4Commands.QueueFPS(controls, value);
                        controls.I2C.CommitCommands();
                        recordingMetadata.frameRate = value;
                    }));

            subscriptions.Add(
                focusSubject
                    .StartWith(Focus)
                    .Select(value => Math.Max(-127, Math.Min(127, value)))
                    .DistinctUntilChanged()
                    .Subscribe(value =>
                    {
                        MiniscopeV4Commands.SetFocus(controls, value);
                        recordingMetadata.ewl = value;
                    }));

            subscriptions.Add(
                gainSubject
                    .StartWith(Gain)
                    .DistinctUntilChanged()
                    .Subscribe(value =>
                    {
                        MiniscopeV4Commands.SetGain(controls, value);
                        recordingMetadata.gain = value.ToString();
                    }));

        }

        static bool IsFatalConfigurationError(Exception exception)
        {
            return exception is NotSupportedException || exception is ArgumentException;
        }

        sealed class FrameSequenceState
        {
            public long? NextFrameNumber { get; set; }
        }

        /// <summary>
        /// Generates the current-firmware UCLA Miniscope V4 data sequence.
        /// </summary>
        public override IObservable<FrameIMUV4> Generate()
        {
            return source;
        }
    }
}
