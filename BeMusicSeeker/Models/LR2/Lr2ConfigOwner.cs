using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.LR2;

/// <summary>構成の寿命でLR2設定全文を所有し、通常の保存値と必要媒体成立後の公開入力を分けます。</summary>
internal sealed class Lr2ConfigOwner
{
    private readonly object gate = new();
    private LR2Config document;
    private Lr2ConfigSnapshot published;
    private PreviewScope preview;

    /// <summary>初回検証済みXMLを採用します。以後の参照・保存ではディスクを読み直しません。</summary>
    internal Lr2ConfigOwner(LR2Config initial)
    {
        document = new LR2Config(initial ?? throw new ArgumentNullException(nameof(initial)));
        published = new Lr2ConfigSnapshot(document, null);
    }

    /// <summary>この所有者が採用したXMLの正規化済み保存先です。</summary>
    internal string ConfigFilePath => document.ConfigFilePath;

    /// <summary>実処理へ公開した登録と分類用配置を、一つの変更不能な入力として捕捉します。</summary>
    internal Lr2ConfigSnapshot Capture() { lock (gate) { return published; } }

    /// <summary>保存済み通常値の小さい入力を捕捉します。部分保存の未公開値も含むため保存計画だけに用います。</summary>
    internal Lr2ConfigSnapshot CaptureSaved() { lock (gate) { return new(document, published.Placement); } }

    /// <summary>初期配置を接続します。全文や公開済みの登録は差し替えません。</summary>
    internal void SetPlacement(CustomFolderOutputSettingsSnapshot placement)
    {
        lock (gate) { if (published.Placement == null) { published = published.With(published.RegisteredRoots, placement); } }
    }

    /// <summary>短い排他内で正本コピーだけを変更し、実保存成功後に通常文書を採用します。処理待機やファイル準備は呼出前に済ませます。</summary>
    private Lr2RootChange Save(Action<LR2Config> change, bool publish = false, Lr2RootChange publicationChange = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (gate)
        {
            LR2Config next = new(document);
            change(next);
            Lr2RootChange roots = new(document.GetBMSSearchDirectoriesForChangeTracking(), next.GetBMSSearchDirectoriesForChangeTracking());
            if (!XNode.DeepEquals(document, next))
            {
                Write(next);
                document = next;
            }
            if (publish) { PublishUnderGate(publicationChange ?? roots, published.Placement); }
            return publicationChange ?? roots;
        }
    }

    /// <summary>今回確定するユーザー一覧だけを変更し、捕捉済み管理領域の登録と通常値を保って保存します。公開は必要媒体の成立後に行います。</summary>
    internal void SaveUserRoots(IReadOnlyList<string> roots, IReadOnlyList<string> managedBoundaries)
        => Save(config =>
        {
            IEnumerable<string> managed = config.GetBMSSearchDirectoriesForChangeTracking()
                .Where(path => managedBoundaries.Any(boundary => Lr2FolderPath.IsSameOrDescendant(path, boundary)));
            config.SetBMSSearchDirectories(managed.Concat(roots));
            config.EnsureDatabaseAutoReloadManualOnly();
        });

    /// <summary>起動時の手動再読込み専用設定を、通常文書の保存成功後に採用・公開します。</summary>
    internal void EnsureDatabaseAutoReloadManualOnly() => Save(config => config.EnsureDatabaseAutoReloadManualOnly(), publish: true);

    /// <summary>準備済み登録差分だけを現在正本へ保存し、他操作の成立済み変更を保持します。XMLの保存差分がなくても、今回成立した要求だけを公開します。</summary>
    /// <returns>正規化した今回の要求差分です。別媒体成立後の限定公開へ渡せます。</returns>
    internal Lr2RootChange SaveRoots(Lr2RootChange change, bool publish = true)
    {
        Lr2RootChange normalized = change.Normalize(document.NormalizeRequestedSearchDirectory);
        return Save(config => config.SetBMSSearchDirectories(normalized.Apply(config.GetBMSSearchDirectoriesForChangeTracking())),
            publish, normalized);
    }

    /// <summary>必要媒体成立後に今回の登録差分と分類用配置だけを公開します。全文の未公開値を巻き込みません。</summary>
    internal void Publish(Lr2RootChange change, CustomFolderOutputSettingsSnapshot placement = null)
    {
        lock (gate) { PublishUnderGate(change, placement ?? published.Placement); }
    }

