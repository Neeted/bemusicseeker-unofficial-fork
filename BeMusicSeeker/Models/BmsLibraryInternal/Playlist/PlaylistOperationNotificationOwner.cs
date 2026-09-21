using System;
using System.Collections.Generic;
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

    internal sealed class OperationNotification
    {
        internal OperationNotification(string message, string caption, OperationNotificationSeverity severity)
        {
            Message = message;
            Caption = caption;
            Severity = severity;
        }

        internal string Message { get; }

        internal string Caption { get; }

        internal OperationNotificationSeverity Severity { get; }
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

    internal void QueueWarning(string message, string caption = null)
    {
        QueueNotification(message, caption ?? Resources.MessageBoxTitle_Warning, OperationNotificationSeverity.Warning);
    }

    internal void QueueInformation(string message, string caption)
    {
        QueueNotification(message, caption, OperationNotificationSeverity.Information);
    }

    internal void QueueError(string message, string caption)
    {
        QueueNotification(message, caption, OperationNotificationSeverity.Error);
    }

    private void QueueNotification(string message, string caption, OperationNotificationSeverity severity)
    {
        OperationNotificationSession session = currentSession.Value;
        if (session == null)
        {
            throw new InvalidOperationException("Playlist operation notification session is not active.");
        }
        session.Add(new OperationNotification(message, caption, severity));
    }
}
