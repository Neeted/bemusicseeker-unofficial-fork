using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MainWindowViewModelAppSchemaRepairTests
{
    /// <summary>実旧DBの非同期検査・保留中の確認・取消を通り、schemaと保存行を変えず終了結果だけを返します。実Closeと受付解放はWindow接続の代表ケースへ分担します。</summary>
    [DataTestMethod]
    [DataRow(MessageBoxResult.Cancel)]
    [DataRow(MessageBoxResult.None)]
    public async Task PrepareSchemaAsync_CancelPreservesDatabaseAndReturnsExit(MessageBoxResult choice)
    {
        using var scope = Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        var gateway = new BmsLibraryDbGateway(scope.SongDbPath);
        gateway.EnsureLibraryStartupSchema();
        BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(scope.SongDbPath,
            db => db.Execute("UPDATE app_schema_version SET version = 0 WHERE name = 'app_schema'"));
        using var gate = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(gate);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new MainWindowViewModelStartupProgressTests.CompletionDialogs
        { ConfirmationObservedAsync = _ => { entered.TrySetResult(); return release.Task; } };
        string[] before;
        using (LR2SongDBExtended db = gateway.OpenSongDbReadOnly()) { before = db.Query<SchemaRow>("SELECT sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY name").Select(row => row.sql).ToArray(); }
        Task<(AppSchemaPreflightResult Approval, bool ExitRequested)> operation = owner.PrepareSchemaAsync(
            new StartupSettingsSnapshot { LR2SongDBPath = scope.SongDbPath }, dialogs, () => false);
        try
        {
            await Task.WhenAny(entered.Task, operation);
            Assert.IsTrue(entered.Task.IsCompleted); Assert.IsFalse(operation.IsCompleted);
            release.TrySetResult(UiDialogResult.FromMessageBoxResult(choice));
            (AppSchemaPreflightResult Approval, bool ExitRequested) result = await operation;
            Assert.IsTrue(result.ExitRequested); Assert.IsNull(result.Approval);
            using LR2SongDBExtended db = gateway.OpenSongDbReadOnly();
            CollectionAssert.AreEqual(before, db.Query<SchemaRow>("SELECT sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY name").Select(row => row.sql).ToArray());
            Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT version FROM app_schema_version WHERE name = 'app_schema'"));
        }
        finally { release.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)); await operation; }
    }

    /// <summary>実DBの承認後だけ適用へ進み、確認不要の初期準備と修復のみの不足は確認を増やしません。承認した同一セッションの再準備も確認を重ねません。</summary>
    [DataTestMethod]
    [DataRow("approval")]
    [DataRow("repair-only")]
    [DataRow("normal")]
    public async Task PrepareSchemaAsync_ApprovedOrUnwarnedPreparationCanApply(string scenario)
    {
        using var scope = Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        var gateway = new BmsLibraryDbGateway(scope.SongDbPath);
        if (scenario != "normal") { gateway.EnsureLibraryStartupSchema(); }
        if (scenario == "approval")
        { BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(scope.SongDbPath, db => db.Execute("UPDATE app_schema_version SET version = 0 WHERE name = 'app_schema'")); }
        if (scenario == "repair-only")
        { BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(scope.SongDbPath, db => db.Execute("DROP TABLE IF EXISTS " + SQLiteTable<LR2SongDBExtended.bmson_song>.GetTableName())); }
        using var gate = new SemaphoreSlim(1, 1);
        var owner = new StartupLibraryInitializationWorkflowOwner(gate);
        var settings = new StartupSettingsSnapshot { LR2SongDBPath = scope.SongDbPath };
        var dialogs = new MainWindowViewModelStartupProgressTests.CompletionDialogs { ConfirmationResult = MessageBoxResult.OK };
        (AppSchemaPreflightResult Approval, bool ExitRequested) prepared = await owner.PrepareSchemaAsync(settings, dialogs, () => false);
        Assert.IsFalse(prepared.ExitRequested); Assert.IsNotNull(prepared.Approval);
        (AppSchemaPreflightResult Approval, bool ExitRequested) repeated = await owner.PrepareSchemaAsync(settings, dialogs, () => false);
        Assert.IsFalse(repeated.ExitRequested);
        Assert.AreEqual(scenario == "approval" ? 1 : 0, dialogs.ConfirmationCount);
        await owner.ApplyPreparedSchemaAsync(settings, prepared.Approval);
        AppSchemaPreflightResult after = new AppSchemaPreflightService().Inspect(scope.SongDbPath);
        Assert.IsFalse(after.RepairRequired); Assert.IsFalse(after.NeedsAppSchemaVersionRepair);
    }

    /// <summary>共通の構造更新案内を表示資源から取得し、内部識別子を利用者へ露出しません。</summary>
    [TestMethod]
    public void BuildAppSchemaRepairWarningMessage_UsesRepairConfirmationResource()
    {
        var result = new AppSchemaPreflightResult(true, true, true, true, RepairableBmsonSchemaIssues.BmsonSongTableMissing);
        Assert.AreEqual(Resources.AppSchemaRepairWarningMessage, StartupLibraryInitializationWorkflowOwner.BuildAppSchemaRepairWarningMessage(result));
    }

    private sealed class SchemaRow { public string sql { get; set; } = string.Empty; }
}
