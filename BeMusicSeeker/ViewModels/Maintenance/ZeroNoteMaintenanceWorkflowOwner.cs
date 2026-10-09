using System;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// ノート警告再確認の実行確定時にLを判定し、入力捕捉から必須公開まで保持します。
/// </summary>
internal sealed class ZeroNoteMaintenanceWorkflowOwner
{
    private readonly Func<BMSLibrary> libraryProvider;
    private readonly ChartFileOperationSynchronizer chartFileOperations;
    private readonly Action<BMSLibrary, LibraryFileMutationCapability> recheck;

    internal ZeroNoteMaintenanceWorkflowOwner(
        Func<BMSLibrary> libraryProvider,
        ChartFileOperationSynchronizer chartFileOperations,
        Action<BMSLibrary, LibraryFileMutationCapability> recheck = null)
    {
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        this.chartFileOperations = chartFileOperations ?? throw new ArgumentNullException(nameof(chartFileOperations));
        this.recheck = recheck ?? ((library, capability) => library.RecheckZeroNoteWarnings(capability));
    }

    /// <summary>入力捕捉・worker起動より前にLを判定します。競合は未開始でfalse、受理は公開終端まで待ちます。</summary>
    internal async Task<bool> RecheckAsync()
    {
        if (!chartFileOperations.TryEnter(out IDisposable admission)) { return false; }
        using (admission)
        using (LibraryFileMutationCapability capability = chartFileOperations.CreateMutationCapability(admission))
        {
            BMSLibrary library = libraryProvider();
            if (library == null) { return false; }
            await Task.Run(() => recheck(library, capability)).ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>同期呼出元の再確認をLで非待機受理し、必須公開まで保持します。</summary>
    internal bool Recheck()
    {
        if (!chartFileOperations.TryEnter(out IDisposable admission)) { return false; }
        using (admission)
        using (LibraryFileMutationCapability capability = chartFileOperations.CreateMutationCapability(admission))
        {
            BMSLibrary library = libraryProvider();
            if (library == null) { return false; }
            recheck(library, capability);
            return true;
        }
    }
}
