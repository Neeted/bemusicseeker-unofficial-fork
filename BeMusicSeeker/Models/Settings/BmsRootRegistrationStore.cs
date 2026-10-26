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

    /// <summary>構成が共有する確定設定と採用済みXML所有者を接続します。</summary>
    internal BmsRootRegistrationStore(Settings settings, Func<Lr2ConfigOwner?> ownerProvider)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.ownerProvider = ownerProvider ?? throw new ArgumentNullException(nameof(ownerProvider));
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
        settings.Persist(values.ToArray());
        settings.Publish(values.ToArray());
        owner?.PublishUserRoots(previousUsers, next);
    }

    private Lr2ConfigOwner GetOwner()
    {
        Lr2ConfigOwner owner = ownerProvider() ?? throw new InvalidOperationException("LR2 root registration owner is unavailable.");
        owner.SetPlacement(CustomFolderOutputSettingsSnapshot.CreateCurrent(settings));
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
