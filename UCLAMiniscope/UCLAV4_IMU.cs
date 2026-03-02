using System;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Numerics;
using OpenCvSharp;
using Bonsai;
using UCLAMiniscope.Helpers;

namespace UCLAMiniscope
{
    /// <summary>
    /// Emits quaternion IMU data from a UCLA Miniscope V4 BNO055 sensor at a configurable sample rate.
    /// This allows IMU data to be streamed independently from the video frame rate.
    /// </summary>
    [Description("Emits quaternion IMU data from a UCLA V4 capture stream.")]
    public class UCLAV4_IMU : Source<Vector4>
    {
        // 1 quaterion = 2^14 bits
        const float QuatConvFactor = 1.0f / (1 << 14);

        /// <summary>
        /// Gets or sets the device index of the V4 miniscope to read IMU data from.
        /// Must match the CameraIndex of the corresponding UCLAV4 source.
        /// </summary>
        [Description("Index of the V4 miniscope to get IMU data from. Must match the CameraIndex of the UCLAV4_Frame source.")]
        public int DeviceIndex { get; set; } = 0;

        /// <summary>
        /// Gets or sets the IMU sampling rate in Hz.
        /// Can be faster than the frame rate to allow independent IMU streaming.
        /// </summary>
        [Description("IMU sampling rate in Hz (faster than frame rate allows independent IMU streaming)")]
        public int SampleRate { get; set; } = 100;

        /// <summary>
        /// Gets or sets whether to wait for the capture device to be registered before starting IMU streaming.
        /// </summary>
        [Description("Wait for the capture device to be registered before starting IMU streaming")]
        public bool WaitForCapture { get; set; } = true;

        /// <summary>
        /// Generates an observable sequence of quaternion IMU data.
        /// </summary>
        /// <returns>An observable sequence of <see cref="Vector4"/> quaternions representing orientation.</returns>
        public override IObservable<Vector4> Generate()
        {
            return Observable.Create<Vector4>((observer, cancellationToken) =>
            {
                return Task.Run(() =>
                {
                    string deviceId = $"V4_{DeviceIndex}";
                    Vector4 q = new(0, 0, 0, 0);
                    int sleepMs = 1000 / SampleRate;
                    VideoCapture capture = null;

                    // Wait for the capture device to be registered
                    if (WaitForCapture)
                    {
                        while (!cancellationToken.IsCancellationRequested && capture == null)
                        {
                            var captureInfo = CaptureService.GetCapture(deviceId);
                            if (captureInfo != null)
                            {
                                capture = captureInfo.Capture;
                                Console.WriteLine($"[UCLAV4_IMU] Found capture device {deviceId}");
                            }
                            else
                            {
                                Thread.Sleep(100); // Wait 100ms before checking again
                            }
                        }
                    }
                    else
                    {
                        var captureInfo = CaptureService.GetCapture(deviceId);
                        capture = captureInfo?.Capture;
                    }

                    if (capture == null && !WaitForCapture)
                    {
                        observer.OnError(new InvalidOperationException($"Capture device {deviceId} not found. Make sure UCLAV4_Frame is running with matching DeviceIndex."));
                        return;
                    }

                    // Main IMU streaming loop
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        if (capture != null)
                        {
                            // Read IMU data directly from capture registers
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

                            observer.OnNext(q);
                        }

                        Thread.Sleep(sleepMs);
                    }
                }, cancellationToken);
            });
        }
    }
}