using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic;

namespace Ribbit.Util.Extensions;

public static class StringExt
{
    private static class Cache<T>
    {
        private delegate T TryPattern(string s, T defaultValue);

        private delegate bool TryParsePattern(string s, out T value);

        private static readonly TryPattern Func;

        private static TryParsePattern tp;

        static Cache()
        {
            MethodInfo method = typeof(T).GetMethod("TryParse", new Type[2]
            {
                typeof(string),
                typeof(T).MakeByRefType()
            });
            if (method == null)
            {
                if (typeof(T) == typeof(string))
                {
                    Func = (string x, T y) => (T)(object)x;
                    return;
                }
                if (typeof(T).IsEnum)
                {
                    throw new NotSupportedException("Convert to enum type is not supported");
                }
                Func = (string x, T y) => y;
            }
            else
            {
                tp = (TryParsePattern)Delegate.CreateDelegate(typeof(TryParsePattern), method);
                Func = (string x, T y) => tp(x, out var value) ? value : y;
            }
        }

        internal static T TryParse(string s, T defaultValue)
        {
            return Func(s, defaultValue);
        }
    }

    private static readonly Encoding sjisEnc = Encoding.GetEncoding("shift_jis", new EncoderReplacementFallback(string.Empty), new DecoderReplacementFallback(string.Empty));

    private static Regex invalidFileNameCharsRegex = new Regex("[" + Regex.Escape(new string(Path.GetInvalidFileNameChars())) + "]", RegexOptions.Compiled);

