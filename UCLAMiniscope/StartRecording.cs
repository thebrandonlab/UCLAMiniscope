using Bonsai;
using OpenCvSharp;
using System;
using System.ComponentModel;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using UCLAMiniscope.Helpers;

namespace UCLAMiniscope
{
    /// <summary>
    /// Starts a recording session on the first element of the input sequence.
    /// Resets the recording timer and frame offsets for all connected devices, creates the session folder,
    /// and optionally writes a <c>metadata.json</c> file containing settings for all registered devices.
    /// Passes the input sequence through unchanged.
    /// </summary>
    [Description("Starts a recording session: resets timers, creates the session folder, and passes the input through. Optionally writes metadata.json.")]
    public class StartRecording : Combinator
    {
        /// <summary>
        /// When <see langword="true"/>, writes a <c>metadata.json</c> file to the session root folder
        /// containing the current settings (device type, ID, name, FPS, gain, LED, EWL) for every
        /// registered device at the moment recording starts.
        /// </summary>
        [Description("When true, writes metadata.json to the session folder with settings for all registered devices.")]
        public bool SaveMetadata { get; set; } = false;

        /// <summary>
        /// Passes the source sequence through unchanged, triggering recording initialisation on the first element
        /// and stopping the recording session when the sequence terminates or the workflow is stopped.
        /// </summary>
        /// <typeparam name="TSource">The type of elements in the source sequence.</typeparam>
        /// <param name="source">The source sequence used as the recording trigger.</param>
        /// <returns>The source sequence, unchanged.</returns>
        public override IObservable<TSource> Process<TSource>(IObservable<TSource> source)
        {
            return source
                .Do(_ =>
                {
                    if (!RecordingService.IsRecording)
                    {
                        // Reset the recording timer
                        TimingService.Stopwatch.Restart();

                        // Get all registered capture devices
                        var captures = CaptureService.GetAllCaptures();

                        if (captures.Count == 0)
                        {
                            Console.WriteLine("[StartRecording] Warning: No capture devices registered. Make sure miniscope/camera sources are running.");
                        }
                        else
                        {
                            Console.WriteLine($"[StartRecording] Starting recording for {captures.Count} device(s)");
                        }

                        // Initialize each device
                        foreach (var kvp in captures)
                        {
                            string deviceId = kvp.Key;
                            var captureInfo = kvp.Value;

                            try
                            {
                                // Reset frame offset
                                int currentContrast = (int)captureInfo.Capture.Get(VideoCaptureProperties.Contrast);
                                CaptureService.SetFrameOffset(deviceId, -currentContrast);

                                // Start Digital Output switching on the DAQ
                                captureInfo.Capture.Set(VideoCaptureProperties.Saturation, 1);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[StartRecording] Error initializing {deviceId}: {ex.Message}");
                            }
                        }

                        // Set the recording service fields
                        RecordingService.Date = DateTime.Now.ToString("yyyy_MM_dd");
                        RecordingService.Time = DateTime.Now.ToString("HH_mm_ss");

                        string sessionFolder = Path.Combine(MouseInfoService.RootPath, MouseInfoService.MouseID, RecordingService.Date, RecordingService.Time);
                        Directory.CreateDirectory(sessionFolder);

                        RecordingService.IsRecording = true;

                        Console.WriteLine($"[StartRecording] Recording started at {RecordingService.Date} {RecordingService.Time}");

                        if (SaveMetadata)
                        {
                            string metadataPath = Path.Combine(sessionFolder, "metadata.json");

                            var allMetadata = DeviceMetadataRegistry.GetAll();

                            var json = JsonSerializer.Serialize(allMetadata, new JsonSerializerOptions { WriteIndented = true });
                            File.WriteAllText(metadataPath, json);

                            Console.WriteLine($"[StartRecording] Wrote metadata for {allMetadata.Count} device(s) to {metadataPath}");
                        }
                    }
                })
                .Finally(() =>
                {
                    if (RecordingService.IsRecording)
                    {
                        // Get all registered capture devices
                        var captures = CaptureService.GetAllCaptures();

                        Console.WriteLine($"[StartRecording] Stopping recording for {captures.Count} device(s)");

                        // Stop Digital Output switching on each device
                        foreach (var kvp in captures)
                        {
                            string deviceId = kvp.Key;
                            var captureInfo = kvp.Value;

                            try
                            {
                                // Stop Digital Output switching on the DAQ
                                captureInfo.Capture.Set(VideoCaptureProperties.Saturation, 0);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[StartRecording] Error stopping {deviceId}: {ex.Message}");
                            }
                        }

                        // Set the recording flag to false
                        RecordingService.IsRecording = false;

                        Console.WriteLine("[StartRecording] Recording stopped");
                    }
                });
        }
    }
}
