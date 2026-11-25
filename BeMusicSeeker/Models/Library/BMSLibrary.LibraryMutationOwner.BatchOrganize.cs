using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models;

internal sealed partial class LibraryMutationOwner
{
    /// <summary>
    /// 重複グループを、一つの共通受付と一つの変更セッションでまとめて整理します。
    /// 二フォルダ以上のグループは、移動元を与えられた順に、単体の統合と同じ処理と設定で KEEP へ統合し、
    /// KEEP 直下に元からある同じ主ハッシュの譜面を一件にします。一フォルダのグループは、そのフォルダ直下の重複を一件にします。
    /// カタログ・DB・索引・LR2同期・通知への反映と必須保守は、全グループの物理処理の後に一回だけ行い、回数はグループ数に比例しません。
    /// </summary>
    /// <remarks>
    /// 管理出力（プレイリスト出力）の保護は、実行対象の全フォルダの和集合で、最初の物理変更の前に一回取ります。
    /// 取れない場合と、Busy・カタログ未収束の場合は、操作全体を未適用で返し、既存の案内を一件だけ加えます。
    /// グループごとの理由は案内に積まず、結果の値で返します。
    /// 最後のコピーの保護は、今回の全移動元の配下を除いた確定済みの所持と、今回の操作で実際に配置へ成功した譜面だけを裏付けにして守ります。
    /// </remarks>
    /// <param name="request">処理するグループ。順序が処理順です。</param>
    /// <param name="operationId">診断識別子。</param>
    /// <param name="notices">この操作の案内を集める入れ物。操作全体の拒否の案内だけを加え、呼出元が受付の解放後に表示します。</param>
    /// <param name="progress">グループの開始ごとと、全グループの物理処理の後・確定の前（反映段階への移行。受理した操作だけ、取消・停止の後も一回）に同期で呼ぶ進捗の受け手。待たずに戻る前提で、例外は結果を変えません。モデルのロックは保持しません。</param>
    /// <param name="cancellationToken">取消の合図。グループの開始前にだけ確認し、処理中のグループは最後まで進めます。確定・後片付け・必須保守は取消の対象にしません。</param>
    /// <param name="capability">同じ受付の生存権限。省略時は新規の非待機受付を取得し、必須保守・後片付けまで保持します。</param>
    internal DuplicateBatchOrganizeResult OrganizeDuplicateGroups(
        DuplicateBatchOrganizeRequest request,
        long operationId,
        LibraryOperationNotices notices,
        Action<DuplicateBatchOrganizeProgress> progress,
        CancellationToken cancellationToken,
        LibraryFileMutationCapability capability = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(notices);
        if (request.Groups.Count == 0)
        {
            return new DuplicateBatchOrganizeResult(DuplicateMergeMaintenanceReceipt.NotApplied, [], canceled: false, admitted: true);
        }
        if (TryBlockCatalogMutation(nameof(BMSLibrary.OrganizeDuplicateGroups), notices, capability: capability))
        {
            return DuplicateBatchOrganizeResult.NotAdmitted(request);
        }

        var totalStopwatch = Stopwatch.StartNew();
        LogInstallPerformance("duplicate_batch_organize start op=" + operationId + " groups=" + request.Groups.Count);
        List<Action> postLeaseNotifications = [];
        DuplicateBatchOrganizeOperation operation = null;
        DuplicateBatchOrganizeResult result;
        try
        {
            using (LibraryFileMutationLease mutationLease = synchronization.EnterMergeWriteScope(notices, capability))
            {
                if (mutationLease == null)
                {
                    return DuplicateBatchOrganizeResult.NotAdmitted(request);
                }
                string[] protectedPaths = [.. request.Groups
                    .SelectMany(group => group.SourceDirectories.Prepend(group.KeepDirectory))
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
                if (!TryEnterManagedOutputMutation(protectedPaths, recursive: true, notices, out LibraryFileMutationLease playlistLease, capability))
                {
                    return DuplicateBatchOrganizeResult.NotAdmitted(request);
                }
                using LibraryFileMutationLease playlistOperation = playlistLease;
                using LibraryFileMutationCapability primaryCapability = mutationLease.CreateMutationCapability();
                using LibraryFileMutationCapability playlistCapability = playlistLease?.CreateMutationCapability();
                using LibraryFileMutationCapability mutationCapability = primaryCapability.WithPlaylistCapability(playlistCapability);
                mutationCapability.Validate(lr2SynchronizationOwner.OperationAdmission);
                operation = new DuplicateBatchOrganizeOperation(
                    this, request, operationId, notices, postLeaseNotifications, mutationCapability, progress, cancellationToken);
                result = operation.Run();
            }

            InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
            if (operation.HadPhysicalChange && !result.Receipt.MergeApplied)
            {
                InvalidateInstalledDirectoryIndex();
            }
            LogInstallPerformance("duplicate_batch_organize done op=" + operationId
                + " groups=" + request.Groups.Count
                + " canceled=" + result.Canceled
                + " durable=" + result.Receipt.HasDurableCommit
                + " finalizationFailed=" + result.Receipt.HasDurableFinalizationFailure
                + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception exception)
        {
            InvalidateInstalledDirectoryIndex();
            LogInstallPerformanceWarning("duplicate_batch_organize failed op=" + operationId + " elapsedMs=" + totalStopwatch.ElapsedMilliseconds + " exception=" + exception.GetType().Name);
            throw;
        }
    }

    /// <summary>確認済みの要求の直下の譜面だけを、同じ現在値の捕捉境界で返します。</summary>
    private List<ChartFile> CreateOwnedDirectChartSnapshotsUnsafe(string directoryPath)
    {
        EnsureOwnedChartCollectionReady();
        lock (catalogOwnedCollectionOwner.Gate)
        {
            return [.. catalogOwnedCollectionOwner.Collection.CreateLibraryChartRefIndexSnapshot()
                .GetDirectChartRefsInRealPaths([directoryPath])
                .Select(catalogOwnedCollectionOwner.Collection.ResolveCurrentChart)
                .Where(chart => chart != null)];
        }
    }

    /// <summary>
    /// 一回の一括整理の調整役です。一つの変更セッションへ、各グループの確定事実を順に集めます。
    /// 単体の統合は通さず、物理処理・事実の追加・確定後の必須保守は単体と同じ部品（<see cref="MoveMergeSourceForSession"/>、
    /// <see cref="AppendLibraryChartRemovalToSession"/>、<see cref="RunMergeFolderMaintenance"/>）を使います。
    /// </summary>
    private sealed class DuplicateBatchOrganizeOperation
    {
        private readonly LibraryMutationOwner owner;
        private readonly DuplicateBatchOrganizeRequest request;
        private readonly long operationId;
        private readonly LibraryOperationNotices notices;
        private readonly ICollection<Action> postLeaseNotifications;
        private readonly LibraryFileMutationCapability mutationCapability;
        private readonly Action<DuplicateBatchOrganizeProgress> progress;
        private readonly CancellationToken cancellationToken;
        private readonly GroupState[] states;

        private readonly Dictionary<string, List<ChartFile>> sourceChartsByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<ChartFile>> directChartsByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> removedChartPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> mergedKeeps = [];
        private readonly HashSet<string> mergedKeepSet = new(StringComparer.OrdinalIgnoreCase);
        private InstallDestinationOverlayChartRefSnapshot installDestinationOverlayChartRefs;
        private List<ChartPackage> pendingPackages = [];
        private List<ChartPackage> installedPackages = [];
        private BmsLibraryPackageInstallService.SessionSuccessOwnershipOverlay ownershipOverlay;
        private IReadOnlyList<string> registeredRoots = [];
        private LibraryMutationSession session;
        private List<ChartFile> destinationMaintenanceCharts;
        private bool maintenanceFinalizerAppended;
        private bool stopped;
        private bool canceled;

        internal DuplicateBatchOrganizeOperation(
            LibraryMutationOwner owner,
            DuplicateBatchOrganizeRequest request,
            long operationId,
            LibraryOperationNotices notices,
            ICollection<Action> postLeaseNotifications,
            LibraryFileMutationCapability mutationCapability,
            Action<DuplicateBatchOrganizeProgress> progress,
            CancellationToken cancellationToken)
        {
            this.owner = owner;
            this.request = request;
            this.operationId = operationId;
            this.notices = notices;
            this.postLeaseNotifications = postLeaseNotifications;
            this.mutationCapability = mutationCapability;
            this.progress = progress;
            this.cancellationToken = cancellationToken;
            states = [.. request.Groups.Select(group => new GroupState(group))];
        }

        /// <summary>確定事実のうち、物理的に成功した変更（統合またはごみ箱への削除）が一つでもあったかどうかです。</summary>
        internal bool HadPhysicalChange { get; private set; }

        /// <summary>全グループを処理し、一回だけ確定して、必須保守までを行います。</summary>
        internal DuplicateBatchOrganizeResult Run()
        {
            AnalyzeGroupRelations();
            CaptureOperationInputs();
            using (session = owner.BeginLibraryMutationSession(
                mutationCapability,
                "duplicate_batch_organize op=" + operationId,
                postLeaseNotifications,
                notices))
            {
                int started = 0;
                for (int index = 0; index < states.Length && !stopped; index++)
                {
                    // 取消はグループの開始前にだけ確認します。処理中のグループは最後まで進めます。
                    if (cancellationToken.IsCancellationRequested)
                    {
                        canceled = true;
                        break;
                    }
                    started = index + 1;
                    ReportProgress(index + 1, states[index].Group.KeepDirectory);
                    try
                    {
                        ProcessGroup(index);
                    }
                    catch (Exception exception)
                    {
                        // 予期しない物理失敗は、継続の安全を確認できないため、残りを未処理として止めます。
                        // 受取り（セッション）の失敗も同じ扱いで、それまでに成功した分だけを確定します。
                        StopAfterUnexpectedFailure(index, exception);
                    }
                }

                // 取消・停止の後でも、受理した操作は確定と必須保守へ進みます。進捗の受け手へ、反映段階へ入ったことを一回だけ知らせます。
                ReportProgress(new DuplicateBatchOrganizeProgress(
                    started, states.Length, null, DuplicateBatchOrganizeProgressKind.Applying));
                LibraryMutationSessionReceipt sessionReceipt = session.Commit();
                bool mergeApplied = sessionReceipt.DurableCommit && !sessionReceipt.HasDurableFinalizationFailure;
                MaintenanceWorkflowResult maintenanceResult = null;
                if (mergeApplied && mergedKeeps.Count > 0)
                {
                    // 必須保守は、成功した全 KEEP を対象に、同じ受理操作の生存権限で一回だけ行います。取消は渡しません。
                    maintenanceResult = owner.RunMergeFolderMaintenance(
                        destinationMaintenanceCharts, mutationCapability, postLeaseNotifications, notices, ref sessionReceipt);
                }

                bool commitFailed = HadPhysicalChange && !sessionReceipt.DurableCommit;
                DuplicateBatchOrganizeGroupResult[] groupResults = [.. states.Select(state => state.ToResult(commitFailed))];
                postLeaseNotifications.Add(() => LogGroupResults(groupResults));
                return new DuplicateBatchOrganizeResult(
                    CreateMergeMaintenanceReceipt(maintenanceResult, sessionReceipt, mergeApplied),
                    groupResults,
                    canceled,
                    admitted: true);
            }
        }

        /// <summary>
        /// 要求内のグループ同士の関係を、フォルダの総数に比例する量で一度だけ解析します。
        /// 同じグループのフォルダ同士の同一・親子と、二フォルダ以上のグループが別のグループのフォルダと同一・親子になる関係を求めます。
        /// </summary>
        private void AnalyzeGroupRelations()
        {
            var groupsByFolder = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < states.Length; index++)
            {
                GroupState state = states[index];
                if (state.FolderKeys.Any(key => key == null))
                {
                    state.PreSkipReason = DuplicateBatchOrganizeSkipReason.InvalidPath;
                    continue;
                }
                var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string key in state.FolderKeys)
                {
                    if (!own.Add(key))
                    {
                        state.HasNestedFolders = true;
                    }
                }
                foreach (string key in own)
                {
                    for (string ancestor = Lr2FolderPath.SafeGetParentNormalizedDirectory(key);
                        !string.IsNullOrWhiteSpace(ancestor);
                        ancestor = Lr2FolderPath.SafeGetParentNormalizedDirectory(ancestor))
                    {
                        if (own.Contains(ancestor))
                        {
                            state.HasNestedFolders = true;
                        }
                    }
                    if (!groupsByFolder.TryGetValue(key, out List<int> owners))
                    {
                        groupsByFolder.Add(key, owners = []);
                    }
                    owners.Add(index);
                }
            }
            foreach ((string key, List<int> descendants) in groupsByFolder)
            {
                for (string ancestor = key;
                    !string.IsNullOrWhiteSpace(ancestor);
                    ancestor = Lr2FolderPath.SafeGetParentNormalizedDirectory(ancestor))
                {
                    if (!groupsByFolder.TryGetValue(ancestor, out List<int> ancestors))
                    {
                        continue;
                    }
                    foreach (int descendantGroup in descendants)
                    {
                        foreach (int ancestorGroup in ancestors)
                        {
                            if (descendantGroup == ancestorGroup)
                            {
                                continue;
                            }
                            // 一フォルダのグループは処理でき、巻き込まれる二フォルダ以上のグループだけを拒否します。
                            states[descendantGroup].OverlapsOtherGroup |= !states[descendantGroup].Group.IsSingleFolder;
                            states[ancestorGroup].OverlapsOtherGroup |= !states[ancestorGroup].Group.IsSingleFolder;
                        }
                    }
                }
            }
            registeredRoots = BmsRootRegistrationStore.CaptureUserRoots(owner.lr2SynchronizationOwner.CurrentOptionsSnapshot);
        }

        /// <summary>
        /// 最初の物理変更の前に、全グループの入力を一度だけ捕捉します。
        /// 全予定移動元（拒否・未処理・失敗になるグループの移動元も含む）の配下を除いた所持判定の基準を作り、
        /// 今回の操作で配置に成功した譜面だけを重ねる所持判定へ渡します。
        /// </summary>
        private void CaptureOperationInputs()
        {
            string[] sourceKeys = [.. states.SelectMany(state => state.SourceKeys).Where(key => key != null).Distinct(StringComparer.OrdinalIgnoreCase)];
            string[] directKeys = [.. states.SelectMany(state => state.FolderKeys).Where(key => key != null).Distinct(StringComparer.OrdinalIgnoreCase)];
            owner.RunWithMergeSnapshotLocks(() =>
            {
                var allSourceCharts = new List<ChartFile>();
                foreach (string sourceKey in sourceKeys)
                {
                    List<ChartFile> charts = owner.CreateOwnedRealPathChartSnapshotsUnsafe(sourceKey);
                    sourceChartsByKey[sourceKey] = charts;
                    allSourceCharts.AddRange(charts);
                }
                foreach (string directKey in directKeys)
                {
                    directChartsByKey[directKey] = owner.CreateOwnedDirectChartSnapshotsUnsafe(directKey);
                }
                installDestinationOverlayChartRefs = owner.CreateInstallDestinationOverlayChartRefSnapshot();
                pendingPackages = [.. owner.packageLifecycleOwner.PendingPackages.Where(package => package != null)];
                installedPackages = [.. owner.packageLifecycleOwner.InstalledPackages.Where(package => package != null)];
                IPrimaryHashLookup hashBaseline = owner.CreateInstalledChartKeySnapshotExcludingChartsUnsafe(
                    allSourceCharts, "duplicate_batch_organize", operationId);
                IInstalledChartLookupIndex ownershipBaseline = new SourceExcludingInstalledChartLookup(
                    owner.CreateInstalledChartLookupSnapshotUnsafe(), sourceKeys);
                ownershipOverlay = owner.packageInstallService.CreateSessionSuccessOwnershipOverlay(ownershipBaseline, hashBaseline);
            });
        }

        private void ReportProgress(int number, string keepDirectory)
            => ReportProgress(new DuplicateBatchOrganizeProgress(number, states.Length, keepDirectory));

        private void ReportProgress(DuplicateBatchOrganizeProgress value)
        {
            if (progress == null)
            {
                return;
            }
            try
            {
                progress(value);
            }
            catch (Exception exception)
            {
                // 受け手の失敗で、整理の結果を変えません。
                try { owner.logFileWarning(exception, "duplicate_batch_organize_progress_failed"); }
                catch (Exception) { /* 診断の失敗は握りつぶします。 */ }
            }
        }

        private void ProcessGroup(int groupIndex)
        {
            GroupState state = states[groupIndex];
            if (state.Group.IsSingleFolder)
            {
                ProcessSingleFolderGroup(state);
            }
            else
            {
                ProcessMergeGroup(groupIndex, state);
            }
        }

        /// <summary>一フォルダのグループです。フォルダ本体のリンクと存在だけを確認し、直下の重複を一件にします。</summary>
        private void ProcessSingleFolderGroup(GroupState state)
        {
            if (!TryGetSingleFolderSkip(state, out DuplicateBatchOrganizeSkipReason reason, out string path))
            {
                List<ChartFile> deletions = DuplicateChartCleanupSelector.SelectDeletions(
                    DirectCharts(state.KeepKey));
                if (deletions.Count > 0)
                {
                    TrashCharts(state, deletions);
                }
                state.Status = state.TrashFailureCount == 0
                    ? DuplicateBatchOrganizeGroupStatus.Completed
                    : DuplicateBatchOrganizeGroupStatus.PartiallyCompleted;
                return;
            }
            state.Skip(reason, path);
        }

        private bool TryGetSingleFolderSkip(GroupState state, out DuplicateBatchOrganizeSkipReason reason, out string path)
        {
            reason = DuplicateBatchOrganizeSkipReason.None;
            path = null;
            if (state.PreSkipReason != DuplicateBatchOrganizeSkipReason.None)
            {
                reason = state.PreSkipReason;
                return true;
            }
            string folder = state.Group.KeepDirectory;
            if (!LongPathFileSystem.DirectoryExists(folder))
            {
                reason = DuplicateBatchOrganizeSkipReason.FolderMissing;
                path = folder;
                return true;
            }
            return TryGetLinkSkip(FolderLinkInspector.InspectFolder(folder), out reason, out path);
        }

        /// <summary>
        /// 二フォルダ以上のグループです。書込み前の拒否を確認し、移動元を与えられた順に統合して、全移動元の後に KEEP 直下の重複を一件にします。
        /// </summary>
        private void ProcessMergeGroup(int groupIndex, GroupState state)
        {
            if (TryGetMergeGroupSkip(state, out DuplicateBatchOrganizeSkipReason reason, out string path))
            {
                state.Skip(reason, path);
                return;
            }
            IReadOnlyList<string> sources = state.Group.SourceDirectories;
            for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                if (!ProcessSource(groupIndex, state, sourceIndex))
                {
                    // 予期しない物理失敗です。残りの移動元は未処理として残し、以降の処理を止めます。
                    state.CompleteAfterStop();
                    return;
                }
            }
            CleanupKeepDirectory(state);
            CleanupAudioCollisions(state);
            state.CompleteMergeStatus();
        }

