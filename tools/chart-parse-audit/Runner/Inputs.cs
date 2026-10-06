using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SQLite;

namespace ChartParseAudit;

/// <summary>本文をcacheせず、SQLiteの行IDとpathだけを一度採取して実行計画にします。</summary>
public static class Inputs
{
    private sealed class SongRow
    {
        [Column("audit_rowid")] public long Id { get; set; }
        [Column("path")] public string? Path { get; set; }
    }
    /// <summary>file一行、またはSQLite ReadOnlyのrowid順スナップショットを返し接続を閉じます。重複と空pathを保持します。</summary>
    public static IReadOnlyList<(long RowId, string Path)> Read(Options options)
    {
        if (options.File != null) return [(0, options.File)];
        SQLitePCL.Batteries_V2.Init();
        using var db = new SQLiteConnection(options.SongDb ?? throw new ArgumentException("song-db required"), SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
        return db.Query<SongRow>("SELECT rowid AS audit_rowid, path FROM song ORDER BY rowid").Select(row => (row.Id, row.Path ?? "")).ToArray();
    }
    /// <summary>対応extensionと相対基準を分類します。不在・アクセス拒否は担当開始後の準備失敗として観測します。</summary>
    public static (string? Path, string? Skip) Resolve(string original, string? chartRoot)
    {
        string? path = null, skip = null;
        try
        {
            if (string.IsNullOrWhiteSpace(original)) skip = "empty-path";
            else if (!Path.IsPathFullyQualified(original) && chartRoot == null) skip = "relative-path-unresolved";
            else
            {
                path = Path.GetFullPath(original, chartRoot ?? Path.GetPathRoot(original) ?? throw new ArgumentException("root unavailable"));
                if (!new[] { ".bms", ".bme", ".bml", ".pms" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) skip = "unsupported";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { skip = "invalid-path"; }
        return (path, skip);
    }
    /// <summary>一行の試行と反復を列挙します。同sampleのseedは共通、旧新の先行順は反復ごとに交互です。</summary>
    public static IEnumerable<AuditCase> Cases(long rowId, string original, string? path, string? skip, Options options)
    {
        for (int sample = 0; sample < options.Samples; sample++)
        {
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{options.Seed}\n{rowId}\n{original}\n{sample}")));
            int seed = BinaryPrimitives.ReadInt32LittleEndian(digest);
            for (int repeat = 0; repeat < options.Repeat; repeat++)
                yield return new($"row-{rowId}-sample-{sample}-repeat-{repeat}", rowId, original, path, sample, repeat, seed, options.Choices, skip, ((rowId + sample + repeat) & 1) == 0 ? ["legacy", "current"] : ["current", "legacy"]);
        }
    }
}
