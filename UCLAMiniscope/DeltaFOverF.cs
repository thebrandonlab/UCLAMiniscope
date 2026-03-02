//
// DeltaFOverF.cs
//
// Description:
//   This class defines a Bonsai transform node that normalizes each input frame by a baseline computed over a 
//   configurable number of frames. The baseline is updated at a specified frequency, allowing the normalization 
//   to adapt to changes over time.
//
// Usage:
//   - The `BufferCapacity` property defines the number of frames to be used for calculating the baseline.
//   - The `UpdateFrequency` property specifies how often the baseline is updated, in number of frames.
//   - The node processes each incoming frame, normalizes it against the baseline, and outputs the ratioing 
//     normalized frame.
//
// Author:
//   Clément Bourguignon
//   Brandon Lab @ McGill University
//   2025
//

using OpenCV.Net;
using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using Bonsai;

namespace UCLAMiniscope
{
    [Description("Divides each frame by a baseline accumulated over a configurable number of frames, updating at a specified frequency.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class DeltaFOverF : Transform<IplImage, IplImage>
    {
        private IplImage baseline;
        private IplImage[] buffer;
        private int index = 0;
        private int frameCounter = 0;
        private int previousCapacity = 0;
        private DateTime t = DateTime.UtcNow;
        private TimeSpan pauseDuration = TimeSpan.FromSeconds(1);

        [Description("Number of frames to hold as reference.")]
        [Range(1, 1024)]
        public int BufferCapacity { get; set; } = 128;

        [Description("Number of frames before updating the baseline.")]
        public int UpdateFrequency { get; set; } = 10;

        [Description("Gain applied to the normalized image.")]
        [Range(0.0, 15.0)]
        public double Gain { get; set; } = 10.0;

        [Description("Enable or disable the delta F over F normalization.")]
        public bool Enabled { get; set; } = true;



        public DeltaFOverF()
        {
        }

        public override IObservable<IplImage> Process(IObservable<IplImage> source)
        {
            return source.Select(input =>
            {
                // If disabled, pass through the input unchanged
                if (!Enabled)
                {
                    return input;
                }

                if (BufferCapacity != previousCapacity)
                {
                    buffer = new IplImage[BufferCapacity];
                    baseline = null; // Reset baseline since buffer size changed
                    index = 0; // Reset index for new buffer
                    previousCapacity = BufferCapacity;
                }

                if (DateTime.UtcNow > t + pauseDuration)
                {
                    // let's reset the buffer after a break
                    baseline = null;
                    index = 0;
                }

                t = DateTime.UtcNow;  // update time

                if (baseline == null)
                {
                    // Initialize the baseline image with proper depth
                    baseline = new IplImage(input.Size, IplDepth.F32, input.Channels);
                    baseline.SetZero();
                }

                if (frameCounter % UpdateFrequency == 0)
                {
                    // Create and fill the tempImage with the normalization factor
                    var tempImage = new IplImage(input.Size, IplDepth.F32, input.Channels);
                    CV.ConvertScale(input, tempImage, 1.0 / BufferCapacity);

                    // Update the baseline image
                    if (index < BufferCapacity)
                    {
                        if (index == 0)
                        {
                            baseline = tempImage.Clone();
                        }
                        else
                        {
                            CV.Add(baseline, tempImage, baseline);
                        }
                    }
                    else
                    {
                        // Rolling buffer: remove the oldest, add the newest
                        CV.Sub(baseline, buffer[index % BufferCapacity], baseline);
                        CV.Add(baseline, tempImage, baseline);
                    }

                    // Store the current tempImage in the buffer
                    buffer[index % BufferCapacity] = tempImage;
                    index++;
                }

                frameCounter++;

                // Create normalized image
                var ratio = new IplImage(input.Size, IplDepth.F32, input.Channels);
                var result = new IplImage(input.Size, IplDepth.U8, 1);

                if (baseline != null)
                {
                    CV.Div(input, baseline, ratio);
                    CV.ConvertScale(ratio, ratio, 1.0, -0.98); // subtract 1 and add 0.02 → -0.98 (scale 1, shift -0.98)
                    CV.ConvertScale(ratio, result, 256.0 * Gain, 0.0); // multiply by 256*10 and squeeze to 8-bit
                }

                return result;
            });
        }
    }
}