        private bool TryGetMergeGroupSkip(GroupState state, out DuplicateBatchOrganizeSkipReason reason, out string path)
        {
            reason = DuplicateBatchOrganizeSkipReason.None;
            path = null;
            if (state.PreSkipReason != DuplicateBatchOrganizeSkipReason.None)
            {
                reason = state.PreSkipReason;
                return true;
            }
            if (state.HasNestedFolders)
            {
                reason = DuplicateBatchOrganizeSkipReason.NestedFolders;
                return true;
            }
            if (state.OverlapsOtherGroup)
            {
                reason = DuplicateBatchOrganizeSkipReason.OverlapsOtherGroup;
                return true;
            }
            foreach (string source in state.Group.SourceDirectories)
            {
                if (BmsLibraryLibraryFileOperationsService.ContainsRegisteredBmsRoot(source, registeredRoots))
                {
                    reason = DuplicateBatchOrganizeSkipReason.RegisteredRoot;
                    path = source;
                    return true;
                }
            }
            foreach (string source in state.Group.SourceDirectories)
            {
                if (TryGetLinkSkip(FolderLinkInspector.InspectMerge(source, state.Group.KeepDirectory), out reason, out path))
                {
                    return true;
                }
            }
            // KEEP 直下の重複整理は、移動元の有無や結果によらず行うため、KEEP 本体がリンクでないことを必ず確認します。
            // 移動元がすべて欠落していると InspectMerge は統合先を検査しません。
            return TryGetLinkSkip(FolderLinkInspector.InspectFolder(state.Group.KeepDirectory), out reason, out path);
        }

