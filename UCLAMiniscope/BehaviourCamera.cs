// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

using Bonsai;
using OpenCV.Net;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace UCLAMiniscope
{
    /// <summary>
    /// Produces color images from a standard camera exposed through Windows Media Capture.
    /// </summary>
    /// <remarks>
    /// This source demonstrates the Windows Media frame-reader model without any Miniscope-specific
    /// device discovery, controls, metadata decoding, or buffering.
    /// </remarks>
    [Description("Produces color images from a standard Windows camera.")]
    public class BehaviourCamera : Source<IplImage>
    {
        /// <summary>
        /// Gets or sets the index within the list of video capture devices reported by Windows.
        /// </summary>
        [Description("Index of the Windows video capture device to open.")]
        public int CameraIndex { get; set; }

        /// <summary>
        /// Generates an observable sequence of BGR color images.
        /// </summary>
        /// <returns>An observable sequence containing the frames produced by the selected camera.</returns>
        public override IObservable<IplImage> Generate()
        {
            return Observable.Create<IplImage>(async (observer, cancellationToken) =>
            {
                MediaCapture capture = null;
                MediaFrameReader reader = null;
                MediaCaptureFailedEventHandler captureFailed = null;
                TypedEventHandler<MediaFrameReader, MediaFrameArrivedEventArgs> frameArrived = null;
                bool readerStarted = false;
                var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                using (cancellationToken.Register(() => stopped.TrySetCanceled()))
                {
                    try
                    {
                        // MediaCapture represents the camera. Unlike the Miniscope source, this selects
                        // from every video capture device reported by Windows and performs no USB filtering.
                        var devices = (await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture))
                            .OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        if (CameraIndex < 0 || CameraIndex >= devices.Count)
                        {
                            throw new InvalidOperationException(
                                $"Camera index {CameraIndex} was not found. Detected cameras: {devices.Count}.");
                        }

                        capture = new MediaCapture();
                        await capture.InitializeAsync(new MediaCaptureInitializationSettings
                        {
                            VideoDeviceId = devices[CameraIndex].Id,
                            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                            SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                            StreamingCaptureMode = StreamingCaptureMode.Video
                        });

                        captureFailed = (sender, args) => stopped.TrySetException(
                            new IOException($"Windows Media Capture failed: {args.Message}"));
                        capture.Failed += captureFailed;

                        // A camera can expose several streams. For a webcam we only need its color stream.
                        var colorSource = capture.FrameSources.Values.FirstOrDefault(
                            source => source.Info.SourceKind == MediaFrameSourceKind.Color);
                        if (colorSource == null)
                            throw new NotSupportedException("The selected camera does not expose a color frame source.");

                        // MediaFrameReader represents that stream. Asking for BGRA8 lets Windows convert
                        // native webcam formats such as YUY2 or MJPEG before frames reach this node.
                        reader = await capture.CreateFrameReaderAsync(colorSource, MediaEncodingSubtypes.Bgra8);
                        reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;

                        frameArrived = (sender, args) =>
                        {
                            if (cancellationToken.IsCancellationRequested) return;

                            IplImage image = null;
                            try
                            {
                                // This is the Windows Media equivalent of Read(), except Windows calls us
                                // when a frame exists. The frame and bitmap must be released immediately.
                                using (var frame = sender.TryAcquireLatestFrame())
                                using (var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap)
                                {
                                    if (bitmap == null) return;
                                    image = CopyBgraToBgr(bitmap);
                                }

                                observer.OnNext(image);
                                image = null; // Ownership passes to the observable pipeline.
                            }
                            catch (Exception ex)
                            {
                                image?.Dispose();
                                stopped.TrySetException(ex);
                            }
                        };

                        reader.FrameArrived += frameArrived;
                        var startStatus = await reader.StartAsync();
                        if (startStatus != MediaFrameReaderStartStatus.Success)
                            throw new IOException($"Could not start camera acquisition: {startStatus}.");

                        readerStarted = true;
                        await stopped.Task;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Disposing the Bonsai subscription is the normal way to stop this source.
                    }
                    catch (Exception ex)
                    {
                        observer.OnError(ex);
                    }
                    finally
                    {
                        if (reader != null)
                        {
                            if (frameArrived != null)
                                reader.FrameArrived -= frameArrived;

                            if (readerStarted)
                            {
                                try
                                {
                                    await reader.StopAsync();
                                }
                                catch
                                {
                                    // The camera may already be unavailable during shutdown.
                                }
                            }

                            reader.Dispose();
                        }

                        if (capture != null)
                        {
                            if (captureFailed != null)
                                capture.Failed -= captureFailed;
                            capture.Dispose();
                        }
                    }
                }
            });
        }

        static unsafe IplImage CopyBgraToBgr(SoftwareBitmap bitmap)
        {
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                throw new InvalidDataException($"Unexpected camera bitmap format: {bitmap.BitmapPixelFormat}.");

            using (var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read))
            using (var reference = buffer.CreateReference())
            {
                var byteAccess = (IMemoryBufferByteAccess)reference;
                byteAccess.GetBuffer(out IntPtr sourcePointer, out uint capacity);

                var plane = buffer.GetPlaneDescription(0);
                int width = bitmap.PixelWidth;
                int height = bitmap.PixelHeight;
                int sourceRowBytes = checked(width * 4);
                long requiredCapacity = (long)plane.StartIndex +
                                        (long)(height - 1) * plane.Stride +
                                        sourceRowBytes;

                if (plane.StartIndex < 0 || plane.Stride < sourceRowBytes || requiredCapacity > capacity)
                    throw new InvalidDataException("The camera bitmap buffer has an invalid BGRA layout.");

                var image = new IplImage(new OpenCV.Net.Size(width, height), IplDepth.U8, 3);
                try
                {
                    byte* source = (byte*)sourcePointer.ToPointer() + plane.StartIndex;
                    byte* destination = (byte*)image.ImageData.ToPointer();

                    for (int row = 0; row < height; row++)
                    {
                        byte* sourceRow = source + row * plane.Stride;
                        byte* destinationRow = destination + row * image.WidthStep;
                        for (int column = 0; column < width; column++)
                        {
                            destinationRow[column * 3] = sourceRow[column * 4];
                            destinationRow[column * 3 + 1] = sourceRow[column * 4 + 1];
                            destinationRow[column * 3 + 2] = sourceRow[column * 4 + 2];
                        }
                    }

                    return image;
                }
                catch
                {
                    image.Dispose();
                    throw;
                }
            }
        }

        [ComImport]
        [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMemoryBufferByteAccess
        {
            void GetBuffer(out IntPtr buffer, out uint capacity);
        }
    }
}
