using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BeMusicSeeker.Properties;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Models;

/// <summary>
/// JSON settings の parse failure を分類します。
/// </summary>
internal enum RightClickActionSettingsParseErrorKind
{
    InvalidJson,
    MissingProperty,
    UnknownProperty,
    DuplicateProperty,
    InvalidValue
}

/// <summary>
/// 保存済み right-click settings の parse failure を startup exception に変換せず伝える診断値です。
/// </summary>
internal sealed class RightClickActionSettingsParseError
{
    /// <summary>
    /// parse failure の分類、位置、診断情報を保持します。
    /// </summary>
    internal RightClickActionSettingsParseError(
        RightClickActionSettingsParseErrorKind kind,
        string path,
        string message,
        Exception exception = null)
    {
        Kind = kind;
        Path = path ?? string.Empty;
        Message = message ?? string.Empty;
        Exception = exception;
    }

    /// <summary>
    /// failure の分類を取得します。
    /// </summary>
    internal RightClickActionSettingsParseErrorKind Kind { get; }

    /// <summary>
    /// failure が検出された JSON path を取得します。
    /// </summary>
    internal string Path { get; }

    /// <summary>
    /// fallback せず表示・記録できる診断文を取得します。
    /// </summary>
    internal string Message { get; }

    /// <summary>
    /// parser が提供した元例外を取得します。
    /// </summary>
    internal Exception Exception { get; }

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Path) ? Message : Path + ": " + Message;
    }
}

/// <summary>
/// settings parse の成功値または typed failure を保持します。
/// </summary>
internal sealed class RightClickActionSettingsParseResult
{
    private RightClickActionSettingsParseResult(
        RightClickActionSettings settings,
        bool isMissing,
        bool isExplicitEmpty,
        RightClickActionSettingsParseError error)
    {
        Settings = settings;
        IsMissing = isMissing;
        IsExplicitEmpty = isExplicitEmpty;
        Error = error;
    }

    /// <summary>
    /// 成功時の immutable aggregate を取得します。failure 時は null です。
    /// </summary>
    internal RightClickActionSettings Settings { get; }

    /// <summary>
    /// parse が成功したかどうかを取得します。
    /// </summary>
    internal bool Succeeded => Error == null;

    /// <summary>
    /// 入力が missing-property default として扱われたかを取得します。
    /// </summary>
    internal bool IsMissing { get; }

    /// <summary>
    /// 入力が明示的な empty aggregate として保持されたかを取得します。
    /// </summary>
    internal bool IsExplicitEmpty { get; }

    /// <summary>
    /// failure 診断を取得します。成功時は null です。
    /// </summary>
    internal RightClickActionSettingsParseError Error { get; }

    /// <summary>
    /// 成功 result を生成します。
    /// </summary>
    internal static RightClickActionSettingsParseResult Success(
        RightClickActionSettings settings,
        bool isMissing = false,
        bool isExplicitEmpty = false)
    {
        return new RightClickActionSettingsParseResult(settings, isMissing, isExplicitEmpty, null);
    }

    /// <summary>
    /// typed failure result を生成します。
    /// </summary>
    internal static RightClickActionSettingsParseResult Failure(RightClickActionSettingsParseError error)
    {
        return new RightClickActionSettingsParseResult(null, false, false, error);
    }
}

/// <summary>
/// right-click settings aggregate の JSON serializer/parser です。
/// </summary>
internal static class RightClickActionSettingsSerializer
{
    private static readonly HashSet<string> AggregateProperties = ["webActions", "programActions"];

    private static readonly HashSet<string> WebActionProperties = ["id", "name", "urlTemplate", "enabled", "chartKind"];

    private static readonly HashSet<string> ProgramActionProperties = ["id", "name", "executablePath", "argumentTemplate", "enabled"];

