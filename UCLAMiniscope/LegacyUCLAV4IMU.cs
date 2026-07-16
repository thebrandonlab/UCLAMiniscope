// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

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
    /// Emits quaternion IMU data from a UCLA Miniscope V4 BNO055 sensor using legacy DAQ firmware.
    /// This allows IMU data to be streamed independently from the video frame rate.
    /// </summary>
    [Description("Emits quaternion IMU data from a UCLA V4 legacy-firmware capture stream.")]
    public class LegacyUCLAV4IMU : Source<Quaternion>
    {
        /// <summary>
        /// Gets or sets the device index of the V4 miniscope to read IMU data from.
        /// Must match the CameraIndex of the corresponding LegacyUCLAV4 source.
        /// </summary>
        [Description("Index of the V4 miniscope to get IMU data from. Must match the CameraIndex of the LegacyUCLAV4Frame source.")]
        public int DeviceIndex { get; set; } = 0;

        /// <summary>
        /// Gets or sets the position of the U.FL connector relative to the animal.
        /// This must match the companion legacy capture source.
        /// </summary>
        [Description("Position of the U.FL connector relative to the animal. Must match the companion capture source.")]
        public UFLConnectorLocation UFLConnectorLocation { get; set; } = Helpers.UFLConnectorLocation.FrontLeft;

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
        /// <returns>An observable sequence of <see cref="Quaternion"/> values representing orientation.</returns>
        public override IObservable<Quaternion> Generate()
        {
            return Observable.Create<Quaternion>((observer, cancellationToken) =>
            {
                return Task.Run(() =>
                {
                    string deviceId = $"V4_{DeviceIndex}";
                    Quaternion q = new(0, 0, 0, 0);
                    int sleepMs = 1000 / SampleRate;
                    VideoCapture capture = null;
                    IDeviceControls deviceControls = null;

                    // Wait for the capture device to be registered
                    if (WaitForCapture)
                    {
                        while (!cancellationToken.IsCancellationRequested && capture == null)
                        {
                            var captureInfo = CaptureService.GetCapture(deviceId);
                            if (captureInfo?.Capture != null && captureInfo.DeviceControls is IDeviceControls controls)
                            {
                                capture = captureInfo.Capture;
                                deviceControls = controls;
                                Console.WriteLine($"[LegacyUCLAV4IMU] Found capture device {deviceId}");
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
                        deviceControls = captureInfo?.DeviceControls as IDeviceControls;
                    }

                    if ((capture == null || deviceControls == null) && !WaitForCapture)
                    {
                        observer.OnError(new InvalidOperationException($"Compatible legacy capture device {deviceId} not found. Make sure LegacyUCLAV4Frame is running with matching DeviceIndex."));
                        return;
                    }

                    // Main IMU streaming loop
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        if (capture != null)
                        {
                            // Read IMU data directly from capture registers
                            var candidate = QuaternionHelper.Read(capture);
                            bool candidateIsValid = QuaternionHelper.IsValid(candidate, out float candidateNormSquared);

                            // A corrupt or torn read must not replace the last good orientation.
                            if (candidateIsValid)
                            {
                                q = candidate;
                            }

                            // A near-zero norm cannot represent an orientation and indicates that
                            // the BNO055 has stopped after a brief power or coax interruption.
                            if (candidateNormSquared < QuaternionHelper.FailureNormSquaredThreshold)
                            {
                                MiniscopeV4Commands.ReinitializeImu(deviceControls, UFLConnectorLocation);
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
