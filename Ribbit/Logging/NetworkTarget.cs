using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NLog.Common;
using NLog.Layouts;
using NLog.Targets;

namespace Ribbit.Logging;

/// <summary>
/// Sends an error report to the application's HTTP endpoint without making
/// logging failures observable to the application route.
/// </summary>
public sealed class NetworkTarget : AsyncTaskTarget
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    public NetworkTarget()
    {
        Layout = Layout.FromString("${longdate}|${level:uppercase=true}|${logger}|${message:withexception=true}");
    }

    public string Address { get; set; }

    protected override async Task WriteAsyncTask(LogEventInfo logEvent, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Address))
        {
            return;
        }

        try
        {
            string message = Layout.Render(logEvent);
            using var content = new StringContent(message, Encoding.UTF8, "text/plain");
            using HttpResponseMessage response = await Client.PostAsync(Address, content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            InternalLogger.Warn(exception, "Network log delivery failed.");
        }
    }
}

internal sealed class TraceTarget : TargetWithLayout
{
    protected override void Write(LogEventInfo logEvent)
    {
        Trace.WriteLine(Layout.Render(logEvent));
    }
}
