// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

using Bonsai.Vision.Design;
using OpenCV.Net;
using System;
using System.Numerics;
using System.Reactive.Linq;


namespace UCLAMiniscope.Helpers
{
    /// <summary>
    /// Represents a frame from a UCLA Miniscope V4 using current DAQ firmware.
    /// </summary>
    public class FrameIMUV4
    {
        /// <summary>
        /// Initializes a current-firmware V4 frame.
        /// </summary>
        public FrameIMUV4(
            IplImage image,
            Quaternion quaternion,
            ulong frameNumber,
            long receiveTimestamp,
            uint hardwareTimestamp,
            byte digitalInputs)
        {
            Image = image;
            Quaternion = quaternion;
            FrameNumber = frameNumber;
            ReceiveTimestamp = receiveTimestamp;
            HardwareTimestamp = hardwareTimestamp;
            DigitalInputs = (byte)(digitalInputs & 0x03);
        }

        /// <summary>
        /// Gets the grayscale image frame.
        /// </summary>
        public IplImage Image { get; }

        /// <summary>
        /// Gets the hardware frame number from the DAQ.
        /// </summary>
        public ulong FrameNumber { get; }

        /// <summary>
        /// Gets the quaternion orientation from the BNO055 IMU.
        /// </summary>
        public Quaternion Quaternion { get; }

        /// <summary>
        /// Gets the host receive timestamp in milliseconds from the shared recording clock.
        /// </summary>
        public long ReceiveTimestamp { get; }

        /// <summary>
        /// Gets the millisecond hardware timer embedded in the frame by the DAQ firmware.
        /// </summary>
        public uint HardwareTimestamp { get; }

        /// <summary>
        /// Gets both current-firmware digital input bits.
        /// </summary>
        public byte DigitalInputs { get; }

        /// <summary>
        /// Gets digital input 0.
        /// </summary>
        public bool Input0 => (DigitalInputs & 0x01) != 0;

        /// <summary>
        /// Gets digital input 1.
        /// </summary>
        public bool Input1 => (DigitalInputs & 0x02) != 0;
    }

    /// <summary>
    /// Represents a frame from a UCLA Miniscope V4 using legacy DAQ firmware.
    /// </summary>
    public class LegacyFrameIMUV4(
        IplImage image,
        Quaternion quaternion,
        ulong frameNumber,
        long receiveTimestamp,
        bool input)
    {
        /// <summary>Gets the grayscale image frame.</summary>
        public IplImage Image { get; } = image;

        /// <summary>Gets the software-adjusted DAQ frame number.</summary>
        public ulong FrameNumber { get; } = frameNumber;

        /// <summary>Gets the quaternion orientation from the BNO055 IMU.</summary>
        public Quaternion Quaternion { get; } = quaternion;

        /// <summary>Gets the host receive timestamp in milliseconds from the shared recording clock.</summary>
        public long ReceiveTimestamp { get; } = receiveTimestamp;

        /// <summary>Gets the single input exposed by legacy DAQ firmware.</summary>
        public bool Input { get; } = input;
    }

    /// <summary>
    /// Represents a frame from UCLA Miniscope V4 without IMU data using legacy DAQ firmware.
    /// </summary>
    public class LegacyFrameV4(IplImage image, ulong frameNumber, long receiveTimestamp, bool input)
    {
        /// <summary>
        /// Gets the grayscale image frame.
        /// </summary>
        public IplImage Image { get; } = image;

        /// <summary>
        /// Gets the hardware frame number from the DAQ.
        /// </summary>
        public ulong FrameNumber { get; } = frameNumber;

        /// <summary>
        /// Gets the host receive timestamp in milliseconds from the shared recording clock.
        /// </summary>
        public long ReceiveTimestamp { get; } = receiveTimestamp;

        /// <summary>
        /// Gets the input state.
        /// </summary>
        public bool Input { get; } = input;
    }

    /// <summary>
    /// Represents a frame from UCLA Miniscope V4 using OpenCvSharp Mat format.
    /// </summary>
    public class LegacyFrameV4Mat(OpenCvSharp.Mat image, Quaternion quaternion, ulong frameNumber, bool input, long receiveTimestamp)
    {
        /// <summary>
        /// Gets the image frame as OpenCvSharp Mat.
        /// </summary>
        public OpenCvSharp.Mat Image { get; } = image;

