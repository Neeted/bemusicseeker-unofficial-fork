using System;
using System.Linq;
using System.Runtime.InteropServices;
using ManagedBass;
using ManagedBass.DirectX8;
using ManagedBass.Fx;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

/// <summary>Locks the Unit 2B effect inventory and native parameter ABI.</summary>
[TestClass]
public sealed class BassAudioEffectTests
{
    [TestMethod]
    public void Catalog_PreservesAllLegacyCategoriesWithoutDuplicateNativeIds()
    {
        BassAudioEffectType[] expectedTypes =
        [
            BassAudioEffectType.Dx8Chorus,
            BassAudioEffectType.Dx8Compressor,
            BassAudioEffectType.Dx8Distortion,
            BassAudioEffectType.Dx8Echo,
            BassAudioEffectType.Dx8Flanger,
            BassAudioEffectType.Dx8Gargle,
            BassAudioEffectType.Dx8I3dl2Reverb,
            BassAudioEffectType.Dx8ParamEq,
            BassAudioEffectType.Dx8Reverb,
            BassAudioEffectType.BfxRotate,
            BassAudioEffectType.BfxEcho,
            BassAudioEffectType.BfxFlanger,
            BassAudioEffectType.BfxVolume,
            BassAudioEffectType.BfxPeakEq,
            BassAudioEffectType.BfxReverb,
            BassAudioEffectType.BfxLpf,
            BassAudioEffectType.BfxMix,
            BassAudioEffectType.BfxDamp,
            BassAudioEffectType.BfxAutoWah,
            BassAudioEffectType.BfxEcho2,
            BassAudioEffectType.BfxPhaser,
            BassAudioEffectType.BfxEcho3,
            BassAudioEffectType.BfxChorus,
            BassAudioEffectType.BfxApf,
            BassAudioEffectType.BfxCompressor,
            BassAudioEffectType.BfxDistortion,
            BassAudioEffectType.BfxCompressor2,
            BassAudioEffectType.BfxVolumeEnvelope,
            BassAudioEffectType.BfxBqf,
            BassAudioEffectType.BfxEcho4,
            BassAudioEffectType.BfxPitchShift,
            BassAudioEffectType.BfxFreeverb
        ];

        BassAudioEffectDefinition[] definitions = BassAudioEffectCatalog.Definitions.ToArray();
        int[] expectedNativeTypeIds =
        [
            0,
            1,
            2,
            3,
            4,
            5,
            6,
            7,
            8,
            .. Enumerable.Range(65536, 23)
        ];
        EffectType[] expectedManagedBassTypes =
        [
            (EffectType)0,
            (EffectType)1,
            (EffectType)2,
            (EffectType)3,
            (EffectType)4,
            (EffectType)5,
            (EffectType)6,
            (EffectType)7,
            (EffectType)8,
            BassAudioEffectNativeTypes.BfxRotate,
            BassAudioEffectNativeTypes.BfxEcho,
            BassAudioEffectNativeTypes.BfxFlanger,
            BassAudioEffectNativeTypes.BfxVolume,
            BassAudioEffectNativeTypes.BfxPeakEq,
            BassAudioEffectNativeTypes.BfxReverb,
            BassAudioEffectNativeTypes.BfxLpf,
            BassAudioEffectNativeTypes.BfxMix,
            BassAudioEffectNativeTypes.BfxDamp,
            BassAudioEffectNativeTypes.BfxAutoWah,
            BassAudioEffectNativeTypes.BfxEcho2,
            BassAudioEffectNativeTypes.BfxPhaser,
            BassAudioEffectNativeTypes.BfxEcho3,
            BassAudioEffectNativeTypes.BfxChorus,
            BassAudioEffectNativeTypes.BfxApf,
            BassAudioEffectNativeTypes.BfxCompressor,
            BassAudioEffectNativeTypes.BfxDistortion,
            BassAudioEffectNativeTypes.BfxCompressor2,
            BassAudioEffectNativeTypes.BfxVolumeEnvelope,
            BassAudioEffectNativeTypes.BfxBqf,
            BassAudioEffectNativeTypes.BfxEcho4,
            BassAudioEffectNativeTypes.BfxPitchShift,
            BassAudioEffectNativeTypes.BfxFreeverb
        ];
        Assert.AreEqual(32, Enum.GetValues<BassAudioEffectType>().Length);
        Assert.AreEqual(32, definitions.Length);
        CollectionAssert.AreEqual(expectedTypes, definitions.Select(definition => definition.Type).ToArray());
        CollectionAssert.AreEqual(expectedNativeTypeIds, definitions.Select(definition => definition.NativeTypeId).ToArray());
        CollectionAssert.AreEqual(expectedManagedBassTypes, definitions.Select(definition => definition.ManagedBassType).ToArray());
        Assert.AreEqual(32, definitions.Select(definition => definition.NativeTypeId).Distinct().Count());
        Assert.AreEqual(definitions.Length, definitions.Select(definition => definition.ParameterType).Distinct().Count());
    }

