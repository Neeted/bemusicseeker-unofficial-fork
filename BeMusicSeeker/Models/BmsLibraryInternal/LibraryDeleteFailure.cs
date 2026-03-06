using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class LibraryDeleteFailure
{
    public string Path { get; set; }

    public Exception Exception { get; set; }

    public bool IsDirectory { get; set; }
}
