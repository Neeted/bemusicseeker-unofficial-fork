using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.Utils;

public class BmsScanResult
{
	public HashSet<string> BmsFilePaths { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, List<string>> FilesByDirectory { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
}
