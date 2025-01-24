using Avalonia;
using Avalonia.Controls;
using LibVLCSharp.Shared;

namespace LibVLCSharp.Avalonia
{
    public static class AppBuilderExtensions
    {
        public static AppBuilder UseVLCSharp(this AppBuilder b, LibVLCAvaloniaRenderingOptions? renderingOptions = null, string libvlcDirectoryPath = null)
        {
            if (renderingOptions != null)
            {
                LibVLCAvaloniaOptions.RenderingOptions = renderingOptions.Value;
            }

            return b.AfterSetup(_ => Core.Initialize(libvlcDirectoryPath));
        }
    }
}