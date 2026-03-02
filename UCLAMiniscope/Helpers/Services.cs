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

    public static class CaptureService
    {
        private static readonly object lockObject = new object();
        private static readonly Dictionary<string, CaptureInfo> captures = new Dictionary<string, CaptureInfo>();

        public class CaptureInfo
        {
            public VideoCapture Capture { get; set; }
            public int FrameOffset { get; set; }
            public string DeviceType { get; set; } // "V4" or "MiniCam"
        }

        /// <summary>
        /// Registers a capture device with the service.
        /// </summary>
        /// <param name="deviceId">Unique identifier for the device (e.g., "V4_0", "MiniCam_1")</param>
        /// <param name="capture">The VideoCapture instance</param>
        /// <param name="deviceType">Type of device ("V4" or "MiniCam")</param>
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
        public static Dictionary<string, CaptureInfo> GetAllCaptures()
        {
            lock (lockObject)
            {
                return new Dictionary<string, CaptureInfo>(captures);
            }
        }

        /// <summary>
        /// Gets a specific capture device.
        /// </summary>
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

    public static class RecordingService
    {
        public static bool IsRecording = false;
        public static string Date;
        public static string Time;
    }

    // ===================================================================
    // TIMING SERVICE
    // ===================================================================

    public static class TimingService
    {
        public static Stopwatch Stopwatch { get; set; }
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
        /// Returns true if initialization was performed, false if already initialized.
        /// </summary>
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
        /// Resets the timing service state when a source stops.
        /// Only fully resets when all sources that initialized have stopped.
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

    public static class MouseInfoService
    {
        public static string MouseID { get; set; } = "Mouse01";
        public static string RootPath { get; set; } = "";
    }

    // ===================================================================
    // RECORDING METADATA SERVICE & MODELS
    // ===================================================================

    public static class RecordingMetadataService
    {
        public static RecordingMetadata Metadata { get; set; } = new RecordingMetadata();
    }

    public static class DeviceMetadataRegistry
    {
        private static readonly object lockObject = new object();
        private static readonly Dictionary<string, RecordingMetadata> registry = new Dictionary<string, RecordingMetadata>();

        public static void Register(string deviceId, RecordingMetadata metadata)
        {
            lock (lockObject) registry[deviceId] = metadata;
        }

        public static void Unregister(string deviceId)
        {
            lock (lockObject) registry.Remove(deviceId);
        }

        public static Dictionary<string, RecordingMetadata> GetAll()
        {
            lock (lockObject) return new Dictionary<string, RecordingMetadata>(registry);
        }
    }

    public class RecordingMetadata
    {
#pragma warning disable IDE1006 // Naming Styles, we want the json lowercase style
        public Roi ROI { get; set; }
        public int deviceID { get; set; }
        public string deviceName { get; set; } = "Miniscope";
        public string deviceType { get; set; } = "Miniscope_V4_BNO";
        public int ewl { get; set; }
        public int frameRate { get; set; }
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public GainV4 gain { get; set; }
        public int led0 { get; set; }
#pragma warning restore IDE1006 // Naming Styles    
    }

    public class Roi
    {
#pragma warning disable IDE1006 // Naming Styles
        public int height { get; set; } = 608;
        public int leftEdge { get; set; } = 0;
        public int topEdge { get; set; } = 0;
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
        /// <summary>Holds the sensor configuration and current state for a registered device.</summary>
        public class SensorConfig
        {
            // MT9P031 effective pixel clock after PLL.
            // For MiniCam: 48MHz EXTCLK × PLL×2 = 96MHz. Verify empirically if FPS accuracy is critical.
            public double PixelClockHz { get; set; } = 96_000_000.0;

            // Geometry registers — updated by SetResolution
            public int ColumnSizeReg { get; set; }          // Column_Size register (size - 1)
            public int RowSizeReg { get; set; }             // Row_Size register (size - 1)
            public int ColumnSkipReg { get; set; }          // Column_Skip register (0=1x, 1=2x, 3=4x)
            public int RowSkipReg { get; set; }             // Row_Skip register
            public int RowBinReg { get; set; }              // Row_Bin register (0=1x, 1=2x, 3=4x)
            public int HorizontalBlankReg { get; set; }     // Horizontal_Blank register (sensor default: 0)
            public int Wdc { get; set; }                    // WDC correction factor (default: 0)

            // Tracked state
            public int OutputWidth { get; set; }
            public int OutputHeight { get; set; }
            public int BinningFactor { get; set; }          // physical binning (1, 2, 4)
            public int CurrentFps { get; set; }
            public int CurrentGain { get; set; }
        }

        private static readonly object lockObject = new object();
        private static readonly Dictionary<string, SensorConfig> configs = new Dictionary<string, SensorConfig>();

        /// <summary>Registers a device. Call right after CaptureService.RegisterCapture.</summary>
        public static void Register(string deviceId, SensorConfig config)
        {
            lock (lockObject) configs[deviceId] = config;
        }

        /// <summary>Removes a device's configuration. Call in the workflow finally block.</summary>
        public static void Unregister(string deviceId)
        {
            lock (lockObject) configs.Remove(deviceId);
        }

        /// <summary>Returns the current configuration for a device, or null if not registered.</summary>
        public static SensorConfig GetConfig(string deviceId)
        {
            lock (lockObject)
                return configs.TryGetValue(deviceId, out var cfg) ? cfg : null;
        }

        /// <summary>
        /// Updates sensor geometry registers after a SetResolution call.
        /// All register values must match what was written to the sensor.
        /// </summary>
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

        /// <summary>Records the currently programmed FPS for a device.</summary>
        public static void UpdateFps(string deviceId, int fps)
        {
            lock (lockObject)
            {
                if (configs.TryGetValue(deviceId, out var cfg))
                    cfg.CurrentFps = fps;
            }
        }

        /// <summary>Records the currently programmed gain for a device.</summary>
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
