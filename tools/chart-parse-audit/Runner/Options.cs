using System.Globalization;

namespace ChartParseAudit;

/// <summary>簡易比較に必要な入力・試行数・スレッド並列度です。</summary>
public sealed record Options(string Command, string? File, string? SongDb, string? ChartRoot, string Out, int Workers, int Samples, int Repeat, int Seed, int[]? Choices, string Culture)
{
    /// <summary>file/scanの必須入力と正の並列度・試行数を検査します。未知の引数は失敗させます。</summary>
    public static Options Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("file" or "scan")) throw new ArgumentException("file または scan を指定してください。");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i++)
        {
            string key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal) || i + 1 == args.Length || !values.TryAdd(key, args[++i])) throw new ArgumentException("引数が不正です: " + key);
        }
        string? Get(string key, string? fallback = null) => values.Remove(key, out string? value) ? value : fallback;
        int Number(string key, int fallback, bool positive = true)
        {
            int value = int.Parse(Get(key, fallback.ToString(CultureInfo.InvariantCulture)) ?? "", CultureInfo.InvariantCulture);
            if (positive && value < 1) throw new ArgumentException("正の数を指定してください: " + key);
            return value;
        }
        string output = Path.GetFullPath(Get("--out") ?? throw new ArgumentException("--out が必要です。"));
        string? file = Get("--file"), db = Get("--song-db"), root = Get("--chart-root");
        int workers = Number("--workers", 1), samples = Number("--random-samples", 1), repeat = Number("--repeat", 1), seed = Number("--seed", 20260930, false);
        if (args[0] == "file" && (file == null || db != null || workers != 1)) throw new ArgumentException("file は --file と並列数1を使います。");
        if (args[0] == "scan" && (db == null || file != null)) throw new ArgumentException("scan は --song-db を使います。");
        string? choiceText = Get("--random-choices");
        int[]? choices = choiceText == null ? null : choiceText.Length == 0 ? [] : choiceText.Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        string culture = Get("--culture", "en-US") ?? "en-US";
        CultureInfo.GetCultureInfo(culture);
        if (values.Count > 0) throw new ArgumentException("未知の引数: " + string.Join(", ", values.Keys));
        return new(args[0], file == null ? null : Path.GetFullPath(file), db == null ? null : Path.GetFullPath(db), root == null ? null : Path.GetFullPath(root), output, workers, samples, repeat, seed, choices, culture);
    }
}
