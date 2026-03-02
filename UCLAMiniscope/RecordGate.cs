using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using Bonsai;
using UCLAMiniscope.Helpers;

namespace UCLAMiniscope
{
    /// <summary>
    /// Filters the input sequence to only pass elements through when recording is active.
    /// </summary>
    [Description("Passes the input through only when recording.")]
    public class RecordGate : Combinator
    {
        /// <summary>
        /// Processes the input sequence, filtering to only emit elements when recording is active.
        /// </summary>
        /// <typeparam name="TSource">The type of elements in the source sequence.</typeparam>
        /// <param name="source">The source sequence to filter.</param>
        /// <returns>A filtered sequence containing only elements emitted while recording is active.</returns>
        public override IObservable<TSource> Process<TSource>(IObservable<TSource> source)
        {
            return source.Where(_ => RecordingService.IsRecording);
        }
    }
}


