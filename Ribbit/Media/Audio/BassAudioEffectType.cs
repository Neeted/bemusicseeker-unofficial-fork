using System;
using System.Collections.Generic;
using ManagedBass;
using ManagedBass.DirectX8;
using ManagedBass.Fx;

namespace Ribbit.Media.Audio;

/// <summary>
/// The wrapper-neutral effect categories retained from the BASS.NET effect catalog.
/// These values are logical catalog identities and are deliberately independent from
/// ManagedBass/native effect IDs.
/// </summary>
internal enum BassAudioEffectType
{
    Dx8Chorus = 0,
    Dx8Compressor = 1,
    Dx8Distortion = 2,
    Dx8Echo = 3,
    Dx8Flanger = 4,
    Dx8Gargle = 5,
    Dx8I3dl2Reverb = 6,
    Dx8ParamEq = 7,
    Dx8Reverb = 8,
    BfxRotate = 9,
    BfxEcho = 10,
    BfxFlanger = 11,
    BfxVolume = 12,
    BfxPeakEq = 13,
    BfxReverb = 14,
    BfxLpf = 15,
    BfxMix = 16,
    BfxDamp = 17,
    BfxAutoWah = 18,
    BfxEcho2 = 19,
    BfxPhaser = 20,
    BfxEcho3 = 21,
    BfxChorus = 22,
    BfxApf = 23,
    BfxCompressor = 24,
    BfxDistortion = 25,
    BfxCompressor2 = 26,
    BfxVolumeEnvelope = 27,
    BfxBqf = 28,
    BfxEcho4 = 29,
    BfxPitchShift = 30,
    BfxFreeverb = 31
}

/// <summary>
/// Explicit ManagedBass/native IDs for the effect categories whose names or values
/// differ from the logical application catalog.
/// </summary>
internal static class BassAudioEffectNativeTypes
{
    internal const EffectType Dx8Chorus = (EffectType)0;
    internal const EffectType Dx8Compressor = (EffectType)1;
    internal const EffectType Dx8Distortion = (EffectType)2;
    internal const EffectType Dx8Echo = (EffectType)3;
    internal const EffectType Dx8Flanger = (EffectType)4;
    internal const EffectType Dx8Gargle = (EffectType)5;
    internal const EffectType Dx8I3dl2Reverb = (EffectType)6;
    internal const EffectType Dx8ParamEq = (EffectType)7;
    internal const EffectType Dx8Reverb = (EffectType)8;
    internal const EffectType BfxRotate = (EffectType)65536;
    internal const EffectType BfxEcho = (EffectType)65537;
    internal const EffectType BfxFlanger = (EffectType)65538;
    internal const EffectType BfxVolume = (EffectType)65539;
    internal const EffectType BfxPeakEq = (EffectType)65540;
    internal const EffectType BfxReverb = (EffectType)65541;
    internal const EffectType BfxLpf = (EffectType)65542;
    internal const EffectType BfxMix = (EffectType)65543;
    internal const EffectType BfxDamp = (EffectType)65544;
    internal const EffectType BfxAutoWah = (EffectType)65545;
    internal const EffectType BfxEcho2 = (EffectType)65546;
    internal const EffectType BfxPhaser = (EffectType)65547;
    internal const EffectType BfxEcho3 = (EffectType)65548;
    internal const EffectType BfxChorus = (EffectType)65549;
    internal const EffectType BfxApf = (EffectType)65550;
    internal const EffectType BfxCompressor = (EffectType)65551;
    internal const EffectType BfxDistortion = (EffectType)65552;
    internal const EffectType BfxCompressor2 = (EffectType)65553;
    internal const EffectType BfxVolumeEnvelope = (EffectType)65554;
    internal const EffectType BfxBqf = (EffectType)65555;
    internal const EffectType BfxEcho4 = (EffectType)65556;
    internal const EffectType BfxPitchShift = (EffectType)65557;
    internal const EffectType BfxFreeverb = (EffectType)65558;
}

/// <summary>
/// Describes one effect category and the ManagedBass parameter object used for it.
/// </summary>
internal sealed class BassAudioEffectDefinition
{
    private readonly Func<IEffectParameter> parameterFactory;

