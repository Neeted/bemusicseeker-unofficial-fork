using System;
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
        return CreateCurrent(SettingsEditSession.CreateDefault().Values);
    }

    internal static BeatorajaBmtOptionsSnapshot CreateCurrent(Settings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        return new BeatorajaBmtOptionsSnapshot
        {
            EnableBeatorajaBmtOutput = settings.EnableBeatorajaBmtOutput,
            KeepBeatorajaBmtFilesWhenOutputDisabled = settings.KeepBeatorajaBmtFilesWhenOutputDisabled,
            BeatorajaRootPath = settings.BeatorajaRootPath,
            BeatorajaBmtTablePath = settings.BeatorajaBmtTablePath,
            RegisterBeatorajaBmtUrls = settings.RegisterBeatorajaBmtUrls,
            BeatorajaBmtHashOutputMode = settings.BeatorajaBmtHashOutputMode
        };
    }
}
