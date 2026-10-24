using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using BeMusicSeeker.Models.Utils;
using Ribbit.Threading;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.LR2;

/// <summary>初回入力と操作内のXML編集文書です。採用後の全文と実保存はLr2ConfigOwnerが所有します。</summary>
internal sealed class LR2Config : XDocument
{
    public const int DatabaseAutoReloadManualOnly = 0;

    private readonly ReaderWriterLockSlim rwlock = new();

    private string _configPath;

    private string ConfigPath
    {
        get
        {
            return _configPath;
        }
        set
        {
            if (_configPath == value)
            {
                return;
            }
            if (LongPathFileSystem.FileExists(value) && (string.Equals(Path.GetFileName(value), "config.xml", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(value), "config.xmh", StringComparison.OrdinalIgnoreCase)))
            {
                _configPath = value;
                string directoryName = Path.GetDirectoryName(_configPath);
                if (string.Equals(Path.GetFileName(directoryName), "Config", StringComparison.OrdinalIgnoreCase))
                {
                    string directoryName2 = Path.GetDirectoryName(directoryName);
                    if (string.Equals(Path.GetFileName(directoryName2), "LR2files", StringComparison.OrdinalIgnoreCase))
                    {
                        LR2RootPath = Path.GetDirectoryName(directoryName2);
                    }
                }
                return;
            }
            throw new FileNotFoundException(BeMusicSeeker.Properties.Resources.Error_InvalidLR2ConfigFile, value);
        }
    }

    public string LR2RootPath { get; private set; }

    /// <summary>
    /// このインスタンスが保存する config.xml の正規化済み path です。
    /// </summary>
    internal string ConfigFilePath => LongPathFileSystem.NormalizePathForStorage(ConfigPath);

    /// <summary>
    /// 指定された設定XMLを読み込み、検索ルートを取得できる構造を確認します。
    /// </summary>
    /// <param name="configPath">config.xml または config.xmh のパスです。</param>
    /// <exception cref="XmlException">XMLが不正、または config/jukebox 要素がありません。</exception>
    public LR2Config(string configPath)
        : base(LoadConfigDocument(configPath))
    {
        ConfigPath = configPath;
    }

    private static XDocument LoadConfigDocument(string configPath)
    {
        string normalizedConfigPath = LongPathFileSystem.NormalizePathForStorage(configPath);
        using FileStream stream = LongPathFileSystem.OpenRead(normalizedConfigPath);
        var document = XDocument.Load(stream);
        if (document.Element("config")?.Element("jukebox") == null)
        {
            throw new XmlException(BeMusicSeeker.Properties.Resources.Error_InvalidLR2ConfigStructure);
        }
        return document;
    }

    /// <summary>採用済み全文から、ファイルを読み直さず操作用の独立した文書を作ります。</summary>
    internal LR2Config(LR2Config source) : base(source)
    {
        _configPath = source._configPath;
        LR2RootPath = source.LR2RootPath;
    }

    private LR2Config(string path, XDocument document) : base(document)
    {
        _configPath = path;
        string configDirectory = Path.GetDirectoryName(path);
        if (string.Equals(Path.GetFileName(configDirectory), "Config", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(Path.GetDirectoryName(configDirectory)), "LR2files", StringComparison.OrdinalIgnoreCase))
        { LR2RootPath = Path.GetDirectoryName(Path.GetDirectoryName(configDirectory)); }
    }

    /// <summary>全文を共有せず、既存の登録計画サービスが操作する一時入力を作ります。読込み・保存は行いません。</summary>
    internal static LR2Config CreateRootPlanningDocument(string path, IEnumerable<string> roots)
    {
        LR2Config plan = new(path, new XDocument(new XElement("config", new XElement("jukebox"))));
        plan.SetBMSSearchDirectories(roots);
        return plan;
    }

    public int GetCustomFolderMask()
    {
        return GetSystemIntValue("customfolder", 0);
    }

    public int GetTitleFlashHours()
    {
        return GetSystemIntValue("titleflash", 24);
    }

    public int GetDatabaseAutoReloadMode()
    {
        return GetSystemIntValue("autoreload", DatabaseAutoReloadManualOnly);
    }

