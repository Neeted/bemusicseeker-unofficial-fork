using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>一回のファイル確定で取得した走査面とDB確定済みBMSパスを後段へ直接渡します。共有公開や消費状態を持ちません。</summary>
internal sealed record LibraryFileInitializationResult
{
    /// <summary>今回の取得値を捕捉します。後段で走査をやり直して異なる入力を混ぜません。</summary>
    internal LibraryFileInitializationResult(Lr2SongDbSyncScanSurfaceSnapshot scanSurface = null, IEnumerable<string> committedBmsPaths = null, BmsLibraryOptionsSnapshot options = null)
    {
        ScanSurface = scanSurface;
        Options = options;
        CommittedBmsPaths = (committedBmsPaths ?? []).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>ファイル確定と同じ操作で捕捉した設定。後段の署名・探索へ再読なしで渡します。</summary>
    internal BmsLibraryOptionsSnapshot Options { get; }

    /// <summary>今回取得した走査面。走査しなかった操作ではnullです。</summary>
    internal Lr2SongDbSyncScanSurfaceSnapshot ScanSurface { get; }

    /// <summary>今回DB確定したためLR2でファイル読取りを省略できるパスです。</summary>
    internal IReadOnlySet<string> CommittedBmsPaths { get; }

    /// <summary>必須UIの成功公開後に登録する、この初期化で捕捉した後続です。日常差分ではnullです。</summary>
    internal LibraryInitializationFollowUp FollowUp { get; init; }
}

/// <summary>モデルの保存済み準備と後続登録を分離し、捕捉入力を共有保留状態なしで返します。</summary>
internal sealed record LibraryInitializationFollowUp(string Reason, bool IncludeMaintenance, long CriticalElapsedMs);

/// <summary>必須譜面情報読込みから補完へ渡す変更不能な解析要否です。候補要約のDB読取りは含みません。</summary>
internal sealed record RequiredChartInfoHydrationResult(bool Succeeded, int OwnerCount, int CandidateOwnerCount, OperationProgressRequest ProgressRequest);

/// <summary>走査中の既存警告条件を、親受付外の通知へ直接渡す変更不能な結果です。</summary>
internal sealed record LibraryScanWarning(LibraryScanWarningKind Kind, string Reason);

/// <summary>既存の走査警告の種類です。表示文言や通知の寿命は画面境界が所有します。</summary>
internal enum LibraryScanWarningKind
{
    EverythingFallback,
    Incomplete,
    EmptyWithExistingData
}
