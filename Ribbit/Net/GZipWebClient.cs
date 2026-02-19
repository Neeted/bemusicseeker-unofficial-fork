using System;
using System.Net;
using System.Reflection;
using Ribbit.Logging;

namespace Ribbit.Net;

public class GZipWebClient : WebClient
{
	public const int DefaultTimeoutMs = 30000;

	private static readonly string DefaultUserAgent = BuildDefaultUserAgent();

	private static readonly NLog.Logger logger = NLogWrapper.FileLogger ?? NLogWrapper.GetLogger();

	public int RequestTimeoutMs { get; set; } = DefaultTimeoutMs;

	public int ReadWriteTimeoutMs { get; set; } = DefaultTimeoutMs;

	private static string BuildDefaultUserAgent()
	{
		try
		{
			Version version = Assembly.GetEntryAssembly()?.GetName()?.Version ?? Assembly.GetExecutingAssembly()?.GetName()?.Version;
			if (version != null)
			{
				return "BeMusicSeeker/" + version;
			}
		}
		catch
		{
		}
		return "BeMusicSeeker/unknown";
	}

	private static bool IsNullOrWhiteSpace(string value)
	{
		if (value != null)
		{
			return value.Trim().Length == 0;
		}
		return true;
	}

	private static void LogWebException(WebRequest request, WebException exception)
	{
		try
		{
			string method = (request as HttpWebRequest)?.Method ?? "UNKNOWN";
			string url = request?.RequestUri?.ToString() ?? "UNKNOWN";
			HttpWebResponse httpWebResponse = exception.Response as HttpWebResponse;
			if (httpWebResponse != null)
			{
				logger?.Warn(exception, "http_request_failed method=" + method + " url=" + url + " statusCode=" + (int)httpWebResponse.StatusCode + " status=" + httpWebResponse.StatusCode + " webStatus=" + exception.Status);
			}
			else
			{
				logger?.Warn(exception, "http_request_failed method=" + method + " url=" + url + " webStatus=" + exception.Status);
			}
		}
		catch
		{
		}
	}

	private static void LogWebResponse(WebRequest request, WebResponse response)
	{
		try
		{
			string method = (request as HttpWebRequest)?.Method ?? "UNKNOWN";
			string url = request?.RequestUri?.ToString() ?? "UNKNOWN";
			HttpWebResponse httpWebResponse = response as HttpWebResponse;
			if (httpWebResponse != null)
			{
				logger?.Info("http_request_succeeded method=" + method + " url=" + url + " statusCode=" + (int)httpWebResponse.StatusCode + " status=" + httpWebResponse.StatusCode);
			}
			else
			{
				logger?.Info("http_request_succeeded method=" + method + " url=" + url);
			}
		}
		catch
		{
		}
	}

	protected override WebRequest GetWebRequest(Uri address)
	{
		try
		{
			HttpWebRequest obj = (HttpWebRequest)base.GetWebRequest(address);
			obj.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
			obj.Timeout = ((RequestTimeoutMs > 0) ? RequestTimeoutMs : DefaultTimeoutMs);
			obj.ReadWriteTimeout = ((ReadWriteTimeoutMs > 0) ? ReadWriteTimeoutMs : DefaultTimeoutMs);
			if (IsNullOrWhiteSpace(obj.UserAgent) && IsNullOrWhiteSpace(obj.Headers[HttpRequestHeader.UserAgent]))
			{
				obj.UserAgent = DefaultUserAgent;
			}
			return obj;
		}
		catch
		{
			return base.GetWebRequest(address);
		}
	}

	protected override WebResponse GetWebResponse(WebRequest request)
	{
		try
		{
			WebResponse webResponse = base.GetWebResponse(request);
			LogWebResponse(request, webResponse);
			return webResponse;
		}
		catch (WebException ex)
		{
			LogWebException(request, ex);
			throw;
		}
	}

	protected override WebResponse GetWebResponse(WebRequest request, IAsyncResult result)
	{
		try
		{
			WebResponse webResponse = base.GetWebResponse(request, result);
			LogWebResponse(request, webResponse);
			return webResponse;
		}
		catch (WebException ex)
		{
			LogWebException(request, ex);
			throw;
		}
	}
}
