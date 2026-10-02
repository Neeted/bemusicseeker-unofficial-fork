#nullable enable
using System;
using System.Threading;
using Ribbit.Media.Audio;

namespace Ribbit.BMS;

/// <summary>一回の解析と復号を対にした、出力へ接続していない次曲の準備結果です。</summary>
internal sealed class PreparedBmsSong
{
    private BmsAudioResourceLoadResult? resources;

    private PreparedBmsSong(BMSFile chart, BmsAudioResourceLoadResult resources)
    {
        Chart = chart;
        this.resources = resources;
    }

    /// <summary>音源の選択に使用した解析済み譜面です。採用時に再解析しません。</summary>
    internal BMSFile Chart { get; }

    /// <summary>通常読込みと先読みで同じ音源探索・復号を行います。native出力を作成しません。</summary>
    internal static PreparedBmsSong Prepare(BMSFile chart, float gain, CancellationToken cancellationToken = default,
        BassAudioSession? expectedSession = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BmsAudioResourceLoadResult result = BmsAudioResourceLoader.Load(chart,
            System.IO.Path.GetDirectoryName(chart.Path) ?? string.Empty, gain, cancellationToken, expectedSession);
        cancellationToken.ThrowIfCancellationRequested();
        return new PreparedBmsSong(chart, result);
    }

    /// <summary>準備結果の所有を実再生へ一回だけ渡します。二回目の採用は拒否します。</summary>
    internal BmsAudioResourceLoadResult TakeResources() =>
        Interlocked.Exchange(ref resources, null)
        ?? throw new InvalidOperationException("The prepared song has already been consumed.");
}
