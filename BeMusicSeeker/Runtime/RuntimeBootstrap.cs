using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Text;
using SQLitePCL;

[assembly: SupportedOSPlatform("windows")]

namespace BeMusicSeeker;

internal static class RuntimeBootstrap
{
    private static int codePagesRegistered;
    private static int sqliteProviderInitialized;

    [ModuleInitializer]
    internal static void InitializeModule()
    {
        Initialize();
    }

    internal static void Initialize()
    {
        if (Interlocked.Exchange(ref sqliteProviderInitialized, 1) == 0)
        {
            Batteries_V2.Init();
        }
        if (Interlocked.Exchange(ref codePagesRegistered, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }
}
