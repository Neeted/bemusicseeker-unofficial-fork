using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.TestSupport.Components.SharedPlaylistWorkspaceFixture;
using PlaylistWorkspaceViewModelTests = BeMusicSeeker.Tests.PlaylistWorkspacePersistenceCommandTests;

namespace BeMusicSeeker.Tests;


/// <summary>backup・復元・export・BMT順序・dropの実確定と、summary buildの世代・取消・終端を検証します。</summary>
/// <remarks>実SQLite、出力、configの確定順と失敗後の事実は実資源を用い、UIは既存schedulerの受理・実行・完了を制御します。画面の所有modalと入力操作はWPF代表が分担し、process DB lock競争だけ非並列です。</remarks>
[TestClass]
public sealed class PlaylistWorkspacePersistenceCommandTests
{

    /// <summary>異なるドライブへ実出力した参照は、rawドライブパスでなく明示file:絶対URIでdata内容へ到達します。ケース所有別名はTask・通知終端後に解除し、物理領域を最後に回収します。</summary>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    public async Task PlaylistWorkspaceExportPlaylistTableAsync_DifferentDriveWritesAbsoluteFileReference()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceViewModelTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            // Zは予約・照会・定義の前に除外し、別名の実領域はこのケース配下だけに置きます。
            using var drive = OwnedTestDrive.Create(tempDirectory, 'Z');
            try
            {
                drive.AssertOwned();
                SerializationObservedTable table = CreateJsonExportTable();
                var dialogs = new PlaylistWorkspaceFixturePorts.PlaylistWorkspaceDialogService();
                PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _, playlistWorkspaceDialogService: dialogs);
                List<PlaylistOperationNotificationPresentationRequestedEventArgs> notifications = [];
                workspace.PlaylistOperationNotificationPresentationRequested += (_, request) => notifications.Add(request);
                string headerPath = Path.Combine(tempDirectory, "header.json");
                string dataPath = Path.Combine(drive.Root, "data.json");
                table.DataUrlReadObserver = () => AssertJsonExportSourceUrls(table, null);

                dialogs.JsonExportResults.Enqueue(new(UiDialogStatus.Accepted, new(headerPath, dataPath)));
                await workspace.ExportPlaylistTableAsync(table);

                drive.AssertOwned();
                AssertExportedJsonDataReference(headerPath, dataPath, absoluteFileReference: true);
                AssertJsonExportSourceUrls(table, null);
                Assert.IsTrue(notifications.Single().Receipt.IsEmpty);
            }
            finally { drive.AssertOwned(); }
        }
        finally { Directory.Delete(tempDirectory, recursive: true); }
    }

    private static void AssertJsonExportSourceUrls(SerializationObservedTable table, Uri? dataUrl)
    {
        Assert.AreEqual(dataUrl, table.Data_url, "シリアライズ中も終端後も正本Data_urlを保持します。");
        Assert.AreEqual(new Uri("https://example.test/export-header.json"), table.Header_url);
        Assert.AreEqual(new Uri("https://example.test/export-page/"), table.Page_url);
    }

    private static SerializationObservedTable CreateJsonExportTable() => new()
    {
        name = "Export",
        symbol = "EX",
        Header_url = new Uri("https://example.test/export-header.json"),
        Page_url = new Uri("https://example.test/export-page/"),
        Folder_order = [],
        entries = [new BMSTableEntry { md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", title = "Export chart", folder = "12", level = 12 }]
    };

    private static void AssertExportedJsonDataReference(string headerPath, string dataPath, bool absoluteFileReference)
    {
        var header = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(headerPath));
        string dataReference = (string?)header["data_url"] ?? throw new AssertFailedException("data_urlを出力します。");
        Assert.IsFalse(string.IsNullOrEmpty(dataReference));
        var referenceUri = new Uri(dataReference, UriKind.RelativeOrAbsolute);
        Assert.AreEqual(absoluteFileReference, referenceUri.IsAbsoluteUri);
        if (absoluteFileReference)
        {
            Assert.IsTrue(dataReference.StartsWith("file:", StringComparison.OrdinalIgnoreCase),
                "rawドライブパスをfile URIの代わりに出力しません。");
            Assert.IsTrue(referenceUri.IsFile);
        }

        // 文書のcanonical file URIを基準にし、implicit-file baseの%二重escapeをoracleへ持ち込みません。
        var headerDocumentUri = new Uri(new Uri(Path.GetFullPath(headerPath)).AbsoluteUri);
        var resolvedDataUri = new Uri(headerDocumentUri, referenceUri);
        Assert.IsTrue(resolvedDataUri.IsAbsoluteUri);
        Assert.IsTrue(resolvedDataUri.IsFile);
        Assert.IsTrue(StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(dataPath), resolvedDataUri.LocalPath),
            "出力参照は選択したdata完全パスへ解決します。");
        var data = Newtonsoft.Json.Linq.JArray.Parse(File.ReadAllText(resolvedDataUri.LocalPath));
        Assert.AreEqual("Export", (string?)header["name"]);
        Assert.AreEqual("EX", (string?)header["symbol"]);
        Assert.AreEqual(1, data.Count);
        Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", (string?)data[0]["md5"]);
        Assert.AreEqual("Export chart", (string?)data[0]["title"]);
        Assert.AreEqual("12", (string?)data[0]["level"]);
    }

    private sealed class SerializationObservedTable : BMSTable
    {
        internal Action? DataUrlReadObserver { get; set; }

        public override string data_url
        {
            get { DataUrlReadObserver?.Invoke(); return base.data_url; }
            set { base.data_url = value; }
        }
    }

}
