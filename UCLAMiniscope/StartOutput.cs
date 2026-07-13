using Bonsai;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using UCLAMiniscope.Helpers;

namespace UCLAMiniscope
{
    public class StartOutput : Combinator
    {
        /// <summary>
        /// Starts the frame output switch for all registered capture devices.
        /// Useful for setting up the minicam and starting the output TTL for recording in another software without control.
        /// </summary>
        /// <returns>The source sequence, unchanged.</returns>

        public override IObservable<TSource> Process<TSource>(IObservable<TSource> source)
        {
            return source
                .Do(_ =>
                {
                    // Get all registered capture devices
                    var captures = CaptureService.GetAllCaptures();

                    // Initialize each device
                    foreach (var kvp in captures)
                    {
                        string deviceId = kvp.Key;
                        var captureInfo = kvp.Value;

                        try
                        {
                            // Start Digital Output switching on the DAQ
                            captureInfo.Capture.Set(VideoCaptureProperties.Saturation, 1);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[StartOutput] Error starting output for {deviceId}: {ex.Message}");
                        }
                    }
                });
            // We don't want to stop the output when the source completes, so no Finally or anything.
        }
    }
}