        private static bool TryGetLinkSkip(FolderLinkInspection inspection, out DuplicateBatchOrganizeSkipReason reason, out string path)
        {
            reason = DuplicateBatchOrganizeSkipReason.None;
            path = null;
            if (inspection.Outcome == FolderLinkInspectionOutcome.Clear)
            {
                return false;
            }
            reason = inspection.Outcome == FolderLinkInspectionOutcome.LinkFound
                ? DuplicateBatchOrganizeSkipReason.LinkFound
                : DuplicateBatchOrganizeSkipReason.LinkCheckFailed;
            path = inspection.Path;
            return true;
        }

        /// <summary>
        /// 移動元一件を処理します。移動元ごとの事前拒否は理由付きで残して継続し、予期しない物理失敗だけがfalseを返して全体を止めます。
        /// 順序は、事前確認 → 実際に移すハッシュに限った移動元直下の重複整理 → ごみ箱へ送れた譜面を除いた統合です。
        /// </summary>
        private bool ProcessSource(int groupIndex, GroupState state, int sourceIndex)
        {
            string source = state.Group.SourceDirectories[sourceIndex];
            string keep = state.Group.KeepDirectory;
            state.Attempted = true;
            state.CurrentSource = source;
            if (!LongPathFileSystem.DirectoryExists(source))
            {
                state.Retain(source, DuplicateBatchOrganizeSourceReason.SourceMissing);
                return true;
            }
            if (!LongPathFileSystem.DirectoryExists(keep))
            {
                state.Retain(source, DuplicateBatchOrganizeSourceReason.DestinationMissing);
                return true;
            }
            PreparedSource prepared = PrepareSource(state, sourceIndex);
            if (prepared == null)
            {
                state.Retain(source, DuplicateBatchOrganizeSourceReason.NoCharts);
                return true;
            }

            // 「既に所持」を理由に移動を省く主ハッシュは、所持側の実ファイルの裏付けを確かめます。取れなければ、移動元の最後のコピーを
            // 消さないよう、直下の重複のごみ箱送りを含む物理変更の前にこの移動元を見送り、独立した次の移動元へ進みます。
            // 所持の基準は全予定移動元の配下を除いてあり、操作内で配置に成功した譜面だけが加わります。存在確認はロックの外で行います。
            string unverifiedChartPath = BmsLibraryPackageInstallService.FindUnverifiedAlreadyOwnedChartPath(
                prepared.Charts, ownershipOverlay, ownershipOverlay, [source]);
            if (unverifiedChartPath != null)
            {
                state.Retain(source, DuplicateBatchOrganizeSourceReason.OwnedFileMissing, unverifiedChartPath);
                return true;
            }

            List<ChartFile> duplicates = SelectSourceDuplicates(state, sourceIndex, prepared);
            if (duplicates.Count > 0)
            {
                // 型の衝突など、統合が事前に拒否する移動元では、ごみ箱へ送らず、重複した譜面も残します。
                // 拒否しない場合だけ、同じ事前確認を通した後で削除し、削除に成功した譜面は統合の入力から除きます。
                PackageInstallSessionMoveResult probe = Move(prepared, keep, probeOnly: true, out bool preflightPassed, out _);
                if (!preflightPassed)
                {
                    return HandleMoveFailure(groupIndex, state, sourceIndex, probe);
                }
                HashSet<string> trashed = TrashCharts(state, duplicates);
                prepared.Charts.RemoveAll(chart => trashed.Contains(chart.Path));
            }

            PackageInstallSessionMoveResult physicalMove = Move(prepared, keep, probeOnly: false, out _, out LibraryCatalogMutationFacts catalogFacts);
            if (!physicalMove.Succeeded)
            {
                return HandleMoveFailure(groupIndex, state, sourceIndex, physicalMove);
            }
            session.AppendFolderMerge(source, keep, catalogFacts, prepared.PackageReferenceFacts, physicalMove.PhysicalMutation);
            // 物理処理に成功した譜面だけを、後続の「既に所持している」判定と残存物の削除の裏付けへ加えます。
            ownershipOverlay.AddSuccessfulCharts(physicalMove.ExecutionResult.AddedCharts);
            HadPhysicalChange = true;
            state.Merged.Add(source);
            RegisterMaintenanceKeep(keep);
            return true;
        }

