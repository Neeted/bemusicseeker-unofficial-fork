using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Core.Tests.Verification;

/// <summary>
/// SQLite 3.6.7 の互換範囲の共通検査器が、禁止分類を一つずつ検出し、本アプリと sqlite-net が残す互換な定義を通すことを確認します。
/// 本番の各 DB が互換範囲に収まることは、スキーマ準備の入口のテストがこの検査器で確認します。
/// </summary>
[TestClass]
public sealed class Sqlite367SchemaCompatibilityTests
{
    /// <summary>
    /// 条件: 禁止分類を一つずつ含む定義（関数は CHECK・DEFAULT・WHEN・本体・ビュー・索引の各位置）と、開発版の候補索引の定義文そのもの。
    /// 期待結果: それぞれ期待した分類の違反が含まれる（同じ箇所が複数分類で報告されてもよい）。
    /// 検出する誤り: 大小文字・関数名と括弧の間の空白とコメント・引用符付き関数名・CASE や副問合せや COLLATE の前の入れ子・改行をまたぐ
    /// ON CONFLICT・<c>((a))</c> や <c>a+0</c> の索引など、字句の揺れで違反を見逃す偽陰性。
    /// </summary>
    [TestMethod]
    [DataRow("dev-candidate-index", "index", null, "ExpressionIndex")]
    [DataRow("dev-candidate-index-char", "index", null, "UnsupportedFunction:char")]
    [DataRow("check-char", "table", "CREATE TABLE t (a TEXT CHECK (a <> char(65)))", "UnsupportedFunction:char")]
    [DataRow("default-printf", "table", "CREATE TABLE t (a TEXT DEFAULT (printf('%d', 1)))", "UnsupportedFunction:printf")]
    [DataRow("when-instr", "trigger", "CREATE TRIGGER g AFTER INSERT ON t WHEN instr(NEW.a, 'x') > 0 BEGIN DELETE FROM t; END", "UnsupportedFunction:instr")]
    [DataRow("body-iif", "trigger", "CREATE TRIGGER g AFTER INSERT ON t BEGIN UPDATE t SET a = iif(NEW.a, 1, 2); END", "UnsupportedFunction:iif")]
    [DataRow("view-json", "view", "CREATE VIEW v AS SELECT json_extract(a, '$.x') FROM t", "UnsupportedFunction:json_extract")]
    [DataRow("index-unicode", "index", "CREATE INDEX i ON t (unicode(a))", "UnsupportedFunction:unicode")]
    [DataRow("upper-char", "table", "CREATE TABLE t (a TEXT CHECK (a <> CHAR(65)))", "UnsupportedFunction:char")]
    [DataRow("spaced-instr", "table", "CREATE TABLE t (a TEXT CHECK (Instr (a, 'x') = 0))", "UnsupportedFunction:instr")]
    [DataRow("comment-before-paren", "table", "CREATE TABLE t (a TEXT CHECK (a <> char/* c */(65)))", "UnsupportedFunction:char")]
    [DataRow("quoted-function", "table", "CREATE TABLE t (a TEXT CHECK (a <> \"char\"(65)))", "UnsupportedFunction:char")]
    [DataRow("nested-case", "table", "CREATE TABLE t (a TEXT CHECK (CASE WHEN a = char(65) THEN 1 ELSE 0 END = 0))", "UnsupportedFunction:char")]
    [DataRow("nested-subquery", "trigger", "CREATE TRIGGER g AFTER INSERT ON t BEGIN DELETE FROM t WHERE a IN (SELECT char(65)); END", "UnsupportedFunction:char")]
    [DataRow("nested-before-collate", "index", "CREATE INDEX i ON t ((lower(char(65))) COLLATE NOCASE)", "UnsupportedFunction:char")]
    [DataRow("unknown-function", "table", "CREATE TABLE t (a TEXT CHECK (my_function(a)))", "UnsupportedFunction:my_function")]
    [DataRow("load-extension", "view", "CREATE VIEW v AS SELECT load_extension('x')", "UnsupportedFunction:load_extension")]
    [DataRow("table-valued", "view", "CREATE VIEW v AS SELECT * FROM generate_series(1, 2)", "TableValuedFunction")]
    [DataRow("aggregate-order", "view", "CREATE VIEW v AS SELECT group_concat(a ORDER BY a) FROM t", "AggregateOrderBy")]
    [DataRow("expression-index", "index", "CREATE INDEX i ON t (lower(a))", "ExpressionIndex")]
    [DataRow("parenthesized-index", "index", "CREATE INDEX i ON t ((a))", "ExpressionIndex")]
    [DataRow("arithmetic-index", "index", "CREATE INDEX i ON t (a+0)", "ExpressionIndex")]
    [DataRow("partial-index", "index", "CREATE INDEX i ON t (a) WHERE a IS NOT NULL", "PartialIndex")]
    [DataRow("index-collation", "index", "CREATE INDEX i ON t (a COLLATE unicode_ci)", "UnsupportedCollation")]
    [DataRow("generated-always", "table", "CREATE TABLE t (a INTEGER, b INTEGER GENERATED ALWAYS AS (a + 1) STORED)", "GeneratedColumn")]
    [DataRow("generated-as", "table", "CREATE TABLE t (a INTEGER, b INTEGER AS (a + 1))", "GeneratedColumn")]
    [DataRow("without-rowid", "table", "CREATE TABLE t (a TEXT PRIMARY KEY) WITHOUT ROWID", "WithoutRowid")]
    [DataRow("strict", "table", "CREATE TABLE t (a TEXT) STRICT", "Strict")]
    [DataRow("unknown-table-option", "table", "CREATE TABLE t (a TEXT) FUTURE_OPTION", "TableOption:FUTURE_OPTION")]
    [DataRow("hex-default", "table", "CREATE TABLE t (a INTEGER DEFAULT 0x10)", "HexLiteral")]
    [DataRow("default-identifier", "table", "CREATE TABLE t (a INTEGER DEFAULT TRUE)", "UnsupportedDefault")]
    [DataRow("cte", "view", "CREATE VIEW v AS WITH x AS (SELECT 1) SELECT * FROM x", "CommonTableExpression")]
    [DataRow("window", "view", "CREATE VIEW v AS SELECT count(*) OVER () FROM t", "WindowFunction")]
    [DataRow("filter", "view", "CREATE VIEW v AS SELECT count(*) FILTER (WHERE a > 0) FROM t", "WindowFunction")]
    [DataRow("upsert-split-line", "trigger", "CREATE TRIGGER g AFTER INSERT ON t BEGIN INSERT INTO u (a) VALUES (NEW.a) ON\nCONFLICT (a) DO NOTHING; END", "Upsert")]
    [DataRow("returning", "trigger", "CREATE TRIGGER g AFTER INSERT ON t BEGIN DELETE FROM u RETURNING a; END", "Returning")]
    [DataRow("row-value", "trigger", "CREATE TRIGGER g AFTER INSERT ON t BEGIN DELETE FROM u WHERE (a, b) = (1, 2); END", "RowValue")]
    [DataRow("update-set-row", "trigger", "CREATE TRIGGER g AFTER INSERT ON t BEGIN UPDATE u SET (a, b) = (1, 2); END", "RowValue")]
    [DataRow("multi-row-values", "trigger", "CREATE TRIGGER g AFTER INSERT ON t BEGIN INSERT INTO u (a) VALUES (1), (2); END", "MultiRowValues")]
    [DataRow("nulls-last", "view", "CREATE VIEW v AS SELECT a FROM t ORDER BY a NULLS LAST", "NullsOrdering")]
    [DataRow("right-join", "view", "CREATE VIEW v AS SELECT * FROM t RIGHT JOIN u ON t.a = u.a", "RightOrFullJoin")]
    [DataRow("full-join", "view", "CREATE VIEW v AS SELECT * FROM t FULL OUTER JOIN u ON t.a = u.a", "RightOrFullJoin")]
    [DataRow("json-arrow", "view", "CREATE VIEW v AS SELECT a -> '$.x' FROM t", "JsonOperator")]
    [DataRow("is-value", "trigger", "CREATE TRIGGER g AFTER UPDATE ON t WHEN NEW.a IS NOT OLD.a BEGIN DELETE FROM u; END", "IsComparison")]
    [DataRow("update-from", "trigger", "CREATE TRIGGER g AFTER INSERT ON t BEGIN UPDATE u SET a = x.a FROM x WHERE x.b = u.b; END", "UpdateFrom")]
    [DataRow("view-columns", "view", "CREATE VIEW v (a) AS SELECT 1", "ViewColumnList")]
    [DataRow("virtual-table", "table", "CREATE VIRTUAL TABLE t USING fts5(a)", "VirtualTable")]
    [DataRow("object-type", "module", "CREATE TABLE t (a)", "UnsupportedObjectType")]
    [DataRow("keyword-name-function", "table", "CREATE TABLE t (a TEXT CHECK (regexp('x', a)))", "UnsupportedFunction:regexp")]
    public void FindViolations_RejectsDefinitionOutsideSqlite367(string caseName, string type, string? sql, string expected)
    {
        string definition = sql ?? BeMusicSeeker.Tests.PlaylistEntryCommitTests.DevelopmentExpressionCandidateIndexSql;
        string[] parts = expected.Split(':');
        Sqlite367ViolationCategory category = Enum.Parse<Sqlite367ViolationCategory>(parts[0]);
        IReadOnlyList<Sqlite367Violation> violations = Sqlite367SchemaCompatibility.FindViolations(type, definition);
        Assert.IsTrue(violations.Any(violation => violation.Category == category
                && (parts.Length == 1 || string.Equals(violation.Detail, parts[1], StringComparison.OrdinalIgnoreCase))),
            caseName + ": " + string.Join("; ", violations.Select(violation => violation.Category + "(" + violation.Detail + ")")));
    }

