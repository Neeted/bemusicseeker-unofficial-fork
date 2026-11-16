using System.Globalization;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Localization;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using Resources = BeMusicSeeker.Properties.Resources;

namespace BeMusicSeeker.Tests;

/// <summary>プレイリスト同期状態の全分岐が、追加辞書と同梱辞書の言語往復で同じ状態インスタンスのまま現在の資源値へ追従することを確認します。</summary>
/// <remarks>共有Resourcesの文化圏と実行専用profileの辞書を一時的に変更するため並列実行しません。</remarks>
[TestClass]
[DoNotParallelize]
public sealed class PlaylistSyncStatusLanguageTests
{
    private static readonly (PlaylistSyncStatusKind Kind, string Key)[] Branches =
    [
        (PlaylistSyncStatusKind.None, nameof(Resources.Playlist_sync_status_none)),
        (PlaylistSyncStatusKind.Ok, nameof(Resources.Playlist_sync_status_ok)),
        (PlaylistSyncStatusKind.Updated, nameof(Resources.Playlist_sync_status_updated)),
        (PlaylistSyncStatusKind.HeaderNotFound, nameof(Resources.Playlist_sync_status_header_url)),
        (PlaylistSyncStatusKind.HeaderParseError, nameof(Resources.Playlist_sync_status_header)),
        (PlaylistSyncStatusKind.DataParseError, nameof(Resources.Playlist_sync_status_data)),
        (PlaylistSyncStatusKind.InvalidDataUrl, nameof(Resources.Playlist_sync_status_invalid_url)),
        (PlaylistSyncStatusKind.Http404, nameof(Resources.Playlist_sync_status_404)),
        (PlaylistSyncStatusKind.Http403, nameof(Resources.Playlist_sync_status_403)),
        (PlaylistSyncStatusKind.HttpError, nameof(Resources.Playlist_sync_status_http)),
        (PlaylistSyncStatusKind.NetworkError, nameof(Resources.Playlist_sync_status_network)),
        (PlaylistSyncStatusKind.UnknownError, nameof(Resources.Playlist_sync_status_unknown)),
    ];

    private sealed record StatusFacts(PlaylistSyncStatusKind Kind, int SortOrder, bool Failure, DateTime CheckedAt, string PageUri, string Diagnostic);

    private static StatusFacts FactsOf(PlaylistSyncRuntimeStatus status) =>
        new(status.Kind, status.StatusSortOrder, status.HasFailureStatus, status.CheckedAt, status.PageUri, status.Diagnostic);

    private static PlaylistSyncAttemptResult CreateResult(PlaylistSyncStatusKind kind, Uri pageUri)
    {
        return kind switch
        {
            PlaylistSyncStatusKind.Ok => PlaylistSyncAttemptResult.CreateSuccess(null, null, pageUri, updated: false),
            PlaylistSyncStatusKind.Updated => PlaylistSyncAttemptResult.CreateSuccess(null, null, pageUri, updated: true),
            _ => new PlaylistSyncAttemptResult
            {
                Succeeded = false,
                FailureKind = kind,
                PageUri = pageUri,
                Detail = "diagnostic " + kind + " 原文"
            }
        };
    }

