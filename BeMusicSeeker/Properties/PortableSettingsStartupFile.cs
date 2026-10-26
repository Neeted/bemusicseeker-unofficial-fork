using System;
using System.Globalization;
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

    /// <summary>入力を検証し、破損XMLをGregorianのUTC保全名へ退避してから、新規作成前に警告します。</summary>
    /// <param name="migrateMissing">設定が存在しない場合だけ呼ぶ旧設定の移入処理です。</param>
    /// <param name="warnRecovery">保全先と初期化を、新規作成・設定値取得の前に知らせます。</param>
    internal void Prepare(Action migrateMissing, Action<string> warnRecovery)
    {
        XDocument document;
        try
        {
            document = PortableSettingsProvider.ReadDocument(path);
        }
        catch (PortableSettingsException exception) when (exception.InnerException is XmlException)
        {
            string backup = path + ".broken-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
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
