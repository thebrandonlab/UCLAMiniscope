// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

/*
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
    - 0x02: Enable Input Trigger streaming to serializer (Modified legacy firmware only)
    - 0x03: Disable Input Trigger streaming (Modified legacy firmware only)
*/

namespace UCLAMiniscope.Helpers
{
    /// <summary>
    /// Defines device operations required by recording-session control nodes.
    /// </summary>
    public interface IDeviceSessionControls
    {
        /// <summary>
        /// Reads the latest hardware frame number reported by the connected device.
        /// </summary>
        ulong ReadFrameNumber();

        /// <summary>
        /// Enables or disables the device frame-output signal.
        /// </summary>
        /// <param name="enabled"><see langword="true"/> to enable frame output; otherwise, <see langword="false"/>.</param>
        void SetFrameOutputEnabled(bool enabled);
    }

    /// <summary>
    /// Defines a transport-neutral queue for I2C commands sent through a UCLA imaging device.
    /// </summary>
    internal interface II2CCommandTransport
    {
        void QueueCommand(byte address, params byte[] data);

        void CommitCommands();
    }

    /// <summary>
    /// Combines recording-session operations with the I2C transport used to configure an imaging device.
    /// </summary>
    internal interface IDeviceControls : IDeviceSessionControls
    {
        II2CCommandTransport I2C { get; }
    }
}
