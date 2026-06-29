using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ribbit.Net;

namespace BeMusicSeeker.Models.Update;

internal sealed class UpdateCheckService
{
    internal const int CurrentUpdaterProtocolVersion = 1;
    internal const string DefaultManifestUrl = "https://raw.githubusercontent.com/Neeted/bemusicseeker-unofficial-fork/main/update.json";

    private static readonly Regex Sha256Regex = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    private readonly AppHttpClient httpClient;

    public UpdateCheckService(AppHttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<UpdateCheckResult> CheckAsync(string manifestUrlOverride)
    {
        string currentVersionText = GetCurrentVersionText();
        Version currentVersion = Version.TryParse(currentVersionText, out Version parsedCurrentVersion) ? parsedCurrentVersion : new Version(0, 0, 0, 0);
        Uri manifestUri = CreateCacheBustedUri(!string.IsNullOrWhiteSpace(manifestUrlOverride) ? manifestUrlOverride : DefaultManifestUrl);

        string manifestJson = await httpClient.GetStringAsync(manifestUri, Encoding.UTF8).ConfigureAwait(false);
        UpdateManifest manifest = ParseManifest(manifestJson);
        return manifest.Version > currentVersion
            ? UpdateCheckResult.Available(manifest.Version, currentVersionText, manifest.VersionText, manifest.Assets, manifest.ReleasePageUrl)
            : UpdateCheckResult.NoUpdate(currentVersionText);
    }

    internal static UpdateManifest ParseManifest(string manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            throw new UpdateManifestValidationException("update.json is empty.");
        }

        JObject root;
        try
        {
            root = JObject.Parse(manifestJson);
        }
        catch (Exception ex)
        {
            throw new UpdateManifestValidationException("update.json is not valid JSON: " + ex.Message);
        }

        int schemaVersion = ReadRequiredInt(root, "schemaVersion");
        if (schemaVersion != 1)
        {
            throw new UpdateManifestValidationException("Unsupported schemaVersion: " + schemaVersion);
        }

        string versionText = ReadRequiredString(root, "version");
        if (!Version.TryParse(versionText, out Version version))
        {
            throw new UpdateManifestValidationException("Invalid version: " + versionText);
        }

        string releaseTag = ReadRequiredString(root, "releaseTag");
        if (!string.Equals(releaseTag, "v" + versionText, StringComparison.Ordinal))
        {
            throw new UpdateManifestValidationException("releaseTag does not match version.");
        }

        string releasePageUrl = ReadRequiredAbsoluteHttpUrl(root, "releasePageUrl");
        int packageFormatVersion = ReadRequiredInt(root, "packageFormatVersion");
        if (packageFormatVersion != 1)
        {
            throw new UpdateManifestValidationException("Unsupported packageFormatVersion: " + packageFormatVersion);
        }

        string minimumUpdaterVersion = ReadRequiredString(root, "minimumUpdaterVersion");
        if (!int.TryParse(minimumUpdaterVersion, out int minimumUpdaterProtocolVersion) || minimumUpdaterProtocolVersion > CurrentUpdaterProtocolVersion)
        {
            throw new UpdateManifestValidationException("Unsupported minimumUpdaterVersion: " + minimumUpdaterVersion);
        }

        if (root["assets"] is not JArray assetsArray || assetsArray.Count == 0)
        {
            throw new UpdateManifestValidationException("assets must contain at least one package.");
        }

        List<UpdateAssetInfo> assets = [.. assetsArray.Select(ParseAsset)];
        if (!assets.Any(asset => string.Equals(asset.Kind, "app", StringComparison.Ordinal)))
        {
            throw new UpdateManifestValidationException("assets must contain an app package.");
        }

        return new UpdateManifest
        {
            SchemaVersion = schemaVersion,
            VersionText = versionText,
            Version = version,
            ReleaseTag = releaseTag,
            ReleasePageUrl = releasePageUrl,
            PackageFormatVersion = packageFormatVersion,
            PublishedAt = ReadOptionalString(root, "publishedAt"),
            MinimumUpdaterVersion = minimumUpdaterVersion,
            Assets = assets
        };
    }

    private static UpdateAssetInfo ParseAsset(JToken token)
    {
        if (token is not JObject asset)
        {
            throw new UpdateManifestValidationException("asset must be an object.");
        }

        string kind = ReadRequiredString(asset, "kind");
        if (!string.Equals(kind, "app", StringComparison.Ordinal) && !string.Equals(kind, "app-with-metadata", StringComparison.Ordinal))
        {
            throw new UpdateManifestValidationException("Unsupported asset kind: " + kind);
        }

        string sha256 = ReadRequiredString(asset, "sha256");
        if (!Sha256Regex.IsMatch(sha256))
        {
            throw new UpdateManifestValidationException("Invalid asset sha256.");
        }

        long sizeBytes = ReadRequiredLong(asset, "sizeBytes");
        if (sizeBytes <= 0)
        {
            throw new UpdateManifestValidationException("asset sizeBytes must be positive.");
        }

        return new UpdateAssetInfo
        {
            Kind = kind,
            Label = ReadRequiredString(asset, "label"),
            FileName = ReadRequiredString(asset, "fileName"),
            Url = ReadRequiredAbsoluteHttpUrl(asset, "url"),
            Sha256 = sha256.ToLowerInvariant(),
            SizeBytes = sizeBytes,
            IncludesChartInfoMetadata = ReadRequiredBool(asset, "includesChartInfoMetadata")
        };
    }

    private static string GetCurrentVersionText()
    {
        return Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .OfType<AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "0.0.0.0";
    }

    private static Uri CreateCacheBustedUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
        {
            throw new UpdateManifestValidationException("URL must be absolute: " + url);
        }

        string separator = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
        return new Uri(uri.AbsoluteUri + separator + "t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private static string ReadRequiredAbsoluteHttpUrl(JObject obj, string propertyName)
    {
        string value = ReadRequiredString(obj, propertyName);
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new UpdateManifestValidationException(propertyName + " must be an absolute HTTP URL.");
        }
        return value;
    }

    private static string ReadRequiredString(JObject obj, string propertyName)
    {
        string value = ReadOptionalString(obj, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new UpdateManifestValidationException(propertyName + " is required.");
        }
        return value;
    }

    private static string ReadOptionalString(JObject obj, string propertyName)
    {
        return obj[propertyName]?.Type == JTokenType.String ? (string)obj[propertyName] : null;
    }

    private static int ReadRequiredInt(JObject obj, string propertyName)
    {
        if (obj[propertyName]?.Type != JTokenType.Integer)
        {
            throw new UpdateManifestValidationException(propertyName + " must be an integer.");
        }
        return (int)obj[propertyName];
    }

    private static long ReadRequiredLong(JObject obj, string propertyName)
    {
        if (obj[propertyName]?.Type != JTokenType.Integer)
        {
            throw new UpdateManifestValidationException(propertyName + " must be an integer.");
        }
        return (long)obj[propertyName];
    }

    private static bool ReadRequiredBool(JObject obj, string propertyName)
    {
        if (obj[propertyName]?.Type != JTokenType.Boolean)
        {
            throw new UpdateManifestValidationException(propertyName + " must be a boolean.");
        }
        return (bool)obj[propertyName];
    }
}
