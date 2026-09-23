using System;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Views.Dialogs;

/// <summary>Formats settings failures consistently at the existing UI terminals.</summary>
internal static class SettingsFailureMessage
{
    /// <summary>Identifies a failed file and distinguishes a partial save from an incomplete apply.</summary>
    internal static string Format(Exception exception)
    {
        return exception switch
        {
            PartialSettingsSaveException partial => string.Format(Resources.SettingsPartiallySaved, partial.FilePath, partial.InnerException.Message),
            PortableSettingsException portable => string.Format(Resources.SettingsSaveFailed, portable.FilePath, portable.InnerException.Message),
            _ => string.Format(Resources.SettingsApplyIncomplete, exception.Message)
        };
    }
}
