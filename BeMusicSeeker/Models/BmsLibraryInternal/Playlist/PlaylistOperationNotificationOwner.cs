using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Properties;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// プレイリスト操作中に発生した通知事実を収集し、presentation 境界へ receipt として渡します。
/// </summary>
internal sealed class PlaylistOperationNotificationOwner
{
    private readonly AsyncLocal<OperationNotificationSession> currentSession = new();

    internal enum OperationNotificationSeverity
    {
        Information,
        Warning,
        Error
    }

    /// <summary>
    /// 通知の本文を、翻訳前の事実（資源名・生の書式引数・生の詳細）として保持します。
    /// 操作中に確定した通知を後で初めて表示するとき、表示直前の選択言語で整形するために、翻訳済み文字列を保持しません。
    /// 書式引数と詳細（例外本文など）は翻訳対象ではなく、生成時点の値のまま渡します。
    /// </summary>
    internal sealed class OperationNotificationText
    {
        private readonly string resourceName;

        private readonly ImmutableArray<string> arguments;

        private readonly string detail;

        private readonly int detailSeparatorLineBreaks;

        private OperationNotificationText(string resourceName, ImmutableArray<string> arguments, string detail, int detailSeparatorLineBreaks)
        {
            this.resourceName = resourceName;
            this.arguments = arguments.IsDefault ? [] : arguments;
            this.detail = detail;
            this.detailSeparatorLineBreaks = detailSeparatorLineBreaks;
        }

        /// <summary>資源名（<c>nameof(Resources.X)</c>）と生の書式引数から作ります。引数なしの資源は書式化せずそのまま使います。</summary>
        internal static OperationNotificationText FromResource(string resourceName, params string[] arguments)
        {
            ArgumentException.ThrowIfNullOrEmpty(resourceName);
            return new OperationNotificationText(resourceName, arguments == null ? [] : [.. arguments], null, 0);
        }

        /// <summary>資源の文の後に、改行を<paramref name="detailSeparatorLineBreaks"/>個挟んで生の詳細（例外本文など）を続けます。</summary>
        internal static OperationNotificationText FromResourceWithDetail(string resourceName, string detail, int detailSeparatorLineBreaks)
        {
            ArgumentException.ThrowIfNullOrEmpty(resourceName);
            ArgumentOutOfRangeException.ThrowIfNegative(detailSeparatorLineBreaks);
            return new OperationNotificationText(resourceName, [], detail ?? string.Empty, detailSeparatorLineBreaks);
        }

        /// <summary>現在の選択言語（<see cref="Resources.Culture"/>）で本文を整形します。通知は表示直前に呼び、例外本文に使う場合は例外を作る時点に呼びます。</summary>
        internal string Format()
        {
            string pattern = Resources.ResourceManager.GetString(resourceName, Resources.Culture)
                ?? throw new InvalidOperationException("Resource is not found: " + resourceName);
            string text = arguments.IsEmpty ? pattern : string.Format(pattern, arguments.Select(argument => (object)argument).ToArray());
            if (detail == null)
            {
                return text;
            }
            return text + string.Concat(Enumerable.Repeat(Environment.NewLine, detailSeparatorLineBreaks)) + detail;
        }
    }

    /// <summary>
    /// 操作中に確定した通知の事実です。本文と見出しは表示直前に <see cref="FormatMessage"/>／<see cref="FormatCaption"/> で整形します。
    /// </summary>
    internal sealed class OperationNotification
    {
        internal OperationNotification(
            OperationNotificationText message,
            string captionResourceName,
            OperationNotificationSeverity severity,
            bool isBmtOutputFailure = false,
            bool isStoppedOutputsWarning = false)
        {
            Text = message ?? throw new ArgumentNullException(nameof(message));
            CaptionResourceName = captionResourceName ?? throw new ArgumentNullException(nameof(captionResourceName));
            Severity = severity;
            IsBmtOutputFailure = isBmtOutputFailure;
            IsStoppedOutputsWarning = isStoppedOutputsWarning;
        }

        /// <summary>
        /// 出力を止めた表の一覧の警告か。同じ見出し・重要度の他の通知とまとめず、本文を省略せず別の一件として表示します。
        /// 操作自身の失敗とは別の事実なので、失敗の通知を補う判定では BMT 出力の失敗と同じく数えません。
        /// </summary>
        internal bool IsStoppedOutputsWarning { get; }

        /// <summary>
        /// BMT 出力の失敗を知らせる通知か。操作自身の失敗の通知とは別の事実なので、
        /// 「警告・エラーが集まっていないときだけ操作の失敗の通知を補う」判定では数えません。
        /// </summary>
        internal bool IsBmtOutputFailure { get; }