    [TestMethod]
    public void Catalog_UsesManagedBassTypesForDX8AndKnownBfxCategories()
    {
        Assert.AreEqual(0, BassAudioEffectCatalog.GetNativeTypeId(BassAudioEffectType.Dx8Chorus));
        Assert.AreEqual(8, BassAudioEffectCatalog.GetNativeTypeId(BassAudioEffectType.Dx8Reverb));
        Assert.AreEqual(65536, BassAudioEffectCatalog.GetNativeTypeId(BassAudioEffectType.BfxRotate));
        Assert.AreEqual(65539, BassAudioEffectCatalog.GetNativeTypeId(BassAudioEffectType.BfxVolume));
        Assert.AreEqual(65540, BassAudioEffectCatalog.GetNativeTypeId(BassAudioEffectType.BfxPeakEq));
        Assert.AreEqual(65556, BassAudioEffectCatalog.GetNativeTypeId(BassAudioEffectType.BfxEcho4));

        BassAudioEffectDefinition dx8 = GetDefinition(BassAudioEffectType.Dx8Chorus);
        BassAudioEffectDefinition rotate = GetDefinition(BassAudioEffectType.BfxRotate);
        BassAudioEffectDefinition volume = GetDefinition(BassAudioEffectType.BfxVolume);
        Assert.AreEqual(typeof(ManagedBass.DirectX8.DXChorusParameters), dx8.ParameterType);
        Assert.AreEqual(typeof(ManagedBass.Fx.RotateParameters), rotate.ParameterType);
        Assert.AreEqual(typeof(ManagedBassBfxVolumeParameters), volume.ParameterType);
        Assert.AreEqual(BassAudioEffectNativeTypes.Dx8Chorus, dx8.ManagedBassType);
        Assert.AreEqual(BassAudioEffectNativeTypes.BfxRotate, rotate.ManagedBassType);
        Assert.AreEqual(BassAudioEffectNativeTypes.BfxVolume, volume.ManagedBassType);
    }

