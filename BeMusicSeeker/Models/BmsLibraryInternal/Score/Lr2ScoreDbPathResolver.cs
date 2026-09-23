using System;
using System.IO;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class Lr2ScoreDbPathResolver
{
    internal static string BuildPlayerScoreDbPath(string lr2RootPath, string playerId)
    {
        if (string.IsNullOrWhiteSpace(lr2RootPath) || string.IsNullOrWhiteSpace(playerId))
        {
            return null;
        }

        return Path.Combine(lr2RootPath, "LR2files", "Database", "Score", playerId + ".db");
    }

    internal static string BuildPlayerScoreDbPath(string lr2RootPath, Func<string> playerIdProvider)
    {
        if (playerIdProvider == null)
        {
            return null;
        }

        string playerId;
        try
        {
            playerId = playerIdProvider();
        }
        catch
        {
            playerId = null;
        }
        return BuildPlayerScoreDbPath(lr2RootPath, playerId);
    }

    internal static string ResolvePlayerScoreDbPath(string lr2RootPath, string playerId)
    {
        string scoreDbPath = BuildPlayerScoreDbPath(lr2RootPath, playerId);
        return File.Exists(scoreDbPath)
            ? scoreDbPath
            : null;
    }

    internal static string ResolvePlayerScoreDbPath(string lr2RootPath, Func<string> playerIdProvider)
    {
        if (playerIdProvider == null)
        {
            return null;
        }

        string playerId;
        try
        {
            playerId = playerIdProvider();
        }
        catch
        {
            playerId = null;
        }
        return ResolvePlayerScoreDbPath(lr2RootPath, playerId);
    }
}
