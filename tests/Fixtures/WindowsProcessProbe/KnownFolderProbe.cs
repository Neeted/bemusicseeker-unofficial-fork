using System.Runtime.InteropServices;

namespace BeMusicSeeker.WindowsProcessProbe;

/// <summary>通常起動と同じ子環境で既知フォルダとprobeの実行runtime版を読み取ります。</summary>
internal static class KnownFolderProbe
{
    /// <summary>設定・registryを書き換えず、専有LocalApplicationDataの解決結果と実行版だけを返します。</summary>
    internal static int Run()
    {
        Program.WriteResult(new
        {
            LocalApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            RuntimeVersion = Environment.Version.ToString(),
            RuntimeDescription = RuntimeInformation.FrameworkDescription
        });
        return 0;
    }
}
