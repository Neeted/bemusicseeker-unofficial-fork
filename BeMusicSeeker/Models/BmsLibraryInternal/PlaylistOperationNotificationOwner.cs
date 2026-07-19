using System;
using System.Collections.Generic;
using System.Threading;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// プレイリスト処理中に発生したユーザー通知を操作単位で収集し、presentation shell へ渡します。
/// </summary>
internal sealed class PlaylistOperationNotificationOwner
{
    private readonly AsyncLocal<OperationNotificationScope> currentScope = new();

    internal enum OperationNotificationSeverity
    {
        Information,
        Warning,
        Error
    }

    internal sealed class OperationNotification
    {
        internal OperationNotification(
            string message,
            string caption,
            OperationNotificationSeverity severity)
        {
            Message = message;
            Caption = caption;
            Severity = severity;
        }

        internal string Message { get; }

        internal string Caption { get; }

        internal OperationNotificationSeverity Severity { get; }
    }

    internal sealed class OperationNotificationScope : IDisposable
    {
        private readonly PlaylistOperationNotificationOwner owner;

        private readonly object gate = new();

        private readonly OperationNotificationScope parent;

        private readonly List<OperationNotification> notifications = [];

        private bool disposed;

        internal OperationNotificationScope(PlaylistOperationNotificationOwner owner)
        {
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
            parent = owner.currentScope.Value;
            owner.currentScope.Value = this;
        }

        internal IReadOnlyList<OperationNotification> Notifications
        {
            get
            {
                lock (gate)
                {
                    return [.. notifications];
                }
            }
        }

        internal void Add(OperationNotification notification)
        {
            if (notification == null)
            {
                return;
            }
            lock (gate)
            {
                notifications.Add(notification);
            }
        }

        internal void Flush(Action<OperationNotification> presenter)
        {
            if (presenter == null)
            {
                throw new ArgumentNullException(nameof(presenter));
            }
            List<OperationNotification> snapshot;
            lock (gate)
            {
                snapshot = [.. notifications];
                notifications.Clear();
            }
            foreach (OperationNotification notification in snapshot)
            {
                presenter(notification);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            if (ReferenceEquals(owner.currentScope.Value, this))
            {
                owner.currentScope.Value = parent;
            }
            disposed = true;
        }
    }

    internal OperationNotificationScope BeginScope()
    {
        return new OperationNotificationScope(this);
    }

    internal void QueueWarning(string message, string caption = null)
    {
        QueueNotification(
            message,
            caption ?? Resources.MessageBoxTitle_Warning,
            OperationNotificationSeverity.Warning);
    }

    internal void QueueInformation(string message, string caption)
    {
        QueueNotification(message, caption, OperationNotificationSeverity.Information);
    }

    internal void QueueError(string message, string caption)
    {
        QueueNotification(message, caption, OperationNotificationSeverity.Error);
    }

    private void QueueNotification(
        string message,
        string caption,
        OperationNotificationSeverity severity)
    {
        OperationNotificationScope scope = currentScope.Value;
        if (scope == null)
        {
            throw new InvalidOperationException("Playlist operation notification scope is not active.");
        }
        scope.Add(new OperationNotification(message, caption, severity));
    }
}
