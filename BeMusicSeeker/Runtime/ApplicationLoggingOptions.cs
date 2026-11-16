#nullable enable
using System;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Runtime;

/// <summary>起動引数から純粋に解決した通常ログ水準と、診断に使う未加工の不正値を変更不能に保持します。</summary>
internal sealed class ApplicationLoggingOptions
{
    private ApplicationLoggingOptions(NormalLogLevel level, string invalidValue)
    {
        Level = level;
        InvalidValue = invalidValue;
    }

    /// <summary>ログ指定がない場合のInfo水準と空の不正値を取得します。</summary>
    internal static ApplicationLoggingOptions Default { get; } = new(NormalLogLevel.Info, string.Empty);

    /// <summary>解決済みの通常ログ水準を取得します。</summary>
    internal NormalLogLevel Level { get; }

    /// <summary>不正な指定の元文字列を取得します。未指定・有効値では空文字列です。</summary>
    internal string InvalidValue { get; }

    /// <summary>指定値を環境や共有状態に触れず解釈します。大小文字を区別せず、前後の空白は除去しません。</summary>
    /// <param name="raw">最後に指定された未加工の値。nullは未指定、空値・空白を含む未知の値はWarnです。</param>
    /// <returns>未指定はInfo、有効値はその水準、不正値はWarnと元の値を保持する起動条件。</returns>
    internal static ApplicationLoggingOptions Parse(string? raw)
    {
        if (raw is null || string.Equals(raw, "info", StringComparison.OrdinalIgnoreCase))
        {
            return Default;
        }
        if (string.Equals(raw, "warn", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "warning", StringComparison.OrdinalIgnoreCase))
        {
            return new ApplicationLoggingOptions(NormalLogLevel.Warn, string.Empty);
        }
        if (string.Equals(raw, "error", StringComparison.OrdinalIgnoreCase))
        {
            return new ApplicationLoggingOptions(NormalLogLevel.Error, string.Empty);
        }
        return new ApplicationLoggingOptions(NormalLogLevel.Warn, raw);
    }
}
