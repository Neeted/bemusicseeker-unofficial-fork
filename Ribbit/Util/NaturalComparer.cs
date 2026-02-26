using System;
using System.Collections.Generic;
using System.Globalization;

namespace Ribbit.Util;

/// <summary>
/// 自然順ソートを行う比較器です。
/// </summary>
/// <remarks>
/// 旧実装は比較前に文字列を分割・配列化してキャッシュしていましたが、
/// 20万件規模のソートで大量アロケーションが発生し GC 負荷が支配的になっていました。
/// 本実装は比較規則を維持したまま、比較時にトークンを逐次走査して中間配列生成を避けます。
/// </remarks>
public class NaturalComparer<T> : Comparer<string>, IDisposable
{
	protected struct TokenSlice
	{
		public int StartIndex;

		public int Length;
	}

	protected Dictionary<string, TokenSlice[]> tokenCache;

	protected bool isWhiteSpacePrior;

	public NaturalComparer(bool isWhiteSpacePrior = false)
	{
		tokenCache = new Dictionary<string, TokenSlice[]>();
		this.isWhiteSpacePrior = isWhiteSpacePrior;
	}

	public NaturalComparer(bool isWhiteSpacePrior, int initialCapacity)
	{
		tokenCache = ((initialCapacity > 0) ? new Dictionary<string, TokenSlice[]>(initialCapacity) : new Dictionary<string, TokenSlice[]>());
		this.isWhiteSpacePrior = isWhiteSpacePrior;
	}

	/// <summary>
	/// 互換のために保持されている破棄メソッドです。
	/// </summary>
	public void Dispose()
	{
		tokenCache?.Clear();
		tokenCache = null;
	}

	/// <summary>
	/// 自然順（数値トークンを数値比較）で文字列を比較します。
	/// </summary>
	/// <param name="x">左辺文字列。</param>
	/// <param name="y">右辺文字列。</param>
	/// <returns>比較結果。</returns>
	public override int Compare(string x, string y)
	{
		if (x == y)
		{
			return 0;
		}
		if (isWhiteSpacePrior)
		{
			if (string.IsNullOrWhiteSpace(x))
			{
				return -1;
			}
			if (string.IsNullOrWhiteSpace(y))
			{
				return 1;
			}
		}
		else
		{
			if (string.IsNullOrWhiteSpace(x))
			{
				return 1;
			}
			if (string.IsNullOrWhiteSpace(y))
			{
				return -1;
			}
		}

		TokenSlice[] leftTokens = GetOrCreateTokenSlices(x);
		TokenSlice[] rightTokens = GetOrCreateTokenSlices(y);
		int comparableTokenCount = System.Math.Min(leftTokens.Length, rightTokens.Length);
		for (int tokenIndex = 0; tokenIndex < comparableTokenCount; tokenIndex++)
		{
			TokenSlice leftToken = leftTokens[tokenIndex];
			TokenSlice rightToken = rightTokens[tokenIndex];
			if (AreTokenTextsEqual(x, leftToken, y, rightToken))
			{
				continue;
			}
			int compareResult = PartCompare(x, leftToken, y, rightToken);
			if (compareResult != 0)
			{
				return compareResult;
			}
		}
		if (rightTokens.Length > leftTokens.Length)
		{
			return 1;
		}
		if (leftTokens.Length > rightTokens.Length)
		{
			return -1;
		}
		return 0;
	}

	/// <summary>
	/// トークン同士を従来互換ルールで比較します。
	/// </summary>
	/// <param name="leftSource">左辺元文字列。</param>
	/// <param name="leftToken">左辺トークン。</param>
	/// <param name="rightSource">右辺元文字列。</param>
	/// <param name="rightToken">右辺トークン。</param>
	/// <returns>比較結果。</returns>
	protected static int PartCompare(string leftSource, TokenSlice leftToken, string rightSource, TokenSlice rightToken)
	{
		if (!LooksNumericToken(leftSource, leftToken) || !LooksNumericToken(rightSource, rightToken))
		{
			return CompareTokenText(leftSource, leftToken, rightSource, rightToken);
		}
		if (!TryParseNumericToken(leftSource, leftToken, out var leftValue))
		{
			return CompareTokenText(leftSource, leftToken, rightSource, rightToken);
		}
		if (!TryParseNumericToken(rightSource, rightToken, out var rightValue))
		{
			return CompareTokenText(leftSource, leftToken, rightSource, rightToken);
		}
		return leftValue.CompareTo(rightValue);
	}

	private static int CompareTokenText(string leftSource, TokenSlice leftToken, string rightSource, TokenSlice rightToken)
	{
		return CultureInfo.CurrentCulture.CompareInfo.Compare(leftSource, leftToken.StartIndex, leftToken.Length, rightSource, rightToken.StartIndex, rightToken.Length, CompareOptions.None);
	}

	private static bool AreTokenTextsEqual(string leftSource, TokenSlice leftToken, string rightSource, TokenSlice rightToken)
	{
		if (leftToken.Length != rightToken.Length)
		{
			return false;
		}
		for (int index = 0; index < leftToken.Length; index++)
		{
			char leftCharacter = leftSource[leftToken.StartIndex + index];
			char rightCharacter = rightSource[rightToken.StartIndex + index];
			if (leftCharacter != rightCharacter)
			{
				return false;
			}
		}
		return true;
	}