    [TestMethod]
    public void Catalog_PreservesLegacyEffectDefaults()
    {
        DXChorusParameters dxChorus = (DXChorusParameters)BassAudioEffectCatalog.CreateParameters(BassAudioEffectType.Dx8Chorus);
        Assert.AreEqual(0f, dxChorus.fWetDryMix);
        Assert.AreEqual(25f, dxChorus.fDepth);
        Assert.AreEqual(DXWaveform.Sine, dxChorus.lWaveform);
        Assert.AreEqual(DXPhase.Zero, dxChorus.lPhase);

        DXDistortionParameters dxDistortion = (DXDistortionParameters)BassAudioEffectCatalog.CreateParameters(BassAudioEffectType.Dx8Distortion);
        Assert.AreEqual(50f, dxDistortion.fEdge);
        Assert.AreEqual(4000f, dxDistortion.fPostEQCenterFrequency);
        Assert.AreEqual(4000f, dxDistortion.fPostEQBandwidth);
        Assert.AreEqual(4000f, dxDistortion.fPreLowpassCutoff);

        DXEchoParameters dxEcho = (DXEchoParameters)BassAudioEffectCatalog.CreateParameters(BassAudioEffectType.Dx8Echo);
        Assert.AreEqual(333f, dxEcho.fLeftDelay);
        Assert.AreEqual(333f, dxEcho.fRightDelay);

        DXFlangerParameters dxFlanger = (DXFlangerParameters)BassAudioEffectCatalog.CreateParameters(BassAudioEffectType.Dx8Flanger);
        Assert.AreEqual(25f, dxFlanger.fDepth);
        Assert.AreEqual(DXPhase.Zero, dxFlanger.lPhase);

        DXParamEQParameters dxParamEq = (DXParamEQParameters)BassAudioEffectCatalog.CreateParameters(BassAudioEffectType.Dx8ParamEq);
        Assert.AreEqual(100f, dxParamEq.fCenter);
        Assert.AreEqual(18f, dxParamEq.fBandwidth);

        DXReverbParameters dxReverb = (DXReverbParameters)BassAudioEffectCatalog.CreateParameters(BassAudioEffectType.Dx8Reverb);
        Assert.AreEqual(1000f, dxReverb.fReverbTime);
        Assert.AreEqual(0.001f, dxReverb.fHighFreqRTRatio);

        ManagedBassBfxEchoParameters bfxEcho = (ManagedBassBfxEchoParameters)BassAudioEffectCatalog.CreateParameters(BassAudioEffectType.BfxEcho);
        Assert.AreEqual(1200, bfxEcho.Delay);
        ManagedBassBfxFlangerParameters bfxFlanger = (ManagedBassBfxFlangerParameters)BassAudioEffectCatalog.CreateParameters(BassAudioEffectType.BfxFlanger);
        Assert.AreEqual(1f, bfxFlanger.WetDry);
        Assert.AreEqual(0.01f, bfxFlanger.Speed);
        Assert.AreEqual(-1, bfxFlanger.Channel);
        ManagedBassBfxVolumeParameters bfxVolume = (ManagedBassBfxVolumeParameters)BassAudioEffectCatalog.CreateParameters(BassAudioEffectType.BfxVolume);
        Assert.AreEqual(-1, bfxVolume.Channel);
        Assert.AreEqual(1f, bfxVolume.Volume);
        ManagedBassBfxReverbParameters bfxReverb = (ManagedBassBfxReverbParameters)BassAudioEffectCatalog.CreateParameters(BassAudioEffectType.BfxReverb);
        Assert.AreEqual(1200, bfxReverb.Delay);
        ManagedBassBfxLpfParameters bfxLpf = (ManagedBassBfxLpfParameters)BassAudioEffectCatalog.CreateParameters(BassAudioEffectType.BfxLpf);
        Assert.AreEqual(2f, bfxLpf.Resonance);
        Assert.AreEqual(200f, bfxLpf.CutOffFrequency);
        Assert.AreEqual(-1, bfxLpf.Channel);

        AutoWahParameters autoWah = (AutoWahParameters)BassAudioEffectCatalog.CreateParameters(BassAudioEffectType.BfxAutoWah);
        Assert.AreEqual(0f, autoWah.fDryMix);
        PhaserParameters phaser = (PhaserParameters)BassAudioEffectCatalog.CreateParameters(BassAudioEffectType.BfxPhaser);
        Assert.AreEqual(0f, phaser.fDryMix);
        ChorusParameters chorus = (ChorusParameters)BassAudioEffectCatalog.CreateParameters(BassAudioEffectType.BfxChorus);
        Assert.AreEqual(0f, chorus.fDryMix);
        DistortionParameters distortion = (DistortionParameters)BassAudioEffectCatalog.CreateParameters(BassAudioEffectType.BfxDistortion);
        Assert.AreEqual(0f, distortion.fDryMix);

        ManagedBassBfxVolumeEnvelopeParameters envelope = (ManagedBassBfxVolumeEnvelopeParameters)BassAudioEffectCatalog.CreateParameters(BassAudioEffectType.BfxVolumeEnvelope);
        Assert.AreEqual(-1, envelope.Channel);
        Assert.IsTrue(envelope.Follow);
    }

