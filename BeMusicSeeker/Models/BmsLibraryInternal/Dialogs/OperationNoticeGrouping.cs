using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BeMusicSeeker.Properties;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 受付の解放後に表示する、OKだけの案内を見出し・重要度ごとにまとめます。
/// 同じ組の案内は一つのダイアログにし、件数と最大 <see cref="MaximumSampleCount"/> 件の標本を示して、
/// 大量の失敗が一件ずつのモーダルにならないようにします。
/// 標本に載らない件が出る組は、まとめる時点で全メンバーの本文を組ごとに一つのログ項目へ残します。
/// 読込み失敗のように、収集した時点で全件をログへ残した案内（<see cref="BMSLibrary.OperationDialogMessage.AllLogged"/>）は重ねて残しません。
/// </summary>
internal static partial class OperationNoticeGrouping
{
    /// <summary>まとめた案内に載せる標本の最大件数です。</summary>
    internal const int MaximumSampleCount = 5;

    private const int MaximumSampleLength = 200;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    /// <summary>
    /// 案内を、見出しと重要度の組が最初に現れた順にまとめます。組の中の標本は入力の順です。
    /// 組が一件だけの案内は、本文・ボタン・既定値を含めて元のまま返します。
    /// 二件以上の組は、件数の行と標本の行で表示時の言語により整形し、標本が全件に満たなければ「- ...」を加えます。
    /// </summary>
    /// <param name="messages">まとめる案内。</param>
    /// <param name="culture">まとめた案内の枠を整形する文化圏。省略時は表示の言語（<see cref="Resources.Culture"/>）です。</param>
    internal static IReadOnlyList<BMSLibrary.OperationDialogMessage> Group(
        IEnumerable<BMSLibrary.OperationDialogMessage> messages, CultureInfo culture = null)
    {
        List<(string Caption, UiDialogIcon Icon, List<BMSLibrary.OperationDialogMessage> Members)> groups = [];
        foreach (BMSLibrary.OperationDialogMessage message in messages ?? [])
        {
            if (message == null)
            {
                continue;
            }
            string caption = message.Caption ?? string.Empty;
            int index = groups.FindIndex(group => group.Icon == message.Icon
                && string.Equals(group.Caption, caption, StringComparison.Ordinal));
            if (index < 0)
            {
                groups.Add((caption, message.Icon, [message]));
            }
            else
            {
                groups[index].Members.Add(message);
            }
        }
        return [.. groups.Select(group => Merge(group.Members, culture))];
    }

    /// <summary>件数の行と標本の行からなる、まとめた案内の本文を整形します。</summary>
    /// <param name="count">案内が表す事象の全件数。</param>
    /// <param name="sampleLines">標本として示す一行の説明。先頭から <see cref="MaximumSampleCount"/> 件だけ使います。</param>
    /// <param name="culture">枠を整形する文化圏。省略時は表示の言語です。</param>
    internal static string FormatGroup(int count, IReadOnlyList<string> sampleLines, CultureInfo culture = null)
    {
        var text = new StringBuilder(string.Format(
            culture ?? CultureInfo.CurrentCulture, Localized(nameof(Resources.Notice_GroupedCount), culture), count));
        int shown = 0;
        foreach (string line in (sampleLines ?? []).Take(MaximumSampleCount))
        {
            text.AppendLine().Append("- ").Append(line);
            shown++;
        }
        if (count > shown)
        {
            text.AppendLine().Append("- ...");
        }
        return text.ToString();
    }

    /// <summary>本文を、標本の一行へ畳みます。改行と連続する空白は一つの空白にし、長い本文は切り詰めます。</summary>
    internal static string ToSampleLine(string text) => ToSampleLine(text, MaximumSampleLength);

    private static string ToSampleLine(string text, int maximumLength)
    {
        string line = WhitespaceRun().Replace(text ?? string.Empty, " ").Trim();
        return line.Length <= maximumLength ? line : line[..(maximumLength - 1)] + "…";
    }

