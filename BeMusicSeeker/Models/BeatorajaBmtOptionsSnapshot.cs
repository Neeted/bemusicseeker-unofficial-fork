using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

/// <summary>
/// beatoraja BMT export workflow が参照する設定値を保持します。
/// </summary>
internal sealed class BeatorajaBmtOptionsSnapshot
{
    public bool EnableBeatorajaBmtOutput { get; init; }

    public bool KeepBeatorajaBmtFilesWhenOutputDisabled { get; init; }

    public string BeatorajaRootPath { get; init; }

    public string BeatorajaBmtTablePath { get; init; }

    public bool RegisterBeatorajaBmtUrls { get; init; }

    public string BeatorajaBmtHashOutputMode { get; init; }

    internal static BeatorajaBmtOptionsSnapshot CreateCurrent()
    {
        return new BeatorajaBmtOptionsSnapshot
        {
            EnableBeatorajaBmtOutput = Settings.Default.EnableBeatorajaBmtOutput,
            KeepBeatorajaBmtFilesWhenOutputDisabled = Settings.Default.KeepBeatorajaBmtFilesWhenOutputDisabled,
            BeatorajaRootPath = Settings.Default.BeatorajaRootPath,
            BeatorajaBmtTablePath = Settings.Default.BeatorajaBmtTablePath,
            RegisterBeatorajaBmtUrls = Settings.Default.RegisterBeatorajaBmtUrls,
            BeatorajaBmtHashOutputMode = Settings.Default.BeatorajaBmtHashOutputMode
        };
    }
}
