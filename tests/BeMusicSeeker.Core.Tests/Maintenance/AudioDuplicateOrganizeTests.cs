using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryDuplicateServiceTestSupport;
using static BeMusicSeeker.TestSupport.Components.SplitPlacementTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 重複音源の整理（モデルの入口 <c>BMSLibrary.OrganizeAudioDuplicateFolders</c>）を、実ファイル・SQLite・実ライブラリで確認します。
/// 実際のごみ箱は使わず、<see cref="TestFileMutationService"/> が削除の記録と失敗を注入します。
/// 名前ごとの観測と削除の細部（NUL・予約文字、親ディレクトリなし、ディレクトリなど）は共用する部品を通る
/// <see cref="DuplicateBatchOrganizeTests"/> の衝突整理のケースが担当するため、ここでは各分類の代表だけを確かめます。
/// </summary>
[TestClass]
public sealed class AudioDuplicateOrganizeTests
{
    private static readonly string[] GateNames = ["rwlockBMSFilesInitializedMin", "rwlockBMSFiles", "rwlockDuplicateChartGroups"];

    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>実ファイルとSQLiteを持つ、一回の整理の入力一式です。Root が登録BMSルートで、探索はこの配下を列挙します。</summary>
    private sealed class Rig
    {
        private readonly ApplicationDatabaseScope databaseScope;
        private readonly string songDbPath;

