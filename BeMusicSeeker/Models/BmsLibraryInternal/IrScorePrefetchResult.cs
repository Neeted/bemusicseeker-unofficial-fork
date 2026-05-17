using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class IrScorePrefetchResult
{
    public int Lr2Id { get; set; }

    public List<LR2IRScore> ScoreTable { get; } = [];

    public string ScoreDigestSha256 { get; set; }

    public long XmlFetchMs { get; set; }

    public long XmlParseMs { get; set; }

    public long DigestMs { get; set; }

    public int ParsedRows { get; set; }

    public string FailureReason { get; set; }

    public bool Succeeded => string.IsNullOrWhiteSpace(FailureReason) && ScoreDigestSha256 != null;
}
