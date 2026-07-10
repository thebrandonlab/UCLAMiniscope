/*
Description:
  Shared services for UCLA Miniscope capture, recording, timing, and metadata management.

Author:
  Clément Bourguignon
  Brandon Lab @ McGill University
  2026

Dependencies:
  - OpenCvSharp

MIT License
Copyright (c) 2026 Clément Bourguignon
*/

using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace UCLAMiniscope.Helpers
{
    // ===================================================================
    // CAPTURE SERVICE
    // ===================================================================

    /// <summary>
    /// Manages the collection of active video capture devices and their frame offsets.
    /// </summary>
    public static class CaptureService
    {
        private static readonly object lockObject = new object();
        private static readonly Dictionary<string, CaptureInfo> captures = new Dictionary<string, CaptureInfo>();

        /// <summary>
        /// Holds metadata about a registered capture device.
        /// </summary>
        public class CaptureInfo
        {
            /// <summary>
            /// Gets or sets the <see cref="VideoCapture"/> instance for this device.
            /// </summary>
            public VideoCapture Capture { get; set; }

            /// <summary>
            /// Gets or sets the running frame offset used to correct hardware frame numbers.
            /// </summary>
            public int FrameOffset { get; set; }

            /// <summary>
            /// Gets or sets the device type identifier (e.g. <c>V4</c> or <c>MiniCam</c>).
            /// </summary>
            public string DeviceType { get; set; }
        }

        /// <summary>
        /// Registers a capture device with the service.
        /// </summary>
        /// <param name="deviceId">Unique identifier for the device (e.g. <c>V4_0</c>, <c>MiniCam_1</c>).</param>
        /// <param name="capture">The <see cref="VideoCapture"/> instance for this device.</param>
        /// <param name="deviceType">Type of device (<c>V4</c> or <c>MiniCam</c>).</param>
        public static void RegisterCapture(string deviceId, VideoCapture capture, string deviceType)
        {
            lock (lockObject)
            {
                captures[deviceId] = new CaptureInfo
                {
                    Capture = capture,
                    FrameOffset = 0,
                    DeviceType = deviceType
                };
            }
        }

        /// <summary>
        /// Unregisters a capture device from the service.
        /// </summary>
        /// <param name="deviceId">The unique identifier of the device to remove.</param>
        public static void UnregisterCapture(string deviceId)
        {
            lock (lockObject)
            {
                captures.Remove(deviceId);
            }
        }

        /// <summary>
        /// Gets all registered capture devices.
        /// </summary>
        /// <returns>A snapshot dictionary mapping device IDs to their <see cref="CaptureInfo"/>.</returns>
        public static Dictionary<string, CaptureInfo> GetAllCaptures()
        {
            lock (lockObject)
            {
                return new Dictionary<string, CaptureInfo>(captures);
            }
        }

        /// <summary>
        /// Gets a specific capture device by its identifier.
        /// </summary>
        /// <param name="deviceId">The unique identifier of the device to retrieve.</param>
        /// <returns>The <see cref="CaptureInfo"/> for the device, or <see langword="null"/> if not registered.</returns>
        public static CaptureInfo GetCapture(string deviceId)
        {
            lock (lockObject)
            {
                return captures.TryGetValue(deviceId, out var info) ? info : null;
            }
        }

        /// <summary>
        /// Updates the frame offset for a specific device.
        /// </summary>
        /// <param name="deviceId">The unique identifier of the device.</param>
        /// <param name="offset">The new frame offset value.</param>
        public static void SetFrameOffset(string deviceId, int offset)
        {
            lock (lockObject)
            {
                if (captures.TryGetValue(deviceId, out var info))
                {
                    info.FrameOffset = offset;
                }
            }
        }

        /// <summary>
        /// Gets the frame offset for a specific device.
        /// </summary>
        /// <param name="deviceId">The unique identifier of the device.</param>
        /// <returns>The current frame offset, or 0 if the device is not registered.</returns>
        public static int GetFrameOffset(string deviceId)
        {
            lock (lockObject)
            {
                return captures.TryGetValue(deviceId, out var info) ? info.FrameOffset : 0;
            }
        }
    }

    // ===================================================================
    // RECORDING SERVICE
    // ===================================================================

    /// <summary>
    /// Provides shared state for recording operations, including recording status and session timestamps.
    /// </summary>
    public static class RecordingService
    {
        /// <summary>
        /// Gets or sets a value indicating whether a recording session is currently active.
        /// </summary>
        public static bool IsRecording = false;

        /// <summary>
        /// Gets or sets the date string for the current recording session.
        /// </summary>
        public static string Date;

        /// <summary>
        /// Gets or sets the time string for the current recording session.
        /// </summary>
        public static string Time;
    }

    // ===================================================================
    // TIMING SERVICE
    // ===================================================================

    /// <summary>
    /// Provides a shared, reference-counted <see cref="System.Diagnostics.Stopwatch"/> used to
    /// produce consistent timestamps across multiple miniscope sources.
    /// </summary>
    public static class TimingService
    {
        /// <summary>
        /// Gets or sets the shared stopwatch used for elapsed-time measurements.
        /// </summary>
        public static Stopwatch Stopwatch { get; set; }

        /// <summary>
        /// Gets or sets the UTC origin tick count recorded at service initialization,
        /// used to convert elapsed stopwatch ticks to wall-clock time.
        /// </summary>
        public static long startTimeUtc = DateTime.UtcNow.Ticks - Stopwatch.GetTimestamp();

        private static int initializationCount = 0;
        private static readonly object lockObject = new object();

        /// <summary>
        /// Indicates whether the timing service has been initialized by a miniscope source.
        /// This prevents multiple sources from resetting the shared stopwatch.
        /// </summary>
        public static bool IsTimingInitialized { get; private set; } = false;

        /// <summary>
        /// Initializes the timing service if not already initialized.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> if the service was already initialized or was just initialized;
        /// subsequent callers increment an internal reference count.
        /// </returns>
        public static bool TryInitialize()
        {
            lock (lockObject)
            {
                if (!IsTimingInitialized)
                {
                    startTimeUtc = DateTime.UtcNow.Ticks - Stopwatch.GetTimestamp();
                    Stopwatch = Stopwatch.StartNew();
                    IsTimingInitialized = true;
                }
                
                initializationCount++;
                return IsTimingInitialized;
            }
        }
        
        /// <summary>
        /// Decrements the reference count and resets the timing service when all sources have stopped.
        /// Safe to call from multiple sources — the stopwatch is only stopped when the last caller releases.
        /// </summary>
        public static void Release()
        {
            lock (lockObject)
            {
                if (initializationCount > 0)
                {
                    initializationCount--;
                }

                // Only reset when all sources have released
                if (initializationCount == 0 && IsTimingInitialized)
                {
                    IsTimingInitialized = false;
                    Stopwatch?.Stop();
                    Stopwatch = null;
                }
            }
        }

        /// <summary>
        /// Force resets the timing service state. Use with caution.
        /// </summary>
        public static void ForceReset()
        {
            lock (lockObject)
            {
                initializationCount = 0;
                IsTimingInitialized = false;
                Stopwatch?.Stop();
                Stopwatch = null;
            }
        }
    }

    // ===================================================================
    // MOUSE INFO SERVICE
    // ===================================================================

    /// <summary>
    /// Stores experiment metadata related to the mouse subject and the recording root path.
    /// </summary>
    public static class MouseInfoService
    {
        /// <summary>
        /// Gets or sets the identifier for the experimental subject (e.g. <c>Mouse01</c>).
        /// </summary>
        public static string MouseID { get; set; } = "Mouse01";

        /// <summary>
        /// Gets or sets the root directory path under which all recording sessions are saved.
        /// </summary>
        public static string RootPath { get; set; } = "";
    }

    // ===================================================================
    // RECORDING METADATA SERVICE & MODELS
    // ===================================================================

    /// <summary>
    /// Provides access to the shared <see cref="RecordingMetadata"/> for the active recording session.
    /// </summary>
    public static class RecordingMetadataService
    {
        /// <summary>
        /// Gets or sets the metadata for the current recording session.
        /// </summary>
        public static RecordingMetadata Metadata { get; set; } = new RecordingMetadata();
    }

    /// <summary>
    /// Manages per-device <see cref="RecordingMetadata"/> for all currently registered capture devices.
    /// </summary>
    public static class DeviceMetadataRegistry
    {
        private static readonly object lockObject = new object();
        private static readonly Dictionary<string, RecordingMetadata> registry = new Dictionary<string, RecordingMetadata>();

        /// <summary>
        /// Associates metadata with a device.
        /// </summary>
        /// <param name="deviceId">The unique identifier of the device.</param>
        /// <param name="metadata">The <see cref="RecordingMetadata"/> to store for this device.</param>
        public static void Register(string deviceId, RecordingMetadata metadata)
        {
            lock (lockObject) registry[deviceId] = metadata;
        }

        /// <summary>
        /// Removes metadata for a device.
        /// </summary>
        /// <param name="deviceId">The unique identifier of the device to remove.</param>
        public static void Unregister(string deviceId)
        {
            lock (lockObject) registry.Remove(deviceId);
        }

        /// <summary>
        /// Returns a snapshot of all registered device metadata.
        /// </summary>
        /// <returns>A dictionary mapping device IDs to their <see cref="RecordingMetadata"/>.</returns>
        public static Dictionary<string, RecordingMetadata> GetAll()
        {
            lock (lockObject) return new Dictionary<string, RecordingMetadata>(registry);
        }
    }

    /// <summary>
    /// Contains device configuration and session parameters for a miniscope recording.
    /// Property names follow lowercase JSON conventions for serialization.
    /// </summary>
    public class RecordingMetadata
    {
#pragma warning disable IDE1006 // Naming Styles, we want the json lowercase style
        /// <summary>Gets or sets the region of interest for this recording.</summary>
        public Roi ROI { get; set; }
        /// <summary>Gets or sets the device index (typically the camera index).</summary>
        public int deviceID { get; set; }
        /// <summary>Gets or sets the display name for the device.</summary>
        public string deviceName { get; set; } = "Miniscope";
        /// <summary>Gets or sets the device type string (e.g. <c>Miniscope_V4_BNO</c>).</summary>
        public string deviceType { get; set; } = "Miniscope_V4_BNO";
        /// <summary>Gets or sets the electrowetting lens (EWL) focus value.</summary>
        public int ewl { get; set; }
        /// <summary>Gets or sets the capture frame rate in frames per second.</summary>
        public int frameRate { get; set; }
        /// <summary>Gets or sets the image sensor gain setting.</summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public GainV4 gain { get; set; }
        /// <summary>Gets or sets the LED brightness value (0–255).</summary>
        public int led0 { get; set; }
#pragma warning restore IDE1006 // Naming Styles    
    }

    /// <summary>
    /// Specifies a rectangular region of interest within an image frame.
    /// Property names follow lowercase JSON conventions for serialization.
    /// </summary>
    public class Roi
    {
#pragma warning disable IDE1006 // Naming Styles
        /// <summary>Gets or sets the height of the region in pixels.</summary>
        public int height { get; set; } = 608;
        /// <summary>Gets or sets the left edge X coordinate of the region in pixels.</summary>
        public int leftEdge { get; set; } = 0;
        /// <summary>Gets or sets the top edge Y coordinate of the region in pixels.</summary>
        public int topEdge { get; set; } = 0;
        /// <summary>Gets or sets the width of the region in pixels.</summary>
        public int width { get; set; } = 608;
#pragma warning restore IDE1006 // Naming Styles    
    }

    // ===================================================================
    // MT9P031 SENSOR CONFIG SERVICE
    // ===================================================================

    /// <summary>
    /// Stores per-device sensor geometry and timing parameters, and computes
    /// register values for FPS control. Compatible with any registered device.
    /// </summary>
    public static class MiniCamConfigService
    {
        /// <summary>
        /// Holds the MT9P031 sensor geometry, timing parameters, and current operational state
        /// for a registered MiniCam device.
        /// </summary>
        public class SensorConfig
        {
            // MT9P031 effective pixel clock after PLL.
            // For MiniCam: 48MHz EXTCLK × PLL×2 = 96MHz. Verify empirically if FPS accuracy is critical.
            /// <summary>Gets or sets the effective pixel clock frequency in Hz (default: 96 MHz for MiniCam).</summary>
            public double PixelClockHz { get; set; } = 96_000_000.0;

            // Geometry registers — updated by SetResolution
            /// <summary>Gets or sets the Column_Size register value (sensor window width − 1).</summary>
            public int ColumnSizeReg { get; set; }
            /// <summary>Gets or sets the Row_Size register value (sensor window height − 1).</summary>
            public int RowSizeReg { get; set; }
            /// <summary>Gets or sets the Column_Skip register value (0 = 1×, 1 = 2×, 3 = 4×).</summary>
            public int ColumnSkipReg { get; set; }
            /// <summary>Gets or sets the Row_Skip register value (0 = 1×, 1 = 2×, 3 = 4×).</summary>
            public int RowSkipReg { get; set; }
            /// <summary>Gets or sets the Row_Bin register value (0 = 1×, 1 = 2×, 3 = 4×).</summary>
            public int RowBinReg { get; set; }
            /// <summary>Gets or sets the Horizontal_Blank register value (sensor default: 0).</summary>
            public int HorizontalBlankReg { get; set; }
            /// <summary>Gets or sets the WDC (Well Depth Correction) factor (default: 0).</summary>
            public int Wdc { get; set; }

            // Tracked state
            /// <summary>Gets or sets the output frame width in pixels.</summary>
            public int OutputWidth { get; set; }
            /// <summary>Gets or sets the output frame height in pixels.</summary>
            public int OutputHeight { get; set; }
            /// <summary>Gets or sets the physical binning factor (1, 2, or 4).</summary>
            public int BinningFactor { get; set; }
            /// <summary>Gets or sets the currently programmed frame rate in FPS.</summary>
            public int CurrentFps { get; set; }
            /// <summary>Gets or sets the currently programmed sensor gain register value.</summary>
            public int CurrentGain { get; set; }
        }

        private static readonly object lockObject = new object();
        private static readonly Dictionary<string, SensorConfig> configs = new Dictionary<string, SensorConfig>();

        /// <summary>
        /// Registers a device configuration. Call right after <see cref="CaptureService.RegisterCapture"/>.
        /// </summary>
        /// <param name="deviceId">The unique identifier of the device.</param>
        /// <param name="config">The <see cref="SensorConfig"/> to associate with the device.</param>
        public static void Register(string deviceId, SensorConfig config)
        {
            lock (lockObject) configs[deviceId] = config;
        }

        /// <summary>
        /// Removes a device's configuration. Call this in the workflow finally block.
        /// </summary>
        /// <param name="deviceId">The unique identifier of the device to remove.</param>
        public static void Unregister(string deviceId)
        {
            lock (lockObject) configs.Remove(deviceId);
        }

        /// <summary>
        /// Returns the current configuration for a device.
        /// </summary>
        /// <param name="deviceId">The unique identifier of the device.</param>
        /// <returns>The <see cref="SensorConfig"/> for the device, or <see langword="null"/> if not registered.</returns>
        public static SensorConfig GetConfig(string deviceId)
        {
            lock (lockObject)
                return configs.TryGetValue(deviceId, out var cfg) ? cfg : null;
        }

        /// <summary>
        /// Updates the stored sensor geometry registers after a SetResolution call.
        /// All register values must match what was written to the hardware.
        /// </summary>
        /// <param name="deviceId">The unique identifier of the device.</param>
        /// <param name="outputWidth">Output frame width in pixels.</param>
        /// <param name="outputHeight">Output frame height in pixels.</param>
        /// <param name="binningFactor">Physical binning factor (1, 2, or 4).</param>
        /// <param name="columnSizeReg">Column_Size register value written to the sensor.</param>
        /// <param name="rowSizeReg">Row_Size register value written to the sensor.</param>
        /// <param name="columnSkipReg">Column_Skip register value written to the sensor.</param>
        /// <param name="rowSkipReg">Row_Skip register value written to the sensor.</param>
        /// <param name="rowBinReg">Row_Bin register value written to the sensor.</param>
        public static void UpdateGeometry(
            string deviceId,
            int outputWidth, int outputHeight,
            int binningFactor,
            int columnSizeReg, int rowSizeReg,
            int columnSkipReg, int rowSkipReg, int rowBinReg)
        {
            lock (lockObject)
            {
                if (!configs.TryGetValue(deviceId, out var cfg)) return;
                cfg.OutputWidth   = outputWidth;
                cfg.OutputHeight  = outputHeight;
                cfg.BinningFactor = binningFactor;
                cfg.ColumnSizeReg = columnSizeReg;
                cfg.RowSizeReg    = rowSizeReg;
                cfg.ColumnSkipReg = columnSkipReg;
                cfg.RowSkipReg    = rowSkipReg;
                cfg.RowBinReg     = rowBinReg;
            }
        }

        /// <summary>
        /// Records the currently programmed frame rate for a device.
        /// </summary>
        /// <param name="deviceId">The unique identifier of the device.</param>
        /// <param name="fps">The frame rate in frames per second.</param>
        public static void UpdateFps(string deviceId, int fps)
        {
            lock (lockObject)
            {
                if (configs.TryGetValue(deviceId, out var cfg))
                    cfg.CurrentFps = fps;
            }
        }

        /// <summary>
        /// Records the currently programmed sensor gain for a device.
        /// </summary>
        /// <param name="deviceId">The unique identifier of the device.</param>
        /// <param name="gain">The gain register value (e.g. 8 = 1×, 16 = 2×, 32 = 4×).</param>
        public static void UpdateGain(string deviceId, int gain)
        {
            lock (lockObject)
            {
                if (configs.TryGetValue(deviceId, out var cfg))
                    cfg.CurrentGain = gain;
            }
        }

        /// <summary>
        /// Computes the shutter width register value that achieves the requested FPS
        /// using the MT9P031 timing model. Returns -1 if the device is not registered.
        /// </summary>
        /// <remarks>
        /// MT9P031 timing (from datasheet):
        ///   W     = 2*ceil((Column_Size + 1) / (2*(Column_Skip + 1)))
        ///   H     = 2*ceil((Row_Size + 1)    / (2*(Row_Skip + 1)))
        ///   HBMIN = 346*(Row_Bin +1 ) + 64 + WDC/2
        ///   totalPixelsPerLine = 2 * max(W/2 + max(HB, HBMIN), 41 + 346*(Row_Bin+1) + 99)
        ///   tFRAME = (SW+1) * totalPixelsPerLine / PixelClockHz   [when SW > H]
        ///   ⟹ SW = round(PixelClockHz / (fps * totalPixelsPerLine)) − 1
        /// </remarks>
        public static int ComputeShutterWidthForFps(string deviceId, int fps)
        {
            if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));

            SensorConfig cfg;
            lock (lockObject)
            {
                if (!configs.TryGetValue(deviceId, out cfg)) return -1;
            }

            int W = 2 * (int)Math.Ceiling((cfg.ColumnSizeReg + 1.0) / (2.0 * (cfg.ColumnSkipReg + 1)));
            int H = 2 * (int)Math.Ceiling((cfg.RowSizeReg    + 1.0) / (2.0 * (cfg.RowSkipReg    + 1)));

            int HB    = cfg.HorizontalBlankReg + 1;
            int HBMIN = 346 * (cfg.RowBinReg + 1) + 64 + (cfg.Wdc / 2);

            double termA           = W / 2.0 + Math.Max(HB, HBMIN);
            double termB           = 41 + 346 * (cfg.RowBinReg + 1) + 99;
            int totalPixelsPerLine = 2 * (int)Math.Ceiling(Math.Max(termA, termB));

            int sw = (int)Math.Round(cfg.PixelClockHz / ((double)fps * totalPixelsPerLine)) - 1;

            // SW must be > H for shutter-controlled frame timing (VBMIN = SW-H+1 dominates)
            return Math.Max(sw, H + 1);
        }
    }
}
