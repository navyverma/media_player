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

namespace MediaPlayer
{
    public class MediaPlayer : IDisposable
    {
        private Pipeline _pipeline;
        private string _mediaUri;
        private Element _videoSink;
        private Bus _bus;
        private AppSink _appSink;
        public event Action<State, State> OnStateChanged;
        public State MediaState { get; private set; } = State.Null;

        private Sample _lastSample;
        private object _sampleLock = new object();

        // Metadata properties
        public int Width { get; private set; }
        public int Height { get; private set; }
        public double Framerate { get; private set; }
        public string Codec { get; private set; }
        private bool localFile = false;

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

            string pipelineDescription;
            if (_mediaUri.StartsWith("rtsp://"))
            {
                // For RTSP streams, use rtspsrc and include tee for snapshot capability
                pipelineDescription =
                    $"rtspsrc location={_mediaUri} ! decodebin ! tee name=t !" +
                    $" queue ! videoconvert !  appsink name=snapsink  t. ! " +
                    $"queue ! videoconvert ! autovideosink";
            }
            else
            {
                localFile = true;
                // For local files, use filesrc and include tee for snapshot capability
                pipelineDescription =
                    $"filesrc location={_mediaUri} ! decodebin ! tee name=t !" +
                    $" queue ! videoconvert !  appsink name=snapsink  t. ! " +
                    $"queue ! videoconvert ! autovideosink";
            }

            try
            {
                _pipeline = Parse.Launch(pipelineDescription) as Pipeline;
                if (_pipeline == null)
                    throw new InvalidOperationException("Failed to create pipeline");

                // List all elements in pipeline for debugging
                var iterator = _pipeline.IterateElements();
                GLib.Value element;
                while (iterator.Next(ref element))
                {
                    Console.WriteLine($"Found element: {element.Name} of type {element.GetType()}");
                }

                // Try getting the appsink directly after creation
                var element = _pipeline.GetByName("snapsink");
                Console.WriteLine($"Found element by name 'snapsink': {element != null}");

                if (element != null)
                {
                    Console.WriteLine($"Element type: {element.GetType()}");
                    _appSink = element as AppSink;
                    Console.WriteLine($"Cast to AppSink successful: {_appSink != null}");
                }


                // Set up bus monitoring
                _bus = _pipeline.Bus;
                _bus.AddSignalWatch();
                _bus.Message += OnBusMessage;

                // Configure AppSink for snapshots
                _appSink = _pipeline.GetByName("snapsink") as AppSink;
                if (_appSink != null)
                {
                    _appSink.EmitSignals = true;
                    _appSink.NewSample += OnNewSample;
                }

                // Connect to pad-added signal for metadata extraction
                var decode = _pipeline.GetByName("decodebin0");
                if (decode != null)
                {
                    decode.PadAdded += OnPadAdded;
                }
                _pipeline.
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to initialize pipeline: {ex.Message}");
            }
        }

        private void OnNewSample(object sender, NewSampleArgs args)
        {
            if (sender is AppSink appSink)
            {
                lock (_sampleLock)
                {
                    _lastSample?.Dispose();
                    _lastSample = appSink.PullSample();
                }
            }
        }