        /// <summary>翻訳前の事実として保持した本文です。文字列は <see cref="FormatMessage"/> で表示直前の選択言語により作ります。</summary>
        internal OperationNotificationText Text { get; }

        /// <summary>見出しの資源名（<c>nameof(Resources.X)</c>）です。積んだ時点の言語では解決せず、<see cref="FormatCaption"/> が表示直前の選択言語で解決します。</summary>
        internal string CaptionResourceName { get; }

        internal OperationNotificationSeverity Severity { get; }

        /// <summary>本文を、呼出し時点の選択言語（<see cref="Resources.Culture"/>）で整形します。通知を初めて表示する直前に呼び、積んだ時点の言語を使わない契約です。</summary>
        internal string FormatMessage() => Text.Format();

        /// <summary>見出しを、呼出し時点の選択言語（<see cref="Resources.Culture"/>）で解決します。通知を初めて表示する直前に呼び、積んだ時点の言語を使わない契約です。</summary>
        internal string FormatCaption()
        {
            return Resources.ResourceManager.GetString(CaptionResourceName, Resources.Culture)
                ?? throw new InvalidOperationException("Resource is not found: " + CaptionResourceName);
        }
    }

    internal sealed class OperationNotificationReceipt
    {
        private readonly IReadOnlyList<OperationNotification> notifications;

        private OperationNotificationReceipt(IReadOnlyList<OperationNotification> notifications)
        {
            this.notifications = notifications;
        }

        internal IReadOnlyList<OperationNotification> Notifications => notifications;

        internal bool IsEmpty => notifications.Count == 0;

        internal static OperationNotificationReceipt Create(IEnumerable<OperationNotification> source)
        {
            List<OperationNotification> copy = [];
            if (source != null)
            {
                foreach (OperationNotification notification in source)
                {
                    if (notification != null)
                    {
                        copy.Add(notification);
                    }
                }
            }
            return new OperationNotificationReceipt(copy.AsReadOnly());
        }
    }

    internal sealed class OperationNotificationSession : IDisposable
    {
        private readonly PlaylistOperationNotificationOwner owner;

        private readonly object gate = new();

        private readonly OperationNotificationSession parent;

        private readonly List<OperationNotification> notifications = [];

        private readonly List<PlaylistStoppedOutput> stoppedOutputs = [];

        private bool warnStoppedOutputs;

        private bool disposed;

        internal OperationNotificationSession(PlaylistOperationNotificationOwner owner)
        {
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
            parent = owner.currentSession.Value;
            owner.currentSession.Value = this;
        }

        /// <summary>
        /// この操作を、出力を止めた表を警告として利用者へ案内する明示操作として扱います。
        /// 明示操作でない出力（項目の変更に伴う出力、表の削除、自動出力）は設定せず、止めた事実を診断ログだけに残します。
        /// </summary>
        internal void WarnStoppedOutputs()
        {
            lock (gate)
            {
                ThrowIfDisposed();
                warnStoppedOutputs = true;
            }
        }

        /// <summary>
        /// 未登録の追加基点を止めた事実を、通知にせずこの収集先へ事実として集めるか。
        /// 受付を持たない起動の必須出力が、受付の解放後に不変の結果として通知するために使います。
        /// </summary>
        internal bool CollectsUnregisteredBase { get; init; }

        internal void AddStoppedOutput(PlaylistStoppedOutput stoppedOutput)
        {
            ArgumentNullException.ThrowIfNull(stoppedOutput);
            lock (gate)
            {
                ThrowIfDisposed();
                AddDistinct(stoppedOutputs, stoppedOutput);
            }
        }

        /// <summary>集めた止めた表の事実を取り出して空にします。起動の必須出力が受付の解放後に通知するための結果として使います。</summary>
        internal IReadOnlyList<PlaylistStoppedOutput> TakeStoppedOutputs()
        {
            lock (gate)
            {
                ThrowIfDisposed();
                List<PlaylistStoppedOutput> snapshot = [.. stoppedOutputs];
                stoppedOutputs.Clear();
                return snapshot.AsReadOnly();
            }
        }

        private static void AddDistinct(List<PlaylistStoppedOutput> target, PlaylistStoppedOutput value)
        {
            if (!target.Contains(value)) { target.Add(value); }
        }