    [TestMethod]
    public void Catalog_CreatesEveryParameterTypeWithMatchingNativeEffectId()
    {
        foreach (BassAudioEffectDefinition definition in BassAudioEffectCatalog.Definitions)
        {
            IEffectParameter parameters = BassAudioEffectCatalog.CreateParameters(definition.Type);
            Assert.AreEqual(definition.ParameterType, parameters.GetType(), definition.Type.ToString());
        }
    }

    [TestMethod]
    public void CustomScalarAdapters_PreserveNativeSizesAndOffsets()
    {
        Assert.AreEqual(8, Marshal.SizeOf<ManagedBassBfxEchoParameters>());
        Assert.AreEqual(12, Marshal.SizeOf<ManagedBassBfxFlangerParameters>());
        Assert.AreEqual(8, Marshal.SizeOf<ManagedBassBfxVolumeParameters>());
        Assert.AreEqual(8, Marshal.SizeOf<ManagedBassBfxReverbParameters>());
        Assert.AreEqual(12, Marshal.SizeOf<ManagedBassBfxLpfParameters>());
        Assert.AreEqual(20, Marshal.SizeOf<ManagedBassBfxEcho2Parameters>());
        Assert.AreEqual(16, Marshal.SizeOf<ManagedBassBfxEcho3Parameters>());
        Assert.AreEqual(12, Marshal.SizeOf<ManagedBassBfxApfParameters>());
        Assert.AreEqual(16, Marshal.SizeOf<ManagedBassBfxCompressorParameters>());
        Assert.AreEqual(20, Marshal.SizeOf<ManagedBassBfxPitchShiftParameters>());

        Assert.AreEqual(0, Marshal.OffsetOf<ManagedBassBfxEcho2Parameters>(nameof(ManagedBassBfxEcho2Parameters.DryMix)).ToInt32());
        Assert.AreEqual(12, Marshal.OffsetOf<ManagedBassBfxEcho2Parameters>(nameof(ManagedBassBfxEcho2Parameters.Delay)).ToInt32());
        Assert.AreEqual(16, Marshal.OffsetOf<ManagedBassBfxEcho2Parameters>(nameof(ManagedBassBfxEcho2Parameters.Channel)).ToInt32());
        Assert.AreEqual(0, Marshal.OffsetOf<ManagedBassBfxVolumeParameters>(nameof(ManagedBassBfxVolumeParameters.Channel)).ToInt32());
        Assert.AreEqual(4, Marshal.OffsetOf<ManagedBassBfxVolumeParameters>(nameof(ManagedBassBfxVolumeParameters.Volume)).ToInt32());
        Assert.AreEqual(0, Marshal.OffsetOf<ManagedBassBfxCompressorParameters>(nameof(ManagedBassBfxCompressorParameters.Threshold)).ToInt32());
        Assert.AreEqual(12, Marshal.OffsetOf<ManagedBassBfxCompressorParameters>(nameof(ManagedBassBfxCompressorParameters.Channel)).ToInt32());
        Assert.AreEqual(0, Marshal.OffsetOf<ManagedBassBfxPitchShiftParameters>(nameof(ManagedBassBfxPitchShiftParameters.PitchShift)).ToInt32());
        Assert.AreEqual(4, Marshal.OffsetOf<ManagedBassBfxPitchShiftParameters>(nameof(ManagedBassBfxPitchShiftParameters.FineTune)).ToInt32());
        Assert.AreEqual(8, Marshal.OffsetOf<ManagedBassBfxPitchShiftParameters>(nameof(ManagedBassBfxPitchShiftParameters.FFTSize)).ToInt32());
        Assert.AreEqual(12, Marshal.OffsetOf<ManagedBassBfxPitchShiftParameters>(nameof(ManagedBassBfxPitchShiftParameters.Oversampling)).ToInt32());
        Assert.AreEqual(16, Marshal.OffsetOf<ManagedBassBfxPitchShiftParameters>(nameof(ManagedBassBfxPitchShiftParameters.Channel)).ToInt32());
    }

