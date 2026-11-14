using System;
using System.Diagnostics;
using System.IO;

namespace Service
{
    public sealed class VideoOptions
    {
        public string FFmpegPath { get; set; } = "ffmpeg";
        public string CacheDir { get; set; }
        public long CacheMaxBytes { get; set; } = 10L * 1024 * 1024 * 1024;
        public int MaxConcurrentMuxes { get; set; } = 2;
        public int PrewarmCount { get; set; } = 2;

        public static string DefaultCacheDir =>
            OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "YouCast", "cache")
                : "/var/cache/youcast";

        public static bool IsFFmpegAvailable(string ffmpegPath)
        {
            try
            {
                using (var process = Process.Start(
                    new ProcessStartInfo(ffmpegPath, "-version")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }))
                {
                    process.StandardOutput.ReadToEnd();
                    return process.WaitForExit(10000) && process.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
