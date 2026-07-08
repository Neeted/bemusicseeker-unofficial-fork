using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Exposes the resource-health input mutation window needed by extracted BMSLibrary workflow coordinators.
/// </summary>
internal interface IResourceHealthInputMutationScope : IDisposable
{
    /// <summary>
    /// Gets the input version captured before the mutation window started.
    /// </summary>
    int BaseInputVersion { get; }

    /// <summary>
    /// Gets whether the resource-health index was current at the base input version.
    /// </summary>
    bool BaseIndexCurrent { get; }

    /// <summary>
    /// Gets the input version observed after the mutation window is disposed.
    /// </summary>
    int TargetInputVersion { get; }
}