        private PreparedSource PrepareSource(GroupState state, int sourceIndex)
        {
            string source = state.Group.SourceDirectories[sourceIndex];
            string sourceKey = state.SourceKeys[sourceIndex];
            PreparedSource prepared = null;
            owner.RunWithMergeSnapshotLocks(() =>
            {
                List<ChartFile> candidates = sourceChartsByKey.TryGetValue(sourceKey, out List<ChartFile> captured)
                    ? [.. captured.Where(chart => !removedChartPaths.Contains(chart.Path))]
                    : [];
                LibraryPackageReferenceFacts referenceFacts = owner.PrepareMergeDirectory(
                    source,
                    state.Group.KeepDirectory,
                    candidates,
                    installDestinationOverlayChartRefs,
                    pendingPackages,
                    installedPackages,
                    // 所持判定は操作内の重ね合わせが持つため、移動元ごとにハッシュの基準を作りません。
                    _ => EmptyPrimaryHashLookup.Instance,
                    out bool success,
                    out List<ChartFile> preparedCharts,
                    out _);
                if (success)
                {
                    prepared = new PreparedSource(source, referenceFacts, preparedCharts);
                }
            });
            return prepared;
        }

        /// <summary>移動元直下の譜面のうち、実際に KEEP へ移す主ハッシュ（既に所持している判定に該当しないもの）の重複を、共用の選定規則で選びます。</summary>
        private List<ChartFile> SelectSourceDuplicates(GroupState state, int sourceIndex, PreparedSource prepared)
        {
            if (!directChartsByKey.TryGetValue(state.SourceKeys[sourceIndex], out List<ChartFile> direct))
            {
                return [];
            }
            var preparedTokens = new HashSet<OwnedChartToken>(prepared.Charts.Where(chart => chart.Token != null).Select(chart => chart.Token));
            return DuplicateChartCleanupSelector.SelectDeletions(direct.Where(chart =>
                chart.Token != null
                && preparedTokens.Contains(chart.Token)
                && !removedChartPaths.Contains(chart.Path)
                && ChartLookupKey.GetPrimaryHash(chart) is { } hash
                && !ownershipOverlay.ContainsPrimaryHash(hash)));
        }

