using OpenCV.Net;
using OpenCvSharp;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Bonsai;
using UCLAMiniscope.Helpers;

namespace UCLAMiniscope
{
    [Description("Produces FrameV4 objects from the UCLA Miniscope V4 using a shared capture instance.")]
    public class UCLAV4_Frame : Source<FrameV4>
    {
        // Frame size
        const int Width = 608;
        const int Height = 608;

        // 1 quaterion = 2^14 bits
        const float QuatConvFactor = 1.0f / (1 << 14);

        // Miniscope properties
        private int ledBrightness = 0;
        private int lastLEDBrightness = 0;
        private int fps = 30;
        private int focus = 0;
        private bool triggered = false;
        private GainV4 gain = GainV4.Low;

        // connection status
        private int connectionTries = 0;

        // Properties subscriptions
        private CompositeDisposable disposables;
        private readonly Subject<int> ledBrightnessSubject = new Subject<int>();
        private readonly Subject<int> fpsSubject = new Subject<int>();
        private readonly Subject<int> focusSubject = new Subject<int>();
        private readonly Subject<bool> triggeredSubject = new Subject<bool>();
        private readonly Subject<GainV4> gainSubject = new Subject<GainV4>();

        [Description("Adjusts the intensity of the miniscope's LED (0-100%).")]
        [Range(0, 100)]
        [Editor(DesignTypes.SliderEditor, DesignTypes.UITypeEditor)]
        public int LEDBrightness
        {
            get => ledBrightness;
            set { ledBrightness = value; ledBrightnessSubject.OnNext(255 - value * 255 / 100); }
        }

        [Description("Sets the framerate.")]
        [Range(10, 30)]
        public int FPS
        {
            get => fps;
            set { fps = value; fpsSubject.OnNext(value); }
        }

        [Description("Sets the EWL focus.")]
        [Range(-127, 127)]
        [Editor(DesignTypes.SliderEditor, DesignTypes.UITypeEditor)]
        public int Focus
        {
            get => focus;
            set { focus = value; focusSubject.OnNext(value); }
        }

        [Description("Sets the gain of the image sensor.")]
        public GainV4 Gain
        {
            get => gain;
            set { gain = value; gainSubject.OnNext(value); }
        }

        [Description("Turns off the LED when the trigger input is low. " +
            "Note that this pin is low by default. Therefore, if it is not driven and " +
            "this option is set to true, the LED will not turn on.")]
        public bool Triggered
        {
            get => triggered;
            set { triggered = value; triggeredSubject.OnNext(value); }
        }

        [Description("Index of the miniscope to capture from.")]
        public int CameraIndex { get; set; } = 0;

        [Description("True if you want to wait for reconnection, false if you want the workflow to stop.")]
        public bool WaitForReconnection { get; set; } = true;

        readonly IObservable<FrameIMUV4> source;

