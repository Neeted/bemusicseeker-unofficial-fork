using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeMusicSeeker.Models.Update;
using Microsoft.Win32;


namespace BeMusicSeeker.TestSupport.Components.Update;

/// <summary>更新ケースの既存入力・registry・managed呼出しを一箇所で保持します。</summary>
internal static class UpdaterPackageTestSupport
{
    /// <summary>再起動引数の受け渡しで順序・引用・空白・末尾の逆スラッシュの欠落を見分けるための難集合です。</summary>
    internal static readonly string[] HardRestartArguments =
    [
        "a b",
        "",
        "\"quoted\"",
        "C:\\dir with space\\",
        "back\\\\\"slash",
        "%PATH%",
        "--app-dir",
        "--update-manifest-url=http://127.0.0.1:1/u.json?x=1&y=2",
        "  lead/trail  ",
        "tab\there",
        "日本語 フォルダ",
        "𝄞"
    ];

    /// <summary>一時物回収の実障害を再起動前後で観測する小さな実packageを作成します。</summary>
    internal static (string AppDirectory, string PackagePath, string BackupDirectory) PrepareCleanupFailurePackage(string tempDirectory)
    {
        string appDirectory = Path.Combine(tempDirectory, "app");
        string sourceDirectory = Path.Combine(tempDirectory, "package-source");
        string packagePath = Path.Combine(appDirectory, "update_work", "downloads", "package.zip");
        string backupDirectory = Path.Combine(appDirectory, "update_backup");
        string managedPaths = string.Join(Environment.NewLine, new[] { "BeMusicSeeker.exe", "restart.exe", "restart.json" });
        WriteTextFile(appDirectory, "BeMusicSeeker.exe", "old-app");
        CopyRestartExecutable(appDirectory, "old.marker", "old");
        WriteTextFile(appDirectory, "update-managed-files.txt", managedPaths);
        WriteTextFile(sourceDirectory, "BeMusicSeeker.exe", "new-app");
        CopyRestartExecutable(sourceDirectory, "restart.marker", "restarted");
        WriteTextFile(sourceDirectory, "update-managed-files.txt", managedPaths);
        ZipFile.CreateFromDirectory(sourceDirectory, packagePath);
        return (appDirectory, packagePath, backupDirectory);
    }

    /// <summary>本番の再起動直前まで到達した後に、元例外同一性を検査する固定故障を発生させます。</summary>
    internal static Process ThrowRestartLaunchFailure()
    {
        throw new Win32Exception(5, "Injected restart launch failure.");
    }

    /// <summary>診断先だけを故障させ、更新や復元の実処理は置換しません。</summary>
    internal sealed class ThrowingDiagnosticWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        /// <summary>任意診断の故障境界へ到達した回数です。</summary>
        internal int WriteAttempts { get; private set; }