        internal Rig(ApplicationDatabaseScope databaseScope, string songDbPath)
        {
            this.databaseScope = databaseScope;
            this.songDbPath = songDbPath;
            Root = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Library");
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        internal TestFileMutationService Mutations { get; } = new();

        internal RecordingDialogService Dialogs { get; } = new();

        /// <summary>プレイリスト出力の局所受付。管理出力の保護を取れない場合の再現に使います。</summary>
        internal ChartFileOperationSynchronizer? PlaylistAdmission { get; init; }

        internal Lr2ConfigOwner? Lr2Owner { get; init; }

        internal bool OperationModeLr2 { get; init; }

        internal string? CustomFolderOutputBaseDir { get; init; }

        internal TestBmsLibrary Library { get; private set; } = null!;

        internal string At(string relative) => Path.Combine(Root, relative);

        internal string Chart(string folder, string name, string id)
        {
            string path = Path.Combine(Dir(folder), name);
            System.IO.File.WriteAllText(path, "#PLAYER 1\r\n#TITLE audio\r\n#ARTIST audio\r\n#BPM 120\r\n#COMMENT " + id + "\r\n#00111:01\r\n");
            return path;
        }

        internal string Dir(string folder)
        {
            string path = Path.Combine(Root, folder);
            Directory.CreateDirectory(path);
            return path;
        }

        internal string Write(string relative, string content)
        {
            string path = At(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, content);
            return path;
        }

        /// <summary>WAVとOGGの組を、内容が識別できる形で作ります。</summary>
        internal void Pair(string folder, string name)
        {
            Write(folder + "\\" + name + ".wav", folder + name + "wav");
            Write(folder + "\\" + name + ".ogg", folder + name + "ogg");
        }

        /// <summary>配下の全譜面をDBと所持へ登録し、実ライブラリを作ります。</summary>
        internal void Start()
        {
            ChartFile[] bms = [.. Directory.EnumerateFiles(Root, "*.bms", SearchOption.AllDirectories)
                .Select(path => BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path)))];
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = OperationModeLr2,
                LR2CustomFolderOutputBaseDir = CustomFolderOutputBaseDir,
                BMSInstallDir = Root
            };
            Library = new TestBmsLibrary(databaseScope, songDbPath, Lr2Owner, null, Mutations, Dialogs,
                new TestUiScheduler(() => null), () => options, playlistOperationAdmission: PlaylistAdmission,
                settings: Lr2Owner == null ? TestBmsFactory.CreateRootSettings([Root]) : null,
                rootFileEnumerator: new FastRootFileEnumerator())
            { BmsCharts = bms, BmsonCharts = [] };
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).UpsertSongs(bms);
        }

        internal AudioDuplicateOrganizeResult Organize(
            AudioDuplicateOrganizeRequest request,
            Action<AudioDuplicateOrganizeProgress>? progress = null,
            CancellationToken cancellationToken = default,
            LibraryOperationNotices? notices = null)
            => Library.OrganizeAudioDuplicateFolders(request, 1, notices ?? new LibraryOperationNotices(), progress, cancellationToken);

        /// <summary>要求を作ります。フォルダは Root からの相対パスで指定します。</summary>
        internal AudioDuplicateOrganizeRequest Request(params (string Folder, string Discard, string[] Names)[] folders)
            => new(folders.Select(folder => new AudioDuplicateOrganizeFolder(
                folder.Folder.Length == 0 || Path.IsPathRooted(folder.Folder) ? folder.Folder : At(folder.Folder), folder.Discard, folder.Names)));

        internal List<KeyValuePair<string, string>> SongRows()
        {
            using LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            return [.. db.Table<LR2SongDB.song>().Select(row => new KeyValuePair<string, string>(Path.GetRelativePath(Root, row.path), row.hash))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)];
        }

        internal SortedDictionary<string, byte[]> Tree()
            => new(Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(Root, path), System.IO.File.ReadAllBytes, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        internal string[] ShellDeletedAudio()
            => [.. Mutations.ShellDeletedFiles.Select(path => Path.GetRelativePath(Root, path).ToLowerInvariant()).Order(StringComparer.Ordinal)];
    }

    private void Run(Action<Rig> arrange, Action<Rig> test)
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            var rig = new Rig(databaseScope, songDbPath);
            arrange(rig);
            rig.Start();
            try { test(rig); }
            finally { rig.Library.RequestShutdown("audio-duplicate-organize"); }
        });
    }

    private static void AssertSameTree(SortedDictionary<string, byte[]> expected, SortedDictionary<string, byte[]> actual)
    {
        CollectionAssert.AreEqual(expected.Keys.ToArray(), actual.Keys.ToArray(), "ファイルの集合が変わりました。");
        foreach (KeyValuePair<string, byte[]> item in expected)
        {
            CollectionAssert.AreEqual(item.Value, actual[item.Key], item.Key + " のバイト列が変わりました。");
        }
    }

    /// <summary>
    /// 保証：両方の形式がファイルとして実在する名前だけ、捨てる形式をごみ箱へ送る（<c>.wav</c> を捨てる場合と <c>.ogg</c> を捨てる場合。拡張子の大文字、下位フォルダの名前を含む）。
    /// 残す形式または捨てる形式がない名前、存在しない名前は変えず数えず、要求にない名前の音源も変えない。完了で、送れなかった名前は空。
    /// 検出する誤り：実在を確かめずに送る（残す形式がない音源を失う）、要求にない名前を送る、拡張子の大文字の見落とし、変更しない名前を失敗に数える。
    /// </summary>
    [TestMethod]
    [DataRow(".wav", DisplayName = "OGGを残す")]
    [DataRow(".ogg", DisplayName = "WAVを残す")]
    public void Organize_TrashesDiscardFormatOnlyWhereBothFormsExist(string discard)
    {
        string keep = discard == ".wav" ? ".ogg" : ".wav";
        Run(rig =>
        {
            rig.Chart("K", "k.bms", "k");
            rig.Write("K\\a" + discard, "a-discard");
            rig.Write("K\\a" + keep, "a-keep");
            rig.Write("K\\sub\\s" + discard.ToUpperInvariant(), "s-discard");
            rig.Write("K\\sub\\s" + keep, "s-keep");
            rig.Write("K\\onlydiscard" + discard, "only-discard");
            rig.Write("K\\onlykeep" + keep, "only-keep");
            rig.Write("K\\unlisted" + discard, "unlisted-discard");
            rig.Write("K\\unlisted" + keep, "unlisted-keep");
        }, rig =>
        {
            AudioDuplicateOrganizeResult result = rig.Organize(
                rig.Request(("K", discard, ["a", "sub\\s", "onlydiscard", "onlykeep", "missing"])));

            Assert.IsTrue(result.Admitted);
            Assert.IsFalse(result.Canceled);
            AudioDuplicateOrganizeFolderResult folder = result.Folders.Single();
            Assert.AreEqual(AudioDuplicateOrganizeStatus.Completed, folder.Status);
            Assert.AreEqual(2, folder.TrashedCount);
            Assert.AreEqual(0, folder.UnsentNames.Count);
            CollectionAssert.AreEqual(
                new[] { "k\\a" + discard, "k\\sub\\s" + discard }.Order(StringComparer.Ordinal).ToArray(), rig.ShellDeletedAudio());
            Assert.IsFalse(File.Exists(rig.At("K\\a" + discard)) || File.Exists(rig.At("K\\sub\\s" + discard)));
            Assert.AreEqual("a-keep", File.ReadAllText(rig.At("K\\a" + keep)));
            Assert.AreEqual("s-keep", File.ReadAllText(rig.At("K\\sub\\s" + keep)));
            Assert.AreEqual("only-discard", File.ReadAllText(rig.At("K\\onlydiscard" + discard)));
            Assert.AreEqual("only-keep", File.ReadAllText(rig.At("K\\onlykeep" + keep)));
            Assert.AreEqual("unlisted-discard", File.ReadAllText(rig.At("K\\unlisted" + discard)));
            Assert.AreEqual("unlisted-keep", File.ReadAllText(rig.At("K\\unlisted" + keep)));
        });
    }

    /// <summary>
    /// 保証：属性を取得できない名前（NUL文字を含み、不存在とは別の例外になる名前）は削除を試みず送れなかった名前に数え、削除の失敗は送れなかった名前に数えてファイルを残す。
    /// どちらも停止の理由にせず、同じフォルダの残りの名前と次のフォルダの整理を続け、失敗のあるフォルダだけが一部完了になる。
    /// 検出する誤り：観測の失敗を不存在として外す、観測せずに削除する、削除の失敗で整理を止める、失敗を完了として報告する。
    /// </summary>
    [TestMethod]
    public void Organize_UnobservableNameAndDeleteFailureAreCountedAndTheOperationContinues()
    {
        Run(rig =>
        {
            foreach (string folder in new[] { "K1", "K2" })
            {
                rig.Chart(folder, folder + ".bms", folder);
                foreach (string name in new[] { "x", "y" })
                {
                    rig.Pair(folder, name);
                }
            }
        }, rig =>
        {
            string failing = rig.At("K1\\y.wav");
            rig.Mutations.FailDeleteFileShellWhen = path => string.Equals(path, failing, StringComparison.OrdinalIgnoreCase);

            AudioDuplicateOrganizeResult result = rig.Organize(rig.Request(
                ("K1", ".wav", ["bad\0name", "x", "y"]),
                ("K2", ".wav", ["x", "y"])));

            AudioDuplicateOrganizeFolderResult first = result.Folders[0], second = result.Folders[1];
            Assert.AreEqual(AudioDuplicateOrganizeStatus.PartiallyCompleted, first.Status);
            Assert.AreEqual(1, first.TrashedCount);
            Assert.AreEqual(2, first.FailureCount);
            CollectionAssert.AreEqual(new[] { "bad\0name", "y" }, first.UnsentNames.ToArray());
            Assert.IsFalse(File.Exists(rig.At("K1\\x.wav")));
            Assert.IsTrue(File.Exists(rig.At("K1\\y.wav")), "削除に失敗したファイルは残します。");
            // 作業領域のパスは無作為な名前を含むため、作業領域からの相対パス（対象フォルダ K1 の下の名前）で判定する。
            Assert.IsFalse(rig.ShellDeletedAudio().Any(relative => relative.StartsWith("k1\\bad", StringComparison.Ordinal)), "観測できなかった名前の削除を試みてはいけません。");
            Assert.AreEqual(AudioDuplicateOrganizeStatus.Completed, second.Status);
            Assert.AreEqual(2, second.TrashedCount);
            Assert.IsFalse(File.Exists(rig.At("K2\\x.wav")) || File.Exists(rig.At("K2\\y.wav")));
        });
    }

    /// <summary>
    /// 保証：存在しないフォルダ、パスが空のフォルダは、理由付きでスキップになりファイルを変えず、次のフォルダの処理を続ける。
    /// 検出する誤り：存在しないフォルダでの失敗・例外による停止、スキップしたフォルダへの変更、理由の取り違え。
    /// </summary>
    [TestMethod]
    public void Organize_SkipsMissingAndInvalidPathFoldersAndContinues()
    {
        Run(rig =>
        {
            rig.Chart("K", "k.bms", "k");
            rig.Pair("K", "x");
        }, rig =>
        {
            string missing = rig.At("Missing");

            AudioDuplicateOrganizeResult result = rig.Organize(rig.Request(
                ("Missing", ".wav", ["x"]), ("", ".wav", ["x"]), ("K", ".wav", ["x"])));

            Assert.AreEqual(AudioDuplicateOrganizeStatus.Skipped, result.Folders[0].Status);
            Assert.AreEqual(DuplicateBatchOrganizeSkipReason.FolderMissing, result.Folders[0].SkipReason);
            Assert.AreEqual(missing, result.Folders[0].SkipPath);
            Assert.AreEqual(AudioDuplicateOrganizeStatus.Skipped, result.Folders[1].Status);
            Assert.AreEqual(DuplicateBatchOrganizeSkipReason.InvalidPath, result.Folders[1].SkipReason);
            Assert.IsTrue(result.Folders.Take(2).All(folder => folder.TrashedCount == 0 && folder.UnsentNames.Count == 0));
            Assert.AreEqual(AudioDuplicateOrganizeStatus.Completed, result.Folders[2].Status);
            Assert.AreEqual(1, result.Folders[2].TrashedCount);
            Assert.IsFalse(Directory.Exists(missing), "存在しないフォルダを作りません。");
        });
    }

    /// <summary>
    /// 保証：整理は変更セッションを開かず、カタログ・DB・リソース索引の世代・重複検索の結果を変えず、ライブラリの変更通知を出さない
    /// （結果がない状態では、探索の結果の有無も変わらない）。一方、音源のファイルだけが変わる。
    /// 検出する誤り：音源の整理でカタログ・索引へ反映する、重複検索の結果を失効させる、不要な通知。
    /// </summary>
    [TestMethod]
    public void Organize_DoesNotOpenASessionOrChangeCatalogDbIndexOrDuplicateResults()
    {
        Run(rig =>
        {
            rig.Chart("K", "k.bms", "k");
            rig.Chart("L", "l.bms", "l");
            rig.Pair("K", "x");
            rig.Pair("L", "x");
        }, rig =>
        {
            rig.Library.SearchDuplicateChartGroups();
            List<DuplicateGroup>? duplicates = rig.Library.DuplicateChartGroups;
            int revision = rig.Library.CatalogRevision;
            long generation = LibraryResourceIndexTestSupport.GetOwner(rig.Library).CaptureSnapshot().Generation;
            List<KeyValuePair<string, string>> rows = rig.SongRows();
            ChartFile[] charts = [.. rig.Library.BmsCharts];
            var changed = new List<string>();
            rig.Library.PropertyChanged += (_, args) => changed.Add(args.PropertyName ?? string.Empty);
            var notices = new LibraryOperationNotices();

            AudioDuplicateOrganizeResult result = rig.Organize(rig.Request(("K", ".wav", ["x"]), ("L", ".ogg", ["x"])), notices: notices);

            Assert.IsTrue(result.Admitted);
            Assert.AreEqual(2, result.Folders.Sum(folder => folder.TrashedCount));
            Assert.IsFalse(File.Exists(rig.At("K\\x.wav")) || File.Exists(rig.At("L\\x.ogg")));
            Assert.AreEqual(revision, rig.Library.CatalogRevision);
            Assert.AreEqual(generation, LibraryResourceIndexTestSupport.GetOwner(rig.Library).CaptureSnapshot().Generation);
            Assert.AreSame(duplicates, rig.Library.DuplicateChartGroups);
            CollectionAssert.AreEqual(rows, rig.SongRows());
            CollectionAssert.AreEqual(charts, rig.Library.BmsCharts.ToArray());
            Assert.AreEqual(0, changed.Count, string.Join("、", changed));
            Assert.IsNull(rig.Library.AudioDuplicateFolders);
            Assert.IsFalse(rig.Library.HasCurrentAudioDuplicateFolders);
            Assert.AreEqual(0, notices.Snapshot().Count);
        });
    }

    /// <summary>
    /// 保証：共通受付が使用中（Busy）の整理は、物理変更の前に操作全体を未適用で返し、進捗を渡さず、全フォルダを未処理にしてファイルを変えない。
    /// 結果がある探索の結果も更新しない。受付を取れないのに物理変更へ進む誤りと、進捗を渡す誤りを検出する。
    /// </summary>
    [TestMethod]
    public void Organize_BusyAdmission_RejectsTheWholeOperation()
    {
        Run(rig =>
        {
            rig.Chart("K", "k.bms", "k");
            rig.Pair("K", "x");
        }, rig =>
        {
            rig.Library.SearchAudioDuplicateFolders(null, CancellationToken.None);
            List<AudioDuplicateFolder> before = rig.Library.AudioDuplicateFolders!;
            SortedDictionary<string, byte[]> tree = rig.Tree();
            var progress = new List<AudioDuplicateOrganizeProgress>();
            AudioDuplicateOrganizeResult result;
            using (LibraryFileMutationLease? held = rig.Library.Lr2Synchronization.TryBeginMutation("audio_busy_probe"))
            {
                Assert.IsNotNull(held);
                result = rig.Organize(rig.Request(("K", ".wav", ["x"])), progress.Add);
            }

            AssertNotAdmitted(result, progress);
            AssertSameTree(tree, rig.Tree());
            Assert.AreSame(before, rig.Library.AudioDuplicateFolders);
        });
    }

    /// <summary>
    /// 保証：管理出力（プレイリスト出力）の保護を取れない整理は、最初の物理変更の前に操作全体を未適用で返し、既存のBusy案内を一件だけ加え、ファイルを変えない。
    /// 管理出力の保護を取らずに物理変更へ進む誤りと、案内の欠落・重複を検出する。
    /// </summary>
    [TestMethod]
    public void Organize_ManagedOutputBusy_RejectsTheWholeOperationWithOneNotice()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            var playlist = new ChartFileOperationSynchronizer();
            var rig = new Rig(databaseScope, songDbPath)
            {
                PlaylistAdmission = playlist,
                Lr2Owner = new Lr2ConfigOwner(TestLr2ConfigFactory.CreateLr2Config(Path.Combine(root, "LR2"), root)),
                OperationModeLr2 = true,
                CustomFolderOutputBaseDir = root
            };
            rig.Chart("K", "k.bms", "k");
            rig.Pair("K", "x");
            rig.Start();
            try
            {
                SortedDictionary<string, byte[]> tree = rig.Tree();
                var notices = new LibraryOperationNotices();
                var progress = new List<AudioDuplicateOrganizeProgress>();
                AudioDuplicateOrganizeResult result;
                Assert.IsTrue(playlist.TryEnter(out IDisposable? heldPlaylist));
                using (heldPlaylist)
                {
                    result = rig.Organize(rig.Request(("K", ".wav", ["x"])), progress.Add, notices: notices);
                }

                AssertNotAdmitted(result, progress);
                Assert.AreEqual(Resources.Warn_LibraryOperationBusy, notices.Snapshot().Single().MessageBoxText);
                AssertSameTree(tree, rig.Tree());
            }
            finally
            {
                rig.Library.RequestShutdown("audio-organize-managed-output-busy");
            }
        });
    }

    private static void AssertNotAdmitted(AudioDuplicateOrganizeResult result, List<AudioDuplicateOrganizeProgress> progress)
    {
        Assert.IsFalse(result.Admitted);
        Assert.IsFalse(result.Canceled);
        Assert.IsTrue(result.Folders.All(folder => folder.Status == AudioDuplicateOrganizeStatus.Unprocessed
            && folder.TrashedCount == 0 && folder.UnsentNames.Count == 0));
        Assert.AreEqual(0, progress.Count);
    }

    /// <summary>
    /// 保証：取消はフォルダの開始前にだけ確認する。処理中のフォルダは取消の後も最後まで整理し、残りのフォルダは未処理でファイルを変えない。
    /// 進捗は取消の確認の後、1始まりの番号・総数・フォルダで、開始したフォルダの分だけ渡し、事前に取り消せば何も渡さず全フォルダが未処理になる。
    /// 検出する誤り：処理中のフォルダを途中で止める、未着手のフォルダを整理する、取消の確認の前の進捗、取消の確認の欠落。
    /// </summary>
    [TestMethod]
    public void Organize_CancellationIsCheckedBeforeEachFolderAndProgressFollowsTheCheck()
    {
        Run(rig =>
        {
            foreach (string folder in new[] { "K1", "K2", "K3" })
            {
                rig.Chart(folder, folder + ".bms", folder);
                rig.Pair(folder, "x");
            }
        }, rig =>
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var progress = new List<AudioDuplicateOrganizeProgress>();
                AudioDuplicateOrganizeResult result = rig.Organize(
                    rig.Request(("K1", ".wav", ["x"]), ("K2", ".wav", ["x"]), ("K3", ".wav", ["x"])),
                    value =>
                    {
                        progress.Add(value);
                        if (value.Number == 2) { cancellation.Cancel(); }
                    },
                    cancellation.Token);

                Assert.IsTrue(result.Admitted);
                Assert.IsTrue(result.Canceled);
                CollectionAssert.AreEqual(
                    new[] { new AudioDuplicateOrganizeProgress(1, 3, rig.At("K1")), new AudioDuplicateOrganizeProgress(2, 3, rig.At("K2")) },
                    progress);
                Assert.AreEqual(AudioDuplicateOrganizeStatus.Completed, result.Folders[0].Status);
                Assert.AreEqual(AudioDuplicateOrganizeStatus.Completed, result.Folders[1].Status);
                Assert.AreEqual(1, result.Folders[1].TrashedCount, "処理中のフォルダは取消の後も最後まで整理します。");
                Assert.AreEqual(AudioDuplicateOrganizeStatus.Unprocessed, result.Folders[2].Status);
                Assert.IsTrue(File.Exists(rig.At("K3\\x.wav")) && File.Exists(rig.At("K3\\x.ogg")));
            }

            using var before = new CancellationTokenSource();
            before.Cancel();
            var none = new List<AudioDuplicateOrganizeProgress>();
            AudioDuplicateOrganizeResult canceled = rig.Organize(rig.Request(("K3", ".wav", ["x"])), none.Add, before.Token);

            Assert.IsTrue(canceled.Admitted);
            Assert.IsTrue(canceled.Canceled);
            Assert.AreEqual(0, none.Count);
            Assert.AreEqual(AudioDuplicateOrganizeStatus.Unprocessed, canceled.Folders.Single().Status);
            Assert.IsTrue(File.Exists(rig.At("K3\\x.wav")));
        });
    }

    /// <summary>
    /// 保証：進捗の受け手はモデルのロックを保持せずに呼ばれ、受け手が例外を送出しても、整理の結果は変わらない（全フォルダが整理される）。
    /// 検出する誤り：ロックを保持したままの進捗、受け手の失敗による整理の中断。
    /// </summary>
    [TestMethod]
    public void Organize_ProgressReceiverRunsOutsideLocksAndItsFailureDoesNotChangeTheResult()
    {
        Run(rig =>
        {
            foreach (string folder in new[] { "K1", "K2" })
            {
                rig.Chart(folder, folder + ".bms", folder);
                rig.Pair(folder, "x");
            }
        }, rig =>
        {
            bool locksFree = true;

            AudioDuplicateOrganizeResult result = rig.Organize(rig.Request(("K1", ".wav", ["x"]), ("K2", ".wav", ["x"])), _ =>
            {
                locksFree &= BmsLibraryLockProbe.AreModelLocksFree(rig.Library);
                throw new InvalidOperationException("receiver");
            });

            Assert.IsTrue(locksFree, "進捗の受け手を、モデルのロックを保持したまま呼びました。");
            Assert.IsTrue(result.Folders.All(folder => folder.Status == AudioDuplicateOrganizeStatus.Completed && folder.TrashedCount == 1));
            Assert.IsFalse(File.Exists(rig.At("K1\\x.wav")) || File.Exists(rig.At("K2\\x.wav")));
        });
    }

    /// <summary>
    /// 保証：起動後に実差分読込みでパスが収束していない（カタログ未収束）操作全体は、物理変更の前に未適用で返す。受理されず、全フォルダが未処理で、
    /// 既存の案内が一件だけ収集先に入り、進捗は呼ばれず、ファイルは変わらない（重複グループの一括整理と同じ形）。
    /// 検出する誤り：未収束のカタログへ物理変更を行う、拒否の案内を出さない・重ねて出す、未適用でも進捗を通知する。
    /// </summary>
    [TestMethod]
    public void Organize_CatalogNotConverged_RejectsTheWholeOperationWithOneNotice()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string folder = Path.Combine(root, "Source");
            Directory.CreateDirectory(folder);
            string plain = Path.Combine(folder, "chart.bms"), dot = Path.Combine(folder, ".", "chart.bms");
            File.WriteAllText(plain, "#PLAYER 1\r\n#TITLE audio\r\n#ARTIST audio\r\n#BPM 120\r\n#COMMENT source\r\n#00111:01\r\n");
            string wav = Path.Combine(folder, "x.wav"), ogg = Path.Combine(folder, "x.ogg");
            File.WriteAllText(wav, "wav");
            File.WriteAllText(ogg, "ogg");
            using (LR2SongDBExtended setup = databaseScope.OpenSongWrite(songDbPath))
            {
                ChartFile row = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(plain)) with { Path = dot };
                setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(row), typeof(LR2SongDB.song));
            }
            var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false, ScanBmsFilesOnStartup = false, PendingInstallEstimateMaxParallelPackages = 1 };
            IChartFileScanner scanner = CapturedChartFileScanner.FromFixture(
                [plain],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase) { [folder] = [] },
                [root]);
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService(),
                new TestUiScheduler(() => null), () => options, scanner, settings: TestBmsFactory.CreateRootSettings([root]))
            { StartupBackgroundTaskScheduler = (_, _, _, _) => false };
            try
            {
                library.InitializeStartup(null);
                var notices = new LibraryOperationNotices();
                var progress = new List<AudioDuplicateOrganizeProgress>();

                AudioDuplicateOrganizeResult result = library.OrganizeAudioDuplicateFolders(
                    new AudioDuplicateOrganizeRequest([new AudioDuplicateOrganizeFolder(folder, ".wav", ["x"])]), 1, notices, progress.Add, CancellationToken.None);

                Assert.IsFalse(result.Admitted);
                Assert.IsFalse(result.Canceled);
                Assert.IsTrue(result.Folders.All(item => item.Status == AudioDuplicateOrganizeStatus.Unprocessed && item.TrashedCount == 0));
                Assert.AreEqual(0, progress.Count);
                Assert.AreEqual(1, notices.Snapshot().Count, "既存の案内の件数が一致しません。");
                Assert.AreEqual("wav", File.ReadAllText(wav));
                Assert.AreEqual("ogg", File.ReadAllText(ogg));
            }
            finally
            {
                library.RequestShutdown("audio-duplicate-not-converged");
            }
        });
    }

    private static void ArrangeFourFolders(Rig rig)
    {
        foreach (string folder in new[] { "A", "B", "C", "D" })
        {
            rig.Chart(folder, folder.ToLowerInvariant() + ".bms", folder);
        }
        foreach (string name in new[] { "x", "y", "z" })
        {
            rig.Pair("A", name);
        }
        rig.Pair("B", "p");
        rig.Pair("C", "q");
        rig.Pair("D", "r");
    }

    private static Dictionary<string, AudioDuplicateFolder> ByFolder(Rig rig)
        => rig.Library.AudioDuplicateFolders!.ToDictionary(folder => Path.GetRelativePath(rig.Root, folder.Folder));

    /// <summary>
    /// 保証：整理の後、保持している探索の結果を、完了・一部完了のフォルダの衝突名を送れなかった名前だけにして更新する。
    /// 一部完了で送れなかった名前だけが残ったフォルダ（要求に含まれない探索時点の名前は除く）は新しい <c>DuplicateGroup</c> インスタンス、衝突名が残らない完了のフォルダは除き、
    /// スキップ（フォルダが消えた）のフォルダは同じインスタンスのまま残る。変更があれば変更通知は一回で、ライブラリのロックを持たない。
    /// 検出する誤り：結果の更新の欠落、送れた名前・要求にない名前の残存、スキップしたフォルダの更新、通知の欠落・重複・ロック保持中の通知。
    /// </summary>
    [TestMethod]
    public void Organize_UpdatesTheHeldResultByTheOutcomeOfEachFolder()
    {
        Run(ArrangeFourFolders, rig =>
        {
            rig.Library.SearchAudioDuplicateFolders(null, CancellationToken.None);
            Dictionary<string, AudioDuplicateFolder> before = ByFolder(rig);
            Assert.AreEqual(4, before.Count);
            var changes = new List<string[]>();
            rig.Library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != "AudioDuplicateFolders") { return; }
                changes.Add([.. GateNames.SelectMany(name => InstallServiceComponentTestSupport.CaptureLibraryGateHoldsOnCurrentThread(rig.Library, name))]);
            };
            string failing = rig.At("A\\y.wav");
            rig.Mutations.FailDeleteFileShellWhen = path => string.Equals(path, failing, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(rig.At("C"), recursive: true);

            AudioDuplicateOrganizeResult result = rig.Organize(
                rig.Request(("A", ".wav", ["x", "y"]), ("B", ".wav", ["p"]), ("C", ".wav", ["q"]), ("D", ".wav", ["r"])));
            Assert.IsFalse(result.Canceled);

            Assert.AreEqual(AudioDuplicateOrganizeStatus.PartiallyCompleted, result.Folders[0].Status);
            Assert.AreEqual(AudioDuplicateOrganizeStatus.Completed, result.Folders[1].Status);
            Assert.AreEqual(AudioDuplicateOrganizeStatus.Skipped, result.Folders[2].Status);
            Assert.AreEqual(AudioDuplicateOrganizeStatus.Completed, result.Folders[3].Status);
            Dictionary<string, AudioDuplicateFolder> after = ByFolder(rig);
            CollectionAssert.AreEquivalent(new[] { "A", "C" }, after.Keys.ToArray());
            CollectionAssert.AreEqual(new[] { "y" }, after["A"].CollisionNames.ToArray(), "送れなかった名前だけ。要求にない z も除く。");
            Assert.AreNotSame(before["A"].Group, after["A"].Group);
            Assert.AreSame(before["C"].Group, after["C"].Group, "スキップは同じインスタンス。");
            Assert.AreSame(before["C"], after["C"]);
            Assert.IsTrue(rig.Library.HasCurrentAudioDuplicateFolders);
            Assert.AreEqual(1, changes.Count);
            Assert.AreEqual(0, changes[0].Length, string.Join("、", changes[0]));
        });
    }

    /// <summary>
    /// 保証：未処理（取消）のフォルダは同じインスタンスのまま残り、送れなかった名前が探索時点の衝突名の集合と同じ（大文字小文字を無視）一部完了のフォルダは同じインスタンスのまま、
    /// 要求の名前が0件の完了のフォルダは結果から除く。変更があった場合だけ通知する。
    /// 検出する誤り：集合が変わらないフォルダの新しいインスタンス化、0件の完了のフォルダの残存、未処理のフォルダの更新。
    /// </summary>
    [TestMethod]
    public void Organize_KeepsInstancesOfUnchangedFoldersAndRemovesCompletedFoldersWithoutNames()
    {
        Run(ArrangeFourFolders, rig =>
        {
            rig.Library.SearchAudioDuplicateFolders(null, CancellationToken.None);
            Dictionary<string, AudioDuplicateFolder> before = ByFolder(rig);
            var changes = new List<string>();
            rig.Library.PropertyChanged += (_, args) => changes.Add(args.PropertyName ?? string.Empty);
            string failing = rig.At("B\\p.wav");
            rig.Mutations.FailDeleteFileShellWhen = path => string.Equals(path, failing, StringComparison.OrdinalIgnoreCase);
            using var cancellation = new CancellationTokenSource();

            AudioDuplicateOrganizeResult result = rig.Organize(
                rig.Request(("B", ".wav", ["P"]), ("A", ".wav", []), ("C", ".wav", ["q"]), ("D", ".wav", ["r"])),
                value =>
                {
                    if (value.Number == 2) { cancellation.Cancel(); }
                },
                cancellation.Token);

            Assert.IsTrue(result.Canceled);
            Assert.AreEqual(AudioDuplicateOrganizeStatus.PartiallyCompleted, result.Folders[0].Status);
            Assert.AreEqual(AudioDuplicateOrganizeStatus.Completed, result.Folders[1].Status);
            Assert.AreEqual(AudioDuplicateOrganizeStatus.Unprocessed, result.Folders[2].Status);
            Dictionary<string, AudioDuplicateFolder> after = ByFolder(rig);
            CollectionAssert.AreEquivalent(new[] { "B", "C", "D" }, after.Keys.ToArray(), "要求の名前が0件の完了のフォルダは除く。");
            Assert.AreSame(before["B"], after["B"], "送れなかった名前の集合が変わらなければ同じインスタンス。");
            Assert.AreSame(before["C"], after["C"], "未処理は同じインスタンス。");
            Assert.AreSame(before["D"], after["D"]);
            CollectionAssert.AreEqual(new[] { "AudioDuplicateFolders" }, changes, "フォルダが除かれた一回だけ通知する。");
        });
    }

    /// <summary>
    /// 保証：変更がなければ結果も通知も変わらない（送れなかった名前だけのフォルダが探索時点と同じ集合）。結果がない場合は結果を作らず、
    /// リソース索引の世代が探索時点と異なる場合（失効扱い）は保持している結果の一覧を更新しない。いずれも整理自体は行い、通知しない。
    /// 検出する誤り：結果がない状態での結果の生成、世代の違う結果の更新、変更がないのに通知する。
    /// </summary>
    [TestMethod]
    public void Organize_DoesNotUpdateWithoutAResultOrWhenTheGenerationDiffers()
    {
        Run(ArrangeFourFolders, rig =>
        {
            var changes = new List<string>();
            rig.Library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == "AudioDuplicateFolders") { changes.Add(args.PropertyName); }
            };

            AudioDuplicateOrganizeResult none = rig.Organize(rig.Request(("B", ".wav", ["p"])));

            Assert.AreEqual(1, none.Folders[0].TrashedCount);
            Assert.IsNull(rig.Library.AudioDuplicateFolders);
            Assert.AreEqual(0, changes.Count);

            rig.Library.SearchAudioDuplicateFolders(null, CancellationToken.None);
            List<AudioDuplicateFolder> searched = rig.Library.AudioDuplicateFolders!;
            changes.Clear();
            SetIndex(rig.Library);
            Assert.IsFalse(rig.Library.HasCurrentAudioDuplicateFolders);

            AudioDuplicateOrganizeResult stale = rig.Organize(rig.Request(("C", ".wav", ["q"])));

            Assert.AreEqual(1, stale.Folders[0].TrashedCount);
            Assert.AreSame(searched, rig.Library.AudioDuplicateFolders, "世代が違う結果は更新しない。");
            Assert.AreEqual(0, changes.Count);
        });
    }
}
