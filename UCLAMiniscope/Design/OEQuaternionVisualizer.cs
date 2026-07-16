// SPDX-FileCopyrightText: 2024 Open Ephys and Contributors
// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

using System;
using Bonsai;
using Bonsai.Design.Visualizers;
using System.Numerics;

// Adapted from the Open Ephys bonsai-miniscope quaternion visualizer.
// See THIRD_PARTY_NOTICES.md in the package root.

[assembly: TypeVisualizer(typeof(UCLAMiniscope.Design.OEQuaternionVisualizer), Target = typeof(Quaternion))]

namespace UCLAMiniscope.Design
{
    /// <summary>
    /// Provides a type visualizer that displays a sequence of <see cref="Quaternion"/>
    /// values as a time series.
    /// </summary>
    public class OEQuaternionVisualizer : TimeSeriesVisualizer
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="OEQuaternionVisualizer"/> class.
        /// </summary>
        public OEQuaternionVisualizer()
            : base(numSeries: 4)
        {
        }

        /// <inheritdoc/>
        public override void Show(object value)
        {
            var q = (Quaternion)value;
            AddValue(DateTime.Now, q.X, q.Y, q.Z, q.W);
        }
    }
}
