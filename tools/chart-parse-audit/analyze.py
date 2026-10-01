"""3つのJSONLを同順に読み、旧新位置比較の集計とCSVを出力します。"""

import argparse
import collections
import contextlib
import csv
import json
import pathlib
import statistics


def quantiles(values):
    """数値列だけを保持し、分位値を集計します。"""
    if not values:
        return {}
    ordered = sorted(values)
    return {str(percent): ordered[round((len(ordered) - 1) * percent / 100)]
            for percent in (0, 25, 50, 75, 90, 95, 99, 100)}


def numeric_summary(values):
    """速度差の数値列だけから中央値・範囲・分位値を要約します。未取得はnullです。"""
    return {"count": len(values), "median": statistics.median(values) if values else None,
            "minimum": min(values) if values else None, "maximum": max(values) if values else None,
            "quantiles": quantiles(values)}


def read_record(stream):
    """改行のない不完全末尾だけをprefix終了とし、完全行の不正JSONは失敗させます。"""
    line = stream.readline()
    if not line:
        return None
    try:
        return json.loads(line)
    except json.JSONDecodeError:
        if not line.endswith("\n"):
            return None
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, type=pathlib.Path)
    parser.add_argument("--out", required=True, type=pathlib.Path)
    parser.add_argument("--min-ms-increase", type=float)
    parser.add_argument("--min-speed-ratio", type=float)
    parser.add_argument("--min-tick-difference", type=int)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    run = json.loads((args.input / "run.json").read_text(encoding="utf-8"))
    fields = ["caseId", "rowId", "path", "sample", "repeat", "seed", "choices",
              "classification", "legacyStatus", "currentStatus", "comparisonComplete"]
    numeric_fields = ["maxAbsoluteTickDifference", "durationDifference", "minimumSignedTickDifference",
                      "maximumSignedTickDifference", "maxDifferenceKey", "added", "removed",
                      "valueDifferences", "tickDifferences", "measureStartDifferences", "barLineDifferences",
                      "maxBoundaryAbsoluteTickDifference", "maxBoundaryDifferenceKey"]
    counts = collections.Counter()
    groups = collections.defaultdict(list)
    current_ms, speed_ratios, ms_increases = [], [], []
    total_fields = ("added", "removed", "valueDifferences", "tickDifferences",
                    "measureStartDifferences", "barLineDifferences")
    totals = dict.fromkeys(total_fields, 0)
    numeric_cases = 0
    maximum_tick, maximum_boundary = None, None
    minimum_signed, maximum_signed = None, None
    minimum_duration, maximum_duration = None, None
    duration_cases = 0
    prefix = 0
    with contextlib.ExitStack() as stack:
        inputs = [stack.enter_context((args.input / name).open(encoding="utf-8"))
                  for name in ("cases.jsonl", "results.jsonl", "comparisons.jsonl")]
        def output(name, columns):
            stream = stack.enter_context((args.out / name).open("w", encoding="utf-8-sig", newline=""))
            writer = csv.DictWriter(stream, fieldnames=columns, extrasaction="ignore")
            writer.writeheader()
            return writer
        observations = output("observations.csv", fields)
        compatibility = output("compatibility.csv", fields)
        numeric = output("numeric.csv", fields + numeric_fields)
        speed = output("speed.csv", fields + ["legacyMs", "currentMs", "millisecondsIncrease", "speedRatio"])
        repetitions = output("repetitions.csv", ["rowId", "sample", "count", "legacyMedianMs", "currentMedianMs",
                                               "legacyMinMs", "legacyMaxMs", "currentMinMs", "currentMaxMs", "currentStddevMs"])
        while True:
            case = read_record(inputs[0])
            if case is None:
                break
            legacy, current = read_record(inputs[1]), read_record(inputs[1])
            comparison = read_record(inputs[2])
            if legacy is None or current is None or comparison is None:
                break
            identifier = case["id"]
            if any(row["caseId"] != identifier for row in (legacy, current, comparison)) or legacy["engine"] != "legacy" or current["engine"] != "current":
                raise ValueError("JSONLのケース順が一致しません。")
            # 分類・速度・数値集計はRunnerの値をそのまま採用します。
            classification = comparison["classification"]
            record = {"caseId": identifier, "rowId": case["rowId"], "path": case["originalPath"],
                      "sample": case["sample"], "repeat": case["repeat"], "seed": case["seed"],
                      "choices": json.dumps(case["choices"], ensure_ascii=False), "classification": classification,
                      "legacyStatus": legacy["status"], "currentStatus": current["status"],
                      "comparisonComplete": comparison["comparisonComplete"]}
            observations.writerow(record)
            counts[classification] += 1
            prefix += 1
            # Runnerが全件比較を完了した件数だけを合計します。場所や詳細は保持しません。
            if comparison["comparisonComplete"]:
                numeric_cases += 1
                for name in total_fields:
                    totals[name] += int(comparison[name])
                tick_value = int(comparison["maxAbsoluteTickDifference"])
                boundary_value = int(comparison["maxBoundaryAbsoluteTickDifference"])
                maximum_tick = tick_value if maximum_tick is None else max(maximum_tick, tick_value)
                maximum_boundary = boundary_value if maximum_boundary is None else max(maximum_boundary, boundary_value)
                if comparison["minimumSignedTickDifference"] is not None:
                    value = int(comparison["minimumSignedTickDifference"])
                    minimum_signed = value if minimum_signed is None else min(minimum_signed, value)
                if comparison["maximumSignedTickDifference"] is not None:
                    value = int(comparison["maximumSignedTickDifference"])
                    maximum_signed = value if maximum_signed is None else max(maximum_signed, value)
            # Durationは比較不能でも両解析結果から独立して既知になり得ます。
            if comparison["durationDifference"] is not None:
                value = int(comparison["durationDifference"])
                minimum_duration = value if minimum_duration is None else min(minimum_duration, value)
                maximum_duration = value if maximum_duration is None else max(maximum_duration, value)
                duration_cases += 1
            if classification in ("legacy-only-success", "current-only-success", "both-failed", "input-error", "incomparable"):
                compatibility.writerow(record)
            tick = comparison["maxAbsoluteTickDifference"]
            if tick is not None:
                # 任意精度整数のまま抽出し、2^53超のtickもfloatにしません。
                difference = int(tick)
                if args.min_tick_difference is None or difference >= args.min_tick_difference:
                    numeric.writerow({**record, **{key: comparison[key] for key in numeric_fields}})
            ratio, increase = comparison["speedRatio"], comparison["millisecondsIncrease"]
            if comparison["comparisonComplete"] and increase is not None:
                old, new = legacy["parseMilliseconds"], current["parseMilliseconds"]
                groups[(case["rowId"], case["sample"])].append((old, new))
                current_ms.append(new)
                ms_increases.append(increase)
                if ratio is not None:
                    speed_ratios.append(ratio)
                if (args.min_ms_increase is None or increase >= args.min_ms_increase) and (args.min_speed_ratio is None or (ratio is not None and ratio >= args.min_speed_ratio)):
                    speed.writerow({**record, "legacyMs": old, "currentMs": new,
                                    "millisecondsIncrease": increase, "speedRatio": ratio})
        for (row_id, sample), values in groups.items():
            old = [pair[0] for pair in values]
            new = [pair[1] for pair in values]
            repetitions.writerow({"rowId": row_id, "sample": sample, "count": len(values),
                                   "legacyMedianMs": statistics.median(old), "currentMedianMs": statistics.median(new),
                                   "legacyMinMs": min(old), "legacyMaxMs": max(old), "currentMinMs": min(new),
                                   "currentMaxMs": max(new), "currentStddevMs": statistics.pstdev(new)})
    report = {"schema": 2, "runStatus": run["status"], "completePrefixCases": prefix,
              "unsubmitted": run["unsubmitted"], "counts": dict(counts), "speedEligiblePairs": len(current_ms),
              "parseMsQuantiles": quantiles(current_ms),
              "speedRatio": numeric_summary(speed_ratios), "millisecondsIncrease": numeric_summary(ms_increases),
              "numeric": {"completePairs": numeric_cases,
                          "totals": {name: value if numeric_cases else None for name, value in totals.items()},
                          "maxAbsoluteTickDifference": maximum_tick, "maxBoundaryAbsoluteTickDifference": maximum_boundary,
                          "minimumSignedTickDifference": minimum_signed, "maximumSignedTickDifference": maximum_signed,
                          "durationDifference": {"count": duration_cases, "minimum": minimum_duration, "maximum": maximum_duration}}}
    (args.out / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    summary = ["# 譜面位置比較の集計", "", f"実行状態: {run['status']}。完全prefix: {prefix}ケース。速度統計: {len(current_ms)}対。", "",
               "| 分類 | 件数 |", "| --- | --- |"]
    summary += [f"| {name} | {count} |" for name, count in sorted(counts.items())]
    summary += ["", "各CSVはJSONLと同じケース順です。Runnerの分類をそのまま採用し、抽出閾値は候補の絞込みに使います。",
                "反復中央値とばらつきはrepetitions.csv、速度差の中央値・範囲・分位値と数値差の全体集計はreport.json、caseごとの整数tick差はnumeric.csvを参照します。"]
    (args.out / "report.ja.md").write_text("\n".join(summary) + "\n", encoding="utf-8", newline="\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
