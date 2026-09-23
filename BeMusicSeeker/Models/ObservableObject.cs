using System;
using System.ComponentModel;
using System.Linq.Expressions;

namespace BeMusicSeeker.Models;

/// <summary>
/// Model / application 層が利用する BCL の observable state contract です。
/// UI framework の通知基底型を production model へ持ち込まないために使用します。
/// </summary>
public class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected void RaisePropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected void RaisePropertyChanged<T>(Expression<Func<T>> propertyExpression)
    {
        if (propertyExpression == null)
        {
            throw new ArgumentNullException(nameof(propertyExpression));
        }

        if (propertyExpression.Body is not MemberExpression memberExpression)
        {
            throw new ArgumentException("Property expression must target a member.", nameof(propertyExpression));
        }

        RaisePropertyChanged(memberExpression.Member.Name);
    }
}