    private static readonly Regex HexRegex = new("^[0-9a-fA-F]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// settings JSON を strict に parse し、failure を typed result として返します。
    /// </summary>
    internal static RightClickActionSettingsParseResult Parse(string json)
    {
        if (json == null)
        {
            return RightClickActionSettingsParseResult.Success(
                RightClickActionSettingsDefaults.Create(),
                isMissing: true);
        }

        if (json.Length == 0)
        {
            return RightClickActionSettingsParseResult.Success(
                RightClickActionSettings.Empty,
                isExplicitEmpty: true);
        }

        JObject root;
        try
        {
            using var reader = new JsonTextReader(new StringReader(json));
            root = JObject.Load(reader, new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
            });

            // JObject.Load stops at the end of the first object. Read once more so a
            // copied or concatenated payload cannot be accepted as a valid aggregate.
            if (reader.Read())
            {
                return RightClickActionSettingsParseResult.Failure(new(
                    RightClickActionSettingsParseErrorKind.InvalidJson,
                    string.Empty,
                    "JSON settings must contain exactly one object."));
            }
        }
        catch (JsonException exception)
        {
            RightClickActionSettingsParseErrorKind kind = exception.Message.Contains(
                "same name",
                StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                ? RightClickActionSettingsParseErrorKind.DuplicateProperty
                : RightClickActionSettingsParseErrorKind.InvalidJson;
            return RightClickActionSettingsParseResult.Failure(new(kind, string.Empty, exception.Message, exception));
        }

        RightClickActionSettingsParseError error = ValidateProperties(root, AggregateProperties, ["webActions", "programActions"], "");
        if (error != null)
        {
            return RightClickActionSettingsParseResult.Failure(error);
        }

        if (root["webActions"] is not JArray webArray)
        {
            return RightClickActionSettingsParseResult.Failure(InvalidValue("webActions", "webActions must be an array."));
        }
        if (root["programActions"] is not JArray programArray)
        {
            return RightClickActionSettingsParseResult.Failure(InvalidValue("programActions", "programActions must be an array."));
        }

        var webActions = new List<RightClickWebActionDefinition>();
        var programActions = new List<RightClickProgramActionDefinition>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < webArray.Count; index++)
        {
            if (webArray[index] is not JObject actionObject)
            {
                return RightClickActionSettingsParseResult.Failure(InvalidValue(
                    "webActions[" + index + "]",
                    "Web action must be an object."));
            }

            RightClickActionSettingsParseError actionError = ValidateProperties(
                actionObject,
                WebActionProperties,
                ["id", "urlTemplate", "enabled", "chartKind"],
                "webActions[" + index + "]");
            if (actionError != null)
            {
                return RightClickActionSettingsParseResult.Failure(actionError);
            }

            if (!TryReadString(actionObject, "id", "webActions[" + index + "].id", out string id, out actionError)
                || string.IsNullOrWhiteSpace(id))
            {
                return RightClickActionSettingsParseResult.Failure(actionError ?? InvalidValue(
                    "webActions[" + index + "].id",
                    "Web action id must be nonblank."));
            }
            if (!ids.Add(id))
            {
                return RightClickActionSettingsParseResult.Failure(new(
                    RightClickActionSettingsParseErrorKind.InvalidValue,
                    "webActions[" + index + "].id",
                    "Duplicate action id: " + id));
            }

            string name = null;
            if (actionObject.TryGetValue("name", StringComparison.Ordinal, out JToken nameToken))
            {
                if (nameToken.Type != JTokenType.Null)
                {
                    if (!TryReadString(actionObject, "name", "webActions[" + index + "].name", out name, out actionError)
                        || string.IsNullOrWhiteSpace(name))
                    {
                        return RightClickActionSettingsParseResult.Failure(actionError ?? InvalidValue(
                            "webActions[" + index + "].name",
                            "Web action name must be null or nonblank."));
                    }
                }
            }
            if (!RightClickActionSettingsDefaults.IsBuiltInId(id) && string.IsNullOrWhiteSpace(name))
            {
                return RightClickActionSettingsParseResult.Failure(InvalidValue(
                    "webActions[" + index + "].name",
                    "Custom web actions require a nonblank name."));
            }

            if (!TryReadString(actionObject, "urlTemplate", "webActions[" + index + "].urlTemplate", out string urlTemplate, out actionError))
            {
                return RightClickActionSettingsParseResult.Failure(actionError);
            }
            if (!TryReadBoolean(actionObject, "enabled", "webActions[" + index + "].enabled", out bool enabled, out actionError))
            {
                return RightClickActionSettingsParseResult.Failure(actionError);
            }
            if (!TryReadChartKind(actionObject, "chartKind", "webActions[" + index + "].chartKind", out ExternalChartKind chartKind, out actionError))
            {
                return RightClickActionSettingsParseResult.Failure(actionError);
            }
            if (!TryValidateUrlTemplate(urlTemplate, "webActions[" + index + "].urlTemplate", out actionError))
            {
                return RightClickActionSettingsParseResult.Failure(actionError);
            }

            webActions.Add(new RightClickWebActionDefinition(id, name, urlTemplate, enabled, chartKind));
        }

        for (int index = 0; index < programArray.Count; index++)
        {
            if (programArray[index] is not JObject actionObject)
            {
                return RightClickActionSettingsParseResult.Failure(InvalidValue(
                    "programActions[" + index + "]",
                    "Program action must be an object."));
            }

            RightClickActionSettingsParseError actionError = ValidateProperties(
                actionObject,
                ProgramActionProperties,
                ["id", "name", "executablePath", "argumentTemplate", "enabled"],
                "programActions[" + index + "]");
            if (actionError != null)
            {
                return RightClickActionSettingsParseResult.Failure(actionError);
            }

            if (!TryReadString(actionObject, "id", "programActions[" + index + "].id", out string id, out actionError)
                || string.IsNullOrWhiteSpace(id))
            {
                return RightClickActionSettingsParseResult.Failure(actionError ?? InvalidValue(
                    "programActions[" + index + "].id",
                    "Program action id must be nonblank."));
            }
            if (!ids.Add(id))
            {
                return RightClickActionSettingsParseResult.Failure(new(
                    RightClickActionSettingsParseErrorKind.InvalidValue,
                    "programActions[" + index + "].id",
                    "Duplicate action id: " + id));
            }

            if (!TryReadString(actionObject, "name", "programActions[" + index + "].name", out string name, out actionError)
                || string.IsNullOrWhiteSpace(name))
            {
                return RightClickActionSettingsParseResult.Failure(actionError ?? InvalidValue(
                    "programActions[" + index + "].name",
                    "Program action name must be nonblank."));
            }
            if (!TryReadString(actionObject, "executablePath", "programActions[" + index + "].executablePath", out string executablePath, out actionError))
            {
                return RightClickActionSettingsParseResult.Failure(actionError);
            }
            if (!Path.IsPathFullyQualified(executablePath))
            {
                return RightClickActionSettingsParseResult.Failure(InvalidValue(
                    "programActions[" + index + "].executablePath",
                    "Executable path must be absolute."));
            }
            if (!TryReadString(actionObject, "argumentTemplate", "programActions[" + index + "].argumentTemplate", out string argumentTemplate, out actionError))
            {
                return RightClickActionSettingsParseResult.Failure(actionError);
            }
            if (!ExternalProgramArgumentTemplate.TryParse(argumentTemplate, out _, out string templateError))
            {
                return RightClickActionSettingsParseResult.Failure(InvalidValue(
                    "programActions[" + index + "].argumentTemplate",
                    templateError));
            }
            if (!TryReadBoolean(actionObject, "enabled", "programActions[" + index + "].enabled", out bool enabled, out actionError))
            {
                return RightClickActionSettingsParseResult.Failure(actionError);
            }

            programActions.Add(new RightClickProgramActionDefinition(id, name, executablePath, argumentTemplate, enabled));
        }

        return RightClickActionSettingsParseResult.Success(new RightClickActionSettings(webActions, programActions));
    }

