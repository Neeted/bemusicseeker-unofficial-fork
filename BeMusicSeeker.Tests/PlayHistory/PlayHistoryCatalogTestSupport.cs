using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Tests;

/// <summary>履歴の表示・検索fixtureへ、実カタログの候補と同時点の現在値を渡します。</summary>
internal static class PlayHistoryCatalogTestSupport
{
    /// <summary>MD5優先とSHA-onlyの両入力を実ownerで捕捉し、独立した履歴値を既存の投影へ渡します。</summary>
    internal static PlayHistoryProjectionIndex Create(IEnumerable<ChartFile> charts,
        IReadOnlyDictionary<string, string>? sha256ByMd5 = null,
        Func<string, string, PlaylistReferenceDisplay>? referenceResolver = null,
        Func<string, string, ChartDetails?>? chartInfoResolver = null)
    {
        ChartFile[] values = charts.ToArray();
        var catalog = new CatalogOwnedCollectionOwner();
        catalog.ReplaceCharts(values.Where(chart => chart.Kind == ChartFileKind.Bms), values.Where(chart => chart.Kind == ChartFileKind.Bmson));
        PlaylistLibraryResolveIndexSnapshot index = catalog.GetPlaylistLibraryResolveIndexSnapshot(CancellationToken.None, out _, out _);
        IEnumerable<PlaylistEntryLookupKey> keys = values.SelectMany(chart => new[] { new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Md5, chart.Md5),
            new PlaylistEntryLookupKey(PlaylistEntryLookupKeyKind.Sha256, chart.Sha256) }).Where(key => key.HasValue);
        return PlayHistoryProjectionIndex.Create(index, sha256ByMd5, referenceResolver, chartInfoResolver,
            catalog.CapturePlaylistReadSnapshot(keys));
    }
}
