using System;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace BeMusicSeeker.Properties;

/// <summary>Prepares the startup settings file before any getter, preserving syntax-corrupt input before reset.</summary>
internal sealed class PortableSettingsStartupFile
{
    private readonly string path;

    /// <summary>Captures the application-owned path; callers must hold single-instance ownership.</summary>
    internal PortableSettingsStartupFile(string path)
    {
        this.path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
    }

    /// <summary>Validates input, optionally imports missing settings, and warns after quarantine but before fresh creation.</summary>
    /// <param name="migrateMissing">Legacy import, invoked only when the portable input is absent.</param>
    /// <param name="warnRecovery">Reports the preserved backup and default reset before creation or materialization.</param>
    internal void Prepare(Action migrateMissing, Action<string> warnRecovery)
    {
        XDocument document;
        try
        {
            document = PortableSettingsProvider.ReadDocument(path);
        }
        catch (PortableSettingsException exception) when (exception.InnerException is XmlException)
        {
            string backup = path + ".broken-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ") + "-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Move(path, backup);
            }
            catch (Exception cause)
            {
                throw new PortableSettingsException(path, "Quarantine", cause);
            }
            warnRecovery(backup);
            PortableSettingsProvider.SaveDocument(PortableSettingsProvider.CreateEmptyDocument(), path, "Create");
            return;
        }

        if (document == null)
        {
            migrateMissing();
            document = PortableSettingsProvider.ReadDocument(path);
            if (document == null)
            {
                PortableSettingsProvider.SaveDocument(PortableSettingsProvider.CreateEmptyDocument(), path, "Create");
                return;
            }
        }

        PortableSettingsProvider.NormalizePortableConfig(path);
    }
}
