using Avalonia.Controls;
using Avalonia.Threading;
using Gst;
using Gst.Video;

namespace GstreamerPlayer
{
    public class GstVideoRenderer : Control, IDisposable
    {
        private Pipeline _pipeline;
        private Element _playbin;
        private Element _videoSink;
        private VideoOverlayAdapter _overlayAdapter;
        private bool _initialized;
        private bool _disposed;
        private IPlatformHandle _handle;
        private readonly bool _enableOverlay;

        public event EventHandler<string> Error;

        public GstVideoRenderer()
        {
            // Check if we can use overlay based on the platform
            _enableOverlay = GetPlatformSupportsOverlay();

            // Initialize when control is loaded
            this.AttachedToLogicalTree += OnAttachedToLogicalTree;
            this.DetachedFromLogicalTree += OnDetachedFromLogicalTree;
            this.GetObservable(BoundsProperty).Subscribe(OnBoundsChanged);
        }

        private bool GetPlatformSupportsOverlay()
        {
            if (OperatingSystem.IsWindows())
                return true;
            if (OperatingSystem.IsLinux())
                return true;
            if (OperatingSystem.IsMacOS())
                return false; // MacOS requires special handling
            return false;
        }

        private void OnAttachedToLogicalTree(object sender, Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
        {
            if (!_initialized)
            {
                InitializeGStreamer();
                _initialized = true;
            }
        }

        private void OnDetachedFromLogicalTree(object sender, Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
        {
            CleanupGStreamer();
        }

        private void InitializeGStreamer()
        {
            try
            {
                // Initialize GStreamer if not already done
                if (!Gst.Application.InitCheck())
                    Gst.Application.Init();

                _pipeline = new Pipeline("video-player");
                _playbin = ElementFactory.Make("playbin", "play");

                // Create appropriate video sink based on platform
                _videoSink = CreatePlatformSpecificVideoSink();

                if (_playbin == null || _videoSink == null)
                {
                    throw new Exception("Failed to create required GStreamer elements.");
                }

                // Set video sink property on playbin
                _playbin["video-sink"] = _videoSink;

                // Add playbin to pipeline
                _pipeline.Add(_playbin);

                // Setup message handling
                var bus = _pipeline.Bus;
                bus.EnableSyncMessageEmission();
                bus.AddSignalWatch();

                bus.Message += OnPipelineMessage;

                if (_enableOverlay)
                {
                    bus.SyncMessage += OnBusSyncMessage;
                }

                // Get platform-specific window handle after the control is loaded
                Dispatcher.UIThread.Post(() =>
                {
                    _handle = this.GetPlatformHandle();
                    if (_enableOverlay && _handle != null)
                    {
                        SetupVideoOverlay();
                    }
                }, DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, $"Failed to initialize GStreamer: {ex.Message}");
            }
        }

        private Element CreatePlatformSpecificVideoSink()
        {
            if (OperatingSystem.IsWindows())
            {
                // Try D3D11 first
                var d3d11Sink = ElementFactory.Make("d3d11videosink", "video-sink");
                if (d3d11Sink != null)
                    return d3d11Sink;

                // Fall back to DirectX
                return ElementFactory.Make("directdrawsink", "video-sink");
            }
            else if (OperatingSystem.IsLinux())
            {
                // Try OpenGL first
                var glSink = ElementFactory.Make("glimagesink", "video-sink");
                if (glSink != null)
                    return glSink;

                // Fall back to X11
                return ElementFactory.Make("xvimagesink", "video-sink");
            }
            else if (OperatingSystem.IsMacOS())
            {
                return ElementFactory.Make("glimagesink", "video-sink");
            }

            // Final fallback
            return ElementFactory.Make("autovideosink", "video-sink");
        }

        private void SetupVideoOverlay()
        {
            if (_videoSink == null || _handle == null) return;

            try
            {
                // Get the window handle
                var whandle = GetWindowHandle();
                if (whandle == IntPtr.Zero) return;

                // Setup video overlay
                var element = _videoSink;
                if (_videoSink is Gst.Bin)
                {
                    element = ((Gst.Bin)_videoSink).GetByInterface(VideoOverlayAdapter.GType);
                }

                if (element != null)
                {
                    _overlayAdapter = new VideoOverlayAdapter(element.Handle);
                    _overlayAdapter.WindowHandle = whandle;
                    _overlayAdapter.HandleEvents(true);
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, $"Failed to setup video overlay: {ex.Message}");
            }
        }

        private IntPtr GetWindowHandle()
        {
            if (_handle == null) return IntPtr.Zero;

            if (OperatingSystem.IsWindows())
            {
                return _handle.Handle;
            }
            else if (OperatingSystem.IsLinux())
            {
                // For X11
                return _handle.Handle;
            }
            else if (OperatingSystem.IsMacOS())
            {
                // MacOS requires special handling through CALayer
                return IntPtr.Zero;
            }

            return IntPtr.Zero;
        }

        private void OnBoundsChanged(Rect bounds)
        {
            if (_overlayAdapter != null)
            {
                // Update the video overlay size
                _overlayAdapter.SetRenderRectangle(
                    (int)bounds.X,
                    (int)bounds.Y,
                    (int)bounds.Width,
                    (int)bounds.Height
                );
            }
        }

        private void OnPipelineMessage(object o, GLib.SignalArgs args)
        {
            var msg = (Message)args.Args[0];
            switch (msg.Type)
            {
                case MessageType.Error:
                    GLib.GException err;
                    string debug;
                    msg.ParseError(out err, out debug);
                    Error?.Invoke(this, $"Pipeline error: {err.Message} ({debug})");
                    break;

                case MessageType.StateChanged:
                    State oldState, newState, pending;
                    msg.ParseStateChanged(out oldState, out newState, out pending);
                    // Handle state changes if needed
                    break;
            }
        }

        private void OnBusSyncMessage(object o, GLib.SignalArgs args)
        {
            var msg = (Message)args.Args[0];
            if (!Gst.Video.Global.IsVideoOverlayPrepareWindowHandleMessage(msg))
                return;

            Element src = msg.Src as Element;
            if (src == null) return;

            try
            {
                var handle = GetWindowHandle();
                if (handle != IntPtr.Zero)
                {
                    var overlay = new VideoOverlayAdapter(src.Handle);
                    overlay.WindowHandle = handle;
                    overlay.HandleEvents(true);
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, $"Failed to handle overlay message: {ex.Message}");
            }
        }

        public void Play(string uri)
        {
            try
            {
                if (_pipeline == null || _playbin == null)
                    throw new InvalidOperationException("GStreamer pipeline not initialized");

                // Set the URI
                _playbin["uri"] = uri;

                // Start playing
                var ret = _pipeline.SetState(State.Playing);
                if (ret == StateChangeReturn.Failure)
                {
                    throw new Exception("Failed to start playback");
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, $"Playback error: {ex.Message}");
            }
        }

        public void Stop()
        {
            _pipeline?.SetState(State.Null);
        }

        public void Pause()
        {
            _pipeline?.SetState(State.Paused);
        }

        public void Resume()
        {
            _pipeline?.SetState(State.Playing);
        }

        private void CleanupGStreamer()
        {
            if (_pipeline != null)
            {
                _pipeline.SetState(State.Null);
                _pipeline.Dispose();
                _pipeline = null;
            }

            _playbin?.Dispose();
            _playbin = null;

            _videoSink?.Dispose();
            _videoSink = null;

            _overlayAdapter = null;
        }

        public void Dispose()
        {
            if (_disposed) return;

            CleanupGStreamer();
            _disposed = true;
        }
    }
}