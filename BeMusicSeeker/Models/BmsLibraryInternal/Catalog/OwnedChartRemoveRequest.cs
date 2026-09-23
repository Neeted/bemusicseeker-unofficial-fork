using System;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal enum OwnedChartRemoveMode
{
    OwnerReference,
    PathCleanup
}

internal sealed class OwnedChartRemoveRequest
{
    private OwnedChartRemoveRequest(
        OwnedChartRemoveMode mode,
        ChartFileKind kind,
        BMSFile bmsOwner,
        LR2SongDBExtended.bmson_song bmsonOwner,
        string path,
        string capturedMd5,
        string capturedSha256,
        bool hasCapturedFacts)
    {
        Mode = mode;
        Kind = kind;
        BmsOwner = bmsOwner;
        BmsonOwner = bmsonOwner;
        Path = path;
        CapturedMd5 = capturedMd5;
        CapturedSha256 = capturedSha256;
        HasCapturedFacts = hasCapturedFacts;
    }

    internal OwnedChartRemoveMode Mode { get; }

    internal ChartFileKind Kind { get; }

    internal BMSFile BmsOwner { get; }

    internal LR2SongDBExtended.bmson_song BmsonOwner { get; }

    internal string Path { get; }

    /// <summary>破壊的な処理の前に捕捉した旧MD5。</summary>
    internal string CapturedMd5 { get; }

    /// <summary>破壊的な処理の前に捕捉した旧SHA-256。</summary>
    internal string CapturedSha256 { get; }

    /// <summary>kind、path、digestを解決済みの不変事実として保持するか。</summary>
    internal bool HasCapturedFacts { get; }

    /// <summary>
    /// BMS ownerを参照し、破壊的な処理に依存しない削除factsを捕捉します。
    /// </summary>
    /// <param name="file">削除対象のBMS storage owner。</param>
    /// <returns>owner参照と旧identityを保持する要求。入力がnullの場合はnull。</returns>
    internal static OwnedChartRemoveRequest FromOwnerReference(BMSFile file)
    {
        return file == null
            ? null
            : new OwnedChartRemoveRequest(
                OwnedChartRemoveMode.OwnerReference,
                ChartFileKind.Bms,
                file,
                null,
                file.path,
                file.hash,
                file.sha256,
                hasCapturedFacts: true);
    }

    /// <summary>
    /// BMS ownerを保持しつつ、破壊前に作成したimmutable projectionから削除factsを確定します。
    /// </summary>
    /// <param name="file">catalogから削除するBMS storage owner。</param>
    /// <param name="capturedChartSnapshot">破壊前に捕捉したowner identityのsnapshot。</param>
    /// <returns>owner参照とsnapshotの旧identityを保持する要求。いずれかがnullまたはpathなしの場合はnull。</returns>
    internal static OwnedChartRemoveRequest FromOwnerReference(
        BMSFile file,
        ChartFile capturedChartSnapshot)
    {
        if (file == null || capturedChartSnapshot == null || string.IsNullOrWhiteSpace(capturedChartSnapshot.Path))
        {
            return null;
        }

        return new OwnedChartRemoveRequest(
            OwnedChartRemoveMode.OwnerReference,
            ChartFileKind.Bms,
            file,
            null,
            capturedChartSnapshot.Path,
            capturedChartSnapshot.Md5,
            capturedChartSnapshot.Sha256,
            hasCapturedFacts: true);
    }

    /// <summary>
    /// BMSON ownerを参照し、破壊的な処理に依存しない削除factsを捕捉します。
    /// </summary>
    /// <param name="song">削除対象のBMSON storage owner。</param>
    /// <returns>owner参照と旧identityを保持する要求。入力がnullの場合はnull。</returns>
    internal static OwnedChartRemoveRequest FromOwnerReference(LR2SongDBExtended.bmson_song song)
    {
        return song == null
            ? null
            : new OwnedChartRemoveRequest(
                OwnedChartRemoveMode.OwnerReference,
                ChartFileKind.Bmson,
                null,
                song,
                song.path,
                song.md5,
                song.sha256,
                hasCapturedFacts: true);
    }

