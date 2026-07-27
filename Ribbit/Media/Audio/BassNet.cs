using System;
using System.Linq;
using Un4seen.Bass;

namespace Ribbit.Media.Audio;

public static class BassNet
{
    private static bool _isInitialized;

    public static void Initialize()
    {
        if (_isInitialized)
        {
            throw new InvalidOperationException("BassNet is already initialized or used.");
        }
        try
        {
            BassNativeRuntime.Load();
            BassNativeRuntime.ValidateSupportedVersions();
            ((Action<Action<string, string>, string, string>)delegate (Action<string, string> f, string i, string j)
            {
                f(new string([.. (from n in "d7cbxdba4x22b9xd7daxdf35xd7cexdf3fxd7caxdc65xd7d8xdc43xd7d2xdc78x2689xd7dcxdbbcxd7d8xdca8xd7d6xdbddxd7dcxdddexd7d0xded6xd7d9xdf53xd7d0".Split('x')
                              select Convert.ToInt32(n, 16)).Zip(i.ToCharArray(), (w, v) => v - w).Select(Convert.ToChar)]), new string([.. (from n in "d80axdf58xd80axdc26xd80bxddc5xd80axdf2fxd80axdf79xd80bxdf2dxd80axdfbcxd80bxddcc".Split('x')
                              select Convert.ToInt32(n, 16)).Zip(j.ToCharArray(), (w, v) => v - w).Select(Convert.ToChar)]));
            })(Un4seen.Bass.BassNet.Registration, "\ud83d\udc0d⌛\ud83c\udfa4\ud83c\udfa4\ud83d\udcd9\ud83d\udca4\ud83d\udce8⛵\ud83d\udc1f\ud83d\udce8\ud83d\udc4a\ud83d\ude47\ud83c\udf04\ud83c\udfc2\ud83d\udc4a\ud83c\udf74\ud83d\udce8\ud83d\udc63\ud83d\udc11\ud83c\udf68\ud83d\udc4a⌛\ud83c\udfc2\ud83d\udc0d\ud83c\udf74\ud83d\udcd9\ud83c\udf68", "\ud83c\udfb0\ud83d\udc5d\ud83d\uddfe\ud83c\udf62\ud83c\udfb0\ud83c\udf62\ud83c\udfee\ud83d\uddfe\ud83c\udfb0\ud83c\udf62\ud83d\ude93\ud83c\udf70\ud83c\udfb0\ud83d\udc70\ud83d\ude0f\ud83c\udfb0");
            _isInitialized = true;
        }
        catch
        {
            BassNativeRuntime.Free();
            throw;
        }
    }

    internal static void FreeDevice()
    {
        if (!Bass.BASS_Free())
        {
            BASSError bASSError = Bass.BASS_ErrorGetCode();
            throw new Exception("BASS_Free failed: " + bASSError);
        }
    }

    internal static void Shutdown()
    {
        if (_isInitialized)
        {
            if (!Bass.BASS_Free())
            {
                BASSError error = Bass.BASS_ErrorGetCode();
                if (error != BASSError.BASS_ERROR_INIT)
                {
                    throw new Exception("BASS_Free failed: " + error);
                }
            }
        }

        BassNativeRuntime.Free();
        _isInitialized = false;
    }
}
