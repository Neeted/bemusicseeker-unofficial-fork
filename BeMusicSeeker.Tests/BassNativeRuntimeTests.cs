using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLog;
using NLog.Config;
using NLog.Targets;
using Ribbit.Logging;
using Ribbit.Media;
using Ribbit.Media.Audio;
using ManagedBass;
using ManagedBass.Enc;
using ManagedBass.Mix;
using BassAudioRuntime = Ribbit.Media.Audio.BassAudioRuntime;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BassNativeRuntimeTests
{
    [TestInitialize]
    public void ResetNativeRuntime()
    {
        BassAudioRuntime.Shutdown();
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
            "FEBB2CF1882D554C3A958280777DA0B69F07DE6E262DF271DE11C56E4A54AFD4",
            "73BF79C8ECCD63DEA8EB3E3E9B5FFE6F9406DEB9BBCCCC7557CA54F5013B4B96",
            "6F0869C11431E01F759FBE1CD6080299C833C519EB8AB1FEAE12106907B1FBD1",
            "F782CAE8090700A456C9E7AEAA7770C3B90CB60A1E765C4B3CBAE739D3B4D58D",
            "A6E1847EEF52D882B4137AF514D834C2E220DACEB417C821D1E502FB7A34C84A",
            "9D8EE8D750DEF93E927E62E35D02A4CC8457C509CFA561C47AED3381691F51F8"
        };

        string nativeDirectory = Path.Combine(AppContext.BaseDirectory, "libs", "x64");
        Assert.IsTrue(Path.IsPathFullyQualified(nativeDirectory));
        Assert.AreEqual("x64", Path.GetFileName(nativeDirectory));
        for (int index = 0; index < expectedNames.Length; index++)
        {
            string path = Path.Combine(nativeDirectory, expectedNames[index]);
            Assert.IsTrue(File.Exists(path), path);
            Assert.AreEqual(expectedHashes[index], Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            using FileStream stream = File.OpenRead(path);
            using var peReader = new PEReader(stream);
            Assert.AreEqual(Machine.Amd64, peReader.PEHeaders.CoffHeader.Machine, path);
        }

        BassAudioRuntime.Initialize();
        try
        {
            Assert.AreEqual(0x02041203u, BassVersionPacking.Pack(ManagedBass.Bass.Version));
            Assert.AreEqual(0x01040300u, BassVersionPacking.Pack(ManagedBass.Asio.BassAsio.Version));
            Assert.AreEqual(0x02040401u, BassVersionPacking.Pack(ManagedBass.Wasapi.BassWasapi.Version));
            Assert.AreEqual(0x02040C00u, BassVersionPacking.Pack(ManagedBass.Mix.BassMix.Version));
            Assert.AreEqual(0x02040C06u, BassVersionPacking.Pack(ManagedBass.Fx.BassFx.Version));
            Assert.AreEqual(0x02041100u, BassVersionPacking.Pack(ManagedBass.Enc.BassEnc.Version));
        }
        finally
        {
            BassAudioRuntime.Shutdown();
        }

    }

    [TestMethod]
    public void BassAudioRuntime_InitializesAndDeactivatesNativeRuntime()
    {
        BassAudioRuntime.Initialize();
        bool bassInitialized = false;
        try
        {
            bassInitialized = Bass.Init(
                0,
                44100,
                DeviceInitFlags.NoSpeakerAssignment,
                IntPtr.Zero,
                IntPtr.Zero);
            Assert.IsTrue(bassInitialized, Bass.LastError.ToString());
            BassAudioRuntime.FreeDevice();
            bassInitialized = false;
            Assert.AreEqual(0x02041203u, BassVersionPacking.Pack(ManagedBass.Bass.Version));
            bassInitialized = Bass.Init(
                0,
                44100,
                DeviceInitFlags.NoSpeakerAssignment,
                IntPtr.Zero,
                IntPtr.Zero);
            Assert.IsTrue(bassInitialized, Bass.LastError.ToString());
        }
        finally
        {
            BassAudioRuntime.Shutdown();
        }

    }

    [TestMethod]
    public void FreeCoreDevice_SuccessDoesNotReadLastError()
    {
        var events = new List<string>();

        BassAudioRuntime.FreeCoreDevice(
            () =>
            {
                events.Add("free");
                return true;
            },
            () =>
            {
                events.Add("error");
                return ManagedBass.Errors.Handle;
            },
            () => events.Add("already-free"));

        CollectionAssert.AreEqual(new[] { "free" }, events);
    }

    [TestMethod]
    public void FreeCoreDevice_CapturesInitAfterFalseAndTreatsItAsIdempotent()
    {
        var events = new List<string>();

        BassAudioRuntime.FreeCoreDevice(
            () =>
            {
                events.Add("free");
                return false;
            },
            () =>
            {
                events.Add("error");
                return ManagedBass.Errors.Init;
            },
            () => events.Add("already-free"));

        CollectionAssert.AreEqual(new[] { "free", "error", "already-free" }, events);
    }

    [TestMethod]
    public void FreeCoreDevice_ReportsNonInitErrorWithLegacyDiagnosticSpelling()
    {
        var events = new List<string>();

        Exception exception = Assert.ThrowsException<Exception>(() => BassAudioRuntime.FreeCoreDevice(
            () =>
            {
                events.Add("free");
                return false;
            },
            () =>
            {
                events.Add("error");
                return ManagedBass.Errors.Handle;
            },
            () => events.Add("already-free")));

        StringAssert.Contains(exception.Message, "BASS_ERROR_HANDLE");
        CollectionAssert.AreEqual(new[] { "free", "error" }, events);
    }

    [TestMethod]
    public void FreeCoreDevice_PreservesReleaseExceptionWhenErrorReadFails()
    {
        var events = new List<string>();
        var expected = new InvalidOperationException("free failed");

        Exception actual = Assert.ThrowsException<InvalidOperationException>(() => BassAudioRuntime.FreeCoreDevice(
            () =>
            {
                events.Add("free");
                throw expected;
            },
            () =>
            {
                events.Add("error");
                throw new InvalidOperationException("error read failed");
            },
            () => events.Add("already-free")));

        Assert.AreSame(expected, actual);
        CollectionAssert.AreEqual(new[] { "free", "error" }, events);
    }

    [TestMethod]
    public void BassAudioRuntime_InitializeReusesNativeOwnershipAcrossCycles()
    {
        for (int cycle = 0; cycle < 2; cycle++)
        {
            BassAudioRuntime.Initialize();
            try
            {
                Assert.IsTrue(BassNativeRuntime.IsLoaded);
                using BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
                Assert.IsTrue(BassNativeRuntime.IsLoaded);
                Assert.AreEqual(0x02041203u, BassVersionPacking.Pack(ManagedBass.Bass.Version));
            }
            finally
            {
                BassAudioRuntime.Shutdown();
            }

            Assert.IsFalse(BassNativeRuntime.IsLoaded);
        }
    }

    [TestMethod]
    public void ManagedBassResolver_MapsKnownNamesToCurrentGenerationHandles()
    {
        BassAudioRuntime.Initialize();
        try
        {
            (string BareName, string ExtensionName, string CanonicalName)[] knownNames =
            {
                ("bass", "BASS.DLL", "bass.dll"),
                ("BassAsio", "BASSASIO.DLL", "bassasio.dll"),
                ("basswasapi", "BASSWASAPI.DLL", "basswasapi.dll"),
                ("bassmix", "BASSMIX.DLL", "bassmix.dll"),
                ("bass_fx", "BASS_FX.DLL", "bass_fx.dll"),
                ("bassenc", "BASSENC.DLL", "bassenc.dll")
            };

            foreach ((string bareName, string extensionName, string canonicalName) in knownNames)
            {
                IntPtr bareHandle = ManagedBassNativeLibraryResolver.ResolveForTesting(bareName);
                Assert.AreNotEqual(IntPtr.Zero, bareHandle, bareName);
                Assert.AreEqual(
                    bareHandle,
                    ManagedBassNativeLibraryResolver.ResolveForTesting(extensionName),
                    extensionName);
                Assert.AreEqual(
                    bareHandle,
                    BassNativeRuntime.ResolveLoadedLibrary(canonicalName),
                    canonicalName);
            }
        }
        finally
        {
            BassAudioRuntime.Shutdown();
        }
    }

    [TestMethod]
    public void ManagedBassResolver_LeavesUnknownNamesToDefaultProbing()
    {
        Assert.AreEqual(IntPtr.Zero, ManagedBassNativeLibraryResolver.ResolveForTesting("not-a-bass-library"));
        Assert.AreEqual(IntPtr.Zero, ManagedBassNativeLibraryResolver.ResolveForTesting("C:\\other\\bass.dll"));
        Assert.AreEqual(IntPtr.Zero, ManagedBassNativeLibraryResolver.ResolveForTesting("bassenc_lame"));
    }

    [TestMethod]
    public void ManagedBassResolver_IsInstalledIdempotently()
    {
        ManagedBassNativeLibraryResolver.EnsureInstalled();
        ManagedBassNativeLibraryResolver.EnsureInstalled();
    }

    [TestMethod]
    public void ManagedBassResolver_RejectsKnownNameAfterRuntimeDeactivation()
    {
        BassAudioRuntime.Initialize();
        BassAudioRuntime.Shutdown();

        Assert.ThrowsException<DllNotFoundException>(
            () => ManagedBassNativeLibraryResolver.ResolveForTesting("bass"));
    }

    [TestMethod]
    public void BassNativeRuntime_ReusesPinnedManagedBassGenerationAcrossLogicalRestart()
    {
        BassAudioRuntime.Initialize();
        IntPtr[] firstGenerationHandles =
        [
            BassNativeRuntime.ResolveLoadedLibrary("bass.dll"),
            BassNativeRuntime.ResolveLoadedLibrary("bassasio.dll"),
            BassNativeRuntime.ResolveLoadedLibrary("basswasapi.dll"),
            BassNativeRuntime.ResolveLoadedLibrary("bassmix.dll"),
            BassNativeRuntime.ResolveLoadedLibrary("bass_fx.dll"),
            BassNativeRuntime.ResolveLoadedLibrary("bassenc.dll")
        ];
        BassAudioRuntime.Shutdown();
        Assert.IsFalse(BassNativeRuntime.IsLoaded);

        BassAudioRuntime.Initialize();
        try
        {
            CollectionAssert.AreEqual(
                firstGenerationHandles,
                new[]
                {
                    BassNativeRuntime.ResolveLoadedLibrary("bass.dll"),
                    BassNativeRuntime.ResolveLoadedLibrary("bassasio.dll"),
                    BassNativeRuntime.ResolveLoadedLibrary("basswasapi.dll"),
                    BassNativeRuntime.ResolveLoadedLibrary("bassmix.dll"),
                    BassNativeRuntime.ResolveLoadedLibrary("bass_fx.dll"),
                    BassNativeRuntime.ResolveLoadedLibrary("bassenc.dll")
                });
            Assert.AreEqual(0x02041100u, BassVersionPacking.Pack(ManagedBass.Enc.BassEnc.Version));
        }
        finally
        {
            BassAudioRuntime.Shutdown();
        }
    }

    [TestMethod]
    public void BassNativeRuntime_LoadFailureRollsBackCurrentHandlesInReverseOrder()
    {
        var loadedHandles = new List<IntPtr>();
        var freedHandles = new List<IntPtr>();
        int loadCount = 0;

        Assert.ThrowsException<DllNotFoundException>(() => BassNativeRuntime.LoadForTesting(
            _ =>
            {
                loadCount++;
                if (loadCount == 4)
                {
                    return IntPtr.Zero;
                }

                IntPtr handle = new(loadCount);
                loadedHandles.Add(handle);
                return handle;
            },
            handle => freedHandles.Add(handle)));

        CollectionAssert.AreEqual(loadedHandles.AsEnumerable().Reverse().ToArray(), freedHandles);
        Assert.IsFalse(BassNativeRuntime.IsLoaded);
    }

    [TestMethod]
    public void BassNativeRuntime_VersionMismatchPreservesComponentAndPackedValues()
    {
        InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(() =>
            BassNativeRuntime.ValidateVersionForTesting(
                "bassmix.dll",
                new Version(2, 4, 12, 1),
                0x02040C00u));

        StringAssert.Contains(exception.Message, "bassmix.dll");
        StringAssert.Contains(exception.Message, "Expected 0x02040C00");
        StringAssert.Contains(exception.Message, "loaded 0x02040C01");
    }

    [TestMethod]
    public void BassVersionPackingRoundTripsSupportedNativeVersionShape()
    {
        var expectedVersions = new Dictionary<uint, Version>
        {
            [0x02041203] = new(2, 4, 18, 3),
            [0x02040C00] = new(2, 4, 12, 0),
            [0x02041100] = new(2, 4, 17, 0),
            [0x02040401] = new(2, 4, 4, 1),
            [0x02040C06] = new(2, 4, 12, 6),
            [0x01040300] = new(1, 4, 3, 0)
        };

        foreach ((uint packed, Version expected) in expectedVersions)
        {
            Assert.AreEqual(expected, BassVersionPacking.Unpack(packed));
            Assert.AreEqual(packed, BassVersionPacking.Pack(expected));
        }

        Assert.AreEqual(
            new Version(2, 4, 18, 0),
            BassVersionPacking.Unpack(BassVersionPacking.Pack(new Version(2, 4, 18))));
        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => BassVersionPacking.Pack(new Version(2, 4, 256, 0)));
    }

    [TestMethod]
    public void AudioWriterRejectsStopBeforeStart()
    {
        try
        {
            BassAudioRuntime.Initialize();

            Assert.AreEqual(PlayState.Stopped, BassAudioWriter.RecordState);
            Assert.ThrowsException<InvalidOperationException>(BassAudioWriter.StopRecording);
            Assert.AreEqual(PlayState.Stopped, BassAudioWriter.RecordState);
        }
        finally
        {
            BassAudioRuntime.Shutdown();
        }
    }

    [TestMethod]
    public void AudioWriterStartsAndStopsWavRecordingWithoutPhysicalDevice()
    {
        string directoryPath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeekerWriterNativeContracts",
            Guid.NewGuid().ToString("N"));
        string outputWithoutExtension = Path.Combine(directoryPath, "recording");
        string lameWithoutExtension = Path.Combine(directoryPath, "lame");
        string neroWithoutExtension = Path.Combine(directoryPath, "nero");
        string opusWithoutExtension = Path.Combine(directoryPath, "opus");
        string flacWithoutExtension = Path.Combine(directoryPath, "flac");
        string oggWithoutExtension = Path.Combine(directoryPath, "ogg");
        string previousEncoderDirectory = BassAudioWriter.EncoderDirectory;
        SampleRate previousFrequency = BassAudioPlayer.Frequency;
        SampleFormat previousFormat = BassAudioPlayer.Format;
        BassAudioSession ownedSession = null;
        FieldInfo encoderField = null;
        ExceptionDispatchInfo failure = null;
        void CaptureCleanup(Action cleanup)
        {
            try
            {
                cleanup();
            }
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        AudioEncoderSession GetCurrentEncoder()
        {
            Assert.IsNotNull(encoderField);
            var currentEncoder = encoderField.GetValue(null) as AudioEncoderSession;
            Assert.IsNotNull(currentEncoder);
            return currentEncoder;
        }

        void AssertCreatedEncoder(string expectedOutputFile)
        {
            AudioEncoderSession currentEncoder = GetCurrentEncoder();
            Assert.AreEqual(expectedOutputFile, currentEncoder.OutputFile);
            Assert.AreEqual(0, currentEncoder.EncoderHandle);
            Assert.AreEqual(AudioEncoderSessionState.Created, currentEncoder.State);
        }

        try
        {
            Directory.CreateDirectory(directoryPath);
            encoderField = typeof(BassAudioWriter).GetField(
                "encoder",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(encoderField);

            foreach (EncoderType encoderType in new[]
            {
                EncoderType.MP3_LAME,
                EncoderType.AAC_NERO,
                EncoderType.OPUS,
                EncoderType.FLAC,
                EncoderType.OGG_VORBIS
            })
            {
                File.WriteAllText(
                    Path.Combine(directoryPath, encoderType.GetEncoderFileName()),
                    string.Empty);
            }

            foreach (string outputFile in new[]
            {
                lameWithoutExtension + ".mp3",
                neroWithoutExtension + ".m4a",
                opusWithoutExtension + ".opus",
                flacWithoutExtension + ".flac",
                oggWithoutExtension + ".ogg",
                outputWithoutExtension + ".wav"
            })
            {
                File.WriteAllText(outputFile, string.Empty);
            }

            BassAudioWriter.EncoderDirectory = directoryPath;
            BassAudioPlayer.Frequency = SampleRate.SAMPLE_RATE_48000Hz;
            BassAudioPlayer.Format = SampleFormat.SAMPLE_INT_16BIT;
            BassAudioRuntime.Initialize();
            BassAudioWriter.InitializeOwnedSession(out ownedSession);

            ChannelInfo sourceInfo = Bass.ChannelGetInfo(ownedSession.MixerHandle);
            Assert.AreEqual(SampleFormat.SAMPLE_INT_16BIT, BassAudioPlayer.Format);
            Assert.AreEqual(48000, sourceInfo.Frequency);
            Assert.AreEqual(2, sourceInfo.Channels);
            Assert.IsTrue(sourceInfo.Flags.HasFlag(BassFlags.Float));
            Assert.AreEqual(SampleRate.SAMPLE_RATE_48000Hz, ownedSession.NegotiationResult.ActualRate);
            Assert.AreEqual(2, ownedSession.NegotiationResult.ActualChannels);
            Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, ownedSession.NegotiationResult.EngineFormat);
            Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, ownedSession.NegotiationResult.EndpointFormat);

            string lameOutputFile = lameWithoutExtension + " (2).mp3";
            BassAudioWriter.CreateEncoderLAME(lameWithoutExtension, quality: 0.6f);
            AssertCreatedEncoder(lameOutputFile);
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, lameOutputFile);
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, " -s 48 ");
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, "--bitwidth 32");
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, " -V 4 ");
            Assert.AreEqual(
                EncodeFlags.Unicode | EncodeFlags.NoHeader | EncodeFlags.ConvertFloatTo32Bit,
                BassAudioWriter.EncoderFlags);

            string neroOutputFile = neroWithoutExtension + " (2).m4a";
            BassAudioWriter.CreateEncoderNeroAAC(neroWithoutExtension, quality: 0.6f);
            AssertCreatedEncoder(neroOutputFile);
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, neroOutputFile);
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, " -q 0.6 ");
            Assert.AreEqual(EncodeFlags.Unicode, BassAudioWriter.EncoderFlags);

            string opusOutputFile = opusWithoutExtension + " (2).opus";
            BassAudioWriter.CreateEncoderOPUS(opusWithoutExtension, quality: 0.6f);
            AssertCreatedEncoder(opusOutputFile);
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, opusOutputFile);
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, "--raw-rate 48000");
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, "--raw-chan 2");
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, "--raw-bits 24");
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, "--bitrate 156 ");
            Assert.AreEqual(
                EncodeFlags.Unicode | EncodeFlags.NoHeader | EncodeFlags.ConvertFloatTo24Bit,
                BassAudioWriter.EncoderFlags);

            string flacOutputFile = flacWithoutExtension + " (2).flac";
            BassAudioWriter.CreateEncoderFLAC(flacWithoutExtension, quality: 0.6f);
            AssertCreatedEncoder(flacOutputFile);
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, flacOutputFile);
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, "--sample-rate=48000");
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, "--channels=2");
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, "--bps=24");
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, "--replay-gain -6 ");
            Assert.AreEqual(
                EncodeFlags.Unicode | EncodeFlags.NoHeader | EncodeFlags.ConvertFloatTo24Bit,
                BassAudioWriter.EncoderFlags);

            string oggOutputFile = oggWithoutExtension + " (2).ogg";
            BassAudioWriter.CreateEncoderOGG(oggWithoutExtension, quality: 0.6f);
            AssertCreatedEncoder(oggOutputFile);
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, oggOutputFile);
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, "-F 3");
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, "-C 2");
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, "-R 48000");
            StringAssert.Contains(BassAudioWriter.EncoderCommandLine, " -q 6.0 ");
            Assert.IsFalse(BassAudioWriter.EncoderCommandLine.Contains(" -B ", StringComparison.Ordinal));
            Assert.AreEqual(EncodeFlags.Unicode | EncodeFlags.NoHeader, BassAudioWriter.EncoderFlags);

            string outputPath = outputWithoutExtension + " (2).wav";
            BassAudioWriter.CreateEncoderWAV(outputWithoutExtension);
            AssertCreatedEncoder(outputPath);
            Assert.AreEqual(outputPath, BassAudioWriter.EncoderCommandLine);
            Assert.AreEqual(
                EncodeFlags.PCM | EncodeFlags.ConvertFloatTo16BitInt,
                BassAudioWriter.EncoderFlags);

            BassAudioWriter.StartRecording();
            Assert.AreEqual(PlayState.Playing, BassAudioWriter.RecordState);
            BassAudioWriter.RecordToFile(TimeSpan.FromMilliseconds(50));

            BassAudioWriter.StopRecording();
            Assert.AreEqual(PlayState.Stopped, BassAudioWriter.RecordState);

            Assert.IsTrue(File.Exists(outputPath));
            Assert.IsTrue(new FileInfo(outputPath).Length >= 44);
        }
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            bool encoderReleased = false;
            CaptureCleanup(() =>
            {
                encoderReleased = BassAudioWriter.TryReleaseEncoder();
                if (!encoderReleased)
                {
                    throw new InvalidOperationException("The writer encoder owner did not release.");
                }
            });

            bool sessionReleased = ownedSession == null;
            if (encoderReleased)
            {
                CaptureCleanup(() =>
                {
                    sessionReleased = BassAudioPlayer.Free(ownedSession);
                    if (!sessionReleased)
                    {
                        throw new InvalidOperationException("The writer audio session did not release.");
                    }
                });
            }

            if (encoderReleased && sessionReleased)
            {
                CaptureCleanup(BassAudioRuntime.Shutdown);
            }

            CaptureCleanup(() =>
            {
                BassAudioWriter.EncoderDirectory = previousEncoderDirectory;
                BassAudioPlayer.Frequency = previousFrequency;
                BassAudioPlayer.Format = previousFormat;
            });
            CaptureCleanup(() =>
            {
                if (Directory.Exists(directoryPath))
                {
                    Directory.Delete(directoryPath, recursive: true);
                }
            });
        }

        failure?.Throw();
    }

    [TestMethod]
    public void BassAudioRuntime_ShutdownClosesAdmissionBeforeDeactivatingNativeGeneration()
    {
        BassAudioRuntime.Initialize();
        BassAudioOperationLease operation = BassAudioRuntime.EnterAudioOperation();
        Task shutdown = Task.Run(BassAudioRuntime.Shutdown);

        try
        {
            Assert.IsTrue(BassAudioRuntime.WaitForAudioShutdownRequest(TimeSpan.FromSeconds(5)));
            Task<bool> newRootAdmission = Task.Run(() =>
            {
                if (!BassAudioRuntime.TryEnterAudioOperation(out BassAudioOperationLease lease))
                {
                    return false;
                }

                lease.Dispose();
                return true;
            });
            Assert.IsFalse(newRootAdmission.GetAwaiter().GetResult());
            Assert.IsTrue(BassNativeRuntime.IsLoaded);
        }
        finally
        {
            operation.Dispose();
        }

        shutdown.GetAwaiter().GetResult();
        Assert.IsFalse(BassNativeRuntime.IsLoaded);
    }

    [TestMethod]
    public void BassAudioRuntime_InitializationCoreOrdersNativeLoadBeforeVersionValidation()
    {
        var events = new System.Collections.Generic.List<string>();

        BassAudioRuntime.InitializeRuntimeCore(
            () => events.Add("LoadNative"),
            () => events.Add("ValidateVersions"));

        CollectionAssert.AreEqual(
            new[] { "LoadNative", "ValidateVersions" },
            events);
    }

    [TestMethod]
    public void BassAudioRuntime_InitializationCoreStopsAfterNativeLoadFailure()
    {
        var events = new System.Collections.Generic.List<string>();

        Assert.ThrowsException<InvalidOperationException>(() => BassAudioRuntime.InitializeRuntimeCore(
            () =>
            {
                events.Add("LoadNative");
                throw new InvalidOperationException("load failed");
            },
            () => events.Add("ValidateVersions")));

        CollectionAssert.AreEqual(new[] { "LoadNative" }, events);
    }

    [TestMethod]
    public void NativeRuntime_NoSoundDecodeMixerPullProducesNonZeroPcm()
    {
        string wavePath = CreateNativeSmokeWaveFile();
        int sourceHandle = 0;
        int mixerHandle = 0;
        BassMixerSourceController sourceController = null;
        bool coreInitialized = false;
        string stage = "not started";
        Exception primaryException = null;
        Exception cleanupException = null;
        try
        {
            stage = "BassAudioRuntime.Initialize";
            BassAudioRuntime.Initialize();

            stage = "Bass.Init";
            Assert.IsTrue(
                Bass.Init(0, 44100, DeviceInitFlags.Default, IntPtr.Zero, IntPtr.Zero),
                NativeSmokeDiagnostic(stage, sourceHandle, mixerHandle, 0, 0, 0, 0));
            coreInitialized = true;

            stage = "Bass.CreateStream";
            sourceHandle = Bass.CreateStream(
                wavePath,
                0L,
                0L,
                BassFlags.Float | BassFlags.Prescan | BassFlags.Decode);
            Assert.AreNotEqual(
                0,
                sourceHandle,
                NativeSmokeDiagnostic(stage, sourceHandle, mixerHandle, 0, 0, 0, 0));

            stage = "BassMix.CreateMixerStream";
            mixerHandle = BassMix.CreateMixerStream(
                44100,
                1,
                BassFlags.Float | BassFlags.Decode);
            Assert.AreNotEqual(
                0,
                mixerHandle,
                NativeSmokeDiagnostic(stage, sourceHandle, mixerHandle, 0, 0, 0, 0));

            stage = "BassMix.ChannelGetMixer before attach";
            Assert.AreEqual(
                0,
                BassMix.ChannelGetMixer(sourceHandle),
                NativeSmokeDiagnostic(stage, sourceHandle, mixerHandle, 0, 0, 0, 0));
            Assert.AreEqual(Errors.Handle, Bass.LastError);

            sourceController = new BassMixerSourceController(new BassMixerSourceNativeBoundary());
            stage = "BassMix.ChannelAdd paused";
            BassMixerSourceAttachment attachment = sourceController.EnsureAttachedPaused(
                mixerHandle,
                sourceHandle,
                wavePath);
            Assert.IsTrue(attachment.NewlyAttached);
            Assert.AreEqual(
                mixerHandle,
                BassMix.ChannelGetMixer(sourceHandle),
                NativeSmokeDiagnostic(stage, sourceHandle, mixerHandle, 0, 0, 0, 0));

            stage = "BassMix.ChannelFlags resume";
            sourceController.Resume(mixerHandle, sourceHandle, wavePath);

            long sourcePositionBefore = Bass.ChannelGetPosition(sourceHandle, PositionFlags.Bytes);
            const int requestedBytes = 4096 * sizeof(float);
            byte[] buffer = new byte[requestedBytes];
            stage = "Bass.ChannelGetData";
            int returnedBytes = Bass.ChannelGetData(mixerHandle, buffer, requestedBytes);
            long sourcePositionAfter = Bass.ChannelGetPosition(sourceHandle, PositionFlags.Bytes);
            Assert.IsTrue(
                returnedBytes > 0,
                NativeSmokeDiagnostic(
                    stage,
                    sourceHandle,
                    mixerHandle,
                    requestedBytes,
                    returnedBytes,
                    sourcePositionBefore,
                    sourcePositionAfter));
            Assert.IsTrue(
                sourcePositionAfter > sourcePositionBefore,
                NativeSmokeDiagnostic(
                    stage,
                    sourceHandle,
                    mixerHandle,
                    requestedBytes,
                    returnedBytes,
                    sourcePositionBefore,
                    sourcePositionAfter));

            int finiteSamples = 0;
            int nonZeroSamples = 0;
            double energy = 0d;
            int sampleCount = returnedBytes / sizeof(float);
            for (int index = 0; index < sampleCount; index++)
            {
                float sample = BitConverter.ToSingle(buffer, index * sizeof(float));
                Assert.IsTrue(
                    float.IsFinite(sample),
                    NativeSmokeDiagnostic(
                        stage,
                        sourceHandle,
                        mixerHandle,
                        requestedBytes,
                        returnedBytes,
                        sourcePositionBefore,
                        sourcePositionAfter));
                finiteSamples++;
                if (sample != 0f)
                {
                    nonZeroSamples++;
                }
                energy += sample * sample;
            }

            Assert.AreEqual(sampleCount, finiteSamples);
            Assert.IsTrue(nonZeroSamples > 0);
            Assert.IsTrue(energy > 0d);
        }
        catch (Exception exception)
        {
            primaryException = exception;
            throw;
        }
        finally
        {
            try
            {
                if (sourceController != null && sourceHandle != 0 && mixerHandle != 0)
                {
                    sourceController.RemoveFromExpectedMixer(mixerHandle, sourceHandle, wavePath);
                }
            }
            catch (Exception exception)
            {
                cleanupException ??= exception;
            }

            try
            {
                if (mixerHandle != 0 && !Bass.StreamFree(mixerHandle))
                {
                    cleanupException = new InvalidOperationException(
                        "BASS mixer cleanup failed: " + Bass.LastError);
                }
            }
            catch (Exception exception)
            {
                cleanupException ??= exception;
            }

            try
            {
                if (sourceHandle != 0 && !Bass.StreamFree(sourceHandle))
                {
                    cleanupException ??= new InvalidOperationException(
                        "BASS source cleanup failed: " + Bass.LastError);
                }
            }
            catch (Exception exception)
            {
                cleanupException ??= exception;
            }

            try
            {
                if (coreInitialized
                    && !Bass.Free()
                    && Bass.LastError != Errors.Init)
                {
                    cleanupException ??= new InvalidOperationException(
                        "BASS core cleanup failed: " + Bass.LastError);
                }
            }
            catch (Exception exception)
            {
                cleanupException ??= exception;
            }

            try
            {
                BassAudioRuntime.Shutdown();
            }
            catch (Exception exception)
            {
                cleanupException ??= exception;
            }

            try
            {
                File.Delete(wavePath);
            }
            catch (Exception exception)
            {
                cleanupException ??= exception;
            }

            if (primaryException == null && cleanupException != null)
            {
                throw cleanupException;
            }
        }
    }

    private static string CreateNativeSmokeWaveFile()
    {
        const int sampleRate = 44100;
        const int channelCount = 1;
        const int sampleCount = sampleRate / 4;
        const short bitsPerSample = 16;
        int dataLength = sampleCount * channelCount * sizeof(short);
        byte[] wave = new byte[44 + dataLength];
        using (var stream = new MemoryStream(wave))
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataLength);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)channelCount);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channelCount * (bitsPerSample / 8));
            writer.Write((short)(channelCount * (bitsPerSample / 8)));
            writer.Write(bitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataLength);
            for (int index = 0; index < sampleCount; index++)
            {
                double phase = (2d * Math.PI * 440d * index) / sampleRate;
                writer.Write((short)Math.Round(Math.Sin(phase) * short.MaxValue * 0.5d));
            }
        }

        string path = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker.BassNativeSmoke." + Guid.NewGuid().ToString("N") + ".wav");
        File.WriteAllBytes(path, wave);
        return path;
    }

    [TestMethod]
    public void BassAudioPlayer_MemoryWavPlayPullsNonZeroPcmThroughOwningMixer()
    {
        string wavePath = CreateNativeSmokeWaveFile();
        try
        {
            AssertBassAudioPlayerPlayProducesNonZeroPcm(wavePath, onMemory: true);
        }
        finally
        {
            File.Delete(wavePath);
        }
    }

    [TestMethod]
    public void BassAudioPlayer_MemoryFileProceduresSurviveForcedFullGc()
    {
        string wavePath = CreateNativeSmokeWaveFile();
        BassAudioSession session = null;
        BassAudioPlayer player = null;
        int initialVoices = BassAudioPlayer.CurrentVoices;
        try
        {
            BassAudioPlayer.Frequency = SampleRate.AUTO;
            BassAudioPlayer.Format = SampleFormat.AUTO;
            BassAudioPlayer.InitializeOwned(
                BassAudioPlayer.DeviceDriver.NULL_DEVICE,
                default,
                0f,
                out session);

            player = new BassAudioPlayer(wavePath, onMemory: true);
            ForceFullCollection();

            player.Play();
            BassAudioOwnedStream source = session.GetPlayerStreams().Single();
            int sourceHandle = source.Handle;
            Assert.AreEqual(initialVoices + 1, BassAudioPlayer.CurrentVoices);

            byte[] buffer = new byte[4096 * sizeof(float)];
            int returnedBytes = Bass.ChannelGetData(
                session.MixerHandle,
                buffer,
                buffer.Length);
            Assert.IsTrue(returnedBytes > 0, Bass.LastError.ToString());

            player.CurrentTime = TimeSpan.FromMilliseconds(50);
            Assert.IsTrue(player.CurrentTime > TimeSpan.Zero);
            returnedBytes = Bass.ChannelGetData(
                session.MixerHandle,
                buffer,
                buffer.Length);
            Assert.IsTrue(returnedBytes > 0, Bass.LastError.ToString());

            player.Dispose();
            player.Dispose();
            Assert.AreEqual(initialVoices, BassAudioPlayer.CurrentVoices);
            Assert.AreEqual(0, session.GetPlayerStreams().Count);
            Assert.AreEqual(
                -1L,
                ManagedBass.Bass.ChannelGetPosition(
                    sourceHandle,
                    ManagedBass.PositionFlags.Bytes));
        }
        finally
        {
            try
            {
                player?.Dispose();
            }
            finally
            {
                BassAudioPlayer.Free(session);
                BassAudioRuntime.Shutdown();
                File.Delete(wavePath);
            }
        }
    }

    [TestMethod]
    public void BassAudioPlayer_MemoryOggPlayPullsNonZeroPcmThroughOwningMixer()
    {
        string oggPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "audio",
            "nvorbis-1test.ogg");
        Assert.IsTrue(File.Exists(oggPath), oggPath);
        AssertBassAudioPlayerPlayProducesNonZeroPcm(oggPath, onMemory: true);
    }

    [TestMethod]
    public void BassAudioPlayer_VerifiedMembershipRaceCountsVoiceAndLifecycleIsIdempotent()
    {
        string wavePath = CreateNativeSmokeWaveFile();
        BassAudioSession session = null;
        BassAudioPlayer player = null;
        int initialVoices = BassAudioPlayer.CurrentVoices;
        try
        {
            BassAudioPlayer.Frequency = SampleRate.AUTO;
            BassAudioPlayer.Format = SampleFormat.AUTO;
            BassAudioPlayer.InitializeOwned(
                BassAudioPlayer.DeviceDriver.NULL_DEVICE,
                default,
                0f,
                out session);

            var native = new PlayerMixerSourceNativeBoundary(
                () => session.MixerHandle,
                initiallyAttached: true);
            player = new BassAudioPlayer(wavePath, onMemory: true, native);

            player.Play();
            Assert.AreEqual(initialVoices + 1, BassAudioPlayer.CurrentVoices);
            player.Pause();
            Assert.AreEqual(initialVoices + 1, BassAudioPlayer.CurrentVoices);
            player.Pause();
            Assert.AreEqual(initialVoices + 1, BassAudioPlayer.CurrentVoices);

            player.Stop();
            Assert.AreEqual(initialVoices, BassAudioPlayer.CurrentVoices);
            player.Stop();
            Assert.AreEqual(initialVoices, BassAudioPlayer.CurrentVoices);

            player.Play();
            Assert.AreEqual(initialVoices + 1, BassAudioPlayer.CurrentVoices);
            player.Dispose();
            Assert.AreEqual(initialVoices, BassAudioPlayer.CurrentVoices);
        }
        finally
        {
            try
            {
                player?.Dispose();
            }
            finally
            {
                BassAudioPlayer.Free(session);
                BassAudioRuntime.Shutdown();
                File.Delete(wavePath);
            }
        }
    }

    [TestMethod]
    public void BassAudioPlayer_NaturalEndCanBeReenteredWithoutStaleCleanup()
    {
        string wavePath = CreateNativeSmokeWaveFile();
        BassAudioSession session = null;
        BassAudioPlayer player = null;
        int initialVoices = BassAudioPlayer.CurrentVoices;
        try
        {
            BassAudioPlayer.Frequency = SampleRate.AUTO;
            BassAudioPlayer.Format = SampleFormat.AUTO;
            BassAudioPlayer.InitializeOwned(
                BassAudioPlayer.DeviceDriver.NULL_DEVICE,
                default,
                0f,
                out session);

            player = new BassAudioPlayer(wavePath, onMemory: true);
            player.Play();
            Assert.AreEqual(initialVoices + 1, BassAudioPlayer.CurrentVoices);

            byte[] drainBuffer = new byte[1024 * 1024];
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int returnedBytes = Bass.ChannelGetData(
                    session.MixerHandle,
                    drainBuffer,
                    drainBuffer.Length);
                if (returnedBytes <= 0 || returnedBytes < drainBuffer.Length)
                {
                    break;
                }
            }

            Assert.AreEqual(initialVoices, BassAudioPlayer.CurrentVoices);
            player.Play();
            Assert.AreEqual(initialVoices + 1, BassAudioPlayer.CurrentVoices);
            player.Stop();
            Assert.AreEqual(initialVoices, BassAudioPlayer.CurrentVoices);
        }
        finally
        {
            try
            {
                player?.Dispose();
            }
            finally
            {
                BassAudioPlayer.Free(session);
                BassAudioRuntime.Shutdown();
                File.Delete(wavePath);
            }
        }
    }

    [TestMethod]
    public void BassAudioPlayer_NaturalEndGenerationRejectsStaleCallback()
    {
        Assert.AreEqual(8, BassAudioPlayer.GetNextPlaybackGeneration(7));
        Assert.AreNotEqual(7, BassAudioPlayer.GetNextPlaybackGeneration(7));
        Assert.IsTrue(BassAudioPlayer.IsCurrentPlaybackGeneration(7, 7));
        Assert.IsFalse(BassAudioPlayer.IsCurrentPlaybackGeneration(8, 7));
        Assert.IsFalse(BassAudioPlayer.ShouldPublishPendingEndCleanup(8, 8, 7));
        Assert.IsTrue(BassAudioPlayer.ShouldPublishPendingEndCleanup(8, 7, 8));
        Assert.IsFalse(BassAudioPlayer.ShouldPublishPendingEndCleanup(8, 9, 8));
    }

    private static void AssertBassAudioPlayerPlayProducesNonZeroPcm(string path, bool onMemory)
    {
        float originalVolume = BassAudioPlayer.DeviceVolume;
        bool originalMute = BassAudioPlayer.IsDeviceMuted;
        SampleRate originalFrequency = BassAudioPlayer.Frequency;
        SampleFormat originalFormat = BassAudioPlayer.Format;
        BassAudioSession session = null;
        BassAudioPlayer player = null;
        try
        {
            BassAudioRuntime.Shutdown();
            BassAudioPlayer.Frequency = SampleRate.AUTO;
            BassAudioPlayer.Format = SampleFormat.AUTO;
            BassAudioPlayer.IsDeviceMuted = false;
            BassAudioPlayer.InitializeOwned(
                BassAudioPlayer.DeviceDriver.NULL_DEVICE,
                default,
                0f,
                out session);

            player = new BassAudioPlayer(path, onMemory);
            player.Play();

            BassAudioOwnedStream source = session.GetPlayerStreams().Single();
            Assert.AreEqual(
                session.MixerHandle,
                BassMix.ChannelGetMixer(source.Handle),
                Bass.LastError.ToString());

            byte[] buffer = new byte[4096 * sizeof(float)];
            int returnedBytes = Bass.ChannelGetData(session.MixerHandle, buffer, buffer.Length);
            Assert.IsTrue(returnedBytes > 0, Bass.LastError.ToString());

            int finiteSamples = 0;
            int nonZeroSamples = 0;
            double energy = 0d;
            for (int index = 0; index + sizeof(float) <= returnedBytes; index += sizeof(float))
            {
                float sample = BitConverter.ToSingle(buffer, index);
                Assert.IsTrue(float.IsFinite(sample));
                finiteSamples++;
                if (sample != 0f)
                {
                    nonZeroSamples++;
                }
                energy += sample * sample;
            }

            Assert.IsTrue(finiteSamples > 0);
            Assert.IsTrue(nonZeroSamples > 0);
            Assert.IsTrue(energy > 0d);
            player.Stop();
        }
        finally
        {
            try
            {
                player?.Dispose();
            }
            finally
            {
                BassAudioPlayer.Free(session);
                BassAudioRuntime.Shutdown();
                BassAudioPlayer.Frequency = originalFrequency;
                BassAudioPlayer.Format = originalFormat;
                RestoreManagedAudioState(originalVolume, originalMute);
            }
        }
    }

    private sealed class PlayerMixerSourceNativeBoundary : IBassMixerSourceNativeBoundary
    {
        private readonly Func<int> expectedMixer;
        private bool attached;

        internal PlayerMixerSourceNativeBoundary(Func<int> expectedMixer, bool initiallyAttached)
        {
            this.expectedMixer = expectedMixer;
            attached = initiallyAttached;
        }

        public int GetMixer(int sourceHandle) => attached ? expectedMixer() : 0;

        public bool AddChannel(int mixerHandle, int sourceHandle, ManagedBass.BassFlags flags)
        {
            attached = true;
            return true;
        }

        public ManagedBass.BassFlags SetMixerChannelFlags(
            int sourceHandle,
            ManagedBass.BassFlags flags,
            ManagedBass.BassFlags mask)
            => ManagedBass.BassFlags.Default;

        public bool RemoveChannel(int sourceHandle)
        {
            attached = false;
            return true;
        }

        public bool SetPosition(int sourceHandle, long position, ManagedBass.PositionFlags mode) => true;

        public ManagedBass.Errors GetError() => ManagedBass.Errors.Handle;
    }

    private static string NativeSmokeDiagnostic(
        string stage,
        int sourceHandle,
        int mixerHandle,
        int requestedBytes,
        int returnedBytes,
        long sourcePositionBefore,
        long sourcePositionAfter)
    {
        return "BASS native smoke test failed. stage=" + stage
            + " sourceHandle=" + sourceHandle
            + " mixerHandle=" + mixerHandle
            + " nativeErrorSource=BASS"
            + " nativeErrorCode=" + Bass.LastError
            + " requestedBytes=" + requestedBytes
            + " returnedBytes=" + returnedBytes
            + " sourcePositionBefore=" + sourcePositionBefore
            + " sourcePositionAfter=" + sourcePositionAfter;
    }

    [TestMethod]
    public void PreRuntimeVolumeAndMuteChanges_UpdateManagedStateWithoutLoadingNativeRuntime()
    {
        float originalVolume = BassAudioPlayer.DeviceVolume;
        bool originalMute = BassAudioPlayer.IsDeviceMuted;
        try
        {
            BassAudioRuntime.Shutdown();
            int loadCountBefore = BassNativeRuntime.LoadInvocationCount;

            BassAudioPlayer.IsDeviceMuted = false;
            BassAudioPlayer.DeviceVolume = 0.73f;
            BassAudioPlayer.IsDeviceMuted = true;

            Assert.AreEqual(0.73f, BassAudioPlayer.DeviceVolume);
            Assert.IsTrue(BassAudioPlayer.IsDeviceMuted);
            Assert.AreEqual(loadCountBefore, BassNativeRuntime.LoadInvocationCount);
        }
        finally
        {
            RestoreManagedAudioState(originalVolume, originalMute);
            BassAudioRuntime.Shutdown();
        }
    }

    [TestMethod]
    public void MutedVolumeChange_IsRestoredWhenDeviceIsUnmutedBeforeRuntimeInitialization()
    {
        float originalVolume = BassAudioPlayer.DeviceVolume;
        bool originalMute = BassAudioPlayer.IsDeviceMuted;
        try
        {
            BassAudioRuntime.Shutdown();
            BassAudioPlayer.IsDeviceMuted = false;
            BassAudioPlayer.DeviceVolume = 0.31f;
            BassAudioPlayer.IsDeviceMuted = true;
            BassAudioPlayer.DeviceVolume = 0.82f;

            BassAudioPlayer.IsDeviceMuted = false;

            Assert.AreEqual(0.82f, BassAudioPlayer.DeviceVolume);
            Assert.IsFalse(BassAudioPlayer.IsDeviceMuted);
        }
        finally
        {
            RestoreManagedAudioState(originalVolume, originalMute);
            BassAudioRuntime.Shutdown();
        }
    }

    [TestMethod]
    public void VolumeAndMuteChangesAfterRuntimeShutdown_DoNotExposeAdmissionFailure()
    {
        float originalVolume = BassAudioPlayer.DeviceVolume;
        bool originalMute = BassAudioPlayer.IsDeviceMuted;
        try
        {
            BassAudioRuntime.Initialize();
            BassAudioRuntime.Shutdown();

            BassAudioPlayer.DeviceVolume = 0.62f;
            BassAudioPlayer.IsDeviceMuted = true;
            BassAudioPlayer.IsDeviceMuted = false;

            Assert.AreEqual(0.62f, BassAudioPlayer.DeviceVolume);
        }
        finally
        {
            RestoreManagedAudioState(originalVolume, originalMute);
            BassAudioRuntime.Shutdown();
        }
    }

    [TestMethod]
    public void EffectiveDeviceVolumeForBackend_SeparatesNullDeviceRenderGainFromAudibleMute()
    {
        const float deviceVolume = 0.37f;

        Assert.AreEqual(
            deviceVolume,
            BassAudioPlayer.GetEffectiveDeviceVolumeForBackend(
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                deviceVolume,
                isMuted: false));
        Assert.AreEqual(
            0f,
            BassAudioPlayer.GetEffectiveDeviceVolumeForBackend(
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                deviceVolume,
                isMuted: true));
        Assert.AreEqual(
            0f,
            BassAudioPlayer.GetEffectiveDeviceVolumeForBackend(
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED,
                deviceVolume,
                isMuted: true));
        Assert.AreEqual(
            0f,
            BassAudioPlayer.GetEffectiveDeviceVolumeForBackend(
                BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE,
                deviceVolume,
                isMuted: true));
        Assert.AreEqual(
            0f,
            BassAudioPlayer.GetEffectiveDeviceVolumeForBackend(
                BassAudioPlayer.DeviceDriver.ASIO,
                deviceVolume,
                isMuted: true));
        Assert.AreEqual(
            deviceVolume,
            BassAudioPlayer.GetEffectiveDeviceVolumeForBackend(
                BassAudioPlayer.DeviceDriver.NULL_DEVICE,
                deviceVolume,
                isMuted: false));
        Assert.AreEqual(
            deviceVolume,
            BassAudioPlayer.GetEffectiveDeviceVolumeForBackend(
                BassAudioPlayer.DeviceDriver.NULL_DEVICE,
                deviceVolume,
                isMuted: true));
    }

    [TestMethod]
    public void AudibleInitializationOrder_UsesOnlySupportedBackendFallbacks()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                BassAudioPlayer.DeviceDriver.ASIO,
                BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE,
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED
            },
            BassAudioPlayer.GetInitializationOrder(BassAudioPlayer.DeviceDriver.ASIO).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE,
                BassAudioPlayer.DeviceDriver.WASAPI_SHARED
            },
            BassAudioPlayer.GetInitializationOrder(BassAudioPlayer.DeviceDriver.WASAPI_EXCLUSIVE).ToArray());
        CollectionAssert.AreEqual(
            new[] { BassAudioPlayer.DeviceDriver.WASAPI_SHARED },
            BassAudioPlayer.GetInitializationOrder(BassAudioPlayer.DeviceDriver.WASAPI_SHARED).ToArray());
        CollectionAssert.AreEqual(
            new[] { BassAudioPlayer.DeviceDriver.NULL_DEVICE },
            BassAudioPlayer.GetInitializationOrder(BassAudioPlayer.DeviceDriver.NULL_DEVICE).ToArray());
        Assert.AreEqual(
            0,
            BassAudioPlayer.GetInitializationOrder(BassAudioPlayer.DeviceDriver.DIRECT_SOUND).Count);
    }

    [TestMethod]
    public void NullDevice_MuteDoesNotMuteOfflineRenderGain()
    {
        float originalVolume = BassAudioPlayer.DeviceVolume;
        bool originalMute = BassAudioPlayer.IsDeviceMuted;
        float originalDefaultVolume = BassAudioPlayer.DefaultVolume;
        SampleRate originalFrequency = BassAudioPlayer.Frequency;
        SampleFormat originalFormat = BassAudioPlayer.Format;
        BassAudioSession ownedSession = null;
        try
        {
            BassAudioRuntime.Shutdown();
            BassAudioPlayer.Frequency = SampleRate.AUTO;
            BassAudioPlayer.Format = SampleFormat.AUTO;
            BassAudioPlayer.IsDeviceMuted = true;
            BassAudioPlayer.DeviceVolume = 0.25f;

            BassAudioPlayer.InitializeOwned(
                BassAudioPlayer.DeviceDriver.NULL_DEVICE,
                default,
                0f,
                out ownedSession);

            Assert.IsTrue(BassAudioPlayer.IsDeviceMuted);
            Assert.AreEqual(0.4f, BassAudioPlayer.DeviceVolume);
            Assert.AreNotEqual(0, ownedSession.VolumeEffectHandle);
            Assert.AreEqual(0.4f, ReadVolumeEffectGain(ownedSession.VolumeEffectHandle), 0.0001f);

            BassAudioPlayer.DeviceVolume = 0.25f;
            Assert.IsTrue(BassAudioPlayer.IsDeviceMuted);
            Assert.AreEqual(0.25f, ReadVolumeEffectGain(ownedSession.VolumeEffectHandle), 0.0001f);

            BassAudioPlayer.IsDeviceMuted = false;
            Assert.AreEqual(0.25f, ReadVolumeEffectGain(ownedSession.VolumeEffectHandle), 0.0001f);

            BassAudioPlayer.IsDeviceMuted = true;
            Assert.IsTrue(BassAudioPlayer.IsDeviceMuted);
            Assert.AreEqual(0.25f, ReadVolumeEffectGain(ownedSession.VolumeEffectHandle), 0.0001f);
        }
        finally
        {
            try
            {
                BassAudioPlayer.Free(ownedSession);
                BassAudioRuntime.Shutdown();
            }
            finally
            {
                BassAudioPlayer.Frequency = originalFrequency;
                BassAudioPlayer.Format = originalFormat;
                BassAudioPlayer.DefaultVolume = originalDefaultVolume;
                RestoreManagedAudioState(originalVolume, originalMute);
            }
        }
    }

    private static void ForceFullCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static void RestoreManagedAudioState(float volume, bool muted)
    {
        BassAudioPlayer.IsDeviceMuted = false;
        BassAudioPlayer.DeviceVolume = volume;
        BassAudioPlayer.IsDeviceMuted = muted;
    }

    private static float ReadVolumeEffectGain(int effectHandle)
    {
        var parameters = new ManagedBassBfxVolumeParameters
        {
            Channel = -1
        };
        Assert.IsTrue(
            ManagedBassEffectParameters.GetParameters(effectHandle, parameters),
            Ribbit.Media.Audio.BassNativeErrorFormatter.Format(ManagedBass.Bass.LastError));
        return parameters.Volume;
    }

    [TestMethod]
    public void InitializeOwned_ActiveSessionRejectsScopedAndUnscopedReentry()
    {
        BassAudioSession ownedSession = null;
        _ = NLogWrapper.GetLogger("AudioSession");
        LoggingConfiguration originalLoggingConfiguration = LogManager.Configuration;
        var audioSessionTarget = new MemoryTarget { Layout = "${message}" };
        var testLoggingConfiguration = new LoggingConfiguration();
        testLoggingConfiguration.AddRule(LogLevel.Info, LogLevel.Fatal, audioSessionTarget, "AudioSession");
        LogManager.Configuration = testLoggingConfiguration;
        try
        {
            BassAudioPlayer.Frequency = SampleRate.AUTO;
            BassAudioPlayer.Format = SampleFormat.AUTO;
            BassAudioPlayer.InitializeOwned(
                BassAudioPlayer.DeviceDriver.NULL_DEVICE,
                default,
                0f,
                out ownedSession);

            BassAudioBackendResult negotiated = ownedSession.NegotiationResult;
            Assert.IsNotNull(negotiated);
            Assert.AreEqual(BassAudioPlayer.DeviceDriver.NULL_DEVICE, negotiated.Request.Backend);
            Assert.AreEqual(SampleRate.AUTO, negotiated.Request.Rate);
            Assert.AreEqual(SampleFormat.AUTO, negotiated.Request.Format);
            Assert.AreEqual(SampleRate.SAMPLE_RATE_44100Hz, negotiated.ActualRate);
            Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, negotiated.EngineFormat);
            Assert.AreEqual(SampleFormat.SAMPLE_FLOAT_32BIT, negotiated.EndpointFormat);
            Assert.AreEqual(2, negotiated.ActualChannels);
            Assert.AreEqual(0d, negotiated.LatencyMilliseconds);
            Assert.AreEqual(ownedSession.MixerHandle, negotiated.MixerHandle);
            Assert.IsTrue(negotiated.MixerHandle != 0);
            Assert.IsTrue(string.IsNullOrWhiteSpace(negotiated.FallbackReason));

            LogManager.Flush();
            string productionSuccessLog = audioSessionTarget.Logs.Single(
                log => log.Contains("Audio initialization attempt succeeded.", StringComparison.Ordinal));
            StringAssert.Contains(productionSuccessLog, "fallbackOccurred=False");
            StringAssert.Contains(productionSuccessLog, "fallbackDestination=none");
            StringAssert.Contains(productionSuccessLog, "fallbackReason=none");
            StringAssert.Contains(productionSuccessLog, "os=");
            StringAssert.Contains(productionSuccessLog, "bassVersion=0x");
            StringAssert.Contains(productionSuccessLog, "bassWasapiVersion=0x");
            StringAssert.Contains(productionSuccessLog, "bassAsioVersion=0x");
            StringAssert.Contains(productionSuccessLog, "bassMixVersion=0x");
            StringAssert.Contains(productionSuccessLog, "bassFxVersion=0x");
            StringAssert.Contains(productionSuccessLog, "bassEncVersion=0x");

            string exactSuccess = BassAudioPlayer.BuildInitializationSuccessDiagnostics(
                BassAudioPlayer.DeviceDriver.NULL_DEVICE,
                BassAudioPlayer.DeviceDriver.NULL_DEVICE,
                default,
                ownedSession,
                SampleRate.AUTO,
                SampleFormat.AUTO,
                0f,
                requestedEventMode: false,
                "versions");
            StringAssert.Contains(exactSuccess, "fallbackOccurred=False");
            StringAssert.Contains(exactSuccess, "fallbackDestination=none");
            StringAssert.Contains(exactSuccess, "fallbackReason=none");

            string versions = BassAudioPlayer.BuildRuntimeVersionDiagnostics(
                "test-os",
                1,
                2,
                3,
                4,
                5,
                6);
            StringAssert.Contains(versions, "os=test-os");
            StringAssert.Contains(versions, "bassVersion=0x00000001");
            StringAssert.Contains(versions, "bassWasapiVersion=0x00000002");
            StringAssert.Contains(versions, "bassAsioVersion=0x00000003");
            StringAssert.Contains(versions, "bassMixVersion=0x00000004");
            StringAssert.Contains(versions, "bassFxVersion=0x00000005");
            StringAssert.Contains(versions, "bassEncVersion=0x00000006");

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
            try
            {
                BassAudioPlayer.Free(ownedSession);
                BassAudioRuntime.Shutdown();
                LogManager.Flush();
            }
            finally
            {
                LogManager.Configuration = originalLoggingConfiguration;
            }
        }
    }

}