        /// <summary>
        /// 統合の物理処理を行います。<paramref name="probeOnly"/> では、読み取りだけの事前確認を通した直後に、変更せず戻ります。
        /// </summary>
        /// <param name="preflightPassed">事前確認（型の衝突・欠落）を通ったかどうか。</param>
        private PackageInstallSessionMoveResult Move(
            PreparedSource prepared,
            string keep,
            bool probeOnly,
            out bool preflightPassed,
            out LibraryCatalogMutationFacts catalogFacts)
        {
            bool reached = false;
            PackageInstallSessionMoveResult result = owner.MoveMergeSourceForSession(
                CreateDetachedMergePackage(prepared.Charts, prepared.SourceDirectory),
                prepared.Charts,
                keep,
                notices,
                postLeaseNotifications,
                ownershipOverlay,
                ownershipOverlay,
                probeOnly
                    ? (_, _) =>
                    {
                        reached = true;
                        return false;
                    }
            : null,
                out catalogFacts);
            preflightPassed = probeOnly ? reached : result.Succeeded;
            return result;
        }

        /// <summary>
        /// 統合の物理処理が成立しなかった場合の扱いです。事前拒否は理由付きで残して継続し、予期しない失敗は止めてfalseを返します。
        /// </summary>
        private bool HandleMoveFailure(int groupIndex, GroupState state, int sourceIndex, PackageInstallSessionMoveResult failed)
        {
            string source = state.Group.SourceDirectories[sourceIndex];
            FileDbMutationReceipt receipt = failed.FailureReceipt;
            session.AppendPackagePhysicalFailure(receipt, failed.IsPreflightRefusal);
            if (failed.IsPreflightRefusal)
            {
                state.Retain(source, ClassifyRefusal(receipt));
                return true;
            }
            state.Retain(source, DuplicateBatchOrganizeSourceReason.PhysicalFailure);
            stopped = true;
            session.RecordStoppedSuffix(
                source,
                state.Group.KeepDirectory,
                receipt?.Failure ?? new System.IO.IOException("Duplicate merge physical prepare failed without a terminal receipt."),
                RemainingTargets(groupIndex, sourceIndex + 1));
            return false;
        }

