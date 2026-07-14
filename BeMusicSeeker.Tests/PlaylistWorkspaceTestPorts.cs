using System;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Tests;

internal static class PlaylistWorkspaceTestPorts
{
    internal static PlaylistUrlAcquisitionWorkflow CreateUrlAcquisitionWorkflow(Action<string>? log = null)
    {
        return new PlaylistUrlAcquisitionWorkflow(
            new AppPlaylistUrlDownloadGateway(),
            log ?? (_ => { }));
    }

    internal static PlaylistExternalPackageLookupService CreateExternalPackageLookupService()
    {
        return PlaylistExternalPackageLookupService.CreateDefault();
    }

    internal static Func<PlaylistUrlAcquisitionOptionsSnapshot> UrlAcquisitionOptionsProvider =>
        () => new PlaylistUrlAcquisitionOptionsSnapshot();

    internal static Func<bool> InactiveInstallQueueProvider => () => false;

    internal static Action<Exception, string> ExternalPlaylistImportWarningLog => (_, _) => { };

    internal static Action<string> ExternalPlaylistImportInfoLog => _ => { };

    internal static Action<Exception, string> BeatorajaTableUrlImportWarningLog => (_, _) => { };

    internal static Action<string> BeatorajaTableUrlImportInfoLog => _ => { };
}