        public bool TakeSnapshot(string filePath)
        {
            var stateChangeReturn = _pipeline.GetState(out State state, out State pending, Gst.Constants.SECOND * 1);
            if (state != State.Playing)
                return false;

            Sample sample;
            lock (_sampleLock)
            {
                if (_lastSample == null)
                    return false;
                sample = _lastSample;
            }

            try
            {
                using var buffer = sample.Buffer;
                if (!buffer.Map(out MapInfo info, MapFlags.Read))
                    return false;

                try
                {
                    var caps = sample.Caps;
                    var structure = caps.GetStructure(0);

                    structure.GetInt("width", out int width);
                    structure.GetInt("height", out int height);

                    using var bitmap = new System.Drawing.Bitmap(
                        width,
                        height,
                        width * 3,
                        System.Drawing.Imaging.PixelFormat.Format24bppRgb,
                        info.DataPtr);

                    bitmap.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
                    return true;
                }
                finally
                {
                    buffer.Unmap(info);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to take snapshot: {ex.Message}");
                return false;
            }
        }

        public TimeSpan? GetDuration()
        {
            if (_pipeline == null)
                return null;

            // Only try to get duration for file-based media (not RTSP streams)
            if (_mediaUri.StartsWith("rtsp://"))
                return null;

            // Query the duration in nanoseconds
            if (_pipeline.QueryDuration(Format.Time, out long durationNanoseconds))
            {
                // Convert from nanoseconds to TimeSpan
                return TimeSpan.FromTicks(durationNanoseconds / 100);
            }

            return null;
        }

        public TimeSpan? GetCurrentPosition()
        {
            if (_pipeline == null)
                return null;

            // Query the position in nanoseconds
            if (_pipeline.QueryPosition(Format.Time, out long positionNanoseconds))
            {
                // Convert from nanoseconds to TimeSpan
                return TimeSpan.FromTicks(positionNanoseconds / 100);
            }

            return null;
        }

        // Helper method to get progress percentage
        public double? GetProgress()
        {
            var position = GetCurrentPosition();
            var duration = GetDuration();

            if (position.HasValue && duration.HasValue && duration.Value.Ticks > 0)
            {
                return (position.Value.Ticks * 100.0) / duration.Value.Ticks;
            }

            return null;
        }

        public bool SetPlaybackRate(double rate)
        {
            if (_pipeline == null || rate <= 0)
                return false;

            var seekFlags = SeekFlags.Flush | SeekFlags.Accurate;
            Format format = Format.Time;

            // Get current position
            if (!_pipeline.QueryPosition(format, out long position))
                position = 0;

            try
            {
                return _pipeline.Seek(rate, format, seekFlags, Gst.SeekType.Set, position, Gst.SeekType.None, -1);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to set playback rate: {ex.Message}");
                return false;
            }
        }

        private void OnPadAdded(object o, PadAddedArgs args)
        {
            var pad = args.NewPad;
            var caps = pad.CurrentCaps;
            if (caps != null)
            {
                var structure = caps.GetStructure(0);
                if (structure.Name.StartsWith("video/"))
                {
                    if (structure.GetInt("width", out int width))
                        Width = width;

                    if (structure.GetInt("height", out int height))
                        Height = height;

                    if (structure.GetFraction("framerate", out int num, out int denom))
                        Framerate = (double)num / denom;

                    Codec = structure.Name;
                }
            }
        }

        private void OnBusMessage(object o, MessageArgs args)
        {
            var msg = args.Message;
            switch (msg.Type)
            {
                case MessageType.StateChanged:
                    msg.ParseStateChanged(out State oldState, out State newState, out _);
                    MediaState = newState;
                    OnStateChanged?.Invoke(oldState, newState);
                    break;

                case MessageType.Error:
                    msg.ParseError(out GException err, out string debug);
                    Console.WriteLine($"Pipeline error: {err.Message} ({debug})");
                    Stop();
                    break;

                case MessageType.Eos:
                    Console.WriteLine("End of stream reached");
                    Stop();
                    break;
            }
        }

        public void Play()
        {
            InitializePipeline();
            _pipeline?.SetState(State.Playing);
        }

        public void Pause()
        {
            _pipeline?.SetState(State.Paused);
        }

        public void Stop()
        {
            _pipeline?.SetState(State.Ready);
        }

        public void Seek(TimeSpan position)
        {
            if (_pipeline == null)
                return;

            var time = position.Ticks * 100; // Convert to nanoseconds
            _pipeline.SeekSimple(
                Format.Time,
                SeekFlags.Flush | SeekFlags.KeyUnit,
                time);
        }

        public TimeSpan Duration
        {
            get
            {
                if (_pipeline?.QueryDuration(Format.Time, out long duration) == true)
                    return TimeSpan.FromTicks(duration / 100);
                return TimeSpan.Zero;
            }
        }

        public TimeSpan Position
        {
            get
            {
                if (_pipeline?.QueryPosition(Format.Time, out long position) == true)
                    return TimeSpan.FromTicks(position / 100);
                return TimeSpan.Zero;
            }
        }

        public VideoInfo GetVideoInfo()
        {
            return new VideoInfo
            {
                Width = this.Width,
                Height = this.Height,
                Framerate = this.Framerate,
                Codec = this.Codec,
                Duration = this.Duration,
                IsStreaming = _mediaUri.StartsWith("rtsp://"),
                FileSize = !_mediaUri.StartsWith("rtsp://") && File.Exists(_mediaUri)
                    ? new System.IO.FileInfo(_mediaUri).Length
                    : 0
            };
        }

        public void Dispose()
        {
            if (_pipeline != null)
            {
                _pipeline.SetState(State.Null);
                _pipeline.Dispose();
                _pipeline = null;
            }

            _lastSample?.Dispose();
            _lastSample = null;
        }
    }

    public class VideoInfo
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public double Framerate { get; set; }
        public string Codec { get; set; }
        public TimeSpan Duration { get; set; }
        public bool IsStreaming { get; set; }
        public long FileSize { get; set; }
        public override string ToString()
        {
            string info = "Width " + Width + " : Height " + Height + " : FrameRate " + Framerate;
            return info;

        }
    }

}