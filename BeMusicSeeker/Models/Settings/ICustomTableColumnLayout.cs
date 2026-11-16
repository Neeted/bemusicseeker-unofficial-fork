using System.Windows;

namespace BeMusicSeeker.Models;

/// <summary>
/// 列設定の一つの列が持つ幅・表示位置・表示状態の契約です。
/// 永続化される列設定の値を画面が同じ形で扱うため、Properties と画面の両方が使う Models に置きます。
/// プロパティ名は実装の user.config の XML の要素名と一致するため、変えません。
/// </summary>
public interface ICustomTableColumnLayout
{
    int Width { get; set; }

    int DisplayIndex { get; set; }

    Visibility Visibility { get; set; }
}
