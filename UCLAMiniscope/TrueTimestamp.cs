using System;
using System.ComponentModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Diagnostics;
using Bonsai;

namespace Utilities
{
    [Combinator]
    [Description("Tags each element with a DateTimeOffset timestamp using a fudge-free Stopwatch-based clock.")]
    [WorkflowElementCategory(ElementCategory.Combinator)]
    public class TrueTimestamp
    {
        static readonly long StopwatchOrigin = Stopwatch.GetTimestamp();
        static readonly DateTimeOffset WallClockOrigin = DateTimeOffset.UtcNow;

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