    /// <summary>
    /// 条件: sqlite-net の小文字・引用符付き DDL、本アプリの表・索引・トリガーの構文、文字列・引用符付き識別子・コメント内の禁止語。
    /// 期待結果: 違反は0件。
    /// 検出する誤り: 型の桁数・列制約・衝突句・名前位置の括弧を式や関数と誤認し、互換な既存定義を不合格にする偽陽性。
    /// </summary>
    [TestMethod]
    [DataRow("sqlite-net", "table", "create table if not exists \"playlist\"(\n\"playlist_id\" integer primary key autoincrement not null ,\n\"name\" varchar(140) ,\n\"amount\" DECIMAL(10,2) ,\n\"tag\" TEXT NULL)")]
    [DataRow("defaults", "table", "CREATE TABLE t (a INTEGER NOT NULL DEFAULT 0, b INTEGER DEFAULT -1, c TEXT DEFAULT 'x', d TEXT DEFAULT CURRENT_TIMESTAMP, e INTEGER DEFAULT (0), f BLOB DEFAULT X'00', g TEXT DEFAULT NULL)")]
    [DataRow("constraints", "table", "CREATE TABLE t (id INTEGER PRIMARY KEY CHECK (id = 1), a TEXT COLLATE NOCASE, b TEXT, p INTEGER REFERENCES p(x) ON DELETE CASCADE, UNIQUE(a,b) ON CONFLICT REPLACE, CONSTRAINT k CHECK (b IS NULL OR b IS NOT NULL))")]
    [DataRow("composite-key", "table", "CREATE TABLE t (a TEXT, b TEXT, PRIMARY KEY(a,b), FOREIGN KEY (a) REFERENCES p(x))")]
    [DataRow("sqlite-sequence", "table", "CREATE TABLE sqlite_sequence(name,seq)")]
    [DataRow("sqlite-net-index", "index", "create unique index if not exists \"playlist_entry_idx_uniq\" on \"playlist_entry\"(\"md5\", \"sha256\", \"playlist_id\")")]
    [DataRow("nocase-index", "index", "CREATE INDEX folder_path_nocase ON folder (path COLLATE NOCASE)")]
    [DataRow("desc-index", "index", "CREATE INDEX IF NOT EXISTS h ON bms_lr2_play_history(played_at DESC, history_id DESC)")]
    [DataRow("candidate-index", "index", "CREATE INDEX playlist_entry_idx_candidate ON playlist_entry (playlist_id, md5 COLLATE NOCASE, sha256 COLLATE NOCASE, folder, lr2_bmsid, title, is_removed)")]
    [DataRow("trigger", "trigger", "CREATE TRIGGER IF NOT EXISTS g AFTER UPDATE OF a, b ON t FOR EACH ROW WHEN NEW.a > OLD.a AND EXISTS (SELECT 1 FROM p WHERE id = 1 AND created_at >= CAST(strftime('%s', 'now') AS INTEGER) - 10) BEGIN INSERT INTO p(id, history_id) VALUES (1, last_insert_rowid()); INSERT OR REPLACE INTO l (hash, at) VALUES (NEW.hash, (SELECT at FROM h WHERE id = (SELECT id FROM p WHERE id = 1))); UPDATE h SET d = (NEW.a + NEW.b) - (OLD.a + OLD.b), e = CASE WHEN NEW.a IN (1,2) THEN 1 ELSE 0 END WHERE id = 1; DELETE FROM p WHERE id = 1; END")]
    [DataRow("raise", "trigger", "CREATE TRIGGER g BEFORE INSERT ON t WHEN NEW.a IS NULL BEGIN SELECT RAISE(ABORT, 'rejected'); END")]
    [DataRow("forbidden-words-in-literals", "table", "CREATE TABLE t (\"over\" TEXT DEFAULT 'WITH char( OVER', [returning] INTEGER /* RETURNING */ -- WITH char(\n, `window` TEXT CHECK (\"over\" <> 'json_extract('))")]
    [DataRow("view", "view", "CREATE VIEW v AS SELECT a, count(*), group_concat(b, ',') FROM t LEFT JOIN u USING (a) GROUP BY a ORDER BY a DESC")]
    [DataRow("keyword-column-index", "index", "CREATE INDEX i ON t (key, first COLLATE NOCASE, last, rows DESC, range, action, filter)")]
    public void FindViolations_AcceptsSqlite367Definition(string caseName, string type, string sql)
    {
        IReadOnlyList<Sqlite367Violation> violations = Sqlite367SchemaCompatibility.FindViolations(type, sql);
        Assert.AreEqual(0, violations.Count,
            caseName + ": " + string.Join("; ", violations.Select(violation => violation.Category + "(" + violation.Detail + ")")));
    }

