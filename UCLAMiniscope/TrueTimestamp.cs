// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Diagnostics;
using Bonsai;

namespace Utilities
{
    /// <summary>
    /// Tags each source value with a monotonic, wall-clock-aligned timestamp.
    /// </summary>
    [Combinator]
    [Description("Tags each element with a DateTimeOffset timestamp using a fudge-free Stopwatch-based clock.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class TrueTimestamp
    {
        static readonly long StopwatchOrigin = Stopwatch.GetTimestamp();
        static readonly DateTimeOffset WallClockOrigin = DateTimeOffset.UtcNow;

        /// <summary>
        /// Tags each value in the source sequence with the corresponding monotonic timestamp.
        /// </summary>
        /// <typeparam name="T">The type of values in the source sequence.</typeparam>
        /// <param name="source">The sequence whose values will be timestamped.</param>
        /// <returns>A sequence containing each source value and its timestamp.</returns>
        public IObservable<Timestamped<T>> Process<T>(IObservable<T> source)
        {
            return source.Select(value =>
            {
                var elapsedTicks = Stopwatch.GetTimestamp() - StopwatchOrigin;
                var timestamp = WallClockOrigin + TimeSpan.FromTicks((long)(elapsedTicks * 10000000.0 / Stopwatch.Frequency));
                return new Timestamped<T>(value, timestamp);
            });
        }
    }
}
