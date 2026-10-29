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

/// <summary>捕捉済み入力からBMS基本値と不変リソース結果を生成します。条件分岐やLNOBJを評価せず全行の使用IDと全定義先を対応付け、未定義IDは要求へ含めません。保存行の生成や再読取りを行いません。</summary>
internal static class BmsChartFileParser
{
    private static readonly Regex visibleObjectChRegex = new("^[\\s\u3000]*#[0-9]{3}(?:[12][1-9A-Z]|[56][1-9A-Z])(?:[\\s\u3000]*:[\\s\u3000]*|[\\s\u3000]+)(?:[\\s\u3000]*00)*[\\s\u3000]*(?!00)[0-9A-Z]{2}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Encoding sjisEnc = Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback());

    /// <summary>同じ入力のCP932保存値・LR2診断と、実採用本文のメタデータ・通常資源を確定します。具体名は既存DecodeBytesのBOM認識を使い、未指定は従来の自動採用規則へ従います。</summary>
    internal static ChartFile ParseSnapshot(ChartFileSnapshot snapshot, string codepageName = null, ChartResourceParseDetails details = null)
        => ParseAdoptedSnapshot(snapshot, codepageName, codepageName == null ? BmsEncodingDetector.Detect(snapshot.Bytes) : null, details, out _);

    /// <summary>検出中に取得した本文を再利用し、同じ採用本文から通常資源と表示文字列を作ります。</summary>
    internal static ChartFile ParseSnapshot(ChartFileSnapshot snapshot, BmsEncodingDetectionResult detection)
        => ParseAdoptedSnapshot(snapshot, null, detection, null, out _);

    /// <summary>既存の判定結果を採用して通常資源と文字メタデータを同時に置き換え、呼出元の利用者値・CP932基本値を保持します。</summary>
    internal static ChartFile ApplyEncodingDetection(ChartFileSnapshot snapshot, ChartFile chart, BmsEncodingDetectionResult detection)
        => ApplyAdoptedValues(chart, ParseAdoptedSnapshot(snapshot, null, detection, null, out _));

    /// <summary>同snapshotの自動採用本文から、通常資源と発見専用PREVIEWを返します。</summary>
    internal static ChartFile ParseSnapshotWithEncodingDetection(ChartFileSnapshot snapshot)
        => ParseSnapshotWithEncodingDetection(snapshot, out _);

    /// <summary>発見専用PREVIEWは保存や健康度へ加えず、実採用本文の最初の非空値を付帯出力します。</summary>
    internal static ChartFile ParseSnapshotWithEncodingDetection(ChartFileSnapshot snapshot, out string preview, string fixedEncoding = null)
        => ParseAdoptedSnapshot(snapshot, fixedEncoding, fixedEncoding == null ? BmsEncodingDetector.Detect(snapshot.Bytes) : null, null, out preview);

    /// <summary>明示文字コードの既存BOM認識で通常資源と文字メタデータを更新し、保存画像三項目などのCP932基本値を維持します。</summary>
    internal static ChartFile ApplyEncoding(ChartFileSnapshot snapshot, ChartFile chart, string encoding)
        => ApplyAdoptedValues(chart, ParseAdoptedSnapshot(snapshot, encoding, null, null, out _)) with
        { LastWriteTimeUtc = snapshot.LastWriteTimeUtc, Date = Lr2SongRowEnricher.ToLr2UnixSeconds(snapshot.LastWriteTimeUtc) };

    private static ChartFile ApplyAdoptedValues(ChartFile chart, ChartFile adopted) => chart with
    {
        RawTitle = adopted.RawTitle,
        RawSubtitle = adopted.RawSubtitle,
        RawArtist = adopted.RawArtist,
        Subartist = adopted.Subartist,
        Genre = adopted.Genre,
        Title = adopted.Title,
        Subtitle = adopted.Subtitle,
        Artist = adopted.Artist,
        Resources = adopted.Resources,
        EncodingName = adopted.EncodingName,
        ResourceHealthMaintenanceSnapshot = adopted.ResourceHealthMaintenanceSnapshot
    };

    private static ChartFile ParseAdoptedSnapshot(ChartFileSnapshot snapshot, string explicitEncoding,
        BmsEncodingDetectionResult detection, ChartResourceParseDetails details, out string preview)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Encoding selectedEncoding = explicitEncoding == null ? null : Encoding.GetEncoding(explicitEncoding.TrimEnd('?'));
        // 同じ既定置換復号でBOM認識にも差がない場合だけ、CP932初期解析と定義詳細を共用します。
        bool useInitialCp932 = selectedEncoding == null ? !ShouldApplyDetectedEncoding(detection?.EncodingName)
            : selectedEncoding.CodePage == 932 && !HasRecognizedBom(snapshot.Bytes);
        ChartResourceParseDetails cp932Details = details == null ? null : useInitialCp932 ? details : new ChartResourceParseDetails();
        using var stream = new MemoryStream(snapshot.Bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.GetEncoding(932), detectEncodingFromByteOrderMarks: false);
        using IEnumerator<bool> statuses = EnumerateCp932LineDecodeStatuses(snapshot.Bytes).GetEnumerator();
        ChartFile basic = ParseLines(ReadLines(reader), snapshot.Path, () => snapshot.Md5, () => snapshot.Sha256,
            cp932Details, statuses, out preview, out Lr2ResourceReferenceEvaluation lr2);
        ChartFile adopted = basic;
        if (!useInitialCp932)
        {
            string text = explicitEncoding == null ? detection.DecodedText : null;
            text ??= BmsEncodingDetector.DecodeBytes(snapshot.Bytes, selectedEncoding ?? Encoding.GetEncoding(detection.EncodingName.TrimEnd('?')));
            using var adoptedReader = new StringReader(text);
            adopted = ParseLines(ReadLines(adoptedReader), snapshot.Path, () => snapshot.Md5, () => snapshot.Sha256,
                details, null, out preview, out _);
        }
        if (details != null && cp932Details != null && !ReferenceEquals(details, cp932Details))
        {
            details.AlternativeCp932Definitions.AddRange(cp932Details.AlternativeCp932Definitions);
            details.AlternativeCp932Definitions.AddRange(cp932Details.Definitions.Where(item => item.Cp932DecodeUnsupported));
        }
        Lr2ChartPathEvaluation path = Lr2CompatibilityEvaluator.EvaluateChartPath(snapshot.Path);
        var facts = new ResourceHealthMaintenanceSnapshot
        {
            Path = snapshot.Path,
            Hash = snapshot.Md5,
            Lr2WarningFlags = (int)(path.WarningFlags | lr2.WarningFlags),
            Lr2ResourceMaxRelativeCp932Bytes = lr2.MaxRelativeCp932Bytes,
            Lr2ResourceHasParentTraversal = lr2.HasParentTraversal
        };
        return basic with
        {
            RawTitle = adopted.RawTitle,
            RawSubtitle = adopted.RawSubtitle,
            RawArtist = adopted.RawArtist,
            Subartist = adopted.Subartist,
            Genre = adopted.Genre,
            Title = adopted.Title,
            Subtitle = adopted.Subtitle,
            Artist = adopted.Artist,
            Resources = adopted.Resources,
            EncodingName = explicitEncoding ?? detection?.EncodingName,
            LastWriteTimeUtc = snapshot.LastWriteTimeUtc,
            ResourceHealthMaintenanceSnapshot = facts,
            Warnings = Lr2CompatibilityWarningProjection.BuildWarnings(facts)
        };
    }