    /// <summary>
    /// BMSON ownerを保持しつつ、破壊前に作成したimmutable projectionから削除factsを確定します。
    /// </summary>
    /// <param name="song">catalogから削除するBMSON storage owner。</param>
    /// <param name="capturedChartSnapshot">破壊前に捕捉したowner identityのsnapshot。</param>
    /// <returns>owner参照とsnapshotの旧identityを保持する要求。いずれかがnullまたはpathなしの場合はnull。</returns>
    internal static OwnedChartRemoveRequest FromOwnerReference(
        LR2SongDBExtended.bmson_song song,
        ChartFile capturedChartSnapshot)
    {
        if (song == null || capturedChartSnapshot == null || string.IsNullOrWhiteSpace(capturedChartSnapshot.Path))
        {
            return null;
        }

        return new OwnedChartRemoveRequest(
            OwnedChartRemoveMode.OwnerReference,
            ChartFileKind.Bmson,
            null,
            song,
            capturedChartSnapshot.Path,
            capturedChartSnapshot.Md5,
            capturedChartSnapshot.Sha256,
            hasCapturedFacts: true);
    }

    /// <summary>
    /// canonical chartから対応するstorage ownerの削除要求を作成します。
    /// </summary>
    /// <param name="chart">削除対象のcanonical chart。</param>
    /// <returns>owner参照と旧identityを保持する要求。ownerがない場合はnull。</returns>
    internal static OwnedChartRemoveRequest FromOwnerReferenceChart(ChartFile chart)
    {
        if (chart == null)
        {
            return null;
        }

        BMSFile bmsOwner = chart.GetBmsStorageOwner();
        if (bmsOwner != null)
        {
            return FromOwnerReference(bmsOwner);
        }

        LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
        return bmsonOwner == null ? null : FromOwnerReference(bmsonOwner);
    }

    /// <summary>確認済みの旧DB行のexact keyを加工せず削除要求に保持します。</summary>
    internal static OwnedChartRemoveRequest FromPathCleanup(ChartFileKind kind, string path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : new OwnedChartRemoveRequest(
                OwnedChartRemoveMode.PathCleanup,
                kind,
                null,
                null,
                path,
                null,
                null,
                hasCapturedFacts: false);
    }

    /// <summary>
    /// 現在のcanonical chartから旧行のdigest factsを捕捉したpath cleanup要求を作成します。
    /// owner参照は保持せず、DB側のpath cleanup exact semanticsを変更しません。
    /// </summary>
    /// <param name="chart">現在のcanonical chart。</param>
    /// <returns>path cleanupと旧identityを保持する要求。chartまたはpathがない場合はnull。</returns>
    internal static OwnedChartRemoveRequest FromResolvedPathCleanup(ChartFile chart)
    {
        return chart == null || string.IsNullOrWhiteSpace(chart.Path)
            ? null
            : new OwnedChartRemoveRequest(
                OwnedChartRemoveMode.PathCleanup,
                chart.Kind,
                null,
                null,
                chart.Path,
                chart.Md5,
                chart.Sha256,
                hasCapturedFacts: true);
    }

    /// <summary>
    /// 削除要求が捕捉したidentityを持つ、索引・resource反映用のchart snapshotを作成します。
    /// </summary>
    /// <returns>pathがある要求のsnapshot。pathがない場合はnull。</returns>
    internal ChartFile CreateChartSnapshot()
    {
        if (HasCapturedFacts)
        {
            return ChartFileProjection.FromIdentitySnapshot(
                Kind,
                Path,
                CapturedMd5,
                CapturedSha256);
        }
        return string.IsNullOrWhiteSpace(Path)
            ? null
            : new ChartFile(
                Kind,
                Path,
                null,
                null,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                System.IO.Path.GetDirectoryName(Path),
                string.Empty,
                string.Empty,
                null,
                0,
                null,
                null,
                null);
    }
}
