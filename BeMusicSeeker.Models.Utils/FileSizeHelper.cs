namespace BeMusicSeeker.Models.Utils;

public static class FileSizeHelper
{
	private static readonly string[] Units = new string[9] { "B", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB" };

	public static string GetReadableFileSize(long size)
	{
		int num = 0;
		while (size >= 1024)
		{
			size /= 1024;
			num++;
		}
		string arg = Units[num];
		return $"{size:0.#} {arg}";
	}
}
