/*
Description:
  Enums for UCLA Miniscope configuration (gains, resolutions, binning).

Author:
  Clément Bourguignon
  Brandon Lab @ McGill University
  2025

MIT License
Copyright (c) 2024 Clément Bourguignon
*/

namespace UCLAMiniscope.Helpers
{
    /// <summary>
    /// Gain settings for UCLA Miniscope V4 image sensor.
    /// </summary>
    public enum GainV4
    {
        /// <summary>
        /// Low gain setting (value: 225).
        /// </summary>
        Low = 225,
        
        /// <summary>
        /// Medium gain setting (value: 228).
        /// </summary>
        Medium = 228,
        
        /// <summary>
        /// High gain setting (value: 36).
        /// </summary>
        High = 36,
    }

    /// <summary>
    /// Gain settings for MiniCam MT9P031 image sensor (0.125 dB steps).
    /// </summary>
    public enum GainMiniCam
    {
        /// <summary>
        /// 1× gain (Dig = 0, Mult = 0, Ana = 8).
        /// </summary>
        X1 = 8,
        
        /// <summary>
        /// 2× gain (Dig = 0, Mult = 0, Ana = 16).
        /// </summary>
        X2 = 16,
        
        /// <summary>
        /// 4× gain (Dig = 0, Mult = 0, Ana = 32).
        /// </summary>
        X4 = 32,
        
        /// <summary>
        /// 8× gain (Dig = 0, Mult = 1, Ana = 32).
        /// </summary>
        X8 = 96,
        
        /// <summary>
        /// 16× gain (Dig = 8, Mult = 1, Ana = 32).
        /// </summary>
        X16 = 2144,
        
        /// <summary>
        /// 32× gain (Dig = 24, Mult = 1, Ana = 32).
        /// </summary>
        X32 = 6240,
    }

    /// <summary>
    /// Supported resolution presets matching DAQ firmware configurations.
    /// </summary>
    public enum ResolutionPreset
    {
        /// <summary>
        /// 608×608 resolution.
        /// </summary>
        R608x608,
        
        /// <summary>
        /// 752×480 resolution.
        /// </summary>
        R752x480,
        
        /// <summary>
        /// 800×800 resolution.
        /// </summary>
        R800x800,
        
        /// <summary>
        /// 1000×1000 resolution.
        /// </summary>
        R1000x1000,
        
        /// <summary>
        /// 1024×768 resolution.
        /// </summary>
        R1024x768,
        
        /// <summary>
        /// 1296×972 resolution.
        /// </summary>
        R1296x972,
        
        /// <summary>
        /// 1500×1500 resolution.
        /// </summary>
        R1500x1500,
        
        /// <summary>
        /// 1800×1800 resolution.
        /// </summary>
        R1800x1800,
        
        /// <summary>
        /// 2592×1944 resolution (maximum).
        /// </summary>
        R2592x1944
    }

    /// <summary>
    /// Binning factor for sensor configuration to increase sensitivity at the cost of resolution.
    /// </summary>
    public enum BinningEnum
    {
        /// <summary>
        /// No binning (1×1).
        /// </summary>
        x1 = 1,
        
        /// <summary>
        /// 2×2 pixel binning.
        /// </summary>
        x2 = 2,
        
        /// <summary>
        /// 4×4 pixel binning.
        /// </summary>
        x4 = 4
    }
}
