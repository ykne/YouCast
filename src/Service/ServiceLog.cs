using System;

namespace Service
{
    /// <summary>
    /// Timestamped (UTC) lines on the standard error stream, in the same "level: category: message" style as the web server's
    /// log, for the parts of the service that run outside a request (video generation, cache). <see cref="System.Diagnostics.Trace"/>
    /// was used before, but nothing listens to it, so those messages never showed up anywhere.
    /// </summary>
    public static class ServiceLog
    {
        public static void Info(string category, string message) => Write("info", category, message);

        public static void Warn(string category, string message) => Write("warn", category, message);

        public static void Error(string category, string message) => Write("fail", category, message);

        private static void Write(string level, string category, string message) =>
            Console.Error.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z {level}: {category}: {message}");
    }
}
