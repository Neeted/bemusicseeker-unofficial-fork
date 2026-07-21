using System;
using System.Threading.Tasks;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Serializes zero-note warning rechecks with other physical chart-file operations.
/// </summary>
internal sealed class ZeroNoteMaintenanceWorkflowOwner
{
    private readonly Func<BMSLibrary> libraryProvider;
    private readonly ChartFileOperationSynchronizer chartFileOperations;
    private readonly Action<BMSLibrary> recheck;

    internal ZeroNoteMaintenanceWorkflowOwner(
        Func<BMSLibrary> libraryProvider,
        ChartFileOperationSynchronizer chartFileOperations,
        Action<BMSLibrary> recheck = null)
    {
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        this.chartFileOperations = chartFileOperations ?? throw new ArgumentNullException(nameof(chartFileOperations));
        this.recheck = recheck ?? (library => library.RecheckZeroNoteWarnings());
    }

    internal Task<bool> RecheckAsync()
    {
        return Task.Run(Recheck);
    }

    internal bool Recheck()
    {
        using (chartFileOperations.Enter())
        {
            BMSLibrary library = libraryProvider();
            if (library == null)
            {
                return false;
            }
            recheck(library);
            return true;
        }
    }
}