    // StreamReaderのBOM認識対象です。短い先頭だけを確認し、本文比較や再走査は行いません。
    private static bool HasRecognizedBom(ReadOnlySpan<byte> bytes)
        => bytes is [0xEF, 0xBB, 0xBF, ..] or [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..] or [0x00, 0x00, 0xFE, 0xFF, ..];

    /// <summary>発見専用PREVIEWを通常資源と同じ採用本文で分類します。原文rootedは正規化前に除外し、通常要求や保存値へ加えません。</summary>
    internal static ChartFile ParseForPackageDiscovery(ChartFileSnapshot snapshot, string fixedEncoding, out ChartResourceReference? preview)
    {
        ChartFile result = ParseAdoptedSnapshot(snapshot, fixedEncoding,
            fixedEncoding == null ? BmsEncodingDetector.Detect(snapshot.Bytes) : null, null, out string raw);
        preview = string.IsNullOrWhiteSpace(raw) ? null
            : Path.IsPathRooted(raw.Trim()) ? new ChartResourceReference(ChartResourceKind.Audio, ChartResourceUsage.Normal,
                raw, string.Empty, string.Empty, ChartResourcePathNormalizationStatus.RootedOrAbsolute)
            : ChartResourceReference.Parse(raw, ChartResourceKind.Audio);
        return result;
    }

