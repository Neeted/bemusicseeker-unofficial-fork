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

internal static class StartupLibraryConstructionTestSupport
{
    internal static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => opCode.Value);


    internal static ApplicationComposition CreateComposition()
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

    internal static LibraryProfile CreateProfile(string temporaryRoot, string songDbPath)
        => CreateProfile(temporaryRoot, songDbPath, operationModeLR2DB: false, searchRoots: [temporaryRoot]);

    internal static LibraryProfile CreateProfile(
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

    internal static string CreateTemporaryRoot()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_StartupLibraryOwner_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static void CreateSongDatabase(string songDbPath)
    {
        using var initialize = new LR2SongDBExtended(songDbPath);
    }

    internal static IEnumerable<MethodBase> EnumerateCalledMethods(MethodBase method)
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

    internal static OpCode ReadOpCode(byte[] il, ref int offset)
    {
        byte first = il[offset++];
        short value = first == 0xfe
            ? unchecked((short)(0xfe00 | il[offset++]))
            : first;
        return OpCodesByValue[value];
    }

    internal static int GetOperandSize(OperandType operandType, byte[] il, int operandOffset)
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

    internal sealed class RecordingDelegatingStartupLibraryFactory : IStartupLibraryFactory
    {
        internal readonly IStartupLibraryFactory inner;

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

    internal sealed class RecordingStartupLibraryApplicationPort : IStartupLibraryApplicationPort
    {
        internal readonly List<string> calls;

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

    internal sealed class ThrowingSearchRoots : IReadOnlyList<string>
    {
        public int Count => 1;

        public string this[int index] => "unused";

        public IEnumerator<string> GetEnumerator()
            => throw new InvalidOperationException("recording search-root enumeration failure");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal sealed class NullStartupLibraryFactory : IStartupLibraryFactory
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