    /// <summary>今回成立したユーザー一覧の順序も公開し、現在の管理登録や別操作の削除を保持します。</summary>
    internal void PublishUserRoots(IReadOnlyList<string> previousUsers, IReadOnlyList<string> nextUsers)
    {
        lock (gate)
        {
            var previous = new HashSet<string>(previousUsers, StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<string> roots = published.RegisteredRoots.Where(path => !previous.Contains(path))
                .Concat(nextUsers).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            published = new Lr2ConfigSnapshot(document, published.Placement).With(roots, published.Placement);
        }
    }

    private void PublishUnderGate(Lr2RootChange change, CustomFolderOutputSettingsSnapshot placement)
    {
        published = new Lr2ConfigSnapshot(document, placement).With(change.Apply(published.RegisteredRoots), placement);
    }

    private void Write(LR2Config normal)
    {
        if (preview?.Overlay != true) { WriteDocument(normal); return; }
        XDocument output = new(normal);
        ApplyOverlay(output, preview);
        WriteDocument(output);
    }

    private void WriteDocument(XDocument output) => AtomicFileWriter.Write(ConfigFilePath,
        stream => output.Save(stream, SaveOptions.None));

    /// <summary>一時値を正本へ混ぜずに、同じ所有者の試聴スコープを開始します。</summary>
    internal PreviewScope BeginPreview()
    {
        lock (gate)
        {
            if (preview != null) { throw new InvalidOperationException("LR2 config is already used by an active preview."); }
            preview = new PreviewScope(this);
            return preview;
        }
    }

    /// <summary>正本コピーの既存5項目だけを一時値で書き出します。プロセス操作は呼出元が排他外で行います。</summary>
    internal void PublishPreview(PreviewScope scope, int windowSizeX, int windowSizeY, bool isWindowMode, int masterVolume, bool isVolumeEnabled)
    {
        lock (gate)
        {
            Validate(scope);
            scope.Values = [windowSizeX.ToString(CultureInfo.InvariantCulture), windowSizeY.ToString(CultureInfo.InvariantCulture),
                isWindowMode ? "1" : "0", masterVolume.ToString(CultureInfo.InvariantCulture), isVolumeEnabled ? "1" : "0"];
            XDocument output = new(document);
            ApplyOverlay(output, scope);
            WriteDocument(output);
            scope.Overlay = true;
        }
    }

    /// <summary>現在正本を実保存し、成功後だけoverlayを終了します。失敗時は同じスコープで明示再試行できます。</summary>
    internal void RestorePreview(PreviewScope scope)
    {
        lock (gate)
        {
            Validate(scope);
            WriteDocument(document);
            scope.Overlay = false;
        }
    }

    /// <summary>復元に成功した試聴のcleanupを終えます。未了の復元を破棄しません。</summary>
    internal void EndPreview(PreviewScope scope)
    {
        lock (gate)
        {
            Validate(scope);
            if (scope.Overlay) { throw new InvalidOperationException("LR2 preview restoration is incomplete."); }
            preview = null;
            scope.Ended = true;
        }
    }

    private void Validate(PreviewScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!ReferenceEquals(scope.Owner, this)) { throw new ArgumentException("試聴scopeの所有者が一致しません。", nameof(scope)); }
        if (!ReferenceEquals(preview, scope) || scope.Ended) { throw new InvalidOperationException("LR2 preview scope is no longer active."); }
    }

    private static void ApplyOverlay(XDocument output, PreviewScope scope)
    {
        string[] sections = ["system", "system", "system", "sound", "sound"];
        string[] fields = ["windowsize_x", "windowsize_y", "screenmode", "volumemaster", "volumeflag"];
        XElement config = output.Element("config") ?? throw new InvalidOperationException("LR2 config section is missing.");
        for (int index = 0; index < fields.Length; index++)
        {
            XElement section = config.Element(sections[index]);
            if (section == null) { section = new XElement(sections[index]); config.Add(section); }
            section.SetElementValue(fields[index], scope.Values[index]);
        }
    }