    /// <summary>
    /// 条件: 実 DB に式索引・部分索引・生成列・WITHOUT ROWID・STRICT を作る。
    /// 期待結果: 接続版の検査がそれぞれのオブジェクトについて該当分類を返す。
    /// 検出する誤り: sqlite_master の sql だけを見て、エンジンが認識した構造（PRAGMA 由来）を見落とす誤りと、接続版が行を読み落とす誤り。
    /// </summary>
    [TestMethod]
    public void FindViolations_StoredDefinitionsIncludeEngineIndexAndTableShape()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var databaseScope = new ApplicationDatabaseScope();
        string path = Path.Combine(workspace.RootDirectory, "shape.db");
        using (SQLiteConnectionEx db = databaseScope.OpenProviderConnection(path))
        {
            db.Execute("CREATE TABLE base (a INTEGER, b TEXT);");
            db.Execute("CREATE INDEX expression_index ON base (a + 1);");
            db.Execute("CREATE INDEX partial_index ON base (b) WHERE a > 0;");
            db.Execute("CREATE TABLE generated_table (a INTEGER, b INTEGER GENERATED ALWAYS AS (a * 2) VIRTUAL);");
            db.Execute("CREATE TABLE without_rowid_table (a TEXT PRIMARY KEY) WITHOUT ROWID;");
            db.Execute("CREATE TABLE strict_table (a INTEGER) STRICT;");
        }
        IReadOnlyList<Sqlite367Violation> violations;
        using (SQLiteConnectionEx verify = databaseScope.OpenProviderConnection(path, SQLite.SQLiteOpenFlags.ReadOnly))
        {
            violations = Sqlite367SchemaCompatibility.FindViolations(verify);
        }
        string listing = string.Join("; ", violations.Select(violation => violation.Name + ":" + violation.Category + "(" + violation.Detail + ")"));
        AssertContains("expression_index", Sqlite367ViolationCategory.ExpressionIndex, "index_xinfo cid=-2");
        AssertContains("partial_index", Sqlite367ViolationCategory.PartialIndex, "index_list partial=1");
        AssertContains("generated_table", Sqlite367ViolationCategory.GeneratedColumn, null);
        AssertContains("without_rowid_table", Sqlite367ViolationCategory.WithoutRowid, "table_list wr=1");
        AssertContains("strict_table", Sqlite367ViolationCategory.Strict, "table_list strict=1");
        Assert.IsFalse(violations.Any(violation => violation.Name == "base"), listing);

        void AssertContains(string name, Sqlite367ViolationCategory category, string? detail)
            => Assert.IsTrue(violations.Any(violation => violation.Name == name && violation.Category == category
                && (detail == null || violation.Detail == detail)), name + " " + category + ": " + listing);
    }
}
