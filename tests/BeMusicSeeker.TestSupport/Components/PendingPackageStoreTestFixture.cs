using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>pending ownerと通常一覧編集のcaseで、同じ専有storeの入力記録・設定済み失敗の伝播を共用します。</summary>
internal static class PendingPackageStoreTestFixture
{

    internal sealed class RecordingStore : IPendingPackageStore
    {
        private readonly List<string> events;

        internal RecordingStore(List<string> events)
        {
            this.events = events;
        }

        internal int SearchPackagesCount { get; private set; }

        internal int SearchPendingCount { get; private set; }


        internal IReadOnlyList<ChartPackage> LastPackages { get; private set; } = [];

        internal InstallEstimationCandidatePolicy LastCandidatePolicy { get; private set; }

        internal PendingInstallDestinationSearchRequest? LastSearchRequest { get; private set; }

        internal IReadOnlyList<ChartFile> ChangedCharts { get; set; } = [];

        internal ChartFile? SetResult { get; set; }

        internal PendingInstallDestinationEditRequest? LastPendingRequest { get; private set; }

        internal string? LastDestinationDirectory { get; private set; }

        internal IReadOnlyList<ChartPackage> ResolvedPackages { get; set; } = [];

        internal ISet<ChartPackage> ApprovedNormalInstallOverridePackages { get; private set; } = new HashSet<ChartPackage>();

        internal IReadOnlyList<BMSLibrary.DuplicateInstallRepairConfirmation> DuplicateConfirmations { get; set; } = [];

        internal IReadOnlyList<string> ApprovedDuplicateRemovalChartPaths { get; private set; } = [];

        internal IReadOnlyList<ChartPackage> InstalledOnlyPendingPackages { get; set; } = [];

        internal IReadOnlyList<ChartFile> PendingBmsFormatCharts { get; set; } = [];

        internal PendingInstalledOnlyResourceOverwriteResult? OverwriteResult { get; set; }

        internal bool WaitForDeleteSourcesCancellation { get; set; }

        internal Exception? DeleteSourcesFailure { get; set; }

        internal Exception? Failure { get; set; }

        internal bool EmptyPendingSection { get; set; }

        internal Exception? EmptyPendingSectionFailure { get; set; }

        internal int IsPendingSectionEmptyCount { get; private set; }


        /// <summary>導入・削除・改名・上書きの呼出しで所有者から受け取った案内の収集先へ、caseが案内を加える口です。操作ごとに受け取った収集先も記録します。</summary>
        internal Action<BMSLibrary, LibraryOperationNotices>? NoticeAction { get; set; }

        /// <summary>直近の操作で所有者から受け取った案内の収集先です。</summary>
        internal LibraryOperationNotices? LastNotices { get; private set; }

        private void ReceiveNotices(BMSLibrary library, LibraryOperationNotices notices)
        {
            LastNotices = notices;
            NoticeAction?.Invoke(library, notices);
        }

        public void SearchPackages(
            BMSLibrary library,
            IReadOnlyList<ChartPackage> packages,
            InstallEstimationCandidatePolicy candidatePolicy)
        {
            events.Add("store-search-packages");
            SearchPackagesCount++;
            LastPackages = packages;
            LastCandidatePolicy = candidatePolicy;
            ThrowIfConfigured();
        }

        public IReadOnlyList<ChartFile> SearchPending(
            BMSLibrary library,
            PendingInstallDestinationSearchRequest request)
        {
            events.Add("store-search-pending");
            SearchPendingCount++;
            LastSearchRequest = request;
            ThrowIfConfigured();
            return ChangedCharts;
        }

        public IReadOnlyList<ChartFile> ClearPackages(IReadOnlyList<ChartPackage> packages)
        {
            events.Add("store-clear-packages");
            ThrowIfConfigured();
            return ChangedCharts;
        }

        public IReadOnlyList<ChartFile> ClearPending(
            BMSLibrary library,
            PendingInstallDestinationClearRequest request)
        {
            events.Add("store-clear-pending");
            ThrowIfConfigured();
            return ChangedCharts;
        }

        public IReadOnlyList<ChartFile> SearchCorrect(
            BMSLibrary library,
            RepairInstalledLocationRequest request)
        {
            events.Add("store-search-correct");
            ThrowIfConfigured();
            return ChangedCharts;
        }

        public IReadOnlyList<ChartFile> ClearCorrect(
            BMSLibrary library,
            RepairInstalledLocationRequest request)
        {
            events.Add("store-clear-correct");
            ThrowIfConfigured();
            return ChangedCharts;
        }

        public IReadOnlyList<ChartFile>? SetPending(
            BMSLibrary library,
            PendingInstallDestinationEditRequest request,
            string destinationDirectory,
            LibraryOperationNotices notices)
        {
            events.Add("store-set");
            LastPendingRequest = request;
            LastDestinationDirectory = destinationDirectory;
            ThrowIfConfigured();
            return SetResult == null ? null : [SetResult];
        }

        public IReadOnlyList<ChartPackage> ResolvePendingPackages(
            BMSLibrary library,
            IReadOnlyList<ChartOperationTarget> targets)
        {
            events.Add("store-resolve-packages");
            ThrowIfConfigured();
            return ResolvedPackages;
        }

        internal LibraryMutationSessionReceipt TerminalReceipt { get; set; } = LibraryMutationSessionReceipt.Empty;

