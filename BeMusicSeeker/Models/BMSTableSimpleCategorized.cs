using System.Collections.Generic;

namespace BeMusicSeeker.Models;

public class BMSTableSimpleCategorized : BMSTableSimple
{
	public List<BMSTableSimpleCategorized> Children { get; set; }

	public BMSTableSimpleCategorized()
	{
	}

	public BMSTableSimpleCategorized(BMSTableSimple t)
	{
		base.name = t.name;
		base.symbol = t.symbol;
		base.url = t.url;
		base.tag1 = t.tag1;
		base.tag2 = t.tag2;
	}
}