        private static DuplicateBatchOrganizeSourceReason ClassifyRefusal(FileDbMutationReceipt receipt)
        {
            if (receipt?.DestinationTypeConflicts.Count > 0)
            {
                return DuplicateBatchOrganizeSourceReason.TypeConflict;
            }
            return receipt?.Failure switch
            {
                System.IO.FileNotFoundException => DuplicateBatchOrganizeSourceReason.SourceMissing,
                System.IO.DirectoryNotFoundException => DuplicateBatchOrganizeSourceReason.DestinationMissing,
                _ => DuplicateBatchOrganizeSourceReason.PreflightRefused
            };
        }

        /// <summary>全移動元の後に、KEEP 直下に元からある同じ主ハッシュの譜面を、共用の選定規則で一件にします。</summary>
        private void CleanupKeepDirectory(GroupState state)
        {
            if (!LongPathFileSystem.DirectoryExists(state.Group.KeepDirectory))
            {
                return;
            }
            List<ChartFile> deletions = DuplicateChartCleanupSelector.SelectDeletions(DirectCharts(state.KeepKey));
            if (deletions.Count > 0)
            {
                TrashCharts(state, deletions);
            }
        }

        /// <summary>
        /// 衝突整理の指定があり、移動元を一つ以上統合できたグループで、KEEP に同名の WAV と OGG が両方とも実在する名前の、捨てる形式のファイルをごみ箱へ送ります。
        /// 残す形式が実在しない名前は変えず、音源を復号・比較しません。ファイルは一件ずつ、譜面の削除と同じ窓口・設定で送ります。
        /// </summary>
        /// <remarks>
        /// 音源キーの集合は変わらないため、変更セッションへ事実を加えず、索引・通知へ追加の反映をしません。
        /// 名前ごとの失敗（パスの組立て・実在の確認・削除）は停止の理由にせず、そのファイルを残して数えます。
        /// 失敗を外へ出すと、予期しない物理失敗の経路が移動元の失敗として誤記録するため、プロセスの継続に関わる <see cref="OutOfMemoryException"/> 以外はここで受けます。
        /// </remarks>
        private void CleanupAudioCollisions(GroupState state)
        {
            DuplicateBatchAudioCollisionCleanup cleanup = state.Group.AudioCollisionCleanup;
            if (cleanup == null || state.Group.IsSingleFolder || state.Merged.Count == 0)
            {
                return;
            }
            var stopwatch = Stopwatch.StartNew();
            string keep = state.Group.KeepDirectory;
            foreach (string name in cleanup.Names)
            {
                try
                {
                    string keepPath = System.IO.Path.Combine(keep, name + cleanup.KeepExtension);
                    string discardPath = System.IO.Path.Combine(keep, name + cleanup.DiscardExtension);
                    // 指定を作った後にファイルが変わっていても、残す形式が実在しない名前の音源を失わないよう、残す形式、捨てる形式の順に実在を確かめます。
                    // 確認できなかった名前は例外のまま下で失敗として数え、削除を試みません。
                    if (!ObserveAudioFileExists(keepPath) || !ObserveAudioFileExists(discardPath))
                    {
                        continue;
                    }
                    owner.fileMutationService.DeleteFileShell(
                        discardPath,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                        owner.targetOnlyFileMutationOptions);
                    state.TrashedAudioCount++;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    state.AudioTrashFailureCount++;
                    try { owner.logFileWarning(exception, "duplicate_batch_organize_audio_trash_failed"); }
                    catch (Exception) { /* 診断の失敗は握りつぶします。 */ }
                }
            }
            state.AudioCleanupMilliseconds = stopwatch.ElapsedMilliseconds;
        }

        /// <summary>
        /// 音源のパスが、ファイルとして実在するかを属性の取得で観測します。内容は読みません。
        /// ファイル・途中の親ディレクトリがない対象とディレクトリは「存在しない」として false を返します。
        /// 属性を取得できてディレクトリでない対象は true です。リンクはリンク先をたどらず、リンク自身の属性で判断するため、リンク先の有無によらずファイルとして存在すると扱います。
        /// </summary>
        /// <exception cref="Exception">属性の取得が、存在しない以外の理由（アクセス拒否・入出力の失敗・パスとして扱えない名前など）で失敗したとき。呼出し側が失敗として数えます。</exception>
        private static bool ObserveAudioFileExists(string path)
        {
            System.IO.FileAttributes attributes;
            try
            {
                attributes = LongPathFileSystem.GetAttributes(path);
            }
            catch (Exception exception) when (LongPathFileSystem.IsNotFoundFailure(exception))
            {
                return false;
            }
            return (attributes & System.IO.FileAttributes.Directory) == 0;
        }

        private IEnumerable<ChartFile> DirectCharts(string directoryKey)
            => directChartsByKey.TryGetValue(directoryKey, out List<ChartFile> charts)
                ? charts.Where(chart => !removedChartPaths.Contains(chart.Path))
                : [];

        /// <summary>
        /// 重複した譜面をごみ箱へ送り、確定事実を操作全体のセッションへ加えます（行パス通知の方針は抑止）。
        /// 個別の削除の失敗は停止の理由にせず、失敗した譜面は残して数えます。フォルダ全体の承認は常に空です。
        /// </summary>
        /// <returns>ごみ箱へ送れた譜面のパス。</returns>
        private HashSet<string> TrashCharts(GroupState state, IReadOnlyList<ChartFile> charts)
        {
            state.Attempted = true;
            var prepared = new LibraryChartRemovalPreflight(charts, [], [], charts.Count, 0);
            LibraryCatalogMutationFacts catalogFacts = owner.ExecuteLibraryChartRemovalAfterAdmission(
                prepared,
                sendToRecycleBin: true,
                approvedWholeFolderDeletePaths: [],
                mutationCapability,
                out LibraryPackageReferenceFacts packageReferenceFacts,
                out _,
                out LibraryChartRemovalOutcome filesystemOutcome,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _,
                out IReadOnlyList<string> deletedFolderPaths);
            AppendLibraryChartRemovalToSession(
                session, catalogFacts, packageReferenceFacts, filesystemOutcome, deletedFolderPaths,
                LibraryStorageRowPathNotificationPolicy.Suppressed);
            var trashed = new HashSet<string>(
                filesystemOutcome.Targets.Where(target => target.State == LibraryChartRemovalState.Confirmed).Select(target => target.Path),
                StringComparer.OrdinalIgnoreCase);
            removedChartPaths.UnionWith(trashed);
            state.TrashedChartCount += trashed.Count;
            state.TrashFailureCount += charts.Count - trashed.Count;
            if (trashed.Count > 0)
            {
                HadPhysicalChange = true;
            }
            return trashed;
        }

