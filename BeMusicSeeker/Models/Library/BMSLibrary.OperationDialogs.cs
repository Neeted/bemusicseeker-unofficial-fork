using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
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

    private MessageBoxResult ShowOperationDialog(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult = MessageBoxResult.None) =>
        scopedOperationDialogService.Show(messageBoxText, caption, button, icon, defaultResult);
}
