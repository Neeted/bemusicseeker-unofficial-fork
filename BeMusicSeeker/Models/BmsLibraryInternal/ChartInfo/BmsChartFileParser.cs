using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>捕捉済みの入力からBMS基本値と同じ不変リソース結果を生成します。保存行の生成や再読取りを行いません。</summary>
internal static class BmsChartFileParser
{
    private static readonly Regex visibleObjectChRegex = new("^[\\s\u3000]*#[0-9]{3}(?:[12][1-9A-Z]|[56][1-9A-Z])(?:[\\s\u3000]*:[\\s\u3000]*|[\\s\u3000]+)(?:[\\s\u3000]*00)*[\\s\u3000]*(?!00)[0-9A-Z]{2}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Encoding sjisEnc = Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback());

    /// <summary>捕捉済みの内容・ハッシュ・時刻を使って共通の基本値を返します。</summary>
    internal static ChartFile ParseSnapshot(ChartFileSnapshot snapshot, string codepageName = "shift_jis")
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var stream = new MemoryStream(snapshot.Bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.GetEncoding(codepageName), detectEncodingFromByteOrderMarks: !IsShiftJis(codepageName));
        ChartFile chart = ParseLines(ReadLines(reader), snapshot.Path, () => snapshot.Md5, () => snapshot.Sha256);
        if (HasCp932DecodeUnsupportedResourceValue(snapshot.Bytes, out ChartResourceKind kind))
        {
            chart = chart with
            {
                Resources = chart.Resources.Add(new ChartResourceReference(kind, ChartResourceUsage.InputDiagnostic,
                    string.Empty, string.Empty, string.Empty, ChartResourcePathNormalizationStatus.Cp932DecodeUnsupported))
            };
        }
        return chart with
        {
            LastWriteTimeUtc = snapshot.LastWriteTimeUtc,
            Warnings = Lr2CompatibilityWarningProjection.BuildWarnings(new ResourceHealthMaintenanceSnapshot
            { Lr2WarningFlags = (int)Lr2CompatibilityEvaluator.EvaluateChartPath(chart.Path).WarningFlags })
        };
    }

    /// <summary>文字コード判定を表示の生メタデータだけへ適用し、LR2のCP932リソース結果をそのまま共有します。</summary>
    internal static ChartFile ParseSnapshot(ChartFileSnapshot snapshot, BmsEncodingDetectionResult detection)
    {
        ArgumentNullException.ThrowIfNull(detection);
        return ApplyEncodingDetection(snapshot, ParseSnapshot(snapshot), detection);
    }

    /// <summary>既に捕捉した基本値へ文字コード判定のメタデータだけを反映し、資源を再解析しません。</summary>
    internal static ChartFile ApplyEncodingDetection(ChartFileSnapshot snapshot, ChartFile chart, BmsEncodingDetectionResult detection)
    {
        if (!ShouldApplyDetectedMetadataEncoding(detection.EncodingName))
        {
            return chart with { EncodingName = detection.EncodingName };
        }
        string text = detection.DecodedText ?? BmsEncodingDetector.DecodeBytes(snapshot.Bytes,
            Encoding.GetEncoding(detection.EncodingName.TrimEnd('?')));
        return ApplyDecodedMetadata(chart, text) with { EncodingName = detection.EncodingName };
    }

    /// <summary>捕捉した入力から文字コードを判定し、同じ入力の基本値を直接返します。</summary>
    internal static ChartFile ParseSnapshotWithEncodingDetection(ChartFileSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return ParseSnapshot(snapshot, BmsEncodingDetector.Detect(snapshot.Bytes));
    }

    /// <summary>指定文字コードによる生メタデータだけを更新し、捕捉済み資源結果と識別を継承します。</summary>
    internal static ChartFile ApplyMetadataEncoding(ChartFileSnapshot snapshot, ChartFile chart, string encoding)
        => ApplyDecodedMetadata(chart, BmsEncodingDetector.DecodeBytes(snapshot.Bytes, Encoding.GetEncoding(encoding.TrimEnd('?')))) with { EncodingName = encoding, LastWriteTimeUtc = snapshot.LastWriteTimeUtc, Date = Lr2SongRowEnricher.ToLr2UnixSeconds(snapshot.LastWriteTimeUtc) };