        internal OperationNotificationReceipt TakeReceipt()
        {
            List<OperationNotification> snapshot;
            List<PlaylistStoppedOutput> collisions;
            lock (gate)
            {
                ThrowIfDisposed();
                snapshot = [.. notifications];
                notifications.Clear();
                collisions = warnStoppedOutputs
                    ? [.. stoppedOutputs.Where(stopped => stopped.Reason == PlaylistOutputStopReason.Collision)]
                    : [];
                if (warnStoppedOutputs) { stoppedOutputs.RemoveAll(stopped => stopped.Reason == PlaylistOutputStopReason.Collision); }
            }
            // 出力を止めた表は、操作ごとに一件の警告へまとめます。明示操作だけが警告し、ほかは診断ログに残した事実だけです。
            if (collisions.Count > 0) { snapshot.Add(CreateStoppedOutputsNotification(collisions)); }
            return OperationNotificationReceipt.Create(snapshot);
        }

        internal void Add(OperationNotification notification)
        {
            if (notification == null)
            {
                return;
            }
            lock (gate)
            {
                ThrowIfDisposed();
                notifications.Add(notification);
            }
        }

        /// <summary>
        /// 外側の収集先があれば、集めた事実をすべて外側へ移して true を返します。外側がなければ何もせず false を返します。
        /// 外側の操作が受付（P）を解放した後に、まとめて表示を依頼するための引継ぎです。
        /// 受付を保持したまま内側の操作が表示を依頼しないよう、最も外側の収集先だけが表示を依頼します。
        /// </summary>
        /// <remarks>外側の破棄との競合で通知を失わないよう、外側の破棄の判定と追加を外側の同じロックの中で行い、移せなかった事実はこの収集先へ戻します。</remarks>
        /// <returns>事実を外側へ移した場合は true。移す先がない場合は false で、事実はこの収集先に残ります。</returns>
        internal bool TryForwardToParent()
        {
            OperationNotificationSession target = parent;
            if (target == null)
            {
                return false;
            }
            List<OperationNotification> moved;
            List<PlaylistStoppedOutput> movedStopped;
            bool movedWarn;
            lock (gate)
            {
                ThrowIfDisposed();
                moved = [.. notifications];
                notifications.Clear();
                movedStopped = [.. stoppedOutputs];
                stoppedOutputs.Clear();
                movedWarn = warnStoppedOutputs;
            }
            bool forwarded;
            lock (target.gate)
            {
                forwarded = !target.disposed;
                if (forwarded)
                {
                    target.notifications.AddRange(moved);
                    foreach (PlaylistStoppedOutput stopped in movedStopped) { AddDistinct(target.stoppedOutputs, stopped); }
                    target.warnStoppedOutputs |= movedWarn;
                }
            }
            if (!forwarded)
            {
                lock (gate)
                {
                    notifications.InsertRange(0, moved);
                    foreach (PlaylistStoppedOutput stopped in movedStopped) { AddDistinct(stoppedOutputs, stopped); }
                }
            }
            return forwarded;
        }

