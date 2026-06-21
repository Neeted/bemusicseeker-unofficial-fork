using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using BeMusicSeeker.Models.Update;

namespace BeMusicSeeker.ViewModels;

internal sealed class UpdateAvailableDialogViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MainWindowViewModel ownerViewModel;
    private UpdateAssetInfo selectedAsset;

    public UpdateAvailableDialogViewModel(UpdateCheckResult updateCheckResult, MainWindowViewModel ownerViewModel)
    {
        UpdateCheckResult = updateCheckResult ?? throw new ArgumentNullException(nameof(updateCheckResult));
        this.ownerViewModel = ownerViewModel;
        Assets = new ObservableCollection<UpdateAssetInfo>(UpdateCheckResult.Assets ?? []);
        selectedAsset = Assets.FirstOrDefault();
        if (ownerViewModel != null)
        {
            ownerViewModel.PropertyChanged += OwnerViewModelPropertyChanged;
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public UpdateCheckResult UpdateCheckResult { get; }

    public ObservableCollection<UpdateAssetInfo> Assets { get; }

    public string Title => "Update Available";

    public string Message => $"A new version ({UpdateCheckResult.LatestVersionText}) is available.";

    public string CurrentVersionText => UpdateCheckResult.CurrentVersionText;

    public string LatestVersionText => UpdateCheckResult.LatestVersionText;

    public string ReleasePageUrl => UpdateCheckResult.ReleasePageUrl;

    public bool HasSelectableAssets => Assets.Count > 0;

    public bool CanApplyUpdateNow => HasSelectableAssets && ownerViewModel?.IsStartupProgressActive != true;

    public string ApplyBlockedReason => CanApplyUpdateNow ? string.Empty : "Startup initialization is still running.";

    public UpdateAssetInfo SelectedAsset
    {
        get => selectedAsset;
        set
        {
            if (!ReferenceEquals(selectedAsset, value))
            {
                selectedAsset = value;
                RaisePropertyChanged(nameof(SelectedAsset));
                RaisePropertyChanged(nameof(CanApplyUpdateNow));
            }
        }
    }

    public void Dispose()
    {
        if (ownerViewModel != null)
        {
            ownerViewModel.PropertyChanged -= OwnerViewModelPropertyChanged;
        }
    }

    private void OwnerViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsStartupProgressActive))
        {
            RaisePropertyChanged(nameof(CanApplyUpdateNow));
            RaisePropertyChanged(nameof(ApplyBlockedReason));
        }
    }

    private void RaisePropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