    /// <summary>12分岐の StatusText がその時点の辞書値になり、Detail の先頭行は現在の StatusText で、時刻・PageUri・診断・種別・整列順・失敗判定・検査日時が往復で変わらないことを確認します。</summary>
    [TestMethod]
    public void SyncStatus_AllBranchesFollowAdditionalAndBundledDictionaries()
    {
        const string additionalCulture = "it-IT";
        CultureInfo? previousResourcesCulture = Resources.Culture;
        string languageDirectory = ApplicationPathPolicy.Current.LanguageDirectory;
        string languagePath = Path.Combine(languageDirectory, additionalCulture + ".json");
        byte[]? languageBackup = File.Exists(languagePath) ? File.ReadAllBytes(languagePath) : null;
        try
        {
            Assert.AreEqual(Path.GetFullPath(Path.Combine(ApplicationPathPolicy.Current.ProfileDirectory, "lang")), Path.GetFullPath(languageDirectory));
            Directory.CreateDirectory(languageDirectory);
            File.WriteAllText(languagePath, "{\"_language_name\":\"Sync status regression\","
                + string.Join(",", Branches.Select(branch => "\"" + branch.Key + "\":\"ADDL " + branch.Key + "\"")) + "}");
            JsonLanguageCatalog.Invalidate(additionalCulture);
            JsonLanguageCatalog.InvalidateLanguages();
            ResourceService.Current.ChangeCulture("ja-JP");

            DateTime checkedAt = new(2026, 3, 8, 20, 0, 0);
            var pageUri = new Uri("https://example.invalid/table.html");
            var statuses = new Dictionary<PlaylistSyncStatusKind, PlaylistSyncRuntimeStatus>();
            foreach ((PlaylistSyncStatusKind kind, _) in Branches)
            {
                statuses[kind] = kind == PlaylistSyncStatusKind.None
                    ? PlaylistSyncStatusMapper.CreateNone()
                    : PlaylistSyncStatusMapper.Create(CreateResult(kind, pageUri), checkedAt);
            }
            // 実記録経路で保持された状態も、同じ分岐の対象に含めます。
            PlaylistWorkspaceViewModel workspace = PlaylistWorkspaceTestPorts.CreateProgressWorkspace(action => action());
            var recordedKeys = new Dictionary<PlaylistSyncStatusKind, string>();
            int playlistId = 9100;
            foreach ((PlaylistSyncStatusKind kind, _) in Branches.Where(branch => branch.Kind != PlaylistSyncStatusKind.None))
            {
                var table = new BMSTable { playlist_id = playlistId };
                PlaylistSyncAttemptResult result = CreateResult(kind, pageUri);
                result.SourceTable = table;
                result.ResultTable = table;
                workspace.RecordPlaylistSyncResult(result);
                recordedKeys[kind] = "id:" + playlistId;
                playlistId++;
            }
            IReadOnlyDictionary<string, PlaylistSyncRuntimeStatus> captured = workspace.CapturePlaylistSyncStatusSnapshot();
            var recorded = recordedKeys.ToDictionary(pair => pair.Key, pair => captured[pair.Value]);

            var factsBefore = statuses.ToDictionary(pair => pair.Key, pair => FactsOf(pair.Value));
            var recordedFactsBefore = recorded.ToDictionary(pair => pair.Key, pair => FactsOf(pair.Value));

            string Expected(string dictionary, string key)
            {
                if (dictionary == "added") { return "ADDL " + key; }
                Assert.IsTrue(JsonLanguageCatalog.TryGetString(dictionary, key, out string value), key);
                return value;
            }

            void AssertStage(string dictionary, Dictionary<string, string> seen)
            {
                foreach ((PlaylistSyncStatusKind kind, string key) in Branches)
                {
                    string expected = Expected(dictionary, key);
                    foreach ((PlaylistSyncRuntimeStatus status, StatusFacts before, string source) in new[]
                    {
                        (statuses[kind], factsBefore[kind], "mapper"),
                        kind == PlaylistSyncStatusKind.None ? (statuses[kind], factsBefore[kind], "mapper") : (recorded[kind], recordedFactsBefore[kind], "recorded"),
                    })
                    {
                        string context = dictionary + " " + kind + " " + source;
                        Assert.AreEqual(expected, status.StatusText, context);
                        Assert.AreEqual(before, FactsOf(status), context);
                        string[] lines = status.Detail.Split(Environment.NewLine);
                        if (kind == PlaylistSyncStatusKind.None)
                        {
                            Assert.AreEqual(string.Empty, status.Detail, context);
                            continue;
                        }
                        Assert.AreEqual(expected, lines[0], context);
                        bool failed = kind is not (PlaylistSyncStatusKind.Ok or PlaylistSyncStatusKind.Updated);
                        string[] rest = source == "mapper"
                            ? ["2026-03-08 20:00:00", pageUri.ToString(), .. failed ? new[] { "diagnostic " + kind + " 原文" } : []]
                            : [lines[1], pageUri.ToString(), .. failed ? new[] { "diagnostic " + kind + " 原文" } : []];
                        CollectionAssert.AreEqual(rest, lines[1..], context);
                    }
                    seen[dictionary + ":" + key] = expected;
                }
            }

            var seenValues = new Dictionary<string, string>();
            AssertStage("ja-JP", seenValues);
            foreach ((string culture, string dictionary) in new[] { (additionalCulture, "added"), ("ja-JP", "ja-JP"), (additionalCulture, "added"), ("en-US", "en-US") })
            {
                ResourceService.Current.ChangeCulture(culture);
                AssertStage(dictionary, seenValues);
            }
            foreach ((_, string key) in Branches)
            {
                Assert.AreNotEqual(seenValues["ja-JP:" + key], seenValues["added:" + key], key + " 追加辞書は日本語と異なる値です。");
                Assert.AreNotEqual(seenValues["en-US:" + key], seenValues["added:" + key], key + " 追加辞書は英語と異なる値です。");
            }
        }
        finally
        {
            if (languageBackup != null) { File.WriteAllBytes(languagePath, languageBackup); }
            else { File.Delete(languagePath); }
            JsonLanguageCatalog.Invalidate(additionalCulture);
            JsonLanguageCatalog.InvalidateLanguages();
            ResourceService.Current.ChangeCulture(previousResourcesCulture?.Name ?? "en-US");
            Resources.Culture = previousResourcesCulture;
        }
    }
}
