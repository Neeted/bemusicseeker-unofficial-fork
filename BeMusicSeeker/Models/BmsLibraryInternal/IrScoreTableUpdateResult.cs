using System.Collections.Generic;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class IrScoreTableUpdateResult
{
    public List<LR2IRScore> ScoreTable { get; set; }

    public long XmlFetchMs { get; set; }

    public long XmlParseMs { get; set; }

    public long DbReplaceMs { get; set; }

    public int ParsedRows { get; set; }

    public bool Succeeded => ScoreTable != null;
}
