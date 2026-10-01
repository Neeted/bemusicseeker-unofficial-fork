#nullable enable
using System;

namespace Ribbit.BMS;

/// <summary>解析1回にだけ適用する乱数源と任意診断です。未指定の本番解析では診断処理を行いません。</summary>
public sealed class BmsParseOptions
{
    /// <summary>Queueで指定されなかった有効RANDOMを選ぶ乱数源です。</summary>
    public Random? RandomSource { get; init; }
    /// <summary>実際に読み取ったbyte列の識別を、譜面解析前に通知します。</summary>
    public Action<BmsInputIdentity>? InputRead { get; init; }
    /// <summary>履歴へ追加したRANDOMのRange/Value/Usedを一件ずつ一度だけ通知します。途中の失敗でも通知済み選択を回収できます。</summary>
    public Action<BMSFile.RandomNumber>? RandomChoice { get; init; }
    /// <summary>診断する小節を選びます。未指定なら全小節です。入力診断には適用しません。</summary>
    public Predicate<int>? DiagnoseMeasure { get; init; }
    /// <summary>入力、演算前、結果と小節の開始・途中・完了を通知します。</summary>
    public Action<BmsTimingDiagnostic>? TimingDiagnostic { get; init; }
}

/// <summary>追加のreadなしで取得した解析入力の識別です。</summary>
public sealed record BmsInputIdentity(string Md5, long ByteCount, string EncodingWebName, int EncodingCodePage);

/// <summary>演算前は上界、結果は実測bit数、inputは係数桁数と十進指数を持つ診断です。</summary>
public sealed record BmsTimingDiagnostic(int Measure, string Stage, long NumeratorBits, long DenominatorBits, long OperandBitUpperBound = 0, int CoefficientDigits = 0, string? DecimalExponent = null);
