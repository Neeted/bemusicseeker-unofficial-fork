using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.Update;

internal sealed class UpdateCheckResult
{
    private UpdateCheckResult(
        bool isUpdateAvailable,
        Version latestVersion,
        string currentVersionText,
        string latestVersionText,
        string source,
        IReadOnlyList<UpdateAssetInfo> assets,
        string releasePageUrl)
    {
        IsUpdateAvailable = isUpdateAvailable;
        LatestVersion = latestVersion;
        CurrentVersionText = currentVersionText;
        LatestVersionText = latestVersionText;
        Source = source;
        Assets = assets ?? [];
        ReleasePageUrl = releasePageUrl;
    }

    public bool IsUpdateAvailable { get; }

    public Version LatestVersion { get; }

    public string CurrentVersionText { get; }

    public string LatestVersionText { get; }

    public string Source { get; }

    public IReadOnlyList<UpdateAssetInfo> Assets { get; }

    public string ReleasePageUrl { get; }

    public static UpdateCheckResult NoUpdate(string currentVersionText, string source)
    {
        return new UpdateCheckResult(false, null, currentVersionText, null, source, [], null);
    }

    public static UpdateCheckResult Available(Version latestVersion, string currentVersionText, string latestVersionText, string source, IReadOnlyList<UpdateAssetInfo> assets, string releasePageUrl)
    {
        return new UpdateCheckResult(true, latestVersion, currentVersionText, latestVersionText, source, assets, releasePageUrl);
    }
}
