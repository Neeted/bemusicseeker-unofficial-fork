using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using BeMusicSeeker.Models.LR2;
namespace BeMusicSeeker.Models;

public class LR2IRCache : ObservableObject
{
    internal Lr2IrRankingLookup Lookup { get; private set; }
    public List<LR2IRData> ranking { get; private set; }

    public LR2IRCache()
    {
        ranking = [];
    }

    public LR2IRCache(XDocument getrankingxml, string md5, DateTime cacheupdate)
        : this()
    {
        SetRanking(getrankingxml, md5, cacheupdate);
    }

    public LR2IRCache(string rankingxml, string md5, DateTime cacheupdate)
        : this()
    {
        SetRanking(rankingxml, md5, cacheupdate);
    }

    public void SetRanking(XDocument getrankingxml, string md5, DateTime cacheupdate)
    {
        if (getrankingxml == null)
        {
            throw new ArgumentNullException();
        }
        if (md5 == null)
        {
            throw new ArgumentNullException();
        }
        if (!LR2SongDB.md5HashRegex.IsMatch(md5))
        {
            throw new ArgumentException("md5 hashではありません", "md5");
        }
        if (!Lr2IrRankingCacheParser.TryParseLookup(getrankingxml, md5, cacheupdate, true, out Lr2IrRankingLookup lookup))
        {
            ranking = [];
            Lookup = null;
            throw new FormatException("LR2IR ranking cache XML could not be parsed.");
        }

        Lookup = lookup;
        ranking = [.. lookup.Ranking];
    }

    public void SetRanking(string rankingxml, string md5, DateTime cacheupdate)
    {
        if (rankingxml == null)
        {
            throw new ArgumentNullException();
        }
        if (md5 == null)
        {
            throw new ArgumentNullException();
        }
        if (!LR2SongDB.md5HashRegex.IsMatch(md5))
        {
            throw new ArgumentException("md5 hashではありません", "md5");
        }
        if (!Lr2IrRankingCacheParser.TryParseLookup(rankingxml, md5, cacheupdate, true, out Lr2IrRankingLookup lookup))
        {
            ranking = [];
            Lookup = null;
            throw new FormatException("LR2IR ranking cache XML could not be parsed.");
        }

        Lookup = lookup;
        ranking = [.. lookup.Ranking];
    }

    public LR2IRData GetLR2IRData(int _lr2id)
    {
        return Lookup?.GetLR2IRData(_lr2id);
    }

    public int GetRankingNum()
    {
        return Lookup?.GetRankingNum() ?? ranking.Count;
    }

    public int GetRankFromScore(int score)
    {
        return Lookup?.GetRankFromScore(score) ?? ranking.TakeWhile(s => s.score > score).Count() + 1;
    }
}
