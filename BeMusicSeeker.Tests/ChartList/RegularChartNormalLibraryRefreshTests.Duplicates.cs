using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class RegularChartNormalLibraryRefreshTests
{
    /// <summary>実重複検索後に一件だけ文字コードを確定し、空検索/新語加入/旧語離脱で表示済み・検索外入力、Title順、再選択を更新します。空検索は通知配送前の再選択も固定し、実再調査で両形式の健康度・固有警告と、固有兄弟・旧snapshot・未訪問行も維持します。</summary>
    [DataTestMethod]
    [DataRow("", false)]
    [DataRow("日本語", false)]
    [DataRow("old", false)]
    [DataRow("", true)]
    public void DuplicateCurrentValues_RealEncodingAndRescanPatchOnlyAffectedRows(string keyword, bool reselectBeforeDelivery)
    {
        WithTemporarySongDb(path => RunAsync(path).GetAwaiter().GetResult());

        async Task RunAsync(string path)
        {
            using var fixture = new RegularScoreDeliveryTestFixture(path);
            string root = Path.GetDirectoryName(path) ?? throw new InvalidOperationException();
            string firstDirectory = Path.Combine(root, "duplicates-a");
            string secondDirectory = Path.Combine(root, "duplicates-b");
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            ChartFile ReadBms(string directory, string name, string text)
            {
                string chartPath = Path.Combine(directory, name + ".bms");
                File.WriteAllText(chartPath, text, new UTF8Encoding(false));
                return BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            }
            // CP932資源解析でも次の物理行を保ち、今回の変更をメタデータの復号だけに限定する。
            const string contents = "#PLAYER 1\n#TITLE Alpha 日本語 title\n#ARTIST 新作者 artist\n#WAV01 missing.wav\n#00111:01\n";
            ChartFile target = ReadBms(firstDirectory, "target", contents) with
            { Title = "Zulu old", RawTitle = "Zulu old", Artist = "Old artist", RawArtist = "Old artist" };
            ChartFile sameHash = ReadBms(secondDirectory, "duplicate", contents) with { Title = "Middle duplicate", RawTitle = "Middle duplicate" };
            ChartFile[] siblings = Enumerable.Range(0, 32).Select(index => ReadBms(firstDirectory, "unique-" + index,
                "#TITLE Middle sibling " + index + "\n#WAV01 missing.wav\n#00111:01\n")).ToArray();
            string bmsonPath = Path.Combine(secondDirectory, "duplicate.bmson");
            File.WriteAllText(bmsonPath, "{\"info\":{\"title\":\"Middle bmson\",\"artist\":\"Artist\"},\"sound_channels\":[{\"name\":\"missing.wav\",\"notes\":[{\"x\":1,\"y\":0}]}]}");
            string bmsonSiblingPath = Path.Combine(firstDirectory, "duplicate.bmson");
            File.Copy(bmsonPath, bmsonSiblingPath);
            ChartFile bmson = ChartTestValues.ReadBmson(bmsonPath);
            ChartFile bmsonSibling = ChartTestValues.ReadBmson(bmsonSiblingPath);
            Assert.IsTrue(target.Resources.Any(reference => reference.IsUsed && reference.Kind == ChartResourceKind.Audio));
            Assert.IsTrue(bmson.Resources.Any(reference => reference.IsUsed && reference.Kind == ChartResourceKind.Audio));
            ChartFile background = ReadBms(root, "background", "#TITLE Background\n");
            new BmsLibraryDbGateway(path).UpsertSongs([target, sameHash, .. siblings, background, bmson, bmsonSibling]);
            fixture.Library.BmsCharts = [target, sameHash, .. siblings, background];
            fixture.Library.BmsonCharts = [bmson, bmsonSibling];
            fixture.Library.SearchDuplicateChartGroups();
            await DeliverReferencesAsync(fixture);
            DuplicateGroup group = fixture.Library.DuplicateChartGroups.Single();
            Assert.AreEqual(36, group.ChartFiles.Count);
            string header = group.Header;
            ChartFile capturedTarget = group.ChartFiles.Single(chart => chart.Path == target.Path);
            OwnedDuplicateChartRowSnapshot oldSnapshot = OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(fixture.Library)
                .Collection.CreateDuplicateChartRowSnapshot();
            RegularChartListEntryRequest request = CreateEntryRequest(MainViewUpdateMode.DuplicateFilterSelected, keyword);
            request.Library = fixture.Library;
            request.TreeParameter = DuplicateViewContext.CaptureGroup(group);
            Assert.IsTrue(fixture.Owner.ApplyRegularView(request).WasCommitted);
            ChartListVirtualView view = fixture.View;
            LibraryChartRow? displayedTarget = keyword == "日本語" ? null : (LibraryChartRow)view[view.Count - 1];
            if (displayedTarget != null) { Assert.AreEqual(target.Path, displayedTarget.Chart.Path); }
            int realizedBefore = view.RealizedRowCount;
            var work = new Dictionary<string, int>();
            fixture.Table.RowProjection.NormalInputWorkObserver = (kind, count) => work[kind] = work.GetValueOrDefault(kind) + count;
            int presentationCalls = 0;
            fixture.Owner.NormalLibraryRefreshApplied += (_, args) =>
            {
                Assert.AreEqual(MainViewRefreshAction.RefreshDisplay, fixture.Owner.ApplyNotificationPresentation(
                    fixture.Library, args.NotificationBatch, request.Mode, null, request.Filters, false));
                presentationCalls++;
            };
            fixture.Library.SetBMSFilesEncoding([capturedTarget], "utf-8");
            if (reselectBeforeDelivery)
            {
                Task delivery = fixture.Owner.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
                ScheduledScoreOperation pending = await fixture.Scheduler.NextAsync();
                Assert.AreEqual(0, presentationCalls, "確定済みカタログの通知配送は再選択まで止めます。");
                request.Stopwatch = Stopwatch.StartNew();
                Assert.IsTrue(fixture.Owner.ApplyRegularView(request).WasCommitted);
                view = fixture.View;
                Assert.AreEqual(0, view.RealizedRowCount, "現在値の再捕捉でも全行を具体化しません。");
                displayedTarget = null;
                realizedBefore = 0;
                work.Clear();
                pending.Run();
                pending.Complete();
                await delivery;
            }
            else { await DeliverReferencesAsync(fixture); }
            Assert.IsTrue(presentationCalls > 0);
            Assert.AreSame(view, fixture.Table.Rows);
            Assert.AreEqual(keyword == "old" ? 0 : realizedBefore, view.RealizedRowCount, "離脱行を解放し、対象外行と新規加入行を具体化しません。");
            Assert.AreEqual(0, work.GetValueOrDefault("hash_dependency_visit"));
            Assert.AreEqual(0, work.GetValueOrDefault("order_buffer"));
            Assert.AreEqual(0, work.GetValueOrDefault("projection_buffer"));
            Assert.IsTrue(work.GetValueOrDefault("source_visit") <= 4);
            Assert.IsTrue(work.GetValueOrDefault("projection") <= 1);
            Assert.IsTrue(work.GetValueOrDefault("order_key") <= 1);
            if (keyword == "old") { Assert.AreEqual(0, view.Count); }
            else
            {
                var updated = (LibraryChartRow)view[0];
                Assert.AreEqual(target.Path, updated.Chart.Path);
                Assert.AreEqual("Alpha 日本語 title", updated.Title);
                Assert.AreEqual("新作者 artist", updated.Artist);
                Assert.AreEqual("utf-8", updated.encoding);
                if (displayedTarget != null) { Assert.AreSame(displayedTarget, updated); }
            }
            Assert.AreEqual("Zulu old", capturedTarget.Title);
            Assert.AreEqual("Zulu old", oldSnapshot.Rows.Single(row => row.Path == target.Path).Chart.Title);
            Assert.AreSame(group, fixture.Library.DuplicateChartGroups.Single(), "共通値だけの変更で重複全解析を起動しません。");
            Assert.AreEqual(header, group.Header);
            using (LR2SongDBExtended saved = new BmsLibraryDbGateway(path).OpenSongDbReadOnly())
            {
                Assert.AreEqual("Alpha 日本語 title", saved.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", target.Path));
                Assert.AreEqual("新作者 artist", saved.ExecuteScalar<string>("SELECT artist FROM song WHERE path = ?;", target.Path));
            }
            fixture.Table.RowProjection.NormalInputWorkObserver = null;
            request.Filters = ChartListFilterSnapshot.Default;
            request.Stopwatch = Stopwatch.StartNew();
            Assert.IsTrue(fixture.Owner.ApplyRegularView(request).WasCommitted);
            Assert.AreEqual("Alpha 日本語 title", ((LibraryChartRow)fixture.View[0]).Title, "同じ検索結果の再選択で旧値を戻しません。");
            if (keyword.Length > 0) { return; }

            Assert.IsTrue(RegularChartListOwner.TryResolveDuplicateSource([group], DuplicateViewContext.CaptureGroup(group), out RegularChartListSubsetSource source));
            Assert.IsTrue(fixture.Owner.TryApplyVirtualChartSubset(new()
            {
                Library = fixture.Library,
                SourceCharts = source.SourceCharts,
                SourceProjectionMode = source.SourceProjectionMode,
                ApplyResourceHealthProjection = true,
                SubsetName = source.Name,
                TreeMode = request.Mode,
                ModeFilter = ChartModeFilter.All,
                SortColumnName = nameof(LibraryChartRow.WAVHealth),
                SortDirection = ListSortDirection.Ascending,
                ColumnSelection = new MainChartListColumnSelection(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD), false, 0,
                    request.Mode, Visibility.Collapsed, new PlaylistSummaryColumnSettings()),
                Mode = request.Mode,
                Stopwatch = Stopwatch.StartNew()
            }).WasCommitted);
            view = fixture.View;
            var first = (LibraryChartRow)view[4];
            Assert.AreEqual(siblings[0].Path, first.Chart.Path);
            fixture.Table.SelectedIndex = 4;
            int unrelatedNotifications = 0;
            first.PropertyChanged += (_, _) => unrelatedNotifications++;
            ChartFile[] current = fixture.Library.CaptureNormalLibraryChartSnapshot().BmsRows.ToArray();
            ChartFile currentTarget = current.Single(chart => chart.Path == target.Path);
            ChartFile currentBmson = fixture.Library.CaptureNormalLibraryChartSnapshot().BmsonRows.Single(chart => chart.Path == bmsonPath);
            work.Clear();
            fixture.Table.RowProjection.NormalInputWorkObserver = (kind, count) => work[kind] = work.GetValueOrDefault(kind) + count;
            MaintenanceWorkflowResult scan = fixture.Library.RescanResourceHealthCharts([currentTarget, currentBmson]);
            Assert.AreEqual(2, scan.CheckedFileCount);
            Assert.AreEqual(0, scan.ChangedCharts.Single(chart => chart.Path == target.Path).WAVHealth, "BMSの実再調査で不足を確定します。");
            Assert.AreEqual(0, scan.ChangedCharts.Single(chart => chart.Path == bmson.Path).WAVHealth, "BMSONの実再調査で不足を確定します。");
            await DeliverReferencesAsync(fixture);
            Assert.AreSame(view, fixture.Table.Rows);
            Assert.AreEqual(1, view.RealizedRowCount);
            Assert.IsTrue(work.GetValueOrDefault("source_visit") <= 4);
            Assert.IsTrue(work.GetValueOrDefault("projection") <= 2);
            Assert.AreEqual(0, work.GetValueOrDefault("hash_dependency_visit"));
            Assert.AreEqual(0, work.GetValueOrDefault("order_buffer"));
            Assert.AreEqual(0, unrelatedNotifications);
            Assert.AreSame(first, view[2]);
            Assert.AreEqual(2, fixture.Table.SelectedIndex);
            LibraryChartRow[] rescanned = [((LibraryChartRow)view[view.Count - 2]), ((LibraryChartRow)view[view.Count - 1])];
            CollectionAssert.AreEquivalent(new[] { target.Path, bmson.Path }, rescanned.Select(row => row.Chart.Path).ToArray());
            foreach (LibraryChartRow row in rescanned)
            {
                Assert.AreEqual(0, row.WAVHealth);
                Assert.IsTrue(row.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart));
                StringAssert.Contains(row.WarningDigestText, BeMusicSeeker.Properties.Resources.WarningDigest_ResourceMissing);
            }
            Assert.IsFalse(first.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart), "固有の兄弟譜面へ重複警告を付けません。");
            Assert.AreSame(group, fixture.Library.DuplicateChartGroups.Single());
            Assert.AreEqual(36, view.Count);
            Assert.IsNull(fixture.Library.CaptureNormalLibraryChartSnapshot().BmsRows.Single(chart => chart.Path == background.Path).WAVHealth);
            fixture.Table.RowProjection.NormalInputWorkObserver = null;
            request.Stopwatch = Stopwatch.StartNew();
            Assert.IsTrue(fixture.Owner.ApplyRegularView(request).WasCommitted);
            Assert.AreEqual(0, ((LibraryChartRow)fixture.View[0]).WAVHealth);
            Assert.AreEqual(0, ((LibraryChartRow)fixture.View[2]).WAVHealth);
        }
    }
}
