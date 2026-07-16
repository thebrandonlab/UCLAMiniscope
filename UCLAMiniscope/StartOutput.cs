// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

using Bonsai;
using System;
using System.Linq;
using System.Reactive.Linq;
using UCLAMiniscope.Helpers;

namespace UCLAMiniscope
{
    /// <summary>
    /// Enables the frame-output signal on all registered DAQ devices when the source emits.
    /// </summary>
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
                    // Include temporarily disconnected sources so their requested state is restored on reconnection.
                    var captures = CaptureService.GetAllCaptures();
                    var deviceIds = captures.Keys.Union(DeviceMetadataRegistry.GetAll().Keys);

                    // Initialize each device
                    foreach (string deviceId in deviceIds)
                    {
                        try
                        {
                            // Start Digital Output switching on the DAQ
                            CaptureService.SetFrameOutputEnabled(deviceId, true);
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
