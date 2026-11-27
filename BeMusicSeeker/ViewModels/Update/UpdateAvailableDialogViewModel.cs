using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.ViewModels;

internal sealed class UpdateAvailableDialogViewModel : INotifyPropertyChanged
{
    private UpdatePackageOption selectedPackage;

    public UpdateAvailableDialogViewModel(UpdateCheckResult updateCheckResult)
    {
        UpdateCheckResult = updateCheckResult ?? throw new ArgumentNullException(nameof(updateCheckResult));
        // 配信側が update.json の並び順で推奨を決めるため、並べ替えずに先頭を初期選択します。
        Packages = new ObservableCollection<UpdatePackageOption>((UpdateCheckResult.Assets ?? [])
            .Where(asset => asset != null)
            .Select(UpdatePackageOption.Create));
        selectedPackage = Packages.FirstOrDefault();
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

    /// <summary>選択できるパッケージが1件以上あれば更新を始められます。起動時の初期化の進行は条件にしません。</summary>
    public bool CanStartUpdate => HasSelectableAssets;

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

        /// <summary>資産の中身を表す <see cref="UpdateAssetInfo.IncludesChartInfoMetadata"/> から名前と説明を選びます。</summary>
        public static UpdatePackageOption Create(UpdateAssetInfo asset)
        {
            return asset.IncludesChartInfoMetadata
                ? new UpdatePackageOption(
                    asset,
                    Resources.UpdateDialog_PackageWithMetadata,
                    Resources.UpdateDialog_PackageWithMetadataDescription)
                : new UpdatePackageOption(
                    asset,
                    Resources.UpdateDialog_PackageAppOnly,
                    Resources.UpdateDialog_PackageAppOnlyDescription);
        }
    }
}
