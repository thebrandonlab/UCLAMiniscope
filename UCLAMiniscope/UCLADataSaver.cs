// SPDX-FileCopyrightText: 2026 Clément Bourguignon
// SPDX-License-Identifier: MIT

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using Bonsai;
using Bonsai.Vision;
using OpenCV.Net;
using UCLAMiniscope.Helpers;

namespace UCLAMiniscope
{
    /// <summary>
    /// Saves Miniscope video and a timestamps.csv containing every metadata field available
    /// on the input frame type.
    /// </summary>
    [Description("Saves Miniscope video and all frame metadata to timestamps.csv.")]
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
                            frame = new FrameIMUV4(
                                croppedImage,
                                frame.Quaternion,
                                frame.FrameNumber,
                                frame.ReceiveTimestamp,
                                frame.HardwareTimestamp,
                                frame.DigitalInputs);
                        }

                        if (!RecordingService.IsRecording)
                        {
                            observer.OnNext(frame);
                            return;
                        }

                        // Initialize the writer from the first frame dimensions, but do not record
                        // frames received before FFmpeg has connected to the named pipe.
                        if (recorder == null)
                        {
                            int width = frame.Image.Width;
                            int height = frame.Image.Height;

                            lock (gate)
                            {
                                recorder ??= new Recorder(
                                    BaseVideoName,
                                    VideoContainer,
                                    SegmentFrames,
                                    width,
                                    height,
                                    VideoCodec,
                                    ExtraCodecArgs,
                                    CsvSchema.CurrentV4,
                                    deviceFolderName: DeviceFolderName,
                                    subFolderPattern: SubFolderPattern);
                            }

                            observer.OnNext(frame);
                            return;
                        }

                        if (!recorder.IsReady)
                        {
                            observer.OnNext(frame);
                            return;
                        }

                        recorder.WriteCsv(frame);
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

        /// <summary>
        /// Saves frames produced by the legacy-firmware V4 source.
        /// </summary>
        public IObservable<LegacyFrameIMUV4> Process(IObservable<LegacyFrameIMUV4> source)
        {
            return Observable.Create<LegacyFrameIMUV4>(observer =>
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
                            frame = new LegacyFrameIMUV4(
                                croppedImage,
                                frame.Quaternion,
                                frame.FrameNumber,
                                frame.ReceiveTimestamp,
                                frame.Input);
                        }

                        if (!RecordingService.IsRecording)
                        {
                            observer.OnNext(frame);
                            return;
                        }

                        if (recorder == null)
                        {
                            int width = frame.Image.Width;
                            int height = frame.Image.Height;

                            lock (gate)
                            {
                                recorder ??= new Recorder(
                                    BaseVideoName,
                                    VideoContainer,
                                    SegmentFrames,
                                    width,
                                    height,
                                    VideoCodec,
                                    ExtraCodecArgs,
                                    CsvSchema.LegacyV4,
                                    deviceFolderName: DeviceFolderName,
                                    subFolderPattern: SubFolderPattern);
                            }

                            observer.OnNext(frame);
                            return;
                        }

                        if (!recorder.IsReady)
                        {
                            observer.OnNext(frame);
                            return;
                        }

                        recorder.WriteCsv(frame);
                        recorder.WriteVideo(frame.Image);
                        observer.OnNext(frame);
                    }
                    catch (Exception ex)
                    {
                        observer.OnError(ex);
                    }
                },
                ex => { recorder?.Dispose(); observer.OnError(ex); },
                () => { recorder?.Dispose(); observer.OnCompleted(); });

                return new CompositeDisposable(sub, Disposable.Create(() => recorder?.Dispose()));
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
                            frame = new FrameMiniCam(
                                croppedImage,
                                frame.FrameNumber,
                                frame.ReceiveTimestamp,
                                frame.HardwareTimestamp,
                                frame.DigitalInputs);
                        }

                        if (!RecordingService.IsRecording)
                        {
                            observer.OnNext(frame);
                            return;
                        }

                        // Initialize the writer from the first frame dimensions, but do not record
                        // frames received before FFmpeg has connected to the named pipe.
                        if (recorder == null)
                        {
                            int width = frame.Image.Width;
                            int height = frame.Image.Height;

                            lock (gate)
                            {
                                recorder ??= new Recorder(
                                    BaseVideoName,
                                    VideoContainer,
                                    SegmentFrames,
                                    width,
                                    height,
                                    VideoCodec,
                                    ExtraCodecArgs,
                                    CsvSchema.CurrentMiniCam,
                                    deviceFolderName: DeviceFolderName,
                                    subFolderPattern: SubFolderPattern);
                            }

                            observer.OnNext(frame);
                            return;
                        }

                        if (!recorder.IsReady)
                        {
                            observer.OnNext(frame);
                            return;
                        }

                        recorder.WriteCsv(frame);
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

        // -- overload for LegacyFrameMiniCam ------------------------------
        /// <summary>
        /// Subscribes to the legacy-firmware MiniCam sequence and writes every available metadata field.
        /// </summary>
        /// <param name="source">The legacy-firmware MiniCam frames to save.</param>
        /// <returns>An observable sequence that passes through every source frame.</returns>
        public IObservable<LegacyFrameMiniCam> Process(IObservable<LegacyFrameMiniCam> source)
        {
            return Observable.Create<LegacyFrameMiniCam>(observer =>
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
                            frame = new LegacyFrameMiniCam(
                                croppedImage,
                                frame.FrameNumber,
                                frame.ReceiveTimestamp,
                                frame.Input);
                        }

                        if (!RecordingService.IsRecording)
                        {
                            observer.OnNext(frame);
                            return;
                        }

                        if (recorder == null)
                        {
                            int width = frame.Image.Width;
                            int height = frame.Image.Height;

                            lock (gate)
                            {
                                recorder ??= new Recorder(
                                    BaseVideoName,
                                    VideoContainer,
                                    SegmentFrames,
                                    width,
                                    height,
                                    VideoCodec,
                                    ExtraCodecArgs,
                                    CsvSchema.LegacyMiniCam,
                                    deviceFolderName: DeviceFolderName,
                                    subFolderPattern: SubFolderPattern);
                            }

                            observer.OnNext(frame);
                            return;
                        }

                        if (!recorder.IsReady)
                        {
                            observer.OnNext(frame);
                            return;
                        }

                        recorder.WriteCsv(frame);
                        recorder.WriteVideo(frame.Image);
                        observer.OnNext(frame);
                    }
                    catch (Exception ex)
                    {
                        observer.OnError(ex);
                    }
                },
                ex => { recorder?.Dispose(); observer.OnError(ex); },
                () => { recorder?.Dispose(); observer.OnCompleted(); });

                return new CompositeDisposable(sub, Disposable.Create(() => recorder?.Dispose()));
            });
        }

        enum CsvSchema
        {
            CurrentV4,
            LegacyV4,
            CurrentMiniCam,
            LegacyMiniCam
        }

        // ------------------------- helper -------------------------------
        sealed class Recorder : IDisposable
        {
            readonly StreamWriter csvWriter;
            readonly Process ffmpeg;
            readonly ReadyImageWriter pipeWriter;
            readonly string pipeName;
            readonly Subject<IplImage> frameQueue;
            readonly IDisposable pipeSubscription;
            int frameCounter;

            public bool IsReady => pipeWriter.IsReady;

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
            /// <param name="csvSchema">Metadata schema determined by the input frame type.</param>
            /// <param name="deviceFolderName">Device subfolder name inside the output path.</param>
            /// <param name="subFolderPattern">Subfolder pattern supporting {mouseID}, {date}, and {time} tokens.</param>
            public Recorder(
                string baseName,
                string videoContainer,
                int segmentFrames,
                int width,
                int height,
                string videoCodec,
                string extraCodecArgs,
                CsvSchema csvSchema,
                string deviceFolderName,
                string subFolderPattern)
            {
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

                csvWriter = new StreamWriter(path: Path.Combine(folder, "timestamps.csv"), append: false, encoding: Encoding.UTF8);
                string csvHeader = csvSchema switch
                {
                    CsvSchema.CurrentV4 => "FrameNumber,ReceiveTimestamp_ms,HardwareTimestamp_ms,qw,qx,qy,qz,Input0,Input1",
                    CsvSchema.LegacyV4 => "FrameNumber,ReceiveTimestamp_ms,qw,qx,qy,qz,Input",
                    CsvSchema.CurrentMiniCam => "FrameNumber,ReceiveTimestamp_ms,HardwareTimestamp_ms,Input0,Input1",
                    CsvSchema.LegacyMiniCam => "FrameNumber,ReceiveTimestamp_ms,Input",
                    _ => throw new ArgumentOutOfRangeException(nameof(csvSchema))
                };
                csvWriter.WriteLine(csvHeader);

                // TODO: Consider a bounded startup buffer to preserve frames received while FFmpeg connects.
                // Buffered frames must own their image data and keep the CSV and video sequences aligned.
                // pipe server
                pipeName = $@"\\.\pipe\{Guid.NewGuid():N}";
                pipeWriter = new ReadyImageWriter { Path = pipeName };
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
            public void WriteCsv(FrameIMUV4 frame)
            {
                var q = frame.Quaternion;
                csvWriter.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4},{5},{6},{7},{8}",
                    frame.FrameNumber,
                    frame.ReceiveTimestamp,
                    frame.HardwareTimestamp,
                    q.W,
                    q.X,
                    q.Y,
                    q.Z,
                    frame.Input0 ? 1 : 0,
                    frame.Input1 ? 1 : 0));
            }

            public void WriteCsv(LegacyFrameIMUV4 frame)
            {
                var q = frame.Quaternion;
                csvWriter.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4},{5},{6}",
                    frame.FrameNumber,
                    frame.ReceiveTimestamp,
                    q.W,
                    q.X,
                    q.Y,
                    q.Z,
                    frame.Input ? 1 : 0));
            }

            public void WriteCsv(FrameMiniCam frame)
            {
                csvWriter.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4}",
                    frame.FrameNumber,
                    frame.ReceiveTimestamp,
                    frame.HardwareTimestamp,
                    frame.Input0 ? 1 : 0,
                    frame.Input1 ? 1 : 0));
            }

            public void WriteCsv(LegacyFrameMiniCam frame)
            {
                csvWriter.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0},{1},{2}",
                    frame.FrameNumber,
                    frame.ReceiveTimestamp,
                    frame.Input ? 1 : 0));
            }

            /// <summary>
            /// Sends an image frame to FFmpeg via the connected named pipe.
            /// </summary>
            /// <param name="img">The image frame to write.</param>
            public void WriteVideo(IplImage img)
            {
                frameQueue.OnNext(img);

                if (++frameCounter % 1000 == 0)
                    Console.WriteLine($"[Saver] wrote {frameCounter:N0} frames");
            }

            sealed class ReadyImageWriter : ImageWriter
            {
                int isReady;

                public bool IsReady => Volatile.Read(ref isReady) != 0;

                protected override BinaryWriter CreateWriter(Stream stream)
                {
                    var writer = base.CreateWriter(stream);
                    Volatile.Write(ref isReady, 1);
                    Console.WriteLine("[Saver] FFmpeg pipe connected; recording frames.");
                    return writer;
                }
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
