using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Text;

[assembly: SupportedOSPlatform("windows")]

namespace BeMusicSeeker;

internal static class RuntimeBootstrap
{
    private static int codePagesRegistered;

    [ModuleInitializer]
    internal static void InitializeModule()
    {
        Initialize();
    }

    internal static void Initialize()
    {
        if (Interlocked.Exchange(ref codePagesRegistered, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }
}
