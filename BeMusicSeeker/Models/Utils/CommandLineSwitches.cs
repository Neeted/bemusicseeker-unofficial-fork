#nullable enable
using System;
using BeMusicSeeker.Runtime;

namespace BeMusicSeeker.Models.Utils;

public enum NormalLogLevel
{
    Info,
    Warn,
    Error
}

/// <summary>Programが並行処理の開始前に適用したログ条件を、既存の診断入口へ公開します。</summary>
public static class CommandLineSwitches
{
    private static ApplicationLoggingOptions loggingOptions = ApplicationLoggingOptions.Default;

    /// <summary>起動境界で確定した通常ログ水準を取得します。未適用時はInfoです。</summary>
    public static NormalLogLevel LogLevel => loggingOptions.Level;

    /// <summary>Infoの通常診断と性能ログを出力する指定かを取得します。</summary>
    public static bool IsInfoLoggingEnabled => loggingOptions.Level == NormalLogLevel.Info;

    /// <summary>空白以外の不正値があり、従来の警告対象となるかを取得します。</summary>
    public static bool HasInvalidLogLevelValue => !string.IsNullOrWhiteSpace(loggingOptions.InvalidValue);

    /// <summary>不正なログ指定の元文字列を取得します。未指定・有効値では空文字列です。</summary>
    public static string InvalidLogLevelValue => loggingOptions.InvalidValue;

    /// <summary>Programが実行条件の捕捉後、App生成と並行処理の開始前に一度だけログ条件を適用します。</summary>
    /// <param name="options">解析済みで変更不能のログ条件。</param>
    internal static void Configure(ApplicationLoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        loggingOptions = options;
    }
}
