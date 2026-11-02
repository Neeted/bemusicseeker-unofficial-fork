using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Files;
using Newtonsoft.Json;
using Ribbit.Net;
using static BeMusicSeeker.TestSupport.Components.PlaylistUrlOwnershipTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class PlaylistUrlAcquisitionOwnershipTests
{
    /// <summary>作品の配布候補だけをURI検査し、無関係リンク・追加物・別作品を除外して一件だけを保存します。未対応HTTPは元ページへ戻し、非HTTP本体だけは要求せず失敗します。</summary>
    [TestMethod]
    [DataRow("folder")]
    [DataRow("zip")]
    [DataRow("mirror")]
    [DataRow("shiftjis")]
    [DataRow("venue")]
    [DataRow("venue-none")]
    [DataRow("search")]
    [DataRow("flight")]
    [DataRow("nonhttp")]
    public async Task DownloadCandidate_SelectsOnlyCurrentWorkDistribution(string condition)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var owned = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        Uri page = new(condition.StartsWith("venue", StringComparison.Ordinal) || condition == "flight"
            ? "https://venue.bmssearch.net/test/30" : condition == "search" ? "https://bmssearch.net/bmses/current"
            : "https://manbow.nothing.sh/event/event.cgi?action=More_def&num=1&event=1");
        Uri archive = new("https://files.example.test/body.zip");
        string folder = "https://drive.google.com/drive/folders/unresolved";
        string unrelated = "<a href='mailto:author@example.test'>mail</a><a href='javascript:void(0)'>other</a>";
        string html = condition switch
        {
            "folder" => "<table><tr><th>DownloadAddress</th><td><a href='" + folder + "'>本体</a></td></tr></table>",
            "nonhttp" => "<table><tr><th>DownloadAddress</th><td><a href='file:///C:/body.zip'>本体</a></td></tr></table>",
            "mirror" or "shiftjis" => "<table><tr><TH> Down Load Address <td><a href='" + folder + "'>WAV本体</a><br>本体ミラー <a href='" + archive + "'>mirror</a><br>追加BGA mirror <a href='https://files.example.test/bga.zip'>mirror</a><tr><th>Comment<td><a href='https://files.example.test/comment.zip'>other</a></table>",
            "zip" => "<table><tr><th>DownloadAddress</th><td><a href='" + archive + "'>ogg本体</a></td></tr></table>",
            "search" => "<script type='application/json'>" + JsonConvert.SerializeObject(new
            {
                list = new object[] {
                new { id = "current", title = "省略形" },
                new { id = "other", downloads = new[] { new { url = "https://files.example.test/other.zip" } } },
                new { id = "current", downloads = new[] { new { url = archive.ToString() } }, relatedLinks = new[] { new { url = "https://files.example.test/related.zip" } } }
            }
            }) + "</script>",
            _ => string.Empty
        };
        if (condition.StartsWith("venue", StringComparison.Ordinal) || condition == "flight")
        {
            string json = JsonConvert.SerializeObject(new
            {
                bms = new
                {
                    number = 30,
                    exhibition = new { alias = "test", packages = new[] { new { downloadURL = "https://files.example.test/event.zip" } } },
                    description = "quote \" brace } 日本語",
                    downloadLinks = new[] {
                    new { type = "ADDITIONAL", downloadURL = "https://files.example.test/bga.zip" },
                    new { type = "CORE", downloadURL = folder },
                    new { type = condition == "venue-none" ? "ADDITIONAL" : "CORE", downloadURL = archive.ToString() }
                }
                },
                other = new { number = 31, exhibition = new { alias = "test" }, downloadLinks = new[] { new { type = "CORE", downloadURL = "https://files.example.test/other.zip" } } }
            });
            html = "<script type='application/json'>" + json + "</script>";
            if (condition == "flight")
            {
                string records = "1:I[\"ignored\"]\n2:HL[\"ignored\"]\n3:" + json + "\n";
                int split = records.Length / 2;
                html = "<script>self.__next_f.push(" + JsonConvert.SerializeObject(new object[] { 1, records[..split] }) + ")</script>"
                    + "<script>self.__next_f.push(" + JsonConvert.SerializeObject(new object[] { 1, records[split..] }) + ")</script>";
            }
            html += "<a href='https://files.example.test/bga.zip'>additional</a><a href='https://files.example.test/event.zip'>package</a>";
        }
        html = "<html><head>" + (condition == "shiftjis" ? "<meta http-equiv='Content-Type' content='text/html; charset=Shift_JIS'>" : "")
            + "</head><body>" + unrelated + html + "</body></html>";
        var gateway = new RecordingPlaylistUrlDownloadGateway(owned.RootDirectory);
        byte[] bytes = condition == "shiftjis" ? Encoding.GetEncoding("shift_jis").GetBytes(html) : Encoding.UTF8.GetBytes(html);
        gateway.AddResponse(page, () =>
        {
            AppHttpResponse response = CreateHttpResponse(page, bytes, "text/html");
            if (condition == "shiftjis") { (response.ContentHeaders.ContentType ?? throw new AssertFailedException()).CharSet = "unknown-charset"; }
            return response;
        });
        byte[] body = [1, 2, 3, 4];
        gateway.AddResponse(archive, () => CreateHttpResponse(archive, body, "application/octet-stream"));
        var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });
        PlaylistUrlDownloadResult result = await workflow.DownloadCandidateAsync(page);
        bool success = condition is not "folder" and not "venue-none" and not "nonhttp";
        Assert.AreEqual(success ? PlaylistUrlDownloadResultKind.Downloaded : condition == "nonhttp" ? PlaylistUrlDownloadResultKind.Failed : PlaylistUrlDownloadResultKind.BrowserFallback, result.Kind);
        Assert.AreEqual(condition == "nonhttp", result.IsUnsupportedScheme);
        Assert.AreEqual(success ? 1 : 0, gateway.OpenWriteCount);
        CollectionAssert.AreEqual(success ? new[] { page, archive } : new[] { page }, gateway.RequestedUris.ToArray());
        if (success) { CollectionAssert.AreEqual(body, File.ReadAllBytes(result.FilePath)); }
    }
    /// <summary>旧anchorページでも配布archiveだけを検査し、非HTTP本体の失敗と後続HTTP本体の成功を区別します。</summary>
    [TestMethod]
    [DataRow("venue", false)]
    [DataRow("venue", true)]
    [DataRow("search", false)]
    [DataRow("search", true)]
    public async Task DownloadCandidate_LegacyArchiveAnchorsPreserveSchemeValidation(string site, bool hasHttpArchive)
    {
        using var owned = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        Uri page = new(site == "venue" ? "https://venue.bmssearch.net/test/30" : "https://bmssearch.net/bmses/current");
        Uri archive = new("https://files.example.test/body.zip");
        string html = "<html><body><a href='mailto:author.zip@example.test'>mail</a><a href='javascript:body.zip'>action</a>"
            + "<a href='ftp://files.example.test/body.zip'>body</a>"
            + (hasHttpArchive ? "<a href='" + archive + "'>mirror</a>" : "") + "</body></html>";
        var gateway = new RecordingPlaylistUrlDownloadGateway(owned.RootDirectory);
        gateway.AddResponse(page, () => CreateHttpResponse(page, Encoding.UTF8.GetBytes(html), "text/html"));
        byte[] body = [7, 8, 9];
        gateway.AddResponse(archive, () => CreateHttpResponse(archive, body, "application/octet-stream"));
        var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });
        PlaylistUrlDownloadResult result = await workflow.DownloadCandidateAsync(page);
        Assert.AreEqual(hasHttpArchive ? PlaylistUrlDownloadResultKind.Downloaded : PlaylistUrlDownloadResultKind.Failed, result.Kind);
        Assert.AreEqual(!hasHttpArchive, result.IsUnsupportedScheme);
        Assert.AreEqual(hasHttpArchive ? 1 : 0, gateway.OpenWriteCount);
        CollectionAssert.AreEqual(hasHttpArchive ? new[] { page, archive } : new[] { page }, gateway.RequestedUris.ToArray());
        if (hasHttpArchive) { CollectionAssert.AreEqual(body, File.ReadAllBytes(result.FilePath)); }
    }

    /// <summary>競合するBOM・HTTP・metaの宣言と既定UTF8を、日本語の本体ミラー説明だけで選ばれる配布候補から判別します。</summary>
    [TestMethod]
    [DataRow("bom")]
    [DataRow("http")]
    [DataRow("meta")]
    [DataRow("default")]
    public async Task DownloadCandidate_DecodingPrioritySelectsJapaneseBodyMirror(string condition)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var owned = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        Uri page = new("https://manbow.nothing.sh/event/event.cgi?action=More_def&num=1&event=1");
        Uri archive = new("https://files.example.test/body.zip");
        string meta = condition == "http" ? "<meta charset='UTF-8'>" : condition == "meta" ? "<meta charset='Shift_JIS'>" : "";
        string html = "<html><head>" + meta + "</head><body><table><tr><th>DownloadAddress</th><td>"
            + "<a href='https://drive.google.com/drive/folders/unresolved'>original</a><br>本体ミラー <a href='" + archive + "'>download</a>"
            + "</td></tr></table></body></html>";
        byte[] bytes = condition is "http" or "meta" ? Encoding.GetEncoding("shift_jis").GetBytes(html) : Encoding.UTF8.GetBytes(html);
        if (condition == "bom") { bytes = [0xef, 0xbb, 0xbf, .. bytes]; }
        var gateway = new RecordingPlaylistUrlDownloadGateway(owned.RootDirectory);
        gateway.AddResponse(page, () =>
        {
            AppHttpResponse response = CreateHttpResponse(page, bytes, "text/html");
            if (condition is "bom" or "http") { (response.ContentHeaders.ContentType ?? throw new AssertFailedException()).CharSet = "Shift_JIS"; }
            return response;
        });
        byte[] body = [9, 4, 2];
        gateway.AddResponse(archive, () => CreateHttpResponse(archive, body, "application/octet-stream"));
        var workflow = new PlaylistUrlAcquisitionWorkflow(gateway, _ => { });
        PlaylistUrlDownloadResult result = await workflow.DownloadCandidateAsync(page);
        Assert.AreEqual(PlaylistUrlDownloadResultKind.Downloaded, result.Kind);
        CollectionAssert.AreEqual(new[] { page, archive }, gateway.RequestedUris.ToArray());
        Assert.AreEqual(1, gateway.OpenWriteCount); CollectionAssert.AreEqual(body, File.ReadAllBytes(result.FilePath));
    }

}
