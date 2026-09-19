using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace BeMusicSeeker.Updater;

internal static partial class Program
{
    internal const int ProtocolVersion = 1;

    private static readonly HashSet<string> PreservedTopLevelNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "config",
        "data",
        "log",
        "logs",
        "update_backup",
        "update_work"
    };

    private const string ManagedFilesManifestName = "update-managed-files.txt";
    private const string UpdateFailureReceiptFileName = "update-failure.txt";
    private const string UpdateFailureReceiptTemporaryFileName = "update-failure.txt.tmp";
    private const string TransactionJournalFileName = "update-transaction.json";
    private const string TransactionJournalTemporaryFileName = "update-transaction.json.tmp";
    private const string TransactionLeaseFileName = "update-transaction.lock";
    private const string RollbackStagingDirectoryName = "rollback-staging";
    private const string RollbackOriginalDirectoryPrefix = "rollback-original-";
    private const string RecoveryRunOnceSubKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string RecoveryRunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // The leading ! keeps the value present until the recovery command exits. Recovery
    // re-arms the handoff before it mutates anything and clears it only after success.
    private const string RecoveryRunOnceValuePrefix = "!BeMusicSeeker.UpdateRecovery-";
    private const string LegacyRecoveryRunOnceValuePrefix = "BeMusicSeeker.UpdateRecovery-";
    private const string RecoverySupervisorValuePrefix = "BeMusicSeeker.UpdateRecoverySupervisor-";
    private const string UpdaterReadyFileName = "updater-ready.txt";
    private const string UpdaterDecisionFileName = "updater-decision.txt";
    private const int DecisionWaitMilliseconds = 300000;
    private const int WatchdogApplicationExitWaitMilliseconds = 60000;
    private const string ImportedMetadataDirectoryName = "imported_metadata";
    private const string MetadataDbFileName = "chart-info-metadata.db";
    private const string MetadataArchiveFileName = "chart-info-metadata.7z";

    private static class TransactionPhase
    {
        internal const string Prepared = "prepared";
        internal const string BackingUp = "backing-up";
        internal const string Applying = "applying";
        internal const string Applied = "applied";
        internal const string RolledBack = "rolled-back";
        internal const string Restarting = "restarting";
        internal const string Committed = "committed";
    }

    private static readonly string[] LegacyManagedFilePaths =
    [
        "libs/Bass.Net.dll",
        "libs/DynamicJson.dll",
        "libs/IniLibrary.dll",
        "libs/Livet.dll",
        "libs/Livet.Extensions.dll",
        "libs/MetroRadiance.Chrome.dll",
        "libs/MetroRadiance.Core.dll",
        "libs/MetroRadiance.dll",
        "libs/Microsoft.Expression.Drawing.dll",
        "libs/Microsoft.Expression.Effects.dll",
        "libs/Microsoft.Expression.Interactions.dll",
        "libs/Microsoft.WindowsAPICodePack.dll",
        "libs/Microsoft.WindowsAPICodePack.Shell.dll",
        "libs/Newtonsoft.Json.dll",
        "libs/NLog.Database.dll",
        "libs/NLog.dll",
        "libs/NLog.WindowsEventLog.dll",
        "libs/OggVorbis.NET64.dll",
        "libs/QuickConverter.dll",
        "libs/SgmlReaderDll.dll",
        "libs/SevenZipExtractor.dll",
        "libs/System.Collections.Immutable.dll",
        "libs/System.Resources.Extensions.dll",
        "libs/System.Memory.dll",
        "libs/System.Buffers.dll",
        "libs/System.Numerics.Vectors.dll",
        "libs/System.Runtime.CompilerServices.Unsafe.dll",
        "libs/System.Windows.Interactivity.dll",
        "libs/sqlite.net.dll",
        "libs/x64/OggVorbis.NET64.dll",
        "libs/x64/sqlite3.dll",
        "runtimes/win-x64/native/e_sqlite3.dll",
        "x64/sqlite3.dll",
        "x86/sqlite3.dll",
        "x86/7z.dll",
        "x86/bass.dll",
        "x86/bass_fx.dll",
        "x86/bassasio.dll",
        "x86/bassenc.dll",
        "x86/bassmix.dll",
        "x86/basswasapi.dll",
        "libs/x86/sqlite3.dll",
        "libs/x86/7z.dll",
        "libs/x86/bass.dll",
        "libs/x86/bass_fx.dll",
        "libs/x86/bassasio.dll",
        "libs/x86/bassenc.dll",
        "libs/x86/bassmix.dll",
        "libs/x86/basswasapi.dll",
        "x64/OggVorbis.NET64.dll",
        "x64/7z.dll",
        "x64/bass.dll",
        "x64/bass_fx.dll",
        "x64/bassasio.dll",
        "x64/bassenc.dll",
        "x64/bassmix.dll",
        "x64/basswasapi.dll",
        "BeMusicSeeker.exe.config",
        "SevenZipExtractor.dll",
        "OggVorbis.NET.dll",
        "OggVorbis.NET64.dll"
    ];

    private static int Main(string[] args)
    {
        if (args.Length == 1 && string.Equals(args[0], "--version", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(ProtocolVersion);
            return 0;
        }

        if (args.Length > 0 && string.Equals(args[0], "--recover", StringComparison.OrdinalIgnoreCase))
        {
            return RecoverFromCommandLine(args);
        }

        if (args.Length > 0 && string.Equals(args[0], "--watch", StringComparison.OrdinalIgnoreCase))
        {
            return WatchTransactionFromCommandLine(args);
        }

        UpdateRequest request = null;
        bool transactionLeaseAcquired = false;
        try
        {
            request = UpdateRequest.Parse(args);
            using TransactionLease lease = AcquireTransactionLease(request.AppDirectory);
            transactionLeaseAcquired = true;
            DeferRecoveryIfApplicationIsRunning(request.AppDirectory);
            RecoverIncompleteTransaction(NormalizeExistingDirectory(request.AppDirectory));
            ClearRecoveryStartup(request.AppDirectory);
            RegisterRecoveryStartup(request.AppDirectory);
            WritePreparedTransactionJournal(request);
            StartTransactionWatchdog(request.AppDirectory);
            PublishReadyHandshake(request);
            if (!WaitForLaunchDecision(request))
            {
                DiscardPreparedTransactionJournal(request.AppDirectory);
                ClearRecoveryStartup(request.AppDirectory);
                return 0;
            }
            PrepareFullTransactionJournal(request);
            ApplyUpdate(request, Process.Start);
            return 0;
        }
        catch (TransactionRecoveryDeferredException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (Exception ex)
        {
            if (request != null)
            {
                if (transactionLeaseAcquired)
                {
                    TryDiscardPreparedTransactionJournal(request.AppDirectory);
                    TryWriteFailureReceipt(request, ex);
                }
            }
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int RecoverFromCommandLine(string[] args)
    {
        string appDirectory = null;
        try
        {
            appDirectory = RecoveryRequest.Parse(args).AppDirectory;
            while (true)
            {
                try
                {
                    using TransactionLease lease = AcquireTransactionLease(appDirectory);
                    return RecoverFromCommandLineWithLease(appDirectory);
                }
                catch (TransactionLeaseUnavailableException)
                {
                    // Run and RunOnce may invoke the same recovery command at logon.
                    // Wait for the owner to finish instead of rearming after it has
                    // already cleared the handoff.
                    Thread.Sleep(100);
                }
            }
        }
        catch (Exception exception)
        {
            TryWriteFailureReceipt(appDirectory, exception);
            TryRearmRecoveryStartup(appDirectory);
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int RecoverFromCommandLineWithLease(string appDirectory)
    {
        string normalizedAppDirectory = NormalizeExistingDirectory(appDirectory);
        RegisterRecoveryStartup(normalizedAppDirectory);
        string journalPath = Path.Combine(normalizedAppDirectory, "update_work", TransactionJournalFileName);
        TransactionJournalRecord journal = null;
        if (UpdaterFileSystem.FileExists(journalPath))
        {
            EnsureNoReparsePointIfPresent(journalPath);
            journal = ReadTransactionJournal(journalPath);
            ValidateTransactionJournal(journal, normalizedAppDirectory);
        }

        // The recorded application PID is only a shutdown hint. It is not a
        // process identity across an OS restart, so use the executable path when
        // deciding whether a live application still owns the tree.
        bool applicationStillRunning = journal != null
            && IsProcessRunningForExecutable(journal.RestartExecutablePath);
        bool restartApplication = journal != null
            && !applicationStillRunning
            && !string.Equals(journal.Phase, TransactionPhase.Committed, StringComparison.Ordinal)
            && !(string.Equals(journal.Phase, TransactionPhase.Restarting, StringComparison.Ordinal)
                && (journal.RestartProcessId > 0
                    || IsProcessRunningForExecutable(journal.RestartExecutablePath)));
        if (applicationStillRunning
            && journal != null
            && !string.Equals(journal.Phase, TransactionPhase.Committed, StringComparison.Ordinal))
        {
            // Startup cleanup must never mutate a live application tree. Leave the
            // journal and handoff in place for the watchdog or a later logon.
            TryRearmRecoveryStartup(normalizedAppDirectory);
            return 2;
        }
        RecoverIncompleteTransaction(normalizedAppDirectory, retainRolledBackJournal: true);
        if (restartApplication)
        {
            StartRecoveredApplication(journal);
        }
        CleanupRolledBackJournalIfPresent(normalizedAppDirectory);
        ClearRecoveryStartup(normalizedAppDirectory);
        return 0;
    }

    private static TransactionLease AcquireTransactionLease(string appDirectory)
    {
        string normalizedAppDirectory = NormalizeExistingDirectory(appDirectory);
        EnsureNoReparsePointAncestors(normalizedAppDirectory, "update_work/update-transaction.lock");
        string workDirectory = Path.Combine(normalizedAppDirectory, "update_work");
        EnsureNoReparsePointIfPresent(workDirectory);
        UpdaterFileSystem.CreateDirectory(workDirectory);
        string leasePath = Path.Combine(workDirectory, TransactionLeaseFileName);
        EnsureNoReparsePointIfPresent(leasePath);
        FileStream leaseStream;
        try
        {
            leaseStream = UpdaterFileSystem.Open(
                leasePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException exception)
        {
            throw new TransactionLeaseUnavailableException(
                "Another updater transaction currently owns the application update lease.",
                exception);
        }
        try
        {
            leaseStream.SetLength(0);
            byte[] marker = Encoding.UTF8.GetBytes(Environment.ProcessId.ToString());
            leaseStream.Write(marker, 0, marker.Length);
            leaseStream.Flush(flushToDisk: true);
            return new TransactionLease(leaseStream);
        }
        catch
        {
            leaseStream.Dispose();
            throw;
        }
    }

    // Keep process creation at the edge of the transaction so restart-failure
    // tests can exercise rollback without asking Windows to run an invalid EXE.
    private static void ApplyUpdate(UpdateRequest request, Func<ProcessStartInfo, Process> startApplication)
    {
        try
        {
            WaitForApplicationExit(request.ProcessId);
        }
        catch (Exception exception)
        {
            TryWriteFailureReceipt(request, exception);
            throw;
        }

        try
        {
            ApplyUpdateAfterApplicationExit(request, startApplication);
        }
        catch (RollbackFailureException exception)
        {
            // Do not restart when the updater could not prove that the previous
            // application state was fully restored.
            TryWriteFailureReceipt(request, exception);
            throw;
        }
        catch (Exception exception)
        {
            TryDiscardPreparedTransactionJournal(request.AppDirectory);
            TryWriteFailureReceipt(request, exception);
            throw;
        }
    }

    private static void PublishReadyHandshake(UpdateRequest request)
    {
        string appDirectory = NormalizeExistingDirectory(request.AppDirectory);
        string readyFilePath = NormalizeFilePath(request.ReadyFilePath);
        string expectedReadyFilePath = NormalizeFilePath(Path.Combine(appDirectory, "update_work", "current", UpdaterReadyFileName));
        if (!string.Equals(readyFilePath, expectedReadyFilePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ready-file must be the dedicated updater-ready.txt file under update_work/current.");
        }
        EnsureNoReparsePointAncestors(appDirectory, "update_work/current/updater-ready.txt");
        EnsureNoReparsePointIfPresent(readyFilePath);
        byte[] marker = Encoding.UTF8.GetBytes(ProtocolVersion.ToString());
        using FileStream stream = UpdaterFileSystem.Open(readyFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(marker, 0, marker.Length);
        stream.Flush(flushToDisk: true);
    }

    private static bool WaitForLaunchDecision(UpdateRequest request)
    {
        try
        {
            string appDirectory = NormalizeExistingDirectory(request.AppDirectory);
            string decisionFilePath = NormalizeFilePath(request.DecisionFilePath);
            string expectedDecisionFilePath = NormalizeFilePath(Path.Combine(appDirectory, "update_work", "current", UpdaterDecisionFileName));
            if (!string.Equals(decisionFilePath, expectedDecisionFilePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("decision-file must be the dedicated updater-decision.txt file under update_work/current.");
            }
            EnsureNoReparsePointAncestors(appDirectory, "update_work/current/updater-decision.txt");
            EnsureNoReparsePointIfPresent(decisionFilePath);

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(DecisionWaitMilliseconds);
            while (true)
            {
                if (UpdaterFileSystem.FileExists(decisionFilePath))
                {
                    string decision;
                    using (FileStream stream = UpdaterFileSystem.OpenRead(decisionFilePath))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        decision = reader.ReadToEnd().Trim();
                    }
                    UpdaterFileSystem.DeleteFile(decisionFilePath);
                    if (string.Equals(decision, "proceed", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                    if (string.Equals(decision, "cancel", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    throw new InvalidOperationException("The updater launch decision was not recognized: " + decision);
                }

                if (DateTime.UtcNow >= deadline)
                {
                    if (!IsProcessRunning(request.ProcessId))
                    {
                        throw new TimeoutException("The application exited before publishing an updater launch decision.");
                    }
                    // Keep the decision channel alive while the still-running application
                    // drains its shutdown work; the updater must not start its 60-second
                    // process-exit window before explicit approval.
                    deadline = DateTime.UtcNow.AddMilliseconds(DecisionWaitMilliseconds);
                }
                Thread.Sleep(50);
            }
        }
        catch (Exception exception)
        {
            TryWriteFailureReceipt(request, exception);
            throw;
        }
    }

    private static void ApplyUpdateAfterApplicationExit(UpdateRequest request, Func<ProcessStartInfo, Process> startApplication)
    {
        string appDirectory = NormalizeExistingDirectory(request.AppDirectory);
        string preparedJournalPath = Path.Combine(appDirectory, "update_work", TransactionJournalFileName);
        TransactionJournalRecord journal = ReadTransactionJournal(preparedJournalPath);
        ValidateTransactionJournal(journal, appDirectory);
        if (!string.Equals(journal.Phase, TransactionPhase.Prepared, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The updater did not find its prepared transaction journal before applying the package.");
        }

        string packagePath = NormalizeExistingFile(request.PackagePath);
        string backupDirectory = NormalizeDirectoryPath(request.BackupDirectory);
        string restartExePath = NormalizeExistingFile(request.RestartExePath);
        EnsurePathUnderDirectory(appDirectory, restartExePath, "restart executable");

        string expectedBackupDirectory = NormalizeDirectoryPath(Path.Combine(appDirectory, "update_backup"));
        if (!string.Equals(backupDirectory, expectedBackupDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("backup-dir must be the dedicated update_backup directory under app-dir.");
        }

        string workDirectory = Path.Combine(appDirectory, "update_work");
        string extractDirectory = Path.Combine(workDirectory, "extracted");
        EnsurePackagePathUnderDownloads(appDirectory, packagePath);
        EnsureNoReparsePointAncestors(appDirectory, "update_work/extracted");
        if (UpdaterFileSystem.DirectoryExists(extractDirectory))
        {
            EnsureNoReparsePoint(extractDirectory);
            UpdaterFileSystem.DeleteDirectory(extractDirectory, recursive: true);
        }
        UpdaterFileSystem.CreateDirectory(extractDirectory);

        ValidatePackage(packagePath);
        ExtractPackage(packagePath, extractDirectory);
        EnsureNoPreservedTopLevelEntries(extractDirectory);
        string restartRelativePath = GetRelativePath(appDirectory, restartExePath);
        if (!EnumerateRelativePackagePaths(extractDirectory).Contains(restartRelativePath, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The restart executable must be included in the updated package: " + restartRelativePath);
        }

        ValidateExtractedPackageBeforeBackupRotation(extractDirectory, appDirectory);
        HashSet<string> newPackagePaths = EnumerateRelativePackagePaths(extractDirectory);
        string previousDirectory = Path.Combine(backupDirectory, "previous");
        journal.PackagePath = packagePath;
        journal.BackupDirectory = backupDirectory;
        journal.PreviousDirectory = previousDirectory;
        journal.ExtractDirectory = extractDirectory;
        journal.RestartExecutablePath = restartExePath;
        journal.NewPackagePaths = [.. newPackagePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        bool restartStarted = false;
        var appliedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var createdDirectories = new List<string>();
        bool applicationMutationStarted = false;
        bool backupRotationStarted = false;
        try
        {
            journal.Phase = TransactionPhase.BackingUp;
            WriteTransactionJournal(journal);
            backupRotationStarted = true;
            if (UpdaterFileSystem.DirectoryExists(backupDirectory))
            {
                EnsureNoReparsePoint(backupDirectory);
                UpdaterFileSystem.DeleteDirectory(backupDirectory, recursive: true);
            }
            EnsureNoReparsePointAncestors(appDirectory, "update_backup/previous");
            UpdaterFileSystem.CreateDirectory(backupDirectory);
            UpdaterFileSystem.CreateDirectory(previousDirectory);

            HashSet<string> previousManagedPaths = PrepareTransactionBackup(
                appDirectory,
                previousDirectory,
                newPackagePaths);
            journal.BackupComplete = true;
            journal.Phase = TransactionPhase.Applying;
            WriteTransactionJournal(journal);
            applicationMutationStarted = true;
            ApplyExtractedPackage(
                extractDirectory,
                appDirectory,
                previousManagedPaths,
                newPackagePaths,
                appliedPaths,
                createdDirectories);
            if (!UpdaterFileSystem.FileExists(restartExePath))
            {
                throw new FileNotFoundException("The restart executable was not included in the updated application.", restartExePath);
            }

            journal.Phase = TransactionPhase.Applied;
            WriteTransactionJournal(journal);

            try
            {
                SafeDeleteFile(packagePath);
                SafeDeleteDirectory(extractDirectory);
            }
            catch (Exception cleanupException)
            {
                Console.Error.WriteLine("The update was applied, but temporary cleanup before restart failed; the restarted application will retry it: " + cleanupException);
            }

            journal.Phase = TransactionPhase.Restarting;
            journal.RestartProcessId = -1;
            WriteTransactionJournal(journal);
            Process restartProcess = startApplication(new ProcessStartInfo(restartExePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(restartExePath) ?? appDirectory
            });
            if (restartProcess == null)
            {
                throw new InvalidOperationException("The updated application could not be restarted.");
            }
            restartStarted = true;
            journal.RestartProcessId = restartProcess.Id;
            try
            {
                journal.Phase = TransactionPhase.Restarting;
                WriteTransactionJournal(journal);
                journal.Phase = TransactionPhase.Committed;
                WriteTransactionJournal(journal);
                TryCleanupCommittedTransactionArtifacts(journal);
            }
            catch (Exception cleanupException)
            {
                Console.Error.WriteLine("The update restarted successfully, but durable transaction cleanup will be retried on the next updater startup: " + cleanupException);
            }
        }
        catch (Exception updateException)
        {
            if (!restartStarted)
            {
                try
                {
                    if (applicationMutationStarted)
                    {
                        TryRollback(appDirectory, previousDirectory, appliedPaths, createdDirectories);
                        MarkTransactionRolledBack(journal);
                    }
                    TryCleanupFailedUpdateArtifacts(packagePath, extractDirectory);
                    if (backupRotationStarted)
                    {
                        SafeDeleteDirectory(backupDirectory);
                    }
                    DeleteTransactionJournal(journal);
                }
                catch (Exception rollbackException)
                {
                    throw new RollbackFailureException(updateException, rollbackException);
                }
            }
            throw;
        }
    }

    private static void WaitForApplicationExit(int processId)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), "The application process id must be positive.");
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit(60000);
            if (!process.HasExited)
            {
                throw new TimeoutException("Application process did not exit within 60 seconds.");
            }
        }
        catch (ArgumentException)
        {
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void StartTransactionWatchdog(string appDirectory)
    {
        string updaterPath = NormalizeExistingFile(Environment.ProcessPath);
        ProcessStartInfo startInfo = new(updaterPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(updaterPath) ?? appDirectory
        };
        startInfo.ArgumentList.Add("--watch");
        startInfo.ArgumentList.Add("--app-dir");
        startInfo.ArgumentList.Add(NormalizeExistingDirectory(appDirectory));
        startInfo.ArgumentList.Add("--pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

        var watchdog = Process.Start(startInfo);
        if (watchdog == null)
        {
            throw new InvalidOperationException("The updater transaction watchdog could not be started.");
        }
        watchdog.Dispose();
    }

    private static void RegisterRecoveryStartup(string appDirectory)
    {
        string normalizedAppDirectory = NormalizeExistingDirectory(appDirectory);
        string updaterPath = NormalizeExistingFile(Environment.ProcessPath);
        string command = QuoteWindowsArgument(updaterPath)
            + " --recover --app-dir "
            + QuoteWindowsArgument(normalizedAppDirectory);
        using RegistryKey runOnce = Registry.CurrentUser.CreateSubKey(RecoveryRunOnceSubKey, writable: true)
            ?? throw new InvalidOperationException("The updater recovery RunOnce key could not be opened.");
        DeleteRecoveryRunOnceGenerations(runOnce, normalizedAppDirectory);
        runOnce.SetValue(
            GetRecoveryRunOnceValueName(normalizedAppDirectory)
                + "-"
                + Guid.NewGuid().ToString("N"),
            command,
            RegistryValueKind.String);
        runOnce.Flush();

        using RegistryKey run = Registry.CurrentUser.CreateSubKey(RecoveryRunSubKey, writable: true)
            ?? throw new InvalidOperationException("The updater recovery supervisor Run key could not be opened.");
        run.SetValue(
            GetRecoverySupervisorValueName(normalizedAppDirectory),
            command,
            RegistryValueKind.String);
        run.Flush();
    }

    private static void TryRearmRecoveryStartup(string appDirectory)
    {
        if (string.IsNullOrWhiteSpace(appDirectory))
        {
            return;
        }

        try
        {
            RegisterRecoveryStartup(appDirectory);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("The updater could not re-arm its recovery RunOnce handoff: " + exception);
        }
    }

    private static void ClearRecoveryStartup(string appDirectory)
    {
        if (string.IsNullOrWhiteSpace(appDirectory))
        {
            return;
        }

        try
        {
            string normalizedAppDirectory = UpdaterFileSystem.NormalizePath(appDirectory);
            using RegistryKey runOnce = Registry.CurrentUser.OpenSubKey(RecoveryRunOnceSubKey, writable: true);
            if (runOnce != null)
            {
                DeleteRecoveryRunOnceGenerations(runOnce, normalizedAppDirectory);
                runOnce.Flush();
            }

            using RegistryKey run = Registry.CurrentUser.OpenSubKey(RecoveryRunSubKey, writable: true);
            if (run != null)
            {
                foreach (string valueName in run.GetValueNames())
                {
                    object value = run.GetValue(valueName, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (IsRecoverySupervisorValueForApp(valueName, value, normalizedAppDirectory))
                    {
                        run.DeleteValue(valueName, throwOnMissingValue: false);
                    }
                }
                run.Flush();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("The updater could not clear its recovery RunOnce handoff: " + exception);
        }
    }

    private static string GetRecoveryRunOnceValueName(string appDirectory)
    {
        return RecoveryRunOnceValuePrefix + GetRecoveryRunOnceHash(appDirectory);
    }

    private static string GetRecoverySupervisorValueName(string appDirectory)
    {
        return RecoverySupervisorValuePrefix + GetRecoveryRunOnceHash(appDirectory);
    }

    private static bool IsRecoveryRunOnceValueForApp(string valueName, object value, string normalizedAppDirectory)
    {
        string recoveryHash = GetRecoveryRunOnceHash(normalizedAppDirectory);
        string currentBaseName = RecoveryRunOnceValuePrefix + recoveryHash;
        string legacyBaseName = LegacyRecoveryRunOnceValuePrefix + recoveryHash;
        bool hasKnownPrefix = string.Equals(valueName, currentBaseName, StringComparison.Ordinal)
            || valueName.StartsWith(currentBaseName + "-", StringComparison.Ordinal)
            || string.Equals(valueName, legacyBaseName, StringComparison.Ordinal)
            || valueName.StartsWith(legacyBaseName + "-", StringComparison.Ordinal);
        return hasKnownPrefix
            && value is string command
            && command.Contains(normalizedAppDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteRecoveryRunOnceGenerations(RegistryKey runOnce, string normalizedAppDirectory)
    {
        foreach (string valueName in runOnce.GetValueNames())
        {
            object value = runOnce.GetValue(valueName, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (IsRecoveryRunOnceValueForApp(valueName, value, normalizedAppDirectory))
            {
                runOnce.DeleteValue(valueName, throwOnMissingValue: false);
            }
        }
    }

    private static string GetRecoveryRunOnceHash(string appDirectory)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(UpdaterFileSystem.NormalizePath(appDirectory)));
        return Convert.ToHexString(digest.AsSpan(0, 8));
    }

    private static bool IsRecoverySupervisorValueForApp(string valueName, object value, string normalizedAppDirectory)
    {
        return string.Equals(
                valueName,
                GetRecoverySupervisorValueName(normalizedAppDirectory),
                StringComparison.Ordinal)
            && value is string command
            && command.Contains(normalizedAppDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteWindowsArgument(string value)
    {
        value ??= string.Empty;
        if (value.Length == 0)
        {
            return "\"\"";
        }

        var builder = new StringBuilder();
        builder.Append('"');
        int backslashCount = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashCount * 2 + 1);
                builder.Append('"');
                backslashCount = 0;
                continue;
            }

            builder.Append('\\', backslashCount);
            backslashCount = 0;
            builder.Append(character);
        }

        builder.Append('\\', backslashCount * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static void WritePreparedTransactionJournal(UpdateRequest request)
    {
        string appDirectory = NormalizeExistingDirectory(request.AppDirectory);
        string backupDirectory = NormalizeDirectoryPath(request.BackupDirectory);
        var journal = new TransactionJournalRecord
        {
            Phase = TransactionPhase.Prepared,
            AppDirectory = appDirectory,
            PackagePath = NormalizeExistingFile(request.PackagePath),
            ApplicationProcessId = request.ProcessId,
            BackupDirectory = backupDirectory,
            PreviousDirectory = Path.Combine(backupDirectory, "previous"),
            ExtractDirectory = Path.Combine(appDirectory, "update_work", "extracted"),
            RestartExecutablePath = NormalizeExistingFile(request.RestartExePath),
            Preflight = true,
            NewPackagePaths = []
        };
        WriteTransactionJournal(journal);
    }

    private static void PrepareFullTransactionJournal(UpdateRequest request)
    {
        string appDirectory = NormalizeExistingDirectory(request.AppDirectory);
        string journalPath = Path.Combine(appDirectory, "update_work", TransactionJournalFileName);
        TransactionJournalRecord journal = ReadTransactionJournal(journalPath);
        ValidateTransactionJournal(journal, appDirectory);
        if (!journal.Preflight || !string.Equals(journal.Phase, TransactionPhase.Prepared, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The updater preflight transaction journal was not available before application shutdown.");
        }

        string packagePath = NormalizeExistingFile(request.PackagePath);
        string backupDirectory = NormalizeDirectoryPath(request.BackupDirectory);
        string restartExecutablePath = NormalizeExistingFile(request.RestartExePath);
        EnsurePathUnderDirectory(appDirectory, restartExecutablePath, "restart executable");
        journal.PackagePath = packagePath;
        journal.BackupDirectory = backupDirectory;
        journal.PreviousDirectory = Path.Combine(backupDirectory, "previous");
        journal.ExtractDirectory = Path.Combine(appDirectory, "update_work", "extracted");
        journal.RestartExecutablePath = restartExecutablePath;
        journal.Preflight = false;
        WriteTransactionJournal(journal);
    }

    private static void DiscardPreparedTransactionJournal(string appDirectory)
    {
        string normalizedAppDirectory = NormalizeExistingDirectory(appDirectory);
        string journalPath = Path.Combine(normalizedAppDirectory, "update_work", TransactionJournalFileName);
        EnsureNoReparsePointIfPresent(journalPath);
        if (!UpdaterFileSystem.FileExists(journalPath))
        {
            return;
        }

        TransactionJournalRecord journal = ReadTransactionJournal(journalPath);
        ValidateTransactionJournal(journal, normalizedAppDirectory);
        if (!string.Equals(journal.Phase, TransactionPhase.Prepared, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The updater cannot cancel a transaction after mutation has started.");
        }

        SafeDeleteDirectory(journal.ExtractDirectory);
        DeleteTransactionJournal(journal);
    }

    private static void TryDiscardPreparedTransactionJournal(string appDirectory)
    {
        try
        {
            string normalizedAppDirectory = NormalizeExistingDirectory(appDirectory);
            string journalPath = Path.Combine(normalizedAppDirectory, "update_work", TransactionJournalFileName);
            EnsureNoReparsePointIfPresent(journalPath);
            if (!UpdaterFileSystem.FileExists(journalPath))
            {
                ClearRecoveryStartup(normalizedAppDirectory);
                return;
            }

            TransactionJournalRecord journal = ReadTransactionJournal(journalPath);
            ValidateTransactionJournal(journal, normalizedAppDirectory);
            if (string.Equals(journal.Phase, TransactionPhase.Prepared, StringComparison.Ordinal))
            {
                SafeDeleteDirectory(journal.ExtractDirectory);
                DeleteTransactionJournal(journal);
                ClearRecoveryStartup(normalizedAppDirectory);
            }
        }
        catch (Exception cleanupException)
        {
            Console.Error.WriteLine("The updater could not discard its prepared transaction after failure: " + cleanupException);
        }
    }

    private static int WatchTransactionFromCommandLine(string[] args)
    {
        WatchdogRequest request = null;
        try
        {
            request = WatchdogRequest.Parse(args);
            WaitForParentExit(request.ProcessId);

            string appDirectory = NormalizeExistingDirectory(request.AppDirectory);
            while (true)
            {
                try
                {
                    using TransactionLease lease = AcquireTransactionLease(appDirectory);
                    RegisterRecoveryStartup(appDirectory);
                    string journalPath = Path.Combine(appDirectory, "update_work", TransactionJournalFileName);
                    EnsureNoReparsePointIfPresent(journalPath);
                    if (!UpdaterFileSystem.FileExists(journalPath))
                    {
                        ClearRecoveryStartup(appDirectory);
                        return 0;
                    }

                    TransactionJournalRecord journal = ReadTransactionJournal(journalPath);
                    ValidateTransactionJournal(journal, appDirectory);
                    if (string.Equals(journal.Phase, TransactionPhase.Prepared, StringComparison.Ordinal)
                        && IsProcessRunningForExecutable(journal.RestartExecutablePath))
                    {
                        DateTime deadline = DateTime.UtcNow.AddMilliseconds(WatchdogApplicationExitWaitMilliseconds);
                        while (IsProcessRunningForExecutable(journal.RestartExecutablePath)
                            && DateTime.UtcNow < deadline)
                        {
                            Thread.Sleep(100);
                        }

                        if (IsProcessRunningForExecutable(journal.RestartExecutablePath))
                        {
                            RecoverIncompleteTransaction(appDirectory, retainRolledBackJournal: true);
                            ClearRecoveryStartup(appDirectory);
                            return 0;
                        }
                    }

                    bool restartApplication = !string.Equals(journal.Phase, TransactionPhase.Committed, StringComparison.Ordinal)
                        && !(string.Equals(journal.Phase, TransactionPhase.Restarting, StringComparison.Ordinal)
                            && (journal.RestartProcessId > 0
                                || IsProcessRunningForExecutable(journal.RestartExecutablePath)));
                    RecoverIncompleteTransaction(appDirectory, retainRolledBackJournal: true);
                    if (restartApplication)
                    {
                        StartRecoveredApplication(journal);
                    }
                    CleanupRolledBackJournalIfPresent(appDirectory);
                    ClearRecoveryStartup(appDirectory);
                    return 0;
                }
                catch (TransactionLeaseUnavailableException)
                {
                    Thread.Sleep(100);
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            return 0;
        }
        catch (Exception exception)
        {
            TryWriteFailureReceipt(request?.AppDirectory, exception);
            TryRearmRecoveryStartup(request?.AppDirectory);
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void WaitForParentExit(int processId)
    {
        if (processId <= 0 || processId == Environment.ProcessId)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), "The watched updater process id must identify another process.");
        }

        while (IsProcessRunning(processId))
        {
            Thread.Sleep(100);
        }
    }

    private static void StartRecoveredApplication(TransactionJournalRecord journal)
    {
        string appDirectory = NormalizeExistingDirectory(journal.AppDirectory);
        string restartExecutablePath = NormalizeExistingFile(journal.RestartExecutablePath);
        EnsurePathUnderDirectory(appDirectory, restartExecutablePath, "restart executable");
        var restartProcess = Process.Start(new ProcessStartInfo(restartExecutablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(restartExecutablePath) ?? appDirectory
        });
        if (restartProcess == null)
        {
            throw new InvalidOperationException("The recovered application could not be restarted.");
        }
        restartProcess.Dispose();
    }

    private static bool IsProcessRunningForExecutable(string executablePath, int excludedProcessId = 0)
    {
        string normalizedExecutablePath = NormalizeExistingFile(executablePath);
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == Environment.ProcessId || process.Id == excludedProcessId)
                {
                    continue;
                }

                string processPath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(processPath)
                    && string.Equals(
                        UpdaterFileSystem.NormalizePath(processPath),
                        normalizedExecutablePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // Processes that exit or deny module inspection cannot prove that
                // the restart executable is alive.
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    private static void TryWriteFailureReceipt(UpdateRequest request, Exception exception)
    {
        if (request != null)
        {
            TryWriteFailureReceipt(request.AppDirectory, exception);
        }
    }

    private static void TryWriteFailureReceipt(string appDirectory, Exception exception)
    {
        if (string.IsNullOrWhiteSpace(appDirectory))
        {
            return;
        }

        string temporaryReceiptPath = null;
        try
        {
            appDirectory = NormalizeExistingDirectory(appDirectory);
            string workDirectory = Path.Combine(appDirectory, "update_work");
            string receiptPath = Path.Combine(workDirectory, UpdateFailureReceiptFileName);
            temporaryReceiptPath = Path.Combine(workDirectory, UpdateFailureReceiptTemporaryFileName);
            EnsureNoReparsePointAncestors(appDirectory, "update_work/" + UpdateFailureReceiptFileName);
            EnsureNoReparsePointIfPresent(workDirectory);
            UpdaterFileSystem.CreateDirectory(workDirectory);
            EnsureNoReparsePointIfPresent(receiptPath);
            EnsureNoReparsePointIfPresent(temporaryReceiptPath);
            string detailsText = exception?.ToString() ?? "The updater failed without exception details.";
            if (exception is RollbackFailureException
                && UpdaterFileSystem.FileExists(receiptPath))
            {
                try
                {
                    using FileStream existingReceiptStream = UpdaterFileSystem.OpenRead(receiptPath);
                    using var existingReceiptReader = new StreamReader(existingReceiptStream, Encoding.UTF8);
                    string existingDetails = existingReceiptReader.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(existingDetails)
                        && !detailsText.Contains(existingDetails, StringComparison.Ordinal))
                    {
                        detailsText = existingDetails.TrimEnd()
                            + Environment.NewLine
                            + "--- rollback failure ---"
                            + Environment.NewLine
                            + detailsText;
                    }
                }
                catch (Exception existingReceiptException)
                {
                    Console.Error.WriteLine("The updater could not read the previous failure receipt while retaining rollback diagnostics: " + existingReceiptException);
                }
            }

            byte[] details = Encoding.UTF8.GetBytes(detailsText);
            using (FileStream stream = UpdaterFileSystem.Open(temporaryReceiptPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(details, 0, details.Length);
                stream.Flush(flushToDisk: true);
            }
            UpdaterFileSystem.MoveFile(temporaryReceiptPath, receiptPath, overwrite: true);
            UpdaterFileSystem.FlushFile(receiptPath);
        }
        catch (Exception receiptException)
        {
            Console.Error.WriteLine("The updater could not atomically persist its failure receipt; the temporary receipt will be retried on the next startup: " + receiptException);
            bool temporaryReceiptExists = false;
            try
            {
                temporaryReceiptExists = temporaryReceiptPath != null && UpdaterFileSystem.FileExists(temporaryReceiptPath);
            }
            catch (Exception fallbackProbeException)
            {
                Console.Error.WriteLine("The updater could not inspect its temporary failure receipt fallback: " + fallbackProbeException);
            }
            if (!temporaryReceiptExists)
            {
                Console.Error.WriteLine("The updater has no durable failure receipt fallback: " + receiptException);
            }
        }
    }

    private static void TryCleanupFailedUpdateArtifacts(string packagePath, string extractDirectory)
    {
        try
        {
            SafeDeleteFile(packagePath);
            SafeDeleteDirectory(extractDirectory);
        }
        catch (Exception cleanupException)
        {
            Console.Error.WriteLine("The update was rolled back, but temporary cleanup failed; startup cleanup will retry it: " + cleanupException);
        }
    }

    private static void DeferRecoveryIfApplicationIsRunning(string appDirectory)
    {
        string normalizedAppDirectory = NormalizeExistingDirectory(appDirectory);
        string journalPath = Path.Combine(normalizedAppDirectory, "update_work", TransactionJournalFileName);
        EnsureNoReparsePointIfPresent(journalPath);
        if (!UpdaterFileSystem.FileExists(journalPath))
        {
            return;
        }

        TransactionJournalRecord journal = ReadTransactionJournal(journalPath);
        ValidateTransactionJournal(journal, normalizedAppDirectory);
        if (!string.Equals(journal.Phase, TransactionPhase.Committed, StringComparison.Ordinal)
            && IsProcessRunningForExecutable(journal.RestartExecutablePath))
        {
            TryRearmRecoveryStartup(normalizedAppDirectory);
            throw new TransactionRecoveryDeferredException(
                "An incomplete update transaction belongs to a running application process; recovery was deferred.");
        }
    }

    private static void RecoverIncompleteTransaction(string appDirectory, bool retainRolledBackJournal = false)
    {
        EnsureNoReparsePointIfPresent(appDirectory);
        string journalPath = Path.Combine(appDirectory, "update_work", TransactionJournalFileName);
        EnsureNoReparsePointAncestors(appDirectory, "update_work/update-transaction.json");
        EnsureNoReparsePointIfPresent(journalPath);
        if (!UpdaterFileSystem.FileExists(journalPath))
        {
            return;
        }

        TransactionJournalRecord journal = ReadTransactionJournal(journalPath);
        ValidateTransactionJournal(journal, appDirectory);
        switch (journal.Phase)
        {
            case TransactionPhase.Prepared:
                // No application path has been moved yet. Keep the downloaded package
                // available for the current invocation, but discard only extraction
                // state; the prior-generation backup is still authoritative until
                // backup rotation begins.
                SafeDeleteDirectory(journal.ExtractDirectory);
                DeleteTransactionJournal(journal);
                return;

            case TransactionPhase.BackingUp:
                // Backup creation is copy-first. The application tree remains the
                // authoritative generation until the Applying phase is durably recorded;
                // never restore a possibly partial copy over it.
                SafeDeleteDirectory(journal.ExtractDirectory);
                SafeDeleteDirectory(journal.BackupDirectory);
                DeleteTransactionJournal(journal);
                return;

            case TransactionPhase.Applying:
            case TransactionPhase.Applied:
                TryRollbackFromJournal(journal, removeNewPackagePaths: journal.BackupComplete);
                MarkTransactionRolledBack(journal);
                if (!retainRolledBackJournal)
                {
                    CleanupRolledBackTransactionArtifacts(journal, removePackage: false);
                }
                return;

            case TransactionPhase.RolledBack:
                // The previous generation has already been restored and that fact is
                // durable. A restart during cleanup must never replay rollback and
                // delete the restored tree a second time.
                if (!retainRolledBackJournal)
                {
                    CleanupRolledBackTransactionArtifacts(journal, removePackage: false);
                }
                return;

            case TransactionPhase.Restarting:
                if (journal.RestartProcessId <= 0
                    && !IsProcessRunningForExecutable(journal.RestartExecutablePath))
                {
                    TryRollbackFromJournal(journal, removeNewPackagePaths: journal.BackupComplete);
                    MarkTransactionRolledBack(journal);
                    if (!retainRolledBackJournal)
                    {
                        CleanupRolledBackTransactionArtifacts(journal, removePackage: false);
                    }
                    return;
                }
                // A restart process is alive, so make the commit point durable before
                // removing the backup. If cleanup is interrupted, the next recovery
                // sees Committed and never attempts rollback against a missing backup.
                journal.Phase = TransactionPhase.Committed;
                WriteTransactionJournal(journal);
                TryCleanupCommittedTransactionArtifacts(journal);
                return;

            case TransactionPhase.Committed:
                TryCleanupCommittedTransactionArtifacts(journal);
                return;

            default:
                throw new InvalidOperationException("The updater transaction journal has an unknown phase: " + journal.Phase);
        }
    }

    private static void TryRollbackFromJournal(TransactionJournalRecord journal, bool removeNewPackagePaths)
    {
        try
        {
            TryRollbackManagedTree(
                journal.AppDirectory,
                journal.PreviousDirectory,
                removeNewPackagePaths ? (journal.NewPackagePaths ?? []) : [],
                createdDirectories: [],
                requirePreviousDirectory: removeNewPackagePaths);
        }
        catch (Exception exception)
        {
            throw new RollbackFailureException(
                "The updater recovered an incomplete transaction, but rollback could not restore every managed path.",
                exception);
        }
    }

    private static void TryCleanupCommittedTransactionArtifacts(TransactionJournalRecord journal)
    {
        SafeDeleteFile(journal.PackagePath);
        SafeDeleteDirectory(journal.ExtractDirectory);
        SafeDeleteDirectory(journal.BackupDirectory);
        DeleteTransactionJournal(journal);
        ClearRecoveryStartup(journal.AppDirectory);
    }

    private static void MarkTransactionRolledBack(TransactionJournalRecord journal)
    {
        journal.Phase = TransactionPhase.RolledBack;
        journal.RestartProcessId = 0;
        WriteTransactionJournal(journal);
    }

    private static void CleanupRolledBackTransactionArtifacts(TransactionJournalRecord journal, bool removePackage)
    {
        if (removePackage)
        {
            SafeDeleteFile(journal.PackagePath);
        }
        SafeDeleteDirectory(journal.ExtractDirectory);
        SafeDeleteDirectory(journal.BackupDirectory);
        DeleteTransactionJournal(journal);
    }

    private static void CleanupRolledBackJournalIfPresent(string appDirectory)
    {
        string journalPath = Path.Combine(appDirectory, "update_work", TransactionJournalFileName);
        if (!UpdaterFileSystem.FileExists(journalPath))
        {
            return;
        }

        TransactionJournalRecord journal = ReadTransactionJournal(journalPath);
        ValidateTransactionJournal(journal, appDirectory);
        if (string.Equals(journal.Phase, TransactionPhase.RolledBack, StringComparison.Ordinal))
        {
            CleanupRolledBackTransactionArtifacts(journal, removePackage: false);
        }
    }

    private static void WriteTransactionJournal(TransactionJournalRecord journal)
    {
        if (journal == null)
        {
            throw new ArgumentNullException(nameof(journal));
        }

        string appDirectory = NormalizeExistingDirectory(journal.AppDirectory);
        ValidateTransactionJournal(journal, appDirectory);
        string workDirectory = Path.Combine(appDirectory, "update_work");
        string journalPath = Path.Combine(workDirectory, TransactionJournalFileName);
        string temporaryPath = Path.Combine(workDirectory, TransactionJournalTemporaryFileName);
        EnsureNoReparsePointAncestors(appDirectory, "update_work/update-transaction.json");
        EnsureNoReparsePointIfPresent(workDirectory);
        UpdaterFileSystem.CreateDirectory(workDirectory);
        EnsureNoReparsePointIfPresent(journalPath);
        EnsureNoReparsePointIfPresent(temporaryPath);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            journal,
            UpdaterJsonSerializerContext.Default.TransactionJournalRecord);
        using (FileStream stream = UpdaterFileSystem.Open(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(payload, 0, payload.Length);
            stream.Flush(flushToDisk: true);
        }

        UpdaterFileSystem.MoveFile(temporaryPath, journalPath, overwrite: true);
        UpdaterFileSystem.FlushFile(journalPath);
    }

    private static TransactionJournalRecord ReadTransactionJournal(string journalPath)
    {
        try
        {
            using FileStream stream = UpdaterFileSystem.OpenRead(journalPath);
            TransactionJournalRecord journal = JsonSerializer.Deserialize(
                stream,
                UpdaterJsonSerializerContext.Default.TransactionJournalRecord);
            return journal ?? throw new InvalidOperationException("The updater transaction journal was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The updater transaction journal was not valid JSON.", exception);
        }
    }

    private static void DeleteTransactionJournal(TransactionJournalRecord journal)
    {
        if (journal == null || string.IsNullOrWhiteSpace(journal.AppDirectory))
        {
            return;
        }

        string journalPath = Path.Combine(journal.AppDirectory, "update_work", TransactionJournalFileName);
        string temporaryPath = Path.Combine(journal.AppDirectory, "update_work", TransactionJournalTemporaryFileName);
        EnsureNoReparsePointIfPresent(journalPath);
        EnsureNoReparsePointIfPresent(temporaryPath);
        SafeDeleteFile(journalPath);
        SafeDeleteFile(temporaryPath);
    }

    private static void ValidateTransactionJournal(TransactionJournalRecord journal, string appDirectory)
    {
        if (journal == null || journal.Version != 1)
        {
            throw new InvalidOperationException("The updater transaction journal version was not recognized.");
        }

        string normalizedAppDirectory = NormalizeDirectoryPath(appDirectory);
        EnsureNoReparsePointIfPresent(normalizedAppDirectory);
        if (!string.Equals(NormalizeDirectoryPath(journal.AppDirectory), normalizedAppDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The updater transaction journal belongs to a different application directory.");
        }

        string normalizedRestartExecutablePath = NormalizeExistingFile(journal.RestartExecutablePath);
        EnsurePathUnderDirectory(normalizedAppDirectory, normalizedRestartExecutablePath, "transaction restart executable");

        string expectedBackupDirectory = NormalizeDirectoryPath(Path.Combine(normalizedAppDirectory, "update_backup"));
        string expectedPreviousDirectory = NormalizeDirectoryPath(Path.Combine(expectedBackupDirectory, "previous"));
        string expectedExtractDirectory = NormalizeDirectoryPath(Path.Combine(normalizedAppDirectory, "update_work", "extracted"));
        if (!string.Equals(NormalizeDirectoryPath(journal.BackupDirectory), expectedBackupDirectory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(NormalizeDirectoryPath(journal.PreviousDirectory), expectedPreviousDirectory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(NormalizeDirectoryPath(journal.ExtractDirectory), expectedExtractDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The updater transaction journal paths were not dedicated transaction paths.");
        }

        EnsureNoReparsePointAncestors(normalizedAppDirectory, "update_work/update-transaction.json");
        EnsureNoReparsePointIfPresent(Path.Combine(normalizedAppDirectory, "update_work"));
        EnsureNoReparsePointAncestors(normalizedAppDirectory, "update_backup/previous");
        EnsureNoReparsePointIfPresent(journal.BackupDirectory);
        EnsureNoReparsePointIfPresent(journal.PreviousDirectory);
        EnsureNoReparsePointIfPresent(journal.ExtractDirectory);
        if (UpdaterFileSystem.DirectoryExists(journal.BackupDirectory))
        {
            EnsureNoReparsePoint(journal.BackupDirectory);
        }
        if (UpdaterFileSystem.DirectoryExists(journal.ExtractDirectory))
        {
            EnsureNoReparsePoint(journal.ExtractDirectory);
        }

        string normalizedPackagePath = UpdaterFileSystem.NormalizePath(journal.PackagePath);
        EnsurePackagePathUnderDownloads(normalizedAppDirectory, normalizedPackagePath);
        EnsureNoReparsePointIfPresent(normalizedPackagePath);
        foreach (string relativePath in journal.NewPackagePaths ?? [])
        {
            NormalizeRelativePackagePath(relativePath);
        }
    }

    private static void ValidatePackage(string packagePath)
    {
        using ZipArchive archive = OpenPackage(packagePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string entryName = entry.FullName.Replace('\\', '/');
            bool isDirectoryEntry = entryName.EndsWith("/", StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(entryName))
            {
                continue;
            }

            if (Path.IsPathRooted(entryName) || entryName.Contains(":"))
            {
                throw new InvalidOperationException("Package contains rooted entry: " + entry.FullName);
            }

            string[] segments = entryName.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => segment == "." || segment == ".."))
            {
                throw new InvalidOperationException("Package contains traversal entry: " + entry.FullName);
            }

            if (segments.Length > 0 && PreservedTopLevelNames.Contains(segments[0]))
            {
                throw new InvalidOperationException("Package must not contain preserved directory: " + segments[0]);
            }
            if (segments.Length > 0 && string.Equals(segments[0], ImportedMetadataDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Package must not contain app-managed metadata cache: " + segments[0]);
            }
            if (segments.Length > 0 && string.Equals(segments[0], MetadataDbFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Package must not contain root metadata database: " + segments[0]);
            }
            if ((segments.Length > 1 || isDirectoryEntry) && string.Equals(segments[0], MetadataArchiveFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Package must contain root metadata archive as a file: " + segments[0]);
            }
        }
    }

    private static ZipArchive OpenPackage(string packagePath)
    {
        return new ZipArchive(UpdaterFileSystem.OpenRead(packagePath), ZipArchiveMode.Read);
    }

    private static void ExtractPackage(string packagePath, string extractDirectory)
    {
        using ZipArchive archive = OpenPackage(packagePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string relativePath = NormalizeRelativePackagePath(entry.FullName);
            string destination = Path.Combine(extractDirectory, relativePath);
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                UpdaterFileSystem.CreateDirectory(destination);
                continue;
            }

            string destinationDirectory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                UpdaterFileSystem.CreateDirectory(destinationDirectory);
            }
            using Stream source = entry.Open();
            using FileStream destinationStream = UpdaterFileSystem.Open(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(destinationStream);
            destinationStream.Flush(flushToDisk: true);
        }
    }

    private static void EnsureNoPreservedTopLevelEntries(string extractDirectory)
    {
        foreach (string path in UpdaterFileSystem.EnumerateFileSystemEntries(extractDirectory))
        {
            string name = Path.GetFileName(path);
            if (PreservedTopLevelNames.Contains(name))
            {
                throw new InvalidOperationException("Extracted package contains preserved entry: " + name);
            }
        }
    }

    private static void ApplyExtractedPackage(
        string extractDirectory,
        string appDirectory,
        HashSet<string> previousManagedPaths,
        HashSet<string> newPackagePaths,
        HashSet<string> appliedPaths,
        ICollection<string> createdDirectories)
    {
        foreach (string relativePath in newPackagePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(relativePath, ManagedFilesManifestName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            EnsureNoReparsePointAncestors(appDirectory, relativePath);
            appliedPaths.Add(relativePath);
            string source = Path.Combine(extractDirectory, relativePath);
            string destination = Path.Combine(appDirectory, relativePath);
            RemoveBlockingManagedAncestors(appDirectory, relativePath, previousManagedPaths, newPackagePaths);
            CreateDestinationDirectory(appDirectory, destination, createdDirectories);
            ApplyFileAtomically(source, destination);
        }

        appliedPaths.Add(ManagedFilesManifestName);
        WriteManagedFilesManifest(appDirectory, newPackagePaths);
        foreach (string relativePath in previousManagedPaths.Except(newPackagePaths, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => path.Length))
        {
            EnsureNoReparsePointAncestors(appDirectory, relativePath);
            string path = Path.Combine(appDirectory, relativePath);
            EnsureNoReparsePointIfPresent(path);
            if (UpdaterFileSystem.DirectoryExists(path))
            {
                EnsureNoReparsePoint(path);
            }
            SafeDeletePath(path);
        }
        RemoveEmptyDirectories(appDirectory);
    }

    private static void RemoveBlockingManagedAncestors(
        string appDirectory,
        string relativePath,
        HashSet<string> previousManagedPaths,
        HashSet<string> newPackagePaths)
    {
        string[] segments = NormalizeRelativePackagePath(relativePath)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < segments.Length; i++)
        {
            string ancestor = string.Join(Path.DirectorySeparatorChar.ToString(), segments[..i]);
            if (!previousManagedPaths.Contains(ancestor)
                || newPackagePaths.Contains(ancestor))
            {
                continue;
            }

            EnsureNoReparsePointAncestors(appDirectory, ancestor);
            string path = Path.Combine(appDirectory, ancestor);
            EnsureNoReparsePointIfPresent(path);
            if (UpdaterFileSystem.DirectoryExists(path))
            {
                EnsureNoReparsePoint(path);
            }
            SafeDeletePath(path);
        }
    }

    private static HashSet<string> PrepareTransactionBackup(
        string appDirectory,
        string previousDirectory,
        HashSet<string> newPackagePaths)
    {
        bool hasManagedFilesManifest = UpdaterFileSystem.FileExists(Path.Combine(appDirectory, ManagedFilesManifestName));
        HashSet<string> previousManagedPaths = ReadManagedFilesManifest(appDirectory, newPackagePaths);
        AddLegacyManagedPaths(previousManagedPaths, appDirectory);
        AddAppManagedMetadataArtifactPaths(previousManagedPaths, appDirectory);
        ValidateExistingPackagePathsBeforeMutation(appDirectory, previousManagedPaths, newPackagePaths, hasManagedFilesManifest);
        ValidateFileToDirectoryTransitions(appDirectory, previousManagedPaths, newPackagePaths);
        CopyExistingPathToBackup(appDirectory, previousDirectory, ManagedFilesManifestName);
        foreach (string relativePath in previousManagedPaths.Except(newPackagePaths, StringComparer.OrdinalIgnoreCase))
        {
            EnsureNoReparsePointAncestors(appDirectory, relativePath);
            CopyExistingPathToBackup(appDirectory, previousDirectory, relativePath);
        }

        foreach (string relativePath in newPackagePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(relativePath, ManagedFilesManifestName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            EnsureNoReparsePointAncestors(appDirectory, relativePath);
            CopyExistingPathToBackup(appDirectory, previousDirectory, relativePath);
        }

        return previousManagedPaths;
    }

    private static void ValidateExtractedPackageBeforeBackupRotation(string extractDirectory, string appDirectory)
    {
        HashSet<string> newPackagePaths = EnumerateRelativePackagePaths(extractDirectory);
        bool hasManagedFilesManifest = UpdaterFileSystem.FileExists(Path.Combine(appDirectory, ManagedFilesManifestName));
        HashSet<string> previousManagedPaths = ReadManagedFilesManifest(appDirectory, newPackagePaths);
        AddLegacyManagedPaths(previousManagedPaths, appDirectory);
        AddAppManagedMetadataArtifactPaths(previousManagedPaths, appDirectory);
        ValidateExistingPackagePathsBeforeMutation(appDirectory, previousManagedPaths, newPackagePaths, hasManagedFilesManifest);
        ValidateFileToDirectoryTransitions(appDirectory, previousManagedPaths, newPackagePaths);
        PreflightManagedPathsForMutation(appDirectory, previousManagedPaths, newPackagePaths);
    }

    private static void PreflightManagedPathsForMutation(
        string appDirectory,
        HashSet<string> previousManagedPaths,
        HashSet<string> newPackagePaths)
    {
        foreach (string relativePath in previousManagedPaths.Union(newPackagePaths, StringComparer.OrdinalIgnoreCase))
        {
            string normalizedRelativePath = NormalizeRelativePackagePath(relativePath);
            string path = Path.Combine(appDirectory, normalizedRelativePath);
            if (!UpdaterFileSystem.EntryExists(path))
            {
                continue;
            }

            EnsureNoReparsePointAncestors(appDirectory, normalizedRelativePath);
            EnsureNoReparsePoint(path);
            if (UpdaterFileSystem.FileExists(path))
            {
                EnsureManagedFileHasExclusiveAccess(path);
                continue;
            }

            foreach (string managedFile in UpdaterFileSystem.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                EnsureManagedFileHasExclusiveAccess(managedFile);
            }
        }
    }

    private static void EnsureManagedFileHasExclusiveAccess(string path)
    {
        try
        {
            using FileStream stream = UpdaterFileSystem.Open(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            throw new IOException(
                "Managed path is not available for exclusive access before update mutation: " + path,
                exception);
        }
    }

    private static void AddAppManagedMetadataArtifactPaths(HashSet<string> managedPaths, string appDirectory)
    {
        AddAppManagedMetadataDirectoryPath(managedPaths, appDirectory, ImportedMetadataDirectoryName);
        AddLegacyManagedFilePath(managedPaths, appDirectory, MetadataDbFileName);
        AddLegacyManagedFilePath(managedPaths, appDirectory, MetadataArchiveFileName);
    }

    private static void ValidateExistingPackagePathsBeforeMutation(
        string appDirectory,
        HashSet<string> previousManagedPaths,
        HashSet<string> newPackagePaths,
        bool hasManagedFilesManifest)
    {
        foreach (string relativePath in previousManagedPaths.Union(newPackagePaths, StringComparer.OrdinalIgnoreCase))
        {
            EnsureNoReparsePointAncestors(appDirectory, relativePath);
            string path = Path.Combine(appDirectory, relativePath);
            EnsureNoReparsePointIfPresent(path);
            if (UpdaterFileSystem.DirectoryExists(path))
            {
                EnsureNoReparsePoint(path);
            }
        }

        foreach (string relativePath in previousManagedPaths)
        {
            string path = Path.Combine(appDirectory, relativePath);
            if (!UpdaterFileSystem.DirectoryExists(path))
            {
                continue;
            }

            foreach (string existingFile in UpdaterFileSystem.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                string existingRelativePath = GetRelativePath(appDirectory, existingFile);
                if (!IsManagedPath(existingRelativePath, appDirectory, previousManagedPaths))
                {
                    throw new IOException("A managed file path contains an unmanaged descendant: " + path);
                }
            }
        }

        foreach (string relativePath in newPackagePaths)
        {
            ValidateExistingFileAncestors(appDirectory, relativePath, previousManagedPaths);
            if (!hasManagedFilesManifest
                || string.Equals(relativePath, ManagedFilesManifestName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string path = Path.Combine(appDirectory, relativePath);
            if (UpdaterFileSystem.FileExists(path)
                && !IsManagedPath(relativePath, appDirectory, previousManagedPaths))
            {
                throw new IOException("Cannot replace an unmanaged application file: " + path);
            }
        }
    }

    private static void ValidateExistingFileAncestors(
        string appDirectory,
        string relativePath,
        HashSet<string> previousManagedPaths)
    {
        string normalized = NormalizeRelativePackagePath(relativePath);
        string[] segments = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        string current = appDirectory;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            current = Path.Combine(current, segments[i]);
            if (!UpdaterFileSystem.FileExists(current))
            {
                continue;
            }

            string ancestorRelativePath = string.Join(Path.DirectorySeparatorChar.ToString(), segments[..(i + 1)]);
            if (!IsManagedPath(ancestorRelativePath, appDirectory, previousManagedPaths))
            {
                throw new IOException("Cannot create a directory below an unmanaged file: " + current);
            }
        }
    }

    private static void ValidateFileToDirectoryTransitions(
        string appDirectory,
        HashSet<string> previousManagedPaths,
        HashSet<string> newPackagePaths)
    {
        foreach (string relativePath in newPackagePaths)
        {
            if (string.Equals(relativePath, ManagedFilesManifestName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string path = Path.Combine(appDirectory, relativePath);
            if (!UpdaterFileSystem.DirectoryExists(path))
            {
                continue;
            }

            EnsureNoReparsePoint(path);
            foreach (string existingFile in UpdaterFileSystem.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                string existingRelativePath = GetRelativePath(appDirectory, existingFile);
                if (!IsManagedPath(existingRelativePath, appDirectory, previousManagedPaths))
                {
                    throw new IOException("Cannot replace a directory containing unmanaged files: " + path);
                }
            }
        }
    }

    private static bool IsManagedPath(
        string relativePath,
        string appDirectory,
        HashSet<string> managedPaths)
    {
        string normalized = NormalizeRelativePackagePath(relativePath);
        if (managedPaths.Contains(normalized))
        {
            return true;
        }

        string current = normalized;
        while (current.Contains(Path.DirectorySeparatorChar))
        {
            current = current[..current.LastIndexOf(Path.DirectorySeparatorChar)];
            if (!managedPaths.Contains(current))
            {
                continue;
            }

            string managedPath = Path.Combine(appDirectory, current);
            if (UpdaterFileSystem.DirectoryExists(managedPath)
                && managedPaths.Any(candidate => IsRelativePathUnderDirectory(candidate, current)))
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsurePackagePathUnderDownloads(string appDirectory, string packagePath)
    {
        string downloadsDirectory = Path.Combine(appDirectory, "update_work", "downloads");
        EnsurePathUnderDirectory(downloadsDirectory, packagePath, "update package");

        EnsureNoReparsePointAncestors(appDirectory, "update_work/downloads");
        EnsureNoReparsePointIfPresent(downloadsDirectory);
        if (UpdaterFileSystem.DirectoryExists(downloadsDirectory))
        {
            EnsureNoReparsePoint(downloadsDirectory);
        }
    }

    private static void EnsurePathUnderDirectory(string rootDirectory, string candidatePath, string description)
    {
        string normalizedCandidatePath = UpdaterFileSystem.NormalizePath(candidatePath);
        string normalizedRootDirectory = UpdaterFileSystem.NormalizePath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedCandidatePath, normalizedRootDirectory, StringComparison.OrdinalIgnoreCase)
            || !normalizedCandidatePath.StartsWith(
                normalizedRootDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The " + description + " must be under " + normalizedRootDirectory + ".");
        }
    }

    private static void AddAppManagedMetadataDirectoryPath(HashSet<string> managedPaths, string appDirectory, string relativePath)
    {
        string normalized = NormalizeRelativePackagePath(relativePath);
        string path = Path.Combine(appDirectory, normalized);
        if (UpdaterFileSystem.DirectoryExists(path))
        {
            EnsureNoReparsePoint(path);
            managedPaths.RemoveWhere(candidate => IsRelativePathUnderDirectory(candidate, normalized));
            foreach (string file in UpdaterFileSystem.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                managedPaths.Add(GetRelativePath(appDirectory, file));
            }
            return;
        }

        if (UpdaterFileSystem.FileExists(path))
        {
            managedPaths.Add(normalized);
        }
    }

    private static void TryRollback(
        string appDirectory,
        string previousDirectory,
        HashSet<string> appliedPaths,
        IEnumerable<string> createdDirectories)
    {
        try
        {
            TryRollbackManagedTree(
                appDirectory,
                previousDirectory,
                appliedPaths,
                createdDirectories,
                requirePreviousDirectory: true);
        }
        catch (Exception exception)
        {
            throw new RollbackFailureException(
                "The update failed and rollback could not restore every managed path.",
                exception);
        }
    }

    private static void TryRollbackManagedTree(
        string appDirectory,
        string previousDirectory,
        IEnumerable<string> pathsToRemove,
        IEnumerable<string> createdDirectories,
        bool requirePreviousDirectory)
    {
        string normalizedAppDirectory = NormalizeExistingDirectory(appDirectory);
        string normalizedPreviousDirectory = NormalizeDirectoryPath(previousDirectory);
        string workDirectory = Path.Combine(normalizedAppDirectory, "update_work");
        string stagingDirectory = Path.Combine(workDirectory, RollbackStagingDirectoryName);
        string rollbackOriginalDirectory = Path.Combine(
            workDirectory,
            RollbackOriginalDirectoryPrefix + Guid.NewGuid().ToString("N"));

        EnsureNoReparsePointAncestors(normalizedAppDirectory, "update_work/" + RollbackStagingDirectoryName);
        EnsureNoReparsePointIfPresent(workDirectory);
        EnsureNoReparsePointIfPresent(stagingDirectory);
        if (UpdaterFileSystem.FileExists(stagingDirectory))
        {
            throw new IOException("Rollback staging path is occupied by a file: " + stagingDirectory);
        }
        if (UpdaterFileSystem.DirectoryExists(stagingDirectory))
        {
            EnsureNoReparsePoint(stagingDirectory);
            UpdaterFileSystem.DeleteDirectory(stagingDirectory, recursive: true);
        }
        UpdaterFileSystem.CreateDirectory(stagingDirectory);
        UpdaterFileSystem.CreateDirectory(rollbackOriginalDirectory);

        if (UpdaterFileSystem.FileExists(normalizedPreviousDirectory))
        {
            throw new IOException("The previous-generation backup path is a file: " + normalizedPreviousDirectory);
        }
        if (requirePreviousDirectory
            && !UpdaterFileSystem.DirectoryExists(normalizedPreviousDirectory))
        {
            throw new DirectoryNotFoundException(
                "The previous-generation backup path was not available for rollback: "
                + normalizedPreviousDirectory);
        }
        if (UpdaterFileSystem.DirectoryExists(normalizedPreviousDirectory))
        {
            EnsureNoReparsePoint(normalizedPreviousDirectory);
            CopyDirectoryContentsToBackup(normalizedPreviousDirectory, stagingDirectory);
        }

        PromoteRollbackStaging(
            normalizedAppDirectory,
            normalizedPreviousDirectory,
            stagingDirectory,
            rollbackOriginalDirectory,
            pathsToRemove,
            createdDirectories);

        CleanupRollbackArtifacts(workDirectory);
    }

    private static void PromoteRollbackStaging(
        string appDirectory,
        string previousDirectory,
        string stagingDirectory,
        string rollbackOriginalDirectory,
        IEnumerable<string> pathsToRemove,
        IEnumerable<string> createdDirectories)
    {
        HashSet<string> normalizedPathsToRemove = NormalizeRollbackCandidatePaths(pathsToRemove);
        string[] stagedEntries = UpdaterFileSystem.EnumerateFileSystemEntries(stagingDirectory)
            .OrderBy(path => UpdaterFileSystem.DirectoryExists(path) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (string stagedEntry in stagedEntries)
        {
            string destination = Path.Combine(appDirectory, Path.GetFileName(stagedEntry));
            PromoteRollbackEntry(
                stagedEntry,
                destination,
                appDirectory,
                rollbackOriginalDirectory,
                normalizedPathsToRemove);
        }

        RemovePathsAbsentFromPreviousGeneration(
            appDirectory,
            previousDirectory,
            normalizedPathsToRemove);
        RemoveCreatedDirectories(createdDirectories ?? []);
    }

    private static void PromoteRollbackEntry(
        string stagedPath,
        string destinationPath,
        string appDirectory,
        string rollbackOriginalDirectory,
        HashSet<string> pathsToRemove)
    {
        EnsureNoReparsePointEntry(stagedPath);
        EnsureNoReparsePointAncestors(
            appDirectory,
            GetRelativePath(appDirectory, destinationPath));
        EnsureNoReparsePointIfPresent(destinationPath);
        EnsureRollbackDestinationParent(destinationPath);
        if (UpdaterFileSystem.FileExists(stagedPath))
        {
            if (UpdaterFileSystem.FileExists(destinationPath))
            {
                EnsureNoReparsePointEntry(destinationPath);
                UpdaterFileSystem.ReplaceFile(stagedPath, destinationPath);
                return;
            }

            if (UpdaterFileSystem.DirectoryExists(destinationPath))
            {
                EnsureRollbackDirectoryContainsOnlyCandidatePaths(
                    appDirectory,
                    destinationPath,
                    pathsToRemove);
                MoveRollbackDestinationToOriginal(
                    destinationPath,
                    appDirectory,
                    rollbackOriginalDirectory);
            }

            UpdaterFileSystem.MoveFile(stagedPath, destinationPath);
            UpdaterFileSystem.FlushFile(destinationPath);
            return;
        }

        if (!UpdaterFileSystem.DirectoryExists(stagedPath))
        {
            return;
        }

        if (UpdaterFileSystem.FileExists(destinationPath))
        {
            MoveRollbackDestinationToOriginal(
                destinationPath,
                appDirectory,
                rollbackOriginalDirectory);
        }
        else if (!UpdaterFileSystem.DirectoryExists(destinationPath))
        {
            UpdaterFileSystem.MoveDirectory(stagedPath, destinationPath);
            return;
        }

        string[] stagedChildren = UpdaterFileSystem.EnumerateFileSystemEntries(stagedPath)
            .OrderBy(path => UpdaterFileSystem.DirectoryExists(path) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (string stagedChild in stagedChildren)
        {
            PromoteRollbackEntry(
                stagedChild,
                Path.Combine(destinationPath, Path.GetFileName(stagedChild)),
                appDirectory,
                rollbackOriginalDirectory,
                pathsToRemove);
        }
    }

    private static void EnsureRollbackDestinationParent(string destinationPath)
    {
        string parentDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(parentDirectory)
            || UpdaterFileSystem.DirectoryExists(parentDirectory))
        {
            return;
        }

        if (UpdaterFileSystem.FileExists(parentDirectory))
        {
            throw new IOException("A file blocks the rollback destination directory: " + parentDirectory);
        }

        UpdaterFileSystem.CreateDirectory(parentDirectory);
    }

    private static void MoveRollbackDestinationToOriginal(
        string destinationPath,
        string appDirectory,
        string rollbackOriginalDirectory)
    {
        EnsureNoReparsePointEntry(destinationPath);
        string relativePath = GetRelativePath(appDirectory, destinationPath);
        string originalPath = Path.Combine(rollbackOriginalDirectory, relativePath);
        string originalParentDirectory = Path.GetDirectoryName(originalPath);
        if (!string.IsNullOrWhiteSpace(originalParentDirectory))
        {
            UpdaterFileSystem.CreateDirectory(originalParentDirectory);
        }

        if (UpdaterFileSystem.FileExists(destinationPath))
        {
            UpdaterFileSystem.MoveFile(destinationPath, originalPath);
        }
        else if (UpdaterFileSystem.DirectoryExists(destinationPath))
        {
            EnsureNoReparsePoint(destinationPath);
            UpdaterFileSystem.MoveDirectory(destinationPath, originalPath);
        }
    }

    private static void RemovePathsAbsentFromPreviousGeneration(
        string appDirectory,
        string previousDirectory,
        IEnumerable<string> pathsToRemove)
    {
        HashSet<string> normalizedPaths = NormalizeRollbackCandidatePaths(pathsToRemove);

        foreach (string relativePath in normalizedPaths
            .OrderByDescending(path => path.Length)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string previousPath = Path.Combine(previousDirectory, relativePath);
            if (UpdaterFileSystem.EntryExists(previousPath))
            {
                continue;
            }

            EnsureNoReparsePointAncestors(appDirectory, relativePath);
            string destinationPath = Path.Combine(appDirectory, relativePath);
            EnsureNoReparsePointIfPresent(destinationPath);
            if (!UpdaterFileSystem.EntryExists(destinationPath))
            {
                continue;
            }

            if (UpdaterFileSystem.DirectoryExists(destinationPath))
            {
                EnsureNoReparsePoint(destinationPath);
                EnsureRollbackDirectoryContainsOnlyCandidatePaths(
                    appDirectory,
                    destinationPath,
                    normalizedPaths);
            }

            SafeDeletePath(destinationPath);
        }
    }

    private static HashSet<string> NormalizeRollbackCandidatePaths(IEnumerable<string> pathsToRemove)
    {
        var normalizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relativePath in pathsToRemove ?? [])
        {
            string normalized = NormalizeRelativePackagePath(relativePath);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                normalizedPaths.Add(normalized);
            }
        }

        return normalizedPaths;
    }

    private static void EnsureRollbackDirectoryContainsOnlyCandidatePaths(
        string appDirectory,
        string directoryPath,
        HashSet<string> candidatePaths)
    {
        foreach (string filePath in UpdaterFileSystem.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            string relativePath = GetRelativePath(appDirectory, filePath);
            if (!candidatePaths.Contains(relativePath))
            {
                throw new IOException("Rollback refused to delete an unmanaged descendant: " + filePath);
            }
        }
    }

    private static void CleanupRollbackArtifacts(string workDirectory)
    {
        var failures = new List<Exception>();
        try
        {
            foreach (string directory in UpdaterFileSystem.EnumerateDirectories(
                workDirectory,
                RollbackOriginalDirectoryPrefix + "*",
                SearchOption.TopDirectoryOnly)
                .ToArray())
            {
                try
                {
                    EnsureNoReparsePoint(directory);
                    UpdaterFileSystem.DeleteDirectory(directory, recursive: true);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            string stagingDirectory = Path.Combine(workDirectory, RollbackStagingDirectoryName);
            if (UpdaterFileSystem.DirectoryExists(stagingDirectory))
            {
                EnsureNoReparsePoint(stagingDirectory);
                UpdaterFileSystem.DeleteDirectory(stagingDirectory, recursive: true);
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Rollback temporary artifacts could not be cleaned up.", failures);
        }
    }

    private static void CreateDestinationDirectory(
        string appDirectory,
        string destination,
        ICollection<string> createdDirectories)
    {
        string directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        string normalizedRoot = UpdaterFileSystem.NormalizePath(appDirectory);
        string current = UpdaterFileSystem.NormalizePath(directory);
        var missingDirectories = new List<string>();
        while (!string.Equals(current, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (!current.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Destination directory escaped the application directory: " + destination);
            }

            EnsureNoReparsePointIfPresent(current);
            if (UpdaterFileSystem.DirectoryExists(current))
            {
                break;
            }
            if (UpdaterFileSystem.FileExists(current))
            {
                throw new IOException("A file blocks the destination directory: " + current);
            }

            missingDirectories.Add(current);
            current = Path.GetDirectoryName(current)
                ?? throw new InvalidOperationException("Destination directory has no application ancestor: " + destination);
        }

        foreach (string missingDirectory in missingDirectories.AsEnumerable().Reverse())
        {
            UpdaterFileSystem.CreateDirectory(missingDirectory);
            createdDirectories.Add(missingDirectory);
        }
    }

    private static void RemoveCreatedDirectories(IEnumerable<string> createdDirectories)
    {
        foreach (string directory in createdDirectories
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => path.Length))
        {
            if (!UpdaterFileSystem.DirectoryExists(directory))
            {
                continue;
            }

            EnsureNoReparsePointEntry(directory);
            if (!UpdaterFileSystem.EnumerateFileSystemEntries(directory).Any())
            {
                UpdaterFileSystem.DeleteDirectory(directory, recursive: false);
            }
        }
    }

    private static HashSet<string> EnumerateRelativePackagePaths(string extractDirectory)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string sourceFile in UpdaterFileSystem.EnumerateFiles(extractDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = GetRelativePath(extractDirectory, sourceFile);
            if (!string.Equals(relativePath, ManagedFilesManifestName, StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(relativePath);
            }
        }
        paths.Add(ManagedFilesManifestName);
        return paths;
    }

    private static void AddLegacyManagedPaths(HashSet<string> managedPaths, string appDirectory)
    {
        foreach (string relativePath in LegacyManagedFilePaths)
        {
            AddLegacyManagedFilePath(managedPaths, appDirectory, relativePath);
        }
    }

    private static void AddLegacyManagedFilePath(HashSet<string> managedPaths, string appDirectory, string relativePath)
    {
        string normalized = NormalizeRelativePackagePath(relativePath);
        if (UpdaterFileSystem.FileExists(Path.Combine(appDirectory, normalized)))
        {
            managedPaths.Add(normalized);
        }
    }

    private static bool IsRelativePathUnderDirectory(string relativePath, string directoryPath)
    {
        string normalizedRelativePath = NormalizeRelativePackagePath(relativePath);
        string normalizedDirectoryPath = NormalizeRelativePackagePath(directoryPath);
        string directoryPrefix = normalizedDirectoryPath + Path.DirectorySeparatorChar;
        return normalizedRelativePath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> ReadManagedFilesManifest(string appDirectory, HashSet<string> newPackagePaths)
    {
        string manifestPath = Path.Combine(appDirectory, ManagedFilesManifestName);
        if (!UpdaterFileSystem.FileExists(manifestPath))
        {
            return [.. newPackagePaths.Where(path => UpdaterFileSystem.EntryExists(Path.Combine(appDirectory, path)))];
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in UpdaterFileSystem.ReadAllLines(manifestPath))
        {
            string normalized = NormalizeRelativePackagePath(line);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                paths.Add(normalized);
            }
        }
        paths.Add(ManagedFilesManifestName);
        return paths;
    }

    private static void WriteManagedFilesManifest(string appDirectory, HashSet<string> managedPaths)
    {
        string manifestPath = Path.Combine(appDirectory, ManagedFilesManifestName);
        string temporaryPath = Path.Combine(appDirectory, "update_work", ManagedFilesManifestName + ".tmp");
        string[] lines = [.. managedPaths
            .Where(path => !string.Equals(path, ManagedFilesManifestName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
        EnsureNoReparsePointAncestors(appDirectory, "update_work/update-managed-files.txt.tmp");
        EnsureNoReparsePointIfPresent(temporaryPath);
        UpdaterFileSystem.CreateDirectory(Path.GetDirectoryName(temporaryPath));
        UpdaterFileSystem.WriteAllLines(temporaryPath, lines);
        if (UpdaterFileSystem.FileExists(manifestPath))
        {
            EnsureNoReparsePointEntry(manifestPath);
            UpdaterFileSystem.ReplaceFile(temporaryPath, manifestPath);
        }
        else
        {
            UpdaterFileSystem.MoveFile(temporaryPath, manifestPath);
            UpdaterFileSystem.FlushFile(manifestPath);
        }
    }

    private static void CopyExistingPathToBackup(
        string appDirectory,
        string previousDirectory,
        string relativePath)
    {
        relativePath = NormalizeRelativePackagePath(relativePath);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        string source = Path.Combine(appDirectory, relativePath);
        EnsureNoReparsePointIfPresent(source);
        if (!UpdaterFileSystem.EntryExists(source))
        {
            return;
        }

        EnsureNoReparsePoint(source);
        string destination = Path.Combine(previousDirectory, relativePath);
        EnsureNoReparsePointIfPresent(destination);
        if (UpdaterFileSystem.EntryExists(destination))
        {
            return;
        }

        if (UpdaterFileSystem.FileExists(source))
        {
            UpdaterFileSystem.CreateDirectory(Path.GetDirectoryName(destination));
            UpdaterFileSystem.CopyFile(source, destination, overwrite: false);
        }
        else if (UpdaterFileSystem.DirectoryExists(source))
        {
            UpdaterFileSystem.CreateDirectory(destination);
            CopyDirectoryContentsToBackup(source, destination);
        }
    }

    private static void CopyDirectoryContentsToBackup(string sourceDirectory, string destinationDirectory)
    {
        foreach (string sourceChild in UpdaterFileSystem.EnumerateFileSystemEntries(sourceDirectory))
        {
            EnsureNoReparsePointEntry(sourceChild);
            string destinationChild = Path.Combine(destinationDirectory, Path.GetFileName(sourceChild));
            if (UpdaterFileSystem.FileExists(sourceChild))
            {
                UpdaterFileSystem.CopyFile(sourceChild, destinationChild, overwrite: false);
                continue;
            }

            if (UpdaterFileSystem.DirectoryExists(sourceChild))
            {
                UpdaterFileSystem.CreateDirectory(destinationChild);
                CopyDirectoryContentsToBackup(sourceChild, destinationChild);
            }
        }
    }

    private static void RemoveEmptyDirectories(string appDirectory)
    {
        foreach (string directory in EnumerateDirectoriesWithoutReparsePoints(appDirectory).OrderByDescending(path => path.Length))
        {
            string name = Path.GetFileName(directory);
            if (PreservedTopLevelNames.Contains(name))
            {
                continue;
            }

            if (!UpdaterFileSystem.EnumerateFileSystemEntries(directory).Any())
            {
                UpdaterFileSystem.DeleteDirectory(directory, recursive: false);
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesWithoutReparsePoints(string rootDirectory)
    {
        var pending = new Stack<string>();
        pending.Push(rootDirectory);
        while (pending.Count > 0)
        {
            string parent = pending.Pop();
            foreach (string entry in UpdaterFileSystem.EnumerateFileSystemEntries(parent))
            {
                if (!UpdaterFileSystem.DirectoryExists(entry))
                {
                    continue;
                }

                if (string.Equals(parent, rootDirectory, StringComparison.OrdinalIgnoreCase)
                    && PreservedTopLevelNames.Contains(Path.GetFileName(entry)))
                {
                    continue;
                }

                EnsureNoReparsePointEntry(entry);
                yield return entry;
                pending.Push(entry);
            }
        }
    }

    private static void EnsureNoReparsePoint(string path)
    {
        EnsureNoReparsePointEntry(path);

        if (UpdaterFileSystem.DirectoryExists(path))
        {
            foreach (string child in UpdaterFileSystem.EnumerateFileSystemEntries(path))
            {
                EnsureNoReparsePoint(child);
            }
        }
    }

    private static void EnsureNoReparsePointEntry(string path)
    {
        FileAttributes attributes = UpdaterFileSystem.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Reparse points are not supported in the application directory: " + path);
        }
    }

    private static void EnsureNoReparsePointAncestors(string appDirectory, string relativePath)
    {
        string[] segments = NormalizeAncestorPath(relativePath)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        string current = appDirectory;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            current = Path.Combine(current, segments[i]);
            EnsureNoReparsePointIfPresent(current);
        }
    }

    private static string NormalizeAncestorPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string normalized = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).Trim();
        if (Path.IsPathRooted(normalized) || normalized.Contains(":"))
        {
            throw new InvalidOperationException("Ancestor path must be relative: " + path);
        }

        string[] segments = normalized.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == "." || segment == ".."))
        {
            throw new InvalidOperationException("Ancestor path must not contain traversal: " + path);
        }

        return string.Join(Path.DirectorySeparatorChar.ToString(), segments);
    }

    private static void EnsureNoReparsePointIfPresent(string path)
    {
        try
        {
            EnsureNoReparsePointEntry(path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static string NormalizeExistingDirectory(string path)
    {
        string normalized = NormalizeDirectoryPath(path);
        if (!UpdaterFileSystem.DirectoryExists(normalized))
        {
            throw new DirectoryNotFoundException(normalized);
        }
        return normalized;
    }

    private static string NormalizeExistingFile(string path)
    {
        string normalized = NormalizeFilePath(path);
        if (!UpdaterFileSystem.FileExists(normalized))
        {
            throw new FileNotFoundException("File not found.", normalized);
        }
        return normalized;
    }

    private static string NormalizeDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Directory path is required.");
        }
        return UpdaterFileSystem.NormalizePath(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static string NormalizeFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }
        return UpdaterFileSystem.NormalizePath(path);
    }

    private static string GetRelativePath(string rootDirectory, string path)
    {
        Uri rootUri = new(NormalizeDirectoryPath(rootDirectory) + Path.DirectorySeparatorChar);
        Uri pathUri = new(UpdaterFileSystem.NormalizePath(path));
        string relative = Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        return NormalizeRelativePackagePath(relative);
    }

    private static string NormalizeRelativePackagePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string normalized = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).Trim();
        if (Path.IsPathRooted(normalized) || normalized.Contains(":"))
        {
            throw new InvalidOperationException("Managed package path must be relative: " + path);
        }

        string[] segments = normalized.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == "." || segment == ".."))
        {
            throw new InvalidOperationException("Managed package path must not contain traversal: " + path);
        }

        if (segments.Length > 0 && PreservedTopLevelNames.Contains(segments[0]))
        {
            throw new InvalidOperationException("Managed package path must not target preserved directory: " + path);
        }

        return string.Join(Path.DirectorySeparatorChar.ToString(), segments);
    }

    private static void SafeDeleteFile(string path)
    {
        if (UpdaterFileSystem.FileExists(path))
        {
            UpdaterFileSystem.DeleteFile(path);
        }
    }

    private static void SafeDeleteDirectory(string path)
    {
        if (UpdaterFileSystem.DirectoryExists(path))
        {
            UpdaterFileSystem.DeleteDirectory(path, recursive: true);
        }
    }

    private static void SafeDeletePath(string path)
    {
        if (UpdaterFileSystem.FileExists(path))
        {
            UpdaterFileSystem.DeleteFile(path);
        }
        else if (UpdaterFileSystem.DirectoryExists(path))
        {
            UpdaterFileSystem.DeleteDirectory(path, recursive: true);
        }
    }

    private static void ApplyFileAtomically(string source, string destination)
    {
        if (UpdaterFileSystem.DirectoryExists(destination))
        {
            EnsureNoReparsePoint(destination);
            UpdaterFileSystem.DeleteDirectory(destination, recursive: true);
        }

        if (UpdaterFileSystem.FileExists(destination))
        {
            EnsureNoReparsePointEntry(destination);
            UpdaterFileSystem.ReplaceFile(source, destination);
        }
        else
        {
            UpdaterFileSystem.MoveFile(source, destination);
            UpdaterFileSystem.FlushFile(destination);
        }
    }

    private sealed class TransactionLease : IDisposable
    {
        private FileStream stream;

        internal TransactionLease(FileStream stream)
        {
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref stream, null)?.Dispose();
        }
    }

    private sealed class TransactionJournalRecord
    {
        public int Version { get; set; } = 1;

        public string Phase { get; set; }

        public string AppDirectory { get; set; }

        public string PackagePath { get; set; }

        public int ApplicationProcessId { get; set; }

        public string BackupDirectory { get; set; }

        public string PreviousDirectory { get; set; }

        public string ExtractDirectory { get; set; }

        public string RestartExecutablePath { get; set; }

        public int RestartProcessId { get; set; }

        public bool Preflight { get; set; }

        public bool BackupComplete { get; set; }

        public List<string> NewPackagePaths { get; set; } = [];
    }

    private sealed class RollbackFailureException : Exception
    {
        public RollbackFailureException(Exception updateException, Exception rollbackException)
            : base(
                "The update failed and rollback could not be completed.",
                new AggregateException(updateException, rollbackException))
        {
        }

        public RollbackFailureException(string message, Exception rollbackException)
            : base(message, rollbackException)
        {
        }
    }

    private sealed class TransactionLeaseUnavailableException : IOException
    {
        public TransactionLeaseUnavailableException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private sealed class TransactionRecoveryDeferredException : Exception
    {
        public TransactionRecoveryDeferredException(string message)
            : base(message)
        {
        }
    }

    private sealed class RecoveryRequest
    {
        private RecoveryRequest(string appDirectory)
        {
            AppDirectory = appDirectory;
        }

        public string AppDirectory { get; }

        public static RecoveryRequest Parse(string[] args)
        {
            if (args.Length != 3
                || !string.Equals(args[1], "--app-dir", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(args[2]))
            {
                throw new ArgumentException("Recovery requires exactly --recover --app-dir <path>.");
            }

            return new RecoveryRequest(args[2]);
        }
    }

    private sealed class WatchdogRequest
    {
        private WatchdogRequest(string appDirectory, int processId)
        {
            AppDirectory = appDirectory;
            ProcessId = processId;
        }

        public string AppDirectory { get; }

        public int ProcessId { get; }

        public static WatchdogRequest Parse(string[] args)
        {
            if (args.Length != 5
                || !string.Equals(args[1], "--app-dir", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(args[2])
                || !string.Equals(args[3], "--pid", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(args[4], out int processId)
                || processId <= 0)
            {
                throw new ArgumentException("Watchdog requires exactly --watch --app-dir <path> --pid <id>.");
            }

            return new WatchdogRequest(args[2], processId);
        }
    }

    private sealed class UpdateRequest
    {
        public string AppDirectory { get; private set; }

        public string PackagePath { get; private set; }

        public string BackupDirectory { get; private set; }

        public string ReadyFilePath { get; private set; }

        public string DecisionFilePath { get; private set; }

        public int ProcessId { get; private set; }

        public string RestartExePath { get; private set; }

        public static UpdateRequest Parse(string[] args)
        {
            Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i];
                if (!key.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("Unexpected argument: " + key);
                }
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("Missing value for argument: " + key);
                }
                values[key] = args[++i];
            }

            string processIdText = ReadRequired(values, "--pid");
            if (!int.TryParse(processIdText, out int processId) || processId <= 0)
            {
                throw new ArgumentException("The --pid argument must be a positive process id.");
            }

            return new UpdateRequest
            {
                AppDirectory = ReadRequired(values, "--app-dir"),
                PackagePath = ReadRequired(values, "--package"),
                BackupDirectory = ReadRequired(values, "--backup-dir"),
                ReadyFilePath = ReadRequired(values, "--ready-file"),
                DecisionFilePath = ReadRequired(values, "--decision-file"),
                ProcessId = processId,
                RestartExePath = ReadRequired(values, "--restart-exe")
            };
        }

        private static string ReadRequired(Dictionary<string, string> values, string key)
        {
            if (!values.TryGetValue(key, out string value) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Missing required argument: " + key);
            }
            return value;
        }
    }
}
