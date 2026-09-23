using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using BeMusicSeeker.Models.LR2;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models;

internal sealed class Lr2IrRankingParseResult(LR2IRData irData, Lr2IrRankingLookup lookup, int scoresParsed)
{
    public LR2IRData IrData { get; } = irData;
    public Lr2IrRankingLookup Lookup { get; } = lookup;
    public int ScoresParsed { get; } = scoresParsed;
}

internal sealed class Lr2IrRankingLookup
{
    private readonly Dictionary<int, int> scoreFrequency;
    private readonly Dictionary<int, List<int>> notesByScore;
    private readonly Dictionary<int, LR2IRData> bestByLr2Id;
    private readonly List<LR2IRData> materializedRanking;

    internal Lr2IrRankingLookup(
        string hash,
        int playersNum,
        double average,
        double sigma,
        DateTime lastUpdate,
        DateTime lastCacheUpdate,
        Dictionary<int, int> scoreFrequency,
        Dictionary<int, List<int>> notesByScore,
        Dictionary<int, LR2IRData> bestByLr2Id,
        List<LR2IRData> materializedRanking)
    {
        this.scoreFrequency = scoreFrequency ?? [];
        this.notesByScore = notesByScore ?? [];
        this.bestByLr2Id = bestByLr2Id ?? [];
        this.materializedRanking = materializedRanking;
        Hash = hash;
        PlayersNum = playersNum;
        Average = average;
        Sigma = sigma;
        LastUpdate = lastUpdate;
        LastCacheUpdate = lastCacheUpdate;
    }

    public string Hash { get; }
    public int PlayersNum { get; }
    public double Average { get; }
    public double Sigma { get; }
    public DateTime LastUpdate { get; }
    public DateTime LastCacheUpdate { get; }
    public IReadOnlyList<LR2IRData> Ranking => materializedRanking ?? (IReadOnlyList<LR2IRData>)[];

    public LR2IRData GetLR2IRData(int lr2Id)
    {
        if (PlayersNum == 0)
        {
            return null;
        }

        if (bestByLr2Id.TryGetValue(lr2Id, out LR2IRData source))
        {
            LR2IRData data = Clone(source);
            ApplyAggregate(data, data.score);
            return data;
        }

        var noPlay = new LR2IRData(Hash)
        {
            lr2id = lr2Id,
            notes = GetMedianScoreNotes(),
            clear = ClearType.NO_PLAY,
            rank = -1,
            lastupdate = LastUpdate,
            lastcacheupdate = LastCacheUpdate
        };
        ApplyAggregate(noPlay, null);
        return noPlay;
    }

    public int GetRankFromScore(int score)
    {
        int betterCount = 0;
        foreach (KeyValuePair<int, int> item in scoreFrequency)
        {
            if (item.Key > score)
            {
                betterCount += item.Value;
            }
        }
        return betterCount + 1;
    }

    public int GetRankingNum()
    {
        return PlayersNum;
    }

    private int GetMedianScoreNotes()
    {
        if (notesByScore.Count == 0)
        {
            return 0;
        }

        int medianIndex = PlayersNum / 2;
        int offset = 0;
        foreach (KeyValuePair<int, List<int>> pair in notesByScore.OrderByDescending(x => x.Key))
        {
            int count = scoreFrequency.TryGetValue(pair.Key, out int frequency) ? frequency : pair.Value.Count;
            int nextOffset = offset + count;
            if (nextOffset > medianIndex)
            {
                int notesIndex = medianIndex - offset;
                return notesIndex >= 0 && notesIndex < pair.Value.Count
                    ? pair.Value[notesIndex]
                    : pair.Value.FirstOrDefault();
            }
            offset = nextOffset;
        }

        return notesByScore.Values.LastOrDefault()?.FirstOrDefault() ?? 0;
    }

    private void ApplyAggregate(LR2IRData data, int? rankScore)
    {
        data.players_num = PlayersNum;
        data.average = Average;
        data.sigma = Sigma;
        if (rankScore.HasValue)
        {
            data.rank = GetRankFromScore(rankScore.Value);
        }
        data.lastupdate = LastUpdate;
        data.lastcacheupdate = LastCacheUpdate;
    }

