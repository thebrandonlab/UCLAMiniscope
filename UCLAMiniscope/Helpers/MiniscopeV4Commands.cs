// SPDX-FileCopyrightText: 2024 Open Ephys and Contributors
// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

using System;
using System.Threading;

namespace UCLAMiniscope.Helpers
{
    /// <summary>
    /// Provides commands for configuring the Python480 sensor, BNO055 IMU, electrowetting lens, and LED of a UCLA Miniscope V4.
    /// </summary>
    internal static class MiniscopeV4Commands
    {
        /// <summary>
        /// Initializes a Miniscope V4 and maps the IMU axes for its physical orientation.
        /// </summary>
        /// <param name="controls">The device controls used to queue and commit the configuration.</param>
        /// <param name="connectorLocation">The position of the U.FL connector relative to the animal.</param>
        internal static void Initialize(IDeviceControls controls, UFLConnectorLocation connectorLocation)
        {
            var i2c = controls.I2C;

            // Configure the serializer/deserializer link (SERDES).
            i2c.QueueCommand(0xC0, 0x1F, 0b00010000);             // Set deserializer to MODE override and 12-bit low frequency.
            i2c.QueueCommand(0xB0, 0x05, 0b00100000);             // Set serializer to MODE override and 12-bit low frequency.
            i2c.QueueCommand(0xC0, 0x22, 0b00000010);             // Reduce deserializer I2C bus timer maximum to 50 µs.
            i2c.QueueCommand(0xC0, 0x20, 5 << 1);                 // Enable watchdog and set BCC timeout to 10 ms (2-ms units).
            i2c.QueueCommand(0xC0, 0x07, 0xB0);                   // Provide the deserializer with the serializer address.
            i2c.QueueCommand(0xB0, 0x0F, 0b00000010);             // Reduce serializer I2C bus timer maximum to 50 µs.
            i2c.QueueCommand(0xB0, 0x1E, 5 << 1);                 // Enable watchdog and set BCC timeout to 10 ms (2-ms units).
            i2c.QueueCommand(0xC0, 0x08, 0x20, 0xEE, 0xA0, 0x50); // Register the ATtiny, EWL, potentiometer, and IMU aliases.
            i2c.QueueCommand(0xC0, 0x10, 0x20, 0xEE, 0x58, 0x50); // Register the corresponding remote I2C addresses.

            // Configure the BNO055 IMU for the physical Miniscope orientation.
            // Axis-map registers are writable only in CONFIG mode. Commit before waiting so
            // the mode-change command reaches the BNO055 before the 19-ms switching delay.
            i2c.QueueCommand(0x50, 0x3D, 0b00000000); // Set BNO055 operation mode to CONFIG.
            i2c.CommitCommands();
            Thread.Sleep(20);
            ConfigureImu(i2c, connectorLocation);
            i2c.QueueCommand(0xFE, 0x00);                         // Enable BNO055 streaming in the DAQ.

            // Configure the MAX14574 electrowetting-lens driver.
            i2c.QueueCommand(0xEE, 0x03, 0x03); // Enable the EWL driver.
            i2c.CommitCommands();
        }


        /// <summary>
        /// Reapplies the physical axis mapping and NDOF mode to the BNO055 IMU.
        /// </summary>
        /// <param name="controls">The device controls used to send the commands.</param>
        /// <param name="connectorLocation">The position of the U.FL connector relative to the animal.</param>
        internal static void ReinitializeImu(IDeviceControls controls, UFLConnectorLocation connectorLocation)
        {
            ConfigureImu(controls.I2C, connectorLocation);
        }

        static void ConfigureImu(II2CCommandTransport i2c, UFLConnectorLocation connectorLocation)
        {
            // Output reference frame: X forward, Y left, Z up.
            // AXIS_MAP_CONFIG (0x41) selects the physical BNO055 source for output Z, Y, and X.
            // AXIS_MAP_SIGN (0x42) bits 2, 1, and 0 negate output X, Y, and Z respectively.
            (byte axisMap, byte axisSign) = connectorLocation switch
            {
                // BNO axes: X down, Y back, Z left.
                // Output: X=-Y, Y=+Z, Z=-X.
                UFLConnectorLocation.FrontLeft => ((byte)0b00001001, (byte)0b00000101),

                // BNO axes: X down, Y left, Z front.
                // Output: X=+Z, Y=+Y, Z=-X.
                UFLConnectorLocation.FrontRight => ((byte)0b00000110, (byte)0b00000001),

                // BNO axes: X down, Y right, Z back.
                // Output: X=-Z, Y=-Y, Z=-X.
                UFLConnectorLocation.RearLeft => ((byte)0b00000110, (byte)0b00000111),

                // BNO axes: X down, Y front, Z right.
                // Output: X=+Y, Y=-Z, Z=-X.
                UFLConnectorLocation.RearRight => ((byte)0b00001001, (byte)0b00000011),
                _ => throw new ArgumentOutOfRangeException(nameof(connectorLocation))
            };

            i2c.QueueCommand(0x50, 0x41, axisMap, axisSign); // Remap BNO055 axes and signs.
            i2c.QueueCommand(0x50, 0x3D, 0b00001100);        // Set BNO055 operation mode to NDOF.
            i2c.CommitCommands();
        }

