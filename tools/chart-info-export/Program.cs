using System;

namespace ChartInfoExportTool;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            ChartInfoExportOptions options = ParseOptions(args);
            ChartInfoExportResult result = ChartInfoExportRunner.Export(options);
            Console.WriteLine(result.ToConsoleSummary());
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static ChartInfoExportOptions ParseOptions(string[] args)
    {
        ChartInfoExportOptions options = new ChartInfoExportOptions();
        for (int index = 0; index < (args?.Length ?? 0); index++)
        {
            string name = args[index];
            string Value()
            {
                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException("Missing value for " + name);
                }
                return args[++index];
            }

            switch (name)
            {
                case "--source":
                    options.SourceSongDbPath = Value();
                    break;
                case "--out":
                    options.OutputDbPath = Value();
                    break;
                case "--archive-out":
                    options.ArchiveOutputPath = Value();
                    break;
                case "--sevenzip":
                    options.SevenZipExecutablePath = Value();
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException("Unknown option: " + name);
            }
        }
        return options;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("chart-info-export");
        Console.WriteLine("  --source <path>       source song.db path");
        Console.WriteLine("  --out <path>          output chart-info-metadata.db path");
        Console.WriteLine("  --archive-out <path>  optional output chart-info-metadata.7z path");
        Console.WriteLine("  --sevenzip <path>     optional 7z.exe path");
    }
}
