using Gst;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using Gst.Video;
using GLib;
using Gst.App;

namespace SimpleToDoList.Models
{

    // TODO:: Video rendering is remaining
    public class MediaPlayer
    {
        private Pipeline _pipeline;
        private string _mediaUri;
        private Element _videoSink;
        private  Bus _bus;
        public event Action<State, State> OnStateChanged;
        public State mediaState { get; private set; } = State.Null;
        private Sample? _lastSample; // Store the most recent sample for snapshotElement appSinkTemp
        private AppSink _appSink;
        // Metadata variables
        int width = 0, height = 0;
        double  framerate = 25.0;

        // Flag to indicate metadata is retrieved
        bool metadataRetrieved = false;

        static MediaPlayer()
        {
            Gst.Application.Init();
        }


        public MediaPlayer(string mediaUri)
        {
            _mediaUri = mediaUri;

        }
        private void InitializePipeline()
        {
            if (_pipeline != null) return;

            // Set up video sink for rendering
            _videoSink = ElementFactory.Make("glimagesink", "video_sink");
            if (_videoSink == null)
            {
                throw new InvalidOperationException("Failed to create video sink (glimagesink). Make sure GStreamer is installed properly.");
            }

            string pipelineDescription;
            if (_mediaUri.StartsWith("rtsp://"))
            {
                pipelineDescription = $"rtspsrc location={_mediaUri} ! decodebin ! autovideosink name=videoSink";
            }
            else
            {
                pipelineDescription = $"filesrc location={_mediaUri} ! decodebin ! autovideosink name=videoSink";
            }

            _pipeline = Parse.Launch(pipelineDescription) as Pipeline;

            _bus = _pipeline.Bus;
            _bus.AddSignalWatch();
            _bus.Message += OnBusMessage;


            //_appSink = _pipeline.GetByName("videoSink");

            if (_pipeline == null || _appSink == null)
            {
                Console.WriteLine("Pipeline or appsink is not initialized.");
                return;
            }

            //// Configure the appsink
            //_appSink.EmitSignals = true;
            //_appSink.Caps = Caps.FromString("video/x-raw,format=RGB");
            //_appSink.NewSample += OnNewSample;



        }

        private void OnBusMessage(object? sender, MessageArgs args)
        {
            var message = args.Message;

            // Handle state change messages
            if (message.Type == MessageType.StateChanged)
            {
                message.ParseStateChanged(out State oldState, out State newState, out State _);

                // Notify subscribers about the state change
                OnStateChanged?.Invoke(oldState, newState);
                mediaState = newState;

                // Debug output (optional)
                Console.WriteLine($"State changed: {oldState} -> {newState} )");
            }
        }


        public void Play()
        {
            InitializePipeline();
            _pipeline.SetState(State.Playing);
            Console.WriteLine("Playback started.");
        }

        public void Pause()
        {
            if (_pipeline != null)
            {
                _pipeline.SetState(State.Paused);
                Console.WriteLine("Playback paused.");
            }
        }

        public void Stop()
        {
            if (_pipeline != null)
            {
                _pipeline.SetState(State.Null);
                Console.WriteLine("Playback stopped.");
            }
        }

        public void Resume()
        {
            if (_pipeline != null)
            {
                _pipeline.SetState(State.Playing);
                Console.WriteLine("Playback resumed.");
            }
        }

        private void OnNewSample(object o, NewSampleArgs args)
        {
            // Access the sample from the args
            var appsink = (AppSink)o;
            _lastSample = appsink.TryPullSample(50000000);
            Console.WriteLine("New sample received.");
        }
        public void TakeSnapshot(string filePath)
        {


            // Ensure a sample is available
            if (_lastSample == null)
            {
                Console.WriteLine("No video frame available for snapshot.");
                return;
            }

            // Retrieve buffer from the sample
            var buffer = _lastSample.Buffer;
            var caps = _lastSample.Caps;

            // Map the buffer to access its data
            if (buffer.Map(out MapInfo test, Gst.MapFlags.Read))
            {
                if (test.Size == 0)
                {
                    Console.WriteLine("Failed to read video buffer.");
                    return;
                }

                // Retrieve width, height, and stride from caps
                Structure capsStruct = caps.GetStructure(0);
                capsStruct.GetInt("width",out int width);
                capsStruct.GetInt("height", out int height);
                int stride = width * 3; // 3 bytes per pixel for RGB

                //Convert raw data to an image and save it
                using (var bitmap = new System.Drawing.Bitmap(width, height, stride,
                              System.Drawing.Imaging.PixelFormat.Format24bppRgb, test.DataPtr))
                {
                    bitmap.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipY); // Flip vertically
                    bitmap.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
                    Console.WriteLine($"Snapshot saved to {filePath}");
                }
            }