    /// <summary>指定値の具体性だけを共通判定します。適用する入力の同一性は各入口の既存契約で判定します。</summary>
    internal static string GetConcreteFixedEncodingName(string encoding, bool fixedEncoding)
        => fixedEncoding && !string.IsNullOrWhiteSpace(encoding) && !encoding.EndsWith("?", StringComparison.Ordinal)
            && !encoding.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? encoding : null;

    /// <summary>現在の譜面識別に属する具体的な確定指定だけを返します。unknownや推定末尾?は明示指定として再利用しません。</summary>
    internal static string GetConcreteFixedEncoding(ChartFile chart)
    {
        ResourceHealthMaintenanceSnapshot value = chart?.ResourceHealthMaintenanceSnapshot;
        return value?.EncodingFixed == true && string.Equals(value.Path, chart.Path, StringComparison.OrdinalIgnoreCase)
            && string.Equals(value.Hash, chart.Md5, StringComparison.OrdinalIgnoreCase)
            ? GetConcreteFixedEncodingName(value.Encoding, value.EncodingFixed) : null;
    }

    private static bool ShouldApplyDetectedEncoding(string name)
        => !string.IsNullOrWhiteSpace(name) && !name.EndsWith("?", StringComparison.Ordinal)
            && !name.Equals("unknown", StringComparison.OrdinalIgnoreCase) && Encoding.GetEncoding(name).CodePage != 932;

