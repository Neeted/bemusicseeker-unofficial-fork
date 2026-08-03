using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;
using Ribbit.Media.Audio;
using Un4seen.Bass;
using RibbitBassNet = Ribbit.Media.Audio.BassNet;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BassNativeRuntimeTests
{
    [TestInitialize]
    public void ResetNativeRuntime()
    {
        RibbitBassNet.Shutdown();
    }

    [TestMethod]
    public void NativeFamily_LoadsFixedX64AssetsAndMatchesAbiSet()
    {
        string[] expectedNames =
        {
            "bass.dll",
            "bassasio.dll",
            "basswasapi.dll",
            "bassmix.dll",
            "bass_fx.dll",
            "bassenc.dll"
        };
        string[] expectedHashes =
        {
            "93A913CA11A3466C73A0984F23FEE570BCB29576A00FABE39E3CA1565588BEF3",
            "CA830E47D2F7DBED9EE9F885EA4387A9AB20FF20261F82258B3F48043CDCDE80",
            "C0FE6416A6F59EE33CF617FB9C05D3F0BCB05EF47D6B71759710D2BB5BE5C0B0",
            "9D207EAF880CB5C401285CC75CDF8430C0CD274D8C31D1B02B597F790C99BF34",
            "572F29C568F05EF2E0FE689B5D928A0B546D902E3D6A673D33D7F61F3BC7BCDB",
            "02CD21F2D47244FD0EF15D652524B5A4BEB3A986AC1BF64A756F901C6633E53A"
        };

        string nativeDirectory = Path.Combine(AppContext.BaseDirectory, "libs", "x64");
        Assert.IsTrue(Path.IsPathFullyQualified(nativeDirectory));
        Assert.AreEqual("x64", Path.GetFileName(nativeDirectory));
        for (int index = 0; index < expectedNames.Length; index++)
        {
            string path = Path.Combine(nativeDirectory, expectedNames[index]);
            Assert.IsTrue(File.Exists(path), path);
            Assert.AreEqual(expectedHashes[index], Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }

        BassNativeRuntime.Load();
        try
        {
            BassNativeRuntime.ValidateSupportedVersions();
        }
        finally
        {
            BassNativeRuntime.Free();
        }

    }

    [TestMethod]
    public void BassNet_InitializesAndReleasesNativeRuntime()
    {
        RibbitBassNet.Initialize();
        bool bassInitialized = false;
        try
        {
            bassInitialized = Bass.BASS_Init(0, 44100, BASSInit.BASS_DEVICE_NOSPEAKER, IntPtr.Zero);
            Assert.IsTrue(bassInitialized, Bass.BASS_ErrorGetCode().ToString());
            BassNativeRuntime.ValidateSupportedVersions();
            RibbitBassNet.FreeDevice();
            bassInitialized = false;
            Assert.AreEqual(0x02040C01, Bass.BASS_GetVersion());
            bassInitialized = Bass.BASS_Init(0, 44100, BASSInit.BASS_DEVICE_NOSPEAKER, IntPtr.Zero);
            Assert.IsTrue(bassInitialized, Bass.BASS_ErrorGetCode().ToString());
        }
        finally
        {
            if (bassInitialized)
            {
                RibbitBassNet.Shutdown();
            }
            else
            {
                BassNativeRuntime.Free();
            }
        }

    }

    [TestMethod]
    public void BassAudioPlayer_StaticInitializationDoesNotEnumerateOrLoadNativeRuntime()
    {
        WeakReference loadContextReference = null;
        try
        {
            RunStaticInitializationInCollectibleContext(out loadContextReference);
        }
        finally
        {
            WaitForCollectibleContextUnload(loadContextReference);
        }

        Assert.IsFalse(loadContextReference.IsAlive, "The collectible audio test context must unload before another WPF test resolves application resources.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunStaticInitializationInCollectibleContext(out WeakReference loadContextReference)
    {
        string applicationAssemblyPath = typeof(BassAudioPlayer).Assembly.Location;
        var loadContext = new IsolatedApplicationLoadContext(applicationAssemblyPath);
        loadContextReference = new WeakReference(loadContext, trackResurrection: true);
        try
        {
            Assembly applicationAssembly = loadContext.LoadFromAssemblyPath(applicationAssemblyPath);
            Type runtimeType = applicationAssembly.GetType("Ribbit.Media.Audio.BassNativeRuntime", throwOnError: true)!;
            Type enumeratorType = applicationAssembly.GetType("Ribbit.Media.Audio.BassAudioDeviceEnumerator", throwOnError: true)!;
            Type playerType = applicationAssembly.GetType("Ribbit.Media.BassAudioPlayer", throwOnError: true)!;

            int nativeLoadCountBefore = ReadStaticCounter(runtimeType, "LoadInvocationCount");
            int enumerationCountBefore = ReadStaticCounter(enumeratorType, "EnumerationInvocationCount");

            RuntimeHelpers.RunClassConstructor(playerType.TypeHandle);

            Assert.AreEqual(nativeLoadCountBefore, ReadStaticCounter(runtimeType, "LoadInvocationCount"));
            Assert.AreEqual(enumerationCountBefore, ReadStaticCounter(enumeratorType, "EnumerationInvocationCount"));
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static void WaitForCollectibleContextUnload(WeakReference loadContextReference)
    {
        for (int attempt = 0; attempt < 10 && loadContextReference?.IsAlive == true; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    [TestMethod]
    public void InitializeOwned_ActiveSessionRejectsScopedAndUnscopedReentry()
    {
        BassAudioSession ownedSession = null;
        try
        {
            BassAudioPlayer.InitializeOwned(
                BassAudioPlayer.DeviceDriver.NULL_DEVICE,
                default,
                0f,
                out ownedSession);

            Assert.ThrowsException<AudioInitializationException>(() => BassAudioPlayer.InitializeOwned(
                BassAudioPlayer.DeviceDriver.NULL_DEVICE,
                default,
                0f,
                out _));
            Assert.AreSame(ownedSession, BassAudioPlayer.ActiveSession);

            Assert.ThrowsException<AudioInitializationException>(() => BassAudioPlayer.Initialize(
                BassAudioPlayer.DeviceDriver.NULL_DEVICE));
            Assert.AreSame(ownedSession, BassAudioPlayer.ActiveSession);
        }
        finally
        {
            BassAudioPlayer.Free(ownedSession);
            RibbitBassNet.Shutdown();
        }
    }

    private static int ReadStaticCounter(Type type, string propertyName)
    {
        return (int)type.GetProperty(
            propertyName,
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    }

    private sealed class IsolatedApplicationLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver;

        internal IsolatedApplicationLoadContext(string applicationAssemblyPath)
            : base(isCollectible: true)
        {
            resolver = new AssemblyDependencyResolver(applicationAssemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string? assemblyPath = resolver.ResolveAssemblyToPath(assemblyName);
            return assemblyPath == null ? null : LoadFromAssemblyPath(assemblyPath);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? libraryPath = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return libraryPath == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(libraryPath);
        }
    }
}