    [TestMethod]
    public void PitchShiftAdapter_UsesLegacy32BitLayoutAndCopiesBack()
    {
        var parameters = new ManagedBassBfxPitchShiftParameters();
        Assert.AreEqual(1f, parameters.PitchShift);
        Assert.AreEqual(0f, parameters.FineTune);
        Assert.AreEqual(2048, parameters.FFTSize);
        Assert.AreEqual(8, parameters.Oversampling);
        Assert.AreEqual(-1, parameters.Channel);

        using NativeEffectParameterLease lease = ManagedBassEffectParameters.CreateNativeScope(parameters);
        Marshal.WriteInt32(lease.Pointer, 0, BitConverter.SingleToInt32Bits(2.5f));
        Marshal.WriteInt32(lease.Pointer, 4, BitConverter.SingleToInt32Bits(-0.75f));
        Marshal.WriteInt32(lease.Pointer, 8, 4096);
        Marshal.WriteInt32(lease.Pointer, 12, 4);
        Marshal.WriteInt32(lease.Pointer, 16, -1);
        lease.CopyBack();

        Assert.AreEqual(2.5f, parameters.PitchShift);
        Assert.AreEqual(-0.75f, parameters.FineTune);
        Assert.AreEqual(4096, parameters.FFTSize);
        Assert.AreEqual(4, parameters.Oversampling);
        Assert.AreEqual(-1, parameters.Channel);
    }

    [TestMethod]
    public void PointerAdapters_PinArraysForTheCallAndReleaseThemAfterward()
    {
        var mix = new ManagedBassBfxMixParameters(2, 1);
        Assert.AreEqual(IntPtr.Zero, mix.ChannelsPointer);
        using (NativeEffectParameterLease lease = ManagedBassEffectParameters.CreateNativeScope(mix))
        {
            Assert.AreNotEqual(IntPtr.Zero, lease.Pointer);
            IntPtr channelsPointer = Marshal.ReadIntPtr(lease.Pointer);
            Assert.AreEqual(mix.ChannelsPointer, channelsPointer);
            Assert.AreEqual(2, Marshal.ReadInt32(channelsPointer, 0));
            Assert.AreEqual(1, Marshal.ReadInt32(channelsPointer, sizeof(int)));
            Marshal.WriteInt32(channelsPointer, 0, 1);
            lease.CopyBack();
            Assert.AreEqual(1, mix.Channels[0]);
        }

        Assert.AreEqual(IntPtr.Zero, mix.ChannelsPointer);
        Assert.ThrowsException<ObjectDisposedException>(() =>
        {
            using NativeEffectParameterLease lease = ManagedBassEffectParameters.CreateNativeScope(mix);
            lease.Dispose();
            _ = lease.Pointer;
        });

        var envelope = new ManagedBassBfxVolumeEnvelopeParameters(
            channel: -1,
            follow: true,
            new ManagedBassBfxEnvelopeNode { Position = 0.0, Value = 1.0f },
            new ManagedBassBfxEnvelopeNode { Position = 2.0, Value = 0.5f });
        using (NativeEffectParameterLease lease = ManagedBassEffectParameters.CreateNativeScope(envelope))
        {
            Assert.AreEqual(24, Marshal.SizeOf<ManagedBassBfxVolumeEnvelopeParameters>());
            Assert.AreEqual(0, Marshal.OffsetOf<ManagedBassBfxVolumeEnvelopeParameters>(nameof(ManagedBassBfxVolumeEnvelopeParameters.Channel)).ToInt32());
            Assert.AreEqual(4, Marshal.OffsetOf<ManagedBassBfxVolumeEnvelopeParameters>(nameof(ManagedBassBfxVolumeEnvelopeParameters.NodeCount)).ToInt32());
            Assert.AreEqual(8, Marshal.OffsetOf<ManagedBassBfxVolumeEnvelopeParameters>(nameof(ManagedBassBfxVolumeEnvelopeParameters.NodesPointer)).ToInt32());
            Assert.AreEqual(16, Marshal.OffsetOf<ManagedBassBfxVolumeEnvelopeParameters>(nameof(ManagedBassBfxVolumeEnvelopeParameters.FollowNative)).ToInt32());
            Assert.AreEqual(envelope.NodesPointer, Marshal.ReadIntPtr(lease.Pointer, 8));
            Assert.AreEqual(2, Marshal.ReadInt32(lease.Pointer, 4));
            Assert.AreEqual(1.0f, Marshal.PtrToStructure<ManagedBassBfxEnvelopeNode>(envelope.NodesPointer).Value);
            Marshal.StructureToPtr(
                new ManagedBassBfxEnvelopeNode { Position = 2.0, Value = 0.25f },
                IntPtr.Add(
                    envelope.NodesPointer,
                    Marshal.SizeOf<ManagedBassBfxEnvelopeNode>()),
                fDeleteOld: false);
            lease.CopyBack();
            Assert.AreEqual(0.25f, envelope.Nodes[1].Value);
        }

        Assert.AreEqual(IntPtr.Zero, envelope.NodesPointer);
    }

