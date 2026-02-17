using System;
using System.Linq.Expressions;
using System.Reflection;

namespace Ribbit.Util.Extensions;

public static class ObjectExt
{
	public static string GetName<T>(Expression<Func<T>> e)
	{
		if (e == null)
		{
			throw new ArgumentNullException("e");
		}
		return ((MemberExpression)e.Body).Member.Name;
	}

	public static string GetName<ObjectType, T>(Expression<Func<ObjectType, T>> e)
	{
		if (e == null)
		{
			throw new ArgumentNullException("e");
		}
		return ((MemberExpression)e.Body).Member.Name;
	}

	public static string GetName<ObjectType, T>(this ObjectType obj, Expression<Func<ObjectType, T>> e)
	{
		if (e == null)
		{
			throw new ArgumentNullException("e");
		}
		return ((MemberExpression)e.Body).Member.Name;
	}

	public static Func<T> GetDelegatePropertyGetMethod<ObjectType, T>(this ObjectType obj, Expression<Func<ObjectType, T>> e)
	{
		if (e == null)
		{
			throw new ArgumentNullException("e");
		}
		string name = GetName(e);
		PropertyInfo property = obj.GetType().GetProperty(name);
		if (property == null)
		{
			return null;
		}
		return Delegate.CreateDelegate(typeof(Func<T>), obj, property.GetGetMethod()) as Func<T>;
	}

	public static Action<T> GetDelegatePropertySetMethod<ObjectType, T>(this ObjectType obj, Expression<Func<ObjectType, T>> e)
	{
		if (e == null)
		{
			throw new ArgumentNullException("e");
		}
		string name = GetName(e);
		PropertyInfo property = obj.GetType().GetProperty(name);
		if (property == null)
		{
			return null;
		}
		return Delegate.CreateDelegate(typeof(Action<T>), obj, property.GetSetMethod()) as Action<T>;
	}
}
