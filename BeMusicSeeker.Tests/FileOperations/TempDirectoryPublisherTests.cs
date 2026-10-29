using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class TempDirectoryPublisherTests
{
    /// <summary>未使用のセッションへ並列に初回取得し、各返却時の作成印と独立した管理ディレクトリを確認する。</summary>
    /// <remarks>実製品を別ロードして既存テストによる初期化と分離する。内部I/Oの交錯自体は強制せず、排他範囲の静的確認と分担する。</remarks>
    [TestMethod]
    public async Task ConcurrentFirstGetsReturnDistinctDirectoriesWithCompletedSessionMarker()
    {
        string assemblyPath = typeof(TempDirectoryPublisher).Assembly.Location;
        var context = new IsolatedPublisherLoadContext(assemblyPath);
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim();
        var workers = new List<Task<string>>();
        var cleanupFailures = new List<Exception>();
        Exception? bodyFailure = null;
        Action<Action<string, Exception>>? removeAll = null;
        try
        {
            Type publisher = context.LoadFromAssemblyPath(assemblyPath).GetType(typeof(TempDirectoryPublisher).FullName ?? throw new AssertFailedException(), throwOnError: true)
                ?? throw new AssertFailedException();
            Func<string, string> get = (publisher.GetMethod(nameof(TempDirectoryPublisher.Get), [typeof(string)]) ?? throw new AssertFailedException()).CreateDelegate<Func<string, string>>();
            Func<string, bool> isManaged = (publisher.GetMethod(nameof(TempDirectoryPublisher.IsManagedPath)) ?? throw new AssertFailedException()).CreateDelegate<Func<string, bool>>();
            removeAll = (publisher.GetMethod(nameof(TempDirectoryPublisher.RemoveAll), [typeof(Action<string, Exception>)]) ?? throw new AssertFailedException()).CreateDelegate<Action<Action<string, Exception>>>();
            for (int i = 0; i < 2; i++)
            {
                workers.Add(Task.Factory.StartNew(() =>
                {
                    ready.Signal();
                    start.Wait();
                    string path = get("parallel-first-get");
                    Assert.IsTrue(Directory.Exists(path));
                    Assert.IsTrue(isManaged(path));
                    string session = Path.GetDirectoryName(path) ?? throw new AssertFailedException();
                    // 全worker合流前に観測し、読み手自身は書込みの共有違反を起こさない。
                    using var marker = new FileStream(Path.Combine(session, ".bemusicseeker-temp-session"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(marker);
                    Assert.AreEqual("BeMusicSeeker managed temporary session", reader.ReadToEnd());
                    return path;
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
            }
            ready.Wait();
            start.Set();
            string[] paths = await Task.WhenAll(workers);
            Assert.AreEqual(2, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.AreEqual(Path.GetDirectoryName(paths[0]), Path.GetDirectoryName(paths[1]));
        }
        catch (Exception ex)
        {
            bodyFailure = ex;
        }
        finally
        {
            start.Set();
            try { await Task.WhenAll(workers); }
            catch (Exception ex) { bodyFailure ??= ex; }
            try
            {
                // 別ロードした型が所有するセッションだけを、全取得の終端後に回収する。
                removeAll?.Invoke((path, error) => cleanupFailures.Add(new IOException("テスト所有セッションの回収に失敗: " + path, error)));
            }
            catch (Exception ex)
            {
                cleanupFailures.Add(ex);
            }
            finally
            {
                context.Unload();
            }
        }
        if (bodyFailure != null)
        {
            if (cleanupFailures.Count != 0) { bodyFailure.Data["SessionCleanupFailures"] = new AggregateException(cleanupFailures).ToString(); }
            ExceptionDispatchInfo.Capture(bodyFailure).Throw();
        }
        if (cleanupFailures.Count != 0) { throw new AggregateException(cleanupFailures); }
    }

    /// <summary>非Gregorian文化でも固定UTC形式のsession名で管理ディレクトリを作り、実在・所有判定と回収を確認する。</summary>
    /// <remarks>実ファイルと所有markerの削除境界を確認し、管理外の利用者データ保持を分担する。</remarks>
    [DataTestMethod]
    [DoNotParallelize]
    [DataRow("th-TH")]
    public void GetCreatesManagedDirectory(string cultureName)
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            string directoryPath = TempDirectoryPublisher.Get("unit-test");

            try
            {
                Assert.IsTrue(Directory.Exists(directoryPath));
                Assert.IsTrue(TempDirectoryPublisher.IsManagedPath(directoryPath));
                string sessionName = Path.GetFileName(Path.GetDirectoryName(directoryPath)) ?? throw new AssertFailedException();
                StringAssert.Matches(sessionName, new System.Text.RegularExpressions.Regex(@"^session-[0-9]{17}-[0-9]+-[a-f0-9]{32}$"));
                var sessionTime = DateTime.ParseExact(sessionName.Substring(8, 17), "yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
                Assert.IsTrue(sessionTime <= DateTime.UtcNow, "管理名はGregorianのUTC日時を使います。");
                StringAssert.Contains(directoryPath, Path.Combine(Path.GetTempPath(), "BeMusicSeeker"));
            }
            finally
            {
                TempDirectoryPublisher.TryDeleteManagedPath(directoryPath);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    /// <summary>管理された実ディレクトリ内のファイルとディレクトリだけを削除し、所有対象の回収漏れを検出する。</summary>
    /// <remarks>実ファイルと所有markerの削除境界を確認し、管理外の利用者データ保持を分担する。</remarks>
    [TestMethod]
    public void TryDeleteManagedPathDeletesOnlyManagedPaths()
    {
        string directoryPath = TempDirectoryPublisher.Get("unit-test-delete");
        try
        {
            string filePath = Path.Combine(directoryPath, "archive.zip");
            File.WriteAllText(filePath, "temporary archive");

            bool deletedFile = TempDirectoryPublisher.TryDeleteManagedPath(filePath);
            bool deletedDirectory = TempDirectoryPublisher.TryDeleteManagedPath(directoryPath);

            Assert.IsTrue(deletedFile);
            Assert.IsTrue(deletedDirectory);
            Assert.IsFalse(File.Exists(filePath));
            Assert.IsFalse(Directory.Exists(directoryPath));
        }
        finally
        {
            TempDirectoryPublisher.TryDeleteManagedPath(directoryPath);
        }

    }

    /// <summary>アプリtemp配下でも利用者が作ったファイルは管理外として保持し、場所だけを根拠に誤削除する危険を検出する。</summary>
    /// <remarks>実ファイルと所有markerの削除境界を確認し、管理外の利用者データ保持を分担する。</remarks>
    [TestMethod]
    public void TryDeleteManagedPathDoesNotDeleteManualFileUnderAppTempRoot()
    {
        string managedRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker");
        string manualFilePath = Path.Combine(managedRootPath, "manual-" + Guid.NewGuid().ToString("N") + ".zip");
        Directory.CreateDirectory(managedRootPath);
        File.WriteAllText(manualFilePath, "user owned");

        try
        {
            Assert.IsFalse(TempDirectoryPublisher.IsManagedPath(manualFilePath));
            Assert.IsFalse(TempDirectoryPublisher.TryDeleteManagedPath(manualFilePath));
            Assert.IsTrue(File.Exists(manualFilePath));
        }
        finally
        {
            File.Delete(manualFilePath);
        }
    }

    /// <summary>session風の名前でも管理markerのない実ディレクトリを保持し、名前だけから所有を推定して誤削除する危険を検出する。</summary>
    /// <remarks>実ファイルと所有markerの削除境界を確認し、管理外の利用者データ保持を分担する。</remarks>
    [TestMethod]
    public void TryDeleteManagedPathDoesNotDeleteManualSessionNamedDirectoryWithoutMarker()
    {
        string managedRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker");
        string manualDirectoryPath = Path.Combine(managedRootPath, "session-manual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(manualDirectoryPath);
        File.WriteAllText(Path.Combine(manualDirectoryPath, "archive.zip"), "user owned");

        try
        {
            Assert.IsFalse(TempDirectoryPublisher.IsManagedPath(manualDirectoryPath));
            Assert.IsFalse(TempDirectoryPublisher.TryDeleteManagedPath(manualDirectoryPath));
            Assert.IsTrue(Directory.Exists(manualDirectoryPath));
        }
        finally
        {
            Directory.Delete(manualDirectoryPath, recursive: true);
        }
    }
    private sealed class IsolatedPublisherLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string? path = resolver.ResolveAssemblyToPath(assemblyName);
            return path == null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
