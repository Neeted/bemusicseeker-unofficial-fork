using System;
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

    public BMSTableSimple(dynamic json)
    {
        if (json.IsDefined("symbol") && json.symbol != null)
        {
            symbol = json.symbol.ToString();
        }
        if (json.IsDefined("name") && json.name != null)
        {
            name = json.name.ToString();
        }
        if (json.IsDefined("url") && json.url != null)
        {
            table.Page_url = new Uri(json.url.ToString(), UriKind.Absolute);
        }
        if (json.IsDefined("tag1") && json.name != null)
        {
            tag1 = json.tag1.ToString();
        }
        if (json.IsDefined("tag2") && json.name != null)
        {
            tag2 = json.tag2.ToString();
        }
    }
}
