// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

using OpenCV.Net;
using System;
using System.ComponentModel;
using System.IO;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using Bonsai;
using UCLAMiniscope.Helpers;

namespace UCLAMiniscope
{
    /// <summary>
    /// Writes behavior video and timestamps to disk while recording is active.
    /// </summary>
    [Description("Writes behavior video and timestamps to disk while recording is active.")]
    [WorkflowElementCategory(ElementCategory.Sink)]
    public class BehaviorVideoWriter : Sink<IplImage>
    {
        static readonly object SyncRoot = new();

        /// <summary>
        /// Gets or sets the four-character code of the codec used to compress video frames.
        /// </summary>
        [Description("Specifies the four-character code of the codec used to compress video frames.")]
        public string FourCC { get; set; } = "FMP4";

        /// <summary>
        /// Gets or sets the playback frame rate of the image sequence.
        /// </summary>
        [Description("Specifies the playback frame rate of the image sequence.")]
        public double FrameRate { get; set; } = 30;

        /// <summary>
        /// Gets or sets the optional interpolation method used when resizing video frames.
        /// </summary>
        [Description("Specifies the optional interpolation method if resizing video frames.")]
        public SubPixelInterpolation ResizeInterpolation { get; set; }

        /// <summary>
        /// Gets or sets the number of frames per video segment. Set to 0 for no segmentation (default).
        /// </summary>
        [Description("The number of frames per video segment. Set to 0 for no segmentation (default)")]
        public int SegmentFrames { get; set; } = 0;

        int segmentIndex = 0;
        int frameCount = 0;
        bool hasStarted = false;
        bool wasRecording = false;
        VideoWriterDisposable writer;
        StreamWriter csvWriter;

        string cachedOutputDirectory;

        string GenerateFileName() =>
            Path.Combine(cachedOutputDirectory, $"segment{segmentIndex:D3}.avi");

        VideoWriterDisposable CreateWriter(string fileName, IplImage input)
        {
            var frameSize = input.Size;
            var fourCC = FourCC.Length == 4
                ? VideoWriter.FourCC(FourCC[0], FourCC[1], FourCC[2], FourCC[3])
                : 0;

            Directory.CreateDirectory(Path.GetDirectoryName(fileName));

            lock (SyncRoot)
            {
                var w = new VideoWriter(fileName, fourCC, FrameRate, frameSize, input.Channels > 1);
                return new VideoWriterDisposable(w, frameSize, Disposable.Create(() =>
                {
                    lock (SyncRoot)
                    {
                        w.Close();
                    }
                }));
            }
        }

        void CleanupWriter()
        {
            lock (SyncRoot)
            {
                if (writer != null)
                {
                    writer.Dispose();
                    writer = null;
                }
                if (csvWriter != null)
                {
                    csvWriter.Dispose();
                    csvWriter = null;
                }
                hasStarted = false;
            }
        }

        /// <summary>
        /// Subscribes to the source sequence and writes each image frame and its timestamp to disk while recording is active.
        /// </summary>
        /// <param name="source">The source sequence of <see cref="IplImage"/> frames to write.</param>
        /// <returns>An observable sequence that passes through each <see cref="IplImage"/> from the source.</returns>
        public override IObservable<IplImage> Process(IObservable<IplImage> source)
        {
            if (SegmentFrames == 0) {
                SegmentFrames = int.MaxValue;
            }

            return Observable.Create<IplImage>(observer =>
            {
                var sub = source.Subscribe(input =>
                {
                    try
                    {
                        observer.OnNext(input);

                        // Detect when recording stops and cleanup
                        if (wasRecording && !RecordingService.IsRecording)
                        {
                            CleanupWriter();
                        }
                        wasRecording = RecordingService.IsRecording;

                        if (!RecordingService.IsRecording)
                        {
                            return;
                        }

                        if (!hasStarted || writer == null)
                        {
                            hasStarted = true;
                            segmentIndex = 0;
                            frameCount = 0;

                            cachedOutputDirectory = Path.Combine(
                                MouseInfoService.RootPath,
                                MouseInfoService.MouseID,
                                RecordingService.Date,
                                RecordingService.Time,
                                "Behavior"
                            );

                            writer = CreateWriter(GenerateFileName(), input);

                            csvWriter = new StreamWriter(Path.Combine(cachedOutputDirectory, "Timestamps.csv"));
                            csvWriter.WriteLine("Time Stamp (ms)");
                        }

                        var image = input;
                        if (input.Width != writer.FrameSize.Width || input.Height != writer.FrameSize.Height)
                        {
                            var resized = new IplImage(writer.FrameSize, input.Depth, input.Channels);
                            CV.Resize(input, resized, ResizeInterpolation);
                            image = resized;
                        }

                        writer.Writer.WriteFrame(image);
                        csvWriter.WriteLine($"{TimingService.Stopwatch?.ElapsedMilliseconds ?? 0}");
                        frameCount++;

                        if (frameCount >= SegmentFrames)
                        {
                            writer.Dispose();
                            segmentIndex++;
                            frameCount = 0;
                            writer = CreateWriter(GenerateFileName(), input);
                        }
                    }
                    catch (Exception ex)
                    {
                        observer.OnError(ex);
                    }
                },
                ex => { CleanupWriter(); observer.OnError(ex); },
                () => { CleanupWriter(); observer.OnCompleted(); });

                // ensure cleanup happens when workflow stops, even if OnCompleted never fires
                var cd = new CompositeDisposable(sub, Disposable.Create(() => CleanupWriter()));
                return cd;
            });
        }
    }

    /// <summary>
    /// Wraps a <see cref="VideoWriter"/> together with its target frame size and manages its lifetime.
    /// </summary>
    public sealed class VideoWriterDisposable : ICancelable, IDisposable
    {
        IDisposable resource;

        /// <summary>
        /// Initializes a new instance of the <see cref="VideoWriterDisposable"/> class.
        /// </summary>
        /// <param name="writer">The underlying <see cref="VideoWriter"/> instance.</param>
        /// <param name="frameSize">The target frame size used when writing frames.</param>
        /// <param name="disposable">The disposable that releases the writer's resources.</param>
        internal VideoWriterDisposable(VideoWriter writer, Size frameSize, IDisposable disposable)
        {
            Writer = writer ?? throw new ArgumentNullException(nameof(writer));
            FrameSize = frameSize;
            resource = disposable;
        }

        /// <summary>
        /// Gets the underlying <see cref="VideoWriter"/> used to write video frames.
        /// </summary>
        public VideoWriter Writer { get; }

        /// <summary>
        /// Gets the target frame size used when writing video frames.
        /// </summary>
        public Size FrameSize { get; }

        /// <summary>
        /// Gets a value indicating whether the writer has been disposed.
        /// </summary>
        public bool IsDisposed
        {
            get { return resource == null; }
        }

        /// <summary>
        /// Releases all resources used by the <see cref="VideoWriterDisposable"/>.
        /// </summary>
        public void Dispose()
        {
            var disposable = Interlocked.Exchange(ref resource, null);
            disposable?.Dispose();
        }
    }
}
