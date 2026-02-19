using System;
using System.Linq.Expressions;
using System.Reflection;
using Livet;
using Ribbit.Util.Extensions;
using SQLite;

namespace BeMusicSeeker.Models.LR2;

public class SQLiteTable<SelfType> : NotificationObject
{
	public static string GetTableName()
	{
		return ((TableAttribute)(Attribute.GetCustomAttribute(typeof(SelfType), typeof(TableAttribute)) ?? throw new ArgumentException())).Name;
	}

	public static string GetColumnName<T>(Expression<Func<SelfType, T>> e)
	{
		if (e == null)
		{
			throw new ArgumentNullException("e");
		}
		string name = ObjectExt.GetName(e);
		PropertyInfo property = typeof(SelfType).GetProperty(name);
		if (property == null)
		{
			throw new ArgumentException();
		}
		Attribute customAttribute = Attribute.GetCustomAttribute(property, typeof(ColumnAttribute));
		if (customAttribute != null)
		{
			return ((ColumnAttribute)customAttribute).Name;
		}
		return name;
	}
}
