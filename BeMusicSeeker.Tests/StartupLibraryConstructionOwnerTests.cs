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

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class StartupLibraryConstructionOwnerTests
{
    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => opCode.Value);

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
                new[] { "factory-library", "application-library", "factory-playlist", "application-services" },
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

    [TestMethod]
    public void MainWindowInitializeAsync_UsesTypedStartupConstructionOwnerRoute()
    {
        MethodInfo initializeAsync = typeof(MainWindowViewModel).GetMethod(
            "InitializeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainWindowViewModel.InitializeAsync was not found.");
        AsyncStateMachineAttribute stateMachineAttribute = initializeAsync.GetCustomAttribute<AsyncStateMachineAttribute>()
            ?? throw new InvalidOperationException("InitializeAsync must remain an async state machine.");
        MethodInfo moveNext = stateMachineAttribute.StateMachineType.GetMethod(
            "MoveNext",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("InitializeAsync state machine MoveNext was not found.");

        IReadOnlyList<MethodBase> calledMethods = EnumerateCalledMethods(moveNext).ToArray();
        string[] startupOwnerCalls = calledMethods
            .Where(method => method.DeclaringType == typeof(StartupLibraryConstructionOwner))
            .Where(method => method.Name == "CreateAndApply")
            .Select(method => method.Name)
            .ToArray();

        CollectionAssert.AreEqual(new[] { "CreateAndApply" }, startupOwnerCalls);
        Assert.IsFalse(
            calledMethods.Any(method =>
                method.DeclaringType == typeof(ApplicationComposition)
                && method.Name is "CreateBmsLibrary" or "CreateBmsPlaylist"),
            "InitializeAsync must use the typed startup owner instead of calling composition factories directly.");
    }

    private static ApplicationComposition CreateComposition()
    {
        var settings = new BeMusicSeeker.Properties.Settings
        {
            OperationModeLR2DB = false
        };
        return new ApplicationComposition(
            bmsLibraryOptionsProvider: () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false },
            settingsEditSession: new NoOpSettingsEditSession(settings),
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog());
    }

    private static LibraryProfile CreateProfile(string temporaryRoot, string songDbPath)
        => CreateProfile(temporaryRoot, songDbPath, operationModeLR2DB: false, searchRoots: [temporaryRoot]);

    private static LibraryProfile CreateProfile(
        string temporaryRoot,
        string songDbPath,
        bool operationModeLR2DB,
        IReadOnlyList<string> searchRoots)
    {
        return new LibraryProfile(
            operationModeLR2DB,
            songDbPath: songDbPath,
            searchRoots: searchRoots,
            lr2ConfigProvider: () => null!,
            lr2ScoreDbPath: null,
            canWriteLr2Config: false,
            canOutputLr2Folders: false,
            canUseLr2Backup: false,
            canUseLr2IrScore: false,
            startupRequiredFileScanReason: "startup-owner-test");
    }

    private static string CreateTemporaryRoot()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_StartupLibraryOwner_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateSongDatabase(string songDbPath)
    {
        using var initialize = new LR2SongDBExtended(songDbPath);
    }

    private static IEnumerable<MethodBase> EnumerateCalledMethods(MethodBase method)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
        {
            yield break;
        }

        int offset = 0;
        while (offset < il.Length)
        {
            OpCode opCode = ReadOpCode(il, ref offset);
            int operandOffset = offset;
            if (opCode.OperandType == OperandType.InlineMethod)
            {
                int metadataToken = BitConverter.ToInt32(il, operandOffset);
                MethodBase? calledMethod = null;
                try
                {
                    calledMethod = method.Module.ResolveMethod(
                        metadataToken,
                        method.DeclaringType?.GetGenericArguments(),
                        method.IsGenericMethod ? method.GetGenericArguments() : null);
                }
                catch (ArgumentException)
                {
                }
                if (calledMethod != null)
                {
                    yield return calledMethod;
                }
            }

            offset += GetOperandSize(opCode.OperandType, il, operandOffset);
        }
    }

    private static OpCode ReadOpCode(byte[] il, ref int offset)
    {
        byte first = il[offset++];
        short value = first == 0xfe
            ? unchecked((short)(0xfe00 | il[offset++]))
            : first;
        return OpCodesByValue[value];
    }

    private static int GetOperandSize(OperandType operandType, byte[] il, int operandOffset)
    {
        return operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod
                or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
                or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + (BitConverter.ToInt32(il, operandOffset) * 4),
            _ => throw new InvalidOperationException($"Unsupported IL operand type: {operandType}."),
        };
    }

    private sealed class RecordingDelegatingStartupLibraryFactory : IStartupLibraryFactory
    {
        private readonly IStartupLibraryFactory inner;

        internal RecordingDelegatingStartupLibraryFactory(IStartupLibraryFactory inner, List<string> calls)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            Calls = calls ?? throw new ArgumentNullException(nameof(calls));
        }

        internal List<string> Calls { get; }

        internal LibraryProfile? LibraryProfile { get; private set; }

        internal LibraryProfile? PlaylistProfile { get; private set; }

        internal BMSLibrary? CreatedLibrary { get; private set; }

        internal BMSPlaylist? CreatedPlaylist { get; private set; }

        internal BMSLibrary? PlaylistLibrary { get; private set; }

        internal bool ThrowOnPlaylist { get; set; }

        internal bool ReturnNullPlaylist { get; set; }

        internal bool ThrowOnLibrary { get; set; }

        internal IReadOnlyList<string> InitialSearchTargets { get; set; } = [];

        public BMSLibrary CreateBmsLibrary(LibraryProfile libraryProfile)
        {
            Calls.Add("factory-library");
            if (ThrowOnLibrary)
            {
                throw new InvalidOperationException("recording library factory failure");
            }
            LibraryProfile = libraryProfile;
            CreatedLibrary = inner.CreateBmsLibrary(libraryProfile);
            CreatedLibrary.SearchTargets.AddRange(InitialSearchTargets);
            return CreatedLibrary;
        }

        public BMSPlaylist CreateBmsPlaylist(LibraryProfile libraryProfile, BMSLibrary library)
        {
            Calls.Add("factory-playlist");
            PlaylistProfile = libraryProfile;
            PlaylistLibrary = library;
            if (ThrowOnPlaylist)
            {
                throw new InvalidOperationException("recording playlist failure");
            }
            if (ReturnNullPlaylist)
            {
                return null!;
            }

            CreatedPlaylist = inner.CreateBmsPlaylist(libraryProfile, library);
            return CreatedPlaylist;
        }

    }

    private sealed class RecordingStartupLibraryApplicationPort : IStartupLibraryApplicationPort
    {
        private readonly List<string> calls;

        internal RecordingStartupLibraryApplicationPort(List<string> calls)
        {
            this.calls = calls ?? throw new ArgumentNullException(nameof(calls));
        }

        internal BMSLibrary? AttachedLibrary { get; private set; }

        internal StartupLibraryServices? AttachedServices { get; private set; }

        internal BMSLibrary? SearchRootsLibrary { get; private set; }

        internal IReadOnlyList<string> SearchRootsObserved { get; private set; } = [];

        internal bool ThrowOnLibrary { get; set; }

        internal bool ThrowOnServices { get; set; }

        public void AttachStartupLibrary(BMSLibrary library)
        {
            calls.Add("application-library");
            AttachedLibrary = library;
            if (ThrowOnLibrary)
            {
                throw new InvalidOperationException("recording library application failure");
            }
        }

        public void AttachStartupServices(StartupLibraryServices services)
        {
            calls.Add("application-services");
            AttachedServices = services;
            SearchRootsLibrary = services.Library;
            SearchRootsObserved = services.Library.SearchTargets.ToArray();
            if (ThrowOnServices)
            {
                throw new InvalidOperationException("recording services application failure");
            }
        }
    }

    private sealed class ThrowingSearchRoots : IReadOnlyList<string>
    {
        public int Count => 1;

        public string this[int index] => "unused";

        public IEnumerator<string> GetEnumerator()
            => throw new InvalidOperationException("recording search-root enumeration failure");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class NullStartupLibraryFactory : IStartupLibraryFactory
    {
        internal int LibraryCallCount { get; private set; }

        internal int PlaylistCallCount { get; private set; }

        public BMSLibrary CreateBmsLibrary(LibraryProfile libraryProfile)
        {
            LibraryCallCount++;
            return null!;
        }

        public BMSPlaylist CreateBmsPlaylist(LibraryProfile libraryProfile, BMSLibrary library)
        {
            PlaylistCallCount++;
            return null!;
        }
    }
}
