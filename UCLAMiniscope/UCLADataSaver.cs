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
    /// <summary>
    /// Saves Miniscope video and a single timestamps.csv containing FrameNumber, TimeStamp_ms,
    /// and optionally quaternion and input columns.
    /// </summary>
    [Description("Saves Miniscope video and a single timestamps.csv (FrameNumber, TimeStamp_ms, and optionally quat/input columns).")]
    [WorkflowElementCategory(ElementCategory.Sink)]
    public class UCLADataSaver : Sink<FrameIMUV4>
    {
        // -- user parameters --------------------------------------------------
        /// <summary>
        /// Gets or sets the number of frames per video segment. Pick a multiple of the frame rate for stable results.
        /// Set to 0 to disable segmentation (default).
        /// </summary>
        [Description("Frames per FFV1 segment. Pick a multiple of the frame-rate for " +
                     "stable results. Set to 0 to not use segmentation (default)")]
        public int SegmentFrames { get; set; } = 0;

        /// <summary>
        /// Gets or sets the base name for output video files. Leaving this blank produces numbered files only (e.g. 1.mkv, 2.mkv).
        /// </summary>
        [Description("Base name for video files (leaving this blank will only number: 1.mkv, 2.mkv …).")]
        public string BaseVideoName { get; set; } = "";

        /// <summary>
        /// Gets or sets the video codec used for the output video files (e.g. <c>ffv1</c>, <c>h264</c>).
        /// </summary>
        [Description("Video codec to use for the output video files.")]
        public string VideoCodec { get; set; } = "ffv1";

        /// <summary>
        /// Gets or sets optional extra arguments appended to the FFmpeg command. <c>-level 3</c> is recommended for ffv1.
        /// </summary>
        [Description("Optional extra arguments for the FFmpeg command. \"-level 3\" recommended for ffv1")]
        public string ExtraCodecArgs { get; set; } = "";

        /// <summary>
        /// Gets or sets the video container format for output files (e.g. <c>mkv</c>, <c>mp4</c>).
        /// </summary>
        [Description("Video container format to use, e.g. mkv, mp4, ...")]
        public string VideoContainer { get; set; } = "mkv";

        /// <summary>
        /// Gets or sets a value indicating whether qw/qx/qy/qz columns are appended to timestamps.csv.
        /// Only applies when the frame type carries quaternion data.
        /// </summary>
        [Description("Append qw/qx/qy/qz columns to timestamps.csv (only applies if the frame type carries quaternion data).")]
        public bool WriteQuaternion { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether an Input column (0/1) is appended at the end of timestamps.csv.
        /// </summary>
        [Description("Append an Input column (0/1) at the end of timestamps.csv.")]
        public bool WriteInput { get; set; } = true;

        /// <summary>
        /// Gets or sets the folder name used for this device under the output path.
        /// </summary>
        [Description("Folder name for this device")]
        public string DeviceFolderName { get; set; } = "Miniscope";

        /// <summary>
        /// Gets or sets the subfolder structure under RootPath. Supports <c>{mouseID}</c>, <c>{date}</c>, and <c>{time}</c>
        /// tokens in any order and combination. Default is <c>{mouseID}/{date}/{time}</c>.
        /// </summary>
        [Description("Subfolder structure under RootPath. Use {mouseID}, {date}, {time} in any order and combination. Default: {mouseID}/{date}/{time}")]
        public string SubFolderPattern { get; set; } = "{mouseID}/{date}/{time}";

        /// <summary>
        /// Gets or sets a value indicating whether the video is cropped to the specified region of interest before writing.
        /// </summary>
        [Description("When true, crops the video to the specified region of interest.")]
        public bool CropOutputVideo { get; set; } = false;

        /// <summary>
        /// Gets or sets a rectangle specifying the region of interest inside the image.
        /// </summary>
        [Description("Specifies the region of interest inside the image.")]
        [Editor("UCLAMiniscope.Helpers.MultiFrameRectangleEditor, UCLAMiniscope", DesignTypes.UITypeEditor)]
        public Rect RegionOfInterest { get; set; }

        // -- main pipeline -------------------------------------------------------
        /// <summary>
        /// Subscribes to the source sequence and writes each <see cref="FrameIMUV4"/> frame and its metadata
        /// to disk while recording is active.
        /// </summary>
        /// <param name="source">The source sequence of <see cref="FrameIMUV4"/> frames to save.</param>
        /// <returns>An observable sequence that passes through each <see cref="FrameIMUV4"/> from the source.</returns>
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
                            frame = new FrameIMUV4(croppedImage, frame.Quaternion, frame.FrameNumber, frame.Timestamp, frame.Input);
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
                            recorder ??= new Recorder(BaseVideoName, VideoContainer, SegmentFrames, Width, Height, VideoCodec, ExtraCodecArgs, WriteQuaternion, WriteInput, hasQuaternion: true, deviceFolderName: DeviceFolderName, subFolderPattern: SubFolderPattern);
                        }
                    }
                    
                        recorder.WriteCsv(frame.FrameNumber, frame.Timestamp, frame.Input, frame.Quaternion);
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
        /// <summary>
        /// Subscribes to the source sequence and writes each <see cref="FrameMiniCam"/> frame and its metadata
        /// to disk while recording is active.
        /// </summary>
        /// <param name="source">The source sequence of <see cref="FrameMiniCam"/> frames to save.</param>
        /// <returns>An observable sequence that passes through each <see cref="FrameMiniCam"/> from the source.</returns>
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
                            frame = new FrameMiniCam(croppedImage, frame.FrameNumber, frame.Timestamp, frame.Input);
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
                                recorder ??= new Recorder(BaseVideoName, VideoContainer, SegmentFrames, Width, Height, VideoCodec, ExtraCodecArgs, WriteQuaternion, WriteInput, hasQuaternion: false, deviceFolderName: DeviceFolderName, subFolderPattern: SubFolderPattern);
                            }
                        }

                        recorder.WriteCsv(frame.FrameNumber, frame.Timestamp, frame.Input, quaternion: null);
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
            readonly StreamWriter csvWriter;
            readonly Process ffmpeg;
            readonly ImageWriter pipeWriter;
            readonly string pipeName;
            readonly Subject<IplImage> frameQueue;
            readonly IDisposable pipeSubscription;
            readonly bool writeQuaternion;
            readonly bool writeInput;
            int frameCounter;

            /// <summary>
            /// Initializes a new <see cref="Recorder"/> instance, creates the output folder, opens the CSV writer,
            /// and launches the FFmpeg process.
            /// </summary>
            /// <param name="baseName">Base name for video files.</param>
            /// <param name="videoContainer">Container format (e.g. mkv, mp4).</param>
            /// <param name="segmentFrames">Number of frames per segment; 0 disables segmentation.</param>
            /// <param name="width">Frame width in pixels.</param>
            /// <param name="height">Frame height in pixels.</param>
            /// <param name="videoCodec">FFmpeg codec name (e.g. ffv1).</param>
            /// <param name="extraCodecArgs">Additional FFmpeg arguments.</param>
            /// <param name="writeQuaternion">Whether to write quaternion columns to the CSV.</param>
            /// <param name="writeInput">Whether to write the Input column to the CSV.</param>
            /// <param name="hasQuaternion">Whether the source frame type carries quaternion data.</param>
            /// <param name="deviceFolderName">Device subfolder name inside the output path.</param>
            /// <param name="subFolderPattern">Subfolder pattern supporting {mouseID}, {date}, and {time} tokens.</param>
            public Recorder(string baseName, string videoContainer, int segmentFrames, int width, int height, string videoCodec, string extraCodecArgs, bool writeQuaternion, bool writeInput, bool hasQuaternion, string deviceFolderName, string subFolderPattern)
            {
                this.writeQuaternion = writeQuaternion && hasQuaternion;
                this.writeInput = writeInput;

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

                // Single CSV — optional columns appended in order: quat then input
                csvWriter = new StreamWriter(path: Path.Combine(folder, "timestamps.csv"), append: false, encoding: Encoding.UTF8);
                var headerBuilder = new StringBuilder("FrameNumber,TimeStamp_ms");
                if (this.writeQuaternion) headerBuilder.Append(",qw,qx,qy,qz");
                if (this.writeInput)      headerBuilder.Append(",Input");
                csvWriter.WriteLine(headerBuilder.ToString());

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
            /// <summary>
            /// Writes one row to the timestamps CSV for the given frame.
            /// </summary>
            /// <param name="frameNumber">Zero-based index of the frame.</param>
            /// <param name="timestamp">Elapsed time in milliseconds at the time the frame was captured.</param>
            /// <param name="input">Digital input state recorded alongside the frame.</param>
            /// <param name="quaternion">Optional orientation quaternion (written only when <see cref="writeQuaternion"/> is enabled).</param>
            public void WriteCsv(ulong frameNumber, long timestamp, bool input, Vector4? quaternion)
            {
                var row = new StringBuilder($"{frameNumber},{timestamp}");
                if (writeQuaternion && quaternion.HasValue)
                {
                    var q = quaternion.Value;
                    row.Append($",{q.W},{q.X},{q.Y},{q.Z}");
                }
                if (writeInput)
                    row.Append($",{(input ? 1 : 0)}");
                csvWriter.WriteLine(row.ToString());
            }

            /// <summary>
            /// Sends an image frame to FFmpeg via the named pipe. Deep-copies the first 50 frames
            /// to ensure the pipe is ready before passing original pointers.
            /// </summary>
            /// <param name="img">The image frame to write.</param>
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
            /// <summary>
            /// Flushes and closes the CSV writer, shuts down the FFmpeg process, and releases all managed resources.
            /// </summary>
            public void Dispose()
            {
                try
                {
                    frameQueue.OnCompleted();
                    pipeSubscription.Dispose();
                    csvWriter.Dispose();
                    ffmpeg.WaitForExit(2000);
                    if (!ffmpeg.HasExited) ffmpeg.Kill();
                    ffmpeg.Dispose();
                }
                catch { /* ignore shutdown errors */ }
            }
        }
    }
}