    /// <summary>
    /// settings JSON を parse し、成功時の aggregate を返します。
    /// </summary>
    internal static bool TryParse(
        string json,
        out RightClickActionSettings settings,
        out RightClickActionSettingsParseError error)
    {
        RightClickActionSettingsParseResult result = Parse(json);
        settings = result.Settings;
        error = result.Error;
        return result.Succeeded;
    }

    /// <summary>
    /// aggregate を canonical JSON に serialize します。
    /// </summary>
    internal static string Serialize(RightClickActionSettings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        RightClickActionSettingsParseResult validation = Parse(SerializeUnchecked(settings));
        if (!validation.Succeeded)
        {
            throw new ArgumentException(validation.Error.ToString(), nameof(settings));
        }
        return SerializeUnchecked(settings);
    }

    /// <summary>
    /// aggregate を検証してから canonical JSON に serialize します。
    /// </summary>
    internal static bool TrySerialize(
        RightClickActionSettings settings,
        out string json,
        out RightClickActionSettingsParseError error)
    {
        json = null;
        error = null;
        if (settings == null)
        {
            error = InvalidValue(string.Empty, "Settings aggregate is required.");
            return false;
        }

        string candidate = SerializeUnchecked(settings);
        RightClickActionSettingsParseResult validation = Parse(candidate);
        if (!validation.Succeeded)
        {
            error = validation.Error;
            return false;
        }

        json = candidate;
        return true;
    }

