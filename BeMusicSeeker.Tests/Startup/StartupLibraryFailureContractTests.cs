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
public sealed class StartupLibraryFailureContractTests
{
    /// <summary>
    /// 保証：検索先の列挙失敗を元の原因で返し、途中で作成した組を画面へ適用したり空検索先で成功へ読み替えたりしない。
    /// </summary>
    /// <remarks>本番ライブラリ・プレイリストの作成へ委譲し、列挙失敗と適用先への呼出し順を観測する。</remarks>
    [TestMethod]
    public void StartupLibraryConstructionOwner_PropagatesSearchRootEnumerationFailure()
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
                operationModeLR2DB: false,
                searchRoots: new ThrowingSearchRoots());

            InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(
                () => new StartupLibraryConstructionOwner(factory).CreateAndApply(profile, application));

            StringAssert.Contains(exception.Message, "recording search-root enumeration failure");
            CollectionAssert.AreEqual(
                new[] { "factory-library", "factory-playlist" },
                events);
            CollectionAssert.AreEqual(Array.Empty<string>(), application.SearchRootsObserved.ToArray());
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    /// <summary>
    /// 保証：作成先がnullまたは例外の場合は途中組だけを停止して旧組と共有受付を維持し、後の成功時だけ旧組を停止する。
    /// </summary>
    /// <remarks>本番のモデル組を作り、失敗時と次の成功時の実停止状態・新規受付の成立まで観測する。</remarks>
    [TestMethod]
    public void StartupLibraryConstructionOwner_PropagatesFactoryNullAndExceptions()
    {
        LibraryProfile profile = CreateProfile("startup-owner-null", "startup-owner-null.db");
        var nullFactory = new NullStartupLibraryFactory();
        var nullOwner = new StartupLibraryConstructionOwner(nullFactory);
        var nullApplication = new RecordingStartupLibraryApplicationPort([]);

        Assert.ThrowsException<InvalidOperationException>(() => nullOwner.CreateAndApply(profile, nullApplication));
        Assert.AreEqual(1, nullFactory.LibraryCallCount);
        Assert.AreEqual(0, nullFactory.PlaylistCallCount);

        string temporaryRoot = CreateTemporaryRoot();
        string songDbPath = Path.Combine(temporaryRoot, "song.db");
        CreateSongDatabase(songDbPath);
        try
        {
            ApplicationComposition composition = CreateComposition();
            var events = new List<string>();
            var factory = new RecordingDelegatingStartupLibraryFactory(composition, events)
            {
                ReturnNullPlaylist = false
            };
            var application = new RecordingStartupLibraryApplicationPort(events);
            var owner = new StartupLibraryConstructionOwner(factory);
            profile = CreateProfile(temporaryRoot, songDbPath);
            StartupLibraryServices previous = owner.CreateAndApply(profile, application);
            events.Clear();
            application = new RecordingStartupLibraryApplicationPort(events);
            factory.ReturnNullPlaylist = true;

            InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(
                () => owner.CreateAndApply(profile, application, previousLibrary: previous.Library, previousPlaylist: previous.Playlist));

            StringAssert.Contains(exception.Message, "Startup playlist factory returned null");
            CollectionAssert.AreEqual(
                new[] { "factory-library", "factory-playlist" },
                events);
            Assert.IsNull(application.AttachedLibrary);
            Assert.IsTrue((factory.CreatedLibrary ?? throw new InvalidOperationException("Construction did not create the library.")).IsShutdownRequested);
            Assert.IsFalse(previous.Library.IsShutdownRequested);
            Assert.IsFalse(previous.Playlist.IsShutdownRequested);
            Assert.IsFalse(composition.OperationAdmission.IsAdmissionClosed);
            Assert.IsFalse(composition.PlaylistOperationAdmission.IsAdmissionClosed);

            events.Clear();
            factory.ReturnNullPlaylist = false;
            factory.ThrowOnLibrary = true;
            exception = Assert.ThrowsException<InvalidOperationException>(
                () => owner.CreateAndApply(profile, application, previousLibrary: previous.Library, previousPlaylist: previous.Playlist));
            StringAssert.Contains(exception.Message, "recording library factory failure");
            CollectionAssert.AreEqual(new[] { "factory-library" }, events);

            events.Clear();
            factory.ThrowOnLibrary = false;
            factory.ReturnNullPlaylist = false;
            factory.ThrowOnPlaylist = true;
            exception = Assert.ThrowsException<InvalidOperationException>(
                () => owner.CreateAndApply(profile, application, previousLibrary: previous.Library, previousPlaylist: previous.Playlist));
            StringAssert.Contains(exception.Message, "recording playlist failure");
            CollectionAssert.AreEqual(
                new[] { "factory-library", "factory-playlist" },
                events);
            Assert.IsFalse(previous.Library.IsShutdownRequested);
            Assert.IsFalse(previous.Playlist.IsShutdownRequested);
            factory.ThrowOnPlaylist = false;
            StartupLibraryServices current = owner.CreateAndApply(profile, application, previousLibrary: previous.Library, previousPlaylist: previous.Playlist);
            Assert.IsTrue(previous.Library.IsShutdownRequested);
            Assert.IsTrue(previous.Playlist.IsShutdownRequested);
            Assert.IsFalse(current.Library.IsShutdownRequested);
            Assert.IsFalse(current.Playlist.IsShutdownRequested);
            Assert.IsTrue(composition.OperationAdmission.TryEnter(out IDisposable libraryLease));
            libraryLease.Dispose();
            Assert.IsTrue(composition.PlaylistOperationAdmission.TryEnter(out IDisposable playlistLease));
            playlistLease.Dispose();
            current.Playlist.RequestStop("test_cleanup");
            current.Library.RequestStop("test_cleanup");
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    /// <summary>
    /// 保証：ライブラリ適用・サービス適用の各失敗はその段階の例外で返し、後段へ進まず、先に適用済みの事実は保持する。
    /// </summary>
    /// <remarks>本番の作成へ委譲し、適用窓口の失敗と呼出し順・受信済みモデルを確認する。</remarks>
    [TestMethod]
    public void StartupLibraryConstructionOwner_PropagatesApplicationExceptions()
    {
        string temporaryRoot = CreateTemporaryRoot();
        string songDbPath = Path.Combine(temporaryRoot, "song.db");
        CreateSongDatabase(songDbPath);
        try
        {
            LibraryProfile profile = CreateProfile(temporaryRoot, songDbPath);
            ApplicationComposition composition = CreateComposition();

            var libraryEvents = new List<string>();
            var libraryFactory = new RecordingDelegatingStartupLibraryFactory(composition, libraryEvents);
            var libraryApplication = new RecordingStartupLibraryApplicationPort(libraryEvents)
            {
                ThrowOnLibrary = true
            };
            InvalidOperationException libraryException = Assert.ThrowsException<InvalidOperationException>(
                () => new StartupLibraryConstructionOwner(libraryFactory).CreateAndApply(profile, libraryApplication));
            StringAssert.Contains(libraryException.Message, "recording library application failure");
            CollectionAssert.AreEqual(new[] { "factory-library", "factory-playlist", "application-library" }, libraryEvents);

            var servicesEvents = new List<string>();
            var servicesFactory = new RecordingDelegatingStartupLibraryFactory(composition, servicesEvents);
            var servicesApplication = new RecordingStartupLibraryApplicationPort(servicesEvents)
            {
                ThrowOnServices = true
            };
            InvalidOperationException servicesException = Assert.ThrowsException<InvalidOperationException>(
                () => new StartupLibraryConstructionOwner(servicesFactory).CreateAndApply(profile, servicesApplication));
            StringAssert.Contains(servicesException.Message, "recording services application failure");
            CollectionAssert.AreEqual(
                new[] { "factory-library", "factory-playlist", "application-library", "application-services" },
                servicesEvents);
            Assert.AreSame(servicesFactory.CreatedLibrary, servicesApplication.AttachedLibrary);
            Assert.AreSame(servicesFactory.CreatedPlaylist, servicesApplication.AttachedServices?.Playlist);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

}
