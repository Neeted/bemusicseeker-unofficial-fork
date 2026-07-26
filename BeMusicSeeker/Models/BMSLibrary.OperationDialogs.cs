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
    private sealed class EmptyDialogService : IBmsLibraryDialogService
    {
        internal static readonly EmptyDialogService Instance = new();

        public MessageBoxResult Show(
            string messageBoxText,
            string caption,
            MessageBoxButton button,
            MessageBoxImage icon,
            MessageBoxResult defaultResult = MessageBoxResult.None) =>
            defaultResult;
    }

    internal sealed class OperationDialogMessage
    {
        internal OperationDialogMessage(
            string messageBoxText,
            string caption,
            MessageBoxButton button,
            MessageBoxImage icon,
            MessageBoxResult defaultResult)
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
                message.DefaultResult))];

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
                message.DefaultResult));
        }

        internal void Flush() => coordinatorScope.Flush();

        public void Dispose() => coordinatorScope.Dispose();
    }

    internal OperationDialogScope BeginOperationDialogScope() =>
        new(GetScopedOperationDialogService().BeginScope());

    private MessageBoxResult ShowOperationDialog(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult = MessageBoxResult.None) =>
        GetScopedOperationDialogService().Show(messageBoxText, caption, button, icon, defaultResult);

    private ScopedOperationDialogCoordinator GetScopedOperationDialogService()
    {
        return scopedOperationDialogService ??= new(
            dialogService ?? EmptyDialogService.Instance);
    }
}
