using System;
using System.IO;

namespace BeMusicSeeker.Models;

/// <summary>
/// Captures the application layout once at the composition boundary.
/// </summary>
internal sealed class ApplicationPathSnapshot
{
    private ApplicationPathSnapshot(string executablePath, string baseDirectory)
    {
        ExecutablePath = executablePath;
        BaseDirectory = baseDirectory;
        ConfigDirectoryPath = Path.Combine(baseDirectory, "config");
        UserConfigPath = Path.Combine(ConfigDirectoryPath, "user.config");
        DataDirectoryPath = Path.Combine(baseDirectory, "data");
        StandaloneSongDbPath = Path.Combine(DataDirectoryPath, "song.db");
        LanguageDirectory = Path.Combine(baseDirectory, "lang");
        TestSoundPath = Path.Combine(baseDirectory, "test.mp3");
    }

    internal string ExecutablePath { get; }

    internal string BaseDirectory { get; }

    internal string ConfigDirectoryPath { get; }

    internal string UserConfigPath { get; }

    internal string DataDirectoryPath { get; }

    internal string StandaloneSongDbPath { get; }

    internal string LanguageDirectory { get; }

    internal string TestSoundPath { get; }

    internal static ApplicationPathSnapshot Capture()
    {
        string executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Application process path is not available.");
        }
        return FromExecutablePath(executablePath);
    }

    internal static ApplicationPathSnapshot FromExecutablePath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("An executable path is required.", nameof(executablePath));
        }

        string normalizedExecutablePath = Path.GetFullPath(executablePath);
        string baseDirectory = Path.GetDirectoryName(normalizedExecutablePath);
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("The executable path must have a base directory.", nameof(executablePath));
        }

        return new ApplicationPathSnapshot(normalizedExecutablePath, Path.GetFullPath(baseDirectory));
    }
}

/// <summary>
/// Stores the one application path snapshot used by static framework adapters.
/// </summary>
internal static class ApplicationPathPolicy
{
    private static readonly object gate = new();

    private static ApplicationPathSnapshot current;

    internal static ApplicationPathSnapshot Current
    {
        get
        {
            lock (gate)
            {
                return current ??= ApplicationPathSnapshot.Capture();
            }
        }
    }

    internal static void Configure(ApplicationPathSnapshot snapshot)
    {
        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        lock (gate)
        {
            if (current != null && !ReferenceEquals(current, snapshot))
            {
                throw new InvalidOperationException("Application path policy is already configured.");
            }

            current = snapshot;
        }
    }
}
