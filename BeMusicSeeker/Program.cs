#nullable enable
using System;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Runtime;

namespace BeMusicSeeker;

/// <summary>WPFおよびstatic providerの初期化より先に、検証起動の配置を確定する唯一の入口です。</summary>
internal static class Program
{
    /// <summary>不正な起動manifestは通常領域へ代替せず、stderrと非zero終了で報告します。</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationRuntimeOptions options;
        try
        {
            string executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Application process path is not available.");
            options = ApplicationRuntimeOptionsParser.Capture(executablePath, args);
            CommandLineSwitches.Configure(options.LogOptions);
            ApplicationPathPolicy.Configure(options.Paths);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Application startup options failed: " + exception.Message);
            return 1;
        }
        App application = new(options);
        application.InitializeComponent();
        return application.Run();
    }
}
