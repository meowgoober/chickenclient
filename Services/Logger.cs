using System;

namespace ChickenClient.Services;

public static class Logger
{
    // Action router to intercept console logs safely
    public static Action<string>? LogHandler { get; set; }

    public static void Info(string message) => Write($"[INFO] {message}");
    public static void Warn(string message) => Write($"[WARN] {message}");
    public static void Error(string message) => Write($"[ERROR] {message}");

    private static void Write(string formattedMessage)
    {
        if (LogHandler != null)
        {
            LogHandler(formattedMessage);
        }
        else
        {
            Console.WriteLine(formattedMessage);
        }
    }
}