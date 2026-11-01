using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Tests.Infrastructure;

/// <summary>直接MTP実行でもケースの専有領域を準備します。</summary>
[TestClass]
public sealed class ModuleLifetime
{
    /// <summary>テストケースの開始前に一回だけ実行領域を確定します。</summary>
    [AssemblyInitialize]
    public static void Initialize(TestContext context) => _ = TestRunDirectory.RootDirectory;
}
