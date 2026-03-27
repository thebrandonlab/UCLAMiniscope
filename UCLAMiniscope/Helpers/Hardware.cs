/*
Description:
  Hardware communication helpers for UCLA Miniscope V4 and MiniCam devices.
  Provides I2C communication and sensor configuration for different miniscope hardware.

===================================================================
UCLA DAQ v3.3 I2C COMMUNICATION PROTOCOL
===================================================================
Magik configuration - I2C Address Map
8-bit      I2C adr.        Description
--------------------------------------------
0xC0       0b1100000       Deserializer
0xB0       0b1011000       Serializer
0xA0       0b1010000       TPL0102 Digital potentiometer (V4)
0x58       0b0101100       TPL0102 Digital potentiometer (sudo) (V4)
0x50       0b0101000       BNO055 (V4)
0xEE       0b1110111       MAX14574 EWL driver (V4)
0x20       0b0010000       ATTINY MCU (V4)
0xBA       0b1011101       MT9P031 Sensor (minicam)
0x6C       0b0110110       LM3509 LED driver (minicam)
---------------------------------------------
0xFE       ---------       DAQ's USB controller (no I2C passthrough)
---------------------------------------------
NB: The 7-bit I2C addresses are given for reference only
====================================================================

DAQ commands:
    - 0x00: Enable BNO streaming
    - 0x01: Hard Reset DAQ
    - 0x02: Enable Input Trigger streaming to serializer (Modified Firmware only)
    - 0x03: Disable Input Trigger streaming (Modified Firmware only)

Author:
  Clément Bourguignon
  Brandon Lab @ McGill University
  2026

MIT License
Copyright (c) 2026 Clément Bourguignon
*/

using OpenCvSharp;
using System;
using System.Threading;

namespace UCLAMiniscope.Helpers
{
    /// <summary>
    /// Provides hardware communication and configuration for UCLA Miniscope V4 and MiniCam devices.
    /// Handles I2C communication protocol and sensor setup for different miniscope hardware variants.
    /// </summary>
    public static class Hardware
    {
        /// <summary>
        /// Sends I2C commands to the miniscope hardware via the VideoCapture interface.
        /// </summary>
        /// <param name="capture">The VideoCapture instance for the miniscope device.</param>
        /// <param name="address">The 8-bit I2C address of the target device.</param>
        /// <param name="values">The data bytes to send (maximum 5 bytes).</param>
        /// <exception cref="ArgumentException">Thrown when more than 5 data bytes are provided.</exception>
        internal static void SendI2C(VideoCapture capture, byte address, params byte[] values)
        {
            if (values.Length > 5)
                throw new ArgumentException(string.Format("{0} has more than 5 elements", values), nameof(values));

            ulong packet = address;

            if (values.Length == 5)
            {
                packet |= 0x01; // address with bottom bit flipped to 1 to indicate a full 6 byte package
                for (int i = 0; i < values.Length; i++)
                    packet |= (ulong)values[i] << 8 * (1 + i); // add each byte
            }
            else
            {
                packet |= (ulong)(values.Length + 1) << 8; // address and data length
                for (int i = 0; i < values.Length; i++)
                    packet |= (ulong)values[i] << 8 * (2 + i); // add each byte
            }

            capture.Set(VideoCaptureProperties.Contrast, packet & 0x00000000FFFF);
            capture.Set(VideoCaptureProperties.Gamma, (packet & 0x0000FFFF0000) >> 16);
            capture.Set(VideoCaptureProperties.Sharpness, (packet & 0xFFFF00000000) >> 32);
        }

