using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.Update;

/// <summary>update.json のうち、更新判断と取得に使う値だけを保持します。</summary>
internal sealed class UpdateManifest
{
    public string VersionText { get; set; }

    public Version Version { get; set; }

    public string ReleasePageUrl { get; set; }

    public IReadOnlyList<UpdateAssetInfo> Assets { get; set; } = [];
}
