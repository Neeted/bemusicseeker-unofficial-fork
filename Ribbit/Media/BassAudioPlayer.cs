using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using BeMusicSeeker.Models.Utils;
using OggVorbisDotNet64;
using Ribbit.Cryptography;
using Ribbit.Logging;
using Ribbit.Media.Audio;
using Ribbit.Util;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Fx;
using Un4seen.Bass.AddOn.Mix;
using Un4seen.BassAsio;
using Un4seen.BassWasapi;

namespace Ribbit.Media;

public class BassAudioPlayer : IAudioPlayer, IDisposable
{
    private class CachedData
    {
        internal int RefCount { get; set; }

        internal byte[] Data { get; }

        internal CachedData(byte[] data)
        {
            Data = data;
            RefCount = 1;
        }
    }

    public struct DeviceDescriptor(string name, string driver)
    {
        public string Name { get; set; } = name;

        public string Driver { get; set; } = driver;

        public readonly string FriendlyName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Name))
                {
                    return Name;
                }
                return "(Default device)";
            }
        }
    }

    public enum DeviceDriver
    {
        INVALID = -2,
        NULL_DEVICE,
        DIRECT_SOUND,
        WASAPI_SHARED,
        WASAPI_EXCLUSIVE,
        ASIO
    }

    private static readonly object StaticLockObject;

    private static readonly Dictionary<int, object> InstanceLocks;

    private static readonly NamedLocks<uint> Locks;

    private static readonly Dictionary<uint, CachedData> OnMemoryFileCache;

    private static readonly ReadOnlyDictionary<BASSASIOFormat, SampleFormat> FromBASSASIOFormat;

    private static readonly ReadOnlyDictionary<BASSWASAPIFormat, SampleFormat> FromBASSWASAPIFormat;

    private static readonly ReadOnlyDictionary<BASSWASAPIFormat, int> FromBASSWASAPIFormatToByte;

    private static readonly ReadOnlyDictionary<SampleFormat, BASSWASAPIFormat> ToBASSWASAPIFormat;

    protected static int inputMixer;

    protected static int outputMixer;

    protected static int procChannel;

    protected static int tempoChanger;

    protected static int volumeEffect;

    protected static int equalizer;

    private static readonly WASAPIPROC WasapiProc;

    private static readonly ASIOPROC AsioProc;

    private static readonly STREAMPROC StreamProc;

    private static readonly SYNCPROC EndProc;

    private static float playbackRate;

    private static readonly ReadOnlyDictionary<Type, BASSFXType> FxParameterTypeToBASSFXType;

    private static readonly ConcurrentDictionary<BASSFXType, Tuple<int, object>> FxParameters;

    public static readonly ReadOnlyCollection<float> EqualizerFrequencies;

    private static List<float> equalizerGains;

    private static bool _isInitialized;

    private static float latencyParam;

    private static SampleRate _frequency;

    private static SampleFormat _format;

    private static float _defaultVolume;

    private const float _volumeInitValue = 0.4f;

    private static float _deviceVolume;

    private static bool _isDeviceMuted;

    private static float _prevMasterVolume;

    private byte[] _sampleBuffer;

    private int _sampleBufferPos;

    private readonly BASS_FILEPROCS fileProc;

    private PlayState playState;

    private bool isMuted;

    private float prevVolume;

    private int _handle;

    private float _volume;

    private readonly uint fileNameHash;

    private bool disposedValue;

    public static ReadOnlyCollection<float> EqualizerGains => equalizerGains.AsReadOnly();

    public static bool EQEnabled => equalizer != 0;

    public static DeviceDriver DriverType { get; private set; }

    protected static bool IsInitialized
    {
        get
        {
            return _isInitialized;
        }
        set
        {
            if (!value)
            {
                DriverType = DeviceDriver.INVALID;
                Format = SampleFormat.AUTO;
                Frequency = SampleRate.AUTO;
                Latency = 0.0;
                CurrentVoices = 0;
                ClearMaxVoices();
                inputMixer = (outputMixer = (procChannel = (tempoChanger = 0)));
                volumeEffect = (equalizer = 0);
                playbackRate = 1f;
                FxParameters.Clear();
                equalizerGains = new float[10].ToList();
            }
            _isInitialized = value;
        }
    }

    public static ReadOnlyDictionary<DeviceDriver, ReadOnlyCollection<DeviceDescriptor>> DeviceList { get; }

    public static double Latency { get; private set; }

    public static SampleRate Frequency
    {
        get
        {
            return _frequency;
        }
        set
        {
            if (!IsInitialized)
            {
                _frequency = value;
            }
        }
    }

    public static SampleFormat Format
    {
        get
        {
            return _format;
        }
        set
        {
            if (!IsInitialized)
            {
                _format = ((value != SampleFormat.UNKNOWN) ? value : SampleFormat.AUTO);
            }
        }
    }

    public static int CurrentVoices { get; private set; }

    public static int MaxVoices { get; private set; }

    public static float DeviceVolume
    {
        get
        {
            return _deviceVolume;
        }
        set
        {
            if (!(_deviceVolume < 0f))
            {
                _deviceVolume = value;
                if (IsDeviceMuted)
                {
                    _prevMasterVolume = value;
                }
                else
                {
                    SetDeviceMasterVolume(value);
                }
            }
        }
    }

    public static float DefaultVolume
    {
        get
        {
            return _defaultVolume;
        }
        set
        {
            if (!(_defaultVolume < 0f))
            {
                _defaultVolume = value;
            }
        }
    }

    public static bool IsDeviceMuted
    {
        get
        {
            return _isDeviceMuted;
        }
        set
        {
            if (_isDeviceMuted != value)
            {
                _isDeviceMuted = value;
                if (value)
                {
                    _prevMasterVolume = DeviceVolume;
                    SetDeviceMasterVolume(0f);
                }
                else
                {
                    SetDeviceMasterVolume(_prevMasterVolume);
                }
            }
        }
    }

    public bool CanSeek => true;

    public TimeSpan CurrentTime
    {
        get
        {
            long pos = Bass.BASS_ChannelGetPosition(_handle);
            return TimeSpan.FromSeconds(Bass.BASS_ChannelBytes2Seconds(_handle, pos));
        }
        set
        {
            long pos = Bass.BASS_ChannelSeconds2Bytes(_handle, value.TotalSeconds);
            Bass.BASS_ChannelSetPosition(_handle, pos);
        }
    }

    public TimeSpan Duration { get; }

    public PlayState PlayState
    {
        get
        {
            if (!(CurrentTime == Duration))
            {
                return playState;
            }
            return PlayState.Stopped;
        }
    }

    public float Volume
    {
        get
        {
            return _volume;
        }
        set
        {
            if (!(value < 0f))
            {
                _volume = value;
                if (IsMuted)
                {
                    prevVolume = value;
                }
                else
                {
                    Bass.BASS_ChannelSetAttribute(_handle, BASSAttribute.BASS_ATTRIB_VOL, value);
                }
            }
        }
    }

    public bool IsMuted
    {
        get
        {
            return isMuted;
        }
        set
        {
            if (isMuted != value)
            {
                isMuted = value;
                if (value)
                {
                    prevVolume = Volume;
                    Bass.BASS_ChannelSetAttribute(_handle, BASSAttribute.BASS_ATTRIB_VOL, 0f);
                }
                else
                {
                    Bass.BASS_ChannelSetAttribute(_handle, BASSAttribute.BASS_ATTRIB_VOL, prevVolume);
                }
            }
        }
    }

    public string FileName { get; private set; }

    public float PlaybackRate
    {
        get
        {
            return playbackRate;
        }
        set
        {
            SetTempoChange(value);
        }
    }

    public static void Free()
    {
        if (!IsInitialized)
        {
            return;
        }
        switch (DriverType)
        {
            case DeviceDriver.ASIO:
                BassAsio.BASS_ASIO_Stop();
                if (procChannel != 0)
                {
                    Bass.BASS_StreamFree(procChannel);
                }
                if (tempoChanger != 0)
                {
                    Bass.BASS_StreamFree(tempoChanger);
                }
                if (inputMixer != 0)
                {
                    Bass.BASS_StreamFree(inputMixer);
                }
                BassAsio.BASS_ASIO_Free();
                break;
            case DeviceDriver.WASAPI_SHARED:
                SetDeviceMasterVolume(1f);
                goto case DeviceDriver.WASAPI_EXCLUSIVE;
            case DeviceDriver.WASAPI_EXCLUSIVE:
                BassWasapi.BASS_WASAPI_Stop(reset: true);
                if (procChannel != 0)
                {
                    Bass.BASS_StreamFree(procChannel);
                }
                if (tempoChanger != 0)
                {
                    Bass.BASS_StreamFree(tempoChanger);
                }
                if (inputMixer != 0)
                {
                    Bass.BASS_StreamFree(inputMixer);
                }
                BassWasapi.BASS_WASAPI_Free();
                break;
            default:
                if (procChannel != 0)
                {
                    Bass.BASS_StreamFree(procChannel);
                }
                if (tempoChanger != 0)
                {
                    Bass.BASS_StreamFree(tempoChanger);
                }
                if (inputMixer != 0)
                {
                    Bass.BASS_StreamFree(inputMixer);
                }
                break;
        }
        Ribbit.Media.Audio.BassNet.Free();
        IsInitialized = false;
    }

    public static DeviceDescriptor Initialize(DeviceDriver driver = DeviceDriver.WASAPI_EXCLUSIVE, DeviceDescriptor desc = default, float lParam = 0f, params object[] param)
    {
        if (IsInitialized || driver == DeviceDriver.INVALID)
        {
            return default;
        }
        DriverType = driver;
        latencyParam = lParam;
        switch (DriverType)
        {
            case DeviceDriver.ASIO:
                try
                {
                    desc = InitializeAsio(desc);
                    DriverType = DeviceDriver.ASIO;
                }
                catch (Exception ex)
                {
                    NLogWrapper.GetLogger()?.Warn("Initialize ASIO driver failed: " + ex.Message);
                    BassAsio.BASS_ASIO_Free();
                    Ribbit.Media.Audio.BassNet.Free();
                    goto case DeviceDriver.WASAPI_EXCLUSIVE;
                }
                break;
            case DeviceDriver.WASAPI_EXCLUSIVE:
                try
                {
                    desc = InitializeWasapi(desc, isSharedMode: false, param);
                    DriverType = DeviceDriver.WASAPI_EXCLUSIVE;
                }
                catch (Exception ex2)
                {
                    NLogWrapper.GetLogger()?.Warn("Initialize WASAPI(EX) driver failed: " + ex2.Message);
                    BassWasapi.BASS_WASAPI_Free();
                    Ribbit.Media.Audio.BassNet.Free();
                    goto case DeviceDriver.WASAPI_SHARED;
                }
                break;
            case DeviceDriver.WASAPI_SHARED:
                try
                {
                    desc = InitializeWasapi(desc, isSharedMode: true, param);
                    DriverType = DeviceDriver.WASAPI_SHARED;
                }
                catch (Exception ex3)
                {
                    NLogWrapper.GetLogger()?.Warn("Initialize WASAPI(SH) driver failed: " + ex3.Message);
                    BassWasapi.BASS_WASAPI_Free();
                    Ribbit.Media.Audio.BassNet.Free();
                    goto case DeviceDriver.DIRECT_SOUND;
                }
                break;
            case DeviceDriver.DIRECT_SOUND:
                try
                {
                    desc = InitializeDirectSound(desc);
                    DriverType = DeviceDriver.DIRECT_SOUND;
                }
                catch (Exception ex4)
                {
                    NLogWrapper.GetLogger()?.Warn("Initialize DirectSound driver failed: " + ex4.Message);
                    Ribbit.Media.Audio.BassNet.Free();
                    goto default;
                }
                break;
            default:
                InitializeNullDevice();
                desc = default;
                DriverType = DeviceDriver.NULL_DEVICE;
                break;
        }
        IsInitialized = true;
        if (DriverType == DeviceDriver.NULL_DEVICE)
        {
            DeviceVolume = 0.4f;
            DefaultVolume = 0.4f;
        }
        else
        {
            DeviceVolume = _deviceVolume;
            DefaultVolume = _defaultVolume;
        }
        return desc;
    }

    static BassAudioPlayer()
    {
        StaticLockObject = new object();
        InstanceLocks = [];
        Locks = new NamedLocks<uint>();
        OnMemoryFileCache = [];
        FromBASSASIOFormat = new ReadOnlyDictionary<BASSASIOFormat, SampleFormat>(new Dictionary<BASSASIOFormat, SampleFormat>
        {
            {
                BASSASIOFormat.BASS_ASIO_FORMAT_UNKNOWN,
                SampleFormat.UNKNOWN
            },
            {
                BASSASIOFormat.BASS_ASIO_FORMAT_16BIT,
                SampleFormat.SAMPLE_INT_16BIT
            },
            {
                BASSASIOFormat.BASS_ASIO_FORMAT_24BIT,
                SampleFormat.SAMPLE_INT_24BIT
            },
            {
                BASSASIOFormat.BASS_ASIO_FORMAT_32BIT,
                SampleFormat.SAMPLE_INT_32BIT
            },
            {
                BASSASIOFormat.BASS_ASIO_FORMAT_FLOAT,
                SampleFormat.SAMPLE_FLOAT_32BIT
            }
        });
        FromBASSWASAPIFormat = new ReadOnlyDictionary<BASSWASAPIFormat, SampleFormat>(new Dictionary<BASSWASAPIFormat, SampleFormat>
        {
            {
                BASSWASAPIFormat.BASS_WASAPI_FORMAT_UNKNOWN,
                SampleFormat.UNKNOWN
            },
            {
                BASSWASAPIFormat.BASS_WASAPI_FORMAT_FLOAT,
                SampleFormat.SAMPLE_FLOAT_32BIT
            },
            {
                BASSWASAPIFormat.BASS_WASAPI_FORMAT_8BIT,
                SampleFormat.SAMPLE_INT_8BIT
            },
            {
                BASSWASAPIFormat.BASS_WASAPI_FORMAT_16BIT,
                SampleFormat.SAMPLE_INT_16BIT
            },
            {
                BASSWASAPIFormat.BASS_WASAPI_FORMAT_24BIT,
                SampleFormat.SAMPLE_INT_24BIT
            },
            {
                BASSWASAPIFormat.BASS_WASAPI_FORMAT_32BIT,
                SampleFormat.SAMPLE_INT_32BIT
            }
        });
        FromBASSWASAPIFormatToByte = new ReadOnlyDictionary<BASSWASAPIFormat, int>(new Dictionary<BASSWASAPIFormat, int>
        {
            {
                BASSWASAPIFormat.BASS_WASAPI_FORMAT_UNKNOWN,
                0
            },
            {
                BASSWASAPIFormat.BASS_WASAPI_FORMAT_FLOAT,
                4
            },
            {
                BASSWASAPIFormat.BASS_WASAPI_FORMAT_8BIT,
                1
            },
            {
                BASSWASAPIFormat.BASS_WASAPI_FORMAT_16BIT,
                2
            },
            {
                BASSWASAPIFormat.BASS_WASAPI_FORMAT_24BIT,
                3
            },
            {
                BASSWASAPIFormat.BASS_WASAPI_FORMAT_32BIT,
                4
            }
        });
        ToBASSWASAPIFormat = new ReadOnlyDictionary<SampleFormat, BASSWASAPIFormat>(new Dictionary<SampleFormat, BASSWASAPIFormat>
        {
            {
                SampleFormat.UNKNOWN,
                BASSWASAPIFormat.BASS_WASAPI_FORMAT_UNKNOWN
            },
            {
                SampleFormat.SAMPLE_FLOAT_32BIT,
                BASSWASAPIFormat.BASS_WASAPI_FORMAT_FLOAT
            },
            {
                SampleFormat.SAMPLE_INT_8BIT,
                BASSWASAPIFormat.BASS_WASAPI_FORMAT_8BIT
            },
            {
                SampleFormat.SAMPLE_INT_16BIT,
                BASSWASAPIFormat.BASS_WASAPI_FORMAT_16BIT
            },
            {
                SampleFormat.SAMPLE_INT_24BIT,
                BASSWASAPIFormat.BASS_WASAPI_FORMAT_24BIT
            },
            {
                SampleFormat.SAMPLE_INT_32BIT,
                BASSWASAPIFormat.BASS_WASAPI_FORMAT_32BIT
            }
        });
        WasapiProc = delegate (IntPtr buffer, int length, IntPtr user)
        {
            int val = Bass.BASS_ChannelGetData(outputMixer, buffer, length);
            return System.Math.Max(0, val);
        };
        AsioProc = delegate (bool input, int channel, IntPtr buffer, int length, IntPtr user)
        {
            int val = Bass.BASS_ChannelGetData(outputMixer, buffer, length);
            return System.Math.Max(0, val);
        };
        StreamProc = delegate (int handle, IntPtr buffer, int length, IntPtr user)
        {
            int val = Bass.BASS_ChannelGetData(inputMixer, buffer, length);
            return System.Math.Max(0, val);
        };
        EndProc = delegate (int handle, int channel, int data, IntPtr user)
        {
            object obj2;
            lock (StaticLockObject)
            {
                obj2 = InstanceLocks[channel];
            }
            if (!Monitor.TryEnter(obj2))
            {
                NLogWrapper.TraceLogger?.Trace("EndProc get lock failed");
                return;
            }
            try
            {
                if (BassMix.BASS_Mixer_ChannelRemove(channel))
                {
                    lock (StaticLockObject)
                    {
                        CurrentVoices--;
                        return;
                    }
                }
                BASSError bASSError = Bass.BASS_ErrorGetCode();
                NLogWrapper.TraceLogger?.Warn(string.Concat("BASS_Mixer_ChannelRemove failed: ", bASSError, channel.ToString()));
            }
            catch
            {
                throw;
            }
            finally
            {
                Monitor.Exit(obj2);
            }
        };
        playbackRate = 1f;
#pragma warning disable CS0618
        FxParameterTypeToBASSFXType = new ReadOnlyDictionary<Type, BASSFXType>(new Dictionary<Type, BASSFXType>
        {
            {
                typeof(BASS_DX8_CHORUS),
                BASSFXType.BASS_FX_DX8_CHORUS
            },
            {
                typeof(BASS_DX8_COMPRESSOR),
                BASSFXType.BASS_FX_DX8_COMPRESSOR
            },
            {
                typeof(BASS_DX8_DISTORTION),
                BASSFXType.BASS_FX_DX8_DISTORTION
            },
            {
                typeof(BASS_DX8_ECHO),
                BASSFXType.BASS_FX_DX8_ECHO
            },
            {
                typeof(BASS_DX8_FLANGER),
                BASSFXType.BASS_FX_DX8_FLANGER
            },
            {
                typeof(BASS_DX8_GARGLE),
                BASSFXType.BASS_FX_DX8_GARGLE
            },
            {
                typeof(BASS_DX8_I3DL2REVERB),
                BASSFXType.BASS_FX_DX8_I3DL2REVERB
            },
            {
                typeof(BASS_DX8_PARAMEQ),
                BASSFXType.BASS_FX_DX8_PARAMEQ
            },
            {
                typeof(BASS_DX8_REVERB),
                BASSFXType.BASS_FX_DX8_REVERB
            },
            {
                typeof(BASS_BFX_ROTATE),
                BASSFXType.BASS_FX_BFX_ROTATE
            },
            {
                typeof(BASS_BFX_ECHO),
                BASSFXType.BASS_FX_BFX_ECHO
            },
            {
                typeof(BASS_BFX_FLANGER),
                BASSFXType.BASS_FX_BFX_FLANGER
            },
            {
                typeof(BASS_BFX_VOLUME),
                BASSFXType.BASS_FX_BFX_VOLUME
            },
            {
                typeof(BASS_BFX_PEAKEQ),
                BASSFXType.BASS_FX_BFX_PEAKEQ
            },
            {
                typeof(BASS_BFX_REVERB),
                BASSFXType.BASS_FX_BFX_REVERB
            },
            {
                typeof(BASS_BFX_LPF),
                BASSFXType.BASS_FX_BFX_LPF
            },
            {
                typeof(BASS_BFX_MIX),
                BASSFXType.BASS_FX_BFX_MIX
            },
            {
                typeof(BASS_BFX_DAMP),
                BASSFXType.BASS_FX_BFX_DAMP
            },
            {
                typeof(BASS_BFX_AUTOWAH),
                BASSFXType.BASS_FX_BFX_AUTOWAH
            },
            {
                typeof(BASS_BFX_ECHO2),
                BASSFXType.BASS_FX_BFX_ECHO2
            },
            {
                typeof(BASS_BFX_PHASER),
                BASSFXType.BASS_FX_BFX_PHASER
            },
            {
                typeof(BASS_BFX_ECHO3),
                BASSFXType.BASS_FX_BFX_ECHO3
            },
            {
                typeof(BASS_BFX_CHORUS),
                BASSFXType.BASS_FX_BFX_CHORUS
            },
            {
                typeof(BASS_BFX_APF),
                BASSFXType.BASS_FX_BFX_APF
            },
            {
                typeof(BASS_BFX_COMPRESSOR),
                BASSFXType.BASS_FX_BFX_COMPRESSOR
            },
            {
                typeof(BASS_BFX_DISTORTION),
                BASSFXType.BASS_FX_BFX_DISTORTION
            },
            {
                typeof(BASS_BFX_COMPRESSOR2),
                BASSFXType.BASS_FX_BFX_COMPRESSOR2
            },
            {
                typeof(BASS_BFX_VOLUME_ENV),
                BASSFXType.BASS_FX_BFX_VOLUME_ENV
            },
            {
                typeof(BASS_BFX_BQF),
                BASSFXType.BASS_FX_BFX_BQF
            },
            {
                typeof(BASS_BFX_ECHO4),
                BASSFXType.BASS_FX_BFX_ECHO4
            },
            {
                typeof(BASS_BFX_PITCHSHIFT),
                BASSFXType.BASS_FX_BFX_PITCHSHIFT
            },
            {
                typeof(BASS_BFX_FREEVERB),
                BASSFXType.BASS_FX_BFX_FREEVERB
            }
        });
#pragma warning restore CS0618
        FxParameters = new ConcurrentDictionary<BASSFXType, Tuple<int, object>>();
        EqualizerFrequencies = new List<float> { 32f, 64f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 16000f }.AsReadOnly();
        equalizerGains = new float[10].ToList();
        DriverType = DeviceDriver.INVALID;
        _frequency = SampleRate.AUTO;
        _format = SampleFormat.AUTO;
        _defaultVolume = 0.4f;
        _deviceVolume = 0.4f;
        Ribbit.Media.Audio.BassNet.Initialize();
        DeviceList = getDeviceList();
    }

    private static DeviceDescriptor InitializeAsio(DeviceDescriptor desc = default)
    {
        if (!Bass.BASS_Init(0, 44100, BASSInit.BASS_DEVICE_DEFAULT, IntPtr.Zero))
        {
            BASSError bASSError = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_Init failed: " + bASSError);
        }
        Bass.BASS_SetConfig(BASSConfig.BASS_CONFIG_UPDATEPERIOD, 0);
        BASS_ASIO_DEVICEINFO[] array = BassAsio.BASS_ASIO_GetDeviceInfos();
        if (array.Length == 0)
        {
            throw new Exception("ASIO device not found");
        }
        int device = 0;
        bool flag = false;
        if (!desc.Equals(default(DeviceDescriptor)))
        {
            device = array.Select((info, idx) => new { info, idx }).FirstOrDefault(s => desc.Name == s.info.name && desc.Driver == s.info.driver)?.idx ?? array.Select((info, idx) => new { info, idx }).FirstOrDefault(s => desc.Name == s.info.name)?.idx ?? 0;
        }
        else
        {
            flag = true;
        }
        BASS_ASIO_DEVICEINFO bASS_ASIO_DEVICEINFO = BassAsio.BASS_ASIO_GetDeviceInfo(device);
        desc.Name = bASS_ASIO_DEVICEINFO.name;
        desc.Driver = bASS_ASIO_DEVICEINFO.driver;
        if (!BassAsio.BASS_ASIO_Init(device, BASSASIOInit.BASS_ASIO_THREAD))
        {
            BASSError bASSError2 = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_ASIO_Init failed: " + bASSError2);
        }
        Frequency = ((Frequency == SampleRate.AUTO) ? SampleRate.SAMPLE_RATE_48000Hz : Frequency);
        bool func(SampleRate rate)
        {
            Frequency = rate;
            return BassAsio.BASS_ASIO_CheckRate((double)Frequency) && BassAsio.BASS_ASIO_SetRate((double)Frequency);
        }
        if (!func(Frequency))
        {
            Frequency = ((Frequency == SampleRate.SAMPLE_RATE_44100Hz) ? SampleRate.SAMPLE_RATE_88200Hz : Frequency);
            SampleRate frequency = Frequency;
            if (frequency <= SampleRate.SAMPLE_RATE_44100Hz)
            {
                if (frequency <= SampleRate.SAMPLE_RATE_11025Hz)
                {
                    goto IL_02ce;
                }
                else
                {
                    if (frequency == SampleRate.SAMPLE_RATE_22050Hz)
                    {
                        goto IL_02c0;
                    }
                    if (frequency == SampleRate.SAMPLE_RATE_32000Hz)
                    {
                        goto IL_02b2;
                    }
                    if (frequency == SampleRate.SAMPLE_RATE_44100Hz)
                    {
                        goto IL_02a4;
                    }
                }
                goto IL_02ce;
            }
            if (frequency <= SampleRate.SAMPLE_RATE_88200Hz)
            {
                if (frequency == SampleRate.SAMPLE_RATE_48000Hz)
                {
                    goto IL_0296;
                }
                if (frequency != SampleRate.SAMPLE_RATE_88200Hz)
                {
                    goto IL_02ce;
                }
            }
            else
            {
                if (frequency != SampleRate.SAMPLE_RATE_96000Hz)
                {
                    if (frequency != SampleRate.SAMPLE_RATE_176400Hz)
                    {
                        if (frequency != SampleRate.SAMPLE_RATE_192000Hz)
                        {
                            goto IL_02ce;
                        }
                        if (func(SampleRate.SAMPLE_RATE_176400Hz))
                        {
                            goto IL_02f2;
                        }
                    }
                    if (func(SampleRate.SAMPLE_RATE_96000Hz))
                    {
                        goto IL_02f2;
                    }
                }
                if (func(SampleRate.SAMPLE_RATE_88200Hz))
                {
                    goto IL_02f2;
                }
            }
            if (!func(SampleRate.SAMPLE_RATE_48000Hz))
            {
                goto IL_0296;
            }
        }
        goto IL_02f2;
    IL_02c0:
        if (!func(SampleRate.SAMPLE_RATE_11025Hz))
        {
            goto IL_02ce;
        }
        goto IL_02f2;
    IL_02b2:
        if (!func(SampleRate.SAMPLE_RATE_22050Hz))
        {
            goto IL_02c0;
        }
        goto IL_02f2;
    IL_0296:
        if (!func(SampleRate.SAMPLE_RATE_44100Hz))
        {
            goto IL_02a4;
        }
        goto IL_02f2;
    IL_02ce:
        Frequency = SampleRate.AUTO;
        BASSError bASSError3 = Bass.BASS_ErrorGetCode();
        throw new Exception("BASS_ASIO_SetRate failed: " + bASSError3);
    IL_02a4:
        if (!func(SampleRate.SAMPLE_RATE_32000Hz))
        {
            goto IL_02b2;
        }
        goto IL_02f2;
    IL_02f2:
        BassAsio.BASS_ASIO_GetInfo();
        double num = BassAsio.BASS_ASIO_GetRate();
        Frequency = (SampleRate)num;
        if (!BassAsio.BASS_ASIO_ChannelSetRate(input: false, 0, 0.0))
        {
            BASSError bASSError4 = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_ASIO_ChannelSetRate failed: " + bASSError4);
        }
        bool func2(BASSASIOFormat format)
        {
            Format = FromBASSASIOFormat[format];
            return BassAsio.BASS_ASIO_ChannelSetFormat(input: false, 0, format);
        }
        if (Format == SampleFormat.AUTO)
        {
            Format = FromBASSASIOFormat[BASSASIOFormat.BASS_ASIO_FORMAT_FLOAT];
        }
        switch (Format)
        {
            case SampleFormat.SAMPLE_FLOAT_32BIT:
                if (func2(BASSASIOFormat.BASS_ASIO_FORMAT_FLOAT))
                {
                    break;
                }
                goto case SampleFormat.SAMPLE_INT_32BIT;
            case SampleFormat.SAMPLE_INT_32BIT:
                if (func2(BASSASIOFormat.BASS_ASIO_FORMAT_32BIT))
                {
                    break;
                }
                goto case SampleFormat.SAMPLE_INT_8BIT;
            case SampleFormat.SAMPLE_INT_8BIT:
            case SampleFormat.SAMPLE_INT_16BIT:
                if (func2(BASSASIOFormat.BASS_ASIO_FORMAT_16BIT))
                {
                    break;
                }
                goto case SampleFormat.SAMPLE_INT_24BIT;
            case SampleFormat.SAMPLE_INT_24BIT:
                if (func2(BASSASIOFormat.BASS_ASIO_FORMAT_24BIT))
                {
                    break;
                }
                goto default;
            default:
                {
                    Format = SampleFormat.UNKNOWN;
                    BASSError bASSError5 = Bass.BASS_ErrorGetCode();
                    throw new Exception("BASS_ASIO_ChannelSetFormat failed: " + bASSError5);
                }
        }
        BassAsio.BASS_ASIO_ChannelGetRate(input: false, 0);
        BassAsio.BASS_ASIO_ChannelGetFormat(input: false, 0);
        BassAsio.BASS_ASIO_ChannelGetInfo(input: false, 0);
        if (!BassAsio.BASS_ASIO_ChannelEnable(input: false, 0, AsioProc, IntPtr.Zero))
        {
            BASSError bASSError6 = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_ASIO_ChannelEnable failed: " + bASSError6);
        }
        for (int num2 = 1; num2 < 2; num2++)
        {
            if (!BassAsio.BASS_ASIO_ChannelJoin(input: false, num2, 0))
            {
                BASSError bASSError7 = Bass.BASS_ErrorGetCode();
                throw new Exception("BASS_ASIO_ChannelJoin failed: " + bASSError7);
            }
        }
        var flags = (BASSFlag)(((Format == SampleFormat.SAMPLE_FLOAT_32BIT) ? 256 : 0) | 0x200000 | 0x20000);
        inputMixer = BassMix.BASS_Mixer_StreamCreate((int)num, 2, flags);
        if (inputMixer == 0)
        {
            BASSError bASSError8 = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_Mixer_StreamCreate failed: " + bASSError8);
        }
        outputMixer = inputMixer;
        if (latencyParam <= 0f)
        {
            latencyParam = 0f;
        }
        if (!BassAsio.BASS_ASIO_Start(System.Math.Max(0, (int)latencyParam * (int)num / 1000), 4))
        {
            BASSError bASSError9 = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_ASIO_Start failed: " + bASSError9);
        }
        Latency = (double)(BassAsio.BASS_ASIO_GetLatency(input: false) * 1000) / num;
        if (!flag)
        {
            return desc;
        }
        return default;
    }

    private static DeviceDescriptor InitializeWasapi(DeviceDescriptor desc = default, bool isSharedMode = false, params object[] param)
    {
        bool num = param != null && param.Length != 0 && param[0] is bool && (bool)param[0];
        if (!Bass.BASS_Init(0, 44100, BASSInit.BASS_DEVICE_DEFAULT, IntPtr.Zero))
        {
            BASSError bASSError = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_Init failed: " + bASSError);
        }
        Bass.BASS_SetConfig(BASSConfig.BASS_CONFIG_UPDATEPERIOD, 0);
        var array = (from i in BassWasapi.BASS_WASAPI_GetDeviceInfos().Select((info, idx) => new { info, idx })
                     where !i.info.IsUnplugged && !i.info.IsLoopback && i.info.IsEnabled && !i.info.IsInput
                     select i).ToArray();
        if (array.Length == 0)
        {
            throw new Exception("WASAPI device not found");
        }
        int num2 = 0;
        bool flag = false;
        if (!desc.Equals(default(DeviceDescriptor)))
        {
            num2 = array.FirstOrDefault(c => desc.Name == c.info.name && desc.Driver == c.info.id)?.idx ?? array.FirstOrDefault(c => desc.Name == c.info.name)?.idx ?? array.First(c => c.info.IsDefault)?.idx ?? 0;
        }
        else
        {
            flag = true;
            num2 = array.First(c => c.info.IsDefault).idx;
        }
        BASS_WASAPI_DEVICEINFO bASS_WASAPI_DEVICEINFO = BassWasapi.BASS_WASAPI_GetDeviceInfo(num2);
        desc.Name = bASS_WASAPI_DEVICEINFO.name;
        desc.Driver = bASS_WASAPI_DEVICEINFO.id;
        if (isSharedMode)
        {
            BASSWASAPIFormat bASSWASAPIFormat = BassWasapi.BASS_WASAPI_CheckFormat(num2, bASS_WASAPI_DEVICEINFO.mixfreq, bASS_WASAPI_DEVICEINFO.mixchans, BASSWASAPIInit.BASS_WASAPI_SHARED);
            if (bASSWASAPIFormat == BASSWASAPIFormat.BASS_WASAPI_FORMAT_UNKNOWN)
            {
                BASSError bASSError2 = Bass.BASS_ErrorGetCode();
                throw new Exception("BASS_WASAPI_CheckFormat failed: " + bASSError2);
            }
            Format = FromBASSWASAPIFormat[bASSWASAPIFormat];
        }
        else
        {
            BASSWASAPIFormat bASSWASAPIFormat2 = BASSWASAPIFormat.BASS_WASAPI_FORMAT_UNKNOWN;
            if (Frequency != SampleRate.AUTO)
            {
                bASSWASAPIFormat2 = BassWasapi.BASS_WASAPI_CheckFormat(num2, (int)Frequency, 2, BASSWASAPIFormat.BASS_WASAPI_FORMAT_FLOAT);
                if (bASSWASAPIFormat2 == BASSWASAPIFormat.BASS_WASAPI_FORMAT_UNKNOWN)
                {
                    Frequency = SampleRate.AUTO;
                }
            }
            if (Frequency == SampleRate.AUTO)
            {
                bASSWASAPIFormat2 = BassWasapi.BASS_WASAPI_CheckFormat(num2, bASS_WASAPI_DEVICEINFO.mixfreq, 2, BASSWASAPIFormat.BASS_WASAPI_FORMAT_FLOAT);
                if (bASSWASAPIFormat2 == BASSWASAPIFormat.BASS_WASAPI_FORMAT_UNKNOWN)
                {
                    BASSError bASSError3 = Bass.BASS_ErrorGetCode();
                    throw new Exception("BASS_WASAPI_CheckFormat failed: " + bASSError3);
                }
                Frequency = (SampleRate)bASS_WASAPI_DEVICEINFO.mixfreq;
            }
            if (Format != SampleFormat.AUTO && FromBASSWASAPIFormat[bASSWASAPIFormat2] > Format)
            {
                bASSWASAPIFormat2 = BassWasapi.BASS_WASAPI_CheckFormat(num2, (int)Frequency, 2, ToBASSWASAPIFormat[Format]);
                if (bASSWASAPIFormat2 == BASSWASAPIFormat.BASS_WASAPI_FORMAT_UNKNOWN)
                {
                    BASSError bASSError4 = Bass.BASS_ErrorGetCode();
                    throw new Exception("BASS_WASAPI_CheckFormat failed: " + bASSError4);
                }
            }
            Format = FromBASSWASAPIFormat[bASSWASAPIFormat2];
        }
        BASSWASAPIInit bASSWASAPIInit = (isSharedMode ? BASSWASAPIInit.BASS_WASAPI_AUTOFORMAT : (BASSWASAPIInit.BASS_WASAPI_EXCLUSIVE | BASSWASAPIInit.BASS_WASAPI_AUTOFORMAT));
        if (num)
        {
            bASSWASAPIInit |= BASSWASAPIInit.BASS_WASAPI_EVENT;
        }
        if (latencyParam <= 0f)
        {
            latencyParam = 16f;
        }
        if (isSharedMode ? (!BassWasapi.BASS_WASAPI_Init(num2, bASS_WASAPI_DEVICEINFO.mixfreq, bASS_WASAPI_DEVICEINFO.mixchans, bASSWASAPIInit, System.Math.Max(bASS_WASAPI_DEVICEINFO.minperiod + 0.001f, latencyParam / 1000f), bASS_WASAPI_DEVICEINFO.minperiod, WasapiProc, IntPtr.Zero)) : (!BassWasapi.BASS_WASAPI_Init(num2, (int)Frequency, 2, bASSWASAPIInit, ToBASSWASAPIFormat[Format], System.Math.Max(bASS_WASAPI_DEVICEINFO.minperiod + 0.001f, latencyParam / 1000f), bASS_WASAPI_DEVICEINFO.minperiod, WasapiProc, IntPtr.Zero)))
        {
            BASSError bASSError5 = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_WASAPI_Init failed: " + bASSError5);
        }
        BASS_WASAPI_INFO bASS_WASAPI_INFO = BassWasapi.BASS_WASAPI_GetInfo();
        Frequency = (SampleRate)bASS_WASAPI_INFO.freq;
        Format = FromBASSWASAPIFormat[bASS_WASAPI_INFO.format];
        Latency = (float)bASS_WASAPI_INFO.buflen * 1000f / (float)FromBASSWASAPIFormatToByte[bASS_WASAPI_INFO.format] / (float)Frequency / (float)bASS_WASAPI_INFO.chans;
        BASSFlag flags = BASSFlag.BASS_SAMPLE_FLOAT | BASSFlag.BASS_STREAM_PRESCAN | BASSFlag.BASS_STREAM_DECODE;
        inputMixer = BassMix.BASS_Mixer_StreamCreate((int)Frequency, bASS_WASAPI_INFO.chans, flags);
        if (inputMixer == 0)
        {
            BASSError bASSError6 = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_Mixer_StreamCreate failed: " + bASSError6);
        }
        outputMixer = inputMixer;
        if (!isSharedMode)
        {
            volumeEffect = Bass.BASS_ChannelSetFX(inputMixer, BASSFXType.BASS_FX_BFX_VOLUME, 1);
        }
        if (!BassWasapi.BASS_WASAPI_Start())
        {
            BASSError bASSError7 = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_WASAPI_Start failed: " + bASSError7);
        }
        if (!flag)
        {
            return desc;
        }
        return default;
    }

    private static DeviceDescriptor InitializeDirectSound(DeviceDescriptor desc = default)
    {
        var array = (from i in Bass.BASS_GetDeviceInfos().Select((info, idx) => new { info, idx })
                     where i.info.IsEnabled
                     select i).ToArray();
        if (array.Length == 0)
        {
            throw new Exception("DirectSound device not found");
        }
        int num = 0;
        bool flag = false;
        if (!desc.Equals(default(DeviceDescriptor)))
        {
            num = array.FirstOrDefault(c => desc.Name == c.info.name && desc.Driver == c.info.driver)?.idx ?? array.FirstOrDefault(c => desc.Name == c.info.name)?.idx ?? array.First(c => c.info.IsDefault)?.idx ?? 0;
        }
        else
        {
            flag = true;
            num = array.First(c => c.info.IsDefault).idx;
        }
        desc.Name = array[num].info.name;
        desc.Driver = array[num].info.driver;
        if (!Bass.BASS_Init(num, 44100, BASSInit.BASS_DEVICE_DEFAULT, IntPtr.Zero))
        {
            BASSError bASSError = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_Init failed: " + bASSError);
        }
        BASS_INFO bASS_INFO = Bass.BASS_GetInfo();
        if (latencyParam <= 0f)
        {
            latencyParam = 100f;
        }
        else
        {
            latencyParam += 50f;
        }
        Bass.BASS_SetConfig(BASSConfig.BASS_CONFIG_UPDATEPERIOD, 5);
        Bass.BASS_SetConfig(BASSConfig.BASS_CONFIG_BUFFER, System.Math.Max(Bass.BASS_GetConfig(BASSConfig.BASS_CONFIG_UPDATEPERIOD) + 1, (int)latencyParam));
        Frequency = (SampleRate)bASS_INFO.freq;
        Format = SampleFormat.SAMPLE_INT_16BIT;
        Latency = Bass.BASS_GetConfig(BASSConfig.BASS_CONFIG_BUFFER);
        BASSFlag flags = BASSFlag.BASS_SAMPLE_FLOAT | BASSFlag.BASS_STREAM_PRESCAN | BASSFlag.BASS_STREAM_DECODE;
        inputMixer = BassMix.BASS_Mixer_StreamCreate((int)Frequency, 2, flags);
        if (inputMixer == 0)
        {
            BASSError bASSError2 = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_Mixer_StreamCreate failed: " + bASSError2);
        }
        outputMixer = (procChannel = Bass.BASS_StreamCreate((int)Frequency, 2, BASSFlag.BASS_SAMPLE_FLOAT, StreamProc, IntPtr.Zero));
        if (!Bass.BASS_ChannelPlay(outputMixer, restart: false))
        {
            BASSError bASSError3 = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_ChannelPlay failed: " + bASSError3);
        }
        if (!flag)
        {
            return desc;
        }
        return default;
    }

    private static void InitializeNullDevice(DeviceDescriptor desc = default)
    {
        if (!Bass.BASS_Init(0, 44100, BASSInit.BASS_DEVICE_DEFAULT, IntPtr.Zero))
        {
            BASSError bASSError = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_Init failed: " + bASSError);
        }
        Frequency = ((Frequency == SampleRate.AUTO) ? SampleRate.SAMPLE_RATE_44100Hz : Frequency);
        Format = ((Format == SampleFormat.AUTO) ? SampleFormat.SAMPLE_INT_16BIT : Format);
        Latency = 0.0;
        BASSFlag flags = BASSFlag.BASS_SAMPLE_FLOAT | BASSFlag.BASS_STREAM_PRESCAN | BASSFlag.BASS_STREAM_DECODE;
        inputMixer = BassMix.BASS_Mixer_StreamCreate((int)Frequency, 2, flags);
        if (inputMixer == 0)
        {
            BASSError bASSError2 = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_Mixer_StreamCreate failed: " + bASSError2);
        }
        volumeEffect = Bass.BASS_ChannelSetFX(inputMixer, BASSFXType.BASS_FX_BFX_VOLUME, 1);
        outputMixer = inputMixer;
    }

    public static void SetTempoChange(float speed, bool changeFreq = false)
    {
        if (!IsInitialized)
        {
            return;
        }
        if ((double)speed < 0.05 || speed > 50f)
        {
            throw new ArgumentOutOfRangeException("speed");
        }
        if (speed == 1f)
        {
            ResetTempoChange();
            return;
        }
        if (tempoChanger == 0)
        {
            BASSFlag flags = BASSFlag.BASS_STREAM_DECODE;
            switch (DriverType)
            {
                case DeviceDriver.NULL_DEVICE:
                case DeviceDriver.WASAPI_SHARED:
                case DeviceDriver.WASAPI_EXCLUSIVE:
                case DeviceDriver.ASIO:
                    outputMixer = (tempoChanger = BassFx.BASS_FX_TempoCreate(inputMixer, flags));
                    if (outputMixer == 0)
                    {
                        BASSError bASSError4 = Bass.BASS_ErrorGetCode();
                        throw new Exception("BASS_FX_TempoCreate failed: " + bASSError4);
                    }
                    break;
                case DeviceDriver.DIRECT_SOUND:
                    flags = BASSFlag.BASS_DEFAULT;
                    if (!Bass.BASS_StreamFree(procChannel))
                    {
                        BASSError bASSError = Bass.BASS_ErrorGetCode();
                        NLogWrapper.TraceLogger?.Warn("BASS_StreamFree for procChannel failed: " + bASSError);
                    }
                    procChannel = 0;
                    outputMixer = (tempoChanger = BassFx.BASS_FX_TempoCreate(inputMixer, flags));
                    if (tempoChanger == 0)
                    {
                        BASSError bASSError2 = Bass.BASS_ErrorGetCode();
                        throw new Exception("BASS_FX_TempoCreate failed: " + bASSError2);
                    }
                    if (!Bass.BASS_ChannelPlay(outputMixer, restart: false))
                    {
                        BASSError bASSError3 = Bass.BASS_ErrorGetCode();
                        throw new Exception("BASS_ChannelPlay failed: " + bASSError3);
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            Bass.BASS_ChannelSetAttribute(tempoChanger, BASSAttribute.BASS_ATTRIB_TEMPO_OPTION_SEQUENCE_MS, 33f);
            Bass.BASS_ChannelSetAttribute(tempoChanger, BASSAttribute.BASS_ATTRIB_TEMPO_OPTION_SEEKWINDOW_MS, 10f);
        }
        if (changeFreq)
        {
            float value = -1f;
            Bass.BASS_ChannelGetAttribute(inputMixer, BASSAttribute.BASS_ATTRIB_FREQ, ref value);
            Bass.BASS_ChannelSetAttribute(tempoChanger, BASSAttribute.BASS_ATTRIB_TEMPO_FREQ, speed * value);
        }
        else
        {
            Bass.BASS_ChannelSetAttribute(tempoChanger, BASSAttribute.BASS_ATTRIB_TEMPO, 100f * (speed - 1f));
        }
        playbackRate = speed;
    }

    public static void ResetTempoChange()
    {
        if (!IsInitialized || tempoChanger == 0)
        {
            return;
        }
        if (!Bass.BASS_StreamFree(tempoChanger))
        {
            BASSError bASSError = Bass.BASS_ErrorGetCode();
            NLogWrapper.TraceLogger?.Warn("BASS_StreamFree for ResetTempoChanger failed: " + bASSError);
        }
        tempoChanger = 0;
        switch (DriverType)
        {
            case DeviceDriver.NULL_DEVICE:
            case DeviceDriver.WASAPI_SHARED:
            case DeviceDriver.WASAPI_EXCLUSIVE:
            case DeviceDriver.ASIO:
                outputMixer = inputMixer;
                break;
            case DeviceDriver.DIRECT_SOUND:
                outputMixer = (procChannel = Bass.BASS_StreamCreate((int)Frequency, 2, BASSFlag.BASS_SAMPLE_FLOAT, StreamProc, IntPtr.Zero));
                if (!Bass.BASS_ChannelPlay(outputMixer, restart: false))
                {
                    BASSError bASSError2 = Bass.BASS_ErrorGetCode();
                    throw new Exception("BASS_ChannelPlay failed: " + bASSError2);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        playbackRate = 1f;
    }

    public static void CreateFX(object parameter)
    {
        if (!IsInitialized)
        {
            return;
        }
        if (parameter == null)
        {
            throw new ArgumentNullException("parameter");
        }
        if (!FxParameterTypeToBASSFXType.TryGetValue(parameter.GetType(), out BASSFXType value))
        {
            return;
        }
        if (FxParameters.TryGetValue(value, out Tuple<int, object> value2) && value2.Item1 != 0)
        {
            FxParameters[value] = new Tuple<int, object>(value2.Item1, parameter);
            if (!Bass.BASS_FXSetParameters(value2.Item1, parameter))
            {
                BASSError bASSError = Bass.BASS_ErrorGetCode();
                NLogWrapper.TraceLogger?.Warn("BASS_FXSetParameters failed: " + bASSError);
            }
        }
        else
        {
            value2 = new Tuple<int, object>(0, parameter);
            FxParameters[value] = value2;
        }
    }

    public static void RemoveFX(BASSFXType fxType)
    {
        if (IsInitialized && FxParameters.TryRemove(fxType, out Tuple<int, object> value) && value.Item1 != 0 && !Bass.BASS_ChannelRemoveFX(inputMixer, value.Item1))
        {
            BASSError bASSError = Bass.BASS_ErrorGetCode();
            NLogWrapper.TraceLogger?.Warn("BASS_ChannelRemoveFX failed: " + bASSError);
        }
    }

    public static void RemoveFX()
    {
        if (IsInitialized)
        {
            KeyValuePair<BASSFXType, Tuple<int, object>>[] array = [.. FxParameters];
            foreach (KeyValuePair<BASSFXType, Tuple<int, object>> keyValuePair in array)
            {
                RemoveFX(keyValuePair.Key);
            }
        }
    }

    public static void DisableFX(BASSFXType fxType)
    {
        if (IsInitialized && FxParameters.TryGetValue(fxType, out Tuple<int, object> value) && value.Item1 != 0)
        {
            if (!Bass.BASS_ChannelRemoveFX(inputMixer, value.Item1))
            {
                BASSError bASSError = Bass.BASS_ErrorGetCode();
                NLogWrapper.TraceLogger?.Warn("BASS_ChannelRemoveFX failed: " + bASSError);
            }
            else
            {
                value = new Tuple<int, object>(0, value.Item2);
                FxParameters[fxType] = value;
            }
        }
    }

    public static void DisableFX()
    {
        if (IsInitialized)
        {
            KeyValuePair<BASSFXType, Tuple<int, object>>[] array = [.. FxParameters];
            foreach (KeyValuePair<BASSFXType, Tuple<int, object>> keyValuePair in array)
            {
                DisableFX(keyValuePair.Key);
            }
        }
    }

    public static void EnableFX(BASSFXType fxType)
    {
        if (IsInitialized && FxParameters.TryGetValue(fxType, out Tuple<int, object> value) && value.Item1 == 0)
        {
            value = new Tuple<int, object>(Bass.BASS_ChannelSetFX(inputMixer, fxType, 0), value.Item2);
            if (value.Item1 == 0)
            {
                BASSError bASSError = Bass.BASS_ErrorGetCode();
                NLogWrapper.TraceLogger?.Warn("BASS_ChannelSetFX failed: " + bASSError);
            }
            else
            {
                FxParameters[fxType] = value;
                Bass.BASS_FXSetParameters(value.Item1, value.Item2);
            }
        }
    }

    public static void EnableFX()
    {
        if (IsInitialized)
        {
            KeyValuePair<BASSFXType, Tuple<int, object>>[] array = [.. FxParameters];
            foreach (KeyValuePair<BASSFXType, Tuple<int, object>> keyValuePair in array)
            {
                EnableFX(keyValuePair.Key);
            }
        }
    }

    public static bool FXCreated(BASSFXType fxType)
    {
        if (FxParameters.TryGetValue(fxType, out Tuple<int, object> _))
        {
            return true;
        }
        return false;
    }

    public static bool FXEnabled(BASSFXType fxType)
    {
        if (FxParameters.TryGetValue(fxType, out Tuple<int, object> value) && value.Item1 != 0)
        {
            return true;
        }
        return false;
    }

    public static void EnableEQ()
    {
        if (!IsInitialized || EQEnabled)
        {
            return;
        }
        equalizer = Bass.BASS_ChannelSetFX(inputMixer, BASSFXType.BASS_FX_BFX_PEAKEQ, 0);
        var bASS_BFX_PEAKEQ = new BASS_BFX_PEAKEQ
        {
            fQ = 0f,
            fBandwidth = 2.5f,
            lChannel = BASSFXChan.BASS_BFX_CHANALL
        };
        for (int i = 0; i < EqualizerFrequencies.Count; i++)
        {
            bASS_BFX_PEAKEQ.lBand = i;
            bASS_BFX_PEAKEQ.fCenter = EqualizerFrequencies[i];
            bASS_BFX_PEAKEQ.fGain = equalizerGains[i];
            if (!Bass.BASS_FXSetParameters(equalizer, bASS_BFX_PEAKEQ))
            {
                BASSError bASSError = Bass.BASS_ErrorGetCode();
                NLogWrapper.TraceLogger?.Warn("BASS_FXSetParameters (EQ) failed: " + bASSError);
            }
        }
    }

    public static void DisableEQ()
    {
        if (IsInitialized && EQEnabled)
        {
            if (!Bass.BASS_ChannelRemoveFX(inputMixer, equalizer))
            {
                BASSError bASSError = Bass.BASS_ErrorGetCode();
                NLogWrapper.TraceLogger?.Warn("BASS_ChannelRemoveFX (EQ) failed: " + bASSError);
            }
            else
            {
                equalizer = 0;
            }
        }
    }

    public static void UpdateEQ(int slot, float gain)
    {
        if (slot < 0 || slot > EqualizerFrequencies.Count)
        {
            throw new ArgumentOutOfRangeException("slot");
        }
        if (EQEnabled)
        {
            var bASS_BFX_PEAKEQ = new BASS_BFX_PEAKEQ
            {
                lBand = slot
            };
            if (!Bass.BASS_FXGetParameters(equalizer, bASS_BFX_PEAKEQ))
            {
                BASSError bASSError = Bass.BASS_ErrorGetCode();
                NLogWrapper.TraceLogger?.Warn("BASS_FXGetParameters (EQ) failed: " + bASSError);
                return;
            }
            bASS_BFX_PEAKEQ.fGain = gain;
            if (!Bass.BASS_FXSetParameters(equalizer, bASS_BFX_PEAKEQ))
            {
                BASSError bASSError2 = Bass.BASS_ErrorGetCode();
                NLogWrapper.TraceLogger?.Warn("BASS_FXSetParameters (EQ) failed: " + bASSError2);
                return;
            }
        }
        equalizerGains[slot] = gain;
    }

    public static void ResetEQ()
    {
        if (IsInitialized)
        {
            for (int i = 0; i < EqualizerFrequencies.Count; i++)
            {
                UpdateEQ(i, 0f);
            }
        }
    }

    private static ReadOnlyDictionary<DeviceDriver, ReadOnlyCollection<DeviceDescriptor>> getDeviceList()
    {
        if (IsInitialized)
        {
            return DeviceList;
        }
        Dictionary<DeviceDriver, ReadOnlyCollection<DeviceDescriptor>> dictionary = [];
        if (!Bass.BASS_Init(0, 44100, BASSInit.BASS_DEVICE_DEFAULT, IntPtr.Zero))
        {
            BASSError bASSError = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_Init failed: " + bASSError);
        }
        IEnumerable<DeviceDescriptor> second = from i in BassWasapi.BASS_WASAPI_GetDeviceInfos()
                                               where !i.IsUnplugged && !i.IsLoopback && i.IsEnabled && !i.IsInput
                                               select new DeviceDescriptor(i.name, i.id);
        ReadOnlyCollection<DeviceDescriptor> value = (dictionary[DeviceDriver.WASAPI_SHARED] = new DeviceDescriptor[1].Concat(second).ToList().AsReadOnly());
        dictionary[DeviceDriver.WASAPI_EXCLUSIVE] = value;
        IEnumerable<DeviceDescriptor> second2 = from i in BassAsio.BASS_ASIO_GetDeviceInfos()
                                                select new DeviceDescriptor(i.name, i.driver);
        dictionary[DeviceDriver.ASIO] = new DeviceDescriptor[1].Concat(second2).ToList().AsReadOnly();
        IEnumerable<DeviceDescriptor> second3 = from i in Bass.BASS_GetDeviceInfos()
                                                where i.driver != null && i.IsEnabled
                                                select new DeviceDescriptor(i.name, i.driver);
        dictionary[DeviceDriver.DIRECT_SOUND] = new DeviceDescriptor[1].Concat(second3).ToList().AsReadOnly();
        Ribbit.Media.Audio.BassNet.Free();
        return new ReadOnlyDictionary<DeviceDriver, ReadOnlyCollection<DeviceDescriptor>>(dictionary);
    }

    public static void ClearMaxVoices()
    {
        MaxVoices = 0;
    }

    private static void SetDeviceMasterVolume(float vol)
    {
        if (!_isInitialized)
        {
            return;
        }
        switch (DriverType)
        {
            case DeviceDriver.NULL_DEVICE:
                if (!Bass.BASS_FXSetParameters(volumeEffect, new BASS_BFX_VOLUME(vol)))
                {
                    BASSError bASSError5 = Bass.BASS_ErrorGetCode();
                    NLogWrapper.TraceLogger?.Warn("BASS_FXSetParameters failed: " + bASSError5);
                }
                break;
            case DeviceDriver.DIRECT_SOUND:
                if (!Bass.BASS_ChannelSetAttribute(inputMixer, BASSAttribute.BASS_ATTRIB_VOL, vol))
                {
                    BASSError bASSError4 = Bass.BASS_ErrorGetCode();
                    NLogWrapper.TraceLogger?.Warn("BASS_ChannelSetAttribute failed: " + bASSError4);
                }
                break;
            case DeviceDriver.WASAPI_EXCLUSIVE:
                if (!Bass.BASS_FXSetParameters(volumeEffect, new BASS_BFX_VOLUME(vol)))
                {
                    BASSError bASSError2 = Bass.BASS_ErrorGetCode();
                    NLogWrapper.TraceLogger?.Warn("BASS_FXSetParameters failed: " + bASSError2);
                }
                break;
            case DeviceDriver.WASAPI_SHARED:
                if (!BassWasapi.BASS_WASAPI_SetVolume((BASSWASAPIVolume)10, vol))
                {
                    BASSError bASSError3 = Bass.BASS_ErrorGetCode();
                    NLogWrapper.TraceLogger?.Warn("BASS_WASAPI_SetVolume failed: " + bASSError3);
                }
                break;
            case DeviceDriver.ASIO:
                {
                    for (int i = 0; i < 2; i++)
                    {
                        if (!BassAsio.BASS_ASIO_ChannelSetVolume(input: false, i, vol))
                        {
                            BASSError bASSError = Bass.BASS_ErrorGetCode();
                            NLogWrapper.TraceLogger?.Warn("BASS_ASIO_ChannelSetVolume failed: " + bASSError);
                        }
                    }
                    break;
                }
            case DeviceDriver.INVALID:
                break;
        }
    }

    public BassAudioPlayer(string fileName, bool onMemory = true)
    {
        if (!IsInitialized)
        {
            throw new InvalidOperationException("BassAudioPlayer is not initialized.");
        }
        if (fileName == null)
        {
            throw new ArgumentNullException("fileName");
        }
        if (!LongPathFileSystem.FileExists(fileName))
        {
            throw new FileNotFoundException(fileName);
        }
        FileName = fileName;
        fileNameHash = xxHash32.CalculateHash(fileName.ToUpperInvariant());
        lock (Locks.GetLockObject(fileNameHash))
        {
            if (OnMemoryFileCache.ContainsKey(fileNameHash))
            {
                lock (StaticLockObject)
                {
                    CachedData cachedData = OnMemoryFileCache[fileNameHash];
                    _sampleBuffer = cachedData.Data;
                    cachedData.RefCount++;
                }
            }
            else if (onMemory)
            {
                string text = Path.GetExtension(fileName).ToLowerInvariant();
                if (text == ".ogg")
                {
                    try
                    {
                        using FileStream fileStream = LongPathFileSystem.OpenRead(fileName);
                        using var stream = new OggDecodeStream(fileStream);
                        long num = stream.Length;
                        int sampleRate = stream.SamplesPerSecond;
                        int channelCount = stream.Channels;
                        using (new MemoryFailPoint(1 + (int)num / 1024 / 1024))
                        {
                        }
                        _sampleBuffer = new byte[num + 44];
                        stream.Read(_sampleBuffer, 44, _sampleBuffer.Length);
                        using var targetStream = new MemoryStream(_sampleBuffer, 0, 44);
                        WavFile.WriteHeader(targetStream, _sampleBuffer.Length - 44, channelCount, sampleRate);
                    }
                    catch (Exception ex)
                    {
                        NLogWrapper.GetLogger()?.Warn("Ogg Decode failed: " + ex);
                        _sampleBuffer = null;
                        goto end_IL_0060;
                    }
                }
                else
                {
                    _sampleBuffer = LongPathFileSystem.ReadAllBytes(fileName);
                }
                lock (StaticLockObject)
                {
                    OnMemoryFileCache[fileNameHash] = new CachedData(_sampleBuffer);
                }
            }
        end_IL_0060:;
        }
        if (_sampleBuffer != null)
        {
            fileProc = new BASS_FILEPROCS(FileProcClose, FileProcLength, FileProcRead, fileProcSeek);
            _handle = Bass.BASS_StreamCreateFileUser(BASSStreamSystem.STREAMFILE_NOBUFFER, BASSFlag.BASS_SAMPLE_FLOAT | BASSFlag.BASS_STREAM_PRESCAN | BASSFlag.BASS_STREAM_DECODE, fileProc, IntPtr.Zero);
            if (_handle == 0)
            {
                BASSError bASSError = Bass.BASS_ErrorGetCode();
                throw new Exception("BASS_StreamCreateFileUser failed: " + fileName + " " + bASSError);
            }
        }
        else
        {
            _handle = Bass.BASS_StreamCreateFile(LongPathFileSystem.ToExtendedPath(fileName), 0L, 0L, BASSFlag.BASS_SAMPLE_FLOAT | BASSFlag.BASS_STREAM_PRESCAN | BASSFlag.BASS_STREAM_DECODE);
            if (_handle == 0)
            {
                BASSError bASSError2 = Bass.BASS_ErrorGetCode();
                throw new Exception("BASS_StreamCreateFile failed: " + fileName + " " + bASSError2);
            }
        }
        long pos = Bass.BASS_ChannelGetLength(_handle);
        double value = Bass.BASS_ChannelBytes2Seconds(_handle, pos);
        Duration = TimeSpan.FromSeconds(value);
        Volume = DefaultVolume;
        Bass.BASS_ChannelSetSync(_handle, BASSSync.BASS_SYNC_END | BASSSync.BASS_SYNC_MIXTIME, 0L, EndProc, IntPtr.Zero);
        playState = PlayState.Stopped;
        lock (StaticLockObject)
        {
            InstanceLocks[_handle] = new object();
        }
    }

    private void FileProcClose(IntPtr user)
    {
    }

    private long FileProcLength(IntPtr user)
    {
        return (_sampleBuffer != null) ? _sampleBuffer.Length : 0;
    }

    private int FileProcRead(IntPtr buffer, int length, IntPtr user)
    {
        if (_sampleBuffer == null)
        {
            return -1;
        }
        if (_sampleBufferPos >= _sampleBuffer.Length)
        {
            return -1;
        }
        try
        {
            int num = System.Math.Min(length, _sampleBuffer.Length - _sampleBufferPos);
            Marshal.Copy(_sampleBuffer, _sampleBufferPos, buffer, num);
            _sampleBufferPos += num;
            return num;
        }
        catch
        {
            return -1;
        }
    }

    private bool fileProcSeek(long offset, IntPtr user)
    {
        if (_sampleBuffer == null)
        {
            return false;
        }
        if (offset >= _sampleBuffer.Length)
        {
            return false;
        }
        _sampleBufferPos = (int)offset;
        return true;
    }

    public void Pause()
    {
        if (playState != PlayState.Stopped)
        {
            if (playState == PlayState.Playing)
            {
                playState = PlayState.Paused;
                BassMix.BASS_Mixer_ChannelPause(_handle);
            }
            else if (playState == PlayState.Paused)
            {
                playState = PlayState.Playing;
                BassMix.BASS_Mixer_ChannelPlay(_handle);
            }
        }
    }

    public void Play(PlayWith flagPlayWith = PlayWith.RESTART)
    {
        IsMuted = flagPlayWith.HasFlag(PlayWith.MUTE);
        bool flag = false;
        lock (InstanceLocks[_handle])
        {
            BASSActive bASSActive;
            while (true)
            {
                bASSActive = BassMix.BASS_Mixer_ChannelIsActive(_handle);
                if (bASSActive == BASSActive.BASS_ACTIVE_STOPPED)
                {
                    if (BassMix.BASS_Mixer_StreamAddChannel(inputMixer, _handle, BASSFlag.BASS_STREAM_PRESCAN))
                    {
                        lock (StaticLockObject)
                        {
                            CurrentVoices++;
                            if (MaxVoices < CurrentVoices)
                            {
                                MaxVoices = CurrentVoices;
                            }
                        }
                        flag = true;
                    }
                    else
                    {
                        BASSError bASSError = Bass.BASS_ErrorGetCode();
                        NLogWrapper.TraceLogger?.Warn(string.Concat("BASS_Mixer_StreamAddChannel failed: ", bASSError, " :", FileName));
                    }
                    bASSActive = BASSActive.BASS_ACTIVE_PAUSED;
                }
                if (playState == PlayState.Playing && flagPlayWith.HasFlag(PlayWith.RESTART))
                {
                    CurrentTime = TimeSpan.Zero;
                }
                if (flagPlayWith.HasFlag(PlayWith.PAUSE))
                {
                    break;
                }
                if (!BassMix.BASS_Mixer_ChannelPlay(_handle))
                {
                    BASSError bASSError2 = Bass.BASS_ErrorGetCode();
                    NLogWrapper.TraceLogger?.Warn(string.Concat("BASS_Mixer_ChannelPlay failed: ", bASSError2, " :", FileName, flag ? " add channel failed?" : " channel is removed?"));
                    if (flag)
                    {
                        lock (StaticLockObject)
                        {
                            CurrentVoices--;
                            return;
                        }
                    }
                    continue;
                }
                playState = PlayState.Playing;
                return;
            }
            if (bASSActive == BASSActive.BASS_ACTIVE_PLAYING)
            {
                BassMix.BASS_Mixer_ChannelPause(_handle);
            }
            playState = PlayState.Paused;
        }
    }

    public void Stop()
    {
        if (playState == PlayState.Stopped)
        {
            return;
        }
        playState = PlayState.Stopped;
        CurrentTime = TimeSpan.Zero;
        lock (InstanceLocks[_handle])
        {
            if (BassMix.BASS_Mixer_ChannelIsActive(_handle) == BASSActive.BASS_ACTIVE_STOPPED)
            {
                return;
            }
            if (BassMix.BASS_Mixer_ChannelRemove(_handle))
            {
                lock (StaticLockObject)
                {
                    CurrentVoices--;
                    return;
                }
            }
            BASSError bASSError = Bass.BASS_ErrorGetCode();
            NLogWrapper.TraceLogger?.Warn("BASS_Mixer_ChannelRemove failed: " + bASSError);
        }
    }

    private void Dispose(bool disposing)
    {
        if (disposedValue)
        {
            return;
        }
        if (_handle != 0)
        {
            Stop();
            Bass.BASS_StreamFree(_handle);
            _handle = 0;
        }
        if (_sampleBuffer != null)
        {
            _sampleBuffer = null;
            lock (StaticLockObject)
            {
                if (OnMemoryFileCache.ContainsKey(fileNameHash))
                {
                    CachedData cachedData = OnMemoryFileCache[fileNameHash];
                    cachedData.RefCount--;
                    if (cachedData.RefCount == 0)
                    {
                        OnMemoryFileCache.Remove(fileNameHash);
                    }
                }
            }
        }
        lock (StaticLockObject)
        {
            InstanceLocks.Remove(_handle);
        }
        disposedValue = true;
    }

    ~BassAudioPlayer()
    {
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
