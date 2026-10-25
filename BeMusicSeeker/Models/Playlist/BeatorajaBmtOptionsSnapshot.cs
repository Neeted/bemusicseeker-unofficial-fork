using System;
using System.Collections.Immutable;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

/// <summary>
/// beatoraja BMT export workflow が参照する設定値を保持します。
/// </summary>
internal sealed class BeatorajaBmtOptionsSnapshot
{
    public bool EnableBeatorajaBmtOutput { get; init; }

    public bool KeepBeatorajaBmtFilesWhenOutputDisabled { get; init; }

    public string BeatorajaRootPath { get; init; }

    public string BeatorajaBmtTablePath { get; init; }

    public bool RegisterBeatorajaBmtUrls { get; init; }

    public string BeatorajaBmtHashOutputMode { get; init; }

    internal static BeatorajaBmtOptionsSnapshot CreateCurrent(Settings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        return new BeatorajaBmtOptionsSnapshot
        {
            EnableBeatorajaBmtOutput = settings.EnableBeatorajaBmtOutput,
            KeepBeatorajaBmtFilesWhenOutputDisabled = settings.KeepBeatorajaBmtFilesWhenOutputDisabled,
            BeatorajaRootPath = settings.BeatorajaRootPath,
            BeatorajaBmtTablePath = settings.BeatorajaBmtTablePath,
            RegisterBeatorajaBmtUrls = settings.RegisterBeatorajaBmtUrls,
            BeatorajaBmtHashOutputMode = settings.BeatorajaBmtHashOutputMode
        };
    }
}

/// <summary>今回の設定変更だけで使う準備済み出力と、URL切替に必要な直前の所有集合です。後続要求へ保存しません。</summary>
/// <param name="Previous">操作受付時の確定済み旧出力方針。</param>
/// <param name="Target">今回の候補から捕捉し、準備・採用後処理で共有する変更不能な方針。</param>
/// <param name="PreviousManagedUrls">準備前の旧先URL所有を変更不能な文字列だけで捕捉します。旧ファイル整理後の物理残存とは区別します。</param>
/// <param name="TargetPreviousManagedUrls">準備前から新先にあるURL所有の変更不能な集合。同rootでは旧先集合と一回の同期へまとめます。</param>
internal sealed record BeatorajaBmtPreparation(BeatorajaBmtOptionsSnapshot Previous, BeatorajaBmtOptionsSnapshot Target,
    ImmutableArray<string> PreviousManagedUrls,
    ImmutableArray<string> TargetPreviousManagedUrls);
