using System.IO;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Tests;

/// <remarks>専有workspaceへCWDを移して相対入力を準備します。parser自身の前後ではCWDを保持し、fixture回収失敗でもfinallyで元CWDへ戻してからworkspaceを解放します。</remarks>
[TestClass]
[DoNotParallelize]
public sealed class ApplicationRuntimeOptionsParserTests
{
    /// <summary>ログoptionと同じ先頭を持つ相対manifestは値として消費し、ログ指定へ再解釈しません。前Errorのみ・指定なし・後Warningを分担し、ログ結果と全引数の保持を共有ログ状態を変えずに確認します。</summary>
    [TestMethod]
    [DataRow("--test-session", "Error", null, NormalLogLevel.Error)]
    [DataRow("--TeSt-SeSsIoN", null, null, NormalLogLevel.Info)]
    [DataRow("--TEST-SESSION", "Error", "warning", NormalLogLevel.Warn)]
    public void ManifestValueWithLogOptionPrefixDoesNotOverrideCapturedLogging(string sessionSwitch, string? beforeLevel, string? afterLevel, NormalLogLevel expectedLevel)
    {
        (NormalLogLevel LogLevel, bool IsInfoLoggingEnabled, bool HasInvalidLogLevelValue, string InvalidLogLevelValue) processLoggingBefore = (CommandLineSwitches.LogLevel, CommandLineSwitches.IsInfoLoggingEnabled,
            CommandLineSwitches.HasInvalidLogLevelValue, CommandLineSwitches.InvalidLogLevelValue);
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string originalDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = workspace.RootDirectory;
            string currentDirectory = Environment.CurrentDirectory;
            const string rootNamePrefix = "--log-level=case-";
            using var fixture = new RuntimeOptionsManifestFixture(currentDirectory, rootNamePrefix);
            fixture.Write(fixture.CreateDocument());
            string relativeManifest = Path.GetRelativePath(currentDirectory, fixture.ManifestPath);
            Assert.IsFalse(Path.IsPathFullyQualified(relativeManifest));
            StringAssert.StartsWith(relativeManifest, rootNamePrefix);
            Assert.IsTrue(File.Exists(relativeManifest), "実在する相対manifestを解析へ渡します。");
            var arguments = new List<string> { "unknown value" };
            if (beforeLevel is not null) { arguments.Add("--log-level=" + beforeLevel); }
            arguments.Add(sessionSwitch);
            int manifestArgumentIndex = arguments.Count;
            arguments.Add(relativeManifest);
            if (afterLevel is not null) { arguments.Add("--LOG-LEVEL=" + afterLevel); }
            string[] inputArguments = arguments.ToArray();
            string[] expectedRestartArguments = arguments.ToArray();
            expectedRestartArguments[manifestArgumentIndex] = fixture.ManifestPath;

            ApplicationRuntimeOptions options = ApplicationRuntimeOptionsParser.Capture(ExecutablePath, inputArguments);

            Assert.IsNotNull(options.TestSession);
            Assert.AreEqual(fixture.ManifestPath, options.TestSession.ManifestPath);
            Assert.AreEqual(expectedLevel, options.LogOptions.Level);
            Assert.AreEqual(string.Empty, options.LogOptions.InvalidValue);
            CollectionAssert.AreEqual(expectedRestartArguments, options.RestartArguments.ToArray());
            CollectionAssert.AreEqual(arguments, inputArguments, "呼出元の引数配列を書き換えません。");
            Assert.AreEqual(currentDirectory, Environment.CurrentDirectory);
            fixture.AssertOnlyManifestExists();
            Assert.AreEqual(processLoggingBefore, (CommandLineSwitches.LogLevel, CommandLineSwitches.IsInfoLoggingEnabled,
                CommandLineSwitches.HasInvalidLogLevelValue, CommandLineSwitches.InvalidLogLevelValue));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    /// <summary>更新URLのoptionと同じ先頭を持つ相対manifestも値として一度だけ消費し、そのパスだけを絶対化します。URLの前指定のみ・指定なし・前後指定を分担し、それぞれ前URL・既定値・後URLを返して、他の引数・元配列・CWDを保持します。</summary>
    [TestMethod]
    [DataRow("--test-session", "https://example.invalid/before.json", null, "https://example.invalid/before.json")]
    [DataRow("--TeSt-SeSsIoN", null, null, null)]
    [DataRow("--TEST-SESSION", "https://example.invalid/before.json", "https://example.invalid/after.json", "https://example.invalid/after.json")]
    public void TestSessionCanonicalizesManifestAndPreservesOtherRestartArguments(string sessionSwitch, string? beforeUrl, string? afterUrl, string? expectedUrl)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string originalDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = workspace.RootDirectory;
            string currentDirectory = Environment.CurrentDirectory;
            const string rootNamePrefix = "--update-manifest-url=case-";
            // 入力の相対prefixを保ち、専有workspaceの中でparser前後のCWDを比較します。
            using var fixture = new RuntimeOptionsManifestFixture(currentDirectory, rootNamePrefix);
            fixture.Write(fixture.CreateDocument());
            string relativeManifest = Path.GetRelativePath(currentDirectory, fixture.ManifestPath);
            Assert.IsFalse(Path.IsPathFullyQualified(relativeManifest));
            StringAssert.StartsWith(relativeManifest, rootNamePrefix);
            Assert.IsTrue(File.Exists(relativeManifest), "実在する相対manifestを解析へ渡します。");
            var arguments = new List<string> { "unknown value" };
            if (beforeUrl is not null) { arguments.Add("--update-manifest-url=" + beforeUrl); }
            arguments.Add(sessionSwitch);
            int manifestArgumentIndex = arguments.Count;
            arguments.Add(relativeManifest);
            if (afterUrl is not null) { arguments.Add("--UPDATE-MANIFEST-URL=" + afterUrl); }
            string[] inputArguments = arguments.ToArray();
            string[] expectedRestartArguments = arguments.ToArray();
            expectedRestartArguments[manifestArgumentIndex] = fixture.ManifestPath;

            ApplicationRuntimeOptions options = ApplicationRuntimeOptionsParser.Capture(ExecutablePath, inputArguments);

            Assert.IsNotNull(options.TestSession);
            Assert.AreEqual(fixture.ManifestPath, options.TestSession.ManifestPath);
            CollectionAssert.AreEqual(expectedRestartArguments, options.RestartArguments.ToArray());
            CollectionAssert.AreEqual(arguments, inputArguments, "呼出元の引数配列を書き換えません。");
            Assert.AreEqual(expectedUrl ?? UpdateCheckService.DefaultManifestUrl, options.UpdateManifestUrl);
            Assert.AreEqual(currentDirectory, Environment.CurrentDirectory);
            fixture.AssertOnlyManifestExists();
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }
    private static string ExecutablePath => Environment.ProcessPath ?? throw new InvalidOperationException("実行ファイルの位置を取得できません。");
}
