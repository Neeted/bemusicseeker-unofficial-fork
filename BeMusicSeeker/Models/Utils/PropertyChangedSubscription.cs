using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq.Expressions;

namespace BeMusicSeeker.Models.Utils;

/// <summary>
/// Model 層で利用する、特定 property の変更通知を購読する disposable です。
/// Livet の event listener に依存せず、購読と解除の所有者を明示します。
/// </summary>
internal sealed class PropertyChangedSubscription : IDisposable
{
    private readonly INotifyPropertyChanged source;

    private readonly Dictionary<string, List<Action>> handlers = [];

    private readonly object syncRoot = new();

    private bool disposed;

    private PropertyChangedSubscription(INotifyPropertyChanged source)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.source.PropertyChanged += OnPropertyChanged;
    }

    internal static PropertyChangedSubscription Create(INotifyPropertyChanged source)
    {
        return new PropertyChangedSubscription(source);
    }

    internal void RegisterHandler<T>(Expression<Func<T>> propertyExpression, Action handler)
    {
        if (propertyExpression == null)
        {
            throw new ArgumentNullException(nameof(propertyExpression));
        }
        if (handler == null)
        {
            throw new ArgumentNullException(nameof(handler));
        }
        if (propertyExpression.Body is not MemberExpression memberExpression)
        {
            throw new ArgumentException("Property expression must target a member.", nameof(propertyExpression));
        }

        RegisterHandler(memberExpression.Member.Name, handler);
    }

    private void RegisterHandler(string propertyName, Action handler)
    {
        lock (syncRoot)
        {
            ThrowIfDisposed();
            if (!handlers.TryGetValue(propertyName, out List<Action> propertyHandlers))
            {
                propertyHandlers = [];
                handlers.Add(propertyName, propertyHandlers);
            }
            propertyHandlers.Add(handler);
        }
    }

    private void OnPropertyChanged(object sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs == null)
        {
            return;
        }

        List<Action> callbacks = [];
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            if (!string.IsNullOrEmpty(eventArgs.PropertyName)
                && handlers.TryGetValue(eventArgs.PropertyName, out List<Action> propertyHandlers))
            {
                callbacks.AddRange(propertyHandlers);
            }
            else if (string.IsNullOrEmpty(eventArgs.PropertyName))
            {
                foreach (List<Action> allPropertyHandlers in handlers.Values)
                {
                    callbacks.AddRange(allPropertyHandlers);
                }
            }
        }

        foreach (Action callback in callbacks)
        {
            callback();
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            handlers.Clear();
        }

        source.PropertyChanged -= OnPropertyChanged;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(PropertyChangedSubscription));
        }
    }
}
