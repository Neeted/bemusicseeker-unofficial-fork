using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

/// <summary>BASS外部effect IDとnative引数のABI・pin寿命を確認する。</summary>
[TestClass]
public sealed class BassAudioEffectTests
{
    /// <summary>
    /// 必要なBASS effectの存在と外部native ID・ManagedBass指定を照合し、categoryの欠落・ID交換・重複を検出する。
    /// </summary>
    [TestMethod]
    public void Catalog_PreservesAllLegacyCategoriesWithoutDuplicateNativeIds()
    {
        // BASS 2.4 / BASS_FX 2.4 の公式headerに定義された外部effect ID。
        var expectedNativeIds = new Dictionary<BassAudioEffectType, int>
        {
            [BassAudioEffectType.Dx8Chorus] = 0,
            [BassAudioEffectType.Dx8Compressor] = 1,
            [BassAudioEffectType.Dx8Distortion] = 2,
            [BassAudioEffectType.Dx8Echo] = 3,
            [BassAudioEffectType.Dx8Flanger] = 4,
            [BassAudioEffectType.Dx8Gargle] = 5,
            [BassAudioEffectType.Dx8I3dl2Reverb] = 6,
            [BassAudioEffectType.Dx8ParamEq] = 7,
            [BassAudioEffectType.Dx8Reverb] = 8,
            [BassAudioEffectType.BfxRotate] = 0x10000,
            [BassAudioEffectType.BfxEcho] = 0x10001,
            [BassAudioEffectType.BfxFlanger] = 0x10002,
            [BassAudioEffectType.BfxVolume] = 0x10003,
            [BassAudioEffectType.BfxPeakEq] = 0x10004,
            [BassAudioEffectType.BfxReverb] = 0x10005,
            [BassAudioEffectType.BfxLpf] = 0x10006,
            [BassAudioEffectType.BfxMix] = 0x10007,
            [BassAudioEffectType.BfxDamp] = 0x10008,
            [BassAudioEffectType.BfxAutoWah] = 0x10009,
            [BassAudioEffectType.BfxEcho2] = 0x1000A,
            [BassAudioEffectType.BfxPhaser] = 0x1000B,
            [BassAudioEffectType.BfxEcho3] = 0x1000C,
            [BassAudioEffectType.BfxChorus] = 0x1000D,
            [BassAudioEffectType.BfxApf] = 0x1000E,
            [BassAudioEffectType.BfxCompressor] = 0x1000F,
            [BassAudioEffectType.BfxDistortion] = 0x10010,
            [BassAudioEffectType.BfxCompressor2] = 0x10011,
            [BassAudioEffectType.BfxVolumeEnvelope] = 0x10012,
            [BassAudioEffectType.BfxBqf] = 0x10013,
            [BassAudioEffectType.BfxEcho4] = 0x10014,
            [BassAudioEffectType.BfxPitchShift] = 0x10015,
            [BassAudioEffectType.BfxFreeverb] = 0x10016
        };

        BassAudioEffectDefinition[] definitions = BassAudioEffectCatalog.Definitions.ToArray();
        foreach ((BassAudioEffectType type, int nativeId) in expectedNativeIds)
        {
            BassAudioEffectDefinition definition = definitions.Single(definition => definition.Type == type);
            Assert.AreEqual(nativeId, definition.NativeTypeId, $"{type} のnative ID");
            Assert.AreEqual(nativeId, (int)definition.ManagedBassType, $"{type} のManagedBass指定値");
        }

        Assert.AreEqual(definitions.Length, definitions.Select(definition => definition.NativeTypeId).Distinct().Count());
    }

    /// <summary>
    /// 独自scalar引数adapterのサイズとfield offsetをBASSの外部ABIへ揃え、異なる位置・長さでnativeへ渡す誤りを検出する。
    /// </summary>
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

    /// <summary>
    /// PitchShiftのnative引数をlegacy32bit field配置で書き換えて、各利用者指定値をmanaged引数へコピーして戻す。
    /// </summary>
    [TestMethod]
    public void PitchShiftAdapter_UsesLegacy32BitLayoutAndCopiesBack()
    {
        var parameters = new ManagedBassBfxPitchShiftParameters();

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

    /// <summary>
    /// MixとVolumeEnvelopeの配列をnative呼出し中だけpinし、nativeの変更を返して解放後はpointerの再使用を拒否する。
    /// </summary>
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

    /// <summary>
    /// nativeが返したVolumeEnvelopeのnode数とpointerから全node値を再構築し、元の空配列のまま返さない。
    /// </summary>
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

    /// <summary>
    /// 実Disposeがcleanupの元例外を返し、準備フレーム終了後のfull GCで対象配列が回収されることを、disposed leaseを生存させたまま確認する。
    /// </summary>
    [TestMethod]
    public void NativeParameterLease_PreservesCleanupPrimaryErrorAndReleasesPins()
    {
        (InvalidOperationException? expectedFailure, Exception? actualFailure, WeakReference? target, NativeEffectParameterLease? lease) = CreateAndDisposeFailingParameterLease();
        try
        {
            Assert.AreSame(expectedFailure, actualFailure);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            Assert.IsFalse(target.IsAlive, "cleanup例外後もlease所有のpinが対象配列を保持してはいけません。");
        }
        finally
        {
            GC.KeepAlive(lease);
        }
    }

    /// <summary>配列を局所フレームだけで所有し、pinを実leaseへ移譲してから実Disposeの例外と弱参照を返します。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (InvalidOperationException ExpectedFailure, Exception? ActualFailure, WeakReference Target, NativeEffectParameterLease Lease)
        CreateAndDisposeFailingParameterLease()
    {
        byte[] target = new byte[1];
        var weakTarget = new WeakReference(target);
        var pin = GCHandle.Alloc(target, GCHandleType.Pinned);
        InvalidOperationException cleanupFailure = new("cleanup sentinel");
        NativeEffectParameterLease lease;
        try
        {
            lease = NativeEffectParameterLease.Create(
                new ManagedBassBfxVolumeParameters(),
                pins: new[] { pin },
                cleanup: () => throw cleanupFailure);
        }
        catch
        {
            pin.Free();
            throw;
        }

        Exception? actualFailure = null;
        try { lease.Dispose(); }
        catch (Exception exception) { actualFailure = exception; }
        return (cleanupFailure, actualFailure, weakTarget, lease);
    }

}
