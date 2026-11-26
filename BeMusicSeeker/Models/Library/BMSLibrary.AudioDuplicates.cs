using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary
{
    private readonly object audioDuplicateGate = new();

    private List<AudioDuplicateFolder> audioDuplicateFolders;

    private long audioDuplicateGeneration;

    /// <summary>失効した結果を含む、直近に格納した結果です。次の探索がグループのインスタンスを引き継ぐ基底で、失効では消しません。</summary>
    private List<AudioDuplicateFolder> audioDuplicateBase;

    /// <summary>探索の列挙を固定入力へ差し替える任意の列挙役です。本番では <see langword="null"/> で、Everything と管理側の列挙を使います。</summary>
    private readonly IRootFileEnumerator audioDuplicateRootFileEnumerator;

    /// <summary>
    /// 重複音源フォルダの現在の結果です。結果がない状態は <c>null</c>、0件は空の一覧です。保存しません。
    /// </summary>
    /// <remarks>
    /// 重複検索の結果が失効する契機で失効し、探索に使ったリソース索引の世代が現在と異なる結果は有効として扱いません。
    /// 一覧は格納後に変更しません。
    /// </remarks>
    internal List<AudioDuplicateFolder> AudioDuplicateFolders
    {
        get
        {
            lock (audioDuplicateGate)
            {
                return audioDuplicateFolders;
            }
        }
    }

    /// <summary>
    /// 結果があり、その結果を探索したリソース索引の世代が現在の世代と等しいかを返します。
    /// 画面が探索の要否を決めるための読取り専用の判定で、受付もライブラリのロックも取りません。0件の結果も有効です。
    /// </summary>
    internal bool HasCurrentAudioDuplicateFolders
    {
        get
        {
            long currentGeneration = libraryResourceIndexOwner.CaptureSnapshot().Generation;
            lock (audioDuplicateGate)
            {
                return audioDuplicateFolders != null && audioDuplicateGeneration == currentGeneration;
            }
        }
    }

    /// <summary>
    /// 重複音源の結果を失効させ、直前まで結果があった場合だけ変更を非同期に一回通知します。
    /// 重複検索の結果の有無と通知の抑止には従いません。基底は保持します。
    /// </summary>
    private void InvalidateAudioDuplicateFolders()
    {
        bool changed;
        lock (audioDuplicateGate)
        {
            changed = audioDuplicateFolders != null;
            audioDuplicateFolders = null;
        }
        if (changed)
        {
            Task.Run(delegate
            {
                RaisePropertyChanged(nameof(AudioDuplicateFolders));
            }).ObserveFault("AudioDuplicateFolders");
        }
    }

    /// <summary>
    /// 譜面フォルダの中に同名の WAV と OGG がある重複音源フォルダを探索し、<see cref="AudioDuplicateFolders"/> へ格納します。
    /// </summary>
    /// <remarks>
    /// 呼出し側が共通のライブラリ変更受付を保持している間に呼びます。受付はこのメソッドでは取得・確認しません。
    /// 有効な結果（現在のリソース索引の世代で探索した結果）があれば、探索も通知もせずに戻ります。
    /// 所持集合の行は既存の重複検索と同じロックの下で、登録BMSルートはその直後に、探索の開始時に一回だけ捕捉します。
    /// 捕捉の後はロックを保持せずに列挙と集計を行い、カタログ・警告・重複検索の結果を変更しません。
    /// 新しい結果を格納したら、ロックの外・呼出しスレッドで変更を一回通知します。
    /// 取消・失敗では保持している状態（結果・世代・基底）を変えず、通知もしません。
    /// グループのインスタンスは、直近に格納した結果（基底）から、失効した後の探索でも引き継ぎます。
    /// </remarks>
    /// <param name="progress">進捗の受け手。探索を実行するスレッドから同期に呼ばれ、例外は探索の失敗として伝わります。</param>
    /// <param name="cancellationToken">取消の合図。<see cref="OperationCanceledException"/> を送出します。</param>
    /// <exception cref="InvalidOperationException">列挙が不完全な場合（登録ルートが存在しない場合を含む）と、ブリッジの契約の不一致。</exception>
    internal void SearchAudioDuplicateFolders(
        IProgress<AudioDuplicateSearchProgress> progress, CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        LibraryResourceIndexSnapshot resourceSnapshot;
        OwnedDuplicateChartRowSnapshot rowSnapshot;
        List<AudioDuplicateFolder> baseFolders;
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                using (rwlockDuplicateChartGroups.GetWriterGuard())
                {
                    resourceSnapshot = libraryResourceIndexOwner.CaptureSnapshot();
                    lock (audioDuplicateGate)
                    {
                        if (audioDuplicateFolders != null && audioDuplicateGeneration == resourceSnapshot.Generation)
                        {
                            LogInstallPerformance("SearchAudioDuplicateFolders: cacheHit=true totalMs=" + totalStopwatch.ElapsedMilliseconds
                                + " Folders=" + audioDuplicateFolders.Count);
                            return;
                        }
                        baseFolders = audioDuplicateBase;
                    }
                    rowSnapshot = CreateOwnedDuplicateChartRowSnapshotUnsafe();
                }
            }
        }
        List<string> roots = CaptureAudioDuplicateScanRoots();
        long captureMs = totalStopwatch.ElapsedMilliseconds;
        cancellationToken.ThrowIfCancellationRequested();

        // 以降はロックを保持しない。入力は不変の捕捉だけを読む。
        progress?.Report(new AudioDuplicateSearchProgress(AudioDuplicateSearchStage.Enumerate));
        WavOggCollisionSearchResult search = WavOggCollisionFinder.Find(roots, everythingNative, audioDuplicateRootFileEnumerator);
        long enumerateEndMs = totalStopwatch.ElapsedMilliseconds;
        if (!search.Success)
        {
            LogInstallPerformance("SearchAudioDuplicateFolders: failed backend=" + search.BackendName
                + " reason=" + search.FailureReason + " totalMs=" + enumerateEndMs);
            throw new InvalidOperationException("audio_duplicate_enumeration_failed:" + search.FailureReason);
        }
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new AudioDuplicateSearchProgress(AudioDuplicateSearchStage.Aggregate));
        List<string> folderNames = [];
        List<List<ChartFile>> folderCharts = [];
        var folderIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var folderIdsByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (DuplicateChartRow row in rowSnapshot.Rows)
        {
            string directory = row.DirectoryPath;
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }
            ChartFile chart = row.CreateChart();
            if (chart == null)
            {
                continue;
            }
            if (!folderIds.TryGetValue(directory, out int id))
            {
                id = folderNames.Count;
                folderIds[directory] = id;
                folderNames.Add(directory);
                folderCharts.Add([]);
                folderIdsByKey.TryAdd(NormalizeAudioDuplicateKey(directory), id);
            }
            folderCharts[id].Add(chart);
        }

        // 組のあるディレクトリだけを、自身から祖先へたどって最初に見つかる対象フォルダへ割り当てる。
        var namesByFolder = new Dictionary<int, HashSet<string>>();
        foreach (WavOggCollision collision in search.Collisions)
        {
            string directoryKey = NormalizeAudioDuplicateKey(collision.Directory);
            string current = directoryKey;
            while (current.Length > 0)
            {
                if (folderIdsByKey.TryGetValue(current, out int folderId))
                {
                    string relative = directoryKey.Length > current.Length ? directoryKey[(current.Length + 1)..] : string.Empty;
                    string name = relative.Length == 0 ? collision.Name : relative + "\\" + collision.Name;
                    if (!namesByFolder.TryGetValue(folderId, out HashSet<string> names))
                    {
                        names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        namesByFolder[folderId] = names;
                    }
                    names.Add(name);
                    break;
                }
                int separator = current.LastIndexOf('\\');
                if (separator <= 0)
                {
                    break;
                }
                current = current[..separator];
            }
        }

        List<AudioDuplicateFolder> folders = [];
        foreach (KeyValuePair<int, HashSet<string>> pair in namesByFolder)
        {
            string[] names = [.. pair.Value.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
            folders.Add(new AudioDuplicateFolder(new DuplicateGroup(folderCharts[pair.Key], [folderNames[pair.Key]]), names));
        }
        int inherited = InheritAudioDuplicateFolders(folders, baseFolders);
        folders.Sort(CompareAudioDuplicateFolders);
        long aggregateEndMs = totalStopwatch.ElapsedMilliseconds;
        cancellationToken.ThrowIfCancellationRequested();

        lock (audioDuplicateGate)
        {
            audioDuplicateFolders = folders;
            audioDuplicateGeneration = resourceSnapshot.Generation;
            audioDuplicateBase = folders;
        }
        totalStopwatch.Stop();
        LogInstallPerformance("SearchAudioDuplicateFolders: cacheHit=false"
            + " backend=" + search.BackendName
            + " totalMs=" + totalStopwatch.ElapsedMilliseconds
            + " captureMs=" + captureMs
            + " enumerateMs=" + (enumerateEndMs - captureMs)
            + " enumerationEngineMs=" + search.EnumerationMs
            + " collectMs=" + search.AggregateMs
            + " assignMs=" + (aggregateEndMs - enumerateEndMs)
            + " targetFolders=" + folderNames.Count
            + " hits=" + (search.HitCount?.ToString() ?? "unknown")
            + " collisions=" + search.Collisions.Count
            + " Folders=" + folders.Count
            + " inheritedFolders=" + inherited);

        TryInvokePostLeaseNotification(
            () => RaisePropertyChanged(nameof(AudioDuplicateFolders)),
            "audio_duplicate_folders_notification_failed");
    }

    /// <summary>
    /// 登録BMSルートを捕捉します。ライブラリの更新前検査の要求が持つ走査のルートのうち、存在するものと、更新前検査が存在を必須とするもの
    /// だけを範囲に残します。必須とするルートは更新前検査の要求（<see cref="CaptureDirectoryPreflightRequest"/>）の判定を共用し、
    /// 出力基点の配下の未作成のルートは範囲から外します。存在しない必須のルートは列挙を不完全にし、探索の失敗になります。
    /// </summary>
    private List<string> CaptureAudioDuplicateScanRoots()
    {
        LibraryDirectoryPreflightRequest request = CaptureDirectoryPreflightRequest(CurrentOptionsSnapshot);
        var required = new HashSet<string>(
            request.BmsRootDirectories.Select(NormalizeAudioDuplicateKey), StringComparer.OrdinalIgnoreCase);
        // 走査のルートは存在確認の前の登録で、存在するものと、存在を必須とするものだけを範囲に残す。
        return [.. request.ScanRootDirectories.Where(root =>
            !string.IsNullOrWhiteSpace(root)
            && (LongPathFileSystem.DirectoryExists(root) || required.Contains(NormalizeAudioDuplicateKey(root))))];
    }

    /// <summary>区切りを <c>\</c> にそろえ、末尾の区切りを除きます。ディレクトリの比較は大文字小文字を区別しない辞書で行います。</summary>
    private static string NormalizeAudioDuplicateKey(string directory)
        => (directory ?? string.Empty).Replace('/', '\\').TrimEnd('\\');

    /// <summary>見出しの序数順、同じ見出しはフォルダのパスの昇順（大文字小文字を無視する序数比較）。</summary>
    private static int CompareAudioDuplicateFolders(AudioDuplicateFolder x, AudioDuplicateFolder y)
    {
        int byHeader = string.CompareOrdinal(x.Group.Header, y.Group.Header);
        return byHeader != 0 ? byHeader : StringComparer.OrdinalIgnoreCase.Compare(x.Folder, y.Folder);
    }

    /// <summary>
    /// 新しい結果のうち、基底のフォルダとフォルダ（大文字小文字を無視）、譜面の所持項目の識別・パス（完全一致）・順序、
    /// 衝突名の集合（大文字小文字を無視）が一致するものを、基底の <see cref="DuplicateGroup"/> インスタンスへ置き換えます。
    /// 衝突名の一覧は新しい探索のものを持ちます。引き継いだ数を返します。
    /// </summary>
    private static int InheritAudioDuplicateFolders(List<AudioDuplicateFolder> folders, List<AudioDuplicateFolder> baseFolders)
    {
        if (baseFolders == null || baseFolders.Count == 0)
        {
            return 0;
        }
        var byFolder = new Dictionary<string, AudioDuplicateFolder>(StringComparer.OrdinalIgnoreCase);
        foreach (AudioDuplicateFolder previous in baseFolders)
        {
            byFolder[previous.Folder] = previous;
        }
        int inherited = 0;
        for (int i = 0; i < folders.Count; i++)
        {
            AudioDuplicateFolder current = folders[i];
            if (byFolder.TryGetValue(current.Folder, out AudioDuplicateFolder previous)
                && HasSameMembership(current.Group, previous.Group)
                && HasSameCollisionNames(current.CollisionNames, previous.CollisionNames))
            {
                folders[i] = new AudioDuplicateFolder(previous.Group, current.CollisionNames);
                inherited++;
            }
        }
        return inherited;
    }

    private static bool HasSameCollisionNames(IReadOnlyList<string> current, IReadOnlyList<string> previous)
        => current.Count == previous.Count
            && new HashSet<string>(previous, StringComparer.OrdinalIgnoreCase).SetEquals(current);
}