            // Dispose of the sample
            _lastSample.Dispose();
            _lastSample = null;
        }

        public void SetPlaybackRate(double rate)
        {
            if (_pipeline == null) return;

            // Adjust playback rate (fast forward or reverse)
            var seekFlags = SeekFlags.Flush | SeekFlags.Accurate;
            // if (rate < 0) seekFlags |= SeekFlags.Reverse;

            var success = _pipeline.Seek(rate, Format.Time, seekFlags, Gst.SeekType.None, 0, Gst.SeekType.None, -1);
            if (success)
            {
                Console.WriteLine($"Playback rate set to {rate}x.");
            }
            else
            {
                Console.WriteLine("Failed to set playback rate.");
            }
        }

        void OnPadAdded(Element src, Pad newPad)
        {
            Caps caps = newPad.CurrentCaps;
            if (caps != null)
            {
                var structure = caps.GetStructure(0);
                if (structure.HasField("width") && structure.HasField("height"))
                {
                    width = (int)structure.GetValue("width");
                    height = (int)structure.GetValue("height");
                }
                if (structure.HasField("framerate"))
                {
                    var framerateStruct = structure.GetDouble("framerate");
                    framerate = $"{framerateStruct.Numerator}/{framerateStruct.Denominator}";
                }

                metadataRetrieved = true; // Metadata successfully retrieved
            }
        }

        /// <summary>
        /// Retrieves video information such as FPS, resolution, and file size.
        /// </summary>
        /// <returns>A string containing video details.</returns>
        public string GetVideoInfo()
        {
            if (_mediaUri.StartsWith("rtsp://"))
            {
                return "Video information is not available for live RTSP streams.";
            }

            if (!File.Exists(_mediaUri))
            {
                return "File does not exist.";
            }

            // Get file size
            var fileInfo = new System.IO.FileInfo(_mediaUri);
            long fileSize = fileInfo.Length; // File size in bytes

            string pipelineDescription = $"filesrc location={_mediaUri} ! decodebin name=decoder ! fakesink";
            using var pipeline = Parse.Launch(pipelineDescription) as Pipeline;

            if (pipeline == null)
            {
                return "Failed to initialize pipeline to retrieve metadata.";
            }

            Element decoder = pipeline.GetByName("decoder");
            if (decoder == null)
            {
                return "Failed to locate decoder element in the pipeline.";
            }

            // Metadata variables
            int width = 0, height = 0;
            double fps = 0;

            // Connect to the "pad-added" signal of the decodebin element
            decoder.Connect("pad-added", (Element src, Pad newPad) =>
            {
                Caps caps = newPad.CurrentCaps;
                if (caps != null)
                {
                    var structure = caps.GetStructure(0);
                    if (structure.HasField("width") && structure.HasField("height"))
                    {
                        width = (int)structure.GetValue("width");
                        height = (int)structure.GetValue("height");
                    }
                    if (structure.HasField("framerate"))
                    {
                        fps = (double)structure.GetValue("framerate");
                    }
                }
            });

            // Start the pipeline to trigger pad-added signals
            pipeline.SetState(State.Paused);

            // Wait for the pipeline to reach the paused state
            var stateChangeResult = pipeline.GetState(out State state, out _, Gst.Constants.CLOCK_TIME_NONE);
            if (stateChangeResult != StateChangeReturn.Success || state != State.Paused)
            {
                return "Failed to retrieve metadata from the file.";
            }

            pipeline.SetState(State.Null); // Cleanup pipeline

            if (width > 0 && height > 0)
            {
                return $"Resolution: {width}x{height}, FPS: {fps}, File Size: {fileSize / (1024 * 1024)} MB";
            }

            return "Failed to retrieve video information.";
        }
        public TimeSpan GetDuration()
        {
            if (_pipeline.QueryDuration(Format.Time, out long durationNanoseconds))
            {
                return TimeSpan.FromTicks(durationNanoseconds / 100); // Convert nanoseconds to TimeSpan
            }
            return TimeSpan.Zero;
        }

        public TimeSpan GetCurrentPosition()
        {
            if (_pipeline.QueryPosition(Format.Time, out long positionNanoseconds))
            {
                return TimeSpan.FromTicks(positionNanoseconds / 100); // Convert nanoseconds to TimeSpan
            }
            return TimeSpan.Zero;
        }


        public void Dispose()
        {
            if (_pipeline != null)
            {
                _pipeline.SetState(State.Null);
                _pipeline.Dispose();
                _pipeline = null;
            }
        }
    }
}
