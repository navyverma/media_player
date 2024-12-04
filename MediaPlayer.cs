using Gst;
using System;
using System.Collections.Generic;
using System.IO;
using Gst.Video;
using GLib;
using Gst.App;

namespace MediaPlayer
{
    public class MediaPlayerException : Exception
    {
        public MediaPlayerException(string message) : base(message) { }
        public MediaPlayerException(string message, Exception innerException) : base(message, innerException) { }
    }

    public enum MediaType
    {
        LocalFile,
        RtspStream,
        Unknown
    }

    public class MediaPlayer : IDisposable
    {
        private Pipeline _pipeline;
        private readonly string _mediaUri;
        private Element _videoSink;
        private Element _audioVolume;
        private Bus _bus;
        private AppSink _appSink;
        private bool _disposed;
        private readonly MediaType _mediaType;
        private readonly object _stateLock = new object();
        private readonly object _sampleLock = new object();
        private Sample _lastSample;
        private bool _isInitialized;
        private double _volume = 1.0;
        private double _playbackRate = 1.0;
        private State MediaState = State.Null;


        // Events
        public event Action<State, State> OnStateChanged;
        public event Action<TimeSpan> OnPositionChanged;
        public event Action<Exception> OnError;
        public event Action OnEndOfStream;
        public event Action<VideoInfo> OnMetadataChanged;

        // Properties
        public int Width { get; private set; }
        public int Height { get; private set; }
        public double Framerate { get; private set; }
        public string Codec { get; private set; }
        public bool IsPlaying => MediaState == State.Playing;
        public bool IsPaused => MediaState == State.Paused;

        public double Volume
        {
            get => _volume;
            set
            {
                if (value is < 0.0 or > 1.0)
                    throw new ArgumentOutOfRangeException(nameof(value), "Volume must be between 0.0 and 1.0");

                _volume = value;
                if (_audioVolume != null)
                    _audioVolume.SetProperty("volume", new GLib.Value(_volume));
            }
        }

        public double PlaybackRate
        {
            get => _playbackRate;
            set
            {
                if (value <= 0.0)
                    throw new ArgumentOutOfRangeException(nameof(value), "Playback rate must be greater than 0");

                if (SetPlaybackRate(value))
                    _playbackRate = value;
            }
        }

        static MediaPlayer()
        {
            Gst.Application.Init();
        }

        public MediaPlayer(string mediaUri)
        {
            if (string.IsNullOrWhiteSpace(mediaUri))
                throw new ArgumentNullException(nameof(mediaUri));

            _mediaUri = mediaUri;
            _mediaType = DetermineMediaType(mediaUri);

            if (_mediaType == MediaType.LocalFile && !File.Exists(mediaUri))
                throw new FileNotFoundException("Media file not found", mediaUri);
        }

