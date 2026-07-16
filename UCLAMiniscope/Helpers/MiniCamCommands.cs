// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

using System;

namespace UCLAMiniscope.Helpers
{
    /// <summary>
    /// Provides commands for configuring the MT9P031 sensor, SERDES link, and LM3509 LED driver of a UCLA MiniCam.
    /// </summary>
    internal static class MiniCamCommands
    {
        // MT9P031 active sensor dimensions and the first optically active pixel.
        const int SensorActiveWidth = 2592;
        const int SensorActiveHeight = 1944;
        const int SensorActiveColumnStart = 16;
        const int SensorActiveRowStart = 54;

        // The MT9P031 PLL receives a 48-MHz external clock. The timing formulas use
        // a 96-MHz pixel clock (PLL ×2), stored by MiniCamConfigService.
        const double ExternalClockHz = 48_000_000.0;

        /// <summary>
        /// Converts a MiniCam resolution preset into its width and height in pixels.
        /// </summary>
        /// <param name="preset">The resolution preset to convert.</param>
        /// <returns>The width and height in pixels.</returns>
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
                _ => (1024, 768) // Default fallback.
            };
        }

        /// <summary>
        /// Initializes the MiniCam SERDES link, MT9P031 sensor, and LM3509 LED driver.
        /// </summary>
        /// <remarks>
        /// The default sensor configuration is 1024×768 output using 2×2 binning from a
        /// centered 2048×1536 sensor window.
        /// </remarks>
        /// <param name="controls">The device controls used to queue and commit the configuration.</param>
        internal static void Initialize(IDeviceControls controls)
        {
            var i2c = controls.I2C;

            // Configure the serializer/deserializer link (SERDES).
            i2c.QueueCommand(0xC0, 0x07, 0xB0); // Provide the deserializer with the serializer address.
            i2c.QueueCommand(0xC0, 0x22, 0b00000010); // Reduce deserializer I2C bus timer maximum to 50 µs.
            i2c.QueueCommand(0xC0, 0x20, 0b00001010); // Set BCC timeout to 10 ms (2-ms units).
            i2c.QueueCommand(0xB0, 0x0F, 0b00000010); // Reduce serializer I2C bus timer maximum to 50 µs.
            i2c.QueueCommand(0xB0, 0x1E, 0b00001010); // Set BCC timeout to 10 ms (2-ms units).
            i2c.QueueCommand(0xC0, 0x08, 0xBA, 0x6C); // Set aliases for the MT9P031 sensor and LM3509 LED driver.
            i2c.QueueCommand(0xC0, 0x10, 0xBA, 0x6C); // Set the corresponding remote I2C addresses.

            // Configure the MT9P031 sensor for 1024×768 output with 2×2 binning.
            i2c.QueueCommand(0xBA, 0x03, 0x05, 0xFF); // Row Size = 1535 (1536 − 1).
            i2c.QueueCommand(0xBA, 0x04, 0x07, 0xFF); // Column Size = 2047 (2048 − 1).
            i2c.QueueCommand(0xBA, 0x01, 0x01, 0x02); // Row Start = 258: 54 + (1944 − 1536) / 2.
            i2c.QueueCommand(0xBA, 0x02, 0x01, 0x20); // Column Start = 288: 16 + (2592 − 2048) / 2.
            i2c.QueueCommand(0xBA, 0x09, 0x02, 0xFF); // Shutter Width Lower = 767 rows.
            i2c.QueueCommand(0xBA, 0x20, 0b00000000, 0b01100000); // Read Mode 2: sum column-binned pixels.
            i2c.QueueCommand(0xBA, 0x22, 0b00000000, 0b00010001); // Row Address Mode: 2× row skip and bin.
            i2c.QueueCommand(0xBA, 0x23, 0b00000000, 0b00010001); // Column Address Mode: 2× column skip and bin.

            // For optimal performance, the MT9P031 documentation recommends reserved register
            // R0x3E = 0x0080 at gains ≤4× and R0x3E = 0x00C0 at gains >4×.
            i2c.QueueCommand(0xBA, 0x3E, 0x00, 0x80); // Select the low-gain setting.

            // Configure the LM3509 LED driver.
            i2c.QueueCommand(0x6C, 0x10, 0b11010111); // General LED-driver configuration.
            i2c.CommitCommands();
        }

        /// <summary>
        /// Sets a centered MT9P031 sensor window and its binning configuration.
        /// </summary>
        /// <remarks>
        /// If the requested binning would exceed the active sensor dimensions, the method
        /// automatically selects the highest lower binning factor that fits.
        /// </remarks>
        /// <param name="controls">The device controls used to send the commands.</param>
        /// <param name="outputWidth">The desired output width in pixels.</param>
        /// <param name="outputHeight">The desired output height in pixels.</param>
        /// <param name="binning">The binning factor: 1, 2, or 4.</param>
        /// <param name="sumBinning"><see langword="true"/> to sum binned pixels; <see langword="false"/> to average them.</param>
        /// <param name="deviceId">The optional device identifier used to update the sensor-timing model.</param>
        /// <exception cref="ArgumentException">Thrown when the binning or output dimensions are invalid.</exception>
        internal static void SetResolution(
            IDeviceControls controls,
            int outputWidth,
            int outputHeight,
            int binning = 1,
            bool sumBinning = true,
            string deviceId = null)
        {
            // The MT9P031 address-mode registers support the binning factors exposed here.
            if (binning != 1 && binning != 2 && binning != 4)
                throw new ArgumentException("Binning must be 1, 2, or 4.", nameof(binning));

            if (outputWidth <= 0 || outputHeight <= 0)
                throw new ArgumentException("Output dimensions must be positive.");

            // Find the highest requested binning factor whose sensor window fits the active array.
            int originalBinning = binning;
            int sensorWidth = outputWidth * binning;
            int sensorHeight = outputHeight * binning;

            while (binning >= 1)
            {
                if (sensorWidth <= SensorActiveWidth && sensorHeight <= SensorActiveHeight)
                    break;

                if (binning == 1)
                {
                    throw new ArgumentException(
                        $"Requested output dimensions {outputWidth}×{outputHeight} exceed maximum sensor size {SensorActiveWidth}×{SensorActiveHeight}.");
                }

                // Try the next lower supported binning factor.
                binning /= 2;
                sensorWidth = outputWidth * binning;
                sensorHeight = outputHeight * binning;
            }

            if (binning != originalBinning)
            {
                Console.WriteLine(
                    $"[MiniCam] Warning: {originalBinning}× binning at {outputWidth}×{outputHeight} " +
                    $"exceeds the {SensorActiveWidth}×{SensorActiveHeight} sensor. Using {binning}× binning.");
            }

            // Center the sensor window inside the optically active array.
            int columnStart = SensorActiveColumnStart + (SensorActiveWidth - sensorWidth) / 2;
            int rowStart = SensorActiveRowStart + (SensorActiveHeight - sensorHeight) / 2;

            // MT9P031 row and column size registers store size − 1.
            int rowSize = sensorHeight - 1;
            int columnSize = sensorWidth - 1;

            // Address-mode register encoding from the MT9P031 documentation.
            byte binValue = binning switch
            {
                1 => 0b00, // No binning.
                2 => 0b01, // 2× binning.
                4 => 0b11, // 4× binning.
                _ => throw new ArgumentException($"Invalid binning value: {binning}", nameof(binning))
            };

            // Full binning uses the same value for pixel skipping and binning.
            byte skipValue = binValue;
            byte addressMode = (byte)(skipValue << 4 | binValue);

            // Read Mode 2 bit 5 selects summing (1) or averaging (0) for column binning.
            // Bit 6 enables row black-level correction for improved uniformity.
            byte readMode2 = sumBinning ? (byte)0b01100000 : (byte)0b01000000;

            var i2c = controls.I2C;
            i2c.QueueCommand(0xBA, 0x01, (byte)(rowStart >> 8 & 0xFF), (byte)(rowStart & 0xFF)); // Row Start.
            i2c.QueueCommand(0xBA, 0x02, (byte)(columnStart >> 8 & 0xFF), (byte)(columnStart & 0xFF)); // Column Start.
            i2c.QueueCommand(0xBA, 0x03, (byte)(rowSize >> 8 & 0xFF), (byte)(rowSize & 0xFF)); // Row Size.
            i2c.QueueCommand(0xBA, 0x04, (byte)(columnSize >> 8 & 0xFF), (byte)(columnSize & 0xFF)); // Column Size.
            i2c.QueueCommand(0xBA, 0x20, 0x00, readMode2); // Read Mode 2: column-binning mode and row BLC.
            i2c.QueueCommand(0xBA, 0x22, 0x00, addressMode); // Row Address Mode: skip = bin.
            i2c.QueueCommand(0xBA, 0x23, 0x00, addressMode); // Column Address Mode: skip = bin.
            i2c.CommitCommands();

            // Update the timing model so frame-rate changes use the current sensor geometry.
            if (deviceId != null)
            {
                MiniCamConfigService.UpdateGeometry(
                    deviceId,
                    outputWidth,
                    outputHeight,
                    binning,
                    columnSize,
                    rowSize,
                    skipValue,
                    skipValue,
                    binValue);
            }
        }

        /// <summary>
        /// Sets the MiniCam frame rate by writing MT9P031 Shutter Width Lower register R0x09.
        /// </summary>
        /// <remarks>
        /// When a device identifier is supplied, <see cref="MiniCamConfigService"/> computes the
        /// value from the current sensor geometry and pixel clock. Otherwise, the calculation
        /// falls back to the default 1024×768, 2×-binning configuration.
        /// </remarks>
        /// <param name="controls">The device controls used to send the command.</param>
        /// <param name="fps">The desired frame rate in frames per second.</param>
        /// <param name="deviceId">The optional identifier for geometry-aware timing.</param>
        internal static void SetFPS(IDeviceControls controls, int fps, string deviceId = null)
        {
            int shutterWidth = deviceId != null
                ? MiniCamConfigService.ComputeShutterWidthForFps(deviceId, fps)
                : -1;

            if (shutterWidth < 0)
            {
                // Default 1024×768 2× binning: approximately 2536 pixel clocks per line,
                // with PIXCLK = 96 MHz = 2 × EXTCLK.
                shutterWidth = (int)Math.Round(2.0 * ExternalClockHz / (fps * 2536.0)) - 1;
            }

            // Split the 16-bit register value into its high and low bytes.
            byte v0 = (byte)((shutterWidth & 0x0000FF00) >> 8);
            byte v1 = (byte)(shutterWidth & 0x000000FF);
            controls.I2C.QueueCommand(0xBA, 0x09, v0, v1); // MT9P031 Shutter Width Lower.
            controls.I2C.CommitCommands();

            if (deviceId != null)
                MiniCamConfigService.UpdateFps(deviceId, fps);
        }

        /// <summary>
        /// Sets the MT9P031 analog gain and its recommended reserved-register value.
        /// </summary>
        /// <param name="controls">The device controls used to send the commands.</param>
        /// <param name="gain">The sensor register value, where 8 = 1×, 16 = 2×, and 32 = 4×.</param>
        internal static void SetGain(IDeviceControls controls, int gain)
        {
            byte v0 = (byte)((gain & 0x0000FF00) >> 8);
            byte v1 = (byte)(gain & 0x000000FF);
            controls.I2C.QueueCommand(0xBA, 0x35, v0, v1); // MT9P031 Global Gain.

            // The sensor documentation recommends R0x3E = 0x0080 up to 4× gain and
            // R0x3E = 0x00C0 above 4× gain.
            controls.I2C.QueueCommand(0xBA, 0x3E, 0x00, gain > 32 ? (byte)0xC0 : (byte)0x80);
            controls.I2C.CommitCommands();
        }

        /// <summary>
        /// Enables or disables MT9P031 snapshot mode and the modified legacy DAQ trigger stream.
        /// </summary>
        /// <param name="controls">The device controls used to send the commands.</param>
        /// <param name="triggered"><see langword="true"/> for snapshot acquisition; otherwise, continuous acquisition.</param>
        internal static void SetLegacyTriggerMode(IDeviceControls controls, bool triggered)
        {
            // Read Mode 1 defaults to 0x4006. Set or clear bit 8 (Snapshot) while preserving
            // the other default bits.
            ushort registerValue = (ushort)(0x4006 | (triggered ? 1 << 8 : 0));
            byte v0 = (byte)(registerValue >> 8 & 0xFF);
            byte v1 = (byte)(registerValue & 0xFF);

            controls.I2C.QueueCommand(0xBA, 0x1E, v0, v1); // MT9P031 Read Mode 1.
            controls.I2C.QueueCommand(0xFE, (byte)(triggered ? 0x02 : 0x03)); // Enable or disable DAQ input-trigger streaming.
            controls.I2C.QueueCommand(0xBA, 0x0B, 0x00, 0x01); // Restart the sensor to apply the mode.
            controls.I2C.CommitCommands();
        }

        /// <summary>
        /// Sets MiniCam illumination brightness through the LM3509 LED driver.
        /// </summary>
        /// <param name="controls">The device controls used to send the command.</param>
        /// <param name="brightness">The brightness value from 0 to 255.</param>
        internal static void SetLEDBrightness(IDeviceControls controls, int brightness)
        {
            // The LED driver is reached through the serializer alias configured during initialization.
            controls.I2C.QueueCommand(0x6C, 0xA0, (byte)brightness); // Set LM3509 brightness.
            controls.I2C.CommitCommands();
        }
    }
}
