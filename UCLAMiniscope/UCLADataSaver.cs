using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using Bonsai;
using Bonsai.Vision;
using OpenCV.Net;
using UCLAMiniscope.Helpers;

namespace UCLAMiniscope
{
    [Description("Saves Miniscope video, timestamps, quaternions, and metadata.")]
    [WorkflowElementCategory(ElementCategory.Sink)]
    public class UCLADataSaver : Sink<FrameIMUV4>
    {
        // -- user parameters --------------------------------------------------
        [Description("Frames per FFV1 segment. Pick a multiple of the frame-rate for " +
                     "stable results. Set to 0 to not use segmentation (default)")]
        public int SegmentFrames { get; set; } = 0;

        [Description("Base name for video files (leaving this blank will only number: 1.mkv, 2.mkv …).")]
        public string BaseVideoName { get; set; } = "";

        [Description("Video codec to use for the output video files.")]
        public string VideoCodec { get; set; } = "ffv1";

        [Description("Optional extra arguments for the FFmpeg command. \"-level 3\" recommended for ffv1")]
        public string ExtraCodecArgs { get; set; } = "";

        [Description("Video container format to use, e.g. mkv, mp4, ...")]
        public string VideoContainer { get; set; } = "mkv";

        [Description("Write quaternion data to headOrientation.csv (only applies if frame contains quaternion data).")]
        public bool WriteQuaternion { get; set; } = true;

        [Description("Folder name for this device")]
        public string DeviceFolderName { get; set; } = "Miniscope";

        [Description("Subfolder structure under RootPath. Use {mouseID}, {date}, {time} in any order and combination. Default: {mouseID}/{date}/{time}")]
        public string SubFolderPattern { get; set; } = "{mouseID}/{date}/{time}";

        [Description("When true, crops the video to the specified region of interest.")]
        public bool CropOutputVideo { get; set; } = false;

        /// <summary>
        /// Gets or sets a rectangle specifying the region of interest inside the image.
        /// </summary>
        [Description("Specifies the region of interest inside the image.")]
        [Editor("UCLAMiniscope.Helpers.MultiFrameRectangleEditor, UCLAMiniscope", DesignTypes.UITypeEditor)]
        public Rect RegionOfInterest { get; set; }

        // -- main pipeline -------------------------------------------------------
        public override IObservable<FrameIMUV4> Process(IObservable<FrameIMUV4> source)
        {
            return Observable.Create<FrameIMUV4>(observer =>
            {
                Recorder recorder = null;
                var gate = new object();

                var sub = source.Subscribe(frame =>
                {
                try
                {
                        if (CropOutputVideo)
                        {
                            var croppedImage = frame.Image.GetSubRect(RegionOfInterest);
                            frame = new FrameIMUV4(croppedImage, frame.Quaternion, frame.FrameNumber, frame.Timestamp);
                        }

                        if (!RecordingService.IsRecording)
                        {
                            observer.OnNext(frame);
                            return;
                        }

                    // initialise once, **exactly on the first recorded frame**
                    if (recorder == null)
                    {
                        var Width = frame.Image.Width;
                        var Height = frame.Image.Height;

                        lock (gate)
                        {
                            recorder ??= new Recorder(BaseVideoName, VideoContainer, SegmentFrames, Width, Height, VideoCodec, ExtraCodecArgs, WriteQuaternion, hasQuaternion: true, deviceFolderName: DeviceFolderName, subFolderPattern: SubFolderPattern);
                        }
                    }
                    
                        recorder.WriteCsv(frame.FrameNumber, frame.Timestamp, frame.Quaternion);
                        recorder.WriteVideo(frame.Image);
                        observer.OnNext(frame);
                    }
                    catch (Exception ex) { observer.OnError(ex); }
                },
                ex => { recorder?.Dispose(); observer.OnError(ex); },
                () => { recorder?.Dispose(); observer.OnCompleted(); });

                // ensure recorder disposed when workflow stops, even if OnCompleted never fires
                var cd = new CompositeDisposable(sub, Disposable.Create(() => recorder?.Dispose()));
                return cd;
            });
        }

        // -- overload for FrameMiniCam ------------------------------------
        public IObservable<FrameMiniCam> Process(IObservable<FrameMiniCam> source)
        {
            return Observable.Create<FrameMiniCam>(observer =>
            {
                Recorder recorder = null;
                var gate = new object();

                var sub = source.Subscribe(frame =>
                {
                    try
                    {
                        if (CropOutputVideo)
                        {
                            var croppedImage = frame.Image.GetSubRect(RegionOfInterest);
                            frame = new FrameMiniCam(croppedImage, frame.FrameNumber, frame.Timestamp);
                        }

                        if (!RecordingService.IsRecording)
                        {
                            observer.OnNext(frame);
                            return;
                        }

                        // initialise once, on the first recorded frame
                        if (recorder == null)
                        {
                            var Width = frame.Image.Width;
                            var Height = frame.Image.Height;

                            lock (gate)
                            {
                                recorder ??= new Recorder(BaseVideoName, VideoContainer, SegmentFrames, Width, Height, VideoCodec, ExtraCodecArgs, WriteQuaternion, hasQuaternion: false, deviceFolderName: DeviceFolderName, subFolderPattern: SubFolderPattern);
                            }
                        }

                        recorder.WriteCsv(frame.FrameNumber, frame.Timestamp, quaternion: null);
                        recorder.WriteVideo(frame.Image);
                        observer.OnNext(frame);
                    }
                    catch (Exception ex) { observer.OnError(ex); }
                },
                ex => { recorder?.Dispose(); observer.OnError(ex); },
                () => { recorder?.Dispose(); observer.OnCompleted(); });

                // ensure recorder disposed when workflow stops, even if OnCompleted never fires
                var cd = new CompositeDisposable(sub, Disposable.Create(() => recorder?.Dispose()));
                return cd;
            });
        }

