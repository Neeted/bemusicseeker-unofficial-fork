using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using ManagedBass;

namespace Ribbit.Media.Audio;

/// <summary>
/// Provides the ManagedBass parameter call boundary, including temporary pinning for
/// parameter blocks that contain native pointers.
/// </summary>
internal static class ManagedBassEffectParameters
{
    /// <summary>Sets an effect parameter using the appropriate ManagedBass overload.</summary>
    internal static bool SetParameters(int effectHandle, IEffectParameter parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters is not IManagedBassNativeEffectParameter nativeParameter)
        {
            return Bass.FXSetParameters(effectHandle, parameters);
        }

        using NativeEffectParameterLease lease = nativeParameter.PinForNativeCall();
        return Bass.FXSetParameters(effectHandle, lease.Pointer);
    }

    /// <summary>Gets an effect parameter and copies pointer-backed values before unpinning.</summary>
    internal static bool GetParameters(int effectHandle, IEffectParameter parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters is not IManagedBassNativeEffectParameter nativeParameter)
        {
            return Bass.FXGetParameters(effectHandle, parameters);
        }

        using NativeEffectParameterLease lease = nativeParameter.PinForNativeCall();
        bool result = Bass.FXGetParameters(effectHandle, lease.Pointer);
        if (result)
        {
            lease.CopyBack();
        }

        return result;
    }

    /// <summary>
    /// Creates a call-duration native block for tests and the player boundary.
    /// The returned lease owns all temporary unmanaged allocations and pins.
    /// </summary>
    internal static NativeEffectParameterLease CreateNativeScope(IEffectParameter parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters is not IManagedBassNativeEffectParameter nativeParameter)
        {
            throw new ArgumentException(
                "The parameter type uses ManagedBass' built-in marshaller.",
                nameof(parameters));
        }

        return nativeParameter.PinForNativeCall();
    }
}

/// <summary>Owns a temporary native parameter block for one BASS_FX call.</summary>
internal sealed class NativeEffectParameterLease : IDisposable
{
    private readonly Action copyBack;
    private readonly Action cleanup;
    private readonly object copyToManaged;
    private readonly Type parameterType;
    private IntPtr nativeMemory;
    private bool disposed;

    private NativeEffectParameterLease(
        IntPtr nativeMemory,
        Type parameterType,
        object copyToManaged,
        Action copyBack,
        Action cleanup)
    {
        this.nativeMemory = nativeMemory;
        this.parameterType = parameterType;
        this.copyToManaged = copyToManaged;
        this.copyBack = copyBack;
        this.cleanup = cleanup;
    }

