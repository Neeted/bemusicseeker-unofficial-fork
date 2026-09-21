using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.StartupLibraryConstructionTestSupport;
namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class StartupLibraryFailureContractTests
{
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
                new[] { "factory-library", "application-library", "factory-playlist" },
                events);
            CollectionAssert.AreEqual(Array.Empty<string>(), application.SearchRootsObserved.ToArray());
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

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
                ReturnNullPlaylist = true
            };
            var application = new RecordingStartupLibraryApplicationPort(events);
            var owner = new StartupLibraryConstructionOwner(factory);
            profile = CreateProfile(temporaryRoot, songDbPath);

            InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(
                () => owner.CreateAndApply(profile, application));

            StringAssert.Contains(exception.Message, "Startup playlist factory returned null");
            CollectionAssert.AreEqual(
                new[] { "factory-library", "application-library", "factory-playlist" },
                events);
            Assert.AreSame(factory.CreatedLibrary, application.AttachedLibrary);

            events.Clear();
            factory.ReturnNullPlaylist = false;
            factory.ThrowOnLibrary = true;
            exception = Assert.ThrowsException<InvalidOperationException>(
                () => owner.CreateAndApply(profile, application));
            StringAssert.Contains(exception.Message, "recording library factory failure");
            CollectionAssert.AreEqual(new[] { "factory-library" }, events);

            events.Clear();
            factory.ThrowOnLibrary = false;
            factory.ReturnNullPlaylist = false;
            factory.ThrowOnPlaylist = true;
            exception = Assert.ThrowsException<InvalidOperationException>(
                () => owner.CreateAndApply(profile, application));
            StringAssert.Contains(exception.Message, "recording playlist failure");
            CollectionAssert.AreEqual(
                new[] { "factory-library", "application-library", "factory-playlist" },
                events);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

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
            CollectionAssert.AreEqual(new[] { "factory-library", "application-library" }, libraryEvents);

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
                new[] { "factory-library", "application-library", "factory-playlist", "application-services" },
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
