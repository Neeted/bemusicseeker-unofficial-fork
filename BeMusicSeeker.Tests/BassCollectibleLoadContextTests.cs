using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Media;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BassCollectibleLoadContextTests
{
    [TestMethod]
    public void BassAudioPlayer_StaticInitializationDoesNotEnumerateOrLoadNativeRuntime()
    {
        WeakReference? loadContextReference = null;
        try
        {
            RunStaticInitializationInCollectibleContext(out loadContextReference);
        }
        finally
        {
            if (loadContextReference != null)
            {
                WaitForCollectibleContextUnload(loadContextReference);
            }
        }

        Assert.IsNotNull(loadContextReference);
        Assert.IsFalse(
            loadContextReference.IsAlive,
            "The collectible audio test context must unload without relying on another testhost's WPF state.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunStaticInitializationInCollectibleContext(out WeakReference loadContextReference)
    {
        string applicationAssemblyPath = typeof(BassAudioPlayer).Assembly.Location;
        var loadContext = new IsolatedApplicationLoadContext(applicationAssemblyPath);
        loadContextReference = new WeakReference(loadContext, trackResurrection: true);
        try
        {
            Assembly applicationAssembly = loadContext.LoadFromAssemblyPath(applicationAssemblyPath);
            Type runtimeType = applicationAssembly.GetType("Ribbit.Media.Audio.BassNativeRuntime", throwOnError: true)!;
            Type enumeratorType = applicationAssembly.GetType("Ribbit.Media.Audio.BassAudioDeviceEnumerator", throwOnError: true)!;
            Type playerType = applicationAssembly.GetType("Ribbit.Media.BassAudioPlayer", throwOnError: true)!;

            int nativeLoadCountBefore = ReadStaticCounter(runtimeType, "LoadInvocationCount");
            int enumerationCountBefore = ReadStaticCounter(enumeratorType, "EnumerationInvocationCount");

            RuntimeHelpers.RunClassConstructor(playerType.TypeHandle);

            Assert.AreEqual(nativeLoadCountBefore, ReadStaticCounter(runtimeType, "LoadInvocationCount"));
            Assert.AreEqual(enumerationCountBefore, ReadStaticCounter(enumeratorType, "EnumerationInvocationCount"));
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private static void WaitForCollectibleContextUnload(WeakReference loadContextReference)
    {
        for (int attempt = 0; attempt < 10 && loadContextReference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static int ReadStaticCounter(Type type, string propertyName)
    {
        return (int)type.GetProperty(
            propertyName,
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    }

    private sealed class IsolatedApplicationLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver;

        internal IsolatedApplicationLoadContext(string applicationAssemblyPath)
            : base(isCollectible: true)
        {
            resolver = new AssemblyDependencyResolver(applicationAssemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string? assemblyPath = resolver.ResolveAssemblyToPath(assemblyName);
            return assemblyPath == null ? null : LoadFromAssemblyPath(assemblyPath);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? libraryPath = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return libraryPath == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(libraryPath);
        }
    }
}