        private MediaType DetermineMediaType(string uri)
        {
            if (uri.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
                return MediaType.RtspStream;
            else if (File.Exists(uri))
                return MediaType.LocalFile;
            return MediaType.Unknown;
        }

        private void InitializePipeline()
        {
            if (_isInitialized) return;

            lock (_stateLock)
            {
                if (_isInitialized) return;
                try
                {
                    BuildPipeline();
                    ConfigurePipeline();
                    SetupEventHandling();
                    _isInitialized = true;
                }
                catch (Exception ex)
                {
                    throw new MediaPlayerException("Failed to initialize pipeline", ex);
                }
            }
        }

        private void BuildPipeline()
        {
            string pipelineDescription = _mediaType switch
            {
                MediaType.RtspStream => BuildRtspPipeline(),
                MediaType.LocalFile => BuildLocalFilePipeline(),
                _ => throw new MediaPlayerException("Unsupported media type")
            };

            _pipeline = Parse.Launch(pipelineDescription) as Pipeline
                ?? throw new MediaPlayerException("Failed to create pipeline");
        }

        private string BuildRtspPipeline() =>
                    $"rtspsrc location={_mediaUri} ! " +
                    "decodebin name=decoder ! " +
                    "tee name=t ! " +
                    "queue ! videoconvert ! " +
                    "appsink name=snapsink sync=false max-buffers=1 drop=true t. ! " +  // Properly configured appsink
                    "queue ! videoconvert ! autovideosink " +
                    "decoder. ! audioconvert ! volume name=volume ! autoaudiosink";

        private string BuildLocalFilePipeline() =>
                    $"filesrc location={_mediaUri} ! " +
            "decodebin name=decoder ! " +
            "tee name=t ! " +
            "queue ! videoconvert ! " +
            "appsink name=snapsink sync=false max-buffers=1 drop=true t. ! " +  // Properly configured appsink
            "queue ! videoconvert ! autovideosink ";// +
            //"decoder. ! audioconvert ! volume name=volume ! autoaudiosink";

        private void ConfigurePipeline()
        {

            // Configure AppSink for snapshots
            _appSink = _pipeline.GetByName("snapsink") as AppSink;
            if (_appSink != null)
            {
                // Set specific caps for AppSink
                var caps = new Caps("video/x-raw,format=RGB");
               // _appSink.Caps = caps;
                //_appSink.MaxBuffers = 1;
                _appSink.EmitSignals = true;
                //_appSink.Drop = true;
                //_appSink.Sync = false;
                //_appSink.NewSample += OnNewSample;
                _appSink.NewSample += (sender, args) =>
                {
                    Console.WriteLine("New sample received");
                };
            }

            // Configure volume
            _audioVolume = _pipeline.GetByName("volume");
            if (_audioVolume != null)
            {
                _audioVolume.SetProperty("volume", new GLib.Value(_volume));
            }

            // Get video sink and configure it
            //var videoSink = _pipeline.GetByName("videosink");
            //if (videoSink != null)
            //{
            //    videoSink.SetProperty("sync", new GLib.Value(true));
            //    videoSink.SetProperty("async", new GLib.Value(false));
            //}

            // Configure for low latency
            if (_mediaType == MediaType.RtspStream)
            {
                var rtspsrc = _pipeline.GetByName("rtspsrc");
                if (rtspsrc != null)
                {
                    rtspsrc.SetProperty("latency", new GLib.Value(0U));
                    rtspsrc.SetProperty("drop-on-latency", new GLib.Value(true));
                }
            }
        }

        private void SetupEventHandling()
        {
            _bus = _pipeline.Bus;
            //_bus.EnableSyncMessageEmission();
            _bus.AddSignalWatch();
            //_bus.Message += OnBusMessage;
            _bus.Message += (o, args) =>
            {
                Console.WriteLine($"Bus message received: {args.Message.Type}");
            };

            // Set pipeline to realtime
            //_pipeline.SetProperty("async-handling", new GLib.Value(true));


            var decoder = _pipeline.GetByName("decoder");
            if (decoder != null)
                decoder.PadAdded += OnPadAdded;

            // Start position monitoring
            GLib.Timeout.Add(100, () =>
            {
                if (IsPlaying)
                    OnPositionChanged?.Invoke(Position);
                return IsPlaying;
            });
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
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentNullException(nameof(filePath));

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var state = GetCurrentState();
            if (state != State.Playing && state != State.Paused)
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
                throw new MediaPlayerException("Failed to take snapshot", ex);
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

        public TimeSpan Duration
        {
            get
            {
                if (_mediaType == MediaType.RtspStream)
                    return TimeSpan.Zero;

                if (_pipeline?.QueryDuration(Format.Time, out long duration) == true)
                    return TimeSpan.FromTicks(duration / 100);
                return TimeSpan.Zero;
            }
        }

        public double Progress
        {
            get
            {
                if (_mediaType == MediaType.RtspStream || Duration == TimeSpan.Zero)
                    return 0;
                return (Position.TotalMilliseconds / Duration.TotalMilliseconds) * 100;
            }
        }

        private bool SetPlaybackRate(double rate)
        {
            if (_pipeline == null || rate <= 0)
                return false;

            var seekFlags = SeekFlags.Flush | SeekFlags.Accurate;
            if (!_pipeline.QueryPosition(Format.Time, out long position))
                position = 0;

            try
            {
                return _pipeline.Seek(rate, Format.Time, seekFlags,
                    Gst.SeekType.Set, position, Gst.SeekType.None, -1);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(new MediaPlayerException("Failed to set playback rate", ex));
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
                    UpdateVideoMetadata(structure);
                }
            }
        }

        private void UpdateVideoMetadata(Structure structure)
        {
            if (structure.GetInt("width", out int width))
                Width = width;

            if (structure.GetInt("height", out int height))
                Height = height;

            if (structure.GetFraction("framerate", out int num, out int denom))
                Framerate = (double)num / denom;

            Codec = structure.Name;

            var videoInfo = GetVideoInfo();
            OnMetadataChanged?.Invoke(videoInfo);
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
                    OnError?.Invoke(new MediaPlayerException($"{err.Message} ({debug})"));
                    Stop();
                    break;

                case MessageType.Eos:
                    OnEndOfStream?.Invoke();
                    Stop();
                    break;

                case MessageType.Buffering:
                    int percent = msg.ParseBuffering();
                    if (percent < 100)
                        _pipeline?.SetState(State.Paused);
                    else if (MediaState == State.Paused)
                        _pipeline?.SetState(State.Playing);
                    break;
            }
        }

        public void Play()
        {
            InitializePipeline();
            SetState(State.Playing);
            Gst.Debug.BinToDotFile(_pipeline, Gst.DebugGraphDetails.All, "pipeline");
        }

        public void Pause()
        {
            SetState(State.Paused);
        }

        public void Stop()
        {
            SetState(State.Ready);
        }

        public void Seek(TimeSpan position)
        {
            if (_mediaType == MediaType.RtspStream)
                throw new InvalidOperationException("Cannot seek in RTSP streams");

            if (_pipeline == null)
                return;

            var time = position.Ticks * 100; // Convert to nanoseconds
            _pipeline.SeekSimple(
                Format.Time,
                SeekFlags.Flush | SeekFlags.KeyUnit,
                time);
        }

        private void SetState(State state)
        {
            if (_pipeline == null)
                return;

            var stateChangeResult = _pipeline.SetState(state);
            if (stateChangeResult == StateChangeReturn.Failure)
                throw new MediaPlayerException($"Failed to change state to {state}");
        }

        private State GetCurrentState()
        {
            State state = State.Null;
            _pipeline?.GetState(out  state, out _, Gst.Constants.SECOND);
            return state;
        }

        public VideoInfo GetVideoInfo()
        {
            return new VideoInfo
            {
                Width = Width,
                Height = Height,
                Framerate = Framerate,
                Codec = Codec,
                Duration = Duration,
                IsStreaming = _mediaType == MediaType.RtspStream,
                FileSize = _mediaType == MediaType.LocalFile ? new System.IO.FileInfo(_mediaUri).Length : 0
            };
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    Stop();
                    _pipeline?.SetState(State.Null);
                    _pipeline?.Dispose();
                    _lastSample?.Dispose();
                    _bus?.Dispose();
                    _appSink?.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
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

        public override string ToString() =>
            $"Resolution: {Width}x{Height}, Framerate: {Framerate:F2}, " +
            $"Codec: {Codec}, Duration: {Duration}, Streaming: {IsStreaming}";
    }
}