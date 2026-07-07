using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary
{
    internal sealed class OperationDialogMessage
    {
        internal OperationDialogMessage(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult)
        {
            MessageBoxText = messageBoxText;
            Caption = caption;
            Button = button;
            Icon = icon;
            DefaultResult = defaultResult;
        }

        internal string MessageBoxText { get; }

        internal string Caption { get; }

        internal MessageBoxButton Button { get; }

        internal MessageBoxImage Icon { get; }

        internal MessageBoxResult DefaultResult { get; }
    }

    internal sealed class OperationDialogScope : IDisposable
    {
        private readonly BMSLibrary owner;
        private readonly List<OperationDialogMessage> messages = [];
        private bool disposed;

        internal OperationDialogScope(BMSLibrary owner)
        {
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        internal IReadOnlyList<OperationDialogMessage> Messages => messages;

        internal void Enqueue(OperationDialogMessage message)
        {
            if (message != null)
            {
                if (messages.Any(existing =>
                    string.Equals(existing.MessageBoxText, message.MessageBoxText, StringComparison.Ordinal)
                    && string.Equals(existing.Caption, message.Caption, StringComparison.Ordinal)
                    && existing.Button == message.Button
                    && existing.Icon == message.Icon
                    && existing.DefaultResult == message.DefaultResult))
                {
                    return;
                }
                messages.Add(message);
            }
        }

        internal void Flush()
        {
            List<OperationDialogMessage> pendingMessages = [.. messages];
            messages.Clear();
            foreach (OperationDialogMessage message in pendingMessages)
            {
                owner.ShowOperationDialogImmediate(message);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            owner.EndOperationDialogScope(this);
        }
    }

    private sealed class ScopedOperationDialogService : IBmsLibraryDialogService
    {
        private readonly BMSLibrary owner;

        internal ScopedOperationDialogService(BMSLibrary owner)
        {
            this.owner = owner;
        }

        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            return owner.ShowOperationDialog(messageBoxText, caption, button, icon, defaultResult);
        }
    }

    [ThreadStatic]
    private static Stack<OperationDialogScope> threadOperationDialogScopes;

    internal OperationDialogScope BeginOperationDialogScope()
    {
        OperationDialogScope scope = new(this);
        threadOperationDialogScopes ??= new Stack<OperationDialogScope>();
        threadOperationDialogScopes.Push(scope);
        return scope;
    }

    private void EndOperationDialogScope(OperationDialogScope scope)
    {
        Stack<OperationDialogScope> scopes = threadOperationDialogScopes;
        if (scopes == null || scopes.Count == 0)
        {
            return;
        }
        if (ReferenceEquals(scopes.Peek(), scope))
        {
            scopes.Pop();
            return;
        }

        OperationDialogScope[] remainingScopes = [.. scopes.Where(currentScope => !ReferenceEquals(currentScope, scope)).Reverse()];
        scopes.Clear();
        foreach (OperationDialogScope remainingScope in remainingScopes)
        {
            scopes.Push(remainingScope);
        }
    }

    private OperationDialogScope GetCurrentOperationDialogScope()
    {
        Stack<OperationDialogScope> scopes = threadOperationDialogScopes;
        if (scopes == null)
        {
            return null;
        }
        return scopes.FirstOrDefault();
    }

    private MessageBoxResult ShowOperationDialog(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        OperationDialogScope scope = GetCurrentOperationDialogScope();
        if (scope != null && button == MessageBoxButton.OK)
        {
            scope.Enqueue(new OperationDialogMessage(messageBoxText, caption, button, icon, defaultResult));
            return defaultResult == MessageBoxResult.None ? MessageBoxResult.OK : defaultResult;
        }
        if (scope != null)
        {
            throw new InvalidOperationException("Interactive BMS library prompts must be resolved before entering a chart/package mutation boundary.");
        }
        return dialogService.Show(messageBoxText, caption, button, icon, defaultResult);
    }

    private void ShowOperationDialogImmediate(OperationDialogMessage message)
    {
        if (message == null)
        {
            return;
        }
        dialogService.Show(message.MessageBoxText, message.Caption, message.Button, message.Icon, message.DefaultResult);
    }
}
