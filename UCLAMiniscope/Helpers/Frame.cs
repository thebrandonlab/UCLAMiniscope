// Adapted from the work of Jonathan Newman. I merely added OpenCvSharp.Mat support

using Bonsai.Vision.Design;
using OpenCV.Net;
using System;
using System.Numerics;
using System.Reactive.Linq;


namespace UCLAMiniscope.Helpers
{
    /// <summary>
    /// Represents a frame from UCLA Miniscope V4 with IMU data.
    /// </summary>
    public class FrameIMUV4(IplImage image, Vector4 quaternion, ulong frameNumber, long timestamp)
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
        /// Gets the quaternion orientation from the BNO055 IMU.
        /// </summary>
        public Vector4 Quaternion { get; } = quaternion;
        
        /// <summary>
        /// Gets the timestamp in milliseconds.
        /// </summary>
        public long Timestamp { get; } = timestamp;
    }

    /// <summary>
    /// Represents a frame from UCLA Miniscope V4 without IMU data.
    /// </summary>
    public class FrameV4(IplImage image, ulong frameNumber, ulong timestamp)
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
        /// Gets the timestamp in ticks.
        /// </summary>
        public ulong Timestamp { get; } = timestamp;
    }
    
    /// <summary>
    /// Represents a frame from UCLA Miniscope V4 using OpenCvSharp Mat format.
    /// </summary>
    public class FrameV4_Mat(OpenCvSharp.Mat image, Vector4 quaternion, ulong frameNumber, bool trigger, long timestamp)
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
        public Vector4 Quaternion { get; } = quaternion;
        
        /// <summary>
        /// Gets the trigger input state.
        /// </summary>
        public bool Trigger { get; } = trigger;
        
        /// <summary>
        /// Gets the timestamp in milliseconds.
        /// </summary>
        public long Timestamp { get; } = timestamp;
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
    /// Represents a frame from UCLA MiniCam device.
    /// </summary>
    public class FrameMiniCam(IplImage image, ulong frameNumber, long timestamp)
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
        /// Gets the timestamp in milliseconds.
        /// </summary>
        public long Timestamp { get; } = timestamp;
    }

    public class MultiFrameRectangleEditor : IplImageRectangleEditor
    {
        protected override IObservable<IplImage> GetImageSource(IObservable<IObservable<object>> source)
        {
            return source.Merge()
                .Select(value => value switch
                {
                    FrameMiniCam mini => mini.Image,
                    FrameIMUV4 imu => imu.Image,
                    IplImage img => img, // optional: also support raw images
                    _ => null
                })
                .Where(img => img != null);
        }
    }
}