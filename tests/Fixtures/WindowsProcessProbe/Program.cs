using System.Text;
using System.Text.Json;

namespace BeMusicSeeker.WindowsProcessProbe;

/// <summary>fresh-process契約のpayload移管先です。未接続のprobeを成功にしません。</summary>
internal static class Program
{
    private static Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length == 3 && args[0] == "update-package")
        {
            return UpdaterPackageProbe.RunAsync(args[1], args[2]);
        }
        if (args.Length > 0 && args[0] == "runtime-isolation")
        {
            return RuntimeIsolationProbe.RunAsync(args[1..]);
        }
        if (args.Length > 0 && args[0] == "wpf-host-lifecycle")
        {
            return WpfHostLifecycleProbe.RunAsync(args[1..]);
        }
        if (args.Length == 2 && args[0] == "playback")
        {
            return Task.FromResult(PlaybackProbe.Run(args[1]));
        }
        if (args.Length == 2 && args[0] is "external-player" or "updater-gateway")
        {
            return RunProcessBoundaryAsync(args[0], args[1]);
        }
        Console.Error.WriteLine("指定されたprobeはまだ接続されていません: " + string.Join(" ", args));
        return Task.FromResult(2);
    }

    /// <summary>固定した実process境界だけを実行し、表明と回収の完了後に成功を公開します。</summary>
    private static async Task<int> RunProcessBoundaryAsync(string mode, string caseName)
    {
        try
        {
            switch ((mode, caseName))
            {
                case ("external-player", nameof(ExternalPlayerProcessProbe.WindowsProcessSessionPublishesItselfAsExitSender)):
                    new ExternalPlayerProcessProbe().WindowsProcessSessionPublishesItselfAsExitSender();
                    break;
                case ("external-player", nameof(ExternalPlayerProcessProbe.WindowsGatewaySubscribesBeforeStartingPreparedSession)):
                    new ExternalPlayerProcessProbe().WindowsGatewaySubscribesBeforeStartingPreparedSession();
                    break;
                case ("updater-gateway", nameof(UpdaterProcessGatewayProbe.WindowsGatewayAcceptsReadyOnlyAfterMarkerPublicationCompletes)):
                    await new UpdaterProcessGatewayProbe().WindowsGatewayAcceptsReadyOnlyAfterMarkerPublicationCompletes();
                    break;
                case ("updater-gateway", nameof(UpdaterProcessGatewayProbe.WindowsGatewayStopsUpdaterWhenReadyPublicationRemainsIncomplete)):
                    await new UpdaterProcessGatewayProbe().WindowsGatewayStopsUpdaterWhenReadyPublicationRemainsIncomplete();
                    break;
                case ("updater-gateway", nameof(UpdaterProcessGatewayProbe.WindowsGatewayAbortStopsUpdaterWhenDecisionPublicationFails)):
                    await new UpdaterProcessGatewayProbe().WindowsGatewayAbortStopsUpdaterWhenDecisionPublicationFails();
                    break;
                case ("updater-gateway", nameof(UpdaterProcessGatewayProbe.WindowsGatewayProceedNormalizesDecisionPublicationFailureAndStopsUpdater)):
                    await new UpdaterProcessGatewayProbe().WindowsGatewayProceedNormalizesDecisionPublicationFailureAndStopsUpdater();
                    break;
                case ("updater-gateway", nameof(UpdaterProcessGatewayProbe.WindowsGatewayRecoveryTerminatesHungProcessAndReportsVisibleFailure)):
                    await new UpdaterProcessGatewayProbe().WindowsGatewayRecoveryTerminatesHungProcessAndReportsVisibleFailure();
                    break;
                default: throw new ArgumentException("未知のprocess境界caseです: " + caseName);
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            foreach (System.Collections.DictionaryEntry diagnostic in exception.Data)
            {
                Console.Error.WriteLine($"{diagnostic.Key}: {diagnostic.Value}");
            }
            return 2;
        }
        Program.WriteResult(new { Case = caseName, Passed = true });
        return 0;
    }

    /// <summary>probeごとの変更不能な観測結果をUTF-8のJSON一行として親へ返します。</summary>
    internal static void WriteResult<T>(T result) => Console.WriteLine(JsonSerializer.Serialize(result));
}