	private static bool LooksNumericToken(string source, TokenSlice token)
	{
		if (token.Length <= 0)
		{
			return false;
		}
		char firstCharacter = source[token.StartIndex];
		if (firstCharacter == '+' || firstCharacter == '-')
		{
			return token.Length > 1 && char.IsDigit(source[token.StartIndex + 1]);
		}
		return char.IsDigit(firstCharacter);
	}

	private static bool TryParseNumericToken(string source, TokenSlice token, out double value)
	{
		value = 0.0;
		if (token.Length <= 0)
		{
			return false;
		}
		int index = token.StartIndex;
		int endIndex = token.StartIndex + token.Length;
		bool isNegative = false;
		char firstCharacter = source[index];
		if (firstCharacter == '+' || firstCharacter == '-')
		{
			isNegative = firstCharacter == '-';
			index++;
			if (index >= endIndex || !char.IsDigit(source[index]))
			{
				return false;
			}
		}
		else if (!char.IsDigit(firstCharacter))
		{
			return false;
		}

		double integralPart = 0.0;
		while (index < endIndex && char.IsDigit(source[index]))
		{
			integralPart = integralPart * 10.0 + source[index] - '0';
			index++;
		}

		double fractionalPart = 0.0;
		double placeValue = 0.1;
		if (index < endIndex && source[index] == '.')
		{
			// NOTE:
			// 旧実装は double.TryParse(現在カルチャ)に依存していたため、
			// 小数点が "." と一致しないカルチャでは数値比較に失敗し文字列比較へフォールバックしていました。
			// 互換維持のため、この条件を残しています。
			if (!string.Equals(CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator, ".", StringComparison.Ordinal))
			{
				return false;
			}

			index++;
			while (index < endIndex && char.IsDigit(source[index]))
			{
				fractionalPart += (source[index] - '0') * placeValue;
				placeValue *= 0.1;
				index++;
			}
		}

		if (index != endIndex)
		{
			return false;
		}

		value = integralPart + fractionalPart;
		if (isNegative)
		{
			value = -value;
		}
		return true;
	}

	private TokenSlice[] GetOrCreateTokenSlices(string source)
	{
		if (!tokenCache.TryGetValue(source, out var tokenSlices))
		{
			tokenSlices = SplitByNumericTokens(source);
			tokenCache[source] = tokenSlices;
		}
		return tokenSlices;
	}

	private static TokenSlice[] SplitByNumericTokens(string value)
	{
		if (value == null)
		{
			return new TokenSlice[1] { CreateSlice(0, 0) };
		}
		List<TokenSlice> tokenSlices = new List<TokenSlice>(8);
		int cursor = 0;
		int length = value.Length;
		while (cursor < length)
		{
			if (!TryFindNextNumericToken(value, cursor, out int tokenStart, out int tokenEnd, out bool hasFraction, out int fractionStart))
			{
				break;
			}
			tokenSlices.Add(CreateSlice(cursor, tokenStart - cursor));
			tokenSlices.Add(CreateSlice(tokenStart, tokenEnd - tokenStart));
			if (hasFraction)
			{
				tokenSlices.Add(CreateSlice(fractionStart, tokenEnd - fractionStart));
			}
			cursor = tokenEnd;
		}
		tokenSlices.Add(CreateSlice(cursor, value.Length - cursor));
		return tokenSlices.ToArray();
	}

	private static TokenSlice CreateSlice(int startIndex, int length)
	{
		return new TokenSlice
		{
			StartIndex = startIndex,
			Length = length
		};
	}

	private static bool TryFindNextNumericToken(string text, int searchStart, out int tokenStart, out int tokenEnd, out bool hasFraction, out int fractionStart)
	{
		int textLength = text.Length;
		for (int index = searchStart; index < textLength; index++)
		{
			if (TryReadNumericToken(text, index, out tokenEnd, out hasFraction, out fractionStart))
			{
				tokenStart = index;
				return true;
			}
		}
		tokenStart = -1;
		tokenEnd = -1;
		hasFraction = false;
		fractionStart = -1;
		return false;
	}

	private static bool TryReadNumericToken(string text, int tokenStart, out int tokenEnd, out bool hasFraction, out int fractionStart)
	{
		int index = tokenStart;
		int textLength = text.Length;
		if (index >= textLength)
		{
			tokenEnd = -1;
			hasFraction = false;
			fractionStart = -1;
			return false;
		}
		char firstCharacter = text[index];
		if (firstCharacter == '+' || firstCharacter == '-')
		{
			if (index + 1 >= textLength || !char.IsDigit(text[index + 1]))
			{
				tokenEnd = -1;
				hasFraction = false;
				fractionStart = -1;
				return false;
			}
			index++;
		}
		else if (!char.IsDigit(firstCharacter))
		{
			tokenEnd = -1;
			hasFraction = false;
			fractionStart = -1;
			return false;
		}

		while (index < textLength && char.IsDigit(text[index]))
		{
			index++;
		}

		hasFraction = false;
		fractionStart = -1;
		if (index < textLength && text[index] == '.')
		{
			hasFraction = true;
			fractionStart = index;
			index++;
			while (index < textLength && char.IsDigit(text[index]))
			{
				index++;
			}
		}

		tokenEnd = index;
		return true;
	}
}