    /// <summary>一時出力期間とcleanupまでの寿命を区別する、単一所有者に属する試聴要求です。</summary>
    internal sealed class PreviewScope(Lr2ConfigOwner owner)
    {
        /// <summary>このscopeを作成した所有者です。別所有者への適用を拒否します。</summary>
        internal Lr2ConfigOwner Owner { get; } = owner;
        /// <summary>初回復元がまだ成功しておらず、通常保存にも一時値を重ねる期間です。</summary>
        internal bool Overlay { get; set; }
        /// <summary>cleanupまで終了したscopeを再利用しないための状態です。</summary>
        internal bool Ended { get; set; }
        /// <summary>一時出力だけに適用する五項目です。通常文書へは採用しません。</summary>
        internal string[] Values { get; set; } = [];
    }
}

/// <summary>今回の登録変更だけを表し、部分保存で先行した別操作の値を公開しません。</summary>
internal sealed class Lr2RootChange
{
    private readonly ReadOnlyCollection<string> removed;
    private readonly ReadOnlyCollection<string> added;

    /// <summary>操作内で捕捉した変更前後から差分を作ります。</summary>
    internal Lr2RootChange(IEnumerable<string> before, IEnumerable<string> after)
    {
        removed = Array.AsReadOnly(before.Except(after, StringComparer.OrdinalIgnoreCase).ToArray());
        added = Array.AsReadOnly(after.Except(before, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    /// <summary>所有者のLR2ルート基準へ要求を正規化し、文書に表せない空登録を除きます。</summary>
    internal Lr2RootChange Normalize(Func<string, string> normalize) => new(
        removed.Select(normalize).Where(path => !string.IsNullOrWhiteSpace(path)),
        added.Select(normalize).Where(path => !string.IsNullOrWhiteSpace(path)));

    /// <summary>同じ未了要求の候補修正でも、これまで保存に成功した変更を累積します。</summary>
    internal Lr2RootChange Then(Lr2RootChange next) => new(removed.Concat(next.removed),
        added.Except(next.removed, StringComparer.OrdinalIgnoreCase).Concat(next.added));

    /// <summary>指定した現在登録へ、今回の削除・追加だけを反映します。</summary>
    internal IReadOnlyList<string> Apply(IEnumerable<string> current) => Array.AsReadOnly(current
        .Except(removed, StringComparer.OrdinalIgnoreCase).Concat(added).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
}

/// <summary>XMLを持たず、公開済み登録・分類用配置・読取り値だけを保持する変更不能な入力です。</summary>
internal sealed class Lr2ConfigSnapshot
{
    /// <summary>通常文書の必要な値だけを捕捉します。</summary>
    internal Lr2ConfigSnapshot(LR2Config config, CustomFolderOutputSettingsSnapshot placement)
    {
        RegisteredRoots = Array.AsReadOnly(config.GetBMSSearchDirectoriesForChangeTracking().ToArray());
        Placement = placement;
        PlayerId = config.GetPlayerId();
        CustomFolderMask = config.GetCustomFolderMask();
        TitleFlashHours = config.GetTitleFlashHours();
        DatabaseAutoReloadMode = config.GetDatabaseAutoReloadMode();
        WindowSizeX = config.GetWindowSizeX(); WindowSizeY = config.GetWindowSizeY();
        ScreenModeWindow = config.IsScreenModeWindow(); MasterVolume = config.GetMasterVolume(); VolumeEnabled = config.IsVolumeEnabled();
    }

    private Lr2ConfigSnapshot(Lr2ConfigSnapshot source, IReadOnlyList<string> roots, CustomFolderOutputSettingsSnapshot placement)
    {
        RegisteredRoots = Array.AsReadOnly(roots.ToArray()); Placement = placement;
        PlayerId = source.PlayerId; CustomFolderMask = source.CustomFolderMask; TitleFlashHours = source.TitleFlashHours;
        DatabaseAutoReloadMode = source.DatabaseAutoReloadMode;
        WindowSizeX = source.WindowSizeX; WindowSizeY = source.WindowSizeY; ScreenModeWindow = source.ScreenModeWindow;
        MasterVolume = source.MasterVolume; VolumeEnabled = source.VolumeEnabled;
    }

    /// <summary>存在確認前の登録です。元の配列を外部へ公開しません。</summary>
    internal IReadOnlyList<string> RegisteredRoots { get; }
    /// <summary>登録と同時に公開した、管理領域の分類用配置です。</summary>
    internal CustomFolderOutputSettingsSnapshot Placement { get; }
    /// <summary>初回全文と通常保存に由来するプレイヤーIDです。</summary>
    internal string PlayerId { get; }
    /// <summary>組込みfolderの有効種別です。</summary>
    internal int CustomFolderMask { get; }
    /// <summary>新曲folderの判定に使う通常値です。</summary>
    internal int TitleFlashHours { get; }
    /// <summary>通常のDB自動再読込みモードです。</summary>
    internal int DatabaseAutoReloadMode { get; }
    /// <summary>試聴一時値を含まない通常の横幅です。</summary>
    internal int WindowSizeX { get; }
    /// <summary>試聴一時値を含まない通常の縦幅です。</summary>
    internal int WindowSizeY { get; }
    /// <summary>試聴一時値を含まない通常の画面モードです。</summary>
    internal bool ScreenModeWindow { get; }
    /// <summary>試聴一時値を含まない通常の音量です。</summary>
    internal int MasterVolume { get; }
    /// <summary>試聴一時値を含まない通常の音量有効状態です。</summary>
    internal bool VolumeEnabled { get; }
    /// <summary>操作時に存在・Shift_JIS互換と重複範囲を検査し、従来と同じ走査対象を返します。</summary>
    internal IReadOnlyList<string> CaptureExistingSearchRoots()
    {
        var roots = new List<string>();
        foreach (string path in RegisteredRoots.Where(LongPathFileSystem.DirectoryExists).Where(path => path.IsSjisSchemeString())
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path.Length))
        {
            if (!roots.Any(parent => path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) { roots.Add(path); }
        }
        return roots.AsReadOnly();
    }

    /// <summary>XMLを複製せず、今回の公開対象の登録と配置だけを差し替えます。</summary>
    internal Lr2ConfigSnapshot With(IReadOnlyList<string> roots, CustomFolderOutputSettingsSnapshot placement) => new(this, roots, placement);
}

#nullable enable

/// <summary>構成の寿命で採用先を保持し、別環境の候補検証から稼働中の所有者を保護します。</summary>
internal sealed class Lr2ConfigSession
{
    private Lr2ConfigOwner? current;

    /// <summary>同一採用済みパスは読み直さず、未採用入力は元の読込み失敗を保持して検証します。</summary>
    /// <param name="path">起動要求または選択候補のXMLパス。</param>
    /// <param name="adoptInitial">確定済み入力の初回読込みを採用する場合だけtrue。未確定環境の候補検証ではfalseです。</param>
    internal Lr2ConfigOwner Resolve(string path, bool adoptInitial = true)
    {
        string normalized = LongPathFileSystem.NormalizePathForStorage(path);
        if (current != null && string.Equals(current.ConfigFilePath, normalized, StringComparison.OrdinalIgnoreCase)) { return current; }
        Lr2ConfigOwner owner = new(new LR2Config(path));
        if (adoptInitial) { current ??= owner; }
        return owner;
    }

    /// <summary>初回入力の想定済み不備だけを検証失敗として返し、採用済みパスの外部内容は再検査しません。</summary>
    /// <param name="path">未指定・空白も入力不備として受け付ける設定XMLのパスです。</param>
    /// <param name="owner">成功時だけ検証済みの所有者を返します。失敗時はnullで、空の設定には置き換えません。</param>
    /// <param name="adoptInitial">初回の確定入力を採用するかどうかです。</param>
    internal bool TryResolve(string? path, [NotNullWhen(true)] out Lr2ConfigOwner? owner, bool adoptInitial = true)
    {
        owner = null;
        if (string.IsNullOrWhiteSpace(path)) { return false; }
        try { owner = Resolve(path, adoptInitial); return true; }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException
            or System.Security.SecurityException or NotSupportedException or System.Xml.XmlException)
        { return false; }
    }

    /// <summary>検証済み起動要求の所有者を採用します。同じ設定での再初期化は現在の正本を使います。</summary>
    internal void Adopt(Lr2ConfigOwner owner) => current = owner ?? throw new ArgumentNullException(nameof(owner));

}
