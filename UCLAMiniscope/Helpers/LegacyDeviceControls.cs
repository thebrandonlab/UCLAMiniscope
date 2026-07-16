// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

using OpenCvSharp;
using System;
using System.Collections.Generic;

namespace UCLAMiniscope.Helpers
{
    /// <summary>
    /// Implements device control for legacy firmware using repurposed UVC processing-unit properties.
    /// </summary>
    internal sealed class LegacyDeviceControls : IDeviceControls
    {
        readonly VideoCapture capture;

        public LegacyDeviceControls(VideoCapture capture)
        {
            this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
            I2C = new LegacyI2CCommandTransport(capture);
        }

        public II2CCommandTransport I2C { get; }

        public ulong ReadFrameNumber()
        {
            return (ushort)capture.Get(VideoCaptureProperties.Contrast);
        }

        public void SetFrameOutputEnabled(bool enabled)
        {
            capture.Set(VideoCaptureProperties.Saturation, enabled ? 1 : 0);
        }
    }

    /// <summary>
    /// Encodes and sends legacy device I2C commands over Contrast, Gamma, and Sharpness.
    /// </summary>
    internal sealed class LegacyI2CCommandTransport : II2CCommandTransport
    {
        readonly VideoCapture capture;
        readonly Queue<ulong> commandQueue = new Queue<ulong>();
        readonly object commandLock = new object();

        public LegacyI2CCommandTransport(VideoCapture capture)
        {
            this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
        }

        public void QueueCommand(byte address, params byte[] data)
        {
            var command = LegacyI2CCommandEncoder.Encode(address, data);
            lock (commandLock)
            {
                commandQueue.Enqueue(command);
            }
        }

        public void CommitCommands()
        {
            lock (commandLock)
            {
                while (commandQueue.Count > 0)
                {
                    SendEncodedCommand(commandQueue.Dequeue());
                }
            }
        }

        void SendEncodedCommand(ulong command)
        {
            // Legacy firmware reconstructs one six-byte command from three repurposed
            // 16-bit UVC processing-unit properties.
            capture.Set(VideoCaptureProperties.Contrast, command & 0x00000000FFFF);
            capture.Set(VideoCaptureProperties.Gamma, (command & 0x0000FFFF0000) >> 16);
            capture.Set(VideoCaptureProperties.Sharpness, (command & 0xFFFF00000000) >> 32);
        }
    }

    /// <summary>
    /// Encodes the six-byte command representation used by legacy firmware.
    /// </summary>
    internal static class LegacyI2CCommandEncoder
    {
        /// <summary>
        /// Encodes an 8-bit I2C address and up to five data bytes into the six-byte legacy DAQ packet.
        /// </summary>
        /// <param name="address">The 8-bit I2C address of the target device.</param>
        /// <param name="data">The data bytes to send, up to a maximum of five.</param>
        /// <returns>The command packed into the low 48 bits of an unsigned integer.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">Thrown when more than five data bytes are provided.</exception>
        internal static ulong Encode(byte address, params byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length > 5)
                throw new ArgumentException("An I2C command cannot contain more than five data bytes.", nameof(data));

            ulong packet = address;

            if (data.Length == 5)
            {
                // A full six-byte packet has no room for a length byte. The address's low bit
                // is flipped to one to identify this representation to the legacy firmware.
                packet |= 0x01;
                for (int i = 0; i < data.Length; i++)
                {
                    // Append each data byte directly after the address.
                    packet |= (ulong)data[i] << 8 * (1 + i);
                }
            }
            else
            {
                // Shorter packets store the total address-plus-data length in the second byte.
                packet |= (ulong)(data.Length + 1) << 8;
                for (int i = 0; i < data.Length; i++)
                {
                    // Append each data byte after the address and length bytes.
                    packet |= (ulong)data[i] << 8 * (2 + i);
                }
            }

            return packet;
        }
    }
}
