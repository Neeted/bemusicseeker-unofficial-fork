using System;

namespace BeMusicSeeker.ViewModels;

internal sealed class RegularChartFolderEditRequestedEventArgs : EventArgs
{
    internal RegularChartFolderEditRequestedEventArgs(RenameChartFolderRequest request, string folderName)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        FolderName = folderName ?? string.Empty;
    }

    internal RenameChartFolderRequest Request { get; }

    internal string FolderName { get; }
}
