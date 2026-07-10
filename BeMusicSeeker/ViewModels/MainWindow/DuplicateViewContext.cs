namespace BeMusicSeeker.ViewModels;

internal sealed class DuplicateViewContext
{
    internal DuplicateViewContextKind Kind { get; }

    internal string Value { get; }

    private DuplicateViewContext(DuplicateViewContextKind kind, string value)
    {
        Kind = kind;
        Value = value;
    }

    internal static DuplicateViewContext ForGroup(string header)
    {
        return new DuplicateViewContext(DuplicateViewContextKind.GroupHeader, header);
    }

    internal static DuplicateViewContext ForFolder(string folderPath)
    {
        return new DuplicateViewContext(DuplicateViewContextKind.FolderPath, folderPath);
    }
}

internal enum DuplicateViewContextKind
{
    GroupHeader,
    FolderPath
}
