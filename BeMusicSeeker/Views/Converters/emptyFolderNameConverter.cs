using System;
using System.Globalization;
using System.Windows.Data;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Views;

/// <summary>フォルダ順一覧の空文字だけを名前なしの表示へ変換し、保存済みの空白名は原値で表示します。</summary>
internal sealed class emptyFolderNameConverter : IValueConverter
{
    /// <summary>正規の空文字所属だけに表示ラベルを使い、入力値は書き換えません。</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string text = value as string;
        return text == string.Empty ? Resources.No_folder_name : text;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value;
    }
}
