using System.Windows;
using System.Windows.Controls;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

/// <summary>
/// 重複ツリーの子項目の種類で、項目コンテナーのスタイルを選びます。先頭の「統合候補一覧」ノードは見出しだけを持ち、
/// 重複グループは見出しとフォルダの子項目を持ちます。
/// </summary>
internal sealed class duplicateTreeItemContainerStyleSelector : StyleSelector
{
    /// <summary>重複グループの項目に使うスタイルです。</summary>
    public Style GroupStyle { get; set; }

    /// <summary>統合候補一覧のノードに使うスタイルです。</summary>
    public Style CandidateListStyle { get; set; }

    /// <inheritdoc />
    public override Style SelectStyle(object item, DependencyObject container)
        => item is DuplicateMergeCandidateListNode ? CandidateListStyle : GroupStyle;
}
