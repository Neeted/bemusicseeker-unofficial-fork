using System.Diagnostics;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Components.Update;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Desktop.E2E.Tests.Fixtures;

/// <summary>実アプリの更新受入ケースが共有する、起動の成功・配信の要求・process残留の判定です。</summary>
internal static class UpdateScenarioAssertions
{
    /// <summary>製品の初期化が成功し、完了が公開され、失敗がないかを返します。</summary>
    internal static bool IsStartupSuccess(ScenarioInitializationResult startup)
        => startup.Outcome == StartupInitializationOutcome.Succeeded && startup.CompletionPublished
            && startup.IsInitializationCompleted && startup.Failure is null;

    /// <summary>製品の初期化が成功していることを確認します。</summary>
    internal static void AssertStartupSucceeded(ScenarioInitializationResult startup)
        => Assert.IsTrue(IsStartupSuccess(startup), $"実製品の初期化が成功していません: {startup}");

    /// <summary>配信の1要求が、指定したpath・世代のGETで、200で応答したことを確認します。</summary>
    internal static void AssertRequest(LoopbackUpdateFeed.FeedRequest request, string path, int generation = 1)
    {
        Assert.AreEqual("GET", request.Method);
        Assert.AreEqual(path, request.Path);
        Assert.AreEqual(200, request.Status);
        Assert.AreEqual(generation, request.Generation);
    }

    /// <summary>この配布コピーの実行ファイルから起動したprocessが残っていないことを確認します。</summary>
    internal static void AssertNoResidue(string appRoot)
    {
        var residue = new List<string>();
        foreach (string name in new[] { "BeMusicSeeker", "BeMusicSeeker.Updater" })
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        string? path = process.MainModule?.FileName;
                        if (path is not null && path.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase)) { residue.Add($"{process.Id}:{path}"); }
                    }
                    catch (System.ComponentModel.Win32Exception) { /* 終了済みまたは参照できないprocessは、この配布コピーの残留と判定できません。 */ }
                    catch (InvalidOperationException) { /* 列挙中に終了したprocessです。 */ }
                }
            }
        }
        Assert.AreEqual(0, residue.Count, "配布コピーのprocessが残留しています: " + string.Join(", ", residue));
    }
}
