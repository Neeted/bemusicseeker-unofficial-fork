using System.Collections.Generic;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class IrScoreTableUpdateResult
{
    public List<LR2IRScore> ScoreTable { get; set; }

    public long XmlFetchMs { get; set; }

    public long XmlParseMs { get; set; }

    public long DigestMs { get; set; }

    public long DbLoadMs { get; set; }

    public long DbReplaceMs { get; set; }

    public int ParsedRows { get; set; }

    public int LoadedRows { get; set; }

    public bool MetadataUpdated { get; set; }

    public bool Skipped { get; set; }

    public string SkipReason { get; set; } = "unavailable";

    public bool Succeeded => ScoreTable != null;
}
