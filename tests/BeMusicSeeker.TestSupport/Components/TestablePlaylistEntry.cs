using BeMusicSeeker.Models;

namespace BeMusicSeeker.TestSupport.Components;

internal sealed class TestablePlaylistEntry : BMSTableEntry
{
    internal TestablePlaylistEntry(string md5Value, string titleValue)
    {
        md5 = md5Value;
        title = titleValue;
    }

    internal void SetSha256(string value)
    {
        sha256 = value;
    }
}
