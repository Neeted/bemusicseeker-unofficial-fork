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
        IReadOnlyList<UpdateAssetInfo> assets,
        string releasePageUrl)
    {
        IsUpdateAvailable = isUpdateAvailable;
        LatestVersion = latestVersion;
        CurrentVersionText = currentVersionText;
        LatestVersionText = latestVersionText;
        Assets = assets ?? [];
        ReleasePageUrl = releasePageUrl;
    }

    public bool IsUpdateAvailable { get; }

    public Version LatestVersion { get; }

    public string CurrentVersionText { get; }

    public string LatestVersionText { get; }

    public IReadOnlyList<UpdateAssetInfo> Assets { get; }

    public string ReleasePageUrl { get; }

    public static UpdateCheckResult NoUpdate(string currentVersionText)
    {
        return new UpdateCheckResult(false, null, currentVersionText, null, [], null);
    }

    public static UpdateCheckResult Available(Version latestVersion, string currentVersionText, string latestVersionText, IReadOnlyList<UpdateAssetInfo> assets, string releasePageUrl)
    {
        return new UpdateCheckResult(true, latestVersion, currentVersionText, latestVersionText, assets, releasePageUrl);
    }
}