    /// <summary>従来受理する可視ノート記述だけを検査し、音声定義だけの譜面を0ノートとして扱います。</summary>
    internal static bool IsZeroNote(string path)
    {
        using FileStream stream = LongPathFileSystem.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.GetEncoding("shift_jis"), detectEncodingFromByteOrderMarks: false);
        return !ReadLines(reader).Any(line => visibleObjectChRegex.IsMatch(line));
    }

    private static bool TryReadNamedDirective(string line, string name, out string value)
    {
        int end = name.Length + 1;
        value = string.Empty;
        if (line.Length <= end || line[0] != '#' || !line.AsSpan(1, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase)
            || !char.IsWhiteSpace(line[end]))
        {
            return false;
        }

        value = line[end..].Trim();
        return true;
    }

    private static bool TryReadResourceChannel(string line, out string channel, out string data)
    {
        channel = data = string.Empty;
        if (line.Length <= 6 || line[0] != '#' || !line.AsSpan(1, 3).ToString().All(char.IsAsciiDigit))
        {
            return false;
        }

        int separator = 6;
        while (separator < line.Length && char.IsWhiteSpace(line[separator]))
        {
            separator++;
        }

        channel = line.Substring(4, 2).ToUpperInvariant();
        // 軽量解析ではコロン後の原文を二文字ずつ読み、コロンがない行は行全体を使います。
        data = separator < line.Length && line[separator] == ':' ? line[(separator + 1)..] : line;
        return true;
    }

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
        Func<string> sha256Provider,
        ChartResourceParseDetails details,
        IEnumerator<bool> diagnosticLines, out string preview, out Lr2ResourceReferenceEvaluation lr2)
    {
        preview = string.Empty;
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
        var definitions = new List<(string Id, bool Audio, ChartResourceReference Reference, int DetailIndex)>();
        Lr2ResourcePathEvaluationContext lr2Context = Lr2CompatibilityEvaluator.CreateResourcePathEvaluationContext(filePath);
        Lr2CompatibilityWarningFlags lr2Flags = Lr2CompatibilityWarningFlags.None;
        int? lr2Max = null;
        bool lr2Parent = false;
        int lineNumber = 0;
        var audioIds = new HashSet<string>();
        var visualIds = new HashSet<string>();
        bool base62 = false;
        bool hasMine = false;
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
        string cp932Bom = diagnosticLines == null ? null : Encoding.GetEncoding(932).GetString(Encoding.UTF8.GetPreamble());
        // 詳細ノート解析や分岐評価をせず、全構文上の参照を収集します。
        foreach (string item in enumerable)
        {
            lineNumber++;
            bool lineUndecodable = diagnosticLines?.MoveNext() == true && diagnosticLines.Current;
            // 初期CP932の基本値はBOMを認識しません。LR2診断だけは従来の生bytes走査と同じく先頭UTF-8 BOMを除いて認識します。
            if (diagnosticLines != null && lineNumber == 1 && item.StartsWith(cp932Bom, StringComparison.Ordinal))
            {
                string diagnosticLine = item[cp932Bom.Length..];
                if (TryParseDirectiveLine(diagnosticLine, out BmsDirective diagnosticDirective, out int diagnosticStart)
                    && diagnosticStart >= 0 && diagnosticDirective is BmsDirective.Wav or BmsDirective.Bmp or BmsDirective.Banner or BmsDirective.StageFile or BmsDirective.BackBmp)
                {
                    string diagnosticValue = diagnosticLine[diagnosticStart..];
                    Lr2CompatibilityEvaluator.ApplyResourcePathEvaluation(lr2Context, diagnosticValue,
                        ref lr2Flags, ref lr2Max, ref lr2Parent, lineUndecodable);
                    if (lineUndecodable && details != null)
                    {
                        ChartResourceUsage usage = diagnosticDirective switch
                        {
                            BmsDirective.Banner => ChartResourceUsage.Banner,
                            BmsDirective.StageFile => ChartResourceUsage.Stagefile,
                            BmsDirective.BackBmp => ChartResourceUsage.Backbmp,
                            _ => ChartResourceUsage.Normal
                        };
                        string source = diagnosticDirective switch
                        {
                            BmsDirective.Wav => "#WAV",
                            BmsDirective.Bmp => "#BMP",
                            BmsDirective.Banner => "#BANNER",
                            BmsDirective.StageFile => "#STAGEFILE",
                            _ => "#BACKBMP"
                        };
                        details.AlternativeCp932Definitions.Add(new ChartResourceDefinition(source,
                            diagnosticDirective is BmsDirective.Wav or BmsDirective.Bmp ? diagnosticLine.TrimStart().Substring(4, 2) : null,
                            ChartResourceReference.Parse(diagnosticValue, diagnosticDirective == BmsDirective.Wav ? ChartResourceKind.Audio : ChartResourceKind.Unknown, usage),
                            lineNumber, item, Cp932DecodeUnsupported: true));
                    }
                }
            }
            // 定義行と同じ行頭空白を許容し、コロン後と末尾のチャンネル原文は変更しません。
            string resourceLine = item.TrimStart();
            if (TryReadNamedDirective(resourceLine, "BASE", out string numberBase) && numberBase == "62")
            {
                base62 = true;
            }

            if (TryReadResourceChannel(resourceLine, out string channel, out string data))
            {
                bool audio = channel == "01" || channel[0] is '1' or '2' or '3' or '4' or '5' or '6';
                bool visual = channel is "04" or "06" or "07" or "0A";
                bool mine = channel[0] is 'D' or 'E';
                for (int index = 0; index + 1 < data.Length; index += 2)
                {
                    string id = data.Substring(index, 2);
                    if (id == "00" || !id.All(char.IsAsciiLetterOrDigit))
                    {
                        continue;
                    }

                    if (audio)
                    {
                        audioIds.Add(id);
                    }

                    if (visual)
                    {
                        visualIds.Add(id);
                    }

                    if (mine)
                    {
                        hasMine = true;
                    }
                }
            }
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
            bool resourceDirective = directive is BmsDirective.Wav or BmsDirective.Bmp or BmsDirective.Banner or BmsDirective.StageFile or BmsDirective.BackBmp;
            if (resourceDirective && diagnosticLines != null && !string.IsNullOrWhiteSpace(value))
            {
                if (lineUndecodable) { lr2Flags |= Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported; }
                Lr2CompatibilityEvaluator.ApplyResourcePathEvaluation(lr2Context, value, ref lr2Flags, ref lr2Max, ref lr2Parent,
                    lineUndecodable);
            }
            switch (directive)
            {
                case BmsDirective.Preview:
                    if (string.IsNullOrWhiteSpace(preview)) { preview = value; }
                    break;
                case BmsDirective.Wav:
                    ChartResourceReference audioReference = valueStart < 0
                        ? MalformedReference(item, ChartResourceKind.Audio) : ChartResourceReference.Parse(value, ChartResourceKind.Audio);
                    string audioId = item.TrimStart().Substring(4, 2);
                    definitions.Add((audioId, true, audioReference, details?.Definitions.Count ?? -1));
                    details?.Definitions.Add(new ChartResourceDefinition("#WAV", audioId,
                        audioReference, lineNumber, item, Cp932DecodeUnsupported: lineUndecodable));
                    break;
                case BmsDirective.Bmp:
                    ChartResourceReference visualReference = valueStart < 0
                        ? MalformedReference(item, ChartResourceKind.Unknown) : ChartResourceReference.Parse(value, ChartResourceKind.Unknown);
                    string visualId = item.TrimStart().Substring(4, 2);
                    definitions.Add((visualId, false, visualReference, details?.Definitions.Count ?? -1));
                    details?.Definitions.Add(new ChartResourceDefinition("#BMP", visualId,
                        visualReference, lineNumber, item, Cp932DecodeUnsupported: lineUndecodable));
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
                    details?.Definitions.Add(new ChartResourceDefinition("#BANNER", null,
                        ChartResourceReference.Parse(value, ChartResourceKind.Image, ChartResourceUsage.Banner),
                        lineNumber, item, IsAdopted: string.IsNullOrWhiteSpace(banner) && !string.IsNullOrWhiteSpace(value),
                        Cp932DecodeUnsupported: lineUndecodable));
                    if (string.IsNullOrWhiteSpace(banner))
                    {
                        banner = value;
                    }
                    break;
                case BmsDirective.StageFile:
                    details?.Definitions.Add(new ChartResourceDefinition("#STAGEFILE", null,
                        ChartResourceReference.Parse(value, ChartResourceKind.Image, ChartResourceUsage.Stagefile),
                        lineNumber, item, IsAdopted: string.IsNullOrWhiteSpace(stagefile) && !string.IsNullOrWhiteSpace(value),
                        Cp932DecodeUnsupported: lineUndecodable));
                    if (string.IsNullOrWhiteSpace(stagefile))
                    {
                        stagefile = value;
                    }
                    break;
                case BmsDirective.BackBmp:
                    details?.Definitions.Add(new ChartResourceDefinition("#BACKBMP", null,
                        ChartResourceReference.Parse(value, ChartResourceKind.Image, ChartResourceUsage.Backbmp),
                        lineNumber, item, IsAdopted: string.IsNullOrWhiteSpace(backbmp) && !string.IsNullOrWhiteSpace(value),
                        Cp932DecodeUnsupported: lineUndecodable));
                    if (string.IsNullOrWhiteSpace(backbmp))
                    {
                        backbmp = value;
                    }
                    break;
            }
        }
        StringComparer idComparer = base62 ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        audioIds = new HashSet<string>(audioIds, idComparer);
        visualIds = new HashSet<string>(visualIds, idComparer);
        if (hasMine && definitions.Any(definition => definition.Audio && definition.Id == "00"))
        {
            audioIds.Add("00");
        }

        if (definitions.Any(definition => !definition.Audio && definition.Id == "00"))
        {
            visualIds.Add("00");
        }

        for (int index = 0; index < definitions.Count; index++)
        {
            (string Id, bool Audio, ChartResourceReference Reference, int DetailIndex) definition = definitions[index];
            ChartResourceReference reference = definition.Reference.WithUsage((definition.Audio ? audioIds : visualIds).Contains(definition.Id));
            resourceReferences.Add(reference);
            if (details != null)
            {
                ChartResourceDefinition diagnostic = details.Definitions[definition.DetailIndex];
                details.Definitions[definition.DetailIndex] = diagnostic with { Reference = diagnostic.Reference.WithUsage(reference.IsUsed) };
            }
        }
        if (details != null)
        {
            var definedAudioIds = new HashSet<string>(definitions.Where(item => item.Audio).Select(item => item.Id), idComparer);
            var definedVisualIds = new HashSet<string>(definitions.Where(item => !item.Audio).Select(item => item.Id), idComparer);
            details.UndefinedReferences.AddRange(audioIds.Where(id => !definedAudioIds.Contains(id)).OrderBy(id => id, idComparer)
                .Select(id => new ChartResourceReferenceNumber("#WAV", id)));
            details.UndefinedReferences.AddRange(visualIds.Where(id => !definedVisualIds.Contains(id)).OrderBy(id => id, idComparer)
                .Select(id => new ChartResourceReferenceNumber("#BMP", id)));
        }
        AddOptionalImages(resourceReferences, stagefile, backbmp, banner);
        lr2 = new Lr2ResourceReferenceEvaluation(lr2Flags, lr2Max, lr2Parent);
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
        Preview,
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
        if (index + 5 <= line.Length && (StartsWithAsciiIgnoreCase(line, index, "WAV") || StartsWithAsciiIgnoreCase(line, index, "BMP"))
            && IsBase36(line[index + 3]) && IsBase36(line[index + 4]))
        {
            directive = StartsWithAsciiIgnoreCase(line, index, "WAV") ? BmsDirective.Wav : BmsDirective.Bmp;
            index += 5;
            if (index >= line.Length || !char.IsWhiteSpace(line[index])) { return true; }
            while (index < line.Length && char.IsWhiteSpace(line[index])) { index++; }
            valueStart = index;
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
                if (EqualsAsciiIgnoreCase(line, start, "PREVIEW"))
                {
                    directive = BmsDirective.Preview;
                    return true;
                }
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

    private static int FindByteLineEnd(byte[] bytes, int start)
    {
        int end = start;
        while (end < bytes.Length && bytes[end] != '\r' && bytes[end] != '\n')
        {
            end++;
        }
        return end;
    }

    private static int AdvancePastByteLine(byte[] bytes, int end)
    {
        int next = end < bytes.Length ? end + 1 : end;
        if (end < bytes.Length && bytes[end] == '\r' && next < bytes.Length && bytes[next] == '\n')
        {
            next++;
        }
        return next;
    }

    // 不正な先行バイトが改行を消費すると、既存StreamReaderは後続の物理行を同じ値として読みます。
    // 同じ既定デコーダーで行終端の有無を確認し、厳格な検査の結果だけを実際の入力行へ対応付けます。
    private static IEnumerable<bool> EnumerateCp932LineDecodeStatuses(byte[] bytes)
    {
        var encoding = Encoding.GetEncoding("shift_jis");
        bool undecodable = false;
        bool hasPendingLine = false;
        for (int start = 0; start < bytes.Length;)
        {
            int next = AdvancePastByteLine(bytes, FindByteLineEnd(bytes, start));
            undecodable |= !CanDecodeCp932(bytes, start, next - start);
            string text = encoding.GetString(bytes, start, next - start);
            hasPendingLine = true;
            if (text.EndsWith('\r') || text.EndsWith('\n'))
            {
                yield return undecodable;
                undecodable = false;
                hasPendingLine = false;
            }
            start = next;
        }
        if (hasPendingLine)
        {
            yield return undecodable;
        }
    }

    private static ChartResourceReference MalformedReference(string line, ChartResourceKind kind)
        => new(kind, ChartResourceUsage.Normal, line, string.Empty, string.Empty, ChartResourcePathNormalizationStatus.MalformedSyntax);

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

}