        // ------------------------- helper -------------------------------
        sealed class Recorder : IDisposable
        {
            readonly StreamWriter tsWriter;
            readonly StreamWriter quatWriter;
            readonly Process ffmpeg;
            readonly ImageWriter pipeWriter;
            readonly string pipeName;
            readonly Subject<IplImage> frameQueue;
            readonly IDisposable pipeSubscription;
            readonly bool writeQuaternion;
            int frameCounter;

            public Recorder(string baseName, string videoContainer, int segmentFrames, int width, int height, string videoCodec, string extraCodecArgs, bool writeQuaternion, bool hasQuaternion, string deviceFolderName, string subFolderPattern)
            {
                this.writeQuaternion = writeQuaternion && hasQuaternion;

                // folder creation
                string mouseID = MouseInfoService.MouseID;
                string rootPath = MouseInfoService.RootPath;
                string date = RecordingService.Date;
                string time = RecordingService.Time;

                // Build the subfolder from the user-defined pattern
                string subFolder = subFolderPattern
                    .Replace("{mouseID}", mouseID)
                    .Replace("{date}", date)
                    .Replace("{time}", time);

                string folder = string.IsNullOrWhiteSpace(subFolder)
                    ? Path.Combine(rootPath, deviceFolderName)
                    : Path.Combine(rootPath, subFolder, deviceFolderName);
                Directory.CreateDirectory(folder);

                // Sanitize base name
                baseName = baseName.Replace("%d", "");

                // CSVs
                tsWriter = new StreamWriter(path: Path.Combine(folder, "timestamps.csv"), append: false, encoding: Encoding.UTF8);
                tsWriter.WriteLine("FrameNumber,TimeStamp_ms");

                // Only create quaternion writer if needed
                if (this.writeQuaternion)
                {
                    quatWriter = new StreamWriter(path: Path.Combine(folder, "headOrientation.csv"), append: false, encoding: Encoding.UTF8);
                    quatWriter.WriteLine("FrameNumber,TimeStamp_ms,qw,qx,qy,qz");
                }

                // pipe server
                pipeName = $@"\\.\pipe\{Guid.NewGuid():N}";
                pipeWriter = new ImageWriter { Path = pipeName };
                frameQueue = new Subject<IplImage>();
                pipeSubscription = pipeWriter.Process(frameQueue).Subscribe();

                // FFmpeg
                int fps = RecordingMetadataService.Metadata.frameRate;
                string outTemplate = "";
                string segTimeStr = "";

                if (segmentFrames > 0)
                {
                    outTemplate = Path.Combine(folder, $"{baseName}%d.{videoContainer}");
                    double segTime = segmentFrames / (double)fps;
                    segTimeStr = $"-f segment -segment_time {segTime.ToString(CultureInfo.InvariantCulture)} -reset_timestamps 1 -segment_start_number 0";
                }
                else
                {
                    outTemplate = Path.Combine(folder, $"{(string.IsNullOrWhiteSpace(baseName) ? "video" : baseName)}.{videoContainer}");
                }

                string args =
                    $"-f rawvideo -vcodec rawvideo -s {width}x{height} -r {fps} -pix_fmt gray " +
                    $"-i {pipeName} " +
                    $"-fps_mode passthrough " +
                    $"-c:v {videoCodec} {extraCodecArgs} " +
                    $"{segTimeStr} " +
                    $"\"{outTemplate}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                ffmpeg = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg failed to start");
                ffmpeg.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine("[ffmpeg] " + e.Data); };
                ffmpeg.BeginErrorReadLine();

                Console.WriteLine("[Saver] FFmpeg launched: " + args);
            }

            // -------- write helpers -------------------------------------
            public void WriteCsv(ulong frameNumber, long timestamp, Vector4? quaternion)
            {
                tsWriter.WriteLine($"{frameNumber},{timestamp}");

                if (writeQuaternion && quaternion.HasValue)
                {
                    var q = quaternion.Value;
                    quatWriter.WriteLine($"{frameNumber},{timestamp},{q.W},{q.X},{q.Y},{q.Z}");
                }
            }

            public void WriteVideo(IplImage img)
            {
                if (frameCounter < 50)
                {
                    // Deep copy the first few frames as the pipe writer may not be ready before next frame
                    var safeCopy = new IplImage(img.Size, img.Depth, img.Channels);
                    CV.Copy(img, safeCopy);
                    frameQueue.OnNext(safeCopy);
                }
                else
                {
                    // we should be safe sending the original pointer now
                    frameQueue.OnNext(img);
                }

                if (++frameCounter % 1000 == 0)
                    Console.WriteLine($"[Saver] wrote {frameCounter:N0} frames");
            }

            // -------- cleanup -------------------------------------------
            public void Dispose()
            {
                try
                {
                    frameQueue.OnCompleted();
                    pipeSubscription.Dispose();
                    tsWriter.Dispose();
                    quatWriter?.Dispose(); // might be null if quaternion writing disabled
                    ffmpeg.WaitForExit(2000);
                    if (!ffmpeg.HasExited) ffmpeg.Kill();
                    ffmpeg.Dispose();
                }
                catch { /* ignore shutdown errors */ }
            }
        }
    }
}