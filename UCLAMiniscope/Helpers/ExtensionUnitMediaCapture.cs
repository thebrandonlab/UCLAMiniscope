// SPDX-FileCopyrightText: 2024 Open Ephys and Contributors
// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

// Protocol portions adapted from Open Ephys bonsai-miniscope.
// See THIRD_PARTY_NOTICES.md in the package root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;

namespace UCLAMiniscope.Helpers
{
    /// <summary>
    /// Acquires raw YUY2 frames from a UCLA DAQ exposing the Open Ephys UVC extension-unit protocol.
    /// </summary>
    /// <remarks>
    /// The supported USB identities and MediaCapture protocol are adapted from the MIT-licensed
    /// Open Ephys bonsai-miniscope implementation. Frames are exposed synchronously from the Windows
    /// frame-arrival callback and remain valid only for the duration of that callback.
    /// </remarks>
    internal sealed class ExtensionUnitMediaCapture : IDisposable
    {
        const string UvcVendorId = "04B4";
        const string UvcProductIdUsb2 = "00F8";
        const string UvcProductIdUsb3 = "00F9";

        readonly MediaCapture capture;
        readonly ManualResetEventSlim stopped = new ManualResetEventSlim();

        MediaFrameReader frameReader;
        CancellationToken cancellationToken;
        Exception completionError;
        int stopping;
        bool readerStarted;

        ExtensionUnitMediaCapture(
            MediaCapture capture,
            ExtensionUnitDeviceControls deviceControls,
            Version firmwareVersion)
        {
            this.capture = capture;
            DeviceControls = deviceControls;
            FirmwareVersion = firmwareVersion;
        }

        public ExtensionUnitDeviceControls DeviceControls { get; }

        public Version FirmwareVersion { get; }

        public event Action<ExtensionUnitRawFrame> FrameArrived;

        public static ExtensionUnitMediaCapture Create(int deviceIndex)
        {
            return CreateAsync(deviceIndex).GetAwaiter().GetResult();
        }

        static async Task<ExtensionUnitMediaCapture> CreateAsync(int deviceIndex)
        {
            var devices = await GetConnectedDaqsAsync();
            if (deviceIndex < 0 || deviceIndex >= devices.Count)
            {
                throw new NoMiniscopeException(
                    $"Open Ephys firmware UCLA DAQ index {deviceIndex} was not found. " +
                    $"Detected compatible DAQs: {devices.Count}.");
            }

            var mediaCapture = new MediaCapture();
            try
            {
                var settings = new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = devices[deviceIndex].Id,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    SharingMode = MediaCaptureSharingMode.ExclusiveControl
                };

                await mediaCapture.InitializeAsync(settings);

                var extensionUnit = new MediaCaptureUvcExtensionUnit(mediaCapture.VideoDeviceController);
                if (!extensionUnit.IsAvailable)
                {
                    throw new NotSupportedException(
                        "The selected DAQ does not expose the Open Ephys firmware extension unit. " +
                        "Use a legacy node for legacy firmware or update the DAQ firmware.");
                }

                var controls = new ExtensionUnitDeviceControls(extensionUnit);
                return new ExtensionUnitMediaCapture(
                    mediaCapture,
                    controls,
                    extensionUnit.FirmwareVersion);
            }
            catch
            {
                mediaCapture.Dispose();
                throw;
            }
        }

        public void Start(
            int width,
            int height,
            FrameAcquisitionMode acquisitionMode,
            CancellationToken cancellationToken)
        {
            StartAsync(width, height, acquisitionMode, cancellationToken).GetAwaiter().GetResult();
        }

        async Task StartAsync(
            int width,
            int height,
            FrameAcquisitionMode acquisitionMode,
            CancellationToken cancellationToken)
        {
            if (frameReader != null) throw new InvalidOperationException("Frame acquisition has already been started.");
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            this.cancellationToken = cancellationToken;
            capture.Failed += OnMediaCaptureFailed;

            var source = capture.FrameSources.Values.FirstOrDefault(candidate =>
                candidate.Info.SourceKind == MediaFrameSourceKind.Color &&
                candidate.SupportedFormats.Any(format => IsRequestedFormat(format, width, height)));

            if (source == null)
                throw new NotSupportedException($"The selected DAQ does not expose a {width}x{height} YUY2 color frame source.");

            var selectedFormat = source.SupportedFormats.First(format => IsRequestedFormat(format, width, height));
            await source.SetFormatAsync(selectedFormat);
            cancellationToken.ThrowIfCancellationRequested();

            frameReader = await capture.CreateFrameReaderAsync(source, "YUY2");
            frameReader.AcquisitionMode = acquisitionMode switch
            {
                FrameAcquisitionMode.Realtime => MediaFrameReaderAcquisitionMode.Realtime,
                FrameAcquisitionMode.Buffered => MediaFrameReaderAcquisitionMode.Buffered,
                _ => throw new ArgumentOutOfRangeException(nameof(acquisitionMode))
            };
            frameReader.FrameArrived += OnFrameArrived;

            var startStatus = await frameReader.StartAsync();
            if (startStatus != MediaFrameReaderStartStatus.Success)
                throw new IOException($"Could not start UCLA DAQ frame acquisition: {startStatus}.");

            readerStarted = true;
        }

