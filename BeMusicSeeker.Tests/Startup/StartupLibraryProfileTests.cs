using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.StartupLibraryConstructionTestSupport;
namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class StartupLibraryProfileTests
{
    /// <summary>
    /// 保証：同じプロファイルでライブラリを先に作り、そのライブラリへプレイリストを接続してから適用し、既存検索先に指定検索先を渡す。
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
            ApplicationComposition composition = CreateComposition();
            var events = new List<string>();
            var factory = new RecordingDelegatingStartupLibraryFactory(composition, events)
            {
                InitialSearchTargets = [Path.Combine(temporaryRoot, "pre-existing")]
            };
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
            string[] expectedSearchTargets =
            [
                Path.Combine(temporaryRoot, "pre-existing"),
                .. profile.SearchRoots
            ];
            CollectionAssert.AreEqual(expectedSearchTargets, application.SearchRootsObserved.ToArray());
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
    /// <remarks>本番の作成処理へ委譲し、受信側ライブラリの検索先も空のままであることを観測する。</remarks>
    [TestMethod]
    public void StartupLibraryConstructionOwner_DoesNotApplySearchRootsForLr2Profile()
    {
        string temporaryRoot = CreateTemporaryRoot();
        string songDbPath = Path.Combine(temporaryRoot, "song.db");
        CreateSongDatabase(songDbPath);
        try
        {
            ApplicationComposition composition = CreateComposition();
            var events = new List<string>();
            var factory = new RecordingDelegatingStartupLibraryFactory(composition, events);
            var application = new RecordingStartupLibraryApplicationPort(events);
            LibraryProfile profile = CreateProfile(
                temporaryRoot,
                songDbPath,
                operationModeLR2DB: true,
                searchRoots: [
                    Path.Combine(temporaryRoot, "lr2-root"),
                    Path.Combine(temporaryRoot, "lr2-root")]);

            StartupLibraryServices services = new StartupLibraryConstructionOwner(factory)
                .CreateAndApply(profile, application);

            Assert.AreSame(factory.CreatedLibrary, services.Library);
            Assert.AreSame(factory.CreatedLibrary, application.SearchRootsLibrary);
            CollectionAssert.AreEqual(Array.Empty<string>(), application.SearchRootsObserved.ToArray());
            CollectionAssert.AreEqual(Array.Empty<string>(), services.Library.SearchTargets.ToArray());
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

}
