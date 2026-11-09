using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryInitializationTestSupport;
using static BeMusicSeeker.TestSupport.Components.PackageInstallComponentTestSupport;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class PackageInstallWorkflowOwnerTestSupport
{
    /// <summary>旧版で登録された重複行だけを同じfixtureのDBと公開集合へ用意し、その後は通常の取込み入口で修復します。</summary>
    internal static void SeedLegacyPending(AutomaticContinuationFixture fixture, IReadOnlyList<ChartPackage> packages)
    {
        ExecuteSongDbFixtureTransaction(fixture.Library.DatabaseScope, fixture.Database, db =>
        {
            db.CreateTable<LR2SongDBExtended.install>();
            foreach (ChartPackage package in packages) { db.Insert(package, typeof(LR2SongDBExtended.install)); }
        });
        fixture.Library.ChartPackagesPending = packages;
    }

    /// <summary>本番portへ要求と権限・停止継続をそのまま転送し、UI通知を出さない保留正常終端も実Taskの戻り値から観測します。</summary>
    internal sealed class ObservingInstallMutationPort : IPackageInstallMutationPort
    {
        private readonly BmsLibraryPackageInstallMutationPort inner = new();
        internal PackageInstallCommandResult? LatestResult { get; private set; }
        internal int CompletedCount { get; private set; }

        /// <summary>同じ共通権限と入力選択を本番portへ渡し、実終端の結果を捕捉します。</summary>
        public Task<PackageInstallCommandResult> InstallUnderAdmissionAsync(BMSLibrary library, IEnumerable<string> paths, CancellationToken token,
            IPackageInstallProgressWriter progress, LibraryOperationNotices notices, LibraryFileMutationCapability capability, IReadOnlySet<string>? fullySelectedDirectories = null)
            => CaptureAsync(inner.InstallUnderAdmissionAsync(library, paths, token, progress, notices, capability, fullySelectedDirectories));

        /// <summary>本番の停止・P引渡し継続を保持したまま、同じ取り込みの実終端を観測します。</summary>
        public Task<PackageInstallCommandResult> InstallUnderAdmissionAsync(BMSLibrary library, IEnumerable<string> paths,
            CancellationToken token, IPackageInstallProgressWriter progress, LibraryOperationNotices notices, LibraryFileMutationCapability capability,
            Func<Task> beforePhysicalMutation, Action<LibraryFileMutationLease> retainPlaylistLease, IReadOnlySet<string>? fullySelectedDirectories = null)
            => CaptureAsync(inner.InstallUnderAdmissionAsync(library, paths, token, progress, notices, capability, beforePhysicalMutation, retainPlaylistLease, fullySelectedDirectories));

        /// <summary>通常の本番取り込みをそのまま実行し、戻り値だけを保持します。</summary>
        public Task<PackageInstallCommandResult> InstallWithProgressAsync(BMSLibrary library, IEnumerable<string> paths, CancellationToken token,
            IPackageInstallProgressWriter progress, LibraryOperationNotices notices, IReadOnlySet<string>? fullySelectedDirectories = null)
            => CaptureAsync(inner.InstallWithProgressAsync(library, paths, token, progress, notices, fullySelectedDirectories));

        private async Task<PackageInstallCommandResult> CaptureAsync(Task<PackageInstallCommandResult> operation)
        {
            PackageInstallCommandResult result = await operation.ConfigureAwait(false);
            LatestResult = result;
            CompletedCount++;
            return result;
        }
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class HeldSuccessfulEvaluations(bool fault) : IInstallEstimationExecutionObserver
    {
        internal readonly TaskCompletionSource FirstApplied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource SecondEvaluated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ThirdEvaluated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseThird = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly IOException ExpectedFailure = new("実評価の終端失敗");
        internal int FourthStarted;

        public IDisposable BeginWorkItem(InstallEstimationWorkItemObservation observation)
        {
            if (observation.OrderIndex == 3) { Interlocked.Increment(ref FourthStarted); }
            return new EvaluationScope();
        }

        public void ObserveEvaluationCompleted(InstallEstimationAppliedObservation observation)
        {
            if (observation.OrderIndex == 1)
            {
                SecondEvaluated.TrySetResult();
                ReleaseSecond.Task.GetAwaiter().GetResult();
                if (fault) { throw ExpectedFailure; }
            }
            if (observation.OrderIndex == 2)
            {
                ThirdEvaluated.TrySetResult();
                ReleaseThird.Task.GetAwaiter().GetResult();
            }
        }

        public void ObserveResultApplied(InstallEstimationAppliedObservation observation)
        {
            if (observation.OrderIndex == 0) { FirstApplied.TrySetResult(); }
        }

        public void ObserveDispatchStopped() => Stopped.TrySetResult();

        public void ObserveProgress(InstallEstimationProgressObservation observation) { }

        private sealed class EvaluationScope : IDisposable { public void Dispose() { } }
    }

}
