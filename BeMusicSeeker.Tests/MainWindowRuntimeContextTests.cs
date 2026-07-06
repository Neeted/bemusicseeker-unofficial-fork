using System;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MainWindowRuntimeContextTests
{
    [TestMethod]
    public void RuntimeContext_RequiresExplicitProviders()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new MainWindowRuntimeContext(
            null,
            () => null,
            () => null,
            () => null,
            () => null));
        Assert.ThrowsException<ArgumentNullException>(() => new MainWindowRuntimeContext(
            () => null,
            null,
            () => null,
            () => null,
            () => null));
        Assert.ThrowsException<ArgumentNullException>(() => new MainWindowRuntimeContext(
            () => null,
            () => null,
            null,
            () => null,
            () => null));
        Assert.ThrowsException<ArgumentNullException>(() => new MainWindowRuntimeContext(
            () => null,
            () => null,
            () => null,
            null,
            () => null));
        Assert.ThrowsException<ArgumentNullException>(() => new MainWindowRuntimeContext(
            () => null,
            () => null,
            () => null,
            () => null,
            null));
    }

    [TestMethod]
    public void RuntimeContext_UninitializedDependenciesThrowInsteadOfFallingBack()
    {
        var context = new MainWindowRuntimeContext(
            () => null,
            () => null,
            () => null,
            () => null,
            () => null);

        AssertUninitialized(() => context.Files, "Files");
        AssertUninitialized(() => context.Tables, "Tables");
        AssertUninitialized(() => context.Lr2Config, "Lr2Config");
        AssertUninitialized(() => context.BmsPlayer, "BmsPlayer");
        AssertUninitialized(() => context.UiDispatcher, "UiDispatcher");
    }

    [TestMethod]
    public void RuntimeContext_ReturnsAvailableRootOwnedDependencies()
    {
        IBMSPlayer player = new InternalBMSAutoPlayerSoundOnly();
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        var context = new MainWindowRuntimeContext(
            () => null,
            () => null,
            () => null,
            () => player,
            () => dispatcher);

        Assert.AreSame(player, context.BmsPlayer);
        Assert.AreSame(dispatcher, context.UiDispatcher);
    }

    [TestMethod]
    public void MainWindowViewModel_CreatesRuntimeContext()
    {
        var viewModel = new MainWindowViewModel();

        Assert.IsNotNull(viewModel.RuntimeContext);
        Assert.IsNotNull(viewModel.RuntimeContext.BmsPlayer);
    }

    private static void AssertUninitialized<T>(Func<T> accessor, string dependencyName)
    {
        InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(() => accessor());
        StringAssert.Contains(exception.Message, dependencyName);
        StringAssert.Contains(exception.Message, "not initialized");
    }
}