    private static LR2IRData Clone(LR2IRData source)
    {
        return new LR2IRData(source.hash)
        {
            lr2id = source.lr2id,
            clear = source.clear,
            notes = source.notes,
            combo = source.combo,
            pg = source.pg,
            gr = source.gr,
            minbp = source.minbp,
            rank = source.rank,
            players_num = source.players_num,
            average = source.average,
            sigma = source.sigma,
            lastupdate = source.lastupdate,
            lastcacheupdate = source.lastcacheupdate
        };
    }
}

internal static class Lr2IrRankingCacheParser
{
    public static bool TryParseSummary(string rankingXml, string md5, DateTime cacheLastWriteTime, int lr2Id, out Lr2IrRankingParseResult result)
    {
        if (!TryParseLookup(rankingXml, md5, cacheLastWriteTime, false, out Lr2IrRankingLookup lookup))
        {
            result = null;
            return false;
        }

        result = new Lr2IrRankingParseResult(lookup.GetLR2IRData(lr2Id), lookup, lookup.PlayersNum);
        return true;
    }

    public static bool TryParseLookup(string rankingXml, string md5, DateTime cacheLastWriteTime, bool materializeRanking, out Lr2IrRankingLookup lookup)
    {
        lookup = null;
        if (rankingXml == null)
        {
            return false;
        }

        var builder = new Builder(md5, cacheLastWriteTime, materializeRanking);
        int offset = 0;
        bool anyScoreTag = false;
        while (true)
        {
            int start = rankingXml.IndexOf("<score>", offset, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                break;
            }

            anyScoreTag = true;
            int end = rankingXml.IndexOf("</score>", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                return false;
            }

            end += "</score>".Length;
            string block = rankingXml.Substring(start, end - start);
            if (TryReadScoreBlock(block, md5, out LR2IRData row))
            {
                builder.Add(row);
            }
            offset = end;
        }

        if (!anyScoreTag)
        {
            try
            {
                var doc = XDocument.Parse(rankingXml);
                var scores = doc.Descendants("score").ToList();
                foreach (XElement score in scores)
                {
                    if (TryReadScoreElement(score, md5, out LR2IRData row))
                    {
                        builder.Add(row);
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        lookup = builder.Build(ParseLastUpdate(rankingXml, cacheLastWriteTime));
        return true;
    }

    public static bool TryParseLookup(XDocument rankingXml, string md5, DateTime cacheLastWriteTime, bool materializeRanking, out Lr2IrRankingLookup lookup)
    {
        lookup = null;
        if (rankingXml == null)
        {
            return false;
        }

        var builder = new Builder(md5, cacheLastWriteTime, materializeRanking);
        foreach (XElement score in rankingXml.Descendants("score"))
        {
            if (TryReadScoreElement(score, md5, out LR2IRData row))
            {
                builder.Add(row);
            }
        }

        lookup = builder.Build(ParseLastUpdate(rankingXml, cacheLastWriteTime));
        return true;
    }

    private static bool TryReadScoreBlock(string block, string md5, out LR2IRData row)
    {
        row = null;
        if (!TryReadIntTag(block, "id", out int id) ||
            !TryReadIntTag(block, "clear", out int clear) ||
            !TryReadIntTag(block, "notes", out int notes) ||
            !TryReadIntTag(block, "combo", out int combo) ||
            !TryReadIntTag(block, "pg", out int pg) ||
            !TryReadIntTag(block, "gr", out int gr) ||
            !TryReadIntTag(block, "minbp", out int minbp))
        {
            return false;
        }

        row = CreateRow(md5, id, clear, notes, combo, pg, gr, minbp);
        return true;
    }

    private static bool TryReadScoreElement(XElement score, string md5, out LR2IRData row)
    {
        row = null;
        if (!TryReadIntElement(score, "id", out int id) ||
            !TryReadIntElement(score, "clear", out int clear) ||
            !TryReadIntElement(score, "notes", out int notes) ||
            !TryReadIntElement(score, "combo", out int combo) ||
            !TryReadIntElement(score, "pg", out int pg) ||
            !TryReadIntElement(score, "gr", out int gr) ||
            !TryReadIntElement(score, "minbp", out int minbp))
        {
            return false;
        }

        row = CreateRow(md5, id, clear, notes, combo, pg, gr, minbp);
        return true;
    }

    private static LR2IRData CreateRow(string md5, int id, int clear, int notes, int combo, int pg, int gr, int minbp)
    {
        return new LR2IRData(md5)
        {
            lr2id = id,
            clear = ClearTypeStorageConverter.FromLr2Value(clear),
            notes = notes,
            combo = combo,
            pg = pg,
            gr = gr,
            minbp = minbp
        };
    }

    private static bool TryReadIntTag(string block, string tag, out int value)
    {
        value = 0;
        string open = "<" + tag + ">";
        string close = "</" + tag + ">";
        int start = block.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }
        start += open.Length;
        int end = block.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0 || end < start)
        {
            return false;
        }

        string text = block.Substring(start, end - start).Trim();
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    private static bool TryReadIntElement(XElement parent, string name, out int value)
    {
        string text = parent.Element(name)?.Value?.Trim();
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    private static DateTime ParseLastUpdate(string rankingXml, DateTime fallback)
    {
        if (string.IsNullOrEmpty(rankingXml))
        {
            return fallback;
        }

        string tail = rankingXml.TrimEnd('\0').Tail(33, 19);
        return DateTime.TryParseExact(tail, "yyyy-MM-dd HH:mm:ss", null, DateTimeStyles.None, out DateTime parsed)
            ? parsed
            : fallback;
    }

    private static DateTime ParseLastUpdate(XDocument rankingXml, DateTime fallback)
    {
        string text = rankingXml.Element("root")?.Element("lastupdate")?.Value?.TrimEnd('\0');
        return !string.IsNullOrWhiteSpace(text)
            && DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
                ? parsed
                : fallback;
    }

    private sealed class Builder(string md5, DateTime cacheLastWriteTime, bool materializeRanking)
    {
        private readonly string md5 = md5;
        private readonly DateTime cacheLastWriteTime = cacheLastWriteTime;
        private readonly bool materializeRanking = materializeRanking;
        private readonly Dictionary<int, int> scoreFrequency = [];
        private readonly Dictionary<int, List<int>> notesByScore = [];
        private readonly Dictionary<int, LR2IRData> bestByLr2Id = [];
        private readonly List<LR2IRData> materializedRows = materializeRanking ? [] : null;
        private double scoreSum;
        private double scoreSumSquares;
        private int count;

        public void Add(LR2IRData row)
        {
            count++;
            scoreSum += row.score;
            scoreSumSquares += (double)row.score * row.score;

            if (scoreFrequency.TryGetValue(row.score, out int frequency))
            {
                scoreFrequency[row.score] = frequency + 1;
            }
            else
            {
                scoreFrequency[row.score] = 1;
            }

            if (!notesByScore.TryGetValue(row.score, out List<int> notes))
            {
                notes = [];
                notesByScore[row.score] = notes;
            }
            notes.Add(row.notes);

            if (!bestByLr2Id.TryGetValue(row.lr2id, out LR2IRData best) || row.score > best.score)
            {
                bestByLr2Id[row.lr2id] = row;
            }

            if (materializeRanking)
            {
                materializedRows.Add(row);
            }
        }

        public Lr2IrRankingLookup Build(DateTime lastUpdate)
        {
            double average = count > 0 ? scoreSum / count : 0;
            double sigma = 0.0;
            if (count > 1)
            {
                double variance = (scoreSumSquares - scoreSum * scoreSum / count) / (count - 1);
                sigma = Math.Sqrt(Math.Max(0, variance));
            }

            List<LR2IRData> ranking = null;
            if (materializedRows != null)
            {
                ranking = [.. materializedRows.OrderByDescending(x => x.score)];
                foreach (LR2IRData row in ranking)
                {
                    row.rank = CountScoresGreaterThan(row.score) + 1;
                    row.players_num = count;
                    row.average = average;
                    row.sigma = sigma;
                    row.lastupdate = lastUpdate;
                    row.lastcacheupdate = cacheLastWriteTime;
                }
            }

            return new Lr2IrRankingLookup(
                md5,
                count,
                average,
                sigma,
                lastUpdate,
                cacheLastWriteTime,
                scoreFrequency,
                notesByScore,
                bestByLr2Id,
                ranking);
        }

        private int CountScoresGreaterThan(int targetScore)
        {
            int greater = 0;
            foreach (KeyValuePair<int, int> pair in scoreFrequency)
            {
                if (pair.Key > targetScore)
                {
                    greater += pair.Value;
                }
            }
            return greater;
        }
    }
}
