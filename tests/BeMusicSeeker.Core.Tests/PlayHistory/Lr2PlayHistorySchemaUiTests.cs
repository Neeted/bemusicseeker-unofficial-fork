using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class Lr2PlayHistorySchemaUiTests
{

    /// <summary>
    /// LR2標準のplayer DB位置を組み立て、実在時だけ返す。未設定・不在・設定読取失敗を存在するDBへ置き換えない。
    /// </summary>
    [TestMethod]
    public void Lr2ScoreDbPathResolver_ResolvesExistingPlayerScoreDbOnly()
    {
        using var caseWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directoryPath = Path.Combine(caseWorkspace.RootDirectory, "BeMusicSeeker_Lr2ScoreDbPathResolver_" + Guid.NewGuid().ToString("N"));
        try
        {
            string scoreDirectoryPath = Path.Combine(directoryPath, "LR2files", "Database", "Score");
            Directory.CreateDirectory(scoreDirectoryPath);
            string scoreDbPath = Path.Combine(scoreDirectoryPath, "player1.db");
            File.WriteAllText(scoreDbPath, string.Empty);

            Assert.AreEqual(scoreDbPath, Lr2ScoreDbPathResolver.BuildPlayerScoreDbPath(directoryPath, "player1"));
            Assert.AreEqual(Path.Combine(scoreDirectoryPath, "missing.db"), Lr2ScoreDbPathResolver.BuildPlayerScoreDbPath(directoryPath, "missing"));
            Assert.AreEqual(scoreDbPath, Lr2ScoreDbPathResolver.ResolvePlayerScoreDbPath(directoryPath, "player1"));
            Assert.IsNull(Lr2ScoreDbPathResolver.ResolvePlayerScoreDbPath(directoryPath, "missing"));
            Assert.IsNull(Lr2ScoreDbPathResolver.ResolvePlayerScoreDbPath(directoryPath, (string)null!));
            Assert.IsNull(Lr2ScoreDbPathResolver.ResolvePlayerScoreDbPath(directoryPath, () => throw new InvalidOperationException("config")));
        }
        finally
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 未導入だけ導入、修復可能だけ修復を許可し、導入済み/手動修復/読取不能/対象外は両操作を禁止して状態文言を渡す。
    /// </summary>
    [TestMethod]
    public void Lr2PlayHistorySchemaStatusPresentation_MapsActionsAndLabels()
    {
        Lr2PlayHistorySchemaStatusPresentation notInstalled = CreatePresentation(Lr2PlayHistorySchemaStatus.NotInstalled);
        Assert.IsTrue(notInstalled.CanInstall);
        Assert.IsFalse(notInstalled.CanRepair);
        Assert.AreEqual(Resources.Lr2_play_history_schema_status_not_installed, notInstalled.StatusText);

        Lr2PlayHistorySchemaStatusPresentation repairable = CreatePresentation(Lr2PlayHistorySchemaStatus.Repairable);
        Assert.IsFalse(repairable.CanInstall);
        Assert.IsTrue(repairable.CanRepair);
        Assert.AreEqual(Resources.Lr2_play_history_schema_status_repairable, repairable.StatusText);

        foreach (Lr2PlayHistorySchemaStatus status in new[]
        {
            Lr2PlayHistorySchemaStatus.Installed,
            Lr2PlayHistorySchemaStatus.ManualRepairRequired,
            Lr2PlayHistorySchemaStatus.Unreadable,
            Lr2PlayHistorySchemaStatus.SkippedProfile
        })
        {
            Lr2PlayHistorySchemaStatusPresentation presentation = CreatePresentation(status);
            Assert.IsFalse(presentation.CanInstall, status.ToString());
            Assert.IsFalse(presentation.CanRepair, status.ToString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(presentation.StatusText), status.ToString());
        }

        const string diagnostic = "SQLite diagnostic: sample";
        foreach ((Lr2PlayHistorySchemaReason reason, string expected) in new[]
        {
            (Lr2PlayHistorySchemaReason.PathNotConfigured, Resources.Lr2_play_history_schema_message_score_db_path_not_configured),
            (Lr2PlayHistorySchemaReason.FileNotFound, Resources.Lr2_play_history_schema_message_score_db_file_not_found),
            (Lr2PlayHistorySchemaReason.Diagnostic, diagnostic),
            (Lr2PlayHistorySchemaReason.InstallOrRepairFailed, string.Format(Resources.Lr2_play_history_schema_message_install_or_repair_failed_format, diagnostic)),
            (Lr2PlayHistorySchemaReason.UninstallFailed, string.Format(Resources.Lr2_play_history_schema_message_uninstall_failed_format, diagnostic))
        })
        {
            var snapshot = Lr2PlayHistorySchemaStatusSnapshot.FromResult(new Lr2PlayHistorySchemaCheckResult
            {
                Status = Lr2PlayHistorySchemaStatus.Unreadable,
                ScoreDbPath = "score.db",
                Reason = reason,
                Diagnostic = diagnostic
            });
            var presentation = Lr2PlayHistorySchemaStatusPresentation.Create(snapshot);
            Assert.AreEqual(reason, snapshot.Reason);
            Assert.AreEqual(diagnostic, snapshot.Diagnostic);
            Assert.AreEqual("score.db", snapshot.ScoreDbPath);
            Assert.AreEqual(expected, presentation.Message);
            Assert.IsFalse(presentation.CanInstall);
            Assert.IsFalse(presentation.CanRepair);
        }
    }

    private static Lr2PlayHistorySchemaStatusPresentation CreatePresentation(Lr2PlayHistorySchemaStatus status)
    {
        return Lr2PlayHistorySchemaStatusPresentation.Create(
            Lr2PlayHistorySchemaStatusSnapshot.FromResult(new Lr2PlayHistorySchemaCheckResult
            {
                Status = status,
                Reason = Lr2PlayHistorySchemaReason.Diagnostic,
                Diagnostic = status.ToString()
            }));
    }

}
