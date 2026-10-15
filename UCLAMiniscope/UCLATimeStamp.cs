// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

using Bonsai;
using System;
using System.ComponentModel;
using System.Reactive.Linq;
using UCLAMiniscope.Helpers;

namespace UCLAMiniscope
{
    /// <summary>
    /// Defines the time precision for timestamp output.
    /// </summary>
    public enum TimeType
    {
        /// <summary>
        /// Milliseconds precision.
        /// </summary>
        ms = 1,

        /// <summary>
        /// Ticks precision (high-resolution).
        /// </summary>
        ticks = 2,
    }

    /// <summary>
    /// Transforms any input into a timestamp from the shared timing service.
    /// </summary>
    [Description("Emits timestamps from the UCLA Miniscope timing service.")]
    public class UCLATimestamp : Transform<object, long>
    {
        /// <summary>
        /// Gets or sets the precision of the timestamp output (milliseconds or ticks).
        /// </summary>
        [Description("Precision: ms or ticks")]
        public TimeType Precision { get; set; } = TimeType.ms;

        /// <summary>
        /// Transforms the input sequence into timestamp values.
        /// </summary>
        /// <param name="source">The source sequence of any type.</param>
        /// <returns>A sequence of timestamps in the specified precision.</returns>
        public override IObservable<long> Process(IObservable<object> source)
        {
            return source.Select(_ =>
            {
                long timestamp = Precision == TimeType.ticks
                    ? TimingService.Stopwatch?.ElapsedTicks ?? 0
                    : TimingService.Stopwatch?.ElapsedMilliseconds ?? 0;
                return timestamp;
            });
        }
    }
    /// <summary>
    /// Transforms any input into a tuple of a timestamp from the shared timing
    /// service and the original source object, preserving the source's static type.
    /// </summary>
    [Combinator]
    [Description("Emits a tuple of (timestamp, source object) from the UCLA Miniscope timing service.")]
    public class UCLATimestampedObject
    {
        /// <summary>
        /// Gets or sets the precision of the timestamp output (milliseconds or ticks).
        /// </summary>
        [Description("Precision: ms or ticks")]
        public TimeType Precision { get; set; } = TimeType.ms;

        /// <summary>
        /// Combines the input sequence into tuples of (timestamp, source object).
        /// </summary>
        /// <typeparam name="TSource">The type of the elements in the source sequence.</typeparam>
        /// <param name="source">The source sequence of any type.</param>
        /// <returns>A sequence of <see cref="Tuple{T1, T2}"/> of timestamp and source object.</returns>
        public IObservable<Tuple<long, TSource>> Process<TSource>(IObservable<TSource> source)
        {
            return source.Select(value =>
            {
                long timestamp = Precision == TimeType.ticks
                    ? TimingService.Stopwatch?.ElapsedTicks ?? 0
                    : TimingService.Stopwatch?.ElapsedMilliseconds ?? 0;
                return Tuple.Create(timestamp, value);
            });
        }
    }
}