    private static string SerializeUnchecked(RightClickActionSettings settings)
    {
        var root = new JObject
        {
            ["webActions"] = new JArray(settings.WebActions.Select(SerializeWebAction)),
            ["programActions"] = new JArray(settings.ProgramActions.Select(SerializeProgramAction))
        };
        return root.ToString(Formatting.None);
    }

    private static JObject SerializeWebAction(RightClickWebActionDefinition action)
    {
        if (action == null)
        {
            return null;
        }
        var result = new JObject
        {
            ["id"] = action.Id,
            ["name"] = action.Name == null ? JValue.CreateNull() : new JValue(action.Name),
            ["urlTemplate"] = action.UrlTemplate,
            ["enabled"] = action.Enabled,
            ["chartKind"] = action.ChartKind.ToString()
        };
        return result;
    }

    private static JObject SerializeProgramAction(RightClickProgramActionDefinition action)
    {
        if (action == null)
        {
            return null;
        }
        return new JObject
        {
            ["id"] = action.Id,
            ["name"] = action.Name,
            ["executablePath"] = action.ExecutablePath,
            ["argumentTemplate"] = action.ArgumentTemplate,
            ["enabled"] = action.Enabled
        };
    }

    private static RightClickActionSettingsParseError ValidateProperties(
        JObject value,
        ISet<string> allowed,
        IReadOnlyList<string> required,
        string path)
    {
        foreach (JProperty property in value.Properties())
        {
            if (!allowed.Contains(property.Name))
            {
                return new(
                    RightClickActionSettingsParseErrorKind.UnknownProperty,
                    CombinePath(path, property.Name),
                    "Unknown JSON member: " + property.Name);
            }
        }
        foreach (string requiredProperty in required)
        {
            if (value.Property(requiredProperty, StringComparison.Ordinal) == null)
            {
                return new(
                    RightClickActionSettingsParseErrorKind.MissingProperty,
                    CombinePath(path, requiredProperty),
                    "Missing JSON member: " + requiredProperty);
            }
        }
        return null;
    }

    private static bool TryReadString(
        JObject value,
        string propertyName,
        string path,
        out string result,
        out RightClickActionSettingsParseError error)
    {
        JToken token = value[propertyName];
        if (token?.Type != JTokenType.String)
        {
            result = null;
            error = InvalidValue(path, "Value must be a string.");
            return false;
        }
        result = token.Value<string>();
        error = null;
        return true;
    }

    private static bool TryReadBoolean(
        JObject value,
        string propertyName,
        string path,
        out bool result,
        out RightClickActionSettingsParseError error)
    {
        JToken token = value[propertyName];
        if (token?.Type != JTokenType.Boolean)
        {
            result = false;
            error = InvalidValue(path, "Value must be a boolean.");
            return false;
        }
        result = token.Value<bool>();
        error = null;
        return true;
    }

