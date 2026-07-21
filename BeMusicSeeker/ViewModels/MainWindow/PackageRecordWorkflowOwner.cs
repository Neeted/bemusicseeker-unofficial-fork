using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal interface IPackageRecordMutationPresentation
{
    void BeginActivity();

    void BeginRefreshSuppression();

    void EndRefreshSuppression();

    void EndActivity();
}

internal interface IPackageRecordStore
{
    void RemoveAll(BMSLibrary library, DeleteInstallPackageRecordsKind kind);

    void RemovePackages(
        BMSLibrary library,
        DeleteInstallPackageRecordsKind kind,
        IReadOnlyList<ChartPackage> packages);

    IReadOnlyList<ChartPackage> ResolvePackages(
        BMSLibrary library,
        DeleteInstallPackageRecordsKind kind,
        IReadOnlyList<ChartOperationTarget> targets);
}

internal sealed class PackageRecordWorkflowOwner
{
    private readonly Func<BMSLibrary> libraryProvider;
    private readonly ChartFileOperationSynchronizer chartFileOperations;
    private readonly IPackageRecordMutationPresentation presentation;
    private readonly IPackageRecordStore store;

    internal PackageRecordWorkflowOwner(
        Func<BMSLibrary> libraryProvider,
        ChartFileOperationSynchronizer chartFileOperations,
        IPackageRecordMutationPresentation presentation,
        IPackageRecordStore store = null)
    {
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        this.chartFileOperations = chartFileOperations ?? throw new ArgumentNullException(nameof(chartFileOperations));
        this.presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        this.store = store ?? new BmsLibraryPackageRecordStore();
    }

    internal Task RemoveAllAsync(DeleteInstallPackageRecordsKind kind)
    {
        ValidateKind(kind);
        return Task.Run(() => Execute(library => store.RemoveAll(library, kind)));
    }

    internal Task RemovePackagesAsync(
        DeleteInstallPackageRecordsKind kind,
        IEnumerable<ChartPackage> packages)
    {
        ValidateKind(kind);
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        IReadOnlyList<ChartPackage> packageSnapshot = [.. packages.Where(package => package != null)];
        return Task.Run(() => Execute(library => store.RemovePackages(library, kind, packageSnapshot)));
    }

    internal Task RemoveSelectionAsync(DeleteInstallPackageRecordsRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        return Task.Run(() => Execute(library =>
        {
            IReadOnlyList<ChartPackage> packages = store.ResolvePackages(
                library,
                request.Kind,
                request.Targets);
            store.RemovePackages(library, request.Kind, packages);
        }));
    }

    private void Execute(Action<BMSLibrary> mutation)
    {
        BMSLibrary library = libraryProvider();
        if (library == null)
        {
            return;
        }
        BMSLibrary.OperationDialogScope dialogScope = null;
        bool activityStarted = false;
        try
        {
            dialogScope = library.BeginOperationDialogScope();
            activityStarted = true;
            presentation.BeginActivity();
            using (chartFileOperations.Enter())
            {
                bool suppressionStarted = false;
                try
                {
                    suppressionStarted = true;
                    presentation.BeginRefreshSuppression();
                    mutation(library);
                }
                finally
                {
                    if (suppressionStarted)
                    {
                        presentation.EndRefreshSuppression();
                    }
                }
            }
        }
        finally
        {
            if (activityStarted)
            {
                presentation.EndActivity();
            }
            dialogScope?.Dispose();
            dialogScope?.Flush();
        }
    }

    private static void ValidateKind(DeleteInstallPackageRecordsKind kind)
    {
        if (kind != DeleteInstallPackageRecordsKind.Pending
            && kind != DeleteInstallPackageRecordsKind.Installed)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported package record kind.");
        }
    }
}

internal sealed class BmsLibraryPackageRecordStore : IPackageRecordStore
{
    public void RemoveAll(BMSLibrary library, DeleteInstallPackageRecordsKind kind)
    {
        if (kind == DeleteInstallPackageRecordsKind.Pending)
        {
            library.RemovePendingPackagesAll();
        }
        else
        {
            library.RemoveInstalledPackageRecordsAll();
        }
    }

    public void RemovePackages(
        BMSLibrary library,
        DeleteInstallPackageRecordsKind kind,
        IReadOnlyList<ChartPackage> packages)
    {
        if (kind == DeleteInstallPackageRecordsKind.Pending)
        {
            library.RemovePendingPackages(packages);
        }
        else
        {
            library.RemoveInstalledPackageRecords(packages);
        }
    }

    public IReadOnlyList<ChartPackage> ResolvePackages(
        BMSLibrary library,
        DeleteInstallPackageRecordsKind kind,
        IReadOnlyList<ChartOperationTarget> targets)
    {
        IEnumerable<ChartPackage> source = kind == DeleteInstallPackageRecordsKind.Pending
            ? library.ChartPackagesPending
            : library.ChartPackagesInstalled;
        source ??= Enumerable.Empty<ChartPackage>();
        return [.. targets
            .Select(target => source.FirstOrDefault(package => ContainsChartTarget(package, target)))
            .Where(package => package != null)
            .Distinct()];
    }

    private static bool ContainsChartTarget(ChartPackage package, ChartOperationTarget target)
    {
        if (package == null || target?.Chart == null)
        {
            return false;
        }
        if (target.PackageEntry != null)
        {
            return (package.ChartEntries ?? []).Any(entry => entry?.IsSameChartTarget(target.PackageEntry) == true);
        }
        return (package.ChartEntries ?? []).Any(entry => entry?.IsSameChartTarget(target.Chart) == true);
    }
}

internal sealed class NoOpPackageRecordMutationPresentation : IPackageRecordMutationPresentation
{
    public void BeginActivity()
    {
    }

    public void BeginRefreshSuppression()
    {
    }

    public void EndRefreshSuppression()
    {
    }

    public void EndActivity()
    {
    }
}
