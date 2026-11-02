using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Security.Principal;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Files;
using static BeMusicSeeker.TestSupport.Components.PackageInstallComponentTestSupport;
using static BeMusicSeeker.TestSupport.Components.PackageInstallWorkflowOwnerTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
[TestCategory("Suite.install-acl")]
public sealed partial class PackageInstallWorkflowOwnerTests
{
    /// <summary>case固有の元DACL・復旧結果・回収失敗を通常TRX出力へ記録します。</summary>
    public TestContext? TestContext { get; set; }

    /// <summary>過去保存された親directoryと内部fileの重複群が属性取得不能でも正常emptyへ変えず、旧2参照・DB・DST・入力bytesを保持して独立入力だけ登録します。ACL復旧後は同じ独立入力から登録専用整理を確認します。</summary>
    /// <remarks>専用containerのListDirectoryと対象rootのReadAttributesだけを非継承Denyにします。旧EntryExistsがfalseとなる実属性失敗と既知子の読取りを事前確認し、ACL非対応・変更権限不足・拒否未再現は専用環境の準備不足として失敗にします。開始済み操作を回収後、両ACLを必ず復旧してから専用rootを通常回収します。</remarks>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    public Task ProductionImport_AttributeDeniedOverlapKeepsSourceAndAllowsIndependentInput()
        => VerifyAttributeDeniedScopeAsync("Collection", childInput: false);

    /// <summary>supported拡張子を持つ実directoryのkind不能をfileへ落とさず、内部childdirectory入力の単独採用と親だけのmissing削除を防ぎます。</summary>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    public Task ProductionImport_AttributeDeniedSupportedDirectoryKeepsChildAndIndependentInput()
        => VerifyAttributeDeniedScopeAsync("Collection.bms", childInput: true);

