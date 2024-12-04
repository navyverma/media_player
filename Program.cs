﻿// See https://aka.ms/new-console-template for more information
using MediaPlayer;
// Set the path to your GStreamer installation
string gstreamerPath = @"D:\gstreamer\1.0\mingw_x86";
string gstreamerPluginPath = @"D:\gstreamer\1.0\mingw_x86_64\lib\gstreamer-1.0";

// Set environment variables
//Environment.SetEnvironmentVariable("PATH", gstreamerPath + ";" + Environment.GetEnvironmentVariable("PATH"));
Environment.SetEnvironmentVariable("GST_PLUGIN_PATH", gstreamerPluginPath);
Environment.SetEnvironmentVariable("GST_DEBUG", "2");
Environment.SetEnvironmentVariable("GST_DEBUG_DUMP_DOT_DIR", @"D:/Projects/MediaPlayer");
Console.WriteLine("Hello, World!");
MediaPlayer.MediaPlayer mediaPlayer = new MediaPlayer.MediaPlayer(@"D:/happytime-multi-onvif-server/happytime-multi-onvif-server/happytime-rtsp-server/atcc/SingleExportVideo.mp4");
mediaPlayer.Play();
bool playvideo = true;
while (playvideo)
{
    var keyInfo = Console.ReadKey();
    switch (keyInfo.Key)
    {
        case ConsoleKey.Spacebar:
            if (mediaPlayer.IsPlaying)
            {
                mediaPlayer.Pause();
            }
            else
            {
                mediaPlayer.Play();
            }

            break;
        case ConsoleKey.S:
            mediaPlayer.TakeSnapshot("test.jpg");
            break;
        case ConsoleKey.V:
            Console.WriteLine(mediaPlayer.GetVideoInfo().ToString());
            break;
        case ConsoleKey.D:
            Console.WriteLine(mediaPlayer.Duration);
            break;
        case ConsoleKey.P:
            Console.WriteLine(mediaPlayer.Position);
            break;
        case ConsoleKey.Q:
            playvideo = false;
            break;


    }
}