        /// <summary>
        /// Converts a ResolutionPreset enum to width and height dimensions.
        /// </summary>
        /// <param name="preset">The resolution preset to convert.</param>
        /// <returns>A tuple containing the width and height in pixels.</returns>
        internal static (int width, int height) GetResolutionDimensions(ResolutionPreset preset)
        {
            return preset switch
            {
                ResolutionPreset.R608x608 => (608, 608),
                ResolutionPreset.R752x480 => (752, 480),
                ResolutionPreset.R800x800 => (800, 800),
                ResolutionPreset.R1000x1000 => (1000, 1000),
                ResolutionPreset.R1024x768 => (1024, 768),
                ResolutionPreset.R1296x972 => (1296, 972),
                ResolutionPreset.R1500x1500 => (1500, 1500),
                ResolutionPreset.R1800x1800 => (1800, 1800),
                ResolutionPreset.R2592x1944 => (2592, 1944),
                _ => (1024, 768) // Default fallback
            };
        }

        // ===================================================================
        // MINISCOPE V4 HARDWARE
        // ===================================================================

        /// <summary>
        /// Hardware configuration methods for UCLA Miniscope V4 devices.
        /// Provides initialization and control for the Python480 sensor, BNO055 IMU, and LED drivers.
        /// </summary>
        public static class V4
        {
            /// <summary>
            /// Initializes the UCLA Miniscope V4 hardware including SERDES, BNO055 IMU, and EWL driver.
            /// </summary>
            /// <param name="capture">The VideoCapture instance for the V4 device.</param>
            internal static void Initialize(VideoCapture capture)
            {
                // Configure SERDES
                SendI2C(capture, 0xC0, 0x1F, 0b00010000); // Set deserializer to MODE override and 12bit low frequency
                SendI2C(capture, 0xB0, 0x05, 0b00100000); // Set serializer to MODE override and 12bit low frequency
                SendI2C(capture, 0xC0, 0x22, 0b00000010); // Speed up i2c bus timer to 50us max
                SendI2C(capture, 0xC0, 0x20, 5 << 1); // Enable watchdog and decrease BCC timeout to 10ms (units in 2ms)
                SendI2C(capture, 0xC0, 0x07, 0xB0); // Make sure DES has SER ADDRing
                SendI2C(capture, 0xB0, 0x0F, 0b00000010); // Speed up I2c bus timer to 50u Max
                SendI2C(capture, 0xB0, 0x1E, 5 << 1); // Enable watchdog and decrease BCC timeout to 10ms (units in 2ms)
                SendI2C(capture, 0xC0, 0x08, 0x20, 0xEE, 0xA0, 0x50); // set i2c addresses to send through serializer
                SendI2C(capture, 0xC0, 0x10, 0x20, 0xEE, 0x58, 0x50); // set sudo i2c addresses to send through serializer

                // Configure BNO055 IMU
                SendI2C(capture, 0x50, 0x41, 0b00001001, 0b00000101); // Remap BNO axes and signs
                SendI2C(capture, 0x50, 0x3D, 0b00001100); // Set BNO operation mode to NDOF
                SendI2C(capture, 0xFE, 0x00, 0x00); // Enable BNO streaming in DAQ
                SendI2C(capture, 0xEE, 0x03, 0x03); // Enable EWL Driver
            }

            /// <summary>
            /// Sets the frame rate for the V4 miniscope by configuring the Python480 sensor exposure time.
            /// </summary>
            /// <param name="capture">The VideoCapture instance for the V4 device.</param>
            /// <param name="FPS">The desired frame rate in frames per second.</param>
            internal static void SetFPS(VideoCapture capture, int FPS)
            {
                // We get the framerate by setting exposure time in 10-ns, encoded in two bytes (int16)
                // 30 FPS is 0.33...s so 3333 * 10ns. In hex 0x0D05 > v0 = 0x0D and v1 = 0x05
                uint exposureTime = (uint)(1.0 / FPS * 100000.0); // Convert FPS to the exposure time unit

                // Convert the integer value to bytes
                byte v0 = (byte)((exposureTime & 0x0000FF00) >> 8);
                byte v1 = (byte)(exposureTime & 0x000000FF);

                SendI2C(capture, 0x20, 0x05, 0x00, 0xC9, v0, v1); // ATTINY > PYTHON480 > exposure reg
            }

