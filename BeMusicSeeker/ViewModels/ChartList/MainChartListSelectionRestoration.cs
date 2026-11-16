using System;
using System.Collections;
using System.Collections.Generic;

namespace BeMusicSeeker.ViewModels;

/// <summary>選択維持を要求する行差替えの公開中だけ、画面が捕捉した選択の復元処理を保持します。</summary>
internal sealed class MainChartListSelectionRestoration(IList rows) : EventArgs
{
    private readonly List<Action> restorations = [];

    /// <summary>これからBindingへ公開する行集合です。</summary>
    internal IList Rows { get; } = rows;

    /// <summary>行・主添字の公開後に実行する、画面が所有する選択復元を登録します。</summary>
    internal void Register(Action restoration) => restorations.Add(restoration);

    /// <summary>公開済みの最終集合に対して、登録した選択復元を実行します。</summary>
    internal void Restore()
    {
        foreach (Action restoration in restorations) { restoration(); }
    }
}
