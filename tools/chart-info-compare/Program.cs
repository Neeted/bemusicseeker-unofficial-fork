using System;
using System.IO;

namespace ChartInfoCompareTool;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            ChartInfoCompareOptions options = ParseOptions(args);
            ChartInfoCompareResult result = ChartInfoCompareRunner.Compare(options);
            Console.WriteLine(result.ToConsoleSummary());
            return result.HasNonRandomProblems ? 2 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static ChartInfoCompareOptions ParseOptions(string[] args)
    {
        ChartInfoCompareOptions options = new ChartInfoCompareOptions();
        string toolRoot = AppDomain.CurrentDomain.BaseDirectory;
        options.OutputDirectory = Path.GetFullPath(Path.Combine(toolRoot, "..", "..", "..", "reports", "latest"));
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
                case "--app-db":
                    options.AppSongDbPath = Value();
                    break;
                case "--beatoraja-song-db":
                    options.BeatorajaSongDbPath = Value();
                    break;
                case "--beatoraja-info-db":
                    options.BeatorajaInfoDbPath = Value();
                    break;
                case "--out":
                    options.OutputDirectory = Value();
                    break;
                case "--export-fixture":
                    options.FixtureOutputDirectory = Value();
                    break;
                case "--max-fixture-count":
                    options.MaxFixtureCount = int.Parse(Value());
                    break;
                case "--overwrite-fixture":
                    options.OverwriteFixture = true;
                    break;
                case "--no-reports":
                    options.OutputDirectory = null;
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
        Console.WriteLine("chart-info-compare");
        Console.WriteLine("  --app-db <path>              app song.db path");
        Console.WriteLine("  --beatoraja-song-db <path>   beatoraja songdata.db path");
        Console.WriteLine("  --beatoraja-info-db <path>   beatoraja songinfo.db path");
        Console.WriteLine("  --out <dir>                  report output directory");
        Console.WriteLine("  --export-fixture <dir>       export non-RANDOM diff fixtures when count <= max");
        Console.WriteLine("  --max-fixture-count <n>      default 1000");
        Console.WriteLine("  --overwrite-fixture          allow deleting existing fixture output");
    }
}