    public bool EnsureDatabaseAutoReloadManualOnly()
    {
        using (new WriterGuard(rwlock))
        {
            XElement system = GetOrCreateSystemElement();
            XElement autoreload = system.Element("autoreload");
            string manualOnlyValue = DatabaseAutoReloadManualOnly.ToString();
            if (autoreload == null)
            {
                system.Add(new XElement("autoreload", manualOnlyValue));
                return true;
            }
            if (autoreload.Value == manualOnlyValue)
            {
                return false;
            }
            autoreload.SetValue(manualOnlyValue);
            return true;
        }
    }

    private int GetSystemIntValue(string name, int defaultValue)
    {
        using (new ReaderGuard(rwlock))
        {
            string value = Element("config")?.Element("system")?.Element(name)?.Value;
            return int.TryParse(value, out int parsed)
                ? parsed
                : defaultValue;
        }
    }

    private XElement GetOrCreateSystemElement()
    {
        XElement config = Element("config") ?? throw new InvalidOperationException(BeMusicSeeker.Properties.Resources.Error_LR2ConfigSectionMissing);
        XElement system = config.Element("system");
        if (system == null)
        {
            system = new XElement("system");
            config.AddFirst(system);
        }
        return system;
    }

    public List<string> GetBMSSearchDirectories()
    {
        return GetBMSSearchDirectoriesReadOnly();
    }

    public List<string> GetBMSSearchDirectoriesReadOnly()
    {
        return ReadBMSSearchDirectories().list;
    }

