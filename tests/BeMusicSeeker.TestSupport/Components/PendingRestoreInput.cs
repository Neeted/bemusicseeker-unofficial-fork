using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Files;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryInitializationTestSupport;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>登録ルート外に正常・読取不能・正常不存在を持つ専用入力です。通常一時領域の全体回収に依存しません。</summary>
internal sealed class PendingRestoreInput : IDisposable
{
    private readonly TestWorkspace workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
    private string root => workspace.RootDirectory;
    internal string A => Path.Combine(root, "A", "a.bms");
    internal string B => Path.Combine(root, "B", "b.bms");
    internal string Missing => Path.Combine(root, "Missing");
    internal PendingRestoreInput()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(A) ?? throw new InvalidOperationException());
            Directory.CreateDirectory(Path.GetDirectoryName(B) ?? throw new InvalidOperationException());
            File.WriteAllText(A, Chart("Original A")); File.WriteAllText(B, Chart("Original B"));
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }
    internal static string Chart(string title) => "#TITLE " + title + "\n#BPM 120\n#00111:01\n";
    internal FileStream LockB() => new(B, FileMode.Open, FileAccess.Read, FileShare.None);
    internal void Register(ApplicationDatabaseScope databaseScope, string database) => ExecuteSongDbFixtureTransaction(databaseScope, database, db =>
    {
        db.CreateTable<LR2SongDBExtended.install>();
        db.InsertOrReplace(new LR2SongDBExtended.install { path = Path.GetDirectoryName(A) ?? throw new InvalidOperationException(), delete_parent = false });
        db.InsertOrReplace(new LR2SongDBExtended.install { path = Path.GetDirectoryName(B) ?? throw new InvalidOperationException(), delete_parent = true });
        db.InsertOrReplace(new LR2SongDBExtended.install { path = Missing, delete_parent = false });
    });
    internal void BlockReset(ApplicationDatabaseScope databaseScope, string database) => ExecuteSongDbFixtureTransaction(databaseScope, database, db =>
        db.Execute("CREATE TRIGGER pending_reset_fail AFTER DELETE ON install WHEN (SELECT COUNT(*) FROM install) = 0 BEGIN SELECT RAISE(ABORT, 'pending-reset-failure'); END"));
    public void Dispose() => workspace.Dispose();
}
