using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary
{
    /// <summary>受付の解放後に表示する、OKだけの案内です。同じ見出し・重要度の案内は表示時に一つへまとめます。</summary>
    internal sealed class OperationDialogMessage
    {
        /// <param name="count">この案内が表す事象の件数。大量の事象を標本だけで持つ場合に、全件数を保持します。</param>
        /// <param name="sampleLines">まとめた表示で標本として使う一行の説明。省略時は本文を一行へ畳んだ文字列を使います。</param>
        /// <param name="allLogged">全件の内容を、案内を作った側がすでにログへ残したか。真なら、まとめの表示で省いた件を重ねてログへ残しません。</param>
        internal OperationDialogMessage(
            string messageBoxText,
            string caption,
            MessageBoxButton button,
            MessageBoxImage icon,
            MessageBoxResult defaultResult,
            int count = 1,
            IReadOnlyList<string> sampleLines = null,
            bool allLogged = false)
        {
            MessageBoxText = messageBoxText;
            Caption = caption;
            Button = button;
            Icon = icon;
            DefaultResult = defaultResult;
            Count = Math.Max(1, count);
            SampleLines = sampleLines ?? [OperationNoticeGrouping.ToSampleLine(messageBoxText)];
            AllLogged = allLogged;
        }

        internal bool AllLogged { get; }

        internal int Count { get; }

        internal IReadOnlyList<string> SampleLines { get; }

        internal string MessageBoxText { get; }

        internal string Caption { get; }

        internal MessageBoxButton Button { get; }

        internal MessageBoxImage Icon { get; }

        internal MessageBoxResult DefaultResult { get; }
    }

    internal sealed class OperationDialogScope : IDisposable
    {
        private readonly ScopedOperationDialogCoordinator.Scope coordinatorScope;

        internal OperationDialogScope(ScopedOperationDialogCoordinator.Scope coordinatorScope)
        {
            this.coordinatorScope = coordinatorScope
                ?? throw new ArgumentNullException(nameof(coordinatorScope));
        }

        internal IReadOnlyList<OperationDialogMessage> Messages => [.. coordinatorScope.Messages.Select(message =>
            new OperationDialogMessage(
                message.MessageBoxText,
                message.Caption,
                message.Button,
                message.Icon,
                message.DefaultResult,
                message.Count,
                message.SampleLines,
                message.AllLogged))];

        internal void Enqueue(OperationDialogMessage message)
        {
            if (message == null)
            {
                return;
            }
            coordinatorScope.Enqueue(new ScopedOperationDialogCoordinator.DialogMessage(
                message.MessageBoxText,
                message.Caption,
                message.Button,
                message.Icon,
                message.DefaultResult,
                message.Count,
                message.SampleLines,
                message.AllLogged));
        }

        internal void Flush() => coordinatorScope.Flush();

        public void Dispose() => coordinatorScope.Dispose();
    }

    internal OperationDialogScope BeginOperationDialogScope() =>
        new(scopedOperationDialogService.BeginScope());

    /// <summary>
    /// 件数と標本を持つOKだけの案内を、操作の案内の列があれば積み、なければ本文をそのまま表示します。
    /// 保守の読込み失敗のように大量になりうる事象を、一件ずつの表示にせず見出しごとにまとめるために使います。
    /// </summary>
    private void NotifyOperationMessage(OperationDialogMessage message)
    {
        if (message != null)
        {
            scopedOperationDialogService.Notify(message);
        }
    }

    /// <summary>
    /// 保守が集めた読込み失敗を、保守の書込み保護の外で、成功と例外のどちらの経路でも案内へ渡します。
    /// 案内の失敗は保守の結果へ混ぜず、診断だけに残します。
    /// </summary>
    private void NotifyMaintenanceReadFailures(MaintenanceReadFailureCollector failures)
    {
        try
        {
            NotifyOperationMessage(MaintenanceReadFailureNotice.Create(failures));
        }
        catch (Exception exception)
        {
            try { Ribbit.Logging.NLogWrapper.FileLogger?.Warn(exception, "maintenance_read_failure_notice_failed"); }
            catch (Exception) { /* 任意診断の失敗で、保守の結果を変えません。 */ }
        }
    }

    /// <summary>OKだけの警告の案内を、本文・見出し・アイコンと既定値を固定して作ります。</summary>
    private static OperationDialogMessage CreateWarningNotice(string text) =>
        new(text, Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);

    /// <summary>
    /// モデルが根の背景処理と受付の外の経路から、OKだけの案内を待たずに投函します。
    /// 終了の要求の後と、<paramref name="shouldSuppress"/> が真の通知は表示せず、診断だけに残します。
    /// 同じ判定を表示の開始の時点でも窓口が行い、依頼から表示までの間に始まった終了も止めます。
    /// 窓口の失敗は呼出し元の結果を変えず、診断へ残します。投函の呼出しは受付・書込み保護を保持したまま待ちません。
    /// </summary>
    /// <param name="messages">投函する案内。空では何もしません。</param>
    /// <param name="shouldSuppress">失効した世代など、終了以外の抑止の判定。</param>
    private void PostOperationNotices(IReadOnlyList<OperationDialogMessage> messages, Func<bool> shouldSuppress = null)
    {
        if (messages == null || messages.Count == 0)
        {
            return;
        }
        bool Suppressed() => IsShutdownRequested || shouldSuppress?.Invoke() == true;
        try
        {
            if (Suppressed())
            {
                Ribbit.Logging.NLogWrapper.FileLogger?.Warn("bms_library_notice_post_skipped reason=" + (IsShutdownRequested ? "shutdown_requested" : "suppressed")
                    + " count=" + messages.Count);
                return;
            }
            dialogService.Post(messages, Suppressed);
        }
        catch (Exception exception)
        {
            try { Ribbit.Logging.NLogWrapper.FileLogger?.Warn(exception, "bms_library_notice_post_failed"); }
            catch (Exception) { /* 任意診断の失敗で、呼出し元の結果を変えません。 */ }
        }
    }

    /// <summary>自有の公開入口が、共通受付のBusyを待たずに案内します。</summary>
    private void PostLibraryOperationBusyNotice() =>
        PostOperationNotices([new(Resources.Warn_LibraryOperationBusy, Resources.Warning,
            MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.None)]);

    /// <summary>
    /// 保守が集めた読込み失敗を、保守の受付を解放した後に一度だけまとめて投函します。
    /// 成功と例外のどちらの経路でも呼び、案内の失敗は保守の結果へ混ぜず診断だけに残します。
    /// </summary>
    private void PostMaintenanceReadFailures(MaintenanceReadFailureCollector failures)
    {
        try
        {
            OperationDialogMessage notice = MaintenanceReadFailureNotice.Create(failures);
            if (notice != null)
            {
                PostOperationNotices([notice]);
            }
        }
        catch (Exception exception)
        {
            try { Ribbit.Logging.NLogWrapper.FileLogger?.Warn(exception, "maintenance_read_failure_notice_failed"); }
            catch (Exception) { /* 任意診断の失敗で、保守の結果を変えません。 */ }
        }
    }

    private MessageBoxResult ShowOperationDialog(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult = MessageBoxResult.None) =>
        scopedOperationDialogService.Show(messageBoxText, caption, button, icon, defaultResult);
}
