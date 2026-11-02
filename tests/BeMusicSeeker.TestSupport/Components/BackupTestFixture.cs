using System.Globalization;
using System.Text;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>Coreのバックアップケースと旧環境依存ケースで、同じ媒体入力・世代の観測・回収順を共用します。</summary>
internal static class BackupTestFixture
{

    /// <summary>専有workspace内にバックアップ先と元DBの固定入力を作り、同期の検証後に作成したtreeを回収します。</summary>
    /// <param name="workspace">ケースの呼出元が全体の寿命とDisposeを所有する領域。このhelperはその配下に作る一つのtreeだけを回収します。</param>
    /// <param name="test">バックアップ先と元DBパスを受ける同期callback。独自に開始したTaskやVM・DB接続は、媒体を回収する前に呼出元が終端・解放します。</param>
    /// <param name="cleanupTree">falseなら内部treeの回収をworkspace所有者へ委ね、未終端時のPreserveArtifactsを尊重します。</param>
    /// <remarks>
    /// backupsディレクトリの作成後、元DB入力の書込みとcallbackをtry/finallyで囲みます。
    /// 既定のfinallyはtree内の全ファイル属性をNormalへ戻してからtreeを再帰削除します。workspace全体のDisposeは呼出元に残ります。
    /// 入力準備・callbackの失敗は伝播します。属性変更・削除が失敗した場合も伝播し、そのfinally例外が先行する失敗を置き換える通常の例外順序を保ちます。
    /// </remarks>
    internal static void WithBackupTree(TestWorkspace workspace, Action<string, string> test, bool cleanupTree = true)
    {
        string tree = Path.Combine(workspace.RootDirectory, "BackupTests", Guid.NewGuid().ToString("N"));
        string root = Path.Combine(tree, "backups");
        Directory.CreateDirectory(root);
        string source = Path.Combine(tree, "song.db");
        try
        {
            WriteAllText(source, "source-content");
            test(root, source);
        }
        finally
        {
            if (cleanupTree)
            {
                foreach (string file in Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(tree, recursive: true);
            }
        }
    }

    /// <summary>選択バックアップ用の設定XML・曲DB・スコアDBを同じtreeへ準備し、旧環境依存ケースとCoreケースで共用します。</summary>
    /// <param name="workspace">呼出元が全体の寿命とDisposeを所有する領域。配下treeの回収はWithBackupTreeへ委ねます。</param>
    /// <param name="test">バックアップ先・設定XML・曲DB・スコアディレクトリを順に受ける同期callback。独自TaskやVM・DB接続の終端・解放は呼出元が担います。</param>
    /// <param name="cleanupTree">falseなら内部treeの回収をworkspace所有者へ委ね、未終端時のPreserveArtifactsを尊重します。</param>
    /// <remarks>
    /// 固定内容の書込みとcallbackは<see cref="WithBackupTree"/>の回収境界内で実行します。
    /// 準備・callbackの失敗とfinallyの属性復元・tree削除の失敗は、その同じ伝播順序を保ちます。
    /// helperはworkspace全体をDisposeしません。cleanupTree=falseではtreeも残し、呼出元がケース終端時にまとめて回収します。
    /// </remarks>
    internal static void WithSelectedBackupTree(TestWorkspace workspace, Action<string, string, string, string> test, bool cleanupTree = true)
    {
        WithBackupTree(workspace, (root, songDbPath) =>
        {
            string tree = Path.GetDirectoryName(songDbPath)!;
            string configPath = Path.Combine(tree, "config.xml");
            string scoreDirectoryPath = Path.Combine(tree, "Score");
            Directory.CreateDirectory(scoreDirectoryPath);
            WriteAllText(configPath, "config-content");
            WriteAllText(songDbPath, "song-content");
            WriteAllText(Path.Combine(scoreDirectoryPath, "score.db"), "score-content");
            test(root, configPath, songDbPath, scoreDirectoryPath);
        }, cleanupTree);
    }

    /// <summary>日付名の既存世代4件へ識別用DBを作り、保存対象外の世代が保たれることを観測する入力を揃えます。</summary>
    /// <param name="root">呼出元が回収を所有するバックアップ先。</param>
    /// <returns>作成した世代ディレクトリのパス。媒体I/Oの失敗は伝播し、作成済み領域の回収はrootの所有者へ残ります。</returns>
    internal static string[] CreateOldGenerations(string root)
    {
        string[] generations = new string[4];
        for (int index = 0; index < generations.Length; index++)
        {
            string path = Path.Combine(root, DateTime.Today.AddDays(-index - 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(path);
            WriteAllText(Path.Combine(path, "sentinel.db"), "old-content");
            generations[index] = path;
        }
        return generations;
    }

    /// <summary>準備済み世代の存在と識別内容を照合し、バックアップ操作による既存世代の変更を検出します。</summary>
    /// <param name="generations">CreateOldGenerationsで準備し、呼出元が回収を所有する世代パス。表明と読取りの失敗はそのまま伝播します。</param>
    internal static void AssertOldGenerations(string[] generations)
    {
        foreach (string generation in generations)
        {
            Assert.IsTrue(Directory.Exists(generation), generation);
            Assert.AreEqual("old-content", ReadAllText(Path.Combine(generation, "sentinel.db")));
        }
    }

    /// <summary>バックアップ先のローカル実行日をyyyy-MM-ddで表す格納先を返し、準備と結果観測の日付形式を共用します。</summary>
    /// <param name="root">呼出元が所有するバックアップ先。</param>
    /// <returns>当日のパス。ディレクトリの作成や回収は行いません。</returns>
    internal static string TodayPath(string root) => Path.Combine(root, DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    /// <summary>長いパス対応の同じI/O境界からUTF-8指定で内容を読み、バックアップ入力と結果を照合します。</summary>
    /// <param name="path">呼出元が寿命を所有するファイル。</param>
    /// <returns>読取り内容。readerとstreamは返却前に破棄し、読取り・破棄の失敗は伝播します。</returns>
    internal static string ReadAllText(string path)
    {
        using FileStream stream = LongPathFileSystem.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>長いパス対応の同じI/O境界でUTF-8指定の固定入力を書き、既存ファイルは全体を書き換えます。</summary>
    /// <param name="path">呼出元が寿命と削除を所有する入力ファイル。</param>
    /// <param name="contents">書き込む固定内容。writerとstreamは復帰前に破棄し、書込み・破棄の失敗は伝播します。</param>
    internal static void WriteAllText(string path, string contents)
    {
        using FileStream stream = LongPathFileSystem.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(contents);
    }
}