            /// <summary>
            /// Sets the sensor gain for the V4 miniscope.
            /// </summary>
            /// <param name="capture">The VideoCapture instance for the V4 device.</param>
            /// <param name="gain">The desired gain level.</param>
            internal static void SetGain(VideoCapture capture, GainV4 gain)
            {
                SendI2C(capture, 0x20, 0x05, 0x00, 0xCC, 0x00, (byte)gain); // ATTINY > PYTHON480 > gain reg
            }

            /// <summary>
            /// Adjusts the focus position of the V4 miniscope's EWL (Electrowetting Lens).
            /// </summary>
            /// <param name="capture">The VideoCapture instance for the V4 device.</param>
            /// <param name="focus">The focus adjustment value (centered at 0).</param>
            internal static void SetFocus(VideoCapture capture, int focus)
            {
                SendI2C(capture, 0xEE, 0x08, (byte)(127 + focus), 0x02);
            }

            /// <summary>
            /// Sets the LED brightness for the V4 miniscope illumination.
            /// </summary>
            /// <param name="capture">The VideoCapture instance for the V4 device.</param>
            /// <param name="brightness">The brightness value (0-255).</param>
            internal static void SetLEDBrightness(VideoCapture capture, int brightness)
            {
                SendI2C(capture, 0x20, 0x01, (byte)brightness);
                SendI2C(capture, 0x58, 0x00, 0x72, (byte)brightness);
            }

            /// <summary>
            /// Enables or disables external trigger mode for the V4 miniscope.
            /// </summary>
            /// <param name="capture">The VideoCapture instance for the V4 device.</param>
            /// <param name="triggered">True to enable triggered mode, false for continuous acquisition.</param>
            internal static void SetTriggerMode(VideoCapture capture, bool triggered)
            {
                SendI2C(capture, 0x20, 0x01, triggered ? (byte)1 : (byte)0);
            }
        }

        // ===================================================================
        // MINICAM HARDWARE
        // ===================================================================

        /// <summary>
        /// Hardware configuration methods for UCLA MiniCam devices with MT9P031 sensor.
        /// Provides initialization and control for the MT9P031 CMOS sensor and LM3509 LED driver.
        /// </summary>
        public static class MiniCam
        {
            // MT9P031 active sensor dimensions
            private const int SENSOR_ACTIVE_WIDTH = 2592;
            private const int SENSOR_ACTIVE_HEIGHT = 1944;
            private const int SENSOR_ACTIVE_COL_START = 16;
            private const int SENSOR_ACTIVE_ROW_START = 54;

            // 48 MHz EXTCLK input to the MT9P031 PLL.
            // Actual PIXCLK used in timing formulas is 96 MHz (PLL×2) — stored in MiniCamConfigService.
            private const double EXTCLK_HZ = 48_000_000.0;


