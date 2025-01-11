using System;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Gst;
using Gst.Video;

namespace VideoPlayer
{
    public class MediaPlayer : IDisposable
    {
        private Pipeline _pipeline;
        private Element _playbin;
        private Element _videoSink;
        private VideoOverlayAdapter _videoOverlay;
        private IntPtr _windowHandle;
        private bool _initialized;
        private bool _isPlaying;
        private double _volume = 1.0;
        private double _rate = 1.0;
        private long _position;
        private long _duration;

        public event EventHandler<bool> PlayingChanged;
        public event EventHandler<double> VolumeChanged;
        public event EventHandler<double> RateChanged;
        public event EventHandler<TimeSpan> PositionChanged;
        public event EventHandler<TimeSpan> DurationChanged;
        public event EventHandler<string> ErrorOccurred;
        public event EventHandler EndReached;
        public event EventHandler BufferingStarted;
        public event EventHandler BufferingEnded;

        public MediaPlayer()
        {
            if (!Gst.Application.InitCheck())
            {
                Gst.Application.Init();
            }
            InitializeGstreamer();
        }

        private void InitializeGstreamer()
        {
            _pipeline = new Pipeline("pipeline");
            _playbin = ElementFactory.Make("playbin", "playbin");
            _videoSink = ElementFactory.Make("glimagesink", "videosink");

            if (_pipeline == null || _playbin == null || _videoSink == null)
            {
                throw new Exception("Failed to create GStreamer elements");
            }

            _playbin["video-sink"] = _videoSink;
            _pipeline.Add(_playbin);

            var bus = _pipeline.Bus;
            bus.AddSignalWatch();
            bus.Message += OnBusMessage;

            _initialized = true;
        }

        public void SetVideoOutput(IntPtr handle)
        {
            _windowHandle = handle;
            if (_videoSink != null)
            {
                _videoOverlay = new VideoOverlayAdapter(_videoSink.Handle);
                _videoOverlay.WindowHandle = _windowHandle;
                _videoOverlay.HandleEvents(true);
            }
        }

        public void SetSource(string uri)
        {
            if (!_initialized) return;

            Stop();
            _playbin["uri"] = uri;
            GetDuration();
        }

        public void Play()
        {
            if (!_initialized) return;

            var ret = _pipeline.SetState(State.Playing);
            if (ret == StateChangeReturn.Success)
            {
                _isPlaying = true;
                PlayingChanged?.Invoke(this, _isPlaying);
            }
        }

        public void Pause()
        {
            if (!_initialized) return;

            var ret = _pipeline.SetState(State.Paused);
            if (ret == StateChangeReturn.Success)
            {
                _isPlaying = false;
                PlayingChanged?.Invoke(this, _isPlaying);
            }
        }

        public void Stop()
        {
            if (!_initialized) return;

            _pipeline.SetState(State.Ready);
            _isPlaying = false;
            PlayingChanged?.Invoke(this, _isPlaying);
        }

        public void SetVolume(double value)
        {
            if (!_initialized) return;

            _volume = Math.Clamp(value, 0.0, 1.0);
            _playbin["volume"] = _volume;
            VolumeChanged?.Invoke(this, _volume);
        }

        public void SetRate(double value)
        {
            if (!_initialized) return;

            _rate = value;
            var query = Query.NewSegment(Format.Time);
            if (_pipeline.Query(query))
            {
                Format format;
                double rate;
                long start, stop;
                query.ParseSegment(out rate,out format, out start, out stop);
                var event_success = _pipeline.SendEvent(Event.NewSeek(_rate, Format.Time,
                    SeekFlags.Flush | SeekFlags.Accurate,
                    SeekType.Set, _position,
                    SeekType.None, -1));

                if (event_success)
                {
                    RateChanged?.Invoke(this, _rate);
                }
            }
        }

        public void Seek(TimeSpan position)
        {
            if (!_initialized) return;

            long pos = (long)(position.TotalSeconds * Gst.Constants.SECOND);
            var event_success = _pipeline.SendEvent(Event.NewSeek(_rate, Format.Time,
                SeekFlags.Flush | SeekFlags.Accurate,
                SeekType.Set, pos,
                SeekType.None, -1));

            if (event_success)
            {
                _position = pos;
                PositionChanged?.Invoke(this, position);
            }
        }

        private void GetDuration()
        {
            if (_pipeline.QueryDuration(Format.Time, out _duration))
            {
                DurationChanged?.Invoke(this, TimeSpan.FromSeconds(_duration / (double)Gst.Constants.SECOND));
            }
        }

        private void UpdatePosition()
        {
            if (_pipeline.QueryPosition(Format.Time, out _position))
            {
                PositionChanged?.Invoke(this, TimeSpan.FromSeconds(_position / (double)Gst.Constants.SECOND));
            }
        }

        private void OnBusMessage(object o, GLib.SignalArgs args)
        {
            var msg = args.Args[0] as Message;
            if (msg == null) return;

            switch (msg.Type)
            {
                case MessageType.Error:
                    string error;
                    string debug;
                    msg.ParseError(out var gerror, out debug);
                    error = gerror.Message;
                    Stop();
                    ErrorOccurred?.Invoke(this, error);
                    break;

                case MessageType.Eos:
                    Stop();
                    EndReached?.Invoke(this, EventArgs.Empty);
                    break;

                case MessageType.Buffering:
                    int percent = msg.ParseBuffering();
                    if (percent < 100)
                    {
                        _pipeline.SetState(State.Paused);
                        BufferingStarted?.Invoke(this, EventArgs.Empty);
                    }
                    else
                    {
                        _pipeline.SetState(State.Playing);
                        BufferingEnded?.Invoke(this, EventArgs.Empty);
                    }
                    break;

                case MessageType.StateChanged:
                    if (msg.Src.Equals(_pipeline))
                    {
                        State oldState, newState, pendingState;
                        msg.ParseStateChanged(out oldState, out newState, out pendingState);
                        if (newState == State.Playing)
                        {
                            StartPositionTimer();
                        }
                    }
                    break;
            }
        }

        private void StartPositionTimer()
        {
            DispatcherTimer.Run(() =>
            {
                if (_isPlaying)
                {
                    UpdatePosition();
                }
                return _isPlaying;
            }, TimeSpan.FromMilliseconds(500));
        }

        public void Dispose()
        {
            if (_initialized)
            {
                Stop();
                _pipeline.Dispose();
                _initialized = false;
            }
        }
    }
}