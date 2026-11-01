using System;
using System.Collections.Generic;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Files;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <remarks>専有ストアと専用XMLで検証し、返された文化圏の表示リソースへの実適用はAppの起動検証へ分担します。</remarks>
[TestClass]
public sealed class ApplicationSettingsLifecycleTests
{
    private TestWorkspace? ownedWorkspace;

    /// <summary>初期化済みのケース専有媒体だけを返し、constructorでは領域を作りません。</summary>
    private TestWorkspace CaseWorkspace => ownedWorkspace
        ?? throw new InvalidOperationException("ケースの可変媒体はTestInitialize後に利用します。");

    /// <summary>ケースのconstructorと既存field準備が成功した後に可変媒体を所有します。</summary>
    [TestInitialize]
    public void InitializeWorkspace() => ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>ケース固有の処理と既存finallyが終わった後に、開始済みの所有媒体だけを回収します。</summary>
    [TestCleanup]
    public void CleanupWorkspace() => ownedWorkspace?.Dispose();

    /// <summary>実 PowerShell が生成した旧設定を新 wrapper で読み、型付きバージョンと XML 特殊文字を含む値の互換読込みと既存起動判定を確認します。</summary>
    [TestMethod]
    [TestCategory("ProcessIntegration")]
    public void LegacySettingsFixtureGeneratorPreservesTypedVersionAndScalarValues()
    {
        string directory = Path.Combine(CaseWorkspace.RootDirectory, "BmsLegacySettingsFixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "user.config");
        try
        {
            RunLegacySettingsFixtureGenerator(path);

            Settings settings = TestSettingsFactory.OpenSettings(path);
            SerializableVersion version = settings.AssemblyVersion;
            Assert.IsNotNull(version);
            Assert.AreEqual(17, version.Major);
            Assert.AreEqual(23, version.Minor);
            Assert.AreEqual(41, version.Build);
            Assert.AreEqual(59, version.Revision);
            Assert.AreEqual("ja-JP", settings.Lang);
            Assert.AreEqual("Light", settings.AppearanceTheme);
            Assert.IsFalse(settings.OperationModeLR2DB);
            Assert.AreEqual("C:\\譜面 & <fixture>", settings.BMSRootPath);
            Assert.AreEqual("C:\\譜面 & <fixture>\\install", settings.BMSInstallDir);
            Assert.AreEqual(
                new Uri("https://example.invalid/?a=1&b=<fixture>"),
                settings.TableListURL);

            var lifecycle = new ApplicationSettingsLifecycle(
                settingsStore: new FileSettingsStore(settings),
                normalizeSettings: () => { });
            ApplicationSettingsInitializationResult result = lifecycle.Initialize(
                ["ja-JP", "en-US"],
                () => new SerializableVersion(17, 23, 41, 59));
            Assert.IsFalse(result.FirstStartup);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void RunLegacySettingsFixtureGenerator(string path)
    {
        string repositoryRoot = PowerShellTestProcess.FindRepositoryRoot();
        PowerShellTestResult result = PowerShellTestProcess.Run(
            repositoryRoot,
            """
            $ErrorActionPreference = 'Stop'
            . $env:BMS_SETTINGS_FIXTURE_SCRIPT
            $settings = @{
                AssemblyVersion = '17.23.41.59'
                Lang = 'ja-JP'
                AppearanceTheme = 'Light'
                OperationModeLR2DB = 'False'
                BMSRootPath = 'C:\譜面 & <fixture>'
                BMSInstallDir = 'C:\譜面 & <fixture>\install'
                TableListURL = 'https://example.invalid/?a=1&b=<fixture>'
            }
            Write-LegacyConfig -Path $env:BMS_SETTINGS_FIXTURE_PATH -Settings $settings
            """,
            "Legacy settings fixture",
            new Dictionary<string, string>
            {
                ["BMS_SETTINGS_FIXTURE_SCRIPT"] = Path.Combine(
                    repositoryRoot,
                    "scripts",
                    "acceptance-settings-fixture.ps1"),
                ["BMS_SETTINGS_FIXTURE_PATH"] = path
            });
        Assert.IsTrue(
            string.IsNullOrWhiteSpace(result.Error),
            $"Legacy settings fixture generator wrote stderr: {result.Error}");
    }
}