    internal BassAudioEffectDefinition(
        BassAudioEffectType type,
        EffectType managedBassType,
        Type parameterType,
        Func<IEffectParameter> parameterFactory)
    {
        ArgumentNullException.ThrowIfNull(parameterType);
        ArgumentNullException.ThrowIfNull(parameterFactory);
        Type = type;
        ManagedBassType = managedBassType;
        ParameterType = parameterType;
        this.parameterFactory = parameterFactory;
    }

    /// <summary>Gets the wrapper-neutral category.</summary>
    internal BassAudioEffectType Type { get; }

    /// <summary>Gets the explicit native BASS effect ID.</summary>
    internal int NativeTypeId => (int)ManagedBassType;

    /// <summary>Gets the parameter object type created for the category.</summary>
    internal Type ParameterType { get; }

    /// <summary>Gets the explicitly mapped ManagedBass enum representation.</summary>
    internal EffectType ManagedBassType { get; }

    /// <summary>Creates a fresh parameter object without retaining native state.</summary>
    internal IEffectParameter CreateParameters() => parameterFactory();
}

/// <summary>
/// Owns the complete effect inventory and the narrow ManagedBass call boundary used by the player.
/// </summary>
internal static class BassAudioEffectCatalog
{
    private static readonly IReadOnlyList<BassAudioEffectDefinition> definitions =
        Array.AsReadOnly(
        [
            Define(BassAudioEffectType.Dx8Chorus, BassAudioEffectNativeTypes.Dx8Chorus, typeof(DXChorusParameters), static () => new DXChorusParameters
            {
                fWetDryMix = 0f,
                fDepth = 25f,
                fFeedback = 0f,
                fFrequency = 0f,
                lWaveform = DXWaveform.Sine,
                fDelay = 0f,
                lPhase = DXPhase.Zero
            }),
            Define(BassAudioEffectType.Dx8Compressor, BassAudioEffectNativeTypes.Dx8Compressor, typeof(DXCompressorParameters), static () => new DXCompressorParameters
            {
                fGain = 0f,
                fAttack = 10f,
                fRelease = 200f,
                fThreshold = -20f,
                fRatio = 3f,
                fPredelay = 4f
            }),
            Define(BassAudioEffectType.Dx8Distortion, BassAudioEffectNativeTypes.Dx8Distortion, typeof(DXDistortionParameters), static () => new DXDistortionParameters
            {
                fGain = 0f,
                fEdge = 50f,
                fPostEQCenterFrequency = 4000f,
                fPostEQBandwidth = 4000f,
                fPreLowpassCutoff = 4000f
            }),
            Define(BassAudioEffectType.Dx8Echo, BassAudioEffectNativeTypes.Dx8Echo, typeof(DXEchoParameters), static () => new DXEchoParameters
            {
                fWetDryMix = 0f,
                fFeedback = 0f,
                fLeftDelay = 333f,
                fRightDelay = 333f,
                lPanDelay = false
            }),
            Define(BassAudioEffectType.Dx8Flanger, BassAudioEffectNativeTypes.Dx8Flanger, typeof(DXFlangerParameters), static () => new DXFlangerParameters
            {
                fWetDryMix = 0f,
                fDepth = 25f,
                fFeedback = 0f,
                fFrequency = 0f,
                lWaveform = DXWaveform.Sine,
                fDelay = 0f,
                lPhase = DXPhase.Zero
            }),
            Define(BassAudioEffectType.Dx8Gargle, BassAudioEffectNativeTypes.Dx8Gargle, typeof(DXGargleParameters), static () => new DXGargleParameters
            {
                dwRateHz = 500,
                dwWaveShape = DXWaveform.Sine
            }),
            Define(BassAudioEffectType.Dx8I3dl2Reverb, BassAudioEffectNativeTypes.Dx8I3dl2Reverb, typeof(DX_ID3DL2ReverbParameters), static () => new DX_ID3DL2ReverbParameters
            {
                lRoom = -1000,
                lRoomHF = 0,
                flRoomRolloffFactor = 0f,
                flDecayTime = 1.49f,
                flDecayHFRatio = 0.83f,
                lReflections = -2602,
                flReflectionsDelay = 0.007f,
                lReverb = 200,
                flReverbDelay = 0.011f,
                flDiffusion = 100f,
                flDensity = 100f,
                flHFReference = 5000f
            }),
            Define(BassAudioEffectType.Dx8ParamEq, BassAudioEffectNativeTypes.Dx8ParamEq, typeof(DXParamEQParameters), static () => new DXParamEQParameters
            {
                fCenter = 100f,
                fBandwidth = 18f,
                fGain = 0f
            }),
            Define(BassAudioEffectType.Dx8Reverb, BassAudioEffectNativeTypes.Dx8Reverb, typeof(DXReverbParameters), static () => new DXReverbParameters
            {
                fInGain = 0f,
                fReverbMix = 0f,
                fReverbTime = 1000f,
                fHighFreqRTRatio = 0.001f
            }),
            Define(BassAudioEffectType.BfxRotate, BassAudioEffectNativeTypes.BfxRotate, typeof(RotateParameters), static () => new RotateParameters()),
            Define(BassAudioEffectType.BfxEcho, BassAudioEffectNativeTypes.BfxEcho, typeof(ManagedBassBfxEchoParameters), static () => new ManagedBassBfxEchoParameters()),
            Define(BassAudioEffectType.BfxFlanger, BassAudioEffectNativeTypes.BfxFlanger, typeof(ManagedBassBfxFlangerParameters), static () => new ManagedBassBfxFlangerParameters()),
            Define(BassAudioEffectType.BfxVolume, BassAudioEffectNativeTypes.BfxVolume, typeof(ManagedBassBfxVolumeParameters), static () => new ManagedBassBfxVolumeParameters()),
            Define(BassAudioEffectType.BfxPeakEq, BassAudioEffectNativeTypes.BfxPeakEq, typeof(PeakEQParameters), static () => new PeakEQParameters()),
            Define(BassAudioEffectType.BfxReverb, BassAudioEffectNativeTypes.BfxReverb, typeof(ManagedBassBfxReverbParameters), static () => new ManagedBassBfxReverbParameters()),
            Define(BassAudioEffectType.BfxLpf, BassAudioEffectNativeTypes.BfxLpf, typeof(ManagedBassBfxLpfParameters), static () => new ManagedBassBfxLpfParameters()),
            Define(BassAudioEffectType.BfxMix, BassAudioEffectNativeTypes.BfxMix, typeof(ManagedBassBfxMixParameters), static () => new ManagedBassBfxMixParameters()),
            Define(BassAudioEffectType.BfxDamp, BassAudioEffectNativeTypes.BfxDamp, typeof(DampParameters), static () => new DampParameters()),
            Define(BassAudioEffectType.BfxAutoWah, BassAudioEffectNativeTypes.BfxAutoWah, typeof(AutoWahParameters), static () => new AutoWahParameters
            {
                fDryMix = 0f,
                fWetMix = 0f,
                fFeedback = 0f,
                fRate = 0f,
                fRange = 0f,
                fFreq = 0f,
                lChannel = FXChannelFlags.All
            }),
            Define(BassAudioEffectType.BfxEcho2, BassAudioEffectNativeTypes.BfxEcho2, typeof(ManagedBassBfxEcho2Parameters), static () => new ManagedBassBfxEcho2Parameters()),
            Define(BassAudioEffectType.BfxPhaser, BassAudioEffectNativeTypes.BfxPhaser, typeof(PhaserParameters), static () => new PhaserParameters
            {
                fDryMix = 0f,
                fWetMix = 0f,
                fFeedback = 0f,
                fRate = 0f,
                fRange = 0f,
                fFreq = 0f,
                lChannel = FXChannelFlags.All
            }),
            Define(BassAudioEffectType.BfxEcho3, BassAudioEffectNativeTypes.BfxEcho3, typeof(ManagedBassBfxEcho3Parameters), static () => new ManagedBassBfxEcho3Parameters()),
            Define(BassAudioEffectType.BfxChorus, BassAudioEffectNativeTypes.BfxChorus, typeof(ChorusParameters), static () => new ChorusParameters
            {
                fDryMix = 0f,
                fWetMix = 0f,
                fFeedback = 0f,
                fMinSweep = 0f,
                fMaxSweep = 0f,
                fRate = 0f,
                lChannel = FXChannelFlags.All
            }),
            Define(BassAudioEffectType.BfxApf, BassAudioEffectNativeTypes.BfxApf, typeof(ManagedBassBfxApfParameters), static () => new ManagedBassBfxApfParameters()),
            Define(BassAudioEffectType.BfxCompressor, BassAudioEffectNativeTypes.BfxCompressor, typeof(ManagedBassBfxCompressorParameters), static () => new ManagedBassBfxCompressorParameters()),
            Define(BassAudioEffectType.BfxDistortion, BassAudioEffectNativeTypes.BfxDistortion, typeof(DistortionParameters), static () => new DistortionParameters
            {
                fDrive = 0f,
                fDryMix = 0f,
                fWetMix = 0f,
                fFeedback = 0f,
                fVolume = 0f,
                lChannel = FXChannelFlags.All
            }),
            Define(BassAudioEffectType.BfxCompressor2, BassAudioEffectNativeTypes.BfxCompressor2, typeof(CompressorParameters), static () => new CompressorParameters()),
            Define(BassAudioEffectType.BfxVolumeEnvelope, BassAudioEffectNativeTypes.BfxVolumeEnvelope, typeof(ManagedBassBfxVolumeEnvelopeParameters), static () => new ManagedBassBfxVolumeEnvelopeParameters()),
            Define(BassAudioEffectType.BfxBqf, BassAudioEffectNativeTypes.BfxBqf, typeof(BQFParameters), static () => new BQFParameters()),
            Define(BassAudioEffectType.BfxEcho4, BassAudioEffectNativeTypes.BfxEcho4, typeof(EchoParameters), static () => new EchoParameters()),
            Define(BassAudioEffectType.BfxPitchShift, BassAudioEffectNativeTypes.BfxPitchShift, typeof(ManagedBassBfxPitchShiftParameters), static () => new ManagedBassBfxPitchShiftParameters()),
            Define(BassAudioEffectType.BfxFreeverb, BassAudioEffectNativeTypes.BfxFreeverb, typeof(ReverbParameters), static () => new ReverbParameters())
        ]);

