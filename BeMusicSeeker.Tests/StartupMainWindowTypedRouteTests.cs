using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.StartupLibraryConstructionTestSupport;
namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class StartupMainWindowTypedRouteTests
{
    [TestMethod]
    public void MainWindowInitializeAsync_UsesTypedStartupConstructionOwnerRoute()
    {
        MethodInfo initializeAsync = typeof(MainWindowViewModel).GetMethod(
            "InitializeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainWindowViewModel.InitializeAsync was not found.");
        AsyncStateMachineAttribute stateMachineAttribute = initializeAsync.GetCustomAttribute<AsyncStateMachineAttribute>()
            ?? throw new InvalidOperationException("InitializeAsync must remain an async state machine.");
        MethodInfo moveNext = stateMachineAttribute.StateMachineType.GetMethod(
            "MoveNext",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("InitializeAsync state machine MoveNext was not found.");

        IReadOnlyList<MethodBase> calledMethods = EnumerateCalledMethods(moveNext).ToArray();
        string[] startupOwnerCalls = calledMethods
            .Where(method => method.DeclaringType == typeof(StartupLibraryConstructionOwner))
            .Where(method => method.Name == "CreateAndApply")
            .Select(method => method.Name)
            .ToArray();

        CollectionAssert.AreEqual(new[] { "CreateAndApply" }, startupOwnerCalls);
        Assert.IsFalse(
            calledMethods.Any(method =>
                method.DeclaringType == typeof(ApplicationComposition)
                && method.Name is "CreateBmsLibrary" or "CreateBmsPlaylist"),
            "InitializeAsync must use the typed startup owner instead of calling composition factories directly.");
    }

}
