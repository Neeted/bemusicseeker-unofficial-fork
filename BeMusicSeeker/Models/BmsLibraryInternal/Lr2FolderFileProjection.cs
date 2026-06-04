using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BeMusicSeeker.Models.LR2;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2FolderFileDefinition
{
    public string Title { get; set; }

    public string Subtitle { get; set; }

    public string Category { get; set; }

    public string InformationA { get; set; }

    public string InformationB { get; set; }

    public string Command { get; set; }

    public int? MaxTracks { get; set; }

    public string Banner { get; set; }

    public bool HasCustomFolderDirective { get; set; }
}

internal sealed class Lr2FolderFileRowRequest
{
    public string FilePath { get; set; }

    public Lr2FolderFileDefinition Definition { get; set; }

    public LR2SongDB.folder ExistingRow { get; set; }

    public DateTime? LastWriteTimeUtc { get; set; }

    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

    public int FolderType { get; set; } = 2;

    public string ParentHash { get; set; }
}

internal static class Lr2FolderFileProjection
{
    internal static Lr2FolderFileDefinition ParseDefinition(IEnumerable<string> lines)
    {
        var definition = new Lr2FolderFileDefinition();
        foreach (string line in lines ?? [])
        {
            if (!TryReadDirective(line, out string directive, out string body))
            {
                continue;
            }

            switch (directive.ToUpperInvariant())
            {
                case "TITLE":
                    definition.Title = body;
                    break;
                case "SUBTITLE":
                    definition.Subtitle = body;
                    break;
                case "CATEGORY":
                case "GENRE":
                    definition.Category = body;
                    break;
                case "INFORMATION_A":
                    definition.InformationA = body;
                    break;
                case "INFORMATION_B":
                    definition.InformationB = body;
                    break;
                case "TAG":
                case "COMMAND":
                    definition.Command = body;
                    break;
                case "MAXTRACKS":
                case "PLAYLEVEL":
                    if (int.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maxTracks))
                    {
                        definition.MaxTracks = maxTracks;
                    }
                    break;
                case "BANNER":
                    definition.Banner = body;
                    break;
                case "CUSTOMFOLDER":
                    definition.HasCustomFolderDirective = true;
                    break;
            }
        }
        return definition;
    }

    internal static bool TryCreateFolderRow(Lr2FolderFileRowRequest request, out LR2SongDB.folder row)
    {
        row = null;
        request ??= new Lr2FolderFileRowRequest();
        string filePath = NormalizeFilePath(request.FilePath);
        if (string.IsNullOrWhiteSpace(filePath) || request.LastWriteTimeUtc == null || request.FolderType == 1)
        {
            return false;
        }

        if (!Lr2CompatibilityEvaluator.TryGetCp932ByteCount(filePath, out _))
        {
            return false;
        }

        string parentHash = request.ParentHash;
        if (string.IsNullOrWhiteSpace(parentHash)
            && !TryComputeParentHash(filePath, out parentHash))
        {
            return false;
        }

        Lr2FolderFileDefinition definition = request.Definition ?? new Lr2FolderFileDefinition();
        row = new LR2SongDB.folder
        {
            title = definition.Title,
            subtitle = definition.Subtitle,
            category = definition.Category,
            info_a = definition.InformationA,
            info_b = definition.InformationB,
            command = definition.Command,
            path = filePath,
            type = request.FolderType,
            banner = definition.Banner,
            parent = parentHash,
            date = request.LastWriteTimeUtc.Value.ToUnixtime(),
            max = definition.MaxTracks ?? 0,
            adddate = request.ExistingRow?.adddate ?? request.GeneratedAtUtc.ToUnixtime()
        };
        return true;
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

    private static string NormalizeFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
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