        /// <summary>
        /// 収集先を閉じ、現在の収集先を外側へ戻します。受け取られないまま残った通知は、失われたことを診断へ残します。
        /// </summary>
        public void Dispose()
        {
            int discarded;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                discarded = notifications.Count;
                notifications.Clear();
            }
            if (ReferenceEquals(owner.currentSession.Value, this))
            {
                owner.currentSession.Value = parent;
            }
            if (discarded > 0)
            {
                try { NLogWrapper.FileLogger?.Warn("playlist_operation_notice_discarded reason=session_disposed_with_unpublished_notices count=" + discarded); }
                catch (Exception) { /* 診断先の失敗で、操作の結果を変えません。 */ }
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(OperationNotificationSession));
            }
        }
    }

    /// <param name="collectsUnregisteredBase">未登録の追加基点を止めた事実を、通知にせず事実として集めるか。受付を持たない起動の必須出力だけが指定します。</param>
    internal OperationNotificationSession BeginSession(bool collectsUnregisteredBase = false)
    {
        return new OperationNotificationSession(this) { CollectsUnregisteredBase = collectsUnregisteredBase };
    }

    /// <summary>
    /// 現在の操作の収集先があれば通知の事実を積んで true を返します。収集先がなければ積まず false を返します。
    /// 収集先を持たない呼出し元が、操作の外側と同じ収集先に載せられるかを判断するために使います。
    /// </summary>
    internal bool TryAddToCurrentSession(OperationNotification notification)
    {
        OperationNotificationSession session = currentSession.Value;
        if (session == null || notification == null)
        {
            return false;
        }
        try
        {
            session.Add(notification);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 出力を止めた表を報告します。止めた事実は常に診断ログへ残します。収集先がなければ通知せず、失敗にもしません。
    /// 衝突は、操作の収集先へ事実として集め、明示操作（<see cref="OperationNotificationSession.WarnStoppedOutputs"/>）だけが一件の警告にまとめます。
    /// 未登録の追加基点は、既存の表ごとの警告として積みます。起動の必須出力の収集先（<see cref="OperationNotificationSession.CollectsUnregisteredBase"/>）では事実として集めます。
    /// </summary>
    internal void ReportStoppedOutput(PlaylistStoppedOutput stoppedOutput)
    {
        ArgumentNullException.ThrowIfNull(stoppedOutput);
        try
        {
            NLogWrapper.FileLogger?.Warn("playlist_output_stopped reason=" + stoppedOutput.Reason
                + " table=" + stoppedOutput.TableName + " outputDir=" + stoppedOutput.OutputDirectory + " base=" + stoppedOutput.BaseName);
        }
        catch (Exception) { /* 診断先の失敗で、操作の結果を変えません。 */ }
        OperationNotificationSession session = currentSession.Value;
        if (session == null) { return; }
        try
        {
            if (stoppedOutput.Reason == PlaylistOutputStopReason.UnregisteredBase && !session.CollectsUnregisteredBase)
            {
                session.Add(new OperationNotification(
                    OperationNotificationText.FromResource(nameof(Resources.Warn_UnregisteredCustomFolderOutputBase), stoppedOutput.TableName, stoppedOutput.BaseName),
                    nameof(Resources.MessageBoxTitle_Warning), OperationNotificationSeverity.Warning));
                return;
            }
            session.AddStoppedOutput(stoppedOutput);
        }
        catch (ObjectDisposedException) { /* 閉じた収集先へは積めません。事実は診断ログに残しています。 */ }
    }

    /// <summary>現在の操作の収集先を、出力を止めた表を警告として案内する明示操作にします。収集先がなければ何もしません。</summary>
    internal void WarnStoppedOutputsInCurrentSession()
    {
        try { currentSession.Value?.WarnStoppedOutputs(); }
        catch (ObjectDisposedException) { /* 閉じた収集先は案内を表示できません。事実は診断ログに残しています。 */ }
    }

    /// <summary>出力を止めた表の一覧を、導入文と「表名: 出力先」の行の一覧として一件の警告にまとめます。件数の上限や省略は設けません。</summary>
    private static OperationNotification CreateStoppedOutputsNotification(IReadOnlyList<PlaylistStoppedOutput> stoppedOutputs)
        => new(CreateStoppedOutputsText(stoppedOutputs), nameof(Resources.Warning), OperationNotificationSeverity.Warning, isStoppedOutputsWarning: true);

    /// <summary>出力を止めた表の警告本文を、表示直前の言語で整形できる事実として作ります。</summary>
    internal static OperationNotificationText CreateStoppedOutputsText(IEnumerable<PlaylistStoppedOutput> stoppedOutputs)
        => OperationNotificationText.FromResourceWithDetail(nameof(Resources.Warn_PlaylistOutputOverlapStopped),
            string.Join(Environment.NewLine, stoppedOutputs.Select(stopped => stopped.TableName + ": " + stopped.OutputDirectory)), 2);

    /// <summary>警告を積みます。見出しを省略すると、表示直前の言語の警告見出しを使います。</summary>
    internal void QueueWarning(OperationNotificationText message, string captionResourceName = null)
    {
        QueueNotification(message, captionResourceName ?? nameof(Resources.MessageBoxTitle_Warning), OperationNotificationSeverity.Warning);
    }

    /// <summary>情報を積みます。本文と見出しは資源名の事実で保持し、表示直前の選択言語で整形します。</summary>
    internal void QueueInformation(OperationNotificationText message, string captionResourceName)
    {
        QueueNotification(message, captionResourceName, OperationNotificationSeverity.Information);
    }

    /// <summary>エラーを積みます。本文と見出しは資源名の事実で保持し、表示直前の選択言語で整形します。</summary>
    internal void QueueError(OperationNotificationText message, string captionResourceName)
    {
        QueueNotification(message, captionResourceName, OperationNotificationSeverity.Error);
    }

    private void QueueNotification(OperationNotificationText message, string captionResourceName, OperationNotificationSeverity severity)
    {
        OperationNotificationSession session = currentSession.Value;
        if (session == null)
        {
            throw new InvalidOperationException("Playlist operation notification session is not active.");
        }
        session.Add(new OperationNotification(message, captionResourceName, severity));
    }
}