    /// <summary>
    /// 設定 XML の登録 root を存在確認なしで取得します。
    /// 相対 path は LR2 root 基準へ解決し、drive root の意味を保持します。
    /// </summary>
    public List<string> GetBMSSearchDirectoriesForChangeTracking()
    {
        using (new ReaderGuard(rwlock))
        {
            IEnumerable<string> source = Element("config").Element("jukebox").Elements("path")
                .Select(dirs => TrimConfiguredDirectorySeparators(dirs.Value));
            if (!string.IsNullOrWhiteSpace(LR2RootPath))
            {
                source = source.Select(NormalizeBmsSearchDirectoryForChangeTracking);
            }
            return [.. source
                .Where(dir => !string.IsNullOrWhiteSpace(dir))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }
    }

    private static string TrimConfiguredDirectorySeparators(string path)
    {
        string trimmed = path?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        string root = Path.GetPathRoot(trimmed);
        string withoutTrailingSeparators = trimmed.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (!string.IsNullOrWhiteSpace(root)
            && string.Equals(
                withoutTrailingSeparators,
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }
        return withoutTrailingSeparators;
    }

    private string NormalizeBmsSearchDirectoryForChangeTracking(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return path;
        }
        try
        {
            return Path.Combine(LR2RootPath, path);
        }
        catch
        {
            return path;
        }
    }

    /// <summary>操作要求の登録を同じLR2ルート基準へ正規化します。存在確認・読込み・保存は行いません。</summary>
    internal string NormalizeRequestedSearchDirectory(string path)
        => NormalizeBmsSearchDirectoryForChangeTracking(TrimConfiguredDirectorySeparators(path));

    private string NormalizeBmsSearchDirectoryForMatching(string path)
    {
        return NormalizeBmsSearchDirectoryForChangeTracking(path?.TrimEnd('\\'));
    }

    private (List<string> source, List<string> list, bool needSave) ReadBMSSearchDirectories()
    {
        bool needSave = false;
        List<string> source;
        List<string> list;
        using (new ReaderGuard(rwlock))
        {
            source = [.. (from dirs in Element("config").Element("jukebox").Elements("path")
                      select dirs.Value.TrimEnd('\\'))];
            list = (string.IsNullOrWhiteSpace(LR2RootPath) ? source.Where(dir => LongPathFileSystem.DirectoryExists(dir) && dir.IsSjisSchemeString()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : [.. (from dir in source.Select(delegate (string d)
                {
                    try
                    {
                        if (!Path.IsPathRooted(d))
                        {
                            d = Path.Combine(LR2RootPath, d);
                            needSave = true;
                        }
                    }
                    catch
                    {
                        d = string.Empty;
                        needSave = true;
                    }
                    return d;
                })
                                                                                                                                                                                                    where LongPathFileSystem.DirectoryExists(dir) && dir.IsSjisSchemeString()
                                                                                                                                                                                                    select dir).Distinct(StringComparer.OrdinalIgnoreCase)]);
            List<string> list2 = [];
            foreach (string p in list.OrderBy(f => f.Length))
            {
                if (!list2.Any(pp => p.StartsWith(pp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    list2.Add(p);
                }
            }
            list = list2;
        }
        return (source, list, needSave);
    }

    public void SetBMSSearchDirectories(IEnumerable<string> dirs)
    {
        dirs ??= [];
        dirs = [.. dirs
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => NormalizeBmsSearchDirectoryForChangeTracking(TrimConfiguredDirectorySeparators(d)))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> list = [.. dirs.Where(d => !d.IsSjisSchemeString())];
        if (list.Count() > 0)
        {
            throw new ArgumentException(string.Format(BeMusicSeeker.Properties.Resources.Error_DirectoryPathsNotShiftJisFormat, string.Join(Environment.NewLine, list)));
        }
        using (new WriterGuard(rwlock))
        {
            XElement jukebox = Element("config").Element("jukebox");
            var requested = new HashSet<string>(dirs, StringComparer.OrdinalIgnoreCase);
            var retained = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement path in jukebox.Elements("path").ToArray())
            {
                string normalized = NormalizeBmsSearchDirectoryForChangeTracking(TrimConfiguredDirectorySeparators(path.Value));
                if (!requested.Contains(normalized) || !retained.TryAdd(normalized, path)) { path.Remove(); }
            }
            // pathの位置だけを最終一覧に揃え、未知の兄弟・属性と継続pathの付随情報を保持します。
            XElement[] desired = dirs.Select(directory => retained.TryGetValue(directory, out XElement path)
                ? new XElement(path) : new XElement("path", directory.TrimEnd('\\') + "\\")).ToArray();
            XElement[] slots = jukebox.Elements("path").ToArray();
            XElement last = null;
            for (int index = 0; index < desired.Length; index++)
            {
                if (index < slots.Length)
                {
                    if (XNode.DeepEquals(slots[index], desired[index])) { last = slots[index]; }
                    else { slots[index].ReplaceWith(desired[index]); last = desired[index]; }
                }
                else
                {
                    if (last == null) { jukebox.Add(desired[index]); }
                    else { last.AddAfterSelf(desired[index]); }
                    last = desired[index];
                }
            }
        }
    }

    public void AddBMSSearchDirectories(IEnumerable<string> dirs)
    {
        dirs ??= [];
        dirs = [.. dirs.Where(d => LongPathFileSystem.DirectoryExists(d)).Select(d => d.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (dirs.Any(d => !LongPathFileSystem.DirectoryExists(d)))
        {
            throw new ArgumentException(BeMusicSeeker.Properties.Resources.Error_BmsSearchDirectoriesMissing);
        }
        List<string> list = [.. dirs.Where(d => !d.IsSjisSchemeString())];
        if (list.Count() > 0)
        {
            throw new ArgumentException(string.Format(BeMusicSeeker.Properties.Resources.Error_DirectoryPathsNotShiftJisFormat, string.Join(Environment.NewLine, list)));
        }
        List<string> dirsInXML = GetBMSSearchDirectoriesForChangeTracking();
        using (new WriterGuard(rwlock))
        {
            if (dirs.Any(dnew => dirsInXML.Any(dold => IsSameOrNestedDirectory(dnew, dold)))
                || dirs.Any(dnew => dirs.Any(dother => !dnew.Equals(dother, StringComparison.OrdinalIgnoreCase) && IsSameOrNestedDirectory(dnew, dother))))
            {
                throw new ArgumentException(BeMusicSeeker.Properties.Resources.Error_BmsSearchDirectoriesOverlap);
            }
            Element("config").Element("jukebox").Add(dirs.Select(d => new XElement("path")
            {
                Value = d.TrimEnd('\\') + "\\"
            }));
        }
    }

    private static bool IsSameOrNestedDirectory(string left, string right)
    {
        return (left + "\\").StartsWith(right + "\\", StringComparison.OrdinalIgnoreCase)
            || (right + "\\").StartsWith(left + "\\", StringComparison.OrdinalIgnoreCase)
            || (left + "\\").Equals(right + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public bool RemoveBMSSearchDirectories(IEnumerable<string> dirs = null)
    {
        bool result = false;
        using (rwlock.IsWriteLockHeld ? null : new WriterGuard(rwlock))
        {
            if (dirs == null)
            {
                Element("config").Element("jukebox").Elements("path").Remove();
                return false;
            }
            foreach (string dir in dirs)
            {
                string normalizedTarget = NormalizeBmsSearchDirectoryForMatching(dir);
                List<XElement> targets = [.. (from dirInXml in Element("config").Element("jukebox").Elements("path")
                                          where string.Equals(NormalizeBmsSearchDirectoryForMatching(dirInXml.Value), normalizedTarget, StringComparison.OrdinalIgnoreCase)
                                          select dirInXml)];
                if (targets.Count > 0)
                {
                    targets.Remove();
                    result = true;
                }
            }
            return result;
        }
    }

    public int GetWindowSizeX()
    {
        using (new ReaderGuard(rwlock))
        {
            int num;
            try
            {
                num = int.Parse(Element("config").Element("system").Element("windowsize_x").Value);
                if (num <= 0)
                {
                    num = 640;
                }
            }
            catch
            {
                num = 640;
            }
            return num;
        }
    }

    public int GetWindowSizeY()
    {
        using (new ReaderGuard(rwlock))
        {
            int num;
            try
            {
                num = int.Parse(Element("config").Element("system").Element("windowsize_y").Value);
                if (num <= 0)
                {
                    num = 480;
                }
            }
            catch
            {
                num = 480;
            }
            return num;
        }
    }

    public void SetWindowSizeX(int x)
    {
        if (x <= 0)
        {
            throw new ArgumentOutOfRangeException("x", BeMusicSeeker.Properties.Resources.Error_PositiveValueRequired);
        }
        using (new WriterGuard(rwlock))
        {
            Element("config").Element("system").Element("windowsize_x").SetValue(x.ToString());
        }
    }

    public void SetWindowSizeY(int y)
    {
        if (y <= 0)
        {
            throw new ArgumentOutOfRangeException("y", BeMusicSeeker.Properties.Resources.Error_PositiveValueRequired);
        }
        using (new WriterGuard(rwlock))
        {
            Element("config").Element("system").Element("windowsize_y").SetValue(y.ToString());
        }
    }

    public bool IsScreenModeWindow()
    {
        using (new ReaderGuard(rwlock))
        {
            try
            {
                return (int.Parse(Element("config").Element("system").Element("screenmode").Value) != 0);
            }
            catch
            {
                return true;
            }
        }
    }

    public void SetScreenMode(bool isWinMode)
    {
        using (new WriterGuard(rwlock))
        {
            Element("config").Element("system").Element("screenmode").SetValue(isWinMode ? "1" : "0");
        }
    }

    public int GetMasterVolume()
    {
        using (new ReaderGuard(rwlock))
        {
            int num;
            try
            {
                num = int.Parse(Element("config").Element("sound").Element("volumemaster").Value);
                if (num < 0 || num > 100)
                {
                    num = 100;
                }
            }
            catch
            {
                num = 100;
            }
            return num;
        }
    }

    public void SetMasterVolume(int v)
    {
        if (v < 0)
        {
            throw new ArgumentOutOfRangeException("v", BeMusicSeeker.Properties.Resources.Error_NonNegativeValueRequired);
        }
        if (v > 100)
        {
            throw new ArgumentOutOfRangeException("v", string.Format(BeMusicSeeker.Properties.Resources.Error_ValueMustNotExceedFormat, 100));
        }
        using (new WriterGuard(rwlock))
        {
            Element("config").Element("sound").Element("volumemaster").SetValue(v.ToString());
        }
    }

    public bool IsVolumeEnabled()
    {
        using (new ReaderGuard(rwlock))
        {
            try
            {
                return (int.Parse(Element("config").Element("sound").Element("volumeflag").Value) != 0);
            }
            catch
            {
                return true;
            }
        }
    }

    public void SetVolumeFlag(bool isEnabled)
    {
        using (new WriterGuard(rwlock))
        {
            Element("config").Element("sound").Element("volumeflag").SetValue(isEnabled ? "1" : "0");
        }
    }

    public string GetPlayerId()
    {
        try
        {
            using (new ReaderGuard(rwlock))
            {
                return Element("config").Element("player").Element("id").Value;
            }
        }
        catch
        {
            return null;
        }
    }

}
