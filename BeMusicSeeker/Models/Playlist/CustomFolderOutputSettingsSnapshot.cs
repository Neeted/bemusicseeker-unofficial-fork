using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

/// <summary>
/// custom-folder output の一つの操作で共有する設定値を保持します。
/// </summary>
internal sealed class CustomFolderOutputSettingsSnapshot
{
    public bool OperationModeLR2DB { get; init; }

    public string LR2RootPath { get; init; }

    public string LR2CustomFolderOutputBaseDir { get; init; }

    public string LR2CustomFolderOutputBaseDirRootType { get; init; }

    public string LR2CustomFolderAdditionalOutputBaseDirs { get; init; }

    public bool EnableDownloadLr2IrScoreAndDetectUnsent { get; init; }

    public int PlaylistDefaultIgnoreFolderOutput { get; init; }

    public bool ShowRecommUpdatedMsg { get; init; }

    /// <summary>確認済みprefixの変換だけを配置三項目へ適用し、モード・LR2環境・生成flagは保持します。</summary>
    internal CustomFolderOutputSettingsSnapshot WithDirectoryPaths(Func<string, string> replace) => new()
    {
        OperationModeLR2DB = OperationModeLR2DB,
        LR2RootPath = LR2RootPath,
        LR2CustomFolderOutputBaseDir = replace(LR2CustomFolderOutputBaseDir),
        LR2CustomFolderOutputBaseDirRootType = replace(LR2CustomFolderOutputBaseDirRootType),
        LR2CustomFolderAdditionalOutputBaseDirs = ReplaceAdditionalDirectories(replace),
        EnableDownloadLr2IrScoreAndDetectUnsent = EnableDownloadLr2IrScoreAndDetectUnsent,
        PlaylistDefaultIgnoreFolderOutput = PlaylistDefaultIgnoreFolderOutput,
        ShowRecommUpdatedMsg = ShowRecommUpdatedMsg
    };

    private string ReplaceAdditionalDirectories(Func<string, string> replace)
    {
        IReadOnlyList<string> before = CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(LR2CustomFolderAdditionalOutputBaseDirs);
        string[] after = before.Select(replace).ToArray();
        return before.SequenceEqual(after, StringComparer.Ordinal) ? LR2CustomFolderAdditionalOutputBaseDirs
            : CustomFolderOutputBaseRegistry.SerializeBaseDirectories(after);
    }

    /// <param name="placement">標準構成が保存段階完了時に公開した配置。指定時は共有draftの配置を読み替えず、他の生成flagだけを現在値から捕捉します。</param>
    internal static CustomFolderOutputSettingsSnapshot CreateCurrent(Settings settings, CustomFolderOutputSettingsSnapshot placement = null)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        return new CustomFolderOutputSettingsSnapshot
        {
            OperationModeLR2DB = placement != null ? placement.OperationModeLR2DB : settings.OperationModeLR2DB,
            LR2RootPath = placement != null ? placement.LR2RootPath : settings.LR2RootPath,
            LR2CustomFolderOutputBaseDir = placement != null ? placement.LR2CustomFolderOutputBaseDir : settings.LR2CustomFolderOutputBaseDir,
            LR2CustomFolderOutputBaseDirRootType = placement != null ? placement.LR2CustomFolderOutputBaseDirRootType : settings.LR2CustomFolderOutputBaseDirRootType,
            LR2CustomFolderAdditionalOutputBaseDirs = placement != null ? placement.LR2CustomFolderAdditionalOutputBaseDirs : settings.LR2CustomFolderAdditionalOutputBaseDirs,
            EnableDownloadLr2IrScoreAndDetectUnsent = settings.EnableDownloadLr2IrScoreAndDetectUnsent,
            PlaylistDefaultIgnoreFolderOutput = settings.PlaylistDefaultIgnoreFolderOutput,
            ShowRecommUpdatedMsg = settings.ShowRecommUpdatedMsg
        };
    }

}
