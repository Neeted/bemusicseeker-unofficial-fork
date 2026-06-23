using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using BeMusicSeeker.Models.Utils;
using Ribbit.Threading;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.LR2;

public class LR2Config : XDocument
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
            throw new FileNotFoundException("ファイルが見つからないか、config.xml ではありません。", value);
        }
    }

    public string LR2RootPath { get; private set; }

    public LR2Config(string configPath)
        : base(LoadConfigDocument(configPath))
    {
        ConfigPath = configPath;
    }

    private static XDocument LoadConfigDocument(string configPath)
    {
        using FileStream stream = LongPathFileSystem.OpenRead(configPath);
        return XDocument.Load(stream);
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
        XElement config = Element("config") ?? throw new InvalidOperationException("LR2 config.xml の config セクションが見つかりません。");
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

    public List<string> GetBMSSearchDirectoriesForChangeTracking()
    {
        using (new ReaderGuard(rwlock))
        {
            IEnumerable<string> source = Element("config").Element("jukebox").Elements("path")
                .Select(dirs => dirs.Value.TrimEnd('\\'));
            if (!string.IsNullOrWhiteSpace(LR2RootPath))
            {
                source = source.Select(NormalizeBmsSearchDirectoryForChangeTracking);
            }
            return [.. source
                .Where(dir => !string.IsNullOrWhiteSpace(dir))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }
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
            .Select(d => NormalizeBmsSearchDirectoryForChangeTracking(d.TrimEnd('\\')))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> list = [.. dirs.Where(d => !d.IsSjisSchemeString())];
        if (list.Count() > 0)
        {
            throw new ArgumentException("Shift_JISで表現できない文字がディレクトリパスに含まれています。" + Environment.NewLine + string.Join(Environment.NewLine, list));
        }
        using (new WriterGuard(rwlock))
        {
            RemoveBMSSearchDirectories();
            Element("config").Element("jukebox").Add(dirs.Select(d => new XElement("path")
            {
                Value = d.TrimEnd('\\') + "\\"
            }));
        }
    }

    public void AddBMSSearchDirectories(IEnumerable<string> dirs)
    {
        dirs ??= [];
        dirs = [.. dirs.Where(d => LongPathFileSystem.DirectoryExists(d)).Select(d => d.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (dirs.Any(d => !LongPathFileSystem.DirectoryExists(d)))
        {
            throw new ArgumentException("指定されたディレクトリの一部または全てが存在しません。");
        }
        List<string> list = [.. dirs.Where(d => !d.IsSjisSchemeString())];
        if (list.Count() > 0)
        {
            throw new ArgumentException("Shift_JISで表現できない文字がディレクトリパスに含まれています。" + Environment.NewLine + string.Join(Environment.NewLine, list));
        }
        List<string> dirsInXML = GetBMSSearchDirectoriesForChangeTracking();
        using (new WriterGuard(rwlock))
        {
            if (dirs.Any(dnew => dirsInXML.Any(dold => IsSameOrNestedDirectory(dnew, dold)))
                || dirs.Any(dnew => dirs.Any(dother => !dnew.Equals(dother, StringComparison.OrdinalIgnoreCase) && IsSameOrNestedDirectory(dnew, dother))))
            {
                throw new ArgumentException("登録済みディレクトリまたはその親・子ディレクトリは追加できません。");
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
                Element("config").Element("jukebox").RemoveAll();
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
            throw new ArgumentOutOfRangeException("x", "引数は0より大きい必要が有ります");
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
            throw new ArgumentOutOfRangeException("y", "引数は0より大きい必要が有ります");
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
            throw new ArgumentOutOfRangeException("v", "引数は0より大きい必要が有ります");
        }
        if (v > 100)
        {
            throw new ArgumentOutOfRangeException("v", "引数は100以下である必要が有ります");
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

    public void Save()
    {
        using (new WriterGuard(rwlock))
        {
            Save(ConfigPath, SaveOptions.None);
        }
    }
}
