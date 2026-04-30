using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BeMusicSeeker.Models.LR2;
using Livet;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models;

public class LR2IRCache : NotificationObject
{
    private static Regex lr2RankingRegex = new Regex("\\t\\t<id>(\\d+)</id>\\r?\\n\\t\\t<clear>(\\d+)</clear>\\r?\\n\\t\\t<notes>(\\d+)</notes>\\r?\\n\\t\\t<combo>(\\d+)</combo>\\r?\\n\\t\\t<pg>(\\d+)</pg>\\r?\\n\\t\\t<gr>(\\d+)</gr>\\r?\\n\\t\\t<minbp>(\\d+)</minbp>\\r?\\n", RegexOptions.Compiled);

    public List<LR2IRData> ranking { get; private set; }

    public LR2IRCache()
    {
        ranking = new List<LR2IRData>();
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
        try
        {
            DateTime date = default(DateTime);
            string text = getrankingxml.Element("root")?.Element("lastupdate")?.Value;
            if (text == null || !DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                date = cacheupdate;
            }
            ranking = (from e in getrankingxml.Element("root").Element("ranking").Elements("score")
                       select new LR2IRData(md5)
                       {
                           lr2id = int.Parse(e.Element("id").Value),
                           clear = ClearTypeStorageConverter.FromLr2Value(int.Parse(e.Element("clear").Value)),
                           notes = int.Parse(e.Element("notes").Value),
                           combo = int.Parse(e.Element("combo").Value),
                           pg = int.Parse(e.Element("pg").Value),
                           gr = int.Parse(e.Element("gr").Value),
                           minbp = int.Parse(e.Element("minbp").Value),
                           lastupdate = date,
                           lastcacheupdate = cacheupdate
                       } into s
                       orderby s.score descending
                       select s).ToList();
        }
        catch
        {
            ranking = new List<LR2IRData>();
            throw;
        }
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
        try
        {
            if (!DateTime.TryParseExact(rankingxml.Tail(33, 19), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                date = cacheupdate;
            }
            MatchCollection source = lr2RankingRegex.Matches(rankingxml);
            ranking = (from s in source.Cast<Match>().Select(delegate (Match m)
                {
                    try
                    {
                        return new LR2IRData(md5)
                        {
                            lr2id = int.Parse(m.Groups[1].Value),
                            clear = ClearTypeStorageConverter.FromLr2Value(int.Parse(m.Groups[2].Value)),
                            notes = int.Parse(m.Groups[3].Value),
                            combo = int.Parse(m.Groups[4].Value),
                            pg = int.Parse(m.Groups[5].Value),
                            gr = int.Parse(m.Groups[6].Value),
                            minbp = int.Parse(m.Groups[7].Value),
                            lastupdate = date,
                            lastcacheupdate = cacheupdate
                        };
                    }
                    catch
                    {
                        return (LR2IRData)null;
                    }
                })
                       where s != null
                       orderby s.score descending
                       select s).ToList();
        }
        catch
        {
            ranking = new List<LR2IRData>();
            throw;
        }
    }

    public LR2IRData GetLR2IRData(int _lr2id)
    {
        LR2IRData lR2IRData = ranking.FirstOrDefault((LR2IRData s) => s.lr2id == _lr2id);
        if (lR2IRData != null)
        {
            lR2IRData.rank = GetRankFromScore(lR2IRData.score);
        }
        else
        {
            if (ranking.Count == 0)
            {
                return null;
            }
            LR2IRData lR2IRData2 = ranking.ElementAt(ranking.Count / 2);
            lR2IRData = new LR2IRData(lR2IRData2.hash)
            {
                lr2id = _lr2id,
                notes = lR2IRData2.notes,
                clear = ClearType.NO_PLAY,
                pg = 0,
                gr = 0,
                rank = -1,
                lastupdate = lR2IRData2.lastupdate,
                lastcacheupdate = lR2IRData2.lastcacheupdate
            };
        }
        lR2IRData.players_num = ranking.Count;
        lR2IRData.average = ranking.Select((LR2IRData e) => e.score).Average();
        lR2IRData.sigma = ((IEnumerable<LR2IRData>)ranking).Select((Func<LR2IRData, double>)((LR2IRData e) => e.score)).StdDev();
        return lR2IRData;
    }

    public int GetRankingNum()
    {
        return ranking.Count;
    }

    public int GetRankFromScore(int score)
    {
        return ranking.TakeWhile((LR2IRData s) => s.score > score).Count() + 1;
    }
}
