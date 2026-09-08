using System;
using System.Collections.Generic;
using System.Globalization;
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

    private static readonly object saveLock = new();

    private static readonly Dictionary<string, PreviewScope> activePreviewScopes = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// このインスタンスが保存する config.xml の正規化済み path です。
    /// </summary>
    internal string ConfigFilePath => LongPathFileSystem.NormalizePathForStorage(ConfigPath);

    public LR2Config(string configPath)
        : base(LoadConfigDocument(configPath))
    {
        ConfigPath = configPath;
    }

    private static XDocument LoadConfigDocument(string configPath)
    {
        string normalizedConfigPath = LongPathFileSystem.NormalizePathForStorage(configPath);
        lock (saveLock)
        {
            XDocument document = LoadConfigDocumentWithoutPreviewScope(normalizedConfigPath);
            if (activePreviewScopes.TryGetValue(normalizedConfigPath, out PreviewScope scope))
            {
                RestorePreviewFields(document, scope);
            }
            return document;
        }
    }

    private static XDocument LoadConfigDocumentWithoutPreviewScope(string configPath)
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

    /// <summary>
    /// 指定された BMS 検索 root を削除し、変更を config.xml へ保存します。
    /// 保存に失敗した場合は XML の変更を元に戻します。
    /// </summary>
    /// <param name="dirs">削除する検索 root の列挙です。</param>
    /// <returns>一つ以上の root を削除して保存した場合は <see langword="true"/> です。</returns>
    internal bool RemoveBMSSearchDirectoriesAndSave(IEnumerable<string> dirs = null)
    {
        using (new WriterGuard(rwlock))
        {
            XDocument snapshot = new(this);
            bool removed = RemoveBMSSearchDirectories(dirs);
            if (!removed)
            {
                return false;
            }

            try
            {
                SaveAtomically();
                return true;
            }
            catch
            {
                XDocument restored = new(snapshot);
                RemoveNodes();
                Add(restored.Nodes());
                throw;
            }
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

    /// <summary>
    /// 現在の設定 XML を staging file へ書き込み、完了後に config.xml へ公開します。
    /// </summary>
    public void Save()
    {
        using (new WriterGuard(rwlock))
        {
            SaveAtomically();
        }
    }

    private void SaveAtomically()
    {
        string normalizedConfigPath = ConfigFilePath;
        lock (saveLock)
        {
            XDocument document = new(this);
            if (activePreviewScopes.TryGetValue(normalizedConfigPath, out PreviewScope scope))
            {
                RestorePreviewFields(document, scope);
            }
            AtomicFileWriter.Write(
                normalizedConfigPath,
                stagingStream => document.Save(stagingStream, SaveOptions.None));
            if (activePreviewScopes.TryGetValue(normalizedConfigPath, out scope))
            {
                scope.UpdateFromDocument(document);
            }
        }
    }

    /// <summary>
    /// 試聴開始時点の保存済み LR2 設定を scope として登録します。
    /// </summary>
    /// <returns>試聴期間中に使う設定 scope です。</returns>
    internal PreviewScope BeginPreview()
    {
        string normalizedConfigPath = ConfigFilePath;
        lock (saveLock)
        {
            if (activePreviewScopes.ContainsKey(normalizedConfigPath))
            {
                throw new InvalidOperationException("LR2 config is already used by an active preview.");
            }

            XDocument document = LoadConfigDocumentWithoutPreviewScope(normalizedConfigPath);
            PreviewScope scope = new(
                normalizedConfigPath,
                CapturePreviewField(document, "system", "windowsize_x"),
                CapturePreviewField(document, "system", "windowsize_y"),
                CapturePreviewField(document, "system", "screenmode"),
                CapturePreviewField(document, "sound", "volumemaster"),
                CapturePreviewField(document, "sound", "volumeflag"));
            activePreviewScopes.Add(normalizedConfigPath, scope);
            return scope;
        }
    }

    /// <summary>
    /// 試聴用の5項目だけを最新の config.xml へ一時公開します。
    /// </summary>
    /// <param name="scope">開始時に登録した試聴 scope です。</param>
    /// <param name="windowSizeX">試聴中の横幅です。</param>
    /// <param name="windowSizeY">試聴中の縦幅です。</param>
    /// <param name="isWindowMode">試聴中に使う window mode です。</param>
    /// <param name="masterVolume">試聴中に使う master volume です。</param>
    /// <param name="isVolumeEnabled">試聴中に volume を有効にするかです。</param>
    internal void PublishPreview(
        PreviewScope scope,
        int windowSizeX,
        int windowSizeY,
        bool isWindowMode,
        int masterVolume,
        bool isVolumeEnabled)
    {
        ValidatePreviewScope(scope);
        lock (saveLock)
        {
            EnsureActivePreviewScope(scope);
            XDocument document = LoadConfigDocumentWithoutPreviewScope(scope.ConfigPath);
            SetPreviewField(document, "system", "windowsize_x", windowSizeX.ToString(CultureInfo.InvariantCulture));
            SetPreviewField(document, "system", "windowsize_y", windowSizeY.ToString(CultureInfo.InvariantCulture));
            SetPreviewField(document, "system", "screenmode", isWindowMode ? "1" : "0");
            SetPreviewField(document, "sound", "volumemaster", masterVolume.ToString(CultureInfo.InvariantCulture));
            SetPreviewField(document, "sound", "volumeflag", isVolumeEnabled ? "1" : "0");
            PublishDocument(scope.ConfigPath, document);
        }
    }

    /// <summary>
    /// 試聴 scope が保持する保存済み5項目を最新の config.xml へ復元します。
    /// </summary>
    /// <param name="scope">開始時に登録した試聴 scope です。</param>
    internal void RestorePreview(PreviewScope scope)
    {
        ValidatePreviewScope(scope);
        lock (saveLock)
        {
            EnsureActivePreviewScope(scope);
            XDocument document = LoadConfigDocumentWithoutPreviewScope(scope.ConfigPath);
            RestorePreviewFields(document, scope);
            PublishDocument(scope.ConfigPath, document);
        }
    }

    /// <summary>
    /// 試聴終了時に active scope の登録を解除します。
    /// </summary>
    /// <param name="scope">終了する試聴 scope です。</param>
    internal void EndPreview(PreviewScope scope)
    {
        ValidatePreviewScope(scope);
        lock (saveLock)
        {
            if (activePreviewScopes.TryGetValue(scope.ConfigPath, out PreviewScope activeScope)
                && ReferenceEquals(activeScope, scope))
            {
                activePreviewScopes.Remove(scope.ConfigPath);
            }
            scope.IsEnded = true;
        }
    }

    private static void PublishDocument(string configPath, XDocument document)
    {
        AtomicFileWriter.Write(
            configPath,
            stagingStream => document.Save(stagingStream, SaveOptions.None));
    }

    private void ValidatePreviewScope(PreviewScope scope)
    {
        if (scope == null)
        {
            throw new ArgumentNullException(nameof(scope));
        }
        if (!string.Equals(scope.ConfigPath, ConfigFilePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("試聴 scope と config.xml の path が一致しません。", nameof(scope));
        }
    }

    private static void EnsureActivePreviewScope(PreviewScope scope)
    {
        if (scope.IsEnded
            || !activePreviewScopes.TryGetValue(scope.ConfigPath, out PreviewScope activeScope)
            || !ReferenceEquals(activeScope, scope))
        {
            throw new InvalidOperationException("LR2 preview scope is no longer active.");
        }
    }

    private static PreviewField CapturePreviewField(XDocument document, string sectionName, string fieldName)
    {
        XElement section = document.Element("config")?.Element(sectionName);
        XElement field = section?.Element(fieldName);
        return new PreviewField(section != null, field?.Value, field != null);
    }

    private static void SetPreviewField(XDocument document, string sectionName, string fieldName, string value)
    {
        XElement config = document.Element("config")
            ?? throw new InvalidOperationException("LR2 config.xml の config セクションが見つかりません。");
        XElement section = config.Element(sectionName);
        if (section == null)
        {
            section = new XElement(sectionName);
            config.Add(section);
        }
        XElement field = section.Element(fieldName);
        if (field == null)
        {
            section.Add(new XElement(fieldName, value));
        }
        else
        {
            field.SetValue(value);
        }
    }

    private static void RestorePreviewFields(XDocument document, PreviewScope scope)
    {
        RestorePreviewField(document, "system", "windowsize_x", scope.WindowSizeX);
        RestorePreviewField(document, "system", "windowsize_y", scope.WindowSizeY);
        RestorePreviewField(document, "system", "screenmode", scope.ScreenMode);
        RestorePreviewField(document, "sound", "volumemaster", scope.MasterVolume);
        RestorePreviewField(document, "sound", "volumeflag", scope.VolumeEnabled);
    }

    private static void RestorePreviewField(
        XDocument document,
        string sectionName,
        string fieldName,
        PreviewField savedField)
    {
        XElement config = document.Element("config");
        if (config == null)
        {
            return;
        }

        XElement section = config.Element(sectionName);
        if (savedField.Exists)
        {
            if (section == null)
            {
                section = new XElement(sectionName);
                config.Add(section);
            }
            XElement field = section.Element(fieldName);
            if (field == null)
            {
                section.Add(new XElement(fieldName, savedField.Value));
            }
            else
            {
                field.SetValue(savedField.Value);
            }
            return;
        }

        section?.Element(fieldName)?.Remove();
        if (!savedField.SectionExists
            && section != null
            && !section.HasAttributes
            && !section.Nodes().Any())
        {
            section.Remove();
        }
    }

    /// <summary>
    /// 試聴中に保全する5項目と対象 config.xml の path を保持します。
    /// </summary>
    internal sealed class PreviewScope
    {
        /// <summary>
        /// 指定した config.xml と、その時点で保存されている試聴対象項目から scope を作成します。
        /// </summary>
        /// <param name="configPath">正規化済み config.xml の絶対 path。</param>
        /// <param name="windowSizeX">保存されている windowsize_x の状態。</param>
        /// <param name="windowSizeY">保存されている windowsize_y の状態。</param>
        /// <param name="screenMode">保存されている screenmode の状態。</param>
        /// <param name="masterVolume">保存されている volumemaster の状態。</param>
        /// <param name="volumeEnabled">保存されている volumeflag の状態。</param>
        internal PreviewScope(
            string configPath,
            PreviewField windowSizeX,
            PreviewField windowSizeY,
            PreviewField screenMode,
            PreviewField masterVolume,
            PreviewField volumeEnabled)
        {
            ConfigPath = configPath;
            WindowSizeX = windowSizeX;
            WindowSizeY = windowSizeY;
            ScreenMode = screenMode;
            MasterVolume = masterVolume;
            VolumeEnabled = volumeEnabled;
        }

        /// <summary>
        /// この scope が対象とする正規化済み config.xml の path です。
        /// </summary>
        internal string ConfigPath { get; }

        /// <summary>
        /// 保存されている windowsize_x の状態です。
        /// </summary>
        internal PreviewField WindowSizeX { get; private set; }

        /// <summary>
        /// 保存されている windowsize_y の状態です。
        /// </summary>
        internal PreviewField WindowSizeY { get; private set; }

        /// <summary>
        /// 保存されている screenmode の状態です。
        /// </summary>
        internal PreviewField ScreenMode { get; private set; }

        /// <summary>
        /// 保存されている volumemaster の状態です。
        /// </summary>
        internal PreviewField MasterVolume { get; private set; }

        /// <summary>
        /// 保存されている volumeflag の状態です。
        /// </summary>
        internal PreviewField VolumeEnabled { get; private set; }

        /// <summary>
        /// scope が終了済みなら <see langword="true"/> です。
        /// </summary>
        internal bool IsEnded { get; set; }

        /// <summary>
        /// 正常に公開された保存文書から、scope が保全する5項目を更新します。
        /// </summary>
        /// <param name="document">公開済みの保存文書。</param>
        internal void UpdateFromDocument(XDocument document)
        {
            WindowSizeX = CapturePreviewField(document, "system", "windowsize_x");
            WindowSizeY = CapturePreviewField(document, "system", "windowsize_y");
            ScreenMode = CapturePreviewField(document, "system", "screenmode");
            MasterVolume = CapturePreviewField(document, "sound", "volumemaster");
            VolumeEnabled = CapturePreviewField(document, "sound", "volumeflag");
        }
    }

    /// <summary>
    /// XML section と要素の存在、および保存値を表します。
    /// </summary>
    internal readonly struct PreviewField
    {
        /// <summary>
        /// XML section と要素の状態から保全値を作成します。
        /// </summary>
        /// <param name="sectionExists">対象 XML section が存在する場合は <see langword="true"/>。</param>
        /// <param name="value">対象要素の保存値。要素がない場合は <see langword="null"/>。</param>
        /// <param name="exists">対象 XML 要素が存在する場合は <see langword="true"/>。</param>
        internal PreviewField(bool sectionExists, string value, bool exists)
        {
            SectionExists = sectionExists;
            Value = value;
            Exists = exists;
        }

        /// <summary>
        /// 対象 XML section が保存時点で存在したかどうかです。
        /// </summary>
        internal bool SectionExists { get; }

        /// <summary>
        /// 対象 XML 要素の保存値です。
        /// </summary>
        internal string Value { get; }

        /// <summary>
        /// 対象 XML 要素が保存時点で存在したかどうかです。
        /// </summary>
        internal bool Exists { get; }
    }
}