            /// <summary>
            /// Initializes the MiniCam hardware including SERDES, MT9P031 sensor, and LM3509 LED driver.
            /// Configures default settings: 1024×768 resolution with 2×2 binning from 2048×1536 sensor window.
            /// </summary>
            /// <param name="capture">The VideoCapture instance for the MiniCam device.</param>
            internal static void Initialize(VideoCapture capture)
            {
                // Configures SERDES
                SendI2C(capture, 0xC0, 0x07, 0xB0); // Provide deserializer with serializer address
                SendI2C(capture, 0xC0, 0x22, 0b00000010); // Speed up I2c bus timer to 50us max
                SendI2C(capture, 0xC0, 0x20, 0b00001010); // Decrease BCC timeout, units in 2 ms
                SendI2C(capture, 0xB0, 0x0F, 0b00000010); // Speed up I2c bus timer to 50u Max
                SendI2C(capture, 0xB0, 0x1E, 0b00001010); // Decrease BCC timeout, units in 2 ms
                SendI2C(capture, 0xC0, 0x08, 0xBA, 0x6C); // Set aliases for MT9P031 (CMOS sensor) and LM3509 (LED Driver)
                SendI2C(capture, 0xC0, 0x10, 0xBA, 0x6C); // Set aliases for MT9P031 and LM3509

                // MT9P031 Camera Sensor configuration: default to 1024×768 with 2×2 binning
                SendI2C(capture, 0xBA, 0x03, 0x05, 0xFF); // Row Size = 1535 (1536-1)
                SendI2C(capture, 0xBA, 0x04, 0x07, 0xFF); // Column Size = 2047 (2048-1)
                SendI2C(capture, 0xBA, 0x01, 0x01, 0x02); // Row start = 258 (theoretical center: 54 + (1944-1536)/2)
                SendI2C(capture, 0xBA, 0x02, 0x01, 0x20); // Column start = 288 (theoretical center: 16 + (2592-2048)/2)
                SendI2C(capture, 0xBA, 0x09, 0x02, 0xFF); // Shutter Width Lower = 767 rows
                SendI2C(capture, 0xBA, 0x20, 0b00000000, 0b01100000); // Read Mode 2: Set column binning to summing
                SendI2C(capture, 0xBA, 0x22, 0b00000000, 0b00010001); // Row Address Mode: Set row skip and bin (2×2)
                SendI2C(capture, 0xBA, 0x23, 0b00000000, 0b00010001); // Column Address Mode: Set column skip and bin (2×2)
                SendI2C(capture, 0xBA, 0x3E, 0x00, 0x80); // Set register 0x3E to 0x80 for low gain*  
                                                          // * For optimal sensor performance, when using gain settings <= 4.0 set reserved register R0x3E = 0x0080,
                                                          // and for gain settings >4.0 set register R0x003E = 0x00C0

                // LM3509 LED Driver general configuration
                SendI2C(capture, 0x6C, 0x10, 0b11010111);
            }

