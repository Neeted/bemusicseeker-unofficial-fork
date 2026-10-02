extern alias legacy;
extern alias current;

using System.Diagnostics;
using System.Text;
using System.Security.Cryptography;
using LegacyEngine = legacy::ChartParseAudit.EngineEntry;
using CurrentEngine = current::ChartParseAudit.EngineEntry;

namespace ChartParseAudit;

/// <summary>固定数のTaskワーカーが担当行を一度だけ準備し、旧新対を比較して同順のJSONLへ保存します。</summary>
public static class AuditRunner
{
    /// <summary>Ctrl+C後は次caseと次rowを受理しません。受理済caseの旧新比較・保存は自然終了し、出力IO失敗は全体失敗になります。</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        var gate = new object();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; lock (gate) cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            Options options = Options.Parse(args);
            if (Directory.Exists(options.Out) && Directory.EnumerateFileSystemEntries(options.Out).Any()) throw new IOException("output directory must be empty");
            Directory.CreateDirectory(options.Out);
            using var cases = new StreamWriter(Path.Combine(options.Out, "cases.jsonl"), false, new UTF8Encoding(false), 65536);
            using var results = new StreamWriter(Path.Combine(options.Out, "results.jsonl"), false, new UTF8Encoding(false), 65536);
            using var comparisons = new StreamWriter(Path.Combine(options.Out, "comparisons.jsonl"), false, new UTF8Encoding(false), 65536);
            var rows = Inputs.Read(options).Select(row =>
            {
                (string? path, string? skip) = Inputs.Resolve(row.Path, options.ChartRoot);
                return (row.RowId, Original: row.Path, Path: path, Skip: skip);
            }).ToArray();
            long totalCases = checked((long)rows.Length * options.Samples * options.Repeat);
            long planned = rows.LongCount(row => row.Skip == null);
            long started = 0, submitted = 0, completed = 0;
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            DateTime began = DateTime.UtcNow;
            var progressWatch = Stopwatch.StartNew();
            long lastProgress = 0;
            void Progress(bool final = false)
            {
                if (final || started == 1 || progressWatch.ElapsedMilliseconds - lastProgress >= 500)
                {
                    Console.Error.WriteLine($"{(final ? "処理終了" : "処理開始")}: {started} / {planned} 譜面");
                    lastProgress = progressWatch.ElapsedMilliseconds;
                }
            }
            void SaveRun(string status, string? error = null) => File.WriteAllText(Path.Combine(options.Out, "run.json"), Wire.Encode(new
            {
                schema = Wire.Schema,
                status,
                beganUtc = began,
                endedUtc = status == "running" ? (DateTime?)null : DateTime.UtcNow,
                options,
                totalCases,
                submitted,
                completed,
                unsubmitted = totalCases - submitted,
                planned,
                started,
                counts,
                error
            }), new UTF8Encoding(false));
            Console.Error.WriteLine($"処理開始: 0 / {planned} 譜面");
            SaveRun("running");
            Exception? failure = null;
            try
            {
                const string source = "#BPM 120\n#00011:01\n";
                byte[] warmupBytes = Encoding.ASCII.GetBytes(source);
                var warmup = new PreparedInput("warmup.bms", source, Encoding.ASCII, Convert.ToHexStringLower(MD5.HashData(warmupBytes)), warmupBytes.LongLength);
                LegacyEngine.Warmup(warmup, options.Culture);
                CurrentEngine.Warmup(warmup, options.Culture);
                int next = 0;
                void Consume()
                {
                    try
                    {
                        while (true)
                        {
                            int index;
                            lock (gate)
                            {
                                if (cancellation.IsCancellationRequested || next == rows.Length) return;
                                index = next++;
                                if (rows[index].Skip == null) { started++; Progress(); }
                            }
                            var row = rows[index];
                            PreparedInput? input = null;
                            string? preparationError = null;
                            if (row.Skip == null && row.Path != null)
                            {
                                try { input = CurrentEngine.PrepareInput(row.Path); }
                                catch (Exception ex) { preparationError = ex.ToString(); }
                            }
                            void CompareAndSave(AuditCase item)
                            {
                                ParseOutcome Execute(string engine) => input != null
                                    ? engine == "legacy" ? LegacyEngine.Run(input, item, engine, options.Culture) : CurrentEngine.Run(input, item, engine, options.Culture)
                                    : new(new Execution(item.Id, engine, row.Skip == null ? "input-error" : "skip", null, null, null, null, row.Skip ?? preparationError));
                                ParseOutcome first = Execute(item.Order[0]), second = Execute(item.Order[1]);
                                ParseOutcome oldResult = item.Order[0] == "legacy" ? first : second;
                                ParseOutcome newResult = item.Order[0] == "current" ? first : second;
                                ComparisonResult comparison = Comparison.Compare(item.Id, oldResult, newResult);
                                // 一対の保存を同じlockで完了させ、3JSONLのケース順を一致させます。
                                lock (gate)
                                {
                                    cases.WriteLine(Wire.Encode(item));
                                    results.WriteLine(Wire.Encode(oldResult.Record));
                                    results.WriteLine(Wire.Encode(newResult.Record));
                                    comparisons.WriteLine(Wire.Encode(comparison));
                                    completed++;
                                    counts[comparison.Classification] = counts.GetValueOrDefault(comparison.Classification) + 1;
                                }
                            }
                            foreach (AuditCase item in Inputs.Cases(row.RowId, row.Original, row.Path, row.Skip, options))
                            {
                                lock (gate)
                                {
                                    if (cancellation.IsCancellationRequested) return;
                                    submitted++;
                                }
                                CompareAndSave(item);
                            }
                        }
                    }
                    catch { lock (gate) cancellation.Cancel(); throw; }
                }
                await Task.WhenAll(Enumerable.Range(0, options.Workers).Select(_ => Task.Run(Consume)));
            }
            catch (Exception ex) { failure = ex; }
            try { cases.Flush(); results.Flush(); comparisons.Flush(); }
            catch (Exception ex) { failure ??= ex; }
            string state = failure != null ? "error" : cancellation.IsCancellationRequested ? "cancelled" : "complete";
            Progress(true);
            SaveRun(state, failure?.ToString());
            if (failure != null) Console.Error.WriteLine(failure);
            return state == "complete" ? 0 : 2;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 2; }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
