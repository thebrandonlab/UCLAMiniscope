/*
Description:
Based on the official Bonsai.FFmepg VideoWriter operator, but with the following fixes:
  - Changed the very long 1 second delay before starting the ffmpeg process to a much shorter 10 ms delay,
    which should be sufficient for the ImageWriter to start writing frames to the pipe on most hardware,
    removing the 1 second freeze at the start of recording.
  - Added 16 bit grayscale support.
*/

using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using OpenCV.Net;
using Bonsai.Vision;
using Bonsai.IO;
using System.IO;

namespace Bonsai.FFmpeg
{
    /// <summary>
    /// Represents an operator that writes a sequence of images to a video file using an FFmpeg process.
    /// </summary>
    [DefaultProperty("FileName")]
    [Description("Writes a sequence of images to a video file using an FFmpeg process.")]
    public class VideoWriter : Sink<IplImage>
    {
        /// <summary>
        /// Gets or sets the name of the output file.
        /// </summary>
        [Description("The name of the output file.")]
        [Editor("Bonsai.Design.SaveFileNameEditor, Bonsai.Design", DesignTypes.UITypeEditor)]
        public string FileName { get; set; }

        /// <summary>
        /// Gets or sets the optional suffix used to generate file names.
        /// </summary>
        [Description("The optional suffix used to generate file names.")]
        public PathSuffix Suffix { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the output file should be overwritten if it already exists.
        /// </summary>
        [Description("Indicates whether the output file should be overwritten if it already exists.")]
        public bool Overwrite { get; set; }

        /// <summary>
        /// Gets or sets the playback frame rate of the image sequence.
        /// </summary>
        [Description("The playback frame rate of the image sequence.")]
        public int FrameRate { get; set; }

        /// <summary>
        /// Gets or sets the optional set of command-line arguments to use for configuring the video codec.
        /// </summary>
        [Description("The optional set of command-line arguments to use for configuring the video codec.")]
        public string OutputArguments { get; set; }

        /// <summary>
        /// Writes an observable sequence of images to a video file using an FFmpeg process.
        /// </summary>
        /// <param name="source">
        /// A sequence of <see cref="IplImage"/> objects representing the individual video
        /// frames to include in the output file.
        /// </param>
        /// <returns>
        /// An observable sequence that is identical to the <paramref name="source"/>
        /// sequence but where there is an additional side effect of writing the
        /// sequence of images to a video file using an FFmpeg process.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// The file already exists.
        /// </exception>
        public override IObservable<IplImage> Process(IObservable<IplImage> source)
        {
            return source.Publish(ps =>
            {
                var fileName = PathHelper.AppendSuffix(FileName, Suffix);
                var overwrite = Overwrite;
                if (File.Exists(fileName) && !overwrite)
                {
                    throw new InvalidOperationException(string.Format("The file '{0}' already exists.", fileName));
                }

                PathHelper.EnsureDirectory(fileName);
                var pipe = $@"\\.\pipe\{Guid.NewGuid():N}";
                var writer = new ImageWriter { Path = pipe };
                return writer.Process(ps).Merge(ps.Take(1).Delay(TimeSpan.FromMilliseconds(10)).SelectMany(image =>
                {
                    var pixFmt = GetRawPixFmt(image);
                    var args = $"-f rawvideo -vcodec rawvideo {(overwrite ? "-y " : string.Empty)}" +
                               $"-s {image.Width}x{image.Height} " +
                               $"-r {FrameRate} -pix_fmt {pixFmt} -i {pipe} " +
                               $"{OutputArguments} {fileName}";
                    var ffmpeg = new StartProcess();
                    ffmpeg.Arguments = args;
                    ffmpeg.FileName = "ffmpeg.exe";
                    return ffmpeg.Generate().IgnoreElements().Select(x => default(IplImage));
                }));
            });
        }

        private static string GetRawPixFmt(IplImage img)
        {
            if (img == null) throw new ArgumentNullException(nameof(img));

            if (img.Channels == 1)
            {
                return img.Depth switch
                {
                    IplDepth.U8 => "gray",
                    IplDepth.U16 => "gray16le",
                    _ => throw new NotSupportedException($"Unsupported grayscale depth: {img.Depth}")
                };
            }

            if (img.Channels == 3)
            {
                return img.Depth switch
                {
                    IplDepth.U8 => "bgr24",
                    _ => throw new NotSupportedException($"Unsupported BGR depth: {img.Depth}")
                };
            }

            throw new NotSupportedException($"Unsupported channel count: {img.Channels}");
        }

    }
}