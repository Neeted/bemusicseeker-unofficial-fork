using System;
using System.Net;

namespace Ribbit.Net;

public class GZipWebClient : WebClient
{
	protected override WebRequest GetWebRequest(Uri address)
	{
		try
		{
			HttpWebRequest obj = (HttpWebRequest)base.GetWebRequest(address);
			obj.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
			return obj;
		}
		catch
		{
			return base.GetWebRequest(address);
		}
	}
}