    [TestMethod]
    public void VolumeEnvelope_CopyBackRebuildsNodesReturnedByNative()
    {
        var envelope = new ManagedBassBfxVolumeEnvelopeParameters();
        int nodeSize = Marshal.SizeOf<ManagedBassBfxEnvelopeNode>();
        IntPtr nativeNodes = Marshal.AllocHGlobal(nodeSize * 2);
        try
        {
            Marshal.StructureToPtr(
                new ManagedBassBfxEnvelopeNode { Position = 0.25, Value = 0.75f },
                nativeNodes,
                fDeleteOld: false);
            Marshal.StructureToPtr(
                new ManagedBassBfxEnvelopeNode { Position = 0.5, Value = 0.25f },
                IntPtr.Add(nativeNodes, nodeSize),
                fDeleteOld: false);

            using NativeEffectParameterLease lease = ManagedBassEffectParameters.CreateNativeScope(envelope);
            Marshal.WriteInt32(lease.Pointer, 4, 2);
            Marshal.WriteIntPtr(lease.Pointer, 8, nativeNodes);
            lease.CopyBack();

            Assert.AreEqual(2, envelope.NodeCount);
            Assert.AreEqual(2, envelope.Nodes.Count);
            Assert.AreEqual(0.25, envelope.Nodes[0].Position);
            Assert.AreEqual(0.75f, envelope.Nodes[0].Value);
            Assert.AreEqual(0.5, envelope.Nodes[1].Position);
            Assert.AreEqual(0.25f, envelope.Nodes[1].Value);
        }
        finally
        {
            Marshal.FreeHGlobal(nativeNodes);
        }
    }

    [TestMethod]
    public void NativeParameterLease_PreservesCleanupPrimaryErrorAndReleasesPins()
    {
        GCHandle pin = GCHandle.Alloc(new byte[1], GCHandleType.Pinned);
        IntPtr pinHandle = GCHandle.ToIntPtr(pin);
        InvalidOperationException cleanupFailure = new("cleanup sentinel");
        try
        {
            NativeEffectParameterLease lease = NativeEffectParameterLease.Create(
                new ManagedBassBfxVolumeParameters(),
                pins: new[] { pin },
                cleanup: () => throw cleanupFailure);

            InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(
                () => lease.Dispose());
            Assert.AreSame(cleanupFailure, exception);
            GCHandle releasedHandle = GCHandle.FromIntPtr(pinHandle);
            Assert.IsNull(releasedHandle.Target);
        }
        finally
        {
            if (pin.IsAllocated)
            {
                pin.Free();
            }
        }
    }

    private static BassAudioEffectDefinition GetDefinition(BassAudioEffectType type)
    {
        Assert.IsTrue(BassAudioEffectCatalog.TryGetDefinition(type, out BassAudioEffectDefinition definition));
        return definition;
    }
}