        public override IObservable<FrameV4> Generate()
        {
            return Observable.Create<FrameV4>((observer, cancellationToken) =>
            {
                return Task.Factory.StartNew(() =>
                {
                    var startTimeUtc = DateTime.UtcNow.Ticks - Stopwatch.GetTimestamp();

                    using (var capture = new VideoCapture(CameraIndex, VideoCaptureAPIs.DSHOW))
                    {

                        while (!cancellationToken.IsCancellationRequested)
                        {
                            try
                            {

                                if (!capture.IsOpened())
                                {
                                    throw new NoMiniscopeException();
                                }

                                // Register capture with CaptureService
                                string deviceId = $"V4_{CameraIndex}";
                                CaptureService.RegisterCapture(deviceId, capture, "V4");

                                var frame = new OpenCvSharp.Mat();
                                var grayFrame = new OpenCvSharp.Mat();
                                
                                try
                                {
                                    Hardware.V4.Initialize(capture);

                                    // Set frame size
                                    capture.Set(VideoCaptureProperties.FrameWidth, Width);
                                    capture.Set(VideoCaptureProperties.FrameHeight, Height);

                                    // Start Digital Output switching
                                    capture.Set(VideoCaptureProperties.Saturation, 1);

                                    Thread.Sleep(10); // to let everything settle

                                    // Subscribe to property changes
                                    disposables = new CompositeDisposable
                                    {
                                        ledBrightnessSubject.DistinctUntilChanged().Subscribe(LEDBrightness => Hardware.V4.SetLEDBrightness(capture, LEDBrightness)),
                                        fpsSubject.DistinctUntilChanged().Subscribe(FPS => Hardware.V4.SetFPS(capture, FPS)),
                                        focusSubject.DistinctUntilChanged().Subscribe(Focus => Hardware.V4.SetFocus(capture, Focus)),
                                        triggeredSubject.DistinctUntilChanged().Subscribe(Triggered => Hardware.V4.SetTriggerMode(capture, Triggered)),
                                        gainSubject.DistinctUntilChanged().Subscribe(Gain => Hardware.V4.SetGain(capture, Gain)),
                                    };

                                    // Set propoerties to initial values
                                    Hardware.V4.SetTriggerMode(capture, Triggered);
                                    Hardware.V4.SetFPS(capture, FPS);
                                    Hardware.V4.SetGain(capture, gain);
                                    Hardware.V4.SetFocus(capture, 127 * focus / 100);
                                    Hardware.V4.SetLEDBrightness(capture, 255 - LEDBrightness * 255 / 100); // This needs to be set last

                                    // Initialize frame offset
                                    ushort lastContrast = (ushort)capture.Get(VideoCaptureProperties.Contrast);
                                    int frameOffset = -lastContrast;
                                    CaptureService.SetFrameOffset(deviceId, -lastContrast);

                                    ulong frameNumber = 0;

                                    while (!cancellationToken.IsCancellationRequested)
                                    {
                                        capture.Read(frame);
                                        
                                        var timestamp = (ulong)(startTimeUtc + Stopwatch.GetTimestamp());

                                        if (frame.Empty())
                                        {
                                            // Do we want it to throw an error instead of trying to reconnect?
                                            Console.WriteLine("Camera might be disconnected, let's try to reconnect");
                                            break;
                                        }

                                        ushort contrast = (ushort)capture.Get(VideoCaptureProperties.Contrast);

                                        if (contrast < lastContrast)
                                        {
                                            frameOffset = (int)(frameNumber + 1);
                                            CaptureService.SetFrameOffset(deviceId, frameOffset);
                                        }

                                        lastContrast = contrast;

                                        frameNumber = (ulong)(contrast + frameOffset);

                                        Cv2.CvtColor(frame, grayFrame, ColorConversionCodes.BGR2GRAY);
                                        var image = new IplImage(new OpenCV.Net.Size(grayFrame.Cols, grayFrame.Rows), IplDepth.U8, 1, grayFrame.Data);
                                        observer.OnNext(new FrameV4(image, frameNumber, timestamp));
                                    }
                                }
                                catch
                                {
                                }
                                finally
                                {
                                    CaptureService.UnregisterCapture(deviceId);
                                    capture.Set(VideoCaptureProperties.Saturation, 0);
                                    Hardware.V4.SetLEDBrightness(capture, 255);
                                    Thread.Sleep(10); // to let the LED switch off
                                    frame.Dispose();
                                    grayFrame.Dispose();
                                    capture.Release();
                                    capture.Dispose();
                                    disposables.Dispose();
                                }
                            }
                            catch (NoMiniscopeException)
                            {
                                if (WaitForReconnection)
                                {

                                    Console.WriteLine($"miniscope couldn't be reached ({connectionTries})");
                                    connectionTries++;
                                    Thread.Sleep(1000);
                                }
                                else
                                {
                                    throw new WorkflowException("No miniscope or wrong index");
                                }
                            }
                        }
                    }
                }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            })
            .PublishReconnectable()
            .RefCount();
        }
    }
}
