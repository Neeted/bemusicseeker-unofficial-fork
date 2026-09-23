using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.Update;

internal sealed class UpdateManifest
{
    public int SchemaVersion { get; set; }

    public string VersionText { get; set; }

    public Version Version { get; set; }

    public string ReleaseTag { get; set; }

    public string ReleasePageUrl { get; set; }

    public int PackageFormatVersion { get; set; }

    public string PublishedAt { get; set; }

    public string MinimumUpdaterVersion { get; set; }

    public IReadOnlyList<UpdateAssetInfo> Assets { get; set; } = [];
}