        /// <summary>統合が成功した KEEP の保守対象を、確定後・入力元の後片付け前に、一度のロックで集める必須の後処理を一つだけ登録します。</summary>
        private void RegisterMaintenanceKeep(string keepDirectory)
        {
            if (mergedKeepSet.Add(keepDirectory))
            {
                mergedKeeps.Add(keepDirectory);
            }
            if (maintenanceFinalizerAppended)
            {
                return;
            }
            maintenanceFinalizerAppended = true;
            session.AppendRequiredDurableFinalizer(() => owner.RunWithMergeSnapshotLocks(() =>
            {
                var charts = new List<ChartFile>();
                foreach (string keep in mergedKeeps)
                {
                    charts.AddRange(owner.CreateOwnedStorageTargetChartSnapshotsForSubtreeDirectoryUnsafe(keep));
                }
                destinationMaintenanceCharts = charts;
            }));
        }

        /// <summary>予期しない失敗を、停止した位置と残りの未処理とともに変更セッションへ記録します。</summary>
        private void StopAfterUnexpectedFailure(int groupIndex, Exception failure)
        {
            GroupState state = states[groupIndex];
            stopped = true;
            int nextSourceIndex = state.CurrentSource == null ? 0 : state.IndexOfSource(state.CurrentSource) + 1;
            state.CompleteAfterStop();
            BmsLibraryPackageInstallService.RecordUnexpectedInstallSessionFailure(
                session,
                state.CurrentSource ?? state.Group.KeepDirectory,
                state.Group.KeepDirectory,
                failure,
                RemainingTargets(groupIndex, nextSourceIndex));
        }

        /// <summary>現在のグループの残りの移動元と、後続の全グループの対象を、未処理の対象として列挙します。</summary>
        private IReadOnlyList<LibraryMutationSessionTarget> RemainingTargets(int groupIndex, int nextSourceIndex)
        {
            var targets = new List<LibraryMutationSessionTarget>();
            IReadOnlyList<string> currentSources = states[groupIndex].Group.SourceDirectories;
            for (int index = nextSourceIndex; index < currentSources.Count; index++)
            {
                targets.Add(new LibraryMutationSessionTarget(currentSources[index], states[groupIndex].Group.KeepDirectory));
            }
            for (int next = groupIndex + 1; next < states.Length; next++)
            {
                DuplicateBatchOrganizeGroup group = states[next].Group;
                if (group.IsSingleFolder)
                {
                    targets.Add(new LibraryMutationSessionTarget(group.KeepDirectory, string.Empty));
                    continue;
                }
                targets.AddRange(group.SourceDirectories.Select(source => new LibraryMutationSessionTarget(source, group.KeepDirectory)));
            }
            return targets;
        }

        private void LogGroupResults(IReadOnlyList<DuplicateBatchOrganizeGroupResult> results)
        {
            for (int index = 0; index < results.Count; index++)
            {
                DuplicateBatchOrganizeGroupResult result = results[index];
                owner.LogInstallPerformance("duplicate_batch_organize group op=" + operationId
                    + " no=" + (index + 1)
                    + " keep=" + result.KeepDirectory
                    + " sources=" + string.Join("|", request.Groups[index].SourceDirectories)
                    + " status=" + result.Status
                    + (result.SkipReason == DuplicateBatchOrganizeSkipReason.None ? string.Empty : " skipReason=" + result.SkipReason + " skipPath=" + result.SkipPath)
                    + " merged=" + string.Join("|", result.MergedSources)
                    + " retained=" + string.Join("|", result.RetainedSources.Select(retained => retained.Directory + ":" + retained.Reason
                        + (retained.Detail == null ? string.Empty : "(" + retained.Detail + ")")))
                    + " trashed=" + result.TrashedChartCount
                    + " trashFailed=" + result.TrashFailureCount
                    + (request.Groups[index].AudioCollisionCleanup == null
                        ? string.Empty
                        : " audioTrashed=" + result.TrashedAudioCount
                            + " audioTrashFailed=" + result.AudioTrashFailureCount
                            + " audioMs=" + states[index].AudioCleanupMilliseconds));
            }
        }

        /// <summary>統合元一件の準備結果です。統合の入力に使う譜面は、ごみ箱へ送れた譜面を除いて更新します。</summary>
        private sealed class PreparedSource(string sourceDirectory, LibraryPackageReferenceFacts packageReferenceFacts, List<ChartFile> charts)
        {
            internal string SourceDirectory { get; } = sourceDirectory;

            internal LibraryPackageReferenceFacts PackageReferenceFacts { get; } = packageReferenceFacts;

            internal List<ChartFile> Charts { get; } = charts;
        }

        /// <summary>一グループの処理状況です。結果の値は <see cref="ToResult"/> で固定します。</summary>
        private sealed class GroupState
        {
            internal GroupState(DuplicateBatchOrganizeGroup group)
            {
                Group = group;
                KeepKey = Lr2FolderPath.NormalizeDirectoryPath(group.KeepDirectory);
                SourceKeys = [.. group.SourceDirectories.Select(Lr2FolderPath.NormalizeDirectoryPath)];
                FolderKeys = [KeepKey, .. SourceKeys];
            }

            internal DuplicateBatchOrganizeGroup Group { get; }

            internal string KeepKey { get; }

