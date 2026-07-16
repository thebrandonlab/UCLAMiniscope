// SPDX-FileCopyrightText: 2024 Open Ephys and Contributors
// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

// Protocol portions adapted from Open Ephys bonsai-miniscope.
// See THIRD_PARTY_NOTICES.md in the package root.

using System;
using System.IO;
using System.Numerics;
using OpenCV.Net;

namespace UCLAMiniscope.Helpers
{
    /// <summary>
    /// Decodes metadata embedded by Open Ephys DAQ firmware and extracts grayscale luminance from YUY2 frames.
    /// </summary>
    /// <remarks>
    /// The embedded-word layout is adapted from the MIT-licensed Open Ephys bonsai-miniscope host protocol.
    /// </remarks>
    internal static class ExtensionUnitFrameDecoder
    {
        const int MetadataWordCount = 5;
        const int EncodedWordSize = 8;
        const float QuaternionConversionFactor = 1.0f / (1 << 14);

        internal static unsafe ExtensionUnitFrameMetadata DecodeMetadata(ExtensionUnitRawFrame frame)
        {
            if (frame.Data == IntPtr.Zero) throw new ArgumentNullException(nameof(frame.Data));
            if (frame.DataLength < MetadataWordCount * EncodedWordSize)
                throw new InvalidDataException("The YUY2 frame is too short to contain UCLA DAQ metadata.");

            byte* data = (byte*)frame.Data.ToPointer();
            uint frameNumber = ExtractWord(data, 0);
            uint hardwareTime = ExtractWord(data, 1);
            uint state = ExtractWord(data, 2);
            uint quaternionWX = ExtractWord(data, 3);
            uint quaternionYZ = ExtractWord(data, 4);

            short rawW = DecodeLowInt16(quaternionWX);
            short rawX = DecodeHighInt16(quaternionWX);
            short rawY = DecodeLowInt16(quaternionYZ);
            short rawZ = DecodeHighInt16(quaternionYZ);

            var quaternion = new Quaternion(
                rawX * QuaternionConversionFactor,
                rawY * QuaternionConversionFactor,
                rawZ * QuaternionConversionFactor,
                rawW * QuaternionConversionFactor);

            return new ExtensionUnitFrameMetadata(
                frameNumber,
                hardwareTime,
                (byte)(state & 0x03),
                quaternion);
        }

        internal static unsafe IplImage CreateGrayscaleImage(ExtensionUnitRawFrame frame)
        {
            if (frame.Data == IntPtr.Zero) throw new ArgumentNullException(nameof(frame.Data));

            int sourceRowBytes = checked(frame.PixelWidth * 2);
            long requiredLength = (long)frame.PlaneStartIndex +
                                  (long)(frame.PixelHeight - 1) * frame.RowStride +
                                  sourceRowBytes;
            if (frame.PlaneStartIndex < 0 ||
                frame.RowStride < sourceRowBytes ||
                requiredLength > frame.DataLength)
            {
                throw new InvalidDataException("The YUY2 frame buffer layout is inconsistent with its declared dimensions.");
            }

            var image = new IplImage(new Size(frame.PixelWidth, frame.PixelHeight), IplDepth.U8, 1);
            try
            {
                byte* source = (byte*)frame.Data.ToPointer();
                byte* destination = (byte*)image.ImageData.ToPointer();
                for (int row = 0; row < frame.PixelHeight; row++)
                {
                    byte* sourceRow = source + frame.PlaneStartIndex + row * frame.RowStride;
                    byte* destinationRow = destination + row * image.WidthStep;
                    for (int column = 0; column < frame.PixelWidth; column++)
                        destinationRow[column] = sourceRow[column * 2];
                }

                return image;
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        static unsafe uint ExtractWord(byte* data, int wordIndex)
        {
            int offset = wordIndex * EncodedWordSize;
            ulong encoded =
                (ulong)data[offset] |
                (ulong)data[offset + 1] << 8 |
                (ulong)data[offset + 2] << 16 |
                (ulong)data[offset + 3] << 24 |
                (ulong)data[offset + 4] << 32 |
                (ulong)data[offset + 5] << 40 |
                (ulong)data[offset + 6] << 48 |
                (ulong)data[offset + 7] << 56;

            return (uint)(
                (encoded >> 8 & 0x000000FFUL) |
                (encoded >> 16 & 0x0000FF00UL) |
                (encoded >> 24 & 0x00FF0000UL) |
                (encoded >> 32 & 0xFF000000UL));
        }

        static short DecodeLowInt16(uint value)
        {
            return unchecked((short)(ushort)(value & ushort.MaxValue));
        }

        static short DecodeHighInt16(uint value)
        {
            return unchecked((short)(ushort)(value >> 16));
        }
    }

    internal readonly struct ExtensionUnitFrameMetadata
    {
        public ExtensionUnitFrameMetadata(
            uint frameNumber,
            uint hardwareTime,
            byte digitalInputs,
            Quaternion quaternion)
        {
            FrameNumber = frameNumber;
            HardwareTime = hardwareTime;
            DigitalInputs = digitalInputs;
            Quaternion = quaternion;
        }

        public uint FrameNumber { get; }

        public uint HardwareTime { get; }

        public byte DigitalInputs { get; }

        public Quaternion Quaternion { get; }
    }
}