    private static readonly IReadOnlyDictionary<BassAudioEffectType, BassAudioEffectDefinition> definitionsByType =
        CreateDefinitionMap();

    /// <summary>Gets the immutable-in-use inventory of all 32 legacy effect categories.</summary>
    internal static IReadOnlyList<BassAudioEffectDefinition> Definitions => definitions;

    /// <summary>Gets a definition for a category, or <see langword="false"/> when it is unknown.</summary>
    internal static bool TryGetDefinition(
        BassAudioEffectType type,
        out BassAudioEffectDefinition definition)
        => definitionsByType.TryGetValue(type, out definition!);

    /// <summary>Creates the parameter object for a known effect category.</summary>
    internal static IEffectParameter CreateParameters(BassAudioEffectType type)
    {
        if (!TryGetDefinition(type, out BassAudioEffectDefinition definition))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        return definition.CreateParameters();
    }

    /// <summary>Returns the explicit native ID for a project-owned effect category.</summary>
    internal static int GetNativeTypeId(BassAudioEffectType type)
    {
        if (!TryGetDefinition(type, out BassAudioEffectDefinition definition))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        return definition.NativeTypeId;
    }

    /// <summary>Attaches an effect using the catalog's explicit native effect ID.</summary>
    internal static int ChannelSetFX(int channel, BassAudioEffectType type, int priority)
    {
        if (!TryGetDefinition(type, out BassAudioEffectDefinition definition))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        return Bass.ChannelSetFX(channel, definition.ManagedBassType, priority);
    }

    /// <summary>Sets parameters, pinning pointer-bearing adapters only for this call.</summary>
    internal static bool SetParameters(int effectHandle, IEffectParameter parameters)
        => ManagedBassEffectParameters.SetParameters(effectHandle, parameters);

    /// <summary>Gets parameters, copying pointer-bearing adapter data back before unpinning.</summary>
    internal static bool GetParameters(int effectHandle, IEffectParameter parameters)
        => ManagedBassEffectParameters.GetParameters(effectHandle, parameters);

    private static BassAudioEffectDefinition Define(
        BassAudioEffectType type,
        EffectType managedBassType,
        Type parameterType,
        Func<IEffectParameter> parameterFactory)
        => new(type, managedBassType, parameterType, parameterFactory);

    private static IReadOnlyDictionary<BassAudioEffectType, BassAudioEffectDefinition> CreateDefinitionMap()
    {
        Dictionary<BassAudioEffectType, BassAudioEffectDefinition> map = new(definitions.Count);
        foreach (BassAudioEffectDefinition definition in definitions)
        {
            if (!map.TryAdd(definition.Type, definition))
            {
                throw new InvalidOperationException("Duplicate audio effect category: " + definition.Type);
            }
        }

        return map;
    }
}
