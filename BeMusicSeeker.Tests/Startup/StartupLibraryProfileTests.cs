using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.StartupLibraryConstructionTestSupport;
namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class StartupLibraryProfileTests
{
    /// <summary>
    /// 保証：同じプロファイルでライブラリを先に作り、そのライブラリへプレイリストを接続してから適用し、同じ確定readerから登録を捕捉する。
    /// </summary>
    /// <remarks>本番の作成処理へ委譲して、プロファイルの受渡しだけでなく作成したプレイリストとライブラリの実接続を確認する。</remarks>
    [TestMethod]
    public void StartupLibraryConstructionOwner_CreatesLibraryThenPlaylistFromSameProfile()
    {
        string temporaryRoot = CreateTemporaryRoot();
        string songDbPath = Path.Combine(temporaryRoot, "song.db");
        CreateSongDatabase(songDbPath);
        try
        {
            Settings settings = TestBmsFactory.CreateRootSettings([Path.Combine(temporaryRoot, "root-a"), Path.Combine(temporaryRoot, "root-b")]);
            ApplicationComposition composition = CreateComposition(settings);
            var events = new List<string>();
            var factory = new RecordingDelegatingStartupLibraryFactory(composition, events);
            var application = new RecordingStartupLibraryApplicationPort(events);
            var owner = new StartupLibraryConstructionOwner(factory);
            LibraryProfile profile = CreateProfile(
                temporaryRoot,
                songDbPath,
                operationModeLR2DB: false,
                searchRoots:
                [
                    Path.Combine(temporaryRoot, "root-a"),
                    Path.Combine(temporaryRoot, "root-a"),
                    Path.Combine(temporaryRoot, "root-b")
                ]);

            StartupLibraryServices services = owner.CreateAndApply(profile, application);

            CollectionAssert.AreEqual(
                new[] { "factory-library", "factory-playlist", "application-library", "application-services" },
                events);
            Assert.AreSame(profile, factory.LibraryProfile);
            Assert.AreSame(profile, factory.PlaylistProfile);
            Assert.AreSame(factory.CreatedLibrary, factory.PlaylistLibrary);
            Assert.AreSame(factory.CreatedLibrary, services.Library);
            Assert.AreSame(profile, services.Profile);
            Assert.AreSame(factory.CreatedPlaylist, services.Playlist);
            Assert.AreSame(factory.CreatedLibrary, factory.CreatedPlaylist!.LibraryBindings.SourceLibrary);
            Assert.AreSame(services.Library, application.AttachedLibrary);
            Assert.AreSame(services, application.AttachedServices);
            Assert.AreEqual(songDbPath, profile.SongDbPath);
            Assert.IsTrue(File.Exists(profile.SongDbPath));
            CollectionAssert.AreEqual(new[] { Path.Combine(temporaryRoot, "root-a"), Path.Combine(temporaryRoot, "root-b") }, application.SearchRootsObserved.ToArray());
            Assert.AreSame(factory.CreatedLibrary, application.SearchRootsLibrary);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    /// <summary>
    /// 保証：LR2モードの構築では独立モード用検索先を適用せず、起動プロファイルのルートでLR2検索設定を上書きしない。
    /// </summary>
    /// <remarks>本番の作成処理へ委譲し、実XMLの登録がprofileの別一覧で上書きされないことを観測する。</remarks>
    [TestMethod]
    public void StartupLibraryConstructionOwner_DoesNotApplySearchRootsForLr2Profile()
    {
        string temporaryRoot = CreateTemporaryRoot();
        string songDbPath = Path.Combine(temporaryRoot, "song.db");
        CreateSongDatabase(songDbPath);
        try
        {
            string actualRoot = Directory.CreateDirectory(Path.Combine(temporaryRoot, "xml-root")).FullName;
            Settings settings = TestBmsFactory.CreateRootSettings([actualRoot], lr2: true);
            ApplicationComposition composition = CreateComposition(settings);
            var events = new List<string>();
            var factory = new RecordingDelegatingStartupLibraryFactory(composition, events);
            var application = new RecordingStartupLibraryApplicationPort(events);
            LibraryProfile profile = CreateProfile(
                temporaryRoot,
                songDbPath,
                operationModeLR2DB: true,
                searchRoots: [
                    Path.Combine(temporaryRoot, "lr2-root"),
                    Path.Combine(temporaryRoot, "lr2-root")], lr2ConfigOwner: composition.ResolveLr2Config(settings.LR2ConfigXmlPath));

            StartupLibraryServices services = new StartupLibraryConstructionOwner(factory)
                .CreateAndApply(profile, application);

            Assert.AreSame(factory.CreatedLibrary, services.Library);
            Assert.AreSame(factory.CreatedLibrary, application.SearchRootsLibrary);
            CollectionAssert.AreEqual(new[] { actualRoot }, application.SearchRootsObserved.ToArray());
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

}
