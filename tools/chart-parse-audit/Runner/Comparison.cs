using System.Globalization;
using System.Numerics;

namespace ChartParseAudit;

/// <summary>全件照合の集計と、上限付きの差例を区別します。</summary>
public sealed record ComparisonResult(string CaseId, string Classification, bool ComparisonComplete, long? Added, long? Removed, long? ValueDifferences, long? TickDifferences, string? MaxAbsoluteTickDifference, string? MinimumSignedTickDifference, string? MaximumSignedTickDifference, string? DurationDifference, string? MaxDifferenceKey, PositionDifference[] Examples, bool ExamplesTruncated, double? SpeedRatio, double? MillisecondsIncrease, string? Error, long? MeasureStartDifferences = null, long? BarLineDifferences = null, string? MaxBoundaryAbsoluteTickDifference = null, string? MaxBoundaryDifferenceKey = null);
/// <summary>先頭20件だけ文字列化した差例です。</summary>
public sealed record PositionDifference(string Kind, string Key, RowExample? Legacy, RowExample? Current);
/// <summary>差例のValueと整数tickです。全件の射影は保存しません。</summary>
public sealed record RowExample(long? Tick, string Value);

/// <summary>時間や列挙順のzipに依存せず、raw結果の型付き位置列を逐次突合します。</summary>
public static class Comparison
{
    /// <summary>同じ準備入力の成功対を遅延列挙で突合します。skip・準備失敗・解析失敗を区別し、不完全な集計と速度はnullにします。</summary>
    public static ComparisonResult Compare(string caseId, ParseOutcome legacy, ParseOutcome current)
    {
        Execution oldRecord = legacy.Record, newRecord = current.Record;
        string? duration = oldRecord.DurationTicks is long oldDuration && newRecord.DurationTicks is long newDuration ? ((BigInteger)newDuration - oldDuration).ToString(CultureInfo.InvariantCulture) : null;
        ComparisonResult Empty(string classification, string? error = null) => new(caseId, classification, false, null, null, null, null, null, null, null, duration, null, [], false, null, null, error);
        if (oldRecord.Status == "skip" || newRecord.Status == "skip") return Empty("skip");
        if (oldRecord.Status == "input-error" || newRecord.Status == "input-error") return Empty("input-error");
        bool oldOk = oldRecord.Status == "ok", newOk = newRecord.Status == "ok";
        if (oldOk && !newOk) return Empty("legacy-only-success");
        if (!oldOk && newOk) return Empty("current-only-success");
        if (!oldOk && !newOk) return Empty("both-failed");
        if (legacy.Rows == null || current.Rows == null || legacy.RandomPattern == null || current.RandomPattern == null) return Empty("incomparable", "解析結果の列挙がありません。");
        try
        {
            if (!legacy.RandomPattern.SequenceEqual(current.RandomPattern)) return Empty("incomparable", "RandomPatternが異なります。");
        }
        catch (Exception ex) { return Empty("incomparable", ex.ToString()); }
        long added = 0, removed = 0, values = 0, ticks = 0;
        long structuralValues = 0;
        long measureStarts = 0, barLines = 0;
        BigInteger maximumBoundary = 0;
        PositionKey? maximumBoundaryKey = null;
        BigInteger maximum = 0;
        BigInteger? minimumSigned = null, maximumSigned = null;
        PositionKey? maximumKey = null;
        var examples = new List<PositionDifference>();
        long totalExamples = 0;
        void Boundary(PositionRow row, BigInteger? difference)
        {
            if (row.Kind == "measure") measureStarts++;
            else if (row.Kind == "bar-line") barLines++;
            else return;
            if (difference is BigInteger value && BigInteger.Abs(value) > maximumBoundary)
            {
                maximumBoundary = BigInteger.Abs(value); maximumBoundaryKey = row.Key;
            }
        }
        void Example(string kind, PositionRow? oldRow, PositionRow? newRow)
        {
            totalExamples++;
            if (examples.Count < 20) examples.Add(new PositionDifference(kind, (oldRow ?? newRow)?.Key.ToString() ?? "", oldRow is PositionRow old ? new(old.Tick, old.Value.ToString()) : null, newRow is PositionRow updated ? new(updated.Tick, updated.Value.ToString()) : null));
        }
        bool complete = false;
        string? error = null;
        try
        {
            using IEnumerator<PositionRow> oldRows = legacy.Rows.GetEnumerator();
            using IEnumerator<PositionRow> newRows = current.Rows.GetEnumerator();
            bool hasOld = oldRows.MoveNext(), hasNew = newRows.MoveNext();
            while (hasOld || hasNew)
            {
                int order = !hasOld ? 1 : !hasNew ? -1 : oldRows.Current.Key.CompareTo(newRows.Current.Key);
                if (order < 0) { removed++; Boundary(oldRows.Current, null); Example("removed", oldRows.Current, null); hasOld = oldRows.MoveNext(); continue; }
                if (order > 0) { added++; Boundary(newRows.Current, null); Example("added", null, newRows.Current); hasNew = newRows.MoveNext(); continue; }
                PositionRow oldRow = oldRows.Current, newRow = newRows.Current;
                if (oldRow.Kind != newRow.Kind || oldRow.Value != newRow.Value)
                {
                    values++;
                    bool numeric = oldRow.Kind == newRow.Kind && oldRow.Value.IsNumeric && newRow.Value.IsNumeric;
                    if (!numeric) structuralValues++;
                    Example(numeric ? "numeric-value" : "value", oldRow, newRow);
                }
                if (oldRow.Tick != newRow.Tick)
                {
                    ticks++;
                    Boundary(oldRow, oldRow.Tick.HasValue && newRow.Tick.HasValue ? (BigInteger)newRow.Tick.Value - oldRow.Tick.Value : null);
                    Example("tick", oldRow, newRow);
                    if (oldRow.Tick.HasValue && newRow.Tick.HasValue)
                    {
                        BigInteger difference = (BigInteger)newRow.Tick.Value - oldRow.Tick.Value;
                        BigInteger absolute = BigInteger.Abs(difference);
                        if (absolute > maximum) { maximum = absolute; maximumKey = oldRow.Key; }
                        minimumSigned = minimumSigned.HasValue ? BigInteger.Min(minimumSigned.Value, difference) : difference;
                        maximumSigned = maximumSigned.HasValue ? BigInteger.Max(maximumSigned.Value, difference) : difference;
                    }
                }
                hasOld = oldRows.MoveNext(); hasNew = newRows.MoveNext();
            }
            complete = true;
        }
        catch (Exception ex) { error = ex.ToString(); }
        string classification = !complete ? "incomparable" : added + removed + structuralValues > 0 ? "structural-diff" : (ticks + values > 0 || (duration != null && duration != "0")) ? "numeric-diff" : "equal";
        double? oldMs = oldRecord.ParseMilliseconds, newMs = newRecord.ParseMilliseconds;
        bool speedEligible = complete;
        return new(caseId, classification, complete,
            complete ? added : null, complete ? removed : null, complete ? values : null, complete ? ticks : null,
            complete ? maximum.ToString(CultureInfo.InvariantCulture) : null, complete ? minimumSigned?.ToString(CultureInfo.InvariantCulture) : null, complete ? maximumSigned?.ToString(CultureInfo.InvariantCulture) : null,
            duration, complete ? maximumKey?.ToString() : null, examples.ToArray(), totalExamples > examples.Count,
            speedEligible && oldMs > 0 ? newMs / oldMs : null, speedEligible ? newMs - oldMs : null, error,
            complete ? measureStarts : null, complete ? barLines : null,
            complete ? maximumBoundary.ToString(CultureInfo.InvariantCulture) : null, complete ? maximumBoundaryKey?.ToString() : null);
    }

}
