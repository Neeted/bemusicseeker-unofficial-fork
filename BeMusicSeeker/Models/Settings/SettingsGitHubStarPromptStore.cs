#nullable enable
using System;
using System.Globalization;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

/// <summary>GitHubのスター依頼の保存値を読み書きする窓口です。</summary>
internal interface IGitHubStarPromptStore
{
    /// <summary>保存値を読みます。読取りの失敗は例外で返します。</summary>
    GitHubStarPromptRecord Read();

    /// <summary>保存値を保存します。保存の失敗は例外で返し、確定値を変えません。</summary>
    void Save(GitHubStarPromptRecord record);
}

/// <summary>
/// GitHubのスター依頼の4項目を <c>config/user.config</c> へ接続します。
/// </summary>
/// <remarks>
/// 予定はOSの地域設定に依存しないよう、InvariantCultureの往復形式（O）のUTC文字列で保存します。
/// 状態の未知の値は終了として読み、保存を拒否して新しい版が書いた値を残します。
/// </remarks>
internal sealed class SettingsGitHubStarPromptStore : IGitHubStarPromptStore
{
    private const string StarredValue = "Starred";

    private const string DeclinedValue = "Declined";

    private readonly Func<Settings> settingsProvider;

    /// <summary>構成が共有する設定を読む関数から窓口を作ります。</summary>
    /// <param name="settingsProvider">現在の確定設定を返します。</param>
    internal SettingsGitHubStarPromptStore(Func<Settings> settingsProvider)
    {
        this.settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
    }

    /// <inheritdoc />
    public GitHubStarPromptRecord Read()
    {
        Settings settings = settingsProvider();
        return new GitHubStarPromptRecord(
            ParseState(settings.GitHubStarPromptState),
            Math.Max(0, settings.GitHubStarPromptStartupCount),
            ParseUtc(settings.GitHubStarPromptNextEligibleUtc),
            Math.Max(0, settings.GitHubStarPromptShownCount));
    }

    /// <inheritdoc />
    public void Save(GitHubStarPromptRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        string state = record.State switch
        {
            GitHubStarPromptState.Unfinished => string.Empty,
            GitHubStarPromptState.Starred => StarredValue,
            GitHubStarPromptState.Declined => DeclinedValue,
            _ => throw new InvalidOperationException("An unknown GitHub star prompt state must not be overwritten.")
        };
        string nextEligible = record.NextEligibleUtc is { } value
            ? value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
            : string.Empty;
        settingsProvider().Commit(
            (nameof(Settings.GitHubStarPromptState), state),
            (nameof(Settings.GitHubStarPromptStartupCount), record.StartupCount),
            (nameof(Settings.GitHubStarPromptNextEligibleUtc), nextEligible),
            (nameof(Settings.GitHubStarPromptShownCount), record.ShownCount));
    }

    private static GitHubStarPromptState ParseState(string? value)
    {
        return value switch
        {
            null or "" => GitHubStarPromptState.Unfinished,
            StarredValue => GitHubStarPromptState.Starred,
            DeclinedValue => GitHubStarPromptState.Declined,
            _ => GitHubStarPromptState.Unknown
        };
    }

    private static DateTimeOffset? ParseUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
        {
            return null;
        }

        return parsed.ToUniversalTime();
    }
}
