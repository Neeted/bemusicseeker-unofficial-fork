#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

/// <summary>確定利用者登録の読取りと関連項目の保存・限定公開を所有します。候補や物理削除の判断は保持しません。</summary>
internal sealed class BmsRootRegistrationStore
{
    private readonly Settings settings;
    private readonly Func<Lr2ConfigOwner?> ownerProvider;
    private readonly Func<CustomFolderOutputSettingsSnapshot>? placementProvider;
    private readonly Action<CustomFolderOutputSettingsSnapshot>? publishPlacement;

    /// <summary>構成が共有する確定設定と採用済みXML所有者を接続します。</summary>
    internal BmsRootRegistrationStore(Settings settings, Func<Lr2ConfigOwner?> ownerProvider,
        Func<CustomFolderOutputSettingsSnapshot>? placementProvider = null, Action<CustomFolderOutputSettingsSnapshot>? publishPlacement = null)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.ownerProvider = ownerProvider ?? throw new ArgumentNullException(nameof(ownerProvider));
        this.placementProvider = placementProvider;
        this.publishPlacement = publishPlacement;
    }

    /// <summary>今回必要な全媒体への保存と限定公開が成功した後だけ通知します。候補編集の採否は通知しません。</summary>
    internal event EventHandler? Published;

    /// <summary>走査が確認した同所prefixを現在の保存値と公開値へ別々に適用し、全必要媒体の成功後だけ登録と配置を公開します。</summary>
    internal void SaveRootChanges(BmsLibraryOptionsSnapshot input, BmsLibraryOptionsSnapshot confirmed)
    {
        IReadOnlyList<string> before = input.OperationModeLR2DB ? input.Lr2Input.RegisteredRoots : input.StandaloneBmsRootPaths;
        IReadOnlyList<string> after = confirmed.OperationModeLR2DB ? confirmed.Lr2Input.RegisteredRoots : confirmed.StandaloneBmsRootPaths;
        if (before.SequenceEqual(after, StringComparer.Ordinal)) { return; }
        string Replace(string path) => BmsLibraryOptionsSnapshot.ApplyConfirmedRootPrefixes(path, before, after);
        IReadOnlyList<string> userRoots = CaptureUserRoots(input);
        string ReplaceInstall(string path) => userRoots.Any(root =>
            CustomFolderOutputBaseSearchRootSyncService.IsSameDirectory(root, path)) ? Replace(path) : path;
        var values = new List<(string Name, object Value)>();
        void AddChanged(string name, string current, string next)
        { if (!string.Equals(current, next, StringComparison.Ordinal)) { values.Add((name, next)); } }
        Lr2ConfigOwner? owner = input.OperationModeLR2DB ? GetOwner() : null;
        IReadOnlyList<string> savedRoots = owner?.CaptureSaved().RegisteredRoots ?? CaptureStandaloneRoots(settings);
        IReadOnlyList<string> publishedRoots = owner?.Capture().RegisteredRoots ?? savedRoots;
        string[] nextSavedRoots = savedRoots.Select(Replace).ToArray();
        string[] nextPublishedRoots = publishedRoots.Select(Replace).ToArray();
        Lr2RootChange publicationChange = new(publishedRoots, nextPublishedRoots);
        bool rootsChanged = !savedRoots.SequenceEqual(nextSavedRoots, StringComparer.Ordinal)
            || !publishedRoots.SequenceEqual(nextPublishedRoots, StringComparer.Ordinal);
        if (owner == null && rootsChanged)
        {
            values.Add((nameof(Settings.StandaloneBmsRootPaths), StandaloneBmsRootPathSettings.Serialize(nextSavedRoots)));
            values.Add((nameof(Settings.BMSRootPath), nextSavedRoots.FirstOrDefault() ?? string.Empty));
        }
        AddChanged(nameof(Settings.BMSInstallDir), settings.BMSInstallDir,
            ReplaceInstall(settings.BMSInstallDir));
        var currentSettings = CustomFolderOutputSettingsSnapshot.CreateCurrent(settings);
        CustomFolderOutputSettingsSnapshot nextSettings = currentSettings.WithDirectoryPaths(Replace);
        AddChanged(nameof(Settings.LR2CustomFolderOutputBaseDir), currentSettings.LR2CustomFolderOutputBaseDir, nextSettings.LR2CustomFolderOutputBaseDir);
        AddChanged(nameof(Settings.LR2CustomFolderOutputBaseDirRootType), currentSettings.LR2CustomFolderOutputBaseDirRootType, nextSettings.LR2CustomFolderOutputBaseDirRootType);
        AddChanged(nameof(Settings.LR2CustomFolderAdditionalOutputBaseDirs), currentSettings.LR2CustomFolderAdditionalOutputBaseDirs, nextSettings.LR2CustomFolderAdditionalOutputBaseDirs);
        // 現在のapplied配置だけを変換します。別操作の保存済み未公開配置をSettingsから再捕捉しません。
        CustomFolderOutputSettingsSnapshot currentPlacement = placementProvider?.Invoke() ?? owner?.Capture().Placement ?? currentSettings;
        CustomFolderOutputSettingsSnapshot publishedPlacement = currentPlacement.WithDirectoryPaths(Replace);
        bool placementChanged = !string.Equals(currentPlacement.LR2CustomFolderOutputBaseDir, publishedPlacement.LR2CustomFolderOutputBaseDir, StringComparison.Ordinal)
            || !string.Equals(currentPlacement.LR2CustomFolderOutputBaseDirRootType, publishedPlacement.LR2CustomFolderOutputBaseDirRootType, StringComparison.Ordinal)
            || !string.Equals(currentPlacement.LR2CustomFolderAdditionalOutputBaseDirs, publishedPlacement.LR2CustomFolderAdditionalOutputBaseDirs, StringComparison.Ordinal);
        if (!rootsChanged && values.Count == 0 && !placementChanged) { return; }
        if (owner != null) { owner.SaveRoots(new(savedRoots, nextSavedRoots), publish: false); }
        string[] pathNames = [nameof(Settings.BMSInstallDir), nameof(Settings.LR2CustomFolderOutputBaseDir),
            nameof(Settings.LR2CustomFolderOutputBaseDirRootType), nameof(Settings.LR2CustomFolderAdditionalOutputBaseDirs)];
        if (owner == null) { pathNames = [.. pathNames, nameof(Settings.StandaloneBmsRootPaths), nameof(Settings.BMSRootPath)]; }
        settings.PersistTransformedPaths((name, path) => name switch
        {
            nameof(Settings.BMSInstallDir) => ReplaceInstall(path),
            nameof(Settings.StandaloneBmsRootPaths) => ReplaceList(path,
                StandaloneBmsRootPathSettings.Deserialize(path, string.Empty), StandaloneBmsRootPathSettings.Serialize),
            nameof(Settings.LR2CustomFolderAdditionalOutputBaseDirs) => ReplaceList(path,
                CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(path), CustomFolderOutputBaseRegistry.SerializeBaseDirectories),
            _ => Replace(path)
        }, pathNames);
        PublishSavedValues(values, () =>
        {
            owner?.Publish(publicationChange, publishedPlacement);
            publishPlacement?.Invoke(publishedPlacement);
        });

        string ReplaceList(string original, IReadOnlyList<string> paths, Func<IEnumerable<string>, string> serialize)
        {
            string[] next = paths.Select(Replace).ToArray();
            return paths.SequenceEqual(next, StringComparer.Ordinal) ? original : serialize(next);
        }
    }

    /// <summary>欠落・親子・保存順を保つstandaloneの確定登録を読み取ります。</summary>
    internal static IReadOnlyList<string> CaptureStandaloneRoots(Settings settings)
        => Array.AsReadOnly(StandaloneBmsRootPathSettings.Deserialize(settings.StandaloneBmsRootPaths, settings.BMSRootPath).ToArray());

    /// <summary>共有設定に公開済みのstandalone登録を捕捉します。</summary>
    internal IReadOnlyList<string> CaptureStandaloneRoots() => CaptureStandaloneRoots(settings);

    /// <summary>公開済み登録と同時公開された配置だけで管理領域を除外します。</summary>
    internal static IReadOnlyList<string> CaptureUserRoots(BmsLibraryOptionsSnapshot options)
    {
        if (!options.OperationModeLR2DB) { return options.StandaloneBmsRootPaths; }
        Lr2ConfigSnapshot input = options.Lr2Input ?? throw new InvalidOperationException("LR2 root registration owner is unavailable.");
        return CaptureLr2UserRoots(input, input.Placement ?? options.CaptureOutputPlacement());
    }

    /// <summary>利用者登録を存在確認なしで捕捉します。実ライブラリのLR2所有者不足は空一覧へ変換しません。</summary>
    internal IReadOnlyList<string> CaptureUserRoots()
    {
        if (!settings.OperationModeLR2DB) { return CaptureStandaloneRoots(settings); }
        Lr2ConfigSnapshot input = GetOwner().Capture();
        return CaptureLr2UserRoots(input, input.Placement);
    }

    /// <summary>今回の最終一覧と明示関連項目だけを保存し、必要保存の全成功後に公開します。登録解除では導入先を渡しません。</summary>
    /// <param name="roots">呼出元が決めた今回の最終利用者一覧。存在確認やscope判断は行いません。</param>
    /// <param name="explicitInstallDirectory">今回の関連保存に含める導入先。登録解除では省略し、現在値を再保存しません。</param>
    internal void SaveUserRoots(IReadOnlyList<string> roots, string? explicitInstallDirectory = null)
    {
        string[] next = roots.ToArray();
        foreach (string root in next)
        {
            CustomFolderOutputBaseSearchRootSyncService.ValidateOutsideBuiltinCustomFolder(root, settings.LR2RootPath,
                Resources.LibraryDirectoryPreflightBmsRootRole);
        }
        var values = new List<(string Name, object Value)>();
        Lr2ConfigOwner? owner = settings.OperationModeLR2DB ? GetOwner() : null;
        IReadOnlyList<string> previousUsers;
        if (owner != null)
        {
            Lr2ConfigSnapshot input = owner.Capture();
            previousUsers = CaptureLr2UserRoots(input, input.Placement);
            owner.SaveUserRoots(next, CaptureManagedRoots(input.Placement));
        }
        else
        {
            previousUsers = CaptureStandaloneRoots(settings);
            values.Add((nameof(Settings.StandaloneBmsRootPaths), StandaloneBmsRootPathSettings.Serialize(next)));
            values.Add((nameof(Settings.BMSRootPath), next.FirstOrDefault() ?? string.Empty));
        }
        if (explicitInstallDirectory != null) { values.Add((nameof(Settings.BMSInstallDir), explicitInstallDirectory)); }
        CompleteSave(values, () => owner?.PublishUserRoots(previousUsers, next));
    }

    private void CompleteSave(List<(string Name, object Value)> values, Action publishRoots)
    {
        settings.Persist(values.ToArray());
        PublishSavedValues(values, publishRoots);
    }

    private void PublishSavedValues(List<(string Name, object Value)> values, Action publishRoots)
    {
        settings.Publish(values.ToArray());
        publishRoots();
        foreach (EventHandler observer in Published?.GetInvocationList() ?? [])
        {
            try { observer(this, EventArgs.Empty); }
            catch (Exception failure)
            {
                try { Ribbit.Logging.NLogWrapper.FileLogger?.Warn(failure, "bms_root_publication_notification_failed"); }
                catch { /* 成立済み保存は診断先の失敗で取り消しません。 */ }
            }
        }
    }

    private Lr2ConfigOwner GetOwner()
    {
        Lr2ConfigOwner owner = ownerProvider() ?? throw new InvalidOperationException("LR2 root registration owner is unavailable.");
        owner.SetPlacement(placementProvider?.Invoke() ?? CustomFolderOutputSettingsSnapshot.CreateCurrent(settings));
        return owner;
    }

    private static IReadOnlyList<string> CaptureLr2UserRoots(Lr2ConfigSnapshot input, CustomFolderOutputSettingsSnapshot placement)
    {
        IReadOnlyList<string> managed = CaptureManagedRoots(placement);
        return Array.AsReadOnly(input.RegisteredRoots.Where(path => !managed.Any(boundary =>
            LongPathFileSystem.IsSameOrDescendantDirectoryPath(path, boundary))).ToArray());
    }

    private static IReadOnlyList<string> CaptureManagedRoots(CustomFolderOutputSettingsSnapshot placement)
        => CustomFolderOutputBaseRegistry.NormalizeBaseDirectories(new[] { placement.LR2CustomFolderOutputBaseDir,
            placement.LR2CustomFolderOutputBaseDirRootType }.Concat(CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(placement.LR2CustomFolderAdditionalOutputBaseDirs)));
}
