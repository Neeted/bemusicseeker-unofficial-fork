using System;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// ディレクトリ検査失敗を shell 通知へ変換するタイミングを表します。
/// </summary>
internal enum LibraryDirectoryWarningPhase
{
    /// <summary>ライブラリ構築や設定修復より前の検査です。</summary>
    Early,

    /// <summary>ライブラリ構築後または更新操作中に行う再検査です。</summary>
    Late
}

/// <summary>
/// 型付きディレクトリ検査失敗をユーザー向けのローカライズ済み通知へ変換します。
/// </summary>
internal static class LibraryDirectoryWarningFormatter
{
    /// <summary>
    /// 検査失敗の用途、パス、原因、復旧案内を通知文へ変換します。
    /// </summary>
    /// <param name="failure">検査が失敗した事実。</param>
    /// <param name="phase">失敗した検査のタイミング。</param>
    /// <returns>現在の UI 言語に対応した通知本文。</returns>
    internal static string Format(
        LibraryDirectoryPreflightException failure,
        LibraryDirectoryWarningPhase phase)
    {
        if (failure == null)
        {
            throw new ArgumentNullException(nameof(failure));
        }

        string role = FormatRole(failure);
        string cause = FormatCause(failure);
        string guidance = failure.Use == LibraryDirectoryPreflightUse.BmsRoot
            ? Resources.LibraryDirectoryPreflightBmsGuidance
            : Resources.LibraryDirectoryPreflightLr2Guidance;
        string format = phase == LibraryDirectoryWarningPhase.Early
            ? Resources.LibraryDirectoryPreflightEarlyWarningFormat
            : Resources.LibraryDirectoryPreflightLateWarningFormat;
        return string.Format(format, role, failure.DirectoryPath, cause, guidance);
    }

    private static string FormatRole(LibraryDirectoryPreflightException failure)
    {
        if (failure.Use == LibraryDirectoryPreflightUse.BmsRoot)
        {
            return Resources.LibraryDirectoryPreflightBmsRootRole;
        }

        return failure.OutputBaseKind switch
        {
            LibraryDirectoryPreflightOutputBaseKind.Normal =>
                Resources.LibraryDirectoryPreflightLr2NormalOutputRole,
            LibraryDirectoryPreflightOutputBaseKind.Additional =>
                Resources.LibraryDirectoryPreflightLr2AdditionalOutputRole,
            LibraryDirectoryPreflightOutputBaseKind.RootType =>
                Resources.LibraryDirectoryPreflightLr2RootOutputRole,
            _ => Resources.LibraryDirectoryPreflightLr2OutputRole
        };
    }

    private static string FormatCause(LibraryDirectoryPreflightException failure)
    {
        string cause = failure.Cause switch
        {
            LibraryDirectoryPreflightFailureCause.NotFound =>
                Resources.LibraryDirectoryPreflightCauseNotFound,
            LibraryDirectoryPreflightFailureCause.AccessDenied =>
                Resources.LibraryDirectoryPreflightCauseAccessDenied,
            LibraryDirectoryPreflightFailureCause.NotDirectory =>
                Resources.LibraryDirectoryPreflightCauseNotDirectory,
            LibraryDirectoryPreflightFailureCause.InvalidPath =>
                Resources.LibraryDirectoryPreflightCauseInvalidPath,
            LibraryDirectoryPreflightFailureCause.InvalidConfiguration =>
                Resources.LibraryDirectoryPreflightCauseInvalidConfiguration,
            LibraryDirectoryPreflightFailureCause.Io =>
                Resources.LibraryDirectoryPreflightCauseIo,
            LibraryDirectoryPreflightFailureCause.Write =>
                Resources.LibraryDirectoryPreflightCauseWrite,
            LibraryDirectoryPreflightFailureCause.Cleanup =>
                Resources.LibraryDirectoryPreflightCauseCleanup,
            _ => Resources.LibraryDirectoryPreflightCauseIo
        };

        return failure.CleanupException == null
            ? cause
            : cause + Environment.NewLine + Resources.LibraryDirectoryPreflightCleanupFailure;
    }
}
