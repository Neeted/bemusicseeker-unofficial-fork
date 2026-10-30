using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BeMusicSeeker.Models.LR2;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>確定したディレクティブ値。管理出力では物理本文とDB行の共通の正本です。</summary>
internal sealed class Lr2FolderFileDefinition
{
    public string Title { get; init; }

    public string Subtitle { get; init; }

    public string Category { get; init; }

    public string InformationA { get; init; }

    public string InformationB { get; init; }

    public string Command { get; init; }

    public int? MaxTracks { get; init; }

    public string Banner { get; init; }

    public bool HasCustomFolderDirective { get; init; }
}

internal sealed class Lr2FolderFileRowRequest
{
    public string FilePath { get; set; }

    public string DatabasePath { get; set; }

    public Lr2FolderFileDefinition Definition { get; set; }

    public LR2SongDB.folder ExistingRow { get; set; }

    public DateTime? LastWriteTimeUtc { get; set; }

    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

    public int FolderType { get; set; } = 2;

    public string ParentHash { get; set; }
}

internal static class Lr2FolderFileProjection
{
    /// <summary>管理外・組込みの物理ディレクティブを従来の別名・Trim規則で読み、確定後の定義を一度だけ構築します。</summary>
    internal static Lr2FolderFileDefinition ParseDefinition(IEnumerable<string> lines)
    {
        string title = null, subtitle = null, category = null, informationA = null, informationB = null, command = null, banner = null;
        int? maxTracks = null;
        bool hasCustomFolderDirective = false;
        foreach (string line in lines ?? [])
        {
            if (!TryReadDirective(line, out string directive, out string body))
            {
                continue;
            }

            switch (directive.ToUpperInvariant())
            {
                case "TITLE":
                    title = body;
                    break;
                case "SUBTITLE":
                    subtitle = body;
                    break;
                case "CATEGORY":
                case "GENRE":
                    category = body;
                    break;
                case "INFORMATION_A":
                    informationA = body;
                    break;
                case "INFORMATION_B":
                    informationB = body;
                    break;
                case "TAG":
                case "COMMAND":
                    command = body;
                    break;
                case "MAXTRACKS":
                case "PLAYLEVEL":
                    if (int.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedMaxTracks))
                    {
                        maxTracks = parsedMaxTracks;
                    }
                    break;
                case "BANNER":
                    banner = body;
                    break;
                case "CUSTOMFOLDER":
                    hasCustomFolderDirective = true;
                    break;
            }
        }
        return new Lr2FolderFileDefinition
        {
            Title = title,
            Subtitle = subtitle,
            Category = category,
            InformationA = informationA,
            InformationB = informationB,
            Command = command,
            Banner = banner,
            MaxTracks = maxTracks,
            HasCustomFolderDirective = hasCustomFolderDirective
        };
    }

    /// <summary>管理定義から既存の項目順・改行で物理本文を生成します。値の内部空白は変更しません。</summary>
    internal static string CreateManagedText(Lr2FolderFileDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return "#COMMAND " + definition.Command + Environment.NewLine
            + "#MAXTRACKS " + definition.MaxTracks.GetValueOrDefault().ToString(CultureInfo.InvariantCulture) + Environment.NewLine
            + "#CATEGORY " + definition.Category + Environment.NewLine
            + "#TITLE " + definition.Title + Environment.NewLine
            + "#INFORMATION_A " + definition.InformationA + Environment.NewLine
            + "#INFORMATION_B " + definition.InformationB + Environment.NewLine + Environment.NewLine;
    }

    /// <summary>読取り済み定義からcustom行を作り、同じrawキーの既存adddateはNULLも保持します。定義のない新規行は合成しません。</summary>
    internal static bool TryCreateFolderRow(Lr2FolderFileRowRequest request, out LR2SongDB.folder row)
    {
        row = null;
        request ??= new Lr2FolderFileRowRequest();
        string databasePath = NormalizeDatabasePath(request.DatabasePath ?? request.FilePath);
        if (string.IsNullOrWhiteSpace(databasePath)
            || request.LastWriteTimeUtc == null
            || request.Definition == null
            || !IsSupportedFolderType(request.FolderType))
        {
            return false;
        }

        if (!Lr2CompatibilityEvaluator.TryGetCp932ByteCount(databasePath, out _))
        {
            return false;
        }

        if (!TryResolveParentHash(databasePath, request.ParentHash, out string parentHash))
        {
            return false;
        }

        Lr2FolderFileDefinition definition = request.Definition;
        row = new LR2SongDB.folder
        {
            title = definition.Title?.Trim(),
            subtitle = definition.Subtitle?.Trim(),
            category = definition.Category?.Trim(),
            info_a = definition.InformationA?.Trim(),
            info_b = definition.InformationB?.Trim(),
            command = definition.Command?.Trim(),
            path = databasePath,
            type = request.FolderType,
            banner = definition.Banner?.Trim(),
            parent = parentHash,
            date = request.LastWriteTimeUtc.Value.ToUnixtime(),
            max = definition.MaxTracks ?? 0,
            adddate = request.ExistingRow != null
                && string.Equals(request.ExistingRow.path, databasePath, StringComparison.Ordinal)
                ? request.ExistingRow.adddate : request.GeneratedAtUtc.ToUnixtime()
        };
        return true;
    }

    private static bool IsSupportedFolderType(int folderType)
    {
        return folderType == 2 || folderType == 3 || folderType == 4 || folderType == 6;
    }

    internal static bool TryResolveParentHash(string databasePath, string explicitParentHash, out string parentHash)
    {
        parentHash = explicitParentHash;
        return !string.IsNullOrWhiteSpace(parentHash)
            || TryComputeParentHash(databasePath, out parentHash);
    }

    private static bool TryReadDirective(string line, out string directive, out string body)
    {
        directive = null;
        body = null;
        string trimmed = line?.TrimStart();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed[0] != '#')
        {
            return false;
        }

        int index = 1;
        while (index < trimmed.Length && !char.IsWhiteSpace(trimmed[index]))
        {
            index++;
        }
        if (index <= 1)
        {
            return false;
        }

        directive = trimmed.Substring(1, index - 1);
        body = index < trimmed.Length ? trimmed.Substring(index).Trim() : string.Empty;
        return true;
    }

    internal static string NormalizeDatabasePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            string normalizedRelative = NormalizeKnownRelativeLr2FolderPath(filePath);
            return normalizedRelative ?? Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    internal static string NormalizeKnownRelativeLr2FolderPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        string normalized = filePath.Trim()
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
        {
            return null;
        }

        return IsKnownRelativeLr2FolderPath(normalized) ? normalized : null;
    }

    private static bool IsKnownRelativeLr2FolderPath(string filePath)
    {
        return filePath.StartsWith("LR2files" + Path.DirectorySeparatorChar + "CustomFolder" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || filePath.StartsWith("LR2files" + Path.DirectorySeparatorChar + "Rival" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryComputeParentHash(string filePath, out string parentHash)
    {
        parentHash = null;
        try
        {
            string directory = Lr2FolderPath.SafeGetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            parentHash = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(directory);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