    /// <summary>指定した文化圏、省略時は表示の言語の文言を返します。</summary>
    internal static string Localized(string key, CultureInfo culture)
        => Resources.ResourceManager.GetString(key, culture ?? Resources.Culture);

    /// <summary>表示に載らない件が出る組の全メンバーの本文を、組ごとに一つのログ項目へ残します。ログの失敗は表示へ影響させません。</summary>
    private static void LogOmittedMembers(BMSLibrary.OperationDialogMessage first, List<BMSLibrary.OperationDialogMessage> members)
    {
        try
        {
            List<BMSLibrary.OperationDialogMessage> unlogged = [.. members.Where(member => !member.AllLogged)];
            if (unlogged.Count == 0)
            {
                return;
            }
            StringBuilder text = new StringBuilder("operation_notice_group_omitted caption=").Append(first.Caption)
                .Append(" count=").Append(members.Sum(member => member.Count))
                .Append(" logged=").Append(unlogged.Count);
            foreach (BMSLibrary.OperationDialogMessage member in unlogged)
            {
                text.AppendLine().Append("message=").Append(ToSampleLine(member.MessageBoxText, int.MaxValue));
            }
            NLogWrapper.FileLogger?.Warn(text.ToString());
        }
        catch (Exception)
        {
            // 任意診断の失敗で、通知の表示を止めません。
        }
    }

    private static BMSLibrary.OperationDialogMessage Merge(List<BMSLibrary.OperationDialogMessage> members, CultureInfo culture)
    {
        if (members.Count == 1)
        {
            return members[0];
        }
        BMSLibrary.OperationDialogMessage first = members[0];
        int count = members.Sum(member => member.Count);
        List<string> lines = [.. members.SelectMany(member => member.SampleLines).Take(MaximumSampleCount)];
        if (count > lines.Count)
        {
            LogOmittedMembers(first, members);
        }
        return new BMSLibrary.OperationDialogMessage(
            FormatGroup(count, lines, culture), first.Caption, first.Button, first.Icon, first.DefaultResult, count, lines);
    }
}

/// <summary>保守の読込み失敗の事実から、まとめて表示する案内を作ります。</summary>
internal static class MaintenanceReadFailureNotice
{
    /// <summary>
    /// 失敗がなければnullを返します。一件だけなら従来の個別の案内と同じ本文にし、
    /// 二件以上なら件数と標本の行にします。見出しと本文の型は呼出時の言語で整形し、例外メッセージは捕捉時のままです。
    /// </summary>
    /// <param name="failures">保守が集めた読込み失敗。</param>
    /// <param name="culture">見出しと本文の型を整形する文化圏。省略時は表示の言語です。</param>
    internal static BMSLibrary.OperationDialogMessage Create(MaintenanceReadFailureCollector failures, CultureInfo culture = null)
    {
        int count = failures?.Count ?? 0;
        if (count == 0)
        {
            return null;
        }
        IReadOnlyList<MaintenanceReadFailure> samples = failures.Samples;
        // パスは省略せず、原因だけを一行へ畳んで長さを制限します。
        List<string> lines = [.. samples.Select(sample => string.IsNullOrWhiteSpace(sample.Message)
            ? sample.Path
            : sample.Path + " (" + OperationNoticeGrouping.ToSampleLine(sample.Message) + ")")];
        string text = count == 1 && samples.Count == 1
            ? string.Format(culture ?? CultureInfo.CurrentCulture,
                OperationNoticeGrouping.Localized(nameof(Resources.Error_BmsLoadFailedSkip), culture),
                samples[0].Path, samples[0].Message)
            : OperationNoticeGrouping.FormatGroup(count, lines, culture);
        return new BMSLibrary.OperationDialogMessage(
            text, OperationNoticeGrouping.Localized(nameof(Resources.MessageBoxTitle_Error), culture), UiDialogButton.OK, UiDialogIcon.Hand, UiDialogDefaultResult.OK,
            count, lines, allLogged: true);
    }
}