    private static bool TryReadChartKind(
        JObject value,
        string propertyName,
        string path,
        out ExternalChartKind result,
        out RightClickActionSettingsParseError error)
    {
        if (!TryReadString(value, propertyName, path, out string serialized, out error))
        {
            result = default;
            return false;
        }

        switch (serialized)
        {
            case nameof(ExternalChartKind.BmsOnly):
                result = ExternalChartKind.BmsOnly;
                break;
            case nameof(ExternalChartKind.BmsonOnly):
                result = ExternalChartKind.BmsonOnly;
                break;
            case nameof(ExternalChartKind.All):
                result = ExternalChartKind.All;
                break;
            default:
                result = default;
                error = InvalidValue(path, "Chart kind must be BmsOnly, BmsonOnly, or All.");
                return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateUrlTemplate(
        string template,
        string path,
        out RightClickActionSettingsParseError error)
    {
        error = null;
        if (template == null || !Uri.TryCreate(template, UriKind.Absolute, out Uri uri)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !(string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            error = InvalidValue(path, "URL template must be an absolute http/https URL.");
            return false;
        }

        int md5Count = 0;
        int sha256Count = 0;
        for (int index = 0; index < template.Length; index++)
        {
            char current = template[index];
            if (current == '}')
            {
                error = InvalidValue(path, "URL template contains an unbalanced placeholder.");
                return false;
            }
            if (current != '{')
            {
                continue;
            }

            int closeIndex = template.IndexOf('}', index + 1);
            if (closeIndex < 0)
            {
                error = InvalidValue(path, "URL template contains an unbalanced placeholder.");
                return false;
            }
            string placeholder = template.Substring(index + 1, closeIndex - index - 1);
            if (string.Equals(placeholder, "md5", StringComparison.Ordinal))
            {
                md5Count++;
            }
            else if (string.Equals(placeholder, "sha256", StringComparison.Ordinal))
            {
                sha256Count++;
            }
            else
            {
                error = InvalidValue(path, "URL template contains an unknown placeholder: " + placeholder);
                return false;
            }
            index = closeIndex;
        }

        if (md5Count == 0 && sha256Count == 0)
        {
            error = InvalidValue(path, "URL template must contain {md5} or {sha256}.");
            return false;
        }
        return true;
    }

    private static bool IsValidHex(string value, int length)
    {
        return value?.Length == length && HexRegex.IsMatch(value);
    }

    /// <summary>
    /// 32 桁 hex MD5 として使えるかを判定します。
    /// </summary>
    internal static bool IsValidMd5(string value) => IsValidHex(value, 32);

    /// <summary>
    /// 64 桁 hex SHA-256 として使えるかを判定します。
    /// </summary>
    internal static bool IsValidSha256(string value) => IsValidHex(value, 64);

    private static RightClickActionSettingsParseError InvalidValue(string path, string message)
    {
        return new(RightClickActionSettingsParseErrorKind.InvalidValue, path, message);
    }

    private static string CombinePath(string path, string propertyName)
    {
        return string.IsNullOrWhiteSpace(path) ? propertyName : path + "." + propertyName;
    }
}

/// <summary>
/// right-click settings を既存 portable user.config へ接続する owner です。
/// </summary>
internal sealed class RightClickActionSettingsStore
{
    private readonly Func<Settings> settingsProvider;

    /// <summary>
    /// settings provider を指定して store を構築します。
    /// </summary>
    internal RightClickActionSettingsStore(Func<Settings> settingsProvider = null)
    {
        this.settingsProvider = settingsProvider ?? (() => Settings.Default);
    }

    /// <summary>
    /// user.config の現在値を typed result として読み取ります。invalid は default へ fallback しません。
    /// </summary>
    internal RightClickActionSettingsParseResult Load()
    {
        return RightClickActionSettingsSerializer.Parse(settingsProvider().RightClickActionsJson);
    }

    /// <summary>
    /// 有効な aggregate を一つの JSON 値として settings に書き込みます。
    /// </summary>
    internal bool TrySave(
        RightClickActionSettings settings,
        out RightClickActionSettingsParseError error)
    {
        if (!RightClickActionSettingsSerializer.TrySerialize(settings, out string json, out error))
        {
            return false;
        }
        settingsProvider().RightClickActionsJson = json;
        return true;
    }

    /// <summary>
    /// aggregate を settings に書き込み、既存 settings provider の Save を実行します。
    /// </summary>
    internal bool TrySaveAndPersist(
        RightClickActionSettings settings,
        out RightClickActionSettingsParseError error)
    {
        if (!RightClickActionSettingsSerializer.TrySerialize(settings, out string json, out error))
        {
            return false;
        }

        Settings persistedSettings = settingsProvider();
        persistedSettings.RightClickActionsJson = json;
        persistedSettings.Save();
        return true;
    }
}
