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
    [Description("Writes behavior video and timestamps to disk while recording is active.")]
    [WorkflowElementCategory(ElementCategory.Sink)]
    public class BehaviorVideoWriter : Sink<IplImage>
    {
        static readonly object SyncRoot = new();

        [Description("Specifies the four-character code of the codec used to compress video frames.")]
        public string FourCC { get; set; } = "FMP4";

        [Description("Specifies the playback frame rate of the image sequence.")]
        public double FrameRate { get; set; } = 30;

        //[Description("The optional size of video frames.")]
        //public Size FrameSize { get; set; }

        [Description("Specifies the optional interpolation method if resizing video frames.")]
        public SubPixelInterpolation ResizeInterpolation { get; set; }

        [Description("The number of frames per video segment.")]
        public int SegmentFrames { get; set; } = 1000;

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
            //var frameSize = FrameSize.Width > 0 && FrameSize.Height > 0 ? FrameSize : input.Size;
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

        public override IObservable<IplImage> Process(IObservable<IplImage> source)
        {
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

    public sealed class VideoWriterDisposable : ICancelable, IDisposable
    {
        IDisposable resource;

        internal VideoWriterDisposable(VideoWriter writer, Size frameSize, IDisposable disposable)
        {
            Writer = writer ?? throw new ArgumentNullException(nameof(writer));
            FrameSize = frameSize;
            resource = disposable;
        }

        public VideoWriter Writer { get; }

        public Size FrameSize { get; }

        public bool IsDisposed
        {
            get { return resource == null; }
        }

        public void Dispose()
        {
            var disposable = Interlocked.Exchange(ref resource, null);
            disposable?.Dispose();
        }
    }
}