            /// <summary>
            /// Sets the sensor resolution with automatic centering and binning configuration.
            /// Automatically adjusts binning if requested dimensions exceed sensor limits.
            /// </summary>
            /// <param name="capture">The VideoCapture instance for the MiniCam device.</param>
            /// <param name="outputWidth">Desired output width in pixels (e.g., 1024, 640, 320).</param>
            /// <param name="outputHeight">Desired output height in pixels (e.g., 768, 480, 240).</param>
            /// <param name="binning">Binning factor: 1 (no binning), 2 (2×2 binning), or 4 (4×4 binning). Default is 1.</param>
            /// <param name="sumBinning">When true, binned pixels are summed (brighter, more sensitive).
            /// When false, they are averaged (maintains brightness level). Default is true.</param>
            /// <exception cref="ArgumentException">Thrown when binning is not 1, 2, or 4, or when output dimensions are invalid.</exception>
            internal static void SetResolution(VideoCapture capture, int outputWidth, int outputHeight, int binning = 1, bool sumBinning = true, string deviceId = null)
            {
                // Validate binning value
                if (binning != 1 && binning != 2 && binning != 4)
                    throw new ArgumentException("Binning must be 1, 2, or 4", nameof(binning));

                // Validate output dimensions are positive
                if (outputWidth <= 0 || outputHeight <= 0)
                    throw new ArgumentException("Output dimensions must be positive");

                // Try to find the highest binning that fits within sensor limits
                int originalBinning = binning;
                int sensorWidth = outputWidth * binning;
                int sensorHeight = outputHeight * binning;

                while (binning >= 1)
                {
                    // Check if this binning fits
                    if (sensorWidth <= SENSOR_ACTIVE_WIDTH && sensorHeight <= SENSOR_ACTIVE_HEIGHT)
                    {
                        break; // Found a valid binning
                    }

                    if (binning > 1)
                    {
                        // Try next lower binning level
                        binning /= 2;
                        sensorWidth = outputWidth * binning;
                        sensorHeight = outputHeight * binning;
                    }
                    else
                    {
                        // Even 1× binning doesn't fit
                        throw new ArgumentException($"Requested output dimensions {outputWidth}×{outputHeight} exceed maximum sensor size {SENSOR_ACTIVE_WIDTH}×{SENSOR_ACTIVE_HEIGHT}");
                    }
                }

                if (binning != originalBinning)
                {
                    Console.WriteLine($"[MiniCam] Warning: Requested {originalBinning}× binning with {outputWidth}×{outputHeight} would require {outputWidth * originalBinning}×{outputHeight * originalBinning} sensor pixels (max: {SENSOR_ACTIVE_WIDTH}×{SENSOR_ACTIVE_HEIGHT}). Using {binning}× binning instead.");
                }

                // Calculate centered start positions
                int colMargin = (SENSOR_ACTIVE_WIDTH - sensorWidth) / 2;
                int rowMargin = (SENSOR_ACTIVE_HEIGHT - sensorHeight) / 2;
                int colStart = SENSOR_ACTIVE_COL_START + colMargin;
                int rowStart = SENSOR_ACTIVE_ROW_START + rowMargin;

                // Convert to register values (size - 1)
                int rowSize = sensorHeight - 1;
                int colSize = sensorWidth - 1;

                // Convert binning to register values (skip = bin for full binning)
                byte binValue = binning switch
                {
                    1 => 0b00, // No binning (register value 0)
                    2 => 0b01, // 2x binning (register value 1)
                    4 => 0b11, // 4x binning (register value 3)
                    _ => throw new ArgumentException($"Invalid binning value: {binning}", nameof(binning))
                };

                // Set skip equal to bin for full binning
                byte skipValue = binValue;

                // Combine bin and skip into address mode register value
                byte addressMode = (byte)(skipValue << 4 | binValue);

                // Configure column binning mode (Register 0x20 Bit 5: 1=Sum, 0=Average)
                // Also enable Row BLC (Bit 6) for better uniformity
                byte readMode2 = sumBinning ? (byte)0b01100000 : (byte)0b01000000;

                //Console.WriteLine($"Setting resolution to {outputWidth}×{outputHeight} with {binning}x binning " +
                //                  $"(sensor window: {sensorWidth}×{sensorHeight} at start {colStart},{rowStart}, address mode {addressMode})");

                // Prepare new sensor configuration
                SendI2C(capture, 0xBA, 0x01, (byte)(rowStart >> 8 & 0xFF), (byte)(rowStart & 0xFF)); // Row start
                SendI2C(capture, 0xBA, 0x02, (byte)(colStart >> 8 & 0xFF), (byte)(colStart & 0xFF)); // Column start
                SendI2C(capture, 0xBA, 0x03, (byte)(rowSize >> 8 & 0xFF), (byte)(rowSize & 0xFF)); // Row Size
                SendI2C(capture, 0xBA, 0x04, (byte)(colSize >> 8 & 0xFF), (byte)(colSize & 0xFF)); // Column Size
                SendI2C(capture, 0xBA, 0x20, 0x00, readMode2); // Read Mode 2: Configure column binning mode
                SendI2C(capture, 0xBA, 0x22, 0x00, addressMode); // Row Address Mode: skip = bin
                SendI2C(capture, 0xBA, 0x23, 0x00, addressMode); // Column Address Mode: skip = bin

                // Update sensor config service with geometry so SetFPS can compute accurate shutter width
                if (deviceId != null)
                    MiniCamConfigService.UpdateGeometry(deviceId, outputWidth, outputHeight, binning,
                        colSize, rowSize, skipValue, skipValue, binValue);
            }

