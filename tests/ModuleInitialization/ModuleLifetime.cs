using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Tests.Infrastructure;

/// <summary>直接MTP実行でもケースの専有領域を準備します。</summary>
[TestClass]
public sealed class ModuleLifetime
{
    /// <summary>テストケースの開始前に一回だけ実行領域を確定します。</summary>
    [AssemblyInitialize]
    public static void Initialize(TestContext context) => _ = TestRunDirectory.RootDirectory;

    /// <summary>
    /// 画面を起動せず、全ケースの終端後に共有設定fixtureの専有領域を回収します。
    /// 窓口を明示しないケースで起きた予期しないライブラリの投函は、呼出し元が窓口の失敗を診断へ捕捉して見えないため、ここで失敗にします。
    /// </summary>
    [AssemblyCleanup]
    public static void Cleanup()
    {
        TestSettingsFactory.CleanupIsolatedSettingsDirectories();
        IReadOnlyList<string> unexpectedPosts = UnexpectedBmsLibraryDialogService.TakeUnexpectedPosts();
        if (unexpectedPosts.Count > 0)
        {
            throw new InvalidOperationException(
                "窓口を明示しないケースで、予期しないライブラリの投函が " + unexpectedPosts.Count + " 件ありました。"
                + Environment.NewLine + string.Join(Environment.NewLine, unexpectedPosts));
        }
    }
}
