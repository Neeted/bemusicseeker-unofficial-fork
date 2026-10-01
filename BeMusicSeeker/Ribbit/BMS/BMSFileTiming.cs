#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Ribbit.Logging;
using Ribbit.Math;
using Ribbit.Util.Extensions;

namespace Ribbit.BMS;

public partial class BMSFile
{
    private void RecordRandomChoice(RandomNumber choice)
    {
        randomPattern.Add(choice);
        parseOptions?.RandomChoice?.Invoke(choice);
    }

    private bool TryParseNumber(string text, bool decimalSyntax, out BmsNumber value)
    {
        value = default;
        bool wasParsedAsPositiveInfinity = false;
        // 旧TryParseの数値は演算へ戻さず、特殊STOPの受理に必要な正∞分類だけを保持します。
        if (decimalSyntax)
        {
            if (!decimal.TryParse(text, out decimal parsed) || parsed <= 0) return false;
        }
        else
        {
            if (!double.TryParse(text, out double parsed) || !(parsed > 0)) return false;
            wasParsedAsPositiveInfinity = double.IsPositiveInfinity(parsed);
        }

        NumberFormatInfo format = NumberFormatInfo.CurrentInfo;
        string input = text.Trim().TrimEnd('\0').TrimEnd();
        if (!decimalSyntax && (input.Equals(format.PositiveInfinitySymbol, StringComparison.OrdinalIgnoreCase)
            || input.Equals(format.PositiveSign + format.PositiveInfinitySymbol, StringComparison.OrdinalIgnoreCase)))
        {
            value = BmsNumber.PositiveInfinity;
            return true;
        }
        if (input.StartsWith(format.PositiveSign, StringComparison.Ordinal)) input = input[format.PositiveSign.Length..];
        if (decimalSyntax && input.EndsWith(format.PositiveSign, StringComparison.Ordinal)) input = input[..^format.PositiveSign.Length].TrimEnd();
        BigInteger exponent = BigInteger.Zero;
        int exponentAt = input.IndexOfAny(['e', 'E']);
        if (!decimalSyntax && exponentAt >= 0)
        {
            exponent = BigInteger.Parse(input[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, format);
            input = input[..exponentAt];
        }
        if (!string.IsNullOrEmpty(format.NumberGroupSeparator)) input = input.Replace(format.NumberGroupSeparator, string.Empty);
        // .NETの空白区切りカルチャはASCII空白も区切りとして受理します。
        if (format.NumberGroupSeparator is "\u00a0" or "\u202f") input = input.Replace(" ", string.Empty);
        int point = input.IndexOf(format.NumberDecimalSeparator, StringComparison.Ordinal);
        if (point >= 0)
        {
            exponent -= input.Length - point - format.NumberDecimalSeparator.Length;
            input = input.Remove(point, format.NumberDecimalSeparator.Length);
        }
        parseOptions?.TimingDiagnostic?.Invoke(new BmsTimingDiagnostic(-1, "input", 0, 0, CoefficientDigits: input.Length,
            DecimalExponent: exponent.ToString(CultureInfo.InvariantCulture)));
        var coefficient = BigInteger.Parse(input, NumberStyles.None, CultureInfo.InvariantCulture);
        BigInteger scale = PowerOfTen(BigInteger.Abs(exponent));
        value = new BmsNumber(exponent.Sign < 0 ? new Fraction(coefficient, scale) : new Fraction(coefficient * scale, BigInteger.One),
            wasParsedAsPositiveInfinity);
        return true;
    }

    private static BigInteger PowerOfTen(BigInteger exponent)
    {
        if (exponent <= int.MaxValue) return BigInteger.Pow(10, (int)exponent);
        BigInteger result = BigInteger.One;
        BigInteger power = 10;
        while (exponent > 0)
        {
            if (!exponent.IsEven) result *= power;
            exponent >>= 1;
            if (exponent > 0) power *= power;
        }
        return result;
    }

    private void CalculateMeasureTiming()
    {
        BmsNumber currentBpm = Bpm.GetValueOrDefault();
        MinBpm = MaxBpm = currentBpm;
        for (int measureIndex = 0; measureIndex <= Measures.LastIndex; measureIndex++)
        {
            Chart measure = Measures[measureIndex];
            measure.Control = [.. measure.GetPropertiesAllControlNotes.SelectMany(c => c()).OrderByNotes()];
            IList<Chart.Note>[] lanes = [.. measure.GetPropertiesAllNotes.Select(c => c())];
            int[] cursors = new int[lanes.Length];
            measure.Time = measureIndex == 0 ? TimeSpan.Zero : Measures[measureIndex - 1].BarLine.First().AbsoluteTime;
            Action<BmsTimingDiagnostic>? diagnostic = parseOptions?.TimingDiagnostic;
            if (diagnostic != null && parseOptions?.DiagnoseMeasure?.Invoke(measureIndex) == false) diagnostic = null;

            void Observe(string stage, Fraction result)
            {
                if (diagnostic != null) diagnostic(new BmsTimingDiagnostic(measureIndex, stage,
                    BigInteger.Abs(result.Numerator).GetBitLength(), result.Denominator.GetBitLength()));
            }
            Fraction Operate(string operation, Fraction left, Fraction right)
            {
                if (diagnostic != null)
                {
                    long ln = BigInteger.Abs(left.Numerator).GetBitLength(), ld = left.Denominator.GetBitLength();
                    long rn = BigInteger.Abs(right.Numerator).GetBitLength(), rd = right.Denominator.GetBitLength();
                    long upper = operation switch
                    {
                        "add" => System.Math.Max(System.Math.Max(ln + rd, rn + ld) + 1, ld + rd),
                        "multiply" => System.Math.Max(ln + rn, ld + rd),
                        _ => System.Math.Max(ln + rd, ld + rn)
                    };
                    diagnostic(new BmsTimingDiagnostic(measureIndex, "before-" + operation, 0, 0, upper));
                }
                Fraction result = operation switch { "add" => left + right, "multiply" => left * right, _ => left / right };
                Observe("result", result);
                return result;
            }
            Fraction Ratio(long ticks, BmsNumber numerator)
            {
                // 旧STOP×係数の順序では、正∞STOPでも正∞BPMの係数ゼロが優先されます。
                if (currentBpm.IsPositiveInfinity) return Fraction.Zero;
                if (numerator.IsPositiveInfinity)
                {
                    // 有限文字列BPMの厳密値は維持し、明示∞STOPだけ旧パース時のゼロ作用に従います。
                    if (currentBpm.WasParsedAsPositiveInfinity) return Fraction.Zero;
                    throw new ArithmeticException(BeMusicSeeker.Properties.Resources.BmsInfiniteStopTimingFailure);
                }
                Fraction bpm = currentBpm.FiniteValue.GetValueOrDefault();
                return Operate("multiply", Operate("divide", ticks, bpm), numerator.FiniteValue.GetValueOrDefault());
            }
            Fraction accumulated = Fraction.Zero;
            Fraction previousPosition = Fraction.Zero;
            Fraction groupPosition = Fraction.Zero;
            Fraction groupTime = Fraction.Zero;
            Fraction? coefficient = null;
            bool firstControl = true;
            Observe("measure-start", accumulated);
            // 旧小節長はBPM係数を掛ける前に位置へ適用され、正∞BPMでも確定できません。
            if (measure.Length.IsPositiveInfinity)
                throw new ArithmeticException(BeMusicSeeker.Properties.Resources.BmsInfiniteMeasureLengthTimingFailure);
            Fraction TimeAt(Fraction position)
            {
                Fraction distance = Operate("add", position, -previousPosition);
                // 旧処理では距離0の時刻は変わらず、末尾BPM0や次小節先頭での復帰に係数は不要です。
                if (distance == Fraction.Zero) return accumulated;
                coefficient ??= Ratio(2400000000L, measure.Length);
                return Operate("add", accumulated, Operate("multiply", distance, coefficient.Value));
            }
            foreach (Chart.Note control in measure.Control)
            {
                if (firstControl || control.Position != groupPosition)
                {
                    groupTime = TimeAt(control.Position);
                    // 同位置の全ノーツは制御前時刻です。制御自体には整数tickを逆流させません。
                    for (int laneIndex = 0; laneIndex < lanes.Length; laneIndex++)
                    {
                        IList<Chart.Note> lane = lanes[laneIndex];
                        while (cursors[laneIndex] < lane.Count && lane[cursors[laneIndex]].Position <= control.Position)
                        {
                            Chart.Note note = lane[cursors[laneIndex]++];
                            Fraction time = note.Position == control.Position ? groupTime : TimeAt(note.Position);
                            note.AbsoluteTime = TimeSpan.FromTicks(checked(measure.Time.Ticks + time.ToInt64()));
                        }
                    }
                    groupPosition = control.Position;
                    firstControl = false;
                }
                // 旧処理の同位置resetを保持し、STOPは最後のものだけを後続へ反映します。
                accumulated = groupTime;
                previousPosition = control.Position;
                bool changedBpm = false;
                switch (control.Type)
                {
                    case Chart.Note.NoteType.BPM:
                        currentBpm = (int)control.Value;
                        changedBpm = true;
                        break;
                    case Chart.Note.NoteType.EX_BPM:
                        if (BpmArray[control.Index] is BmsNumber bpm)
                        {
                            currentBpm = bpm;
                            control.Value = bpm;
                            changedBpm = true;
                        }
                        else NLogWrapper.GetLogger()?.Warn("BMS Parser: #BPM" + BMSBase64.FromInt(control.Index) + " not found.");
                        break;
                    case Chart.Note.NoteType.STOP:
                        if (StopArray[control.Index] is BmsNumber stop)
                        {
                            Fraction increment = Ratio(12500000L, stop);
                            accumulated = Operate("add", accumulated, increment);
                            control.Value = TimeSpan.FromTicks(increment.ToInt64());
                        }
                        else NLogWrapper.GetLogger()?.Warn("BMS Parser: #STOP" + BMSBase64.FromInt(control.Index) + " not found.");
                        break;
                }
                if (changedBpm)
                {
                    if (currentBpm.CompareTo(MinBpm.GetValueOrDefault()) < 0) MinBpm = currentBpm;
                    if (currentBpm.CompareTo(MaxBpm.GetValueOrDefault()) > 0) MaxBpm = currentBpm;
                    coefficient = null;
                }
                Observe("control", accumulated);
            }
            Observe("measure-complete", accumulated);
        }
    }
}