        static bool IsRequestedFormat(MediaFrameFormat format, int width, int height)
        {
            return string.Equals(format.Subtype, "YUY2", StringComparison.OrdinalIgnoreCase) &&
                   format.VideoFormat != null &&
                   format.VideoFormat.Width == (uint)width &&
                   format.VideoFormat.Height == (uint)height;
        }

        void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            if (Volatile.Read(ref stopping) != 0 || cancellationToken.IsCancellationRequested) return;

            try
            {
                using (var frame = sender.TryAcquireLatestFrame())
                using (var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap)
                {
                    if (bitmap == null) return;
                    long receiveTimestamp = TimingService.Stopwatch.ElapsedMilliseconds;
                    if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Yuy2)
                        throw new InvalidDataException($"Unexpected UCLA DAQ bitmap format: {bitmap.BitmapPixelFormat}.");

                    using (var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read))
                    using (var reference = buffer.CreateReference())
                    {
                        var byteAccess = (IMemoryBufferByteAccess)reference;
                        byteAccess.GetBuffer(out IntPtr sourcePointer, out uint capacity);

                        var plane = buffer.GetPlaneDescription(0);
                        int rowBytes = checked(bitmap.PixelWidth * 2);
                        long requiredCapacity = (long)plane.StartIndex +
                                                (long)(bitmap.PixelHeight - 1) * plane.Stride +
                                                rowBytes;

                        if (plane.StartIndex < 0 || plane.Stride < rowBytes || requiredCapacity > capacity)
                        {
                            throw new InvalidDataException(
                                $"Invalid YUY2 buffer layout (start {plane.StartIndex}, stride {plane.Stride}, " +
                                $"row bytes {rowBytes}, capacity {capacity}).");
                        }

                        var rawFrame = new ExtensionUnitRawFrame(
                            sourcePointer,
                            checked((int)capacity),
                            bitmap.PixelWidth,
                            bitmap.PixelHeight,
                            plane.StartIndex,
                            plane.Stride,
                            receiveTimestamp);

                        FrameArrived?.Invoke(rawFrame);
                    }
                }
            }
            catch (Exception ex)
            {
                SignalFailure(ex);
            }
        }

        void OnMediaCaptureFailed(MediaCapture sender, MediaCaptureFailedEventArgs args)
        {
            if (Volatile.Read(ref stopping) != 0 || cancellationToken.IsCancellationRequested) return;
            SignalFailure(new IOException($"UCLA DAQ MediaCapture failed: {args.Message}"));
        }

        void SignalFailure(Exception error)
        {
            if (Interlocked.CompareExchange(ref completionError, error, null) == null)
                stopped.Set();
        }

        public void Wait(CancellationToken cancellationToken)
        {
            stopped.Wait(cancellationToken);
            var error = Volatile.Read(ref completionError);
            if (error != null)
                ExceptionDispatchInfo.Capture(error).Throw();
        }

        public void Stop()
        {
            StopAsync().GetAwaiter().GetResult();
        }

        async Task StopAsync()
        {
            if (Interlocked.Exchange(ref stopping, 1) != 0) return;

            capture.Failed -= OnMediaCaptureFailed;
            if (frameReader != null)
            {
                frameReader.FrameArrived -= OnFrameArrived;
                if (readerStarted)
                {
                    try
                    {
                        await frameReader.StopAsync();
                    }
                    catch (Exception)
                    {
                        // The device may already be gone during disconnect cleanup.
                    }
                }

                frameReader.Dispose();
                frameReader = null;
            }

            stopped.Set();
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref stopping, 1);
            capture.Failed -= OnMediaCaptureFailed;
            if (frameReader != null)
            {
                frameReader.FrameArrived -= OnFrameArrived;
                frameReader.Dispose();
                frameReader = null;
            }

            capture.Dispose();
            stopped.Dispose();
        }

        public static async Task<IReadOnlyList<DeviceInformation>> GetConnectedDaqsAsync()
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            return devices
                .Where(device =>
                    ContainsIdentity(device.Id, $"vid_{UvcVendorId}") &&
                    (ContainsIdentity(device.Id, $"pid_{UvcProductIdUsb2}") ||
                     ContainsIdentity(device.Id, $"pid_{UvcProductIdUsb3}")))
                .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        static bool ContainsIdentity(string value, string identity)
        {
            return value?.IndexOf(identity, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        [ComImport]
        [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMemoryBufferByteAccess
        {
            void GetBuffer(out IntPtr buffer, out uint capacity);
        }
    }

    internal readonly struct ExtensionUnitRawFrame
    {
        public ExtensionUnitRawFrame(
            IntPtr data,
            int dataLength,
            int pixelWidth,
            int pixelHeight,
            int planeStartIndex,
            int rowStride,
            long receiveTimestamp)
        {
            Data = data;
            DataLength = dataLength;
            PixelWidth = pixelWidth;
            PixelHeight = pixelHeight;
            PlaneStartIndex = planeStartIndex;
            RowStride = rowStride;
            ReceiveTimestamp = receiveTimestamp;
        }

        public IntPtr Data { get; }

        public int DataLength { get; }

        public int PixelWidth { get; }

        public int PixelHeight { get; }

        public int PlaneStartIndex { get; }

        public int RowStride { get; }

        public long ReceiveTimestamp { get; }
    }
}
