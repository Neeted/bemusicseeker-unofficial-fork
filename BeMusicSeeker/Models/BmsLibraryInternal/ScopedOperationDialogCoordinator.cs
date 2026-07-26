using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Owns the thread-local operation-dialog mutation boundary without retaining
/// the application facade.  The raw dialog capability is the only composition
/// dependency; callers can therefore share this boundary across workflows.
/// </summary>
internal sealed class ScopedOperationDialogCoordinator : IBmsLibraryDialogService
{
    private readonly IBmsLibraryDialogService immediateDialogService;

    [ThreadStatic]
    private static Stack<Scope> threadScopes;

    internal ScopedOperationDialogCoordinator(IBmsLibraryDialogService immediateDialogService)
    {
        this.immediateDialogService = immediateDialogService
            ?? throw new ArgumentNullException(nameof(immediateDialogService));
    }

    internal Scope BeginScope()
    {
        Scope scope = new(this);
        threadScopes ??= new Stack<Scope>();
        threadScopes.Push(scope);
        return scope;
    }

    public UiDialogDefaultResult Show(
        string messageBoxText,
        string caption,
        UiDialogButton button,
        UiDialogIcon icon,
        UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None)
    {
        Scope scope = GetCurrentScope();
        if (scope != null && button == UiDialogButton.OK)
        {
            scope.Enqueue(new DialogMessage(messageBoxText, caption, button, icon, defaultResult));
            return defaultResult == UiDialogDefaultResult.None
                ? UiDialogDefaultResult.OK
                : defaultResult;
        }
        if (scope != null)
        {
            throw new InvalidOperationException(
                "Interactive BMS library prompts must be resolved before entering a chart/package mutation boundary.");
        }
        return immediateDialogService.Show(messageBoxText, caption, button, icon, defaultResult);
    }

    private static Scope GetCurrentScope() => threadScopes?.FirstOrDefault();

    private static void EndScope(Scope scope)
    {
        Stack<Scope> scopes = threadScopes;
        if (scopes == null || scopes.Count == 0)
        {
            return;
        }
        if (ReferenceEquals(scopes.Peek(), scope))
        {
            scopes.Pop();
            return;
        }

        Scope[] remainingScopes = [.. scopes.Where(currentScope => !ReferenceEquals(currentScope, scope)).Reverse()];
        scopes.Clear();
        foreach (Scope remainingScope in remainingScopes)
        {
            scopes.Push(remainingScope);
        }
    }

    internal sealed class DialogMessage
    {
        internal DialogMessage(
            string messageBoxText,
            string caption,
            UiDialogButton button,
            UiDialogIcon icon,
            UiDialogDefaultResult defaultResult)
        {
            MessageBoxText = messageBoxText;
            Caption = caption;
            Button = button;
            Icon = icon;
            DefaultResult = defaultResult;
        }

        internal string MessageBoxText { get; }

        internal string Caption { get; }

        internal UiDialogButton Button { get; }

        internal UiDialogIcon Icon { get; }

        internal UiDialogDefaultResult DefaultResult { get; }
    }

    internal sealed class Scope : IDisposable
    {
        private readonly ScopedOperationDialogCoordinator coordinator;
        private readonly List<DialogMessage> messages = [];
        private bool disposed;

        internal Scope(ScopedOperationDialogCoordinator coordinator)
        {
            this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        }

        internal IReadOnlyList<DialogMessage> Messages => messages;

        internal void Enqueue(DialogMessage message)
        {
            if (message == null || messages.Any(existing =>
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

        internal void Flush()
        {
            DialogMessage[] pendingMessages = [.. messages];
            messages.Clear();
            foreach (DialogMessage message in pendingMessages)
            {
                coordinator.immediateDialogService.Show(
                    message.MessageBoxText,
                    message.Caption,
                    message.Button,
                    message.Icon,
                    message.DefaultResult);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            ScopedOperationDialogCoordinator.EndScope(this);
        }
    }
}
