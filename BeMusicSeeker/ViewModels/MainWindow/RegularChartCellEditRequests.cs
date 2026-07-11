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

internal sealed class RegularChartInstallDestinationEditRequestedEventArgs : EventArgs
{
    internal RegularChartInstallDestinationEditRequestedEventArgs(
        PendingInstallDestinationEditRequest request,
        string destinationDirectory)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        DestinationDirectory = destinationDirectory ?? string.Empty;
    }

    internal PendingInstallDestinationEditRequest Request { get; }

    internal string DestinationDirectory { get; }
}