    /// <summary>Gets the temporary native parameter address.</summary>
    internal IntPtr Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return nativeMemory;
        }
    }

    /// <summary>Copies native output into managed pointer-backed values.</summary>
    internal void CopyBack()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (copyToManaged is not null)
        {
            Marshal.PtrToStructure(nativeMemory, copyToManaged);
        }

        copyBack?.Invoke();
    }

    /// <summary>Allocates one temporary formatted block and optionally pins backing arrays.</summary>
    internal static NativeEffectParameterLease Create(
        object parameters,
        IReadOnlyList<GCHandle> pins = null,
        object copyToManaged = null,
        Action copyBack = null,
        Action cleanup = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Type parameterType = parameters.GetType();
        IntPtr nativeMemory = Marshal.AllocHGlobal(Marshal.SizeOf(parameters));
        try
        {
            Marshal.StructureToPtr(parameters, nativeMemory, fDeleteOld: false);
            return new NativeEffectParameterLease(
                nativeMemory,
                parameterType,
                copyToManaged,
                copyBack,
                CreateCleanup(cleanup, pins));
        }
        catch
        {
            Marshal.FreeHGlobal(nativeMemory);
            throw;
        }
    }

    private static Action CreateCleanup(Action cleanup, IReadOnlyList<GCHandle> pins)
        => () =>
        {
            Exception failure = null;
            try
            {
                cleanup?.Invoke();
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            try
            {
                FreePins(pins);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        };

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Exception failure = null;
        try
        {
            Marshal.DestroyStructure(nativeMemory, parameterType);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            cleanup?.Invoke();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            Marshal.FreeHGlobal(nativeMemory);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
        finally
        {
            nativeMemory = IntPtr.Zero;
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void FreePins(IReadOnlyList<GCHandle> pins)
    {
        if (pins is null)
        {
            return;
        }

        Exception failure = null;
        for (int index = pins.Count - 1; index >= 0; index--)
        {
            if (pins[index].IsAllocated)
            {
                try
                {
                    pins[index].Free();
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}

/// <summary>Implemented by project-owned parameter blocks requiring explicit native marshalling.</summary>
internal interface IManagedBassNativeEffectParameter
{
    /// <summary>Creates a lease whose pointer is valid until disposal.</summary>
    NativeEffectParameterLease PinForNativeCall();
}

/// <summary>Base for project-owned scalar BASS_FX parameter blocks.</summary>
[StructLayout(LayoutKind.Sequential)]
internal abstract class ManagedBassCustomEffectParameters : IEffectParameter, IManagedBassNativeEffectParameter
{
    /// <inheritdoc />
    public abstract EffectType FXType { get; }

    NativeEffectParameterLease IManagedBassNativeEffectParameter.PinForNativeCall()
        => PinForNativeCall();

    /// <summary>Creates the scalar block and enables managed copy-back for get calls.</summary>
    internal virtual NativeEffectParameterLease PinForNativeCall()
        => NativeEffectParameterLease.Create(this, copyToManaged: this);
}

/// <summary>Managed representation of the legacy BFX echo block.</summary>
[StructLayout(LayoutKind.Sequential)]
internal sealed class ManagedBassBfxEchoParameters : ManagedBassCustomEffectParameters
{
    internal float Level;
    internal int Delay;

    internal ManagedBassBfxEchoParameters()
    {
        Delay = 1200;
    }

    /// <inheritdoc />
    public override EffectType FXType => BassAudioEffectNativeTypes.BfxEcho;
}

/// <summary>Managed representation of the legacy BFX flanger block.</summary>
[StructLayout(LayoutKind.Sequential)]
internal sealed class ManagedBassBfxFlangerParameters : ManagedBassCustomEffectParameters
{
    internal float WetDry;
    internal float Speed;
    internal int Channel;

    internal ManagedBassBfxFlangerParameters()
    {
        WetDry = 1f;
        Speed = 0.01f;
        Channel = -1;
    }

    /// <inheritdoc />
    public override EffectType FXType => BassAudioEffectNativeTypes.BfxFlanger;
}

/// <summary>Managed representation of the legacy BFX volume block.</summary>
[StructLayout(LayoutKind.Sequential)]
internal sealed class ManagedBassBfxVolumeParameters : ManagedBassCustomEffectParameters
{
    internal int Channel;
    internal float Volume;

    internal ManagedBassBfxVolumeParameters()
    {
        Channel = -1;
        Volume = 1f;
    }

    /// <inheritdoc />
    public override EffectType FXType => BassAudioEffectNativeTypes.BfxVolume;
}

/// <summary>Managed representation of the legacy BFX reverb block.</summary>
[StructLayout(LayoutKind.Sequential)]
internal sealed class ManagedBassBfxReverbParameters : ManagedBassCustomEffectParameters
{
    internal float Level;
    internal int Delay;

    internal ManagedBassBfxReverbParameters()
    {
        Delay = 1200;
    }

    /// <inheritdoc />
    public override EffectType FXType => BassAudioEffectNativeTypes.BfxReverb;
}

/// <summary>Managed representation of the legacy BFX low-pass filter block.</summary>
[StructLayout(LayoutKind.Sequential)]
internal sealed class ManagedBassBfxLpfParameters : ManagedBassCustomEffectParameters
{
    internal float Resonance;
    internal float CutOffFrequency;
    internal int Channel;

    internal ManagedBassBfxLpfParameters()
    {
        Resonance = 2f;
        CutOffFrequency = 200f;
        Channel = -1;
    }

    /// <inheritdoc />
    public override EffectType FXType => BassAudioEffectNativeTypes.BfxLpf;
}

/// <summary>Managed representation of the legacy BFX echo 2 block.</summary>
[StructLayout(LayoutKind.Sequential)]
internal sealed class ManagedBassBfxEcho2Parameters : ManagedBassCustomEffectParameters
{
    internal float DryMix;
    internal float WetMix;
    internal float Feedback;
    internal float Delay;
    internal int Channel;

    internal ManagedBassBfxEcho2Parameters()
    {
        Channel = -1;
    }

    /// <inheritdoc />
    public override EffectType FXType => BassAudioEffectNativeTypes.BfxEcho2;
}

/// <summary>Managed representation of the legacy BFX echo 3 block.</summary>
[StructLayout(LayoutKind.Sequential)]
internal sealed class ManagedBassBfxEcho3Parameters : ManagedBassCustomEffectParameters
{
    internal float DryMix;
    internal float WetMix;
    internal float Delay;
    internal int Channel;

    internal ManagedBassBfxEcho3Parameters()
    {
        Channel = -1;
    }

    /// <inheritdoc />
    public override EffectType FXType => BassAudioEffectNativeTypes.BfxEcho3;
}

/// <summary>Managed representation of the legacy BFX all-pass filter block.</summary>
[StructLayout(LayoutKind.Sequential)]
internal sealed class ManagedBassBfxApfParameters : ManagedBassCustomEffectParameters
{
    internal float Gain;
    internal float Delay;
    internal int Channel;

    internal ManagedBassBfxApfParameters()
    {
        Channel = -1;
    }

    /// <inheritdoc />
    public override EffectType FXType => BassAudioEffectNativeTypes.BfxApf;
}

/// <summary>Managed representation of the legacy BFX compressor block.</summary>
[StructLayout(LayoutKind.Sequential)]
internal sealed class ManagedBassBfxCompressorParameters : ManagedBassCustomEffectParameters
{
    internal float Threshold;
    internal float AttackTime;
    internal float ReleaseTime;
    internal int Channel;

    internal ManagedBassBfxCompressorParameters()
    {
        Channel = -1;
    }

    /// <inheritdoc />
    public override EffectType FXType => BassAudioEffectNativeTypes.BfxCompressor;
}

/// <summary>Managed representation of the legacy BFX pitch-shift block.</summary>
[StructLayout(LayoutKind.Sequential)]
internal sealed class ManagedBassBfxPitchShiftParameters : ManagedBassCustomEffectParameters
{
    internal float PitchShift;
    internal float FineTune;
    internal int FFTSize;
    internal int Oversampling;
    internal int Channel;

    internal ManagedBassBfxPitchShiftParameters()
    {
        PitchShift = 1f;
        FineTune = 0f;
        FFTSize = 2048;
        Oversampling = 8;
        Channel = -1;
    }

    /// <inheritdoc />
    public override EffectType FXType => BassAudioEffectNativeTypes.BfxPitchShift;
}

/// <summary>A native BASS_FX envelope node.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ManagedBassBfxEnvelopeNode
{
    internal double Position;
    internal float Value;
}

/// <summary>Managed representation of the pointer-bearing BFX mix block.</summary>
[StructLayout(LayoutKind.Sequential)]
internal sealed class ManagedBassBfxMixParameters : ManagedBassCustomEffectParameters
{
    [StructLayout(LayoutKind.Sequential)]
    private sealed class NativeParameters
    {
        internal IntPtr ChannelsPointer;
    }

    internal IntPtr ChannelsPointer;

    [NonSerialized]
    private readonly int[] channels;

    internal ManagedBassBfxMixParameters()
        : this(Array.Empty<int>())
    {
    }

    internal ManagedBassBfxMixParameters(params int[] channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        this.channels = (int[])channels.Clone();
    }

    /// <summary>Gets the managed channel-remap array retained outside the native call.</summary>
    internal IReadOnlyList<int> Channels => channels;

    /// <inheritdoc />
    public override EffectType FXType => BassAudioEffectNativeTypes.BfxMix;

    /// <inheritdoc />
    internal override NativeEffectParameterLease PinForNativeCall()
    {
        GCHandle pin = default;
        try
        {
            if (channels.Length != 0)
            {
                pin = GCHandle.Alloc(channels, GCHandleType.Pinned);
                ChannelsPointer = pin.AddrOfPinnedObject();
            }

            NativeParameters nativeParameters = new()
            {
                ChannelsPointer = ChannelsPointer
            };
            return NativeEffectParameterLease.Create(
                nativeParameters,
                pins: pin.IsAllocated ? new[] { pin } : null,
                copyToManaged: nativeParameters,
                copyBack: () => CopyBackFromNative(nativeParameters.ChannelsPointer),
                cleanup: () => ChannelsPointer = IntPtr.Zero);
        }
        catch
        {
            if (pin.IsAllocated)
            {
                pin.Free();
            }

            ChannelsPointer = IntPtr.Zero;
            throw;
        }
    }

    private void CopyBackFromNative(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
        {
            return;
        }

        for (int index = 0; index < channels.Length; index++)
        {
            channels[index] = Marshal.ReadInt32(pointer, index * sizeof(int));
        }
    }
}

/// <summary>Managed representation of the pointer-bearing BFX volume-envelope block.</summary>
[StructLayout(LayoutKind.Sequential)]
internal sealed class ManagedBassBfxVolumeEnvelopeParameters : ManagedBassCustomEffectParameters
{
    [StructLayout(LayoutKind.Sequential)]
    private sealed class NativeParameters
    {
        internal int Channel;
        internal int NodeCount;
        internal IntPtr NodesPointer;
        internal int FollowNative;
    }

    private sealed class NodeState
    {
        internal NodeState(ManagedBassBfxEnvelopeNode[] nodes)
        {
            Nodes = (ManagedBassBfxEnvelopeNode[])nodes.Clone();
        }

        internal ManagedBassBfxEnvelopeNode[] Nodes { get; }
    }

    private static readonly ConditionalWeakTable<
        ManagedBassBfxVolumeEnvelopeParameters,
        NodeState> NodeStates = new();

    internal int Channel;
    internal int NodeCount;
    internal IntPtr NodesPointer;
    internal int FollowNative;

    internal ManagedBassBfxVolumeEnvelopeParameters()
        : this(-1, follow: true)
    {
    }

    internal ManagedBassBfxVolumeEnvelopeParameters(
        int channel,
        bool follow,
        params ManagedBassBfxEnvelopeNode[] nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        Channel = channel;
        Follow = follow;
        NodeStates.Add(this, new NodeState(nodes));
        NodeCount = nodes.Length;
    }

    /// <summary>Gets or sets the native BASS channel mask.</summary>
    internal int ChannelMask
    {
        get => Channel;
        set => Channel = value;
    }

    /// <summary>Gets or sets whether the envelope follows the channel position.</summary>
    internal bool Follow
    {
        get => FollowNative != 0;
        set => FollowNative = value ? 1 : 0;
    }

    /// <summary>Gets the managed node array retained outside the native call.</summary>
    internal IReadOnlyList<ManagedBassBfxEnvelopeNode> Nodes => GetNodeState().Nodes;

    /// <inheritdoc />
    public override EffectType FXType => BassAudioEffectNativeTypes.BfxVolumeEnvelope;

    /// <inheritdoc />
    internal override NativeEffectParameterLease PinForNativeCall()
    {
        GCHandle pin = default;
        try
        {
            ManagedBassBfxEnvelopeNode[] nodes = GetNodeState().Nodes;
            if (nodes.Length != 0)
            {
                pin = GCHandle.Alloc(nodes, GCHandleType.Pinned);
                NodesPointer = pin.AddrOfPinnedObject();
            }

            NodeCount = nodes.Length;
            NativeParameters nativeParameters = new()
            {
                Channel = Channel,
                NodeCount = NodeCount,
                NodesPointer = NodesPointer,
                FollowNative = FollowNative
            };
            return NativeEffectParameterLease.Create(
                nativeParameters,
                pins: pin.IsAllocated ? new[] { pin } : null,
                copyToManaged: nativeParameters,
                copyBack: () => CopyBackFromNative(nativeParameters),
                cleanup: () => NodesPointer = IntPtr.Zero);
        }
        catch
        {
            if (pin.IsAllocated)
            {
                pin.Free();
            }

            NodesPointer = IntPtr.Zero;
            throw;
        }
    }

    private void CopyBackFromNative(NativeParameters nativeParameters)
    {
        int nodeCount = nativeParameters.NodeCount;
        if (nodeCount < 0)
        {
            throw new InvalidDataException("BASS_FX returned a negative volume-envelope node count.");
        }

        int size = Marshal.SizeOf<ManagedBassBfxEnvelopeNode>();
        long byteLength = (long)nodeCount * size;
        if (byteLength > int.MaxValue)
        {
            throw new InvalidDataException("BASS_FX returned an oversized volume-envelope node block.");
        }

        if (nodeCount > 0 && nativeParameters.NodesPointer == IntPtr.Zero)
        {
            throw new InvalidDataException(
                "BASS_FX returned volume-envelope nodes without a native node pointer.");
        }

        ManagedBassBfxEnvelopeNode[] nodes = new ManagedBassBfxEnvelopeNode[nodeCount];
        for (int index = 0; index < nodeCount; index++)
        {
            nodes[index] = Marshal.PtrToStructure<ManagedBassBfxEnvelopeNode>(
                IntPtr.Add(nativeParameters.NodesPointer, checked(index * size)));
        }

        Channel = nativeParameters.Channel;
        FollowNative = nativeParameters.FollowNative;
        NodeStates.Remove(this);
        NodeStates.Add(this, new NodeState(nodes));
        NodeCount = nodeCount;
    }

    private NodeState GetNodeState()
        => NodeStates.GetValue(this, static _ => new NodeState(Array.Empty<ManagedBassBfxEnvelopeNode>()));
}
