using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using BeMusicSeeker.Models.LR2;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models
{
    internal sealed class Lr2IrRankingParseResult
    {
        public Lr2IrRankingParseResult(LR2IRData irData, Lr2IrRankingLookup lookup, int scoresParsed)
        {
            IrData = irData;
            Lookup = lookup;
            ScoresParsed = scoresParsed;
        }

        public LR2IRData IrData { get; }
        public Lr2IrRankingLookup Lookup { get; }
        public int ScoresParsed { get; }
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
            this.scoreFrequency = scoreFrequency ?? new Dictionary<int, int>();
            this.notesByScore = notesByScore ?? new Dictionary<int, List<int>>();
            this.bestByLr2Id = bestByLr2Id ?? new Dictionary<int, LR2IRData>();
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
        public IReadOnlyList<LR2IRData> Ranking => materializedRanking ?? (IReadOnlyList<LR2IRData>)Array.Empty<LR2IRData>();

        public LR2IRData GetLR2IRData(int lr2Id)
        {
            if (PlayersNum == 0)
            {
                return null;
            }

            if (bestByLr2Id.TryGetValue(lr2Id, out var source))
            {
                var data = Clone(source);
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
            var betterCount = 0;
            foreach (var item in scoreFrequency)
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

            var medianIndex = PlayersNum / 2;
            var offset = 0;
            foreach (var pair in notesByScore.OrderByDescending(x => x.Key))
            {
                var count = scoreFrequency.TryGetValue(pair.Key, out var frequency) ? frequency : pair.Value.Count;
                var nextOffset = offset + count;
                if (nextOffset > medianIndex)
                {
                    var notesIndex = medianIndex - offset;
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
            if (!TryParseLookup(rankingXml, md5, cacheLastWriteTime, false, out var lookup))
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
            var offset = 0;
            var anyScoreTag = false;
            while (true)
            {
                var start = rankingXml.IndexOf("<score>", offset, StringComparison.OrdinalIgnoreCase);
                if (start < 0)
                {
                    break;
                }

                anyScoreTag = true;
                var end = rankingXml.IndexOf("</score>", start, StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                {
                    return false;
                }

                end += "</score>".Length;
                var block = rankingXml.Substring(start, end - start);
                if (TryReadScoreBlock(block, md5, out var row))
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
                    foreach (var score in scores)
                    {
                        if (TryReadScoreElement(score, md5, out var row))
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
            foreach (var score in rankingXml.Descendants("score"))
            {
                if (TryReadScoreElement(score, md5, out var row))
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
            if (!TryReadIntTag(block, "id", out var id) ||
                !TryReadIntTag(block, "clear", out var clear) ||
                !TryReadIntTag(block, "notes", out var notes) ||
                !TryReadIntTag(block, "combo", out var combo) ||
                !TryReadIntTag(block, "pg", out var pg) ||
                !TryReadIntTag(block, "gr", out var gr) ||
                !TryReadIntTag(block, "minbp", out var minbp))
            {
                return false;
            }

            row = CreateRow(md5, id, clear, notes, combo, pg, gr, minbp);
            return true;
        }

        private static bool TryReadScoreElement(XElement score, string md5, out LR2IRData row)
        {
            row = null;
            if (!TryReadIntElement(score, "id", out var id) ||
                !TryReadIntElement(score, "clear", out var clear) ||
                !TryReadIntElement(score, "notes", out var notes) ||
                !TryReadIntElement(score, "combo", out var combo) ||
                !TryReadIntElement(score, "pg", out var pg) ||
                !TryReadIntElement(score, "gr", out var gr) ||
                !TryReadIntElement(score, "minbp", out var minbp))
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
            var open = "<" + tag + ">";
            var close = "</" + tag + ">";
            var start = block.IndexOf(open, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return false;
            }
            start += open.Length;
            var end = block.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
            if (end < 0 || end < start)
            {
                return false;
            }

            var text = block.Substring(start, end - start).Trim();
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
        }

        private static bool TryReadIntElement(XElement parent, string name, out int value)
        {
            value = 0;
            var text = parent.Element(name)?.Value?.Trim();
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
        }

        private static DateTime ParseLastUpdate(string rankingXml, DateTime fallback)
        {
            if (string.IsNullOrEmpty(rankingXml))
            {
                return fallback;
            }

            var tail = rankingXml.TrimEnd('\0').Tail(33, 19);
            return DateTime.TryParseExact(tail, "yyyy-MM-dd HH:mm:ss", null, DateTimeStyles.None, out var parsed)
                ? parsed
                : fallback;
        }

        private static DateTime ParseLastUpdate(XDocument rankingXml, DateTime fallback)
        {
            var text = rankingXml.Element("root")?.Element("lastupdate")?.Value?.TrimEnd('\0');
            return !string.IsNullOrWhiteSpace(text)
                && DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? parsed
                    : fallback;
        }

        private sealed class Builder
        {
            private readonly string md5;
            private readonly DateTime cacheLastWriteTime;
            private readonly bool materializeRanking;
            private readonly Dictionary<int, int> scoreFrequency = new Dictionary<int, int>();
            private readonly Dictionary<int, List<int>> notesByScore = new Dictionary<int, List<int>>();
            private readonly Dictionary<int, LR2IRData> bestByLr2Id = new Dictionary<int, LR2IRData>();
            private readonly List<LR2IRData> materializedRows;
            private double scoreSum;
            private double scoreSumSquares;
            private int count;

            public Builder(string md5, DateTime cacheLastWriteTime, bool materializeRanking)
            {
                this.md5 = md5;
                this.cacheLastWriteTime = cacheLastWriteTime;
                this.materializeRanking = materializeRanking;
                materializedRows = materializeRanking ? new List<LR2IRData>() : null;
            }

            public void Add(LR2IRData row)
            {
                count++;
                scoreSum += row.score;
                scoreSumSquares += (double)row.score * row.score;

                if (scoreFrequency.TryGetValue(row.score, out var frequency))
                {
                    scoreFrequency[row.score] = frequency + 1;
                }
                else
                {
                    scoreFrequency[row.score] = 1;
                }

                if (!notesByScore.TryGetValue(row.score, out var notes))
                {
                    notes = new List<int>();
                    notesByScore[row.score] = notes;
                }
                notes.Add(row.notes);

                if (!bestByLr2Id.TryGetValue(row.lr2id, out var best) || row.score > best.score)
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
                var average = count > 0 ? scoreSum / count : 0;
                var sigma = 0.0;
                if (count > 1)
                {
                    var variance = (scoreSumSquares - scoreSum * scoreSum / count) / (count - 1);
                    sigma = Math.Sqrt(Math.Max(0, variance));
                }

                List<LR2IRData> ranking = null;
                if (materializedRows != null)
                {
                    ranking = materializedRows.OrderByDescending(x => x.score).ToList();
                    foreach (var row in ranking)
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
                var greater = 0;
                foreach (var pair in scoreFrequency)
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
}
