#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Update;

namespace BeMusicSeeker.Runtime;

/// <summary>Applicationやstatic設定窓口へ触れる前に、唯一の検証入口と通常CLIの優先順位を確定します。</summary>
internal static class ApplicationRuntimeOptionsParser
{
    private const string TestSessionSwitch = "--test-session";

    /// <summary>引用前のargvから実行条件を捕捉します。検証入力が不正な場合は書込みなしで例外を返します。</summary>
    /// <remarks>--test-sessionの次のtokenはmanifestパスであり、先頭の--もパスの一部として扱います。消費した値を別のoptionとして再解釈しません。</remarks>
    internal static ApplicationRuntimeOptions Capture(string executablePath, IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string[] restartArguments = arguments.ToArray();
        int manifestArgument = -1;
        const string updatePrefix = "--update-manifest-url=";
        const string logPrefix = "--log-level=";
        string? cliUrl = null;
        string? rawLogLevel = null;
        for (int index = 0; index < restartArguments.Length; index++)
        {
            string argument = restartArguments[index];
            if (argument.StartsWith(updatePrefix, StringComparison.OrdinalIgnoreCase))
            {
                cliUrl = argument[updatePrefix.Length..];
                continue;
            }
            if (argument.StartsWith(logPrefix, StringComparison.OrdinalIgnoreCase))
            {
                rawLogLevel = argument[logPrefix.Length..];
                continue;
            }
            if (!argument.StartsWith(TestSessionSwitch, StringComparison.OrdinalIgnoreCase)) { continue; }
            if (!argument.Equals(TestSessionSwitch, StringComparison.OrdinalIgnoreCase)
                || manifestArgument >= 0 || index + 1 >= restartArguments.Length
                || string.IsNullOrWhiteSpace(restartArguments[index + 1]))
            {
                throw new ArgumentException("--test-session requires one manifest path and may occur only once.");
            }
            manifestArgument = ++index;
        }

        string updateUrl = string.IsNullOrWhiteSpace(cliUrl) ? UpdateCheckService.DefaultManifestUrl : cliUrl;
        var paths = ApplicationPathSnapshot.FromExecutablePath(executablePath);
        ApplicationTestSession? session = null;
        if (manifestArgument >= 0)
        {
            string manifestPath = Path.GetFullPath(restartArguments[manifestArgument]);
            restartArguments[manifestArgument] = manifestPath;
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement root = document.RootElement;
            ValidateObject(root, ["schemaVersion", "profileDirectory", "assetsDirectory", "languageDirectory",
                "tempDirectory", "legacySettingsDirectory", "runId", "caseId", "controlPipeName", "providers", "updateManifestUrl"]);
            if (!root.TryGetProperty("schemaVersion", out JsonElement schema)
                || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version != 1)
            {
                throw new FormatException("The test-session schemaVersion must be 1.");
            }
            string profile = ReadAbsolutePath(root, "profileDirectory");
            paths = ApplicationPathSnapshot.FromLayout(paths.ExecutablePath,
                ReadOptionalPath(root, "assetsDirectory", paths.AssetsDirectory), profile,
                ReadOptionalPath(root, "languageDirectory", Path.Combine(profile, "lang")),
                ReadOptionalPath(root, "tempDirectory", Path.Combine(profile, "temp")),
                ReadOptionalPath(root, "legacySettingsDirectory", Path.Combine(profile, "legacy")));
            session = new ApplicationTestSession(manifestPath, ReadRequiredString(root, "runId"),
                ReadRequiredString(root, "caseId"), ReadRequiredString(root, "controlPipeName"));
            if (root.TryGetProperty("providers", out JsonElement providers))
            {
                ValidateObject(providers, ["fileEnumeration"]);
                if (providers.TryGetProperty("fileEnumeration", out JsonElement provider)
                    && (provider.ValueKind != JsonValueKind.String || provider.GetString() != "file-system"))
                {
                    throw new FormatException("The test-session fileEnumeration provider must be file-system.");
                }
            }
            if (root.TryGetProperty("updateManifestUrl", out _))
            {
                updateUrl = ReadRequiredString(root, "updateManifestUrl");
                if (!Uri.TryCreate(updateUrl, UriKind.Absolute, out Uri? uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new FormatException("The test-session updateManifestUrl must be an absolute HTTP(S) URL.");
                }
            }
        }
        return new ApplicationRuntimeOptions(paths, BmsDatabaseLoadOptions.CaptureEnvironment(), session, updateUrl,
            restartArguments, ApplicationLoggingOptions.Parse(rawLogLevel));
    }

    private static void ValidateObject(JsonElement value, string[] acceptedFields)
    {
        if (value.ValueKind != JsonValueKind.Object) { throw new FormatException("A test-session object is required."); }
        HashSet<string> observed = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!acceptedFields.Contains(property.Name, StringComparer.Ordinal) || !observed.Add(property.Name))
            {
                throw new FormatException("Unknown or duplicate test-session field: " + property.Name);
            }
        }
    }

    private static string ReadRequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new FormatException("A nonempty test-session string is required: " + name);
        }
        return value.GetString() ?? throw new FormatException("A test-session string is required: " + name);
    }

    private static string ReadOptionalPath(JsonElement root, string name, string defaultValue)
        => root.TryGetProperty(name, out _) ? ReadAbsolutePath(root, name) : defaultValue;

    private static string ReadAbsolutePath(JsonElement root, string name)
    {
        string value = ReadRequiredString(root, name);
        if (!Path.IsPathFullyQualified(value)) { throw new FormatException("An absolute test-session path is required: " + name); }
        return Path.GetFullPath(value);
    }
}
