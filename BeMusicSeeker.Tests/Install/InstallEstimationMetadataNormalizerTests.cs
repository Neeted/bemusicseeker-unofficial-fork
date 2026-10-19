using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class InstallEstimationMetadataNormalizerTests
{
    /// <summary>最後の区切り群・MIX終端・先頭保護を生TITLEへ一度だけ適用し、同じ小文字化前表記を表示に使う。</summary>
    [DataTestMethod]
    [DataRow("Ark (remix/5keys)", "Ark (remix")]
    [DataRow("Ark (remix/10lines)", "Ark (remix")]
    [DataRow("Ark (mixed)", "Ark (mix")]
    [DataRow("夏草の線路 -J-Trance remix-", "夏草の線路 -J-Trance remix")]
    // 語中ハイフンを閉じ側にすると、後続囲みが別群となってMIX後の末尾ハイフンが残る。
    [DataRow("夏草の線路 -J-Trance remix- [Another]", "夏草の線路 -J-Trance remix")]
    [DataRow("Air -GOD-", "Air")]
    [DataRow("Air -Revolutionary.Himix- [7keys ANOTHER]", "Air -Revolutionary.Himix")]
    [DataRow("Toki (Sound piercer's Timesucker Remix) [SP ANOTHER]", "Toki (Sound piercer's Timesucker Remix")]
    [DataRow("VIEW [Portable Disco Mix] [H14]", "VIEW [Portable Disco Mix")]
    [DataRow("Lamellibrachia [laner+]", "Lamellibrachia")]
    [DataRow("Lamellibrachia [athlete]", "Lamellibrachia")]
    [DataRow("Lamellibrachia [robo]", "Lamellibrachia")]
    [DataRow("new earth [ZOI]", "new earth")]
    [DataRow("[主題] [差分]", "[主題]")]
    // 主題の交差括弧は修復・切断せず、末尾差分の有無で同じ主題と照合キーを保つ。
    [DataRow("【Freak《Show】Down》 [Another]", "【Freak《Show】Down》")]
    [DataRow("【Freak《Show】Down》", "【Freak《Show】Down》")]
    [DataRow("Nothingness[7A(LN)]", "Nothingness")]
    [DataRow("Title (A) 続き [差分]", "Title (A) 続き")]
    [DataRow("Title [差分] 追加表記", "Title")]
    [DataRow("~メッセージ~ [Another]", "~メッセージ~")]
    [DataRow("Title (未閉鎖", "Title")]
    [DataRow("Title ]", "Title")]
    [DataRow("Title 【差分】 《追加》", "Title")]
    [DataRow("Title <差分> 〜追加〜", "Title")]
    [DataRow(" Ｔｉｔｌｅ　（Ａｎｏｔｈｅｒ） ", "Title")]
    [DataRow("Title-Another", "Title-Another")]
    [DataRow("(Wrapped Title)", "(Wrapped Title)")]
    [DataRow("Title [mix A remix B]", "Title [mix A remix")]
    public void NormalizeTitleForTieBreak_UsesRightmostGroupAndMixEnd(string rawTitle, string displayTitle)
    {
        InstallEstimationMetadataProfile profile = InstallEstimationMetadataNormalizer.BuildProfile([(rawTitle, "", "a")]);
        Assert.AreEqual(displayTitle, profile.DominantPairDisplayTitle);
        Assert.AreEqual(displayTitle.ToLowerInvariant(), profile.DominantNormalizedTitle);
        Assert.AreEqual(displayTitle.ToLowerInvariant(), InstallEstimationMetadataNormalizer.NormalizeTitleForTieBreak(rawTitle));
    }

    /// <summary>副役割の表記揺れを同じ主名義へ照合し、大小文字・複合区切り・日本語直結の違いを一致条件へ残さない。</summary>
    [DataTestMethod]
    [DataRow("", "")]
    [DataRow("obj: diffuser", "")]
    // NFKC・大小文字・複合区切りを同時に含んでも、名義のピリオドは削らない。
    [DataRow("  ＳＫＹ．　ＯＢＪ ：；／ Diff  ", "SKY.")]
    [DataRow("SKY.obj:M1CH1", "SKY.")]
    [DataRow("Artist ; .: NoTeS: Diff", "Artist")]
    [DataRow("Artist;notes:Diff", "Artist;")]
    [DataRow("Artist ;/; notes:Diff", "Artist")]
    [DataRow("Music:わかどり | Movie:もがみ", "Music:わかどり")]
    [DataRow("Artist|obj:Diff", "Artist|")]
    // 名義内のハイフンと役割を囲むハイフンを区別し、制作語にも同じ左境界を要求する。
    [DataRow("GU-Note / obj: Mary_Sue", "GU-Note")]
    [DataRow("Frontier Lyric / obj.Diff", "Frontier Lyric")]
    [DataRow("Frontier Lyric/ obj.Diff", "Frontier Lyric")]
    [DataRow("Raver -feat: Guest-", "Raver")]
    [DataRow("Artist feat: Guest-remix: A", "Artist")]
    // 単独BMSや担当名を挟む役割の列を、直接連結した兼任表記として保護しない。
    [DataRow("Music:Musi_chan / BMS:G&P", "Music:Musi_chan")]
    [DataRow("Artist obj:Diff & BGA:Visual", "Artist")]
    [DataRow("Lalot. feat. 翠京 (BGA:SENALI × Frill OBJ: LuvTek ×", "Lalot.")]
    [DataRow("源屋feat.ユスラ", "源屋")]
    [DataRow("Artist featuring Guest", "Artist")]
    [DataRow("Artist ft. Guest", "Artist")]
    [DataRow("Artist Vo.: Guest", "Artist")]
    [DataRow("Artist vocals Guest", "Artist")]
    [DataRow("Artist obj Guest", "Artist")]
    [DataRow("Artist objected by Diff", "Artist")]
    [DataRow("Artist note by Diff", "Artist")]
    [DataRow("Artist pmsobj=Diff", "Artist")]
    [DataRow("MIPSYLE odj.ユ / BGA: ジンジャー塚原", "MIPSYLE")]
    [DataRow("Nota BGI:雪椿", "Nota")]
    [DataRow("Nota/obj:みあ BGI:雪椿", "Nota")]
    [DataRow("あとぅす // 差分: Eri BGI:りーふぱいザウルス", "あとぅす")]
    [DataRow("Artist 譜面: Diff", "Artist")]
    [DataRow("Artist 差分 Diff", "Artist")]
    [DataRow("AKITO / #050C1B", "AKITO")]
    [DataRow("Artist//Diff", "Artist")]
    [DataRow("Artist/ Diff", "Artist")]
    // 役割から始まる括弧群は閉じ忘れも除去し、名義の括弧は保持する。
    [DataRow("Abel (Mov.Siesphere Illust.suisou)", "Abel")]
    [DataRow("kanone[BGI:TAKA.S", "kanone")]
    [DataRow("Artist ( [\nfeat. Guest", "Artist")]
    [DataRow("Artist <Movie: Visual>", "Artist")]
    [DataRow("Artist 【《illust.:. Visual》】", "Artist")]
    [DataRow("yoayua (yoa + yuayua) Movie:自動車 原付 & うまるつふり", "yoayua (yoa + yuayua)")]
    // 合成作者にも現れる譜面・画像・演奏の一般語は、明示された役割として除去する。
    [DataRow("AKITO chart:exclusion", "AKITO")]
    [DataRow("Artist BMS:Chart_Generator,obj:Diff", "Artist")]
    [DataRow("Artist Graphic : Visual", "Artist")]
    [DataRow("牧野 Sliced by И / obj: Diff", "牧野")]
    [DataRow("Artist (Guitar: Player, Vocal: Singer)", "Artist")]
    [DataRow("Artist PMS by Diff", "Artist")]
    [DataRow("Artist Pattern By Diff", "Artist")]
    [DataRow("Artist Sequence: Diff", "Artist")]
    [DataRow("Artist Lyrics: Guest", "Artist")]
    [DataRow("Artist image: Visual", "Artist")]
    [DataRow("Artist Artwork: Visual", "Artist")]
    [DataRow("Artist Visual: Visual", "Artist")]
    // 純副役割の兼任や副作者名に含まれる一般語は、先行する客演の除去を抑止しない。
    [DataRow("Artist Illust&obj:JURUMA", "Artist")]
    [DataRow("Artist BGA&obj:Diff", "Artist")]
    [DataRow("Artist obj + BGA:Diff obj:Tail", "Artist")]
    [DataRow("Artist (bga/obj.: Diff)", "Artist")]
    [DataRow("Artist feat. Guest / BGA & obj:Diff", "Artist")]
    [DataRow("Artist Bms＆Bga:Diff", "Artist")]
    [DataRow("Artist obj.better music seter", "Artist")]
    [DataRow("MiKaDo feat. はらもりよしな Guitar:裸(ﾗｵｳ)王 BGA:MiKaDo Illust&obj:JURUMA", "MiKaDo")]
    // 括弧内の名義を残して客演差を除き、括弧が閉じるかどうかを一致条件にしない。
    [DataRow("Aliesrite* (Ym1024 feat. GuestA)", "Aliesrite* (Ym1024 feat. GuestB)")]
    [DataRow("Aliesrite* (Ym1024 feat. Guest)", "Aliesrite* (Ym1024)")]
    [DataRow("A (B [C feat. X", "A (B [C)")]
    [DataRow("A (B] feat. X", "A (B]")]
    public void IsStrongMatch_IgnoresSecondaryArtistCredits(string artist, string candidateArtist)
    {
        InstallEstimationMetadataProfile target = InstallEstimationMetadataNormalizer.BuildProfile([("  Ｓｏｎｇ [ANOTHER]  ", artist, "a")]);
        InstallEstimationMetadataProfile candidate = InstallEstimationMetadataNormalizer.BuildProfile([("Song", candidateArtist, "b")]);

        Assert.IsTrue(InstallEstimationMetadataNormalizer.IsStrongMatch(target, candidate));
    }

    /// <summary>役割に似た名義・名義内の記号・共作者を短い別名義へ一致させず、過剰切断による偽一致を検出する。</summary>
    [DataTestMethod]
    // 曖昧な短語・一般名詞とASCII語中には、役割としての境界がない。
    [DataRow("Key Note Team", "Key")]
    [DataRow("Object", "")]
    [DataRow("FT 718", "")]
    [DataRow("Vo Team", "")]
    [DataRow("BGAキャラ水着ガチャ", "")]
    [DataRow("ZizhiRFeather", "ZizhiR")]
    [DataRow("Artist_feat.Guest", "Artist")]
    [DataRow("PakPiano-Note , DJ Demadosa", "PakPiano")]
    [DataRow("Frontier Lyric / obj.Diff", "Frontier")]
    // 空白のないスラッシュと、名義に属する句読点・共作者を保つ。
    [DataRow("q/stol", "q")]
    [DataRow("Dango/Dx", "Dango")]
    [DataRow("1/8", "1")]
    [DataRow("π/3", "π")]
    [DataRow("/指紋", "")]
    [DataRow("AcuticNotes(An/レク)", "AcuticNotes(An)")]
    [DataRow("A (B / C)", "A (B)")]
    [DataRow("sasakure.UK", "sasakure.")]
    [DataRow("Re:gats&", "Re")]
    [DataRow("A & B + C vs D", "A")]
    [DataRow("Artist | Other", "Artist")]
    public void IsStrongMatch_PreservesMainArtistNames(string artist, string otherArtist)
    {
        InstallEstimationMetadataProfile target = InstallEstimationMetadataNormalizer.BuildProfile([("Song", artist, "a")]);
        InstallEstimationMetadataProfile candidate = InstallEstimationMetadataNormalizer.BuildProfile([("Song", otherArtist, "b")]);

        Assert.IsFalse(InstallEstimationMetadataNormalizer.IsStrongMatch(target, candidate));
    }

    /// <summary>先行する副役割を除いて明示制作担当から照合を再開し、客演名・括弧位置・譜面担当だけの差を残さない。</summary>
    [DataTestMethod]
    [DataRow("Zris Remixed by Daily BG by Rosa1100", "Zris Remixed by Daily")]
    [DataRow("plastic / Remix: AYhaz / BGI: Loni", "plastic / Remix: AYhaz")]
    [DataRow("(C)Nintendo / Arranged by Y.W", "(C)Nintendo / Arranged by Y.W")]
    [DataRow("AKITO // mixed by TOSH", "AKITO // mixed by TOSH")]
    [DataRow("株式会社タカラ / Covered by はろるど", "株式会社タカラ / Covered by はろるど")]
    [DataRow("ねこみりん feat.みゆ Remixed by Ras", "ねこみりん Remixed by Ras")]
    [DataRow("DJ obelisk feat.axia /remixed.Pino obj:Diff", "DJ obelisk remixed.Pino")]
    [DataRow("Artist / Remix: A / Arr. B / obj: Diff", "Artist Remix: A Arr. B")]
    [DataRow("Artist feat GuestA Remixed by X", "Artist feat GuestB Remixed by X")]
    [DataRow("Artist feat G (Remixed by X)", "Artist (Remixed by X)")]
    [DataRow("Artist -remix: A obj:Diff", "Artist -remix: A")]
    [DataRow("Symphonian feat.kei_iwata rmx bbangsami, illust CAPOKI", "Symphonian rmx bbangsami,")]
    [DataRow("SC.Kanata feat.ORI(composed:OKAYAN)", "SC.Kanata composed:OKAYAN")]
    [DataRow("Midi:sawa Bms＆Bga:くだみつ", "Midi:sawa")]
    // 制作役割を含む兼任は一括保持し、導入用スラッシュの再切断を検出する。
    [DataRow("Music/BMS/BGA - A obj:Diff", "Music/BMS/BGA - A")]
    [DataRow("Artist feat G music & movie: A", "Artist music & movie: A")]
    [DataRow("BMS:ChartAuthor MIDI:Composer", "MIDI:Composer")]
    [DataRow("BMS/GYAON 作曲/井上⊿ chart:Diff", "作曲/井上⊿")]
    [DataRow("Artist feat G Remix / Pino", "Artist Remix / Pino")]
    public void IsStrongMatch_ResumesAtExplicitMusicCredits(string artist, string candidateArtist)
    {
        InstallEstimationMetadataProfile target = InstallEstimationMetadataNormalizer.BuildProfile([("Song", artist, "a")]);
        InstallEstimationMetadataProfile candidate = InstallEstimationMetadataNormalizer.BuildProfile([("Song", candidateArtist, "b")]);

        Assert.IsTrue(InstallEstimationMetadataNormalizer.IsStrongMatch(target, candidate));
    }

    /// <summary>異なる役割接尾を持つ同名義を一組へ集約し、接尾の揺れによって少数の別作者が代表に選ばれる誤りを検出する。</summary>
    [TestMethod]
    public void BuildProfile_GroupsEquivalentArtistCredits()
    {
        InstallEstimationMetadataProfile profile = InstallEstimationMetadataNormalizer.BuildProfile([
            ("Song", "SKY. feat. Guest", "a"), ("Song", "SKY. obj: Diff", "b"),
            ("Song", "Other", "c"), ("Song", "Other", "d"), ("Song", "SKY. BGA: Visual", "e")]);

        Assert.AreEqual("SKY.", profile.DominantPairDisplayArtist);
        Assert.AreEqual("sky.", profile.DominantNormalizedArtist);
        Assert.AreEqual("song", profile.DominantNormalizedTitle);
    }

    /// <summary>実プロファイルからStrongを判定し、役割接尾の一致でTITLE条件や異なる名義・リミックス作者の区別を迂回しない。</summary>
    [DataTestMethod]
    [DataRow("Ice Festival", "Nota BGI:雪椿", "Ice Festival[札幌雪祭り]", "Nota/obj:みあ BGI:雪椿", true)]
    // ARTISTが一致しても、末尾点や別mix名によるTITLEの差は同じ閾値で判断する。
    [DataRow("I believe", "Gun string", "I believe...", "Gun string", true)]
    [DataRow("Midnight BEAST", "AKITO", "Midnight BEAST -deep azure mix-", "AKITO / #050C1B", false)]
    // 別名推定やスラッシュ切断で、異なる制作名義を同一視しない。
    [DataRow("Song", "Zris Remixed by Daily天利 BG by Rosa", "Song", "Zris Remixed by Daily BG by Rosa", false)]
    [DataRow("Song", "Origin / Remixed by A", "Song", "Origin / Remixed by B", false)]
    [DataRow("Song", "Artist feat GuestA Remixed by X", "Song", "Artist feat GuestB Remixed by Y", false)]
    [DataRow("Song", "Artist obj:Same", "Song", "Other obj:Same", false)]
    [DataRow("Song", "q/stol", "Song", "q/other", false)]
    public void IsStrongMatch_RequiresTitleAndMainArtistEvidence(string title, string artist, string candidateTitle, string candidateArtist, bool expected)
    {
        InstallEstimationMetadataProfile target = InstallEstimationMetadataNormalizer.BuildProfile([(title, artist, "a")]);
        InstallEstimationMetadataProfile candidate = InstallEstimationMetadataNormalizer.BuildProfile([(candidateTitle, candidateArtist, "b")]);

        Assert.AreEqual(expected, InstallEstimationMetadataNormalizer.IsStrongMatch(target, candidate));
    }

    /// <summary>独立した最頻ARTISTと異なる実在最頻ペアを採用し、同一レコードの比較キーと表示を保持する。</summary>
    [TestMethod]
    public void BuildProfile_SelectsRealDominantPairAndDisplay()
    {
        InstallEstimationMetadataProfile profile = InstallEstimationMetadataNormalizer.BuildProfile([
            ("A", "X", "a"), ("A", "X", "b"), ("A", "X", "c"),
            ("B", "Y", "d"), ("B", "Y", "e"), ("C", "Y", "f"), ("C", "Y", "g")]);

        Assert.AreEqual("a", profile.DominantNormalizedTitle);
        Assert.AreEqual("x", profile.DominantNormalizedArtist);
        Assert.AreEqual("A", profile.DominantPairDisplayTitle);
        Assert.AreEqual("X", profile.DominantPairDisplayArtist);
    }

    /// <summary>空TITLEを除外し、空ARTISTを有効なペアとして集計する。空TITLEだけの入力から作者を補完しない。</summary>
    [TestMethod]
    public void BuildProfile_ExcludesEmptyTitleAndAcceptsEmptyArtist()
    {
        InstallEstimationMetadataProfile profile = InstallEstimationMetadataNormalizer.BuildProfile([
            ("Alpha", "", "a"), ("Alpha", "obj: Diff", "b"),
            ("", "Beta", "c"), (" ", "Beta", "d"), ("", "Beta", "e"),
            ("", "", "f"), (" ", " ", "g"), ("", "OBJ: Diff", "h"), ("", "", "i")]);

        Assert.AreEqual("alpha", profile.DominantNormalizedTitle);
        Assert.AreEqual(string.Empty, profile.DominantNormalizedArtist);
        Assert.IsTrue(profile.HasPair);
        Assert.AreEqual("Alpha", profile.DominantPairDisplayTitle);
        Assert.AreEqual(string.Empty, profile.DominantPairDisplayArtist);

        foreach ((string, string, string)[] records in new (string, string, string)[][] { [], [(" ", "OBJ: Diff", "a")] })
        {
            InstallEstimationMetadataProfile empty = InstallEstimationMetadataNormalizer.BuildProfile(records);
            Assert.IsFalse(empty.HasPair);
            Assert.AreEqual(string.Empty, empty.DominantPairDisplayTitle);
            Assert.AreEqual(string.Empty, empty.DominantPairDisplayArtist);
        }
    }

    /// <summary>同数グループを大小文字を無視した最小パス・入力位置で選び、表示も同じレコードの両表記から選ぶことを確認する。</summary>
    [TestMethod]
    public void BuildProfile_TiesUsePathThenInputOrderForGroupAndDisplay()
    {
        InstallEstimationMetadataProfile profile = InstallEstimationMetadataNormalizer.BuildProfile([
            ("Song", "ARTIST", "z"), ("Other", "Other", "b"),
            ("SONG", "Artist", "A"), ("Other", "Other", "c")]);
        Assert.AreEqual("song", profile.DominantNormalizedTitle);
        Assert.AreEqual("artist", profile.DominantNormalizedArtist);
        Assert.AreEqual("SONG", profile.DominantPairDisplayTitle);
        Assert.AreEqual("Artist", profile.DominantPairDisplayArtist);

        InstallEstimationMetadataProfile inputTie = InstallEstimationMetadataNormalizer.BuildProfile([
            ("First", "FIRST", "a"), ("Other", "OTHER", "A")]);
        Assert.AreEqual("first", inputTie.DominantNormalizedTitle);
        Assert.AreEqual("first", inputTie.DominantNormalizedArtist);
        Assert.AreEqual("First", inputTie.DominantPairDisplayTitle);
        Assert.AreEqual("FIRST", inputTie.DominantPairDisplayArtist);

        InstallEstimationMetadataProfile displayTie = InstallEstimationMetadataNormalizer.BuildProfile([
            ("Song", "ARTIST", "a"), ("SONG", "Artist", "A")]);
        Assert.AreEqual("Song", displayTie.DominantPairDisplayTitle);
        Assert.AreEqual("ARTIST", displayTie.DominantPairDisplayArtist);
    }

    /// <summary>正規化済みキーのDice境界、重複gram、UTF-16単位を確認し、再切断や集合化の誤りを検出する。</summary>
    [DataTestMethod]
    [DataRow("abcdefghijklmnopqrstuvw", "abcdefghijklmnopqrstuvwxyz012", 0.88)]
    [DataRow("abcdefghijklmnopqrstuvw", "abcdefghijklmnopqrstuvwxyz0123", 44d / 51d)]
    [DataRow("aaaaab", "aab", 4d / 7d)]
    [DataRow("😀a", "😀b", 0.5)]
    [DataRow("a", "a", 1d)]
    [DataRow("", "", 0d)]
    public void ComputeTitleSimilarity_UsesNormalizedKeysAndCountedUtf16Bigrams(string left, string right, double expected)
    {
        Assert.AreEqual(expected, InstallEstimationMetadataNormalizer.ComputeTitleSimilarity(left, right), 1e-12);
    }
    /// <summary>最頻Strongの0.72境界とRelatedの0.50 OR、空TITLEと空ARTISTを独立した期待値で固定します。</summary>
    [DataTestMethod]
    [DataRow("abcdefghijklmnopqrs", "X", "abcdefghijklmnopqrstuvwxyz0123456", "X", (int)InstallEstimationMetadataRelation.Strong)]
    [DataRow("abcdefghijklmnopqrs", "X", "abcdefghijklmnopqrstuvwxyz01234567", "X", (int)InstallEstimationMetadataRelation.Related)]
    [DataRow("abc", "xxx", "abd", "yyy", (int)InstallEstimationMetadataRelation.Related)]
    [DataRow("xxx", "abc", "yyy", "abd", (int)InstallEstimationMetadataRelation.Related)]
    [DataRow("abc", "abc", "xyz", "xyz", (int)InstallEstimationMetadataRelation.Unrelated)]
    [DataRow("abc", "", "abc", "", (int)InstallEstimationMetadataRelation.Strong)]
    [DataRow("abc", "", "xyz", "", (int)InstallEstimationMetadataRelation.Unrelated)]
    [DataRow("", "abc", "abc", "abc", (int)InstallEstimationMetadataRelation.Unavailable)]
    [DataRow("abc", "abc", "", "abc", (int)InstallEstimationMetadataRelation.Unavailable)]
    [DataRow("abc", "xxx", "abc", "yyy", (int)InstallEstimationMetadataRelation.Related)]
    public void Classify_UsesDominantStrongAndIndependentRelatedBoundaries(string title, string artist, string candidateTitle, string candidateArtist,
        int expected)
    {
        InstallEstimationMetadataProfile target = InstallEstimationMetadataNormalizer.BuildProfile([(title, artist, "target")]);
        InstallEstimationMetadataProfile candidate = InstallEstimationMetadataNormalizer.BuildProfile([(candidateTitle, candidateArtist, "candidate")]);
        InstallEstimationMetadataMatch result = new InstallEstimationMetadataMatcher(target).Evaluate(candidate);
        Assert.AreEqual((InstallEstimationMetadataRelation)expected, result.Relation);
    }

    /// <summary>最頻以外の完全一致はRelated止まりとし、1,000番目の実在ペアも省略せず候補側の表示を返します。</summary>
    [TestMethod]
    public void Classify_UsesAllUniquePairsAndKeepsMinorityExactMatchRelated()
    {
        var records = System.Linq.Enumerable.Range(0, 999).Select(i => ("曲" + i, "作者" + i, "a" + i)).ToList();
        records.Insert(0, records[0]);
        records.Add(("Matched Title", "MATCHED ARTIST", "z"));
        InstallEstimationMetadataProfile target = InstallEstimationMetadataNormalizer.BuildProfile([("matched title", "matched artist", "target")]);
        InstallEstimationMetadataProfile candidate = InstallEstimationMetadataNormalizer.BuildProfile(records);
        InstallEstimationMetadataMatch result = new InstallEstimationMetadataMatcher(target).Evaluate(candidate);
        Assert.AreEqual(1000, candidate.Pairs.Count);
        Assert.AreEqual(InstallEstimationMetadataRelation.Related, result.Relation);
        Assert.AreEqual("Matched Title", result.DisplayTitle);
        Assert.AreEqual("MATCHED ARTIST", result.DisplayArtist);
    }

    /// <summary>Related根拠を一レコードから選び、同点はパスと入力順で決めます。別ペアの最高TITLE/ARTISTを合成しません。</summary>
    [TestMethod]
    public void Classify_SelectsOneRealPairWithStablePathAndInputOrder()
    {
        InstallEstimationMetadataProfile target = InstallEstimationMetadataNormalizer.BuildProfile([("abc", "abc", "target")]);
        (string, string, string)[] records = [("abd", "abc", "b"), ("abc", "xyz", "a")];
        foreach ((string, string, string)[]? source in new[] { records, records.Reverse().ToArray() })
        {
            InstallEstimationMetadataMatch result = new InstallEstimationMetadataMatcher(target).Evaluate(InstallEstimationMetadataNormalizer.BuildProfile(source));
            Assert.AreEqual(InstallEstimationMetadataRelation.Related, result.Relation);
            Assert.AreEqual("abc", result.DisplayTitle);
            Assert.AreEqual("xyz", result.DisplayArtist);
        }
        InstallEstimationMetadataMatch tied = new InstallEstimationMetadataMatcher(target).Evaluate(InstallEstimationMetadataNormalizer.BuildProfile([
            ("abd", "abc", "B"), ("abe", "abc", "A")]));
        Assert.AreEqual("abe", tied.DisplayTitle);
        InstallEstimationMetadataMatch inputTied = new InstallEstimationMetadataMatcher(target).Evaluate(InstallEstimationMetadataNormalizer.BuildProfile([
            ("abd", "abc", "A"), ("abe", "abc", "a")]));
        Assert.AreEqual("abd", inputTied.DisplayTitle);
    }

}
