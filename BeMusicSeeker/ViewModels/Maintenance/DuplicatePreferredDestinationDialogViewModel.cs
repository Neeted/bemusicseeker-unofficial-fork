using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 「優先統合先…」のウィンドウの入力です。確定前の登録内容を持ち、キャンセルでは元の登録内容へ影響しません。
/// 同じディレクトリ（大文字小文字と末尾の区切りを無視）の重複した登録は一つにまとめます。
/// </summary>
public sealed class DuplicatePreferredDestinationDialogViewModel
{
    /// <summary>ウィンドウに表示する登録内容です。</summary>
    public ObservableCollection<string> Destinations { get; }

    /// <param name="initialDestinations">開いた時点の登録内容。前回確定した内容が表示されます。</param>
    internal DuplicatePreferredDestinationDialogViewModel(IEnumerable<string> initialDestinations)
    {
        Destinations = [.. initialDestinations ?? []];
    }

    /// <summary>ディレクトリを追加します。空の入力と、すでに登録済みの同じディレクトリは追加しません。</summary>
    /// <returns>追加した場合はtrueです。</returns>
    public bool Add(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) { return false; }
        string key = Normalize(directory);
        if (Destinations.Any(existing => string.Equals(Normalize(existing), key, StringComparison.OrdinalIgnoreCase))) { return false; }
        Destinations.Add(directory.Trim());
        return true;
    }

    /// <summary>選択した登録を削除します。</summary>
    public void Remove(IEnumerable<string> directories)
    {
        foreach (string directory in (directories ?? []).ToArray()) { Destinations.Remove(directory); }
    }

    /// <summary>確定する登録内容を、変更不能な値として取得します。</summary>
    internal IReadOnlyList<string> Capture() => [.. Destinations];

    private static string Normalize(string path) => path.Trim().Replace('/', '\\').TrimEnd('\\');
}
