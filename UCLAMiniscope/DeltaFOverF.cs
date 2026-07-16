// SPDX-FileCopyrightText: 2025-2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

using OpenCV.Net;
using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using Bonsai;

namespace UCLAMiniscope
{
    /// <summary>
    /// Divides each frame by a rolling baseline accumulated over a configurable number of frames,
    /// updating at a specified frequency to produce a ΔF/F normalized output.
    /// </summary>
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

        /// <summary>
        /// Gets or sets the number of frames accumulated into the rolling baseline (1–1024).
        /// </summary>
        [Description("Number of frames to hold as reference.")]
        [Range(1, 1024)]
        public int BufferCapacity { get; set; } = 64;

        /// <summary>
        /// Gets or sets how many frames must pass between baseline updates.
        /// </summary>
        [Description("Number of frames before updating the baseline.")]
        public int UpdateFrequency { get; set; } = 1;

        /// <summary>
        /// Gets or sets the scalar gain applied to the normalized ΔF/F image before output (0.0–15.0).
        /// </summary>
        [Description("Gain applied to the normalized image.")]
        [Range(0.0, 15.0)]
        public double Gain { get; set; } = 10.0;

        /// <summary>
        /// Gets or sets a value indicating whether the ΔF/F normalization is active.
        /// When <see langword="false"/>, frames are passed through unchanged.
        /// </summary>
        [Description("Enable or disable the delta F over F normalization.")]
        public bool Enabled { get; set; } = true;


        /// <summary>
        /// Initializes a new instance of the <see cref="DeltaFOverF"/> class with default settings.
        /// </summary>
        public DeltaFOverF()
        {
        }

        /// <summary>
        /// Applies ΔF/F normalization to each frame in the source sequence using a rolling baseline.
        /// </summary>
        /// <param name="source">The source sequence of <see cref="IplImage"/> frames to normalize.</param>
        /// <returns>
        /// An observable sequence of normalized <see cref="IplImage"/> frames, or the original frames
        /// when <see cref="Enabled"/> is <see langword="false"/>.
        /// </returns>
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
                    CV.ConvertScale(ratio, ratio, 1.0, -0.98); // subtract 1 and add 0.02
                    CV.ConvertScale(ratio, result, 256.0 * Gain, 0.0); // multiply by 256 and squeeze to 8-bit
                }

                return result;
            });
        }
    }
}
