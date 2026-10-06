using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Tests;

/// <summary>リソース消費先のテストに取得済みの不変入力を渡します。</summary>
internal static class TestChartResources
{
    /// <summary>用途別のfixture入力を取得済みの不変結果にします。省略された任意画像は定義しません。</summary>
    internal static ImmutableList<ChartResourceReference> Create(IEnumerable<string>? audio = null,
        IEnumerable<string>? visual = null, string? stagefile = null, string? backbmp = null, string? banner = null)
    {
        var references = new List<ChartResourceReference>();
        foreach (string path in audio ?? [])
        {
            references.Add(ChartResourceReference.Parse(path, ChartResourceKind.Audio));
        }

        foreach (string path in visual ?? [])
        {
            references.Add(ChartResourceReference.Parse(path, ChartResourceKind.Unknown));
        }

        if (stagefile != null)
        {
            references.Add(ChartResourceReference.Parse(stagefile, ChartResourceKind.Image, ChartResourceUsage.Stagefile));
        }

        if (backbmp != null)
        {
            references.Add(ChartResourceReference.Parse(backbmp, ChartResourceKind.Image, ChartResourceUsage.Backbmp));
        }

        if (banner != null)
        {
            references.Add(ChartResourceReference.Parse(banner, ChartResourceKind.Image, ChartResourceUsage.Banner));
        }

        return references.ToImmutableList();
    }

    /// <summary>既存fixtureの通常音声を置換し、他用途の確定結果を維持します。</summary>
    internal static ImmutableList<ChartResourceReference> ReplaceAudio(ImmutableList<ChartResourceReference>? resources, IEnumerable<string> paths)
        => (resources ?? []).Where(reference => reference.Usage != ChartResourceUsage.Normal || reference.Kind != ChartResourceKind.Audio)
            .Concat(Create(audio: paths)).ToImmutableList();

    /// <summary>既存fixtureの通常視覚リソースを置換し、他用途の確定結果を維持します。</summary>
    internal static ImmutableList<ChartResourceReference> ReplaceVisual(ImmutableList<ChartResourceReference>? resources, IEnumerable<string> paths)
        => (resources ?? []).Where(reference => reference.Usage != ChartResourceUsage.Normal || reference.Kind == ChartResourceKind.Audio)
            .Concat(Create(visual: paths)).ToImmutableList();
}
