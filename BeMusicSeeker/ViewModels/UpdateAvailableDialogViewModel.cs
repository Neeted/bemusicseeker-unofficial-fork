using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

internal sealed class UpdateAvailableDialogViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly MainWindowViewModel ownerViewModel;
    private UpdatePackageOption selectedPackage;

    public UpdateAvailableDialogViewModel(UpdateCheckResult updateCheckResult, MainWindowViewModel ownerViewModel)
    {
        UpdateCheckResult = updateCheckResult ?? throw new ArgumentNullException(nameof(updateCheckResult));
        this.ownerViewModel = ownerViewModel;
        Packages = new ObservableCollection<UpdatePackageOption>((UpdateCheckResult.Assets ?? []).Select(UpdatePackageOption.Create));
        selectedPackage = Packages.FirstOrDefault();
        if (ownerViewModel != null)
        {
            ownerViewModel.PropertyChanged += OwnerViewModelPropertyChanged;
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    public UpdateCheckResult UpdateCheckResult { get; }

    public ObservableCollection<UpdatePackageOption> Packages { get; }

    public string Title => Resources.UpdateDialog_Title;

    public string Message => string.Format(Resources.UpdateDialog_Message_Format, UpdateCheckResult.LatestVersionText);

    public string CurrentVersionLabel => Resources.UpdateDialog_CurrentVersion;

    public string LatestVersionLabel => Resources.UpdateDialog_LatestVersion;

    public string PackageLabel => Resources.UpdateDialog_Package;

    public string UpdateButtonLabel => Resources.UpdateDialog_UpdateButton;

    public string ReleasePageButtonLabel => Resources.UpdateDialog_ReleasePageButton;

    public string CurrentVersionText => UpdateCheckResult.CurrentVersionText;

    public string LatestVersionText => UpdateCheckResult.LatestVersionText;

    public string ReleasePageUrl => UpdateCheckResult.ReleasePageUrl;

    public bool HasSelectableAssets => Packages.Count > 0;

    public bool CanStartUpdate => HasSelectableAssets && ownerViewModel?.IsStartupProgressActive != true;

    public string StartBlockedReason => CanStartUpdate ? string.Empty : Resources.UpdateDialog_StartupBlocked;

    public UpdateAssetInfo SelectedAsset => selectedPackage?.Asset;

    public UpdatePackageOption SelectedPackage
    {
        get => selectedPackage;
        set
        {
            if (!ReferenceEquals(selectedPackage, value))
            {
                selectedPackage = value;
                RaisePropertyChanged(nameof(SelectedPackage));
                RaisePropertyChanged(nameof(CanStartUpdate));
                RaisePropertyChanged(nameof(SelectedAsset));
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
            RaisePropertyChanged(nameof(CanStartUpdate));
            RaisePropertyChanged(nameof(StartBlockedReason));
        }
    }

    private void RaisePropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    internal sealed class UpdatePackageOption
    {
        private UpdatePackageOption(UpdateAssetInfo asset, string displayLabel, string description)
        {
            Asset = asset;
            DisplayLabel = displayLabel;
            Description = description;
        }

        public UpdateAssetInfo Asset { get; }

        public string DisplayLabel { get; }

        public string Description { get; }

        public string FileName => Asset?.FileName ?? string.Empty;

        public static UpdatePackageOption Create(UpdateAssetInfo asset)
        {
            if (asset == null)
            {
                return null;
            }

            if (string.Equals(asset.Kind, "app-with-metadata", StringComparison.Ordinal))
            {
                return new UpdatePackageOption(
                    asset,
                    Resources.UpdateDialog_PackageWithMetadata,
                    Resources.UpdateDialog_PackageWithMetadataDescription);
            }

            if (string.Equals(asset.Kind, "app", StringComparison.Ordinal))
            {
                return new UpdatePackageOption(
                    asset,
                    Resources.UpdateDialog_PackageAppOnly,
                    Resources.UpdateDialog_PackageAppOnlyDescription);
            }

            return new UpdatePackageOption(asset, asset.Label ?? asset.FileName ?? asset.Kind, string.Empty);
        }
    }
}