            /// <summary>
            /// Sets the frame rate for the MiniCam by writing the shutter width register (R0x09).
            /// Uses <see cref="MiniCamConfigService"/> when <paramref name="deviceId"/> is provided
            /// to compute the exact value from the current sensor geometry and pixel clock.
            /// Falls back to a fixed approximation for the default 1024×768 2× binning config.
            /// </summary>
            /// <param name="capture">The VideoCapture instance for the MiniCam device.</param>
            /// <param name="FPS">The desired frame rate in frames per second.</param>
            /// <param name="deviceId">Optional device ID for geometry-aware computation via MiniCamConfigService.</param>
            internal static void SetFPS(VideoCapture capture, int FPS, string deviceId = null)
            {
                int sw = deviceId != null
                    ? MiniCamConfigService.ComputeShutterWidthForFps(deviceId, FPS)
                    : -1;

                if (sw < 0)
                {
                    // Fallback: default 1024×768 2× binning (totalPixelsPerLine ≈ 2536, PIXCLK = 96MHz = 2×EXTCLK)
                    sw = (int)Math.Round(2.0 * EXTCLK_HZ / ((double)FPS * 2536)) - 1;
                }

                byte v0 = (byte)((sw & 0x0000FF00) >> 8);
                byte v1 = (byte)(sw & 0x000000FF);
                SendI2C(capture, 0xBA, 0x09, v0, v1);

                if (deviceId != null)
                    MiniCamConfigService.UpdateFps(deviceId, FPS);
            }

            /// <summary>
            /// Sets the analog gain for the MT9P031 sensor.
            /// Automatically adjusts register 0x3E for optimal performance at different gain levels.
            /// </summary>
            /// <param name="capture">The VideoCapture instance for the MiniCam device.</param>
            /// <param name="gain">The gain value (register value, where 8 = 1×, 16 = 2×, 32 = 4×, etc.).</param>
            internal static void SetGain(VideoCapture capture, int gain)
            {
                byte v0 = (byte)((gain & 0x0000FF00) >> 8);
                byte v1 = (byte)(gain & 0x000000FF);
                SendI2C(capture, 0xBA, 0x35, v0, v1);

                // set 0x3E to 0x0080 for low gain and 0x00C0 for high gain
                if (gain > 32) // 4x
                {
                    SendI2C(capture, 0xBA, 0x3E, 0x00, 0xC0);
                }
                else
                {
                    SendI2C(capture, 0xBA, 0x3E, 0x00, 0x80);
                }
            }

            /// <summary>
            /// Sets the operating mode for the MT9P031 sensor.
            /// Sets or resets Snapshot mode.
            /// </summary>
            /// <param name="capture">The VideoCapture instance for the MiniCam device.</param>
            /// <param name="triggered">When True, sets Snapshot mode, when false, resets Snaphsot mode</param>
            internal static void SetTriggerMode(VideoCapture capture, bool triggered)
            {
                // Change the trigger mode by setting Read Mode 1
                // R0x01E default is 0x4006; set or clear bit 8 (Snapshot) while preserving other defaults
                ushort regValue = (ushort)(0x4006 | (triggered ? (1 << 8) : 0));
                byte v0 = (byte)((regValue >> 8) & 0xFF);
                byte v1 = (byte)(regValue & 0xFF);

                SendI2C(capture, 0xBA, 0x1E, v0, v1);
                SendI2C(capture, 0xFE, (byte)(triggered ? 0x02 : 0x03)); // Enable or disable Snapshot mode streaming in DAQ
                SendI2C(capture, 0xBA, 0x0B, 0x00, 0x01); // Restart Sensor
            }

            /// <summary>
            /// Sets the LED brightness for the MiniCam illumination via the LM3509 LED driver.
            /// </summary>
            /// <param name="capture">The VideoCapture instance for the MiniCam device.</param>
            /// <param name="brightness">The brightness value (0-255).</param>
            internal static void SetLEDBrightness(VideoCapture capture, int brightness)
            {
                // The LED driver is controlled by the serializer, so we send the command to the serializer
                SendI2C(capture, 0x6C, 0xA0, (byte)brightness); // Set brightness on LM3509
            }
        }
    }
}