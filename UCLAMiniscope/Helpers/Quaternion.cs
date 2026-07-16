// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

using OpenCvSharp;
using System;
using System.Numerics;

namespace UCLAMiniscope.Helpers
{
    /// <summary>
    /// Decodes and validates BNO055 quaternions exposed through DAQ UVC properties.
    /// </summary>
    internal static class QuaternionHelper
    {
        // BNO055 quaternion scale: 1 quaternion unit = 2^14 LSB.
        const float ConversionFactor = 1.0f / (1 << 14);
        const float NormTolerance = 0.05f;

        /// <summary>
        /// Squared-norm threshold below which the BNO055 output is considered stopped.
        /// </summary>
        internal const float FailureNormSquaredThreshold = 0.01f;

        /// <summary>
        /// Reads a quaternion in System.Numerics order (X, Y, Z, W).
        /// </summary>
        internal static Quaternion Read(VideoCapture capture)
        {
            // Read in the DAQ's legacy W, X, Y, Z order so all components come
            // from as compact a sequence of UVC property requests as possible.
            float w = ReadComponent(capture, VideoCaptureProperties.Saturation);
            float x = ReadComponent(capture, VideoCaptureProperties.Hue);
            float y = ReadComponent(capture, VideoCaptureProperties.Gain);
            float z = ReadComponent(capture, VideoCaptureProperties.Brightness);
            return new Quaternion(x, y, z, w);
        }

        /// <summary>
        /// Tests whether a quaternion has a finite norm within the BNO055 tolerance.
        /// </summary>
        internal static bool IsValid(Quaternion value, out float normSquared)
        {
            normSquared =
                value.W * value.W +
                value.X * value.X +
                value.Y * value.Y +
                value.Z * value.Z;

            if (float.IsNaN(normSquared) || float.IsInfinity(normSquared))
            {
                return false;
            }

            float norm = (float)Math.Sqrt(normSquared);
            return Math.Abs(norm - 1f) < NormTolerance;
        }

        static float ReadComponent(VideoCapture capture, VideoCaptureProperties property)
        {
            // The DAQ exposes the signed BNO055 int16 through a UVC camera property.
            // Preserve the low 16 bits, then reinterpret them as a signed value.
            int raw = (int)capture.Get(property);
            short signed = unchecked((short)(raw & ushort.MaxValue));
            return signed * ConversionFactor;
        }
    }
}
