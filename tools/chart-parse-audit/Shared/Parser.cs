#nullable enable
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Ribbit.BMS;

namespace ChartParseAudit;

/// <summary>旧新の準備済み入力入口を同じ計時境界で同期呼出しします。</summary>
public static class EngineEntry
{
#if CURRENT
    /// <summary>通常の共有読込みとdecoderで担当行を一度だけ準備します。準備失敗は呼出側でinput-errorとして記録します。</summary>
    public static PreparedInput PrepareInput(string path) => Engine.Read(path);
#endif
    /// <summary>共通のメモリ上の小本文をrun開始時に一度だけ解析します。</summary>
    public static void Warmup(PreparedInput input, string culture)
    {
        Initialize(culture);
        NLog.LogManager.GlobalThreshold = NLog.LogLevel.Off;
        Engine.PrepareParse(input, null, new Random(0))();
    }
    /// <summary>options・独立乱数・Queueを計時前に準備し、同期解析入口全体だけを計時します。拒否はparse-errorとして返し、結果射影は遅延列挙します。</summary>
    public static ParseOutcome Run(PreparedInput input, AuditCase item, string engine, string culture)
    {
        Initialize(culture);
        Func<BMSFile> parse = Engine.PrepareParse(input, item.Choices == null ? null : new Queue<int>(item.Choices), new Random(item.Seed));
        var watch = new Stopwatch();
        BMSFile? file = null;
        Exception? error = null;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        watch.Start();
        try { file = parse(); }
        catch (Exception ex) { error = ex; }
        finally { watch.Stop(); }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var identity = new InputIdentity(input.Md5, input.Bytes, input.Encoding.WebName);
        var record = new Execution(item.Id, engine, file == null ? "parse-error" : "ok", identity, file?.Duration.Ticks, watch.Elapsed.TotalMilliseconds, bytes, error?.ToString());
        return file == null ? new(record) : new(record, Projection.Rows(file), Pattern(file));
    }
    private static IEnumerable<Choice> Pattern(BMSFile file)
    {
        foreach (BMSFile.RandomNumber item in file.RandomPattern) yield return new(item.Range, item.Value, item.Used);
    }
    private static void Initialize(string culture)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
    }
}
