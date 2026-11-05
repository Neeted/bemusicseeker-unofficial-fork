using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Properties;

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
        internal OperationNotification(OperationNotificationText message, string captionResourceName, OperationNotificationSeverity severity)
        {
            Text = message ?? throw new ArgumentNullException(nameof(message));
            CaptionResourceName = captionResourceName ?? throw new ArgumentNullException(nameof(captionResourceName));
            Severity = severity;
        }

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

        private bool disposed;

        internal OperationNotificationSession(PlaylistOperationNotificationOwner owner)
        {
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
            parent = owner.currentSession.Value;
            owner.currentSession.Value = this;
        }

        internal OperationNotificationReceipt TakeReceipt()
        {
            ThrowIfDisposed();
            List<OperationNotification> snapshot;
            lock (gate)
            {
                snapshot = [.. notifications];
                notifications.Clear();
            }
            return OperationNotificationReceipt.Create(snapshot);
        }

        internal void Add(OperationNotification notification)
        {
            ThrowIfDisposed();
            if (notification == null)
            {
                return;
            }
            lock (gate)
            {
                notifications.Add(notification);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            if (ReferenceEquals(owner.currentSession.Value, this))
            {
                owner.currentSession.Value = parent;
            }
            disposed = true;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(OperationNotificationSession));
            }
        }
    }

    internal OperationNotificationSession BeginSession()
    {
        return new OperationNotificationSession(this);
    }

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
