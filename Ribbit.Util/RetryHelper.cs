using System;

namespace Ribbit.Util;

public static class RetryHelper
{
	public static void RetryIfError(Action onAction, Action onRetry = null)
	{
		if (onAction == null)
		{
			throw new ArgumentNullException("onAction");
		}
		RetryIfErrorCore(onAction, null, onRetry, null, null);
	}

	public static void RetryIfError(Action onAction, Action<Exception> onError, Action onRetry, uint retryCount)
	{
		if (onAction == null)
		{
			throw new ArgumentNullException("onAction");
		}
		if (onError == null)
		{
			throw new ArgumentNullException("onError");
		}
		RetryIfErrorCore(onAction, onError, onRetry, null, retryCount);
	}

	public static void RetryIfError(Action onAction, Action<Exception> onError, Action onRetry, Func<Exception, bool> retryCondition)
	{
		if (onAction == null)
		{
			throw new ArgumentNullException("onAction");
		}
		if (onError == null)
		{
			throw new ArgumentNullException("onError");
		}
		if (retryCondition == null)
		{
			throw new ArgumentNullException("retryCondition");
		}
		RetryIfErrorCore(onAction, onError, onRetry, retryCondition, null);
	}

	public static void RetryIfError(Action onAction, Action<Exception> onError, Action onRetry, Func<Exception, bool> retryCondition, uint maxRetryCount)
	{
		if (onAction == null)
		{
			throw new ArgumentNullException("onAction");
		}
		if (onError == null)
		{
			throw new ArgumentNullException("onError");
		}
		if (retryCondition == null)
		{
			throw new ArgumentNullException("retryCondition");
		}
		RetryIfErrorCore(onAction, onError, onRetry, retryCondition, maxRetryCount);
	}

	private static void RetryIfErrorCore(Action onAction, Action<Exception> onError, Action onRetry, Func<Exception, bool> retryCondition, uint? retryCount)
	{
		if (onError == null)
		{
			onError = delegate
			{
			};
		}
		if (retryCondition == null)
		{
			retryCondition = (Exception ex2) => true;
		}
		uint num = 0u;
		while (true)
		{
			try
			{
				onAction();
				break;
			}
			catch (Exception ex)
			{
				if (!retryCondition(ex))
				{
					goto IL_008a;
				}
				if (!retryCount.HasValue)
				{
					onRetry?.Invoke();
					continue;
				}
				if (num++ >= retryCount.Value)
				{
					goto IL_008a;
				}
				onRetry?.Invoke();
				goto end_IL_0053;
				IL_008a:
				onError(ex);
				break;
				end_IL_0053:;
			}
		}
	}
}