    private static Regex invalidFileNameStringsRegex = new Regex("^(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9]|CLOCK\\$)(?:\\.+(.*))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Regex invalidHeadTailCharsRegex = new Regex("^[\\s.\u3000]*(.*?)[\\s.\u3000]*$", RegexOptions.Compiled);

    private static Regex multipleExtensionPeriodsRegex = new Regex("[.]+([^.]+)$", RegexOptions.Compiled);

    private static Regex wideCharsAsciiRegex = new Regex("[\u3000！“”＃＄％＆‘’（）＊＋，－．／：；＜＝＞？＠［￥］\uff3e\uff3f\uffe3｜０-９Ａ-Ｚａ-ｚ]", RegexOptions.Compiled);

    private static Regex narrowCharsKanaRegex = new Regex("[｡-ﾟ]+", RegexOptions.Compiled);

    private static Regex multipleSpacesRegex = new Regex("[\\s\u3000]{2,}", RegexOptions.Compiled);

    public static string ReplaceFromStart(this string input, string search, string replacement, bool isIgnoreCase = false)
    {
        return Regex.Replace(input, "^" + Regex.Escape(search), replacement.Replace("$", "$$"), isIgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
    }

    public static string ReplaceFromEnd(this string input, string search, string replacement, bool isIgnoreCase = false)
    {
        return Regex.Replace(input, Regex.Escape(search) + "$", replacement.Replace("$", "$$"), isIgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
    }

    public static IEnumerable<string> Split(this string str, int length)
    {
        if (length < 1)
        {
            throw new ArgumentException("length should be greater than 0");
        }
        for (int idx = 0; idx < str.Length; idx += length)
        {
            yield return str.Substring(idx, System.Math.Min(length, str.Length - idx));
        }
    }

    public static IEnumerable<string> ReadLine(this string source, bool nullToEmpty = true)
    {
        if (nullToEmpty && source == null)
        {
            yield break;
        }
        if (source == null)
        {
            throw new ArgumentNullException("source");
        }
        using StringReader reader = new StringReader(source);
        string text;
        while ((text = reader.ReadLine()) != null)
        {
            yield return text;
        }
    }

    public static string FastReplace(this string s, char a, char b)
    {
        char[] array = s.ToCharArray();
        for (int i = 0; i < array.Length; i++)
        {
            if (array[i] == a)
            {
                array[i] = b;
            }
        }
        return new string(array);
    }

    public static string RemoveBlanks(this string str)
    {
        StringBuilder stringBuilder = new StringBuilder(str.Length);
        foreach (char c in str)
        {
            if (!char.IsWhiteSpace(c))
            {
                stringBuilder.Append(c);
            }
        }
        return stringBuilder.ToString();
    }

    public static string RemoveInvalidFileNameChars(this string s, bool isFolderName = false)
    {
        s = invalidFileNameCharsRegex.Replace(s, string.Empty);
        s = invalidHeadTailCharsRegex.Replace(s, "$1");
        s = invalidFileNameStringsRegex.Replace(s, "$1");
        if (!isFolderName)
        {
            s = multipleExtensionPeriodsRegex.Replace(s, ".$1");
        }
        return s;
    }

    public static string ReplaceInvalidFileNameCharsByWide(this string input)
    {
        return invalidFileNameCharsRegex.Replace(input, (Match m) => Strings.StrConv(m.Value, VbStrConv.Wide, 1041));
    }

    public static string NaturalNormalizationForFileName(this string input)
    {
        input = wideCharsAsciiRegex.Replace(input, (Match m) => Strings.StrConv(m.Value, VbStrConv.Narrow, 1041));
        input = narrowCharsKanaRegex.Replace(input, (Match m) => Strings.StrConv(m.Value, VbStrConv.Wide, 1041));
        input = multipleSpacesRegex.Replace(input, " ");
        return input.Trim();
    }

    public static string ToSjisSchemeString(this string s)
    {
        return sjisEnc.GetString(sjisEnc.GetBytes(s));
    }

    public static bool IsSjisSchemeString(this string s)
    {
        return s.ToSjisSchemeString() == s;
    }

    public static string ReplaceCaseInsensitive(this string input, string search, string replacement)
    {
        return Regex.Replace(input, Regex.Escape(search), replacement.Replace("$", "$$"), RegexOptions.IgnoreCase);
    }

    public static string Tail(this string input, int posFromEnd, int size)
    {
        if (input.Length > posFromEnd)
        {
            return input.Substring(input.Length - posFromEnd, System.Math.Min(posFromEnd, size));
        }
        return input;
    }

    public static string LongestCommonSubstring(this string str1, string str2)
    {
        _ = string.Empty;
        if (string.IsNullOrEmpty(str1) || string.IsNullOrEmpty(str2))
        {
            return string.Empty;
        }
        int[,] array = new int[str1.Length, str2.Length];
        int num = 0;
        int num2 = 0;
        StringBuilder stringBuilder = new StringBuilder();
        for (int i = 0; i < str1.Length; i++)
        {
            for (int j = 0; j < str2.Length; j++)
            {
                if (str1[i] != str2[j])
                {
                    array[i, j] = 0;
                    continue;
                }
                if (i == 0 || j == 0)
                {
                    array[i, j] = 1;
                }
                else
                {
                    array[i, j] = 1 + array[i - 1, j - 1];
                }
                if (array[i, j] > num)
                {
                    num = array[i, j];
                    int num3 = i - array[i, j] + 1;
                    if (num2 == num3)
                    {
                        stringBuilder.Append(str1[i]);
                        continue;
                    }
                    num2 = num3;
                    stringBuilder.Length = 0;
                    stringBuilder.Append(str1.Substring(num2, i + 1 - num2));
                }
            }
        }
        return stringBuilder.ToString();
    }

    public static string LongestCommonSubstringHeadFixed(this string str1, string str2)
    {
        if (string.IsNullOrEmpty(str1) || string.IsNullOrEmpty(str2))
        {
            return string.Empty;
        }
        StringBuilder stringBuilder = new StringBuilder();
        for (int i = 0; i < System.Math.Min(str1.Length, str2.Length) && str1[i] == str2[i]; i++)
        {
            stringBuilder.Append(str1[i]);
        }
        return stringBuilder.ToString();
    }

    public static T TryParseOrDefault<T>(this string text) where T : struct
    {
        return text.TryParseOrDefault(default(T));
    }

    public static T TryParseOrDefault<T>(this string text, T defaultValue) where T : struct
    {
        return Cache<T>.TryParse(text, defaultValue);
    }
}
