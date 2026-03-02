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
            if (Precision == TimeType.ticks)
            {
                // Return the elapsed ticks
                return source.Select(_ => TimingService.Stopwatch?.ElapsedTicks ?? 0);
            }
            else
            {
                return source.Select(_ => TimingService.Stopwatch?.ElapsedMilliseconds ?? 0);
            }
        }
    }
}
