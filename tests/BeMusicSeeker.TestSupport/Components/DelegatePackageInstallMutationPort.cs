using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>導入ownerの受付・進捗・完了を維持し、物理導入の結果だけをcaseの固定delegateへ委譲します。</summary>
internal sealed class DelegatePackageInstallMutationPort : IPackageInstallMutationPort
{
    private readonly Func<BMSLibrary, IEnumerable<string>, CancellationToken, IPackageInstallProgressWriter, PackageInstallCommandResult> install;

    /// <summary>実ownerから受け取るlibrary・入力path・取消・進捗を加工せず渡す導入境界を接続します。</summary>
    internal DelegatePackageInstallMutationPort(
        Func<BMSLibrary, IEnumerable<string>, CancellationToken, IPackageInstallProgressWriter, PackageInstallCommandResult> install)
    {
        this.install = install ?? throw new ArgumentNullException(nameof(install));
    }

    public Task<PackageInstallCommandResult> InstallWithProgressAsync(
        BMSLibrary library,
        IEnumerable<string> installPaths,
        CancellationToken token,
        IPackageInstallProgressWriter progressWriter, IReadOnlySet<string>? fullySelectedDirectories = null)
    {
        return Task.FromResult(install(library, installPaths, token, progressWriter));
    }
}
