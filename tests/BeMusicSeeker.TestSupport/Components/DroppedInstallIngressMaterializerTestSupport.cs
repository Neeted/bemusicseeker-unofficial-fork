using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class DroppedInstallIngressMaterializerTestSupport
{
    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class MaterializerFixture : IDisposable
    {
        private readonly TestWorkspace workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        private readonly string root;
        private readonly string stableRoot;
        private readonly string managedRoot;
        private readonly string ingressParent;
        private readonly bool createIngressInsideSource;

        internal MaterializerFixture(bool createIngressInsideSource = false)
        {
            root = Path.Combine(workspace.RootDirectory, "DroppedInstallIngressMaterializerTests", Guid.NewGuid().ToString("N"));
            SystemTempRoot = Path.Combine(root, "system-temp");
            stableRoot = Path.Combine(root, "stable-root");
            managedRoot = Path.Combine(SystemTempRoot, "managed-session");
            ingressParent = Path.Combine(root, "managed-ingress");
            this.createIngressInsideSource = createIngressInsideSource;
            Directory.CreateDirectory(SystemTempRoot);
            Directory.CreateDirectory(stableRoot);
            Directory.CreateDirectory(managedRoot);
            Directory.CreateDirectory(ingressParent);
            Materializer = new DroppedInstallIngressMaterializer(
                SystemTempRoot,
                path => LongPathFileSystem.IsSameOrDescendantDirectoryPath(path, managedRoot),
                CreateIngressRoot,
                DeleteIngressRoot,
                getAttributes: GetAttributes);
        }

        internal DroppedInstallIngressMaterializer Materializer { get; }

        internal string SystemTempRoot { get; }

        internal string LastIngressRoot { get; private set; } = null!;

        internal string DestinationAncestor { get; set; } = null!;

        internal string? ReportedReparsePath { get; set; }

        internal int DeleteIngressRootCount { get; private set; }

        internal List<string> AttributeProbes { get; } = [];

        internal string CreateExternalFile(string relativePath, string contents)
        {
            string path = Path.Combine(SystemTempRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }

        internal string CreateExternalDirectory(string relativePath)
        {
            string path = Path.Combine(SystemTempRoot, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        internal string CreateStableFile(string relativePath, string contents)
        {
            string path = Path.Combine(stableRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }

        internal string CreateStableDirectory(string relativePath)
        {
            string path = Path.Combine(stableRoot, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        internal string CreateManagedFile(string relativePath, string contents)
        {
            string path = Path.Combine(managedRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }

        internal string CreateManagedDirectory(string relativePath)
        {
            string path = Path.Combine(managedRoot, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        private string CreateIngressRoot()
        {
            LastIngressRoot = createIngressInsideSource
                ? Path.Combine(DestinationAncestor!, "nested-ingress")
                : Path.Combine(ingressParent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(LastIngressRoot);
            return LastIngressRoot;
        }

        private void DeleteIngressRoot(string path)
        {
            DeleteIngressRootCount++;
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        private FileAttributes GetAttributes(string path)
        {
            AttributeProbes.Add(path);
            if (!string.IsNullOrWhiteSpace(ReportedReparsePath)
                && string.Equals(
                    LongPathFileSystem.NormalizePathForStorage(path),
                    LongPathFileSystem.NormalizePathForStorage(ReportedReparsePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return FileAttributes.Directory | FileAttributes.ReparsePoint;
            }
            return LongPathFileSystem.GetAttributes(path);
        }

        public void Dispose()
        {
            workspace.Dispose();
        }
    }

}