    private async Task VerifyAttributeDeniedScopeAsync(string parentName, bool childInput)
    {
        var databaseScope = new ApplicationDatabaseScope();
        if (!OperatingSystem.IsWindows()) { Assert.Fail("Windows ACLによる実属性拒否を確認するケースです。"); }
        var port = new ObservingInstallMutationPort();
        var fixture = new AutomaticContinuationFixture(databaseScope, false, false, mutationPort: port);
        string caseRoot = Path.GetFullPath(fixture.Root);
        ExceptionDispatchInfo? primaryFailure = null;
        var cleanupFailures = new List<Exception>();
        bool rootRestored = false;
        bool containerRestored = false;
        bool operationTerminated = false;
        try
        {
            string container = Path.Combine(fixture.Root, "Private");
            // 空の専有directoryで通常保存APIによる継承状態を確定し、その後に検証入力を作ります。
            Directory.CreateDirectory(container);
            InitializeAccess(new DirectoryInfo(container), "container");
            string parentDirectory = Path.Combine(container, parentName);
            Directory.CreateDirectory(parentDirectory);
            InitializeAccess(new DirectoryInfo(parentDirectory), "root");
            string parent = fixture.WriteInput(Path.Combine("Private", parentName), "a.bms", "attribute-denied");
            string chart = Path.Combine(parent, "a.bms");
            string independent = fixture.WriteInput("Independent", "normal.bms", "independent-attribute-denied");
            string nested = childInput ? fixture.WriteInput(Path.Combine("Private", parentName, "Child"), "child.bms", "attribute-child") : chart;
            string nestedChart = childInput ? Path.Combine(nested, "child.bms") : chart;
            await fixture.InitializeAsync();
            // 現在の通常APIが重複を作るとはせず、過去保存状態を既存helperで用意します。
            var parentEntry = PackageChartEntry.FromPath(chart);
            var fileEntry = PackageChartEntry.FromPath(nestedChart);
            var oldParent = ChartPackage.FromChartEntries([parentEntry]);
            oldParent.path = parent;
            oldParent.delete_parent = true;
            var oldFile = ChartPackage.FromChartEntries([fileEntry]);
            oldFile.path = nested;
            oldFile.delete_parent = false;
            string fileOnlyDestination = Path.Combine(fixture.Root, "FileOnlyDestination");
            parentEntry.RestoreInstallDestinationState(new PackageChartInstallDestinationState(fixture.Destination, "Parent DST", "Parent Artist", [fixture.Destination]));
            fileEntry.RestoreInstallDestinationState(new PackageChartInstallDestinationState(fileOnlyDestination, "File DST", "File Artist", [fileOnlyDestination]));
            ChartPackage[] originals = [oldParent, oldFile];
            SeedLegacyPending(fixture, originals);
            Dictionary<ChartPackage, PackageChartInstallDestinationState> destinations = originals.ToDictionary(package => package, package => package.ChartEntries.Single().CaptureInstallDestinationState());
            var gateway = new BmsLibraryDbGateway(fixture.Library.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), fixture.Database);
            var beforeRows = gateway.LoadInstallPackages().ToDictionary(row => row.path, row => row.delete_parent, StringComparer.Ordinal);
            Dictionary<string, byte[]> preserved = Directory.GetFiles(parent, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            byte[] independentBytes = File.ReadAllBytes(Path.Combine(independent, "normal.bms"));
            var containerInfo = new DirectoryInfo(container);
            var rootInfo = new DirectoryInfo(parent);
            byte[]? containerAcl = null;
            byte[]? rootAcl = null;
            bool containerChanged = false;
            bool rootChanged = false;
            try
            {
                Assert.IsTrue((LongPathFileSystem.GetAttributes(parent) & FileAttributes.Directory) != 0);
                try
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    SecurityIdentifier user = identity.User ?? throw new AssertFailedException("実行ユーザーのSIDを取得できません。");
                    containerAcl = containerInfo.GetAccessControl().GetSecurityDescriptorBinaryForm();
                    rootAcl = rootInfo.GetAccessControl().GetSecurityDescriptorBinaryForm();
                    WriteDescriptor("container", "saved", new RawSecurityDescriptor(containerAcl, 0));
                    WriteDescriptor("root", "saved", new RawSecurityDescriptor(rootAcl, 0));
                    containerRestored = true;
                    rootRestored = true;
                    var deniedContainer = new DirectorySecurity();
                    deniedContainer.SetSecurityDescriptorBinaryForm(containerAcl, AccessControlSections.Access);
                    deniedContainer.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ListDirectory,
                        InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
                    containerChanged = true;
                    containerRestored = false;
                    containerInfo.SetAccessControl(deniedContainer);
                    var deniedRoot = new DirectorySecurity();
                    deniedRoot.SetSecurityDescriptorBinaryForm(rootAcl, AccessControlSections.Access);
                    deniedRoot.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadAttributes,
                        InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
                    rootChanged = true;
                    rootRestored = false;
                    rootInfo.SetAccessControl(deniedRoot);
                }
                catch (Exception failure) when (failure is UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
                { throw new InvalidOperationException("専用rootのACL設定が非対応または権限不足のため実行できません。", failure); }

                Exception? attributeFailure = null;
                try { _ = LongPathFileSystem.GetAttributes(parent); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { attributeFailure = failure; }
                if (attributeFailure == null || LongPathFileSystem.IsNotFoundFailure(attributeFailure) || LongPathFileSystem.EntryExists(parent))
                { Assert.Fail("旧存在確認がfalseとなる非NotFound属性拒否を再現できないため実行できません: " + attributeFailure); }
                CollectionAssert.AreEqual(preserved[chart], File.ReadAllBytes(chart), "traverseと既知の子読取りを保持します。");
                if (childInput)
                {
                    Assert.IsTrue((LongPathFileSystem.GetAttributes(nested) & FileAttributes.Directory) != 0,
                        "親のkind不能を子directoryの属性拒否で代替しません。");
                    CollectionAssert.AreEquivalent(new[] { nestedChart }, Directory.GetFileSystemEntries(nested),
                        "非継承Deny中もChildを列挙できます。");
                    CollectionAssert.AreEqual(preserved[nestedChart], File.ReadAllBytes(nestedChart),
                        "非継承Deny中もChildの譜面を読み取れます。");
                }
                string[] inputs = childInput ? [nested, independent] : [independent];
                // 一般の出力順を固定せず、source順と同物理aliasを変えて保護・候補集合だけ確認します。
                var alias = new ChartPackage { path = Path.Combine(parent, "."), delete_parent = false };
                ChartPackage[][] snapshots = [originals, [.. originals.Reverse()], [alias, .. originals], [oldParent], [alias]];
                foreach (ChartPackage[] snapshot in snapshots)
                {
                    AutoInstallWorkflowResult prepared = new BmsLibraryPackageInstallService().PrepareAutoInstallWorkflow(inputs, snapshot, [fixture.LibraryRoot], _ => false);
                    Assert.IsTrue(prepared.DiscoveryFailures.Any(failure => (failure is IOException or UnauthorizedAccessException)
                        && !LongPathFileSystem.IsNotFoundFailure(failure) && failure.Message.Contains(parent, StringComparison.OrdinalIgnoreCase)));
                    Assert.IsTrue(snapshot.All(source => !prepared.PendingPackagesToRemove.Contains(source)));
                    CollectionAssert.AreEquivalent(new[] { independent }, prepared.DiscoveredPackages.Select(package => package.path).ToArray());
                    Assert.IsFalse(prepared.AutoInstallCandidates.Any(package => IsWithin(package.path, parent)));
                }
                PackageInstallCommandResult? previous = port.LatestResult;
                Assert.IsTrue(fixture.Owner.Enqueue(inputs));
                await fixture.Owner.WaitForIdleAsync();
                Assert.IsNotNull(port.LatestResult);
                Assert.AreNotSame(previous, port.LatestResult);
                Assert.IsTrue(port.LatestResult.HasRequiredFailure);
                Assert.IsTrue(port.LatestResult.HasDurableCommit, "独立正常directoryの登録は確定します。");
                Assert.IsTrue(port.LatestResult.WorkflowFailures.Any(failure => (failure is IOException or UnauthorizedAccessException)
                    && !LongPathFileSystem.IsNotFoundFailure(failure) && failure.Message.Contains(parent, StringComparison.OrdinalIgnoreCase)));
                foreach (ChartPackage source in originals)
                {
                    Assert.AreSame(source, fixture.Library.ChartPackagesPending.Single(package => package.path == source.path));
                    Assert.AreEqual(beforeRows[source.path], source.delete_parent);
                    AssertDestination(destinations[source], source.ChartEntries.Single());
                    ChartPackage row = gateway.LoadInstallPackages().Single(package => package.path == source.path);
                    Assert.AreEqual(beforeRows[source.path], row.delete_parent);
                }
                Assert.IsTrue(fixture.Library.ChartPackagesPending.Any(package => package.path == independent));
                CollectionAssert.AreEquivalent(new[] { parent, nested, independent }, gateway.LoadInstallPackages().Select(package => package.path).ToArray());
                Assert.AreEqual(0, fixture.Library.ChartPackagesInstalled.Count);
            }
            catch (Exception failure) { primaryFailure = ExceptionDispatchInfo.Capture(failure); }
            finally
            {
                try { await fixture.Owner.WaitForIdleAsync(); operationTerminated = true; }
                catch (Exception failure) { RecordCleanupFailure("operation-terminal", failure); }
                if (operationTerminated)
                {
                    if (rootChanged && rootAcl is not null)
                    { rootRestored = TryCleanup("root-restore", () => RestoreSavedAccess("root", rootInfo, rootAcl)); }
                    if (containerChanged && containerAcl is not null)
                    { containerRestored = TryCleanup("container-restore", () => RestoreSavedAccess("container", containerInfo, containerAcl)); }
                    TryCleanup("root-attributes", () => { _ = LongPathFileSystem.GetAttributes(parent); });
                    TryCleanup("container-enumeration", () => CollectionAssert.Contains(Directory.GetFileSystemEntries(container), parent,
                        "containerの列挙権限が実復旧してから回収します。"));
                    foreach ((string path, byte[] bytes) in preserved)
                    { TryCleanup("source-bytes: " + path, () => CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path))); }
                    TryCleanup("independent-bytes", () => CollectionAssert.AreEqual(independentBytes, File.ReadAllBytes(Path.Combine(independent, "normal.bms"))));
                }
                else
                {
                    WriteDiagnostic("root/container restore-attempt=false: operation terminal unconfirmed");
                }
            }
            // 本体・復旧・確認のどれかが失敗した場合、後続の新要求で失敗を隠しません。
            if (primaryFailure is null && cleanupFailures.Count == 0)
            {
                Assert.IsTrue(fixture.Owner.Enqueue([independent]));
                await fixture.Owner.WaitForIdleAsync();
                Assert.IsNotNull(port.LatestResult);
                Assert.IsFalse(port.LatestResult.HasRequiredFailure);
                if (childInput)
                {
                    CollectionAssert.AreEquivalent(new[] { chart, nested, independent }, gateway.LoadInstallPackages().Select(package => package.path).ToArray());
                    Assert.IsFalse(fixture.Library.ChartPackagesPending.Any(package => package.path == parent));
                    ChartPackage direct = fixture.Library.ChartPackagesPending.Single(package => package.path == chart);
                    Assert.IsTrue(string.IsNullOrEmpty(direct.ChartEntries.Single().Chart.InstallDestination), "親から分割した単体へ旧親DSTをコピーしません。");
                    ChartPackage retainedChild = fixture.Library.ChartPackagesPending.Single(package => package.path == nested);
                    Assert.AreEqual(beforeRows[nested], retainedChild.delete_parent);
                    AssertDestination(destinations[oldFile], retainedChild.ChartEntries.Single());
                }
                else
                {
                    ChartPackage repaired = fixture.Library.ChartPackagesPending.Single(package => package.path == parent);
                    Assert.IsFalse(fixture.Library.ChartPackagesPending.Contains(oldFile));
                    Assert.AreEqual(beforeRows[parent], repaired.delete_parent);
                    AssertDestination(destinations[oldParent], repaired.ChartEntries.Single());
                    Assert.AreNotEqual(destinations[oldFile].Destination, repaired.ChartEntries.Single().Chart.InstallDestination,
                        "内部fileの宛先を、同unit親の指定がある整理先へ拡散しません。");
                    CollectionAssert.AreEquivalent(new[] { parent, independent }, gateway.LoadInstallPackages().Select(package => package.path).ToArray());
                }
                foreach ((string path, byte[] bytes) in preserved) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path)); }
            }
        }
        catch (Exception failure)
        {
            if (primaryFailure is null) { primaryFailure = ExceptionDispatchInfo.Capture(failure); }
            else { RecordCleanupFailure("case-terminal", failure); }
        }
        finally
        {
            // Dispose自身の失敗でも媒体を先に消さないため、終端確認後の回収だけをcaseが所有します。
            fixture.PreserveArtifacts();
            bool fixtureDisposed = false;
            try { await fixture.DisposeAsync(); fixtureDisposed = true; }
            catch (Exception failure) { RecordCleanupFailure("fixture-dispose", failure); }
            if (rootRestored && containerRestored && operationTerminated && fixtureDisposed && cleanupFailures.Count == 0)
            {
                TryCleanup("workspace-delete", () =>
                {
                    string runRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(TestRunDirectory.RootDirectory));
                    if (string.Equals(caseRoot, Path.GetPathRoot(caseRoot), StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(Path.GetDirectoryName(caseRoot), runRoot, StringComparison.OrdinalIgnoreCase))
                    { throw new InvalidOperationException("回収対象が専有run直下のcaseではありません: " + caseRoot); }
                    Directory.Delete(caseRoot, recursive: true);
                });
            }
            else
            {
                WriteDiagnostic("workspace preserved: " + caseRoot);
            }
        }
        if (primaryFailure is not null)
        {
            if (cleanupFailures.Count > 0)
            { primaryFailure.SourceException.Data["AclCleanupFailures"] = new AggregateException(cleanupFailures); }
            primaryFailure.Throw();
        }
        if (cleanupFailures.Count > 0) { throw new AggregateException("ACLケースの後片付けに失敗しました。", cleanupFailures); }

        bool TryCleanup(string phase, Action action)
        {
            try { action(); return true; }
            catch (Exception failure) { RecordCleanupFailure(phase, failure); return false; }
        }

        void RecordCleanupFailure(string phase, Exception failure)
        {
            cleanupFailures.Add(new InvalidOperationException(phase, failure));
            WriteDiagnostic(phase + ": " + failure);
        }

        void WriteDiagnostic(string message)
        {
            try { TestContext?.WriteLine(message); }
            catch (Exception failure) { cleanupFailures.Add(new InvalidOperationException("ACL診断の記録に失敗しました。", failure)); }
        }

        void WriteDescriptor(string role, string phase, RawSecurityDescriptor descriptor)
            => WriteDiagnostic(role + " " + phase + " flags=" + descriptor.ControlFlags
                + " access=" + descriptor.GetSddlForm(AccessControlSections.Access));

        void InitializeAccess(DirectoryInfo directory, string role)
        {
            FileAttributes attributes = LongPathFileSystem.GetAttributes(directory.FullName);
            Assert.IsTrue((attributes & FileAttributes.Directory) != 0);
            Assert.IsFalse((attributes & FileAttributes.ReparsePoint) != 0, "初期確定対象は専有tree内の実directoryです。");
            Assert.AreEqual(0, Directory.GetFileSystemEntries(directory.FullName).Length,
                "入力bytesを配置する前の空directoryで初期ACLを確定します。");
            byte[] descriptor = directory.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
            var initialized = new DirectorySecurity();
            initialized.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access);
            directory.SetAccessControl(initialized);
            Assert.AreEqual(attributes, LongPathFileSystem.GetAttributes(directory.FullName));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(directory.FullName).Length,
                "初期確定後も空directoryの列挙を維持します。");
            WriteDiagnostic(role + " initial-access-persisted=true");
        }

        static bool IsWithin(string path, string root) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        static void AssertDestination(PackageChartInstallDestinationState expected, PackageChartEntry entry)
        {
            Assert.AreEqual(expected.Destination, entry.Chart.InstallDestination);
            Assert.AreEqual(expected.Title, entry.Chart.InstallDestinationTitle);
            Assert.AreEqual(expected.Artist, entry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEqual(expected.Suggestions.ToArray(), entry.Chart.InstallDestinationSuggestions.ToArray());
        }

        void RestoreSavedAccess(string role, DirectoryInfo directory, byte[] descriptor)
        {
            // GetAccessControlで取得した未変更objectのPersistはAccessを書きません。
            // 新objectへAccessを明示設定して変更済みにし、Denyを実際に取り除きます。
            var saved = new RawSecurityDescriptor(descriptor, 0);
            string expected = saved.GetSddlForm(AccessControlSections.Access);
            WriteDescriptor(role, "restore-attempt", saved);
            var restored = new DirectorySecurity();
            restored.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access);
            directory.SetAccessControl(restored);
            var actualDescriptor = new RawSecurityDescriptor(directory.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm(), 0);
            string actual = actualDescriptor.GetSddlForm(AccessControlSections.Access);
            WriteDescriptor(role, "restored", actualDescriptor);
            Assert.AreEqual(expected, actual, "保存したDACLの実復旧を確認します: " + directory.FullName);
            WriteDiagnostic(role + " original-dacl-match=true");
        }
    }

}