    private static bool ShouldApplyDetectedMetadataEncoding(string name)
        => !string.IsNullOrWhiteSpace(name) && !name.EndsWith("?", StringComparison.Ordinal)
            && !name.Equals("unknown", StringComparison.OrdinalIgnoreCase) && Encoding.GetEncoding(name).CodePage != 932;

    private static ChartFile ApplyDecodedMetadata(ChartFile chart, string text)
    {
        string title = null;
        string subtitle = null;
        string artist = null;
        string subartist = null;
        string genre = null;
        using var reader = new StringReader(text ?? string.Empty);
        foreach (string line in ReadLines(reader))
        {
            if (!TryParseDirectiveLine(line, out BmsDirective directive, out int valueStart))
            {
                continue;
            }
            string value = valueStart >= 0 && valueStart <= line.Length ? line[valueStart..] : string.Empty;
            switch (directive)
            {
                case BmsDirective.Title when string.IsNullOrWhiteSpace(title): title = value; break;
                case BmsDirective.SubTitle when string.IsNullOrWhiteSpace(subtitle): subtitle = value; break;
                case BmsDirective.Artist when string.IsNullOrWhiteSpace(artist): artist = value; break;
                case BmsDirective.SubArtist when string.IsNullOrWhiteSpace(subartist): subartist = value; break;
                case BmsDirective.Genre when string.IsNullOrWhiteSpace(genre): genre = value; break;
            }
        }
        title ??= string.Empty;
        subtitle ??= string.Empty;
        artist ??= string.Empty;
        subartist ??= string.Empty;
        return chart with
        {
            RawTitle = title,
            RawSubtitle = subtitle,
            RawArtist = artist,
            Subartist = subartist,
            Genre = genre ?? string.Empty,
            Subtitle = subtitle,
            Title = string.IsNullOrWhiteSpace(subtitle) ? title : title + " " + subtitle,
            Artist = string.IsNullOrWhiteSpace(subartist) ? artist : artist + " " + subartist
        };
    }