        public override void WriteLine(string? value)
        {
            WriteAttempts++;
            throw new IOException("Injected stderr failure.");
        }
    }

    /// <summary>本番のatomic marker公開でproceedまたはcancelを渡します。</summary>
    internal static void PublishUpdaterDecision(string decisionFilePath, string decision)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(decision);
        UpdateMarkerPublisher.Publish(decisionFilePath, overwrite: true, stream => stream.Write(bytes, 0, bytes.Length));
    }

    /// <summary>全process終端後に再起動先が生成したmarkerの存在を確認します。</summary>
    internal static void AssertRestartFileExists(string filePath)
    {
        Assert.IsTrue(File.Exists(filePath), "Expected file was not created by the completed restart process: " + filePath);
    }

    /// <summary>case固有のアプリパスを含む回復登録だけを列挙します。</summary>
    internal static IReadOnlyList<string> GetRecoveryRunOnceValues(string appDirectoryPath)
    {
        using RegistryKey? runOnce = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
            writable: false);
        if (runOnce == null)
        {
            return Array.Empty<string>();
        }

        return runOnce.GetValueNames()
            .Where(valueName => valueName.StartsWith("!BeMusicSeeker.UpdateRecovery-", StringComparison.Ordinal)
                || valueName.StartsWith("BeMusicSeeker.UpdateRecovery-", StringComparison.Ordinal))
            .Where(valueName => (runOnce.GetValue(valueName) as string)?.Contains(
                appDirectoryPath,
                StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
    }

    /// <summary>全所有process終端後に、case固有のRunOnceと監視登録だけを解除します。</summary>
    internal static void DeleteRecoveryRunOnceValues(string appDirectoryPath)
    {
        using RegistryKey? runOnce = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
            writable: true);
        if (runOnce != null)
        {
            foreach (string valueName in GetRecoveryRunOnceValues(appDirectoryPath))
            {
                runOnce.DeleteValue(valueName, throwOnMissingValue: false);
            }

            runOnce.Flush();
        }

        using RegistryKey? run = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            writable: true);
        if (run == null)
        {
            return;
        }

        foreach (string valueName in run.GetValueNames()
            .Where(valueName => valueName.StartsWith("BeMusicSeeker.UpdateRecoverySupervisor-", StringComparison.Ordinal))
            .Where(valueName => (run.GetValue(valueName) as string)?.Contains(
                appDirectoryPath,
                StringComparison.OrdinalIgnoreCase) == true)
            .ToArray())
        {
            run.DeleteValue(valueName, throwOnMissingValue: false);
        }

        run.Flush();
    }

    /// <summary>case固有のアプリパスを含む永続監視登録だけを列挙します。</summary>
    internal static IReadOnlyList<string> GetRecoverySupervisorValues(string appDirectoryPath)
    {
        using RegistryKey? run = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            writable: false);
        if (run == null)
        {
            return Array.Empty<string>();
        }

        return run.GetValueNames()
            .Where(valueName => valueName.StartsWith("BeMusicSeeker.UpdateRecoverySupervisor-", StringComparison.Ordinal))
            .Where(valueName => (run.GetValue(valueName) as string)?.Contains(
                appDirectoryPath,
                StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
    }

    /// <summary>指定したcase専有領域へ、故障条件または期待入力の元bytesを配置します。</summary>
    internal static void WriteTextFile(string rootDirectoryPath, string relativePath, string contents)
    {
        string filePath = Path.Combine(rootDirectoryPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string? directoryPath = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        File.WriteAllText(filePath, contents);
    }

    /// <summary>管理対象側のfile内容と空directoryを含めて更新前後の同一性を照合します。</summary>
    internal static string ComputeCanonicalTreeHash(string appDirectoryPath)
    {
        return ComputeTreeHash(appDirectoryPath, includePreservedTopLevel: false);
    }

    /// <summary>保護対象のconfigとdataの内容・集合を更新前後で照合します。</summary>
    internal static string ComputePreservedTreeHash(string appDirectoryPath)
    {
        return ComputeTreeHash(appDirectoryPath, includePreservedTopLevel: true);
    }

    /// <summary>元の保護領域分類でfile内容とdirectory集合の順序に依存しないhashを作ります。</summary>
    internal static string ComputeTreeHash(string rootDirectoryPath, bool includePreservedTopLevel)
    {
        string[] preservedNames = ["config", "data", "log", "logs", "update_backup", "update_work"];
        var entries = new List<string>();
        foreach (string path in Directory.EnumerateFileSystemEntries(rootDirectoryPath, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(rootDirectoryPath, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            string topLevelName = relativePath.Split('/')[0];
            bool isPreserved = preservedNames.Contains(topLevelName, StringComparer.OrdinalIgnoreCase);
            if (isPreserved != includePreservedTopLevel
                || (includePreservedTopLevel
                    && !string.Equals(topLevelName, "config", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(topLevelName, "data", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (Directory.Exists(path))
            {
                entries.Add("D|" + relativePath);
            }
            else
            {
                entries.Add(
                    "F|"
                    + relativePath
                    + "|"
                    + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            }
        }

        entries.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", entries))));
    }

    /// <summary>本番と同じGUI起動先を配置し、marker内容や生存gateの入力をその版とともに移動させます。</summary>
    /// <param name="rootDirectoryPath">アプリ、packageまたはbackupの配置先です。</param>
    /// <param name="markerFile">起動した版が生成するmarker名です。nullなら生成しません。</param>
    /// <param name="markerContents">版ごとのmarker内容です。</param>
    /// <param name="waitForRelease">準備通知と標準入力のreleaseで生存期間を制御する場合にtrueです。</param>
    /// <param name="argumentsFile">指定すると、再起動先がMainの引数をJSON文字列配列としてこの名前で記録します。</param>
    internal static void CopyRestartExecutable(string rootDirectoryPath, string? markerFile = null, string? markerContents = null, bool waitForRelease = false, string? argumentsFile = null)
    {
        Directory.CreateDirectory(rootDirectoryPath);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "updater-restart-application", "UpdaterRestartApplication.exe"),
            Path.Combine(rootDirectoryPath, "restart.exe"),
            overwrite: true);
        WriteTextFile(rootDirectoryPath, "restart.json", JsonSerializer.Serialize(new
        {
            WaitForRelease = waitForRelease,
            MarkerFile = markerFile,
            MarkerContents = markerContents,
            ArgumentsFile = argumentsFile
        }));
    }

    /// <summary>明示したmanaged updaterと終了済PIDから実要求を作り、本番journal準備を呼びます。</summary>
    internal static (Type Program, object Request) PrepareManagedUpdate(string assemblyPath, int exitedProcessId, string appDirectoryPath, string packagePath, string backupDirectoryPath)
    {
        // この呼出し単位の再起動失敗境界はmanaged buildを使う。
        // 実配布exeの供給とは分離した明示build fixtureだけを読み込む。
        var assembly = Assembly.LoadFrom(assemblyPath);
        Type program = assembly.GetType("BeMusicSeeker.Updater.Program", throwOnError: true)!;
        Type requestType = program.GetNestedType("UpdateRequest", BindingFlags.NonPublic)!;
        string restartPath = Path.Combine(appDirectoryPath, "restart.exe");
        string[] arguments =
        [
            "--app-dir", appDirectoryPath,
            "--package", packagePath,
            "--backup-dir", backupDirectoryPath,
            "--restart-exe", restartPath,
            "--pid", exitedProcessId.ToString(),
            "--ready-file", Path.Combine(appDirectoryPath, "update_work", "current", "updater-ready.txt"),
            "--decision-file", Path.Combine(appDirectoryPath, "update_work", "current", "updater-decision.txt")
        ];
        object request = requestType.GetMethod("Parse")!.Invoke(null, [arguments])!;
        // ケース所有のアプリ領域へ本番の準備処理を通し、journal生成を複製しない。
        program.GetMethod("WritePreparedTransactionJournal", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [request]);
        program.GetMethod("PrepareFullTransactionJournal", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [request]);

        return (program, request);
    }

    /// <summary>旧互換と公開updaterが共用する固定CLI要求です。PIDの意味と供給はcase側に残します。</summary>
    /// <param name="restartArgumentsJson"><c>--restart-arguments-json</c>へ渡す生の値です。nullなら省略します。</param>
    internal static ProcessStartInfo CreateUpdaterStartInfo(
        string updaterPath,
        string appDirectoryPath,
        string packagePath,
        string backupDirectoryPath,
        string? restartExecutablePath,
        string processId,
        string? restartArgumentsJson = null)
    {
        restartExecutablePath ??= Path.Combine(appDirectoryPath, "restart.exe");
        string readyFilePath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-ready.txt");
        string decisionFilePath = Path.Combine(appDirectoryPath, "update_work", "current", "updater-decision.txt");
        var startInfo = new ProcessStartInfo
        {
            FileName = updaterPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(updaterPath) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add("--app-dir");
        startInfo.ArgumentList.Add(appDirectoryPath);
        startInfo.ArgumentList.Add("--package");
        startInfo.ArgumentList.Add(packagePath);
        startInfo.ArgumentList.Add("--backup-dir");
        startInfo.ArgumentList.Add(backupDirectoryPath);
        startInfo.ArgumentList.Add("--ready-file");
        startInfo.ArgumentList.Add(readyFilePath);
        startInfo.ArgumentList.Add("--decision-file");
        startInfo.ArgumentList.Add(decisionFilePath);
        startInfo.ArgumentList.Add("--pid");
        startInfo.ArgumentList.Add(processId);
        startInfo.ArgumentList.Add("--restart-exe");
        startInfo.ArgumentList.Add(restartExecutablePath);
        if (restartArgumentsJson is not null)
        {
            startInfo.ArgumentList.Add("--restart-arguments-json");
            startInfo.ArgumentList.Add(restartArgumentsJson);
        }
        return startInfo;
    }

    /// <summary>本番適用後の固定再起動失敗と、成功した巻き戻しが元例外同一性を保つことを照合します。</summary>
    internal static void AssertRestartFailure(Type program, object request, string restartPath)
    {
        bool restartAttempted = false;
        var startFailure = new Win32Exception(5, "Injected restart launch failure.");
        Func<ProcessStartInfo, Process> startApplication = startInfo =>
        {
            Assert.AreEqual(restartPath, startInfo.FileName);
            restartAttempted = true;
            throw startFailure;
        };
        TargetInvocationException failure = Assert.ThrowsExactly<TargetInvocationException>(() =>
            program.GetMethod("ApplyUpdate", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [request, startApplication]));
        Assert.IsTrue(restartAttempted, "The test must reach restart after applying the package, not fail during preflight.");
        Assert.AreSame(startFailure, failure.InnerException, "Successful rollback must preserve the original launch failure.");
    }
}