        public LibraryMutationSessionReceipt ForceInstallPackagesWithReceipt(
            BMSLibrary library,
            IReadOnlyList<ChartPackage> packages,
            ISet<ChartPackage> approvedNormalInstallOverridePackages,
            LibraryOperationNotices notices)
        {
            events.Add("store-force-install");
            LastPackages = packages;
            ReceiveNotices(library, notices);
            ApprovedNormalInstallOverridePackages = new HashSet<ChartPackage>(approvedNormalInstallOverridePackages);
            ThrowIfConfigured();
            return TerminalReceipt;
        }

        public PendingInstallBatchResult ManualInstallPackagesWithReceipt(
            BMSLibrary library,
            IReadOnlyList<ChartPackage> packages,
            LibraryOperationNotices notices)
        {
            events.Add("store-manual-install");
            LastPackages = packages;
            ReceiveNotices(library, notices);
            ThrowIfConfigured();
            return new PendingInstallBatchResult { SessionReceipt = TerminalReceipt };
        }

        public bool IsPendingSectionEmpty(BMSLibrary library)
        {
            IsPendingSectionEmptyCount++;
            if (EmptyPendingSectionFailure != null)
            {
                throw EmptyPendingSectionFailure;
            }
            return EmptyPendingSection;
        }

        public IReadOnlyList<BMSLibrary.DuplicateInstallRepairConfirmation> GetDuplicateInstallRepairConfirmations(
            BMSLibrary library,
            IReadOnlyList<ChartFile> repairCharts)
        {
            events.Add("store-get-duplicate-confirmations");
            ThrowIfConfigured();
            return DuplicateConfirmations;
        }

        internal Exception? RepairFailure { get; set; }
        internal LibraryFixInstallationResult? RepairResult { get; set; }

        internal Func<BMSLibrary, IReadOnlyList<ChartFile>, IReadOnlyList<string>, LibraryFileMutationCapability?, LibraryFixInstallationResult>? FixInstalledLocationsAction { get; set; }

        private LibraryFileMutationCapability? acceptedCapability;

        /// <summary>実モデルを使う修復だけ生存権限を転送し、純fakeの局所境界は維持します。</summary>
        public IPendingPackageStore ForAcceptedOperation(LibraryFileMutationCapability capability)
            => FixInstalledLocationsAction == null ? this : new RecordingStore(events)
            { FixInstalledLocationsAction = FixInstalledLocationsAction, acceptedCapability = capability };

        public LibraryFixInstallationResult FixInstalledLocations(
            BMSLibrary library,
            IReadOnlyList<ChartFile> repairCharts,
            IReadOnlyList<string> approvedDuplicateRemovalChartPaths,
            LibraryOperationNotices notices)
        {
            events.Add("store-fix-installed-locations");
            ChangedCharts = repairCharts;
            ApprovedDuplicateRemovalChartPaths = approvedDuplicateRemovalChartPaths;
            ThrowIfConfigured();
            if (RepairFailure != null)
            {
                throw RepairFailure;
            }

            if (FixInstalledLocationsAction != null)
            {
                return FixInstalledLocationsAction(library, repairCharts, approvedDuplicateRemovalChartPaths, acceptedCapability);
            }
            return RepairResult ?? new LibraryFixInstallationResult();
        }

        public IReadOnlyList<ChartPackage> GetInstalledOnlyPendingPackages(BMSLibrary library)
        {
            events.Add("store-get-installed-only");
            ThrowIfConfigured();
            return InstalledOnlyPendingPackages;
        }

        public IReadOnlyList<ChartFile> GetPendingBmsFormatCharts(BMSLibrary library)
        {
            events.Add("store-get-pending-charts");
            ThrowIfConfigured();
            return PendingBmsFormatCharts;
        }

        public void DeletePendingPackageSources(
            BMSLibrary library,
            IReadOnlyList<ChartPackage> packages,
            LibraryOperationNotices notices,
            CancellationToken cancellationToken,
            Action onEachProcessed)
        {
            events.Add("store-delete-sources");
            LastPackages = packages;
            ReceiveNotices(library, notices);
            if (DeleteSourcesFailure != null)
            {
                throw DeleteSourcesFailure;
            }
            if (WaitForDeleteSourcesCancellation)
            {
                cancellationToken.WaitHandle.WaitOne();
            }
            foreach (ChartPackage _ in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onEachProcessed?.Invoke();
            }
            ThrowIfConfigured();
        }

        public void RenamePendingZeroNoteCharts(
            BMSLibrary library,
            IReadOnlyList<ChartFile> charts,
            LibraryOperationNotices notices,
            CancellationToken cancellationToken,
            Action onEachProcessed)
        {
            events.Add("store-rename-zero-note");
            ChangedCharts = charts;
            ReceiveNotices(library, notices);
            foreach (ChartFile _ in charts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onEachProcessed?.Invoke();
            }
            ThrowIfConfigured();
        }

        public PendingInstalledOnlyResourceOverwriteResult OverwriteInstalledOnlyPendingPackageResources(
            BMSLibrary library,
            IReadOnlyList<ChartPackage> packages,
            LibraryOperationNotices notices,
            CancellationToken cancellationToken,
            Action onEachProcessed)
        {
            events.Add("store-overwrite-resources");
            LastPackages = packages;
            ReceiveNotices(library, notices);
            foreach (ChartPackage _ in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onEachProcessed?.Invoke();
            }
            ThrowIfConfigured();
            return OverwriteResult!;
        }

        private void ThrowIfConfigured()
        {
            if (Failure != null)
            {
                throw Failure;
            }
        }
    }

}
