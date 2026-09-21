using System;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Models;

public class BMSTableSimple : ObservableObject
{
    private readonly BMSTable table = new();

    public string symbol
    {
        get
        {
            return table.symbol;
        }
        set
        {
            table.symbol = value;
        }
    }

    public string name
    {
        get
        {
            return table.name;
        }
        set
        {
            table.name = value;
        }
    }

    public Uri url
    {
        get
        {
            return table.Page_url;
        }
        set
        {
            table.Page_url = value;
        }
    }

    public string tag1 { get; set; }

    public string tag2 { get; set; }

    public BMSTableSimple()
    {
    }

    public BMSTableSimple(JObject json)
    {
        if (json == null)
        {
            throw new ArgumentNullException(nameof(json));
        }
        if (TryGetNonNullProperty(json, "symbol", out JToken symbolToken))
        {
            symbol = symbolToken.ToString();
        }
        if (TryGetNonNullProperty(json, "name", out JToken nameToken))
        {
            name = nameToken.ToString();
        }
        if (TryGetNonNullProperty(json, "url", out JToken urlToken))
        {
            table.Page_url = new Uri(urlToken.ToString(), UriKind.Absolute);
        }
        if (TryGetNonNullProperty(json, "tag1", out JToken tag1Token)
            && TryGetNonNullProperty(json, "name", out _))
        {
            tag1 = tag1Token.ToString();
        }
        if (TryGetNonNullProperty(json, "tag2", out JToken tag2Token)
            && TryGetNonNullProperty(json, "name", out _))
        {
            tag2 = tag2Token.ToString();
        }
    }

    private static bool TryGetNonNullProperty(JObject source, string propertyName, out JToken value)
    {
        return source.TryGetValue(propertyName, out value) && value.Type != JTokenType.Null;
    }
}
