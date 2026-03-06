using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class FolderAutoRenamePlan
{
    public string SourceDirectory { get; set; }

    public string DestinationDirectory { get; set; }

    public Exception FailureException { get; set; }
}
