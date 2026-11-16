using System.Collections.Generic;
using System.ComponentModel;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    /// <summary>Workspaceの値と更新通知を画面寿命だけ接続する一時セッションを作成します。</summary>
    internal PlaylistImportDialogViewModel CreatePlaylistImportDialogSession()
    {
        var session = new PlaylistImportDialogViewModel(TryStartPlaylistImport);
        void UpdateCatalog()
        {
            List<PlaylistImportCandidate> values = [];
            void Visit(BMSTableSimpleCategorized node)
            {
                if (node.url?.IsAbsoluteUri == true)
                {
                    values.Add(new(node.url, node.name, node.symbol ?? "", node.tag1 ?? "", node.tag2 ?? ""));
                }

                if (node.Children != null)
                {
                    foreach (BMSTableSimpleCategorized child in node.Children)
                    {
                        Visit(child);
                    }
                }
            }
            if (BMSExternalTableListExt != null)
            {
                Visit(BMSExternalTableListExt);
            }

            session.UpdateCatalog(values, ExternalTableListState);
        }
        void Changed(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(BMSExternalTableListExt) or nameof(ExternalTableListState))
            {
                UpdateCatalog();
            }
        }
        void CatalogChanged(object sender, PlaylistCatalogChangedEventArgs e) => session.UpdateExisting(CapturePlaylistImportExistingTables());
        void CultureChanged(object sender, PropertyChangedEventArgs e) => session.RefreshCulture();
        UpdateCatalog();
        session.UpdateExisting(CapturePlaylistImportExistingTables());
        ResourceService.Current.PropertyChanged += CultureChanged;
        PropertyChanged += Changed;
        PlaylistCatalogChanged += CatalogChanged;
        session.SetUnsubscribe(() => { PropertyChanged -= Changed; PlaylistCatalogChanged -= CatalogChanged; ResourceService.Current.PropertyChanged -= CultureChanged; });
        return session;
    }
}
