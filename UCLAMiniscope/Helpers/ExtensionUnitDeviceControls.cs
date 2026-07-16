// SPDX-FileCopyrightText: 2024 Open Ephys and Contributors
// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

// Protocol portions adapted from Open Ephys bonsai-miniscope.
// See THIRD_PARTY_NOTICES.md in the package root.

using System;
using System.Collections.Generic;
using System.Threading;

namespace UCLAMiniscope.Helpers
{
    /// <summary>
    /// Implements device control using the Open Ephys firmware UVC extension unit.
    /// </summary>
    /// <remarks>
    /// The extension-unit control identifiers and batched I2C packet layout are adapted from
    /// the Open Ephys host implementation identified in THIRD_PARTY_NOTICES.md.
    /// </remarks>
    internal sealed class ExtensionUnitDeviceControls : IDeviceControls
    {
        enum ExtensionControl : uint
        {
            I2CWrite = 2,
            ControlFlags = 3
        }

        readonly IUvcExtensionUnit extensionUnit;
        uint latestFrameNumber;

        public ExtensionUnitDeviceControls(IUvcExtensionUnit extensionUnit)
        {
            this.extensionUnit = extensionUnit ?? throw new ArgumentNullException(nameof(extensionUnit));
            I2C = new ExtensionUnitI2CTransport(extensionUnit);
        }

        public II2CCommandTransport I2C { get; }

        public ulong ReadFrameNumber()
        {
            return (ulong)Volatile.Read(ref latestFrameNumber);
        }

        public void SetFrameOutputEnabled(bool enabled)
        {
            extensionUnit.Write((uint)ExtensionControl.ControlFlags, [enabled ? (byte)1 : (byte)0, (byte)0]);
        }

        internal void UpdateFrameNumber(uint frameNumber)
        {
            Volatile.Write(ref latestFrameNumber, frameNumber);
        }

        sealed class ExtensionUnitI2CTransport : II2CCommandTransport
        {
            const int CommandsPerPacket = 4;
            const int CommandSize = 6;
            const int PacketSize = 1 + CommandsPerPacket * CommandSize;

            readonly IUvcExtensionUnit extensionUnit;
            readonly Queue<byte[]> packetQueue = new Queue<byte[]>();
            readonly object commandLock = new object();
            byte[] currentPacket = new byte[PacketSize];

            public ExtensionUnitI2CTransport(IUvcExtensionUnit extensionUnit)
            {
                this.extensionUnit = extensionUnit;
            }

            public void QueueCommand(byte address, params byte[] data)
            {
                if (data == null) throw new ArgumentNullException(nameof(data));
                if (data.Length > 5)
                    throw new ArgumentException("An I2C command cannot contain more than five data bytes.", nameof(data));

                lock (commandLock)
                {
                    int commandIndex = currentPacket[0];
                    int commandOffset = 1 + commandIndex * CommandSize;
                    currentPacket[0]++;

                    if (data.Length == 5)
                    {
                        currentPacket[commandOffset] = (byte)(address | 0x01);
                        for (int i = 0; i < data.Length; i++)
                            currentPacket[commandOffset + 1 + i] = data[i];
                    }
                    else
                    {
                        currentPacket[commandOffset] = address;
                        currentPacket[commandOffset + 1] = (byte)(data.Length + 1);
                        for (int i = 0; i < data.Length; i++)
                            currentPacket[commandOffset + 2 + i] = data[i];
                    }

                    if (currentPacket[0] == CommandsPerPacket)
                    {
                        packetQueue.Enqueue(currentPacket);
                        currentPacket = new byte[PacketSize];
                    }
                }
            }

            public void CommitCommands()
            {
                lock (commandLock)
                {
                    if (currentPacket[0] != 0)
                    {
                        packetQueue.Enqueue(currentPacket);
                        currentPacket = new byte[PacketSize];
                    }

                    while (packetQueue.Count > 0)
                    {
                        extensionUnit.Write((uint)ExtensionControl.I2CWrite, packetQueue.Dequeue());
                    }
                }
            }
        }
    }
}
