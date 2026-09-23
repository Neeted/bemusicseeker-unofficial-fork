using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Ribbit.Util;

public static class InstanceCreator<TInstance>
{
    private static readonly ConcurrentDictionary<Type, Func<object, TInstance>> cache1 = new();

    private static readonly ConcurrentDictionary<Tuple<Type, Type>, Func<object, object, TInstance>> cache2 = new();

    private static readonly ConcurrentDictionary<Tuple<Type, Type, Type>, Func<object, object, object, TInstance>> cache3 = new();

    public static TInstance Create<T1>(T1 t1)
    {
        if (cache1.TryGetValue(typeof(T1), out Func<object, TInstance> value))
        {
            return value(t1);
        }
        ConstructorInfo constructor = typeof(TInstance).GetConstructor(BindingFlags.Instance | BindingFlags.Public, Type.DefaultBinder, [typeof(T1)], null);
        ParameterExpression parameterExpression = Expression.Parameter(typeof(object));
        UnaryExpression unaryExpression = Expression.Convert(parameterExpression, typeof(T1));
        value = Expression.Lambda<Func<object, TInstance>>(Expression.New(constructor, unaryExpression), [parameterExpression]).Compile();
        Func<object, TInstance> func = (cache1[typeof(T1)] = value);
        return func(t1);
    }

    public static TInstance Create<T1, T2>(T1 t1, T2 t2)
    {
        var key = Tuple.Create(typeof(T1), typeof(T2));
        if (cache2.TryGetValue(key, out Func<object, object, TInstance> value))
        {
            return value(t1, t2);
        }
        ConstructorInfo constructor = typeof(TInstance).GetConstructor(BindingFlags.Instance | BindingFlags.Public, Type.DefaultBinder,
        [
            typeof(T1),
            typeof(T2)
        ], null);
        ParameterExpression parameterExpression = Expression.Parameter(typeof(object));
        ParameterExpression parameterExpression2 = Expression.Parameter(typeof(object));
        UnaryExpression unaryExpression = Expression.Convert(parameterExpression, typeof(T1));
        UnaryExpression unaryExpression2 = Expression.Convert(parameterExpression2, typeof(T2));
        value = Expression.Lambda<Func<object, object, TInstance>>(Expression.New(constructor, unaryExpression, unaryExpression2), [parameterExpression, parameterExpression2]).Compile();
        Func<object, object, TInstance> func = (cache2[key] = value);
        return func(t1, t2);
    }

    public static TInstance Create<T1, T2, T3>(T1 t1, T2 t2, T3 t3)
    {
        var key = Tuple.Create(typeof(T1), typeof(T2), typeof(T3));
        if (cache3.TryGetValue(key, out Func<object, object, object, TInstance> value))
        {
            return value(t1, t2, t3);
        }
        ConstructorInfo constructor = typeof(TInstance).GetConstructor(BindingFlags.Instance | BindingFlags.Public, Type.DefaultBinder,
        [
            typeof(T1),
            typeof(T2),
            typeof(T3)
        ], null);
        ParameterExpression parameterExpression = Expression.Parameter(typeof(object));
        ParameterExpression parameterExpression2 = Expression.Parameter(typeof(object));
        ParameterExpression parameterExpression3 = Expression.Parameter(typeof(object));
        UnaryExpression unaryExpression = Expression.Convert(parameterExpression, typeof(T1));
        UnaryExpression unaryExpression2 = Expression.Convert(parameterExpression2, typeof(T2));
        UnaryExpression unaryExpression3 = Expression.Convert(parameterExpression3, typeof(T3));
        value = Expression.Lambda<Func<object, object, object, TInstance>>(Expression.New(constructor, unaryExpression, unaryExpression2, unaryExpression3), [parameterExpression, parameterExpression2, parameterExpression3]).Compile();
        Func<object, object, object, TInstance> func = (cache3[key] = value);
        return func(t1, t2, t3);
    }
}
