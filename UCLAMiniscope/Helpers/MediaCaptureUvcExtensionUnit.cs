// SPDX-FileCopyrightText: 2024 Open Ephys and Contributors
// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

// Protocol portions adapted from Open Ephys bonsai-miniscope.
// See THIRD_PARTY_NOTICES.md in the package root.

using System;
using Windows.Media.Devices;

namespace UCLAMiniscope.Helpers
{
    /// <summary>
    /// Provides raw access to a UVC extension unit exposed through MediaCapture.
    /// </summary>
    internal interface IUvcExtensionUnit
    {
        byte[] Read(uint control, uint dataSize);

        void Write(uint control, byte[] data);
    }

    /// <summary>
    /// Locates and communicates with the extension unit implemented by current Open Ephys DAQ firmware.
    /// </summary>
    /// <remarks>
    /// The extension-unit GUID, control layout, and topology probing strategy are adapted from the
    /// MIT-licensed Open Ephys bonsai-miniscope host implementation.
    /// </remarks>
    internal sealed class MediaCaptureUvcExtensionUnit : IUvcExtensionUnit
    {
        static readonly Guid ExtensionUnitGuid = new Guid("b33905c0-5500-4fee-9058-2c3efd330bb9");

        const uint PropertyTypeGet = 0x00000001;
        const uint PropertyTypeSet = 0x00000002;
        const uint PropertyTypeTopology = 0x10000000;
        const uint FirmwareVersionControl = 1;

        readonly VideoDeviceController controller;
        readonly object controlLock = new object();
        readonly uint nodeId;

        public MediaCaptureUvcExtensionUnit(VideoDeviceController controller)
        {
            this.controller = controller ?? throw new ArgumentNullException(nameof(controller));

            for (uint candidateNode = 1; candidateNode < 16; candidateNode++)
            {
                try
                {
                    var versionBytes = Read(FirmwareVersionControl, 3, candidateNode);
                    if (versionBytes.Length < 3) continue;

                    FirmwareVersion = new Version(versionBytes[0], versionBytes[1], versionBytes[2]);
                    nodeId = candidateNode;
                    IsAvailable = true;
                    break;
                }
                catch (Exception)
                {
                    // A UVC topology may contain several unrelated nodes. Probe the next one.
                }
            }
        }

        public bool IsAvailable { get; }

        public Version FirmwareVersion { get; } = new Version(0, 0, 0);

        public byte[] Read(uint control, uint dataSize)
        {
            if (!IsAvailable) throw new InvalidOperationException("The Miniscope extension unit is not available.");
            return Read(control, dataSize, nodeId);
        }

        byte[] Read(uint control, uint dataSize, uint unitNodeId)
        {
            var propertyId = CreateExtendedPropertyId(
                ExtensionUnitGuid,
                control,
                PropertyTypeGet | PropertyTypeTopology,
                unitNodeId);

            lock (controlLock)
            {
                var result = controller.GetDevicePropertyByExtendedId(propertyId, dataSize);
                if (result.Status != VideoDeviceControllerGetDevicePropertyStatus.Success)
                    throw new InvalidOperationException($"Error reading Miniscope extension control {control}: {result.Status}.");

                if (result.Value is byte[] data) return data;
                throw new InvalidOperationException($"Miniscope extension control {control} returned an invalid buffer.");
            }
        }

        public void Write(uint control, byte[] data)
        {
            if (!IsAvailable) throw new InvalidOperationException("The Miniscope extension unit is not available.");
            if (data == null) throw new ArgumentNullException(nameof(data));

            var propertyId = CreateExtendedPropertyId(
                ExtensionUnitGuid,
                control,
                PropertyTypeSet | PropertyTypeTopology,
                nodeId);

            lock (controlLock)
            {
                var status = controller.SetDevicePropertyByExtendedId(propertyId, data);
                if (status != VideoDeviceControllerSetDevicePropertyStatus.Success)
                    throw new InvalidOperationException($"Error writing Miniscope extension control {control}: {status}.");
            }
        }

        static unsafe byte[] CreateExtendedPropertyId(Guid guid, uint control, uint flags, uint unitNodeId)
        {
            var propertyId = new byte[64];
            fixed (byte* pointer = propertyId)
            {
                *(Guid*)pointer = guid;
                *(uint*)(pointer + 16) = control;
                *(uint*)(pointer + 20) = flags;
                *(uint*)(pointer + 24) = unitNodeId;
            }

            return propertyId;
        }
    }
}