        /// <summary>
        /// Sets the frame rate by configuring the Python480 exposure-time register and commits immediately.
        /// </summary>
        /// <param name="controls">The device controls used to send the command.</param>
        /// <param name="fps">The desired frame rate in frames per second.</param>
        internal static void SetFPS(IDeviceControls controls, int fps)
        {
            // The Python480 exposure value is represented in 10-µs units across two bytes.
            // For example, 30 FPS is approximately 33.33 ms, or 3333 units: 0x0D05.
            uint exposureTime = (uint)(1.0 / fps * 100000.0);

            // Split the register value into its high and low bytes.
            byte v0 = (byte)((exposureTime & 0x0000FF00) >> 8);
            byte v1 = (byte)(exposureTime & 0x000000FF);
            controls.I2C.QueueCommand(0x20, 0x05, 0x00, 0xC9, v0, v1); // ATtiny → Python480 exposure register.
            controls.I2C.CommitCommands();
        }

        /// <summary>
        /// Queues a Python480 frame-rate command without committing the current command batch.
        /// </summary>
        /// <param name="controls">The device controls used to queue the command.</param>
        /// <param name="fps">The desired frame rate in frames per second.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="fps"/> is not positive.</exception>
        internal static void QueueFPS(IDeviceControls controls, int fps)
        {
            if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));

            // Preserve the values defined by the Open Ephys host implementation for its
            // supported frame rates. Compute the same 10-µs representation for other rates.
            uint exposureTime = fps switch
            {
                10 => 0x2710,
                15 => 0x1A0B,
                20 => 0x1388,
                25 => 0x0FA0,
                30 => 0x0CE4,
                _ => (uint)Math.Round(100000.0 / fps)
            };

            // Split the register value into its high and low bytes.
            byte v0 = (byte)((exposureTime & 0x0000FF00) >> 8);
            byte v1 = (byte)(exposureTime & 0x000000FF);
            controls.I2C.QueueCommand(0x20, 0x05, 0x00, 0xC9, v0, v1); // ATtiny → Python480 exposure register.
        }

        /// <summary>
        /// Sets the Python480 sensor gain and commits immediately.
        /// </summary>
        /// <param name="controls">The device controls used to send the command.</param>
        /// <param name="gain">The desired sensor gain.</param>
        internal static void SetGain(IDeviceControls controls, GainV4 gain)
        {
            QueueGain(controls, gain);
            controls.I2C.CommitCommands();
        }

        internal static void QueueGain(IDeviceControls controls, GainV4 gain)
        {
            controls.I2C.QueueCommand(0x20, 0x05, 0x00, 0xCC, 0x00, (byte)gain); // ATtiny → Python480 gain register.
        }

        /// <summary>
        /// Adjusts the electrowetting-lens focus and commits immediately.
        /// </summary>
        /// <param name="controls">The device controls used to send the command.</param>
        /// <param name="focus">The focus adjustment centered at zero.</param>
        internal static void SetFocus(IDeviceControls controls, int focus)
        {
            QueueFocus(controls, focus);
            controls.I2C.CommitCommands();
        }

        internal static void QueueFocus(IDeviceControls controls, int focus)
        {
            controls.I2C.QueueCommand(0xEE, 0x08, (byte)(127 + focus), 0x02); // MAX14574 EWL focus register.
        }

        /// <summary>
        /// Sets the Miniscope illumination brightness and commits immediately.
        /// </summary>
        /// <param name="controls">The device controls used to send the commands.</param>
        /// <param name="brightness">The brightness value from 0 to 255.</param>
        internal static void SetLEDBrightness(IDeviceControls controls, int brightness)
        {
            QueueLEDBrightness(controls, brightness);
            controls.I2C.CommitCommands();
        }

        internal static void QueueLEDBrightness(IDeviceControls controls, int brightness)
        {
            controls.I2C.QueueCommand(0x20, 0x01, (byte)brightness); // Send the brightness setting through the ATtiny MCU.
            controls.I2C.QueueCommand(0x58, 0x00, 0x72, (byte)brightness); // Set the TPL0102 potentiometer wiper.
        }

        /// <summary>
        /// Enables or disables the external-trigger stream in the modified legacy firmware.
        /// </summary>
        /// <param name="controls">The device controls used to send the command.</param>
        /// <param name="triggered"><see langword="true"/> to enable triggered acquisition; otherwise, <see langword="false"/>.</param>
        internal static void SetLegacyTriggerMode(IDeviceControls controls, bool triggered)
        {
            // This command requires the modified legacy firmware that routes the DAQ input
            // trigger through the serializer.
            controls.I2C.QueueCommand(0x20, 0x01, triggered ? (byte)1 : (byte)0);
            controls.I2C.CommitCommands();
        }
    }
}