            /// <summary>各移動元の正規化したパス。正規化できない場合は null です。</summary>
            internal string[] SourceKeys { get; }

            /// <summary>KEEP と移動元の正規化したパス。</summary>
            internal string[] FolderKeys { get; }

            internal DuplicateBatchOrganizeSkipReason PreSkipReason { get; set; }

            internal bool HasNestedFolders { get; set; }

            internal bool OverlapsOtherGroup { get; set; }

            /// <summary>物理処理（統合またはごみ箱への削除）を試みる段階に入ったかどうかです。</summary>
            internal bool Attempted { get; set; }

            /// <summary>処理中の移動元です。予期しない失敗が起きた位置の記録に使います。</summary>
            internal string CurrentSource { get; set; }

            internal DuplicateBatchOrganizeGroupStatus Status { get; set; } = DuplicateBatchOrganizeGroupStatus.Unprocessed;

            internal DuplicateBatchOrganizeSkipReason SkipReason { get; private set; }

            internal string SkipPath { get; private set; }

            internal List<string> Merged { get; } = [];

            internal List<DuplicateBatchOrganizeRetainedSource> Retained { get; } = [];

            internal int TrashedChartCount { get; set; }

            internal int TrashFailureCount { get; set; }

            /// <summary>衝突整理でごみ箱へ送れた音源の数です。</summary>
            internal int TrashedAudioCount { get; set; }

            /// <summary>衝突整理でごみ箱へ送れなかった音源の数です。</summary>
            internal int AudioTrashFailureCount { get; set; }

            /// <summary>衝突整理に要した時間（ミリ秒）です。診断のログだけに使います。</summary>
            internal long AudioCleanupMilliseconds { get; set; }

            internal void Skip(DuplicateBatchOrganizeSkipReason reason, string path)
            {
                Status = DuplicateBatchOrganizeGroupStatus.Skipped;
                SkipReason = reason;
                SkipPath = path;
            }

            internal void Retain(string source, DuplicateBatchOrganizeSourceReason reason, string detail = null)
                => Retained.Add(new DuplicateBatchOrganizeRetainedSource(source, reason, detail));

            /// <summary>統合できた移動元の数と、残った移動元・削除の失敗から、グループの状態を決めます。</summary>
            internal void CompleteMergeStatus()
            {
                Status = Merged.Count == 0
                    ? DuplicateBatchOrganizeGroupStatus.Failed
                    : Retained.Count == 0 && TrashFailureCount == 0 && AudioTrashFailureCount == 0
                        ? DuplicateBatchOrganizeGroupStatus.Completed
                        : DuplicateBatchOrganizeGroupStatus.PartiallyCompleted;
            }

            /// <summary>
            /// 予期しない失敗で止まったグループの状態を決めます。処理中だった移動元は物理失敗、始めていない移動元は未処理として残します。
            /// 統合できた移動元がなければ失敗、あれば一部完了です。物理処理に入る前に止まったグループは、未処理のままです。
            /// </summary>
            internal void CompleteAfterStop()
            {
                if (!Attempted)
                {
                    return;
                }
                if (Group.IsSingleFolder)
                {
                    Status = TrashedChartCount > 0
                        ? DuplicateBatchOrganizeGroupStatus.PartiallyCompleted
                        : DuplicateBatchOrganizeGroupStatus.Failed;
                    return;
                }
                foreach (string source in Group.SourceDirectories)
                {
                    if (Merged.Contains(source, StringComparer.OrdinalIgnoreCase)
                        || Retained.Any(retained => string.Equals(retained.Directory, source, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    Retain(source, string.Equals(source, CurrentSource, StringComparison.OrdinalIgnoreCase)
                        ? DuplicateBatchOrganizeSourceReason.PhysicalFailure
                        : DuplicateBatchOrganizeSourceReason.NotProcessed);
                }
                Status = Merged.Count == 0
                    ? DuplicateBatchOrganizeGroupStatus.Failed
                    : DuplicateBatchOrganizeGroupStatus.PartiallyCompleted;
            }

            /// <summary>移動元の位置を返します。見つからない場合は -1 です。</summary>
            internal int IndexOfSource(string source)
            {
                for (int index = 0; index < Group.SourceDirectories.Count; index++)
                {
                    if (string.Equals(Group.SourceDirectories[index], source, StringComparison.OrdinalIgnoreCase))
                    {
                        return index;
                    }
                }
                return -1;
            }

            /// <summary>
            /// 結果の値を固定します。確定に失敗した操作では、物理的に成功していたグループ（統合した移動元またはごみ箱へ送った譜面があるもの）を失敗とし、
            /// 統合した移動元は確定していないものとして残した側へ移します。ごみ箱へ送った数は実数のまま保ちます。
            /// </summary>
            internal DuplicateBatchOrganizeGroupResult ToResult(bool commitFailed)
            {
                DuplicateBatchOrganizeGroupStatus status = Status;
                List<string> merged = Merged;
                List<DuplicateBatchOrganizeRetainedSource> retained = Retained;
                if (commitFailed
                    && status is DuplicateBatchOrganizeGroupStatus.Completed or DuplicateBatchOrganizeGroupStatus.PartiallyCompleted or DuplicateBatchOrganizeGroupStatus.Failed
                    && (Merged.Count > 0 || TrashedChartCount > 0))
                {
                    status = DuplicateBatchOrganizeGroupStatus.Failed;
                    merged = [];
                    retained = [.. Retained, .. Merged.Select(source => new DuplicateBatchOrganizeRetainedSource(source, DuplicateBatchOrganizeSourceReason.NotCommitted))];
                }
                return new DuplicateBatchOrganizeGroupResult(
                    Group.KeepDirectory,
                    Group.IsSingleFolder,
                    status,
                    SkipReason,
                    SkipPath,
                    merged,
                    retained,
                    TrashedChartCount,
                    TrashFailureCount,
                    TrashedAudioCount,
                    AudioTrashFailureCount);
            }
        }
    }
}