        /// <summary>
        /// Gets the hardware frame number from the DAQ.
        /// </summary>
        public ulong FrameNumber { get; } = frameNumber;

        /// <summary>
        /// Gets the quaternion orientation from the BNO055 IMU.
        /// </summary>
        public Quaternion Quaternion { get; } = quaternion;

        /// <summary>
        /// Gets the input state.
        /// </summary>
        public bool Input { get; } = input;

        /// <summary>
        /// Gets the timestamp in milliseconds.
        /// </summary>
        public long ReceiveTimestamp { get; } = receiveTimestamp;
    }

    /// <summary>
    /// Represents a frame from a behavior camera.
    /// </summary>
    public class BehaviourFrame(IplImage image, long timestamp)
    {
        /// <summary>
        /// Gets the image frame.
        /// </summary>
        public IplImage Image { get; } = image;

        /// <summary>
        /// Gets the timestamp in milliseconds.
        /// </summary>
        public long Timestamp { get; } = timestamp;

    }

    /// <summary>
    /// Represents a frame from a UCLA MiniCam using current DAQ firmware.
    /// </summary>
    public class FrameMiniCam
    {
        /// <summary>
        /// Initializes a current-firmware MiniCam frame.
        /// </summary>
        public FrameMiniCam(
            IplImage image,
            ulong frameNumber,
            long receiveTimestamp,
            uint hardwareTimestamp,
            byte digitalInputs)
        {
            Image = image;
            FrameNumber = frameNumber;
            ReceiveTimestamp = receiveTimestamp;
            HardwareTimestamp = hardwareTimestamp;
            DigitalInputs = (byte)(digitalInputs & 0x03);
        }

        /// <summary>Gets the grayscale image frame.</summary>
        public IplImage Image { get; }

        /// <summary>Gets the software-adjusted DAQ frame number.</summary>
        public ulong FrameNumber { get; }

        /// <summary>Gets the host receive timestamp in milliseconds from the shared recording clock.</summary>
        public long ReceiveTimestamp { get; }

        /// <summary>Gets the millisecond hardware timer embedded in the frame by the DAQ firmware.</summary>
        public uint HardwareTimestamp { get; }

        /// <summary>Gets both current-firmware digital input bits.</summary>
        public byte DigitalInputs { get; }

        /// <summary>Gets digital input 0.</summary>
        public bool Input0 => (DigitalInputs & 0x01) != 0;

        /// <summary>Gets digital input 1.</summary>
        public bool Input1 => (DigitalInputs & 0x02) != 0;
    }

    /// <summary>
    /// Represents a frame from a UCLA MiniCam using legacy DAQ firmware.
    /// </summary>
    public class LegacyFrameMiniCam(IplImage image, ulong frameNumber, long receiveTimestamp, bool input)
    {
        /// <summary>Gets the grayscale image frame.</summary>
        public IplImage Image { get; } = image;

        /// <summary>
        /// Gets the software-adjusted DAQ frame number.
        /// </summary>
        public ulong FrameNumber { get; } = frameNumber;

        /// <summary>
        /// Gets the host receive timestamp in milliseconds from the shared recording clock.
        /// </summary>
        public long ReceiveTimestamp { get; } = receiveTimestamp;

        /// <summary>
        /// Gets the single input exposed by legacy DAQ firmware.
        /// </summary>
        public bool Input { get; } = input;
    }

    /// <summary>
    /// Provides a rectangle editor for raw images and all supported Miniscope frame types.
    /// </summary>
    public class MultiFrameRectangleEditor : IplImageRectangleEditor
    {
        /// <inheritdoc/>
        protected override IObservable<IplImage> GetImageSource(IObservable<IObservable<object>> source)
        {
            return source.Merge()
                .Select(value => value switch
                {
                    FrameMiniCam mini => mini.Image,
                    LegacyFrameMiniCam legacyMini => legacyMini.Image,
                    FrameIMUV4 imu => imu.Image,
                    LegacyFrameIMUV4 legacyImu => legacyImu.Image,
                    LegacyFrameV4 legacy => legacy.Image,
                    IplImage img => img, // optional: also support raw images
                    _ => null
                })
                .Where(img => img != null);
        }
    }
}
