using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MainWindowPlaybackWpfTests
{
    [TestMethod]
    public void PlaybackControls_BindPanelStateAndCapabilitiesThroughPlaybackOwner()
    {
        object? selectedRow = null;
        var activatedRows = new List<(int RowIndex, object Row)>();
        var playbackTerminal = new MainWindowPlaybackTerminal(
            row => selectedRow = row,
            (rowIndex, row) =>
            {
                activatedRows.Add((rowIndex, row));
                return false;
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                using HwndSource visualHost = CreateVisualHost(window, "MainWindowPlaybackWpfTests");
                var panel = (PlaybackPanelView)window.FindName("playbackPanelView");
                Assert.IsNotNull(panel);
                Assert.AreSame(viewModel.PlaybackPanel, panel.DataContext);

                IReadOnlyList<Button> seekButtons = FindVisualChildren<Button>(panel)
                    .Where(button => BindingOperations.GetBindingBase(button, UIElement.IsEnabledProperty) is Binding binding
                        && string.Equals(binding.Path?.Path, "CanSeek", StringComparison.Ordinal))
                    .ToArray();
                Assert.AreEqual(2, seekButtons.Count);
                Assert.IsTrue(seekButtons.All(button => button.IsEnabled == viewModel.PlaybackPanel.CanSeek));

                object row = new object();
                var table = (CustomTableView)window.FindName("customTableView");
                table.ItemsSource = new List<object> { new object(), row };
                table.SelectRowsByPredicate(candidate => ReferenceEquals(candidate, row));

                Assert.AreEqual(1, table.SelectedIndex);
                Assert.AreSame(row, selectedRow);

                RaiseKey(table, Key.Return);

                Assert.AreEqual(1, activatedRows.Count);
                Assert.AreEqual(1, activatedRows[0].RowIndex);
                Assert.AreSame(row, activatedRows[0].Row);
            },
            playbackTerminal: playbackTerminal);
    }

    private static IReadOnlyList<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        var result = new List<T>();
        Visit(root, result, new HashSet<DependencyObject>());
        return result;
    }

    private static void Visit<T>(DependencyObject current, List<T> result, HashSet<DependencyObject> visited)
        where T : DependencyObject
    {
        if (current == null || !visited.Add(current))
        {
            return;
        }
        if (current is T match)
        {
            result.Add(match);
        }
        if (current is not Visual && current is not System.Windows.Media.Media3D.Visual3D)
        {
            return;
        }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
        {
            Visit(VisualTreeHelper.GetChild(current, index), result, visited);
        }
    }

    private static HwndSource CreateVisualHost(MainWindow window, string name)
    {
        var source = new HwndSource(new HwndSourceParameters(name)
        {
            Width = 1000,
            Height = 700,
            PositionX = 0,
            PositionY = 0
        });
        source.RootVisual = (Visual)window.Content;
        window.Measure(new Size(1000d, 700d));
        window.Arrange(new Rect(0d, 0d, 1000d, 700d));
        window.UpdateLayout();
        return source;
    }

    private static void RaiseKey(CustomTableView table, Key key)
        => table.HandleKeyDown(key, ModifierKeys.None);
}
