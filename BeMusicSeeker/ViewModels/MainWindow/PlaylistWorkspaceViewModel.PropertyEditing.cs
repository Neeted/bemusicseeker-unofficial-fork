using System;
using System.Threading.Tasks;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private PlaylistPropertySaveService propertySaveService;

    private PlaylistPropertyDialogViewModel activePropertyDialog;

    public PlaylistPropertyDialogViewModel ActivePropertyDialog
    {
        get => activePropertyDialog;
        private set
        {
            if (!ReferenceEquals(activePropertyDialog, value))
            {
                activePropertyDialog = value;
                RaisePropertyChanged(nameof(ActivePropertyDialog));
            }
        }
    }

    internal void ConfigurePropertyEditing(PlaylistPropertySaveService service)
    {
        propertySaveService = service ?? throw new ArgumentNullException(nameof(service));
    }

    internal PlaylistPropertyDialogViewModel OpenPropertyDialog(BMSTable table, bool isNewTable = false)
    {
        PlaylistPropertySaveService service = propertySaveService
            ?? throw new InvalidOperationException("Playlist property editing is not available.");
        if (!service.ContainsActiveTable(table))
        {
            return null;
        }
        ActivePropertyDialog?.Dispose();
        ActivePropertyDialog = new PlaylistPropertyDialogViewModel(service, table, isNewTable);
        return ActivePropertyDialog;
    }

    internal void ClosePropertyDialog(PlaylistPropertyDialogViewModel dialog)
    {
        if (ReferenceEquals(ActivePropertyDialog, dialog))
        {
            ActivePropertyDialog = null;
        }
    }

    internal async Task<bool> ApplySummaryPropertyEditAsync(
        PlaylistSummaryRow row,
        string propertyName,
        string text)
    {
        PlaylistPropertySaveService service = propertySaveService
            ?? throw new InvalidOperationException("Playlist property editing is not available.");
        if (row?.TableRef == null
            || string.IsNullOrWhiteSpace(propertyName)
            || !service.ContainsActiveTable(row.TableRef))
        {
            return false;
        }

        PlaylistPropertySaveCommit commit;
        using (PlaylistPropertyEditSession session = service.BeginEdit(row.TableRef))
        {
            PlaylistPropertyValues values = session.Values;
            switch (propertyName)
            {
                case nameof(PlaylistSummaryRow.Name):
                    {
                        string name = (text ?? string.Empty).Trim();
                        if (string.Equals(values.Name, name, StringComparison.Ordinal))
                        {
                            return true;
                        }
                        values.Name = name;
                        break;
                    }
                case nameof(PlaylistSummaryRow.FolderName):
                    {
                        string outputDirectory = PlaylistPropertySaveService.NormalizeSummaryOutputDirectory(
                            values.Name,
                            text);
                        if (string.Equals(
                            BMSTable.NormalizeOutputDirectoryName(values.OutputDirectory),
                            BMSTable.NormalizeOutputDirectoryName(outputDirectory),
                            StringComparison.Ordinal))
                        {
                            return true;
                        }
                        values.OutputDirectory = outputDirectory;
                        break;
                    }
                case nameof(PlaylistSummaryRow.CompatPrefix):
                    {
                        string compatPrefix = (text ?? string.Empty).TrimStart();
                        if (string.Equals(values.CompatPrefix, compatPrefix, StringComparison.Ordinal))
                        {
                            return true;
                        }
                        values.CompatPrefix = compatPrefix;
                        break;
                    }
                case nameof(PlaylistSummaryRow.Symbol):
                    {
                        string symbol = (text ?? string.Empty).Trim();
                        if (string.Equals(values.Symbol, symbol, StringComparison.Ordinal))
                        {
                            return true;
                        }
                        values.Symbol = symbol;
                        break;
                    }
                default:
                    return false;
            }
            if (!service.TrySave(session, values, out commit))
            {
                return false;
            }
        }
        await service.ApplyPostSaveUpdatesAsync(commit);
        return true;
    }
}
