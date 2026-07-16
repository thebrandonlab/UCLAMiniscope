// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

using Bonsai;
using System;
using System.ComponentModel;
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
    /// Produces frames and embedded timing and digital-input metadata from a UCLA MiniCam
    /// running Open Ephys DAQ firmware.
    /// </summary>
    /// <remarks>
    /// MiniCam metadata is assumed to use the same embedded frame number, hardware timer,
    /// and digital-input layout as the Miniscope V4. Quaternion words are ignored because
    /// the MiniCam has no BNO055 IMU.
    /// </remarks>
    [Description("Produces a data sequence from a UCLA MiniCam using Open Ephys DAQ firmware.")]
    public class MiniCam : Source<FrameMiniCam>
    {
        const int ReconnectDelayMilliseconds = 1000;

        readonly Subject<int> ledBrightnessSubject = new Subject<int>();
        readonly Subject<int> fpsSubject = new Subject<int>();
        readonly Subject<GainMiniCam> gainSubject = new Subject<GainMiniCam>();
        readonly IObservable<FrameMiniCam> source;

        ResolutionPreset resolution = ResolutionPreset.R1024x768;
        BinningEnum binning = BinningEnum.x2;
        int ledBrightness;
        int fps = 30;
        GainMiniCam gain = GainMiniCam.X1;

        /// <summary>
        /// Gets or sets the index within the connected Open Ephys firmware UCLA DAQ list.
        /// </summary>
        [Description("Index of the Open Ephys firmware UCLA DAQ to capture from.")]
        public int CameraIndex { get; set; }

        /// <summary>
        /// Gets or sets the MiniCam output resolution.
        /// </summary>
        [Description("Selects the MiniCam output resolution.")]
        public ResolutionPreset Resolution
        {
            get => resolution;
            set => resolution = value;
        }

        /// <summary>
        /// Gets or sets the sensor binning factor.
        /// </summary>
        [Description("Sets the MiniCam sensor binning factor.")]
        public BinningEnum Binning
        {
            get => binning;
            set => binning = value;
        }

        /// <summary>
        /// Gets or sets the LED ring brightness as a percentage of maximum.
        /// </summary>
        [Description("Adjusts the MiniCam LED ring intensity (0-100%).")]
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
        [Description("Sets the MiniCam frame rate.")]
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
        /// Gets or sets the MT9P031 image-sensor gain.
        /// </summary>
        [Description("Sets the MiniCam image-sensor gain.")]
        public GainMiniCam Gain
        {
            get => gain;
            set
            {
                gain = value;
                gainSubject.OnNext(value);
            }
        }

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
        /// Initializes a new Open Ephys firmware UCLA MiniCam source.
        /// </summary>
        public MiniCam()
        {
            source = Observable.Create<FrameMiniCam>((observer, cancellationToken) =>
                Task.Factory.StartNew(
                    () => RunAcquisition(observer, cancellationToken),
                    cancellationToken,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default))
                .PublishReconnectable()
                .RefCount();
        }

        void RunAcquisition(IObserver<FrameMiniCam> observer, CancellationToken cancellationToken)
        {
            string deviceId = $"MiniCam_{CameraIndex}";
            (int width, int height) = MiniCamCommands.GetResolutionDimensions(Resolution);
            var sequenceState = new FrameSequenceState();

            var recordingMetadata = new RecordingMetadata
            {
                ROI = new Roi
                {
                    height = height,
                    leftEdge = 0,
                    topEdge = 0,
                    width = width
                },
                deviceID = CameraIndex,
                deviceName = string.IsNullOrWhiteSpace(DeviceName) ? deviceId : DeviceName,
                deviceType = "MiniCam",
                firmwareGeneration = "current",
                firmwareVersion = "unavailable",
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
                        RunCaptureSession(
                            observer,
                            deviceId,
                            width,
                            height,
                            recordingMetadata,
                            sequenceState,
                            cancellationToken);

                        if (!cancellationToken.IsCancellationRequested)
                            throw new InvalidOperationException("The MiniCam frame stream ended unexpectedly.");
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

                        Console.WriteLine($"[MiniCam] {ex.Message} Waiting for reconnection...");
                        if (cancellationToken.WaitHandle.WaitOne(ReconnectDelayMilliseconds)) break;
                    }
                }
            }
            finally
            {
                CaptureService.UnregisterCapture(deviceId);
                DeviceMetadataRegistry.Unregister(deviceId);
                MiniCamConfigService.Unregister(deviceId);
                TimingService.Release();
            }
        }

        void RunCaptureSession(
            IObserver<FrameMiniCam> observer,
            string deviceId,
            int width,
            int height,
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

                CaptureService.RegisterCapture(deviceId, controls, "MiniCam");
                registered = true;

                MiniCamConfigService.Register(deviceId, new MiniCamConfigService.SensorConfig
                {
                    PixelClockHz = 96_000_000.0
                });

                MiniCamCommands.Initialize(controls);
                MiniCamCommands.SetResolution(
                    controls,
                    width,
                    height,
                    binning: (int)Binning,
                    deviceId: deviceId);

                controlSubscriptions = new CompositeDisposable();
                SubscribeToControls(controls, recordingMetadata, deviceId, controlSubscriptions);

                bool frameOriginInitialized = false;

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

                    long adjustedFrameNumber = (long)metadata.FrameNumber + CaptureService.GetFrameOffset(deviceId);
                    if (adjustedFrameNumber < 0) adjustedFrameNumber = 0;
                    sequenceState.NextFrameNumber = adjustedFrameNumber == long.MaxValue
                        ? long.MaxValue
                        : adjustedFrameNumber + 1;

                    var image = ExtensionUnitFrameDecoder.CreateGrayscaleImage(rawFrame);
                    bool delivered = false;
                    try
                    {
                        observer.OnNext(new FrameMiniCam(
                            image,
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
                capture.Start(width, height, AcquisitionMode, cancellationToken);
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
                        MiniCamCommands.SetLEDBrightness(controls, 255);
                    }
                    catch (Exception)
                    {
                        // Cleanup commonly runs after the USB device has already disappeared.
                    }
                }

                capture?.Dispose();
                MiniCamConfigService.Unregister(deviceId);

                if (registered)
                    CaptureService.UnregisterCapture(deviceId);
            }
        }

        void SubscribeToControls(
            ExtensionUnitDeviceControls controls,
            RecordingMetadata recordingMetadata,
            string deviceId,
            CompositeDisposable subscriptions)
        {
            subscriptions.Add(
                ledBrightnessSubject
                    .StartWith(LEDBrightness)
                    .Select(value => Math.Max(0, Math.Min(100, value)))
                    .DistinctUntilChanged()
                    .Subscribe(value =>
                    {
                        MiniCamCommands.SetLEDBrightness(controls, 255 - value * 255 / 100);
                        recordingMetadata.led0 = value;
                    }));

            subscriptions.Add(
                fpsSubject
                    .StartWith(FPS)
                    .DistinctUntilChanged()
                    .Subscribe(value =>
                    {
                        if (value < 10 || value > 30)
                            throw new ArgumentOutOfRangeException(nameof(FPS), "FPS must be between 10 and 30.");

                        MiniCamCommands.SetFPS(controls, value, deviceId);
                        recordingMetadata.frameRate = value;
                    }));

            subscriptions.Add(
                gainSubject
                    .StartWith(Gain)
                    .DistinctUntilChanged()
                    .Subscribe(value =>
                    {
                        MiniCamCommands.SetGain(controls, (int)value);
                        MiniCamConfigService.UpdateGain(deviceId, (int)value);
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
        /// Generates the Open Ephys firmware UCLA MiniCam data sequence.
        /// </summary>
        public override IObservable<FrameMiniCam> Generate()
        {
            return source;
        }
    }
}
