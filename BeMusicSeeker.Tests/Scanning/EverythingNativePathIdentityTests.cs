using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class EverythingNativePathIdentityTests
{
    public TestContext? TestContext { get; set; }

    /// <summary>
    /// 実SDK getterから公開pack、managed decode、六面・三逆引き、健康度と通常推定へ渡し、親名と所有先の接続ずれを検出する。
    /// 条件表はnative側へ集約し、通常親子と絶対drive rootの接続代表だけを通す。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("ProcessIntegration")]
    public void ExecuteScan_PreservesDirectoryIdentityThroughDecodeHealthAndEstimation(bool driveRoot)
    {
        string? fixtureBase = Environment.GetEnvironmentVariable("BMS_EVERYTHING_PATH_IDENTITY_FIXTURE");
        if (string.IsNullOrWhiteSpace(fixtureBase))
        {
            Assert.Inconclusive("BMS_EVERYTHING_PATH_IDENTITY_FIXTUREで明示したnative試験DLLが必要です。");
        }
        Assert.IsTrue(Path.IsPathFullyQualified(fixtureBase), "fixture app baseは絶対パスで指定します。");
        string caseRoot = Path.Combine(fixtureBase, "cases", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(caseRoot, "native"));
        IntPtr control = IntPtr.Zero;
        EverythingNative? native = null;
        FixtureEnd? end = null;
        bool started = false;
        Exception? primaryFailure = null;
        try
        {
            string sdk = Path.Combine(fixtureBase, "native", "Everything3_x64.dll");
            string bridge = Path.Combine(fixtureBase, "native", "EverythingBridge_x64.dll");
            Assert.IsTrue(File.Exists(bridge), "指定したfixture bridgeがありません。");
            Assert.IsTrue(File.Exists(sdk), "指定したfixture SDKがありません。");
            Assert.AreEqual("3.0.0.9", FileVersionInfo.GetVersionInfo(sdk).FileVersion);
            Assert.AreEqual("BE25B01C73BBF359B50DDF30255133225F93B4BC40A8D208173319373BCDAA5C",
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sdk))), "private fixtureを使う固定SDKが異なります。");
            string caseSdk = Path.Combine(caseRoot, "native", "Everything3_x64.dll");
            string caseBridge = Path.Combine(caseRoot, "native", "EverythingBridge_x64.dll");
            File.Copy(sdk, caseSdk);
            File.Copy(bridge, caseBridge);
            control = NativeLibrary.Load(caseBridge);
            FixtureBegin begin = Marshal.GetDelegateForFunctionPointer<FixtureBegin>(NativeLibrary.GetExport(control, "PathIdentityFixtureBegin"));
            end = Marshal.GetDelegateForFunctionPointer<FixtureEnd>(NativeLibrary.GetExport(control, "PathIdentityFixtureEnd"));
            string root = driveRoot ? @"C:\" : Path.Combine(caseRoot, "library");
            string child = Path.Combine(root, "pack.ogg");
            if (driveRoot)
            {
                // 専用testhostの既存WorkingDirectoryを観測し、共有current directoryを変更しない。
                string workingDirectory = Environment.CurrentDirectory;
                Assert.AreEqual(@"C:\", Path.GetPathRoot(workingDirectory), "専用processはCドライブの非root作業場所から実行します。");
                Assert.AreNotEqual(root, workingDirectory);
            }
            // SDKの分離列C: + '\\' + nameが一重separatorのC:\root.bmsを生成する。
            Assert.AreEqual(0, begin(caseSdk, driveRoot ? @"C:" : root), "native fixtureの初期化に失敗しました。");
            started = true;
            native = new EverythingNative(ApplicationPathSnapshot.FromExecutablePath(Path.Combine(caseRoot, "BeMusicSeeker.exe")));
            ChartScanExecutionResult execution = native.ExecuteScan("chart", "audio", "image", "movie", "text");
            Assert.IsTrue(execution.Success, execution.NativeBridgeReason);
            Assert.IsTrue(execution.IsComplete);
            Assert.IsTrue(execution.NativeBridgeUsed);
            Assert.IsNotNull(execution.ResourceIndex);
            Assert.AreEqual("native_canonical", execution.ResourceIndex.Source);
            DirectoryResourceLookupCache cache = execution.ResourceIndex.DirectoryLookupCache;
            Assert.AreEqual(2, cache.Count);
            Assert.IsTrue(cache.IsFullReverseLookupBuilt);
            DirectoryResourceLookupCache.Entry rootEntry = cache.GetEntryOrNull(root);
            DirectoryResourceLookupCache.Entry childEntry = cache.GetEntryOrNull(child);
            Assert.IsNotNull(rootEntry);
            Assert.IsNotNull(childEntry);
            if (driveRoot)
            {
                Assert.IsNull(cache.GetEntryOrNull(@"C:"));
                Assert.IsTrue(Path.IsPathFullyQualified(root));
                Assert.AreEqual(root, Path.GetFullPath(root, @"C:\nonroot-identity-base"));
                CollectionAssert.Contains(execution.Result.ChartDirectories.ToArray(), root);
            }
            AssertHashes(rootEntry.AudioRelativePathHashes, @"audio.ogg\tone", @"pack.ogg\sound.jpg\tone");
            AssertHashes(rootEntry.ImageRelativePathHashes, @"audio.ogg\cover", @"pack.ogg\sound.jpg\cover");
            AssertHashes(rootEntry.MovieRelativePathHashes, @"audio.ogg\video", @"pack.ogg\sound.jpg\video");
            AssertHashes(rootEntry.SelfOwnedAudioRelativePathHashes, @"audio.ogg\tone");
            AssertHashes(rootEntry.SelfOwnedImageRelativePathHashes, @"audio.ogg\cover");
            AssertHashes(rootEntry.SelfOwnedMovieRelativePathHashes, @"audio.ogg\video");
            AssertHashes(childEntry.AudioRelativePathHashes, @"sound.jpg\tone");
            AssertHashes(childEntry.ImageRelativePathHashes, @"sound.jpg\cover");
            AssertHashes(childEntry.MovieRelativePathHashes, @"sound.jpg\video");
            AssertHashes(childEntry.SelfOwnedAudioRelativePathHashes, @"sound.jpg\tone");
            AssertHashes(childEntry.SelfOwnedImageRelativePathHashes, @"sound.jpg\cover");
            AssertHashes(childEntry.SelfOwnedMovieRelativePathHashes, @"sound.jpg\video");
            foreach ((string key, string directory) in new[] { (@"audio.ogg\", root), (@"pack.ogg\sound.jpg\", root), (@"sound.jpg\", child) })
            {
                CollectionAssert.AreEquivalent(new[] { directory }, cache.GetDirectoriesByAudioRelativeHash(Hash(key + "tone")).ToArray());
                CollectionAssert.AreEquivalent(new[] { directory }, cache.GetDirectoriesByImageRelativeHash(Hash(key + "cover")).ToArray());
                CollectionAssert.AreEquivalent(new[] { directory }, cache.GetDirectoriesByMovieRelativeHash(Hash(key + "video")).ToArray());
            }
            var context = new ResourceHealthLookupContext(cache);
            foreach (string raw in new[] { @"audio.ogg\tone.wav", @"audio.wav\tone.wav" })
            {
                ChartFile chart = ChartTestValues.Empty() with { Path = Path.Combine(root, "root.bms"), Resources = TestChartResources.Create([raw], []) };
                ResourceHealthMaintenanceSnapshot health = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(chart, context);
                Assert.AreEqual(1, health.WavFilesDefined);
                Assert.AreEqual(raw == @"audio.ogg\tone.wav" ? 1 : 0, health.WavFilesExisting);
                Assert.AreEqual(raw, chart.Resources.Single().RawPath);
            }
            Assert.AreEqual(0, context.FileExistsFallbackCount);
            ChartFile target = ChartTestValues.Empty() with
            {
                Path = Path.Combine(caseRoot, "pending", "target.bms"),
                Md5 = "target",
                RawTitle = "Song",
                RawArtist = "Artist",
                Artist = "Artist",
                Resources = TestChartResources.Create([@"audio.ogg\tone.wav"], [])
            };
            PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.BuildForLooseEntries([PackageChartEntry.FromChart(target)]);
            var service = new BmsLibraryInstallEstimationService(new BmsLibraryOptionsSnapshot());
            InstallEstimationResult estimate = service.EstimateInstallationDirectory(snapshot, cache, false, ChartInstallationEstimateMode.Normal,
                _ => InstallEstimationMetadataNormalizer.BuildProfile([("Song", "Artist", "base.bms")]));
            Assert.IsNotNull(estimate.SelectedCandidate);
            Assert.AreEqual(root, estimate.SelectedCandidate.DirectoryPath);
            Assert.IsTrue(Path.IsPathFullyQualified(estimate.SelectedCandidate.DirectoryPath));
            Assert.AreEqual(1, estimate.CandidateDirectoryCountAfterBroadFilter);
            TestContext?.WriteLine($"native decode: directories={cache.Count}, six-face hashes=15, reverse keys/pairs=9/9, raw bytes={execution.BridgeRawBufferBytes}, FS fallback={context.FileExistsFallbackCount}");
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            // ExecuteScanは同期で全worker終端・packed freeまで所有する。shutdown後にSDK保持参照を検査・解放する。
            var cleanupErrors = new List<Exception>();
            try { native?.Dispose(); } catch (Exception error) { cleanupErrors.Add(error); }
            try
            {
                if (started && end != null)
                {
                    Assert.AreEqual(0, end(), "native client/state/resultまたはSDK参照の回収に失敗しました。");
                }
            }
            catch (Exception error) { cleanupErrors.Add(error); }
            try { if (control != IntPtr.Zero) { NativeLibrary.Free(control); } } catch (Exception error) { cleanupErrors.Add(error); }
            try { Directory.Delete(caseRoot, recursive: true); } catch (Exception error) { cleanupErrors.Add(error); }
            if (cleanupErrors.Count > 0)
            {
                var error = new AggregateException("native fixture cleanup failed", cleanupErrors);
                if (primaryFailure == null)
                {
                    throw error;
                }

                primaryFailure.Data["PathIdentityFixtureCleanupFailure"] = error;
                TestContext?.WriteLine(error.ToString());
            }
        }
    }

    private static uint Hash(string literalKey) => ChartResourceKeyHash.GetLookupHash(literalKey);

    private static void AssertHashes(IEnumerable<uint> actual, params string[] literalKeys) =>
        CollectionAssert.AreEquivalent(literalKeys.Select(Hash).ToArray(), actual.ToArray());

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int FixtureBegin([MarshalAs(UnmanagedType.LPWStr)] string sdkPath, [MarshalAs(UnmanagedType.LPWStr)] string root);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FixtureEnd();
}