    /// <summary>従来受理する可視ノート記述だけを検査し、音声定義だけの譜面を0ノートとして扱います。</summary>
    internal static bool IsZeroNote(string path)
    {
        using FileStream stream = LongPathFileSystem.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.GetEncoding("shift_jis"), detectEncodingFromByteOrderMarks: false);
        return !ReadLines(reader).Any(line => visibleObjectChRegex.IsMatch(line));
    }

    private static bool IsShiftJis(string encodingName)
        => Encoding.GetEncoding(encodingName).CodePage == 932;

    private static IEnumerable<string> ReadLines(TextReader reader)
    {
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            yield return line;
        }
    }

    private static ChartFile ParseLines(
        IEnumerable<string> enumerable,
        string filePath,
        Func<string> md5Provider,
        Func<string> sha256Provider)
    {
        string title = string.Empty;
        string subtitle = string.Empty;
        string artist = string.Empty;
        string subartist = string.Empty;
        string genre = string.Empty;
        string banner = null;
        string stagefile = null;
        string backbmp = null;
        int level = 0;
        int difficulty = -1;
        int mode = 5;
        int judge = 2;
        var resourceReferences = new List<ChartResourceReference>();
        bool flag8 = false;
        bool flag9 = false;
        bool flag10 = false;
        bool flag11 = false;
        bool flag12 = false;
        bool flag13 = false;
        bool flag14 = false;
        bool flag15 = false;
        bool flag16 = false;
        bool flag17 = false;
        bool flag18 = false;
        bool forcePmsMode = filePath.EndsWith(".pms", StringComparison.OrdinalIgnoreCase);
        foreach (string item in enumerable)
        {
            if (IsFpDscDirective(item))
            {
                forcePmsMode = true;
                continue;
            }
            if (IsLr2CustomFolderDirective(item))
            {
                judge = 2;
                continue;
            }
            if (!TryParseDirectiveLine(item, out BmsDirective directive, out int valueStart))
            {
                continue;
            }
            if (directive == BmsDirective.ModeChannel && TryParseModeChannelLine(item, out char channelGroup, out char lane))
            {
                switch (channelGroup)
                {
                    case '1':
                    case '3':
                    case '5':
                        switch (lane)
                        {
                            case '8':
                                flag8 = true;
                                break;
                            case '9':
                                flag9 = true;
                                break;
                        }
                        break;
                    case '2':
                    case '4':
                    case '6':
                        switch (lane)
                        {
                            case '1':
                                flag10 = true;
                                break;
                            case '2':
                                flag11 = true;
                                break;
                            case '3':
                                flag12 = true;
                                break;
                            case '4':
                                flag13 = true;
                                break;
                            case '5':
                                flag14 = true;
                                break;
                            case '6':
                                flag15 = true;
                                break;
                            case '7':
                                flag16 = true;
                                break;
                            case '8':
                                flag17 = true;
                                break;
                            case '9':
                                flag18 = true;
                                break;
                        }
                        break;
                }
                continue;
            }
            string value = valueStart >= 0 && valueStart <= item.Length ? item.Substring(valueStart) : string.Empty;
            switch (directive)
            {
                case BmsDirective.Wav:
                    resourceReferences.Add(ChartResourceReference.Parse(value, ChartResourceKind.Audio));
                    break;
                case BmsDirective.Bmp:
                    resourceReferences.Add(ChartResourceReference.Parse(value, ChartResourceKind.Unknown));
                    break;
                case BmsDirective.Title:
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        title = value;
                    }
                    difficulty = InferLr2DifficultyFromText(value, difficulty);
                    break;
                case BmsDirective.Genre:
                    if (string.IsNullOrWhiteSpace(genre))
                    {
                        genre = value;
                    }
                    difficulty = InferLr2DifficultyFromText(value, difficulty);
                    break;
                case BmsDirective.Artist:
                    if (string.IsNullOrWhiteSpace(artist))
                    {
                        artist = value;
                    }
                    break;
                case BmsDirective.PlayLevel:
                    level = ParseLr2DirectiveInt(value);
                    break;
                case BmsDirective.MaxTracks:
                    level = ParseLr2DirectiveInt(value);
                    break;
                case BmsDirective.Difficulty:
                    difficulty = ParseLr2DirectiveInt(value);
                    break;
                case BmsDirective.Rank:
                    judge = ParseLr2DirectiveInt(value);
                    break;
                case BmsDirective.SubTitle:
                    if (string.IsNullOrWhiteSpace(subtitle))
                    {
                        subtitle = value;
                    }
                    break;
                case BmsDirective.SubArtist:
                    if (string.IsNullOrWhiteSpace(subartist))
                    {
                        subartist = value;
                    }
                    break;
                case BmsDirective.Banner:
                    if (string.IsNullOrWhiteSpace(banner))
                    {
                        banner = value;
                    }
                    break;
                case BmsDirective.StageFile:
                    if (string.IsNullOrWhiteSpace(stagefile))
                    {
                        stagefile = value;
                    }
                    break;
                case BmsDirective.BackBmp:
                    if (string.IsNullOrWhiteSpace(backbmp))
                    {
                        backbmp = value;
                    }
                    break;
            }
        }
        AddOptionalImages(resourceReferences, stagefile, backbmp, banner);
        if (forcePmsMode)
        {
            mode = 9;
        }
        else if (flag17 || flag18)
        {
            mode = 14;
        }
        else if (flag10 || flag11 || flag12 || flag13 || flag14 || flag15 || flag16)
        {
            mode = flag8 || flag9 ? 14 : 10;
        }
        else if (flag8 || flag9)
        {
            mode = 7;
        }
        return new ChartFile(
            ChartFileKind.Bms, filePath, md5Provider(), sha256Provider(),
            string.IsNullOrWhiteSpace(subtitle) ? title : title + " " + subtitle,
            title, string.IsNullOrWhiteSpace(subartist) ? artist : artist + " " + subartist,
            genre, null, string.Empty, level.ToString(CultureInfo.InvariantCulture), level, mode,
            null, subtitle, resourceReferences.ToImmutableList(), stagefile, backbmp, banner)
        {
            RawSubtitle = subtitle,
            RawArtist = artist,
            Subartist = subartist,
            Difficulty = difficulty,
            Judge = judge
        };
    }

    private static string TrimLr2HeaderValue(string value)
    {
        return (value ?? string.Empty).TrimEnd(' ', '\t', '\r', '\n');
    }

    private static int InferLr2DifficultyFromText(string value, int currentDifficulty)
    {
        string text = TrimLr2HeaderValue(value);
        int difficulty = currentDifficulty;
        if (EndsWithAsciiIgnoreCase(text, "HARD"))
        {
            difficulty = 2;
        }
        if (EndsWithAsciiIgnoreCase(text, "HYPER"))
        {
            difficulty = 3;
        }
        if (EndsWithAsciiIgnoreCase(text, "ANOTHER"))
        {
            difficulty = 4;
        }
        if (EndsWithAsciiIgnoreCase(text, "EASY"))
        {
            difficulty = 1;
        }
        if (EndsWithAsciiIgnoreCase(text, "EX"))
        {
            difficulty = 4;
        }
        if (EndsWithAsciiIgnoreCase(text, "MANIAC"))
        {
            difficulty = 4;
        }
        if (TrySplitLr2DifficultyToken(text, out _, out _, out int tokenDifficulty) && tokenDifficulty != 0)
        {
            difficulty = tokenDifficulty;
        }
        return difficulty;
    }

    private static bool TrySplitLr2DifficultyToken(string value, out string left, out string right, out int difficulty)
    {
        string text = TrimLr2HeaderValue(value);
        if (TrySplitLr2DifficultyToken(text, "(", ")", out left, out right, out difficulty)
            || TrySplitLr2DifficultyToken(text, "[", "]", out left, out right, out difficulty)
            || TrySplitLr2DifficultyToken(text, "-", "-", out left, out right, out difficulty)
            || TrySplitLr2DifficultyToken(text, "\"", "\"", out left, out right, out difficulty)
            || TrySplitLr2DifficultyToken(text, "<", ">", out left, out right, out difficulty)
            || TrySplitLr2DifficultyToken(text, "～", "～", out left, out right, out difficulty)
            || TrySplitLr2DifficultyToken(text, "【", "】", out left, out right, out difficulty))
        {
            return true;
        }

        left = text;
        right = string.Empty;
        difficulty = 0;
        return false;
    }

    private static bool TrySplitLr2DifficultyToken(
        string value,
        string tokenLeft,
        string tokenRight,
        out string left,
        out string right,
        out int difficulty)
    {
        left = string.Empty;
        right = string.Empty;
        difficulty = 0;
        if (string.IsNullOrEmpty(value) || !value.EndsWith(tokenRight, StringComparison.Ordinal))
        {
            return false;
        }

        int searchLength = value.Length - tokenRight.Length;
        if (searchLength <= 0)
        {
            return false;
        }
        int position = value.LastIndexOf(tokenLeft, searchLength - 1, searchLength, StringComparison.Ordinal);
        if (position < 1)
        {
            return false;
        }

        left = value.Substring(0, position).Trim(' ', '\t');
        right = value.Substring(left.Length).Trim(' ', '\t');
        if (!TryInferLr2DifficultyFromToken(right, out difficulty))
        {
            difficulty = 0;
        }
        return true;
    }

    private static bool TryInferLr2DifficultyFromToken(string value, out int difficulty)
    {
        string text = value ?? string.Empty;
        if (IndexOfAsciiIgnoreCase(text, "BEG") > 0)
        {
            difficulty = 1;
            return true;
        }
        if (IndexOfAsciiIgnoreCase(text, "HARD") > 0
            || IndexOfAsciiIgnoreCase(text, "HYPE") > 0
            || IndexOfAsciiIgnoreCase(text, "HD") > 0
            || IndexOfAsciiIgnoreCase(text, "5H") > 0
            || IndexOfAsciiIgnoreCase(text, "7H") > 0
            || IndexOfAsciiIgnoreCase(text, "10H") > 0
            || IndexOfAsciiIgnoreCase(text, "14H") > 0
            || IndexOfAsciiIgnoreCase(text, "9H") > 0
            || IndexOfAsciiIgnoreCase(text, "DIF") > 0)
        {
            difficulty = 3;
            return true;
        }
        if (IndexOfAsciiIgnoreCase(text, "VERYHARD") > 0
            || IndexOfAsciiIgnoreCase(text, "EX") > 0
            || IndexOfAsciiIgnoreCase(text, "AN") > 0
            || IndexOfAsciiIgnoreCase(text, "SHD") > 0
            || IndexOfAsciiIgnoreCase(text, "5A") > 0
            || IndexOfAsciiIgnoreCase(text, "7A") > 0
            || IndexOfAsciiIgnoreCase(text, "10A") > 0
            || IndexOfAsciiIgnoreCase(text, "14A") > 0
            || IndexOfAsciiIgnoreCase(text, "9A") > 0
            || IndexOfAsciiIgnoreCase(text, "ULT") > 0
            || IndexOfAsciiIgnoreCase(text, "MANI") > 0
            || IndexOfAsciiIgnoreCase(text, "LUNA") > 0
            || IndexOfAsciiIgnoreCase(text, "AHO") > 0
            || IndexOfAsciiIgnoreCase(text, "AFO") > 0
            || IndexOfAsciiIgnoreCase(text, "ASDF") > 0
            || IndexOfAsciiIgnoreCase(text, "HELL") > 0)
        {
            difficulty = 4;
            return true;
        }

        difficulty = 0;
        return false;
    }

    private static bool EndsWithAsciiIgnoreCase(string value, string suffix)
    {
        if (value == null || suffix == null || value.Length < suffix.Length)
        {
            return false;
        }
        int start = value.Length - suffix.Length;
        for (int i = 0; i < suffix.Length; i++)
        {
            if (ToUpperAscii(value[start + i]) != suffix[i])
            {
                return false;
            }
        }
        return true;
    }

    private static int IndexOfAsciiIgnoreCase(string value, string match)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(match) || match.Length > value.Length)
        {
            return -1;
        }
        for (int i = 0; i <= value.Length - match.Length; i++)
        {
            if (StartsWithAsciiIgnoreCase(value, i, match))
            {
                return i;
            }
        }
        return -1;
    }

    private enum BmsDirective
    {
        Unknown,
        ModeChannel,
        Wav,
        Bmp,
        Title,
        SubTitle,
        Genre,
        Artist,
        SubArtist,
        StageFile,
        BackBmp,
        Banner,
        PlayLevel,
        MaxTracks,
        Difficulty,
        Rank
    }

    private static bool TryParseDirectiveLine(string line, out BmsDirective directive, out int valueStart)
    {
        directive = BmsDirective.Unknown;
        valueStart = -1;
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }
        int index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            index++;
        }
        if (index >= line.Length || line[index] != '#')
        {
            return false;
        }
        index++;
        if (index < line.Length && IsAsciiDigit(line[index]))
        {
            directive = BmsDirective.ModeChannel;
            return true;
        }
        int tokenStart = index;
        while (index < line.Length && IsAsciiAlphaNumeric(line[index]))
        {
            index++;
        }
        int tokenLength = index - tokenStart;
        if (tokenLength <= 0)
        {
            return false;
        }
        if (!TryGetDirectiveFromToken(line, tokenStart, tokenLength, out directive))
        {
            return false;
        }
        if (directive == BmsDirective.Wav || directive == BmsDirective.Bmp)
        {
            if (tokenLength != 5 || index >= line.Length || !char.IsWhiteSpace(line[index]))
            {
                directive = BmsDirective.Unknown;
                return false;
            }
        }
        else if (IsLr2NumericDirective(directive))
        {
            if (index >= line.Length)
            {
                directive = BmsDirective.Unknown;
                return false;
            }
            index++;
        }
        else if (index >= line.Length || !char.IsWhiteSpace(line[index]))
        {
            directive = BmsDirective.Unknown;
            return false;
        }
        else
        {
            index++;
        }
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            index++;
        }
        valueStart = index;
        return true;
    }

    private static bool IsLr2NumericDirective(BmsDirective directive)
    {
        return directive == BmsDirective.PlayLevel
            || directive == BmsDirective.MaxTracks
            || directive == BmsDirective.Difficulty
            || directive == BmsDirective.Rank;
    }

    private static bool TryGetDirectiveFromToken(string line, int start, int length, out BmsDirective directive)
    {
        directive = BmsDirective.Unknown;
        if (length == 5 && StartsWithAsciiIgnoreCase(line, start, "WAV") && IsBase36(line[start + 3]) && IsBase36(line[start + 4]))
        {
            directive = BmsDirective.Wav;
            return true;
        }
        if (length == 5 && StartsWithAsciiIgnoreCase(line, start, "BMP") && IsBase36(line[start + 3]) && IsBase36(line[start + 4]))
        {
            directive = BmsDirective.Bmp;
            return true;
        }
        switch (length)
        {
            case 4:
                if (EqualsAsciiIgnoreCase(line, start, "RANK"))
                {
                    directive = BmsDirective.Rank;
                    return true;
                }
                break;
            case 5:
                if (EqualsAsciiIgnoreCase(line, start, "TITLE"))
                {
                    directive = BmsDirective.Title;
                    return true;
                }
                if (EqualsAsciiIgnoreCase(line, start, "GENRE"))
                {
                    directive = BmsDirective.Genre;
                    return true;
                }
                break;
            case 6:
                if (EqualsAsciiIgnoreCase(line, start, "ARTIST"))
                {
                    directive = BmsDirective.Artist;
                    return true;
                }
                if (EqualsAsciiIgnoreCase(line, start, "BANNER"))
                {
                    directive = BmsDirective.Banner;
                    return true;
                }
                break;
            case 7:
                if (EqualsAsciiIgnoreCase(line, start, "BACKBMP"))
                {
                    directive = BmsDirective.BackBmp;
                    return true;
                }
                break;
            case 8:
                if (EqualsAsciiIgnoreCase(line, start, "SUBTITLE"))
                {
                    directive = BmsDirective.SubTitle;
                    return true;
                }
                break;
            case 9:
                if (EqualsAsciiIgnoreCase(line, start, "SUBARTIST"))
                {
                    directive = BmsDirective.SubArtist;
                    return true;
                }
                if (EqualsAsciiIgnoreCase(line, start, "STAGEFILE"))
                {
                    directive = BmsDirective.StageFile;
                    return true;
                }
                if (EqualsAsciiIgnoreCase(line, start, "PLAYLEVEL"))
                {
                    directive = BmsDirective.PlayLevel;
                    return true;
                }
                if (EqualsAsciiIgnoreCase(line, start, "MAXTRACKS"))
                {
                    directive = BmsDirective.MaxTracks;
                    return true;
                }
                break;
            case 10:
                if (EqualsAsciiIgnoreCase(line, start, "DIFFICULTY"))
                {
                    directive = BmsDirective.Difficulty;
                    return true;
                }
                break;
        }
        return false;
    }

    private static bool StartsWithAsciiIgnoreCase(string value, int start, string prefix)
    {
        if (value == null || prefix == null || start < 0 || start + prefix.Length > value.Length)
        {
            return false;
        }
        for (int i = 0; i < prefix.Length; i++)
        {
            if (ToUpperAscii(value[start + i]) != prefix[i])
            {
                return false;
            }
        }
        return true;
    }

    private static bool EqualsAsciiIgnoreCase(string value, int start, string expected)
    {
        return start >= 0
            && expected != null
            && value != null
            && start + expected.Length <= value.Length
            && StartsWithAsciiIgnoreCase(value, start, expected);
    }

    private static char ToUpperAscii(char value)
    {
        return value >= 'a' && value <= 'z' ? (char)(value - ('a' - 'A')) : value;
    }

    private static bool IsAsciiAlphaNumeric(char value)
    {
        return IsAsciiDigit(value) || (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');
    }

    private static bool IsBase36(char value)
    {
        return IsAsciiAlphaNumeric(value);
    }

    /// <summary>BMSの選択済み任意画像を抽出結果に含めます。空の定義も解析状態を保持します。</summary>
    private static void AddOptionalImages(List<ChartResourceReference> references, string stagefile, string backbmp, string banner)
    {
        if (stagefile != null)
        {
            references.Add(ChartResourceReference.Parse(stagefile, ChartResourceKind.Image, ChartResourceUsage.Stagefile));
        }

        if (backbmp != null)
        {
            references.Add(ChartResourceReference.Parse(backbmp, ChartResourceKind.Image, ChartResourceUsage.Backbmp));
        }

        if (banner != null)
        {
            references.Add(ChartResourceReference.Parse(banner, ChartResourceKind.Image, ChartResourceUsage.Banner));
        }
    }

    private static bool IsFpDscDirective(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        string trimmed = value.TrimStart();
        return trimmed.StartsWith("#FP/DSC", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLr2CustomFolderDirective(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        string trimmed = value.TrimStart();
        return trimmed.StartsWith("#CUSTOMFOLDER", StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseLr2DirectiveInt(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }
        string trimmed = value.TrimStart();
        int sign = 1;
        int index = 0;
        if (index < trimmed.Length && (trimmed[index] == '+' || trimmed[index] == '-'))
        {
            sign = trimmed[index] == '-' ? -1 : 1;
            index++;
        }

        long result = 0;
        bool hasDigit = false;
        while (index < trimmed.Length && IsAsciiDigit(trimmed[index]))
        {
            hasDigit = true;
            result = (result * 10) + (trimmed[index] - '0');
            long signed = sign < 0 ? -result : result;
            if (signed > int.MaxValue)
            {
                return int.MaxValue;
            }
            if (signed < int.MinValue)
            {
                return int.MinValue;
            }
            index++;
        }
        return hasDigit ? (int)(sign * result) : 0;
    }

    private static bool TryParseModeChannelLine(string line, out char channelGroup, out char lane)
    {
        channelGroup = '\0';
        lane = '\0';
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }
        int index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
        {
            index++;
        }
        if (index >= line.Length || line[index] != '#')
        {
            return false;
        }
        index++;
        if (index + 5 > line.Length
            || !IsAsciiDigit(line[index])
            || !IsAsciiDigit(line[index + 1])
            || !IsAsciiDigit(line[index + 2]))
        {
            return false;
        }
        channelGroup = line[index + 3];
        lane = line[index + 4];
        if (!((channelGroup == '1'
                || channelGroup == '2'
                || channelGroup == '3'
                || channelGroup == '4'
                || channelGroup == '5'
                || channelGroup == '6')
            && lane >= '1'
            && lane <= '9'))
        {
            return false;
        }
        return true;
    }

    private static bool IsAsciiDigit(char value)
    {
        return value >= '0' && value <= '9';
    }

    /// <summary>既存の明示取得入口で同じバイト列からリソースとハッシュを確定します。</summary>
    private static bool HasCp932DecodeUnsupportedResourceValue(byte[] bytes, out ChartResourceKind kind)
    {
        kind = ChartResourceKind.Unknown;
        if (bytes == null || bytes.Length == 0)
        {
            return false;
        }

        int lineStart = 0;
        while (lineStart < bytes.Length)
        {
            int lineEnd = lineStart;
            while (lineEnd < bytes.Length && bytes[lineEnd] != '\r' && bytes[lineEnd] != '\n')
            {
                lineEnd++;
            }
            if (TryGetBmsResourceDirectiveValue(bytes, lineStart, lineEnd, out ChartResourceKind resourceKind, out int valueStart, out int valueLength)
                && !CanDecodeCp932(bytes, valueStart, valueLength))
            {
                kind = resourceKind;
                return true;
            }
            if (lineEnd >= bytes.Length)
            {
                break;
            }
            lineStart = lineEnd + 1;
            if (bytes[lineEnd] == '\r' && lineStart < bytes.Length && bytes[lineStart] == '\n')
            {
                lineStart++;
            }
        }
        return false;
    }

    private static bool TryGetBmsResourceDirectiveValue(
        byte[] bytes,
        int lineStart,
        int lineEnd,
        out ChartResourceKind kind,
        out int valueStart,
        out int valueLength)
    {
        kind = ChartResourceKind.Unknown;
        valueStart = -1;
        valueLength = 0;
        if (bytes == null || lineStart < 0 || lineStart >= lineEnd || lineEnd > bytes.Length)
        {
            return false;
        }

        int index = lineStart;
        if (index == 0 && lineEnd - index >= 3 && bytes[index] == 0xEF && bytes[index + 1] == 0xBB && bytes[index + 2] == 0xBF)
        {
            index += 3;
        }
        while (index < lineEnd && IsAsciiWhitespace(bytes[index]))
        {
            index++;
        }
        if (index >= lineEnd || bytes[index] != '#')
        {
            return false;
        }
        index++;
        int tokenStart = index;
        while (index < lineEnd && IsAsciiAlphaNumericByte(bytes[index]))
        {
            index++;
        }
        int tokenLength = index - tokenStart;
        if (!TryGetResourceDirectiveKind(bytes, tokenStart, tokenLength, out kind))
        {
            return false;
        }
        if (index >= lineEnd || !IsAsciiWhitespace(bytes[index]))
        {
            return false;
        }
        index++;
        while (index < lineEnd && IsAsciiWhitespace(bytes[index]))
        {
            index++;
        }

        int valueEnd = lineEnd;
        while (valueEnd > index && IsAsciiWhitespace(bytes[valueEnd - 1]))
        {
            valueEnd--;
        }
        valueStart = index;
        valueLength = Math.Max(0, valueEnd - index);
        return true;
    }

    private static bool TryGetResourceDirectiveKind(byte[] bytes, int start, int length, out ChartResourceKind kind)
    {
        kind = ChartResourceKind.Unknown;
        if (bytes == null || start < 0 || start + length > bytes.Length)
        {
            return false;
        }
        if (length == 5 && StartsWithAsciiIgnoreCase(bytes, start, "WAV") && IsBase36Byte(bytes[start + 3]) && IsBase36Byte(bytes[start + 4]))
        {
            kind = ChartResourceKind.Audio;
            return true;
        }
        if (length == 5 && StartsWithAsciiIgnoreCase(bytes, start, "BMP") && IsBase36Byte(bytes[start + 3]) && IsBase36Byte(bytes[start + 4]))
        {
            kind = ChartResourceKind.Unknown;
            return true;
        }
        switch (length)
        {
            case 6:
                if (EqualsAsciiIgnoreCase(bytes, start, "BANNER"))
                {
                    kind = ChartResourceKind.Image;
                    return true;
                }
                break;
            case 7:
                if (EqualsAsciiIgnoreCase(bytes, start, "BACKBMP"))
                {
                    kind = ChartResourceKind.Image;
                    return true;
                }
                break;
            case 9:
                if (EqualsAsciiIgnoreCase(bytes, start, "STAGEFILE"))
                {
                    kind = ChartResourceKind.Image;
                    return true;
                }
                break;
        }
        return false;
    }

    private static bool CanDecodeCp932(byte[] bytes, int start, int length)
    {
        if (bytes == null || start < 0 || length < 0 || start + length > bytes.Length)
        {
            return true;
        }
        try
        {
            sjisEnc.GetString(bytes, start, length);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool StartsWithAsciiIgnoreCase(byte[] value, int start, string prefix)
    {
        if (value == null || prefix == null || start < 0 || start + prefix.Length > value.Length)
        {
            return false;
        }
        for (int i = 0; i < prefix.Length; i++)
        {
            if (ToUpperAscii((char)value[start + i]) != prefix[i])
            {
                return false;
            }
        }
        return true;
    }

    private static bool EqualsAsciiIgnoreCase(byte[] value, int start, string expected)
    {
        return start >= 0
            && expected != null
            && value != null
            && start + expected.Length <= value.Length
            && StartsWithAsciiIgnoreCase(value, start, expected);
    }

    private static bool IsAsciiAlphaNumericByte(byte value)
    {
        return (value >= '0' && value <= '9')
            || (value >= 'A' && value <= 'Z')
            || (value >= 'a' && value <= 'z');
    }

    private static bool IsBase36Byte(byte value)
    {
        return IsAsciiAlphaNumericByte(value);
    }

    private static bool IsAsciiWhitespace(byte value)
    {
        return value == ' '
            || value == '\t'
            || value == '\r'
            || value == '\n'
            || value == '\f'
            || value == '\v';
    }

}
