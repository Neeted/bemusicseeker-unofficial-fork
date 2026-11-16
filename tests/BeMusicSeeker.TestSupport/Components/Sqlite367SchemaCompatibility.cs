using System.Text;
using SQLite;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>SQLite 3.6.7 が解析できない、DB に残るスキーマ定義の分類です。</summary>
internal enum Sqlite367ViolationCategory
{
    /// <summary>字句・構文を解釈できない定義。未知の形は互換と扱わず不合格にします。</summary>
    Unparsable,
    /// <summary>table・index・view・trigger 以外の種別。</summary>
    UnsupportedObjectType,
    /// <summary><c>CREATE VIRTUAL TABLE</c>。</summary>
    VirtualTable,
    /// <summary>列名と COLLATE・ASC/DESC 以外を含む索引項目（3.9.0）。</summary>
    ExpressionIndex,
    /// <summary>WHERE 句付きの部分索引（3.8.0）。</summary>
    PartialIndex,
    /// <summary>BINARY・NOCASE・RTRIM 以外の照合順序。</summary>
    UnsupportedCollation,
    /// <summary>生成列（3.31.0）。</summary>
    GeneratedColumn,
    /// <summary><c>WITHOUT ROWID</c>（3.8.2）。</summary>
    WithoutRowid,
    /// <summary><c>STRICT</c>（3.37.0）。</summary>
    Strict,
    /// <summary>表定義の閉じ括弧の後に続くその他の表オプション。</summary>
    TableOption,
    /// <summary>数値・文字列・NULL・BLOB・CURRENT_*・括弧式以外の DEFAULT。</summary>
    UnsupportedDefault,
    /// <summary>0x の16進整数リテラル（3.8.6）。</summary>
    HexLiteral,
    /// <summary>3.6.7 の組込み関数の許可一覧にない関数呼出し。</summary>
    UnsupportedFunction,
    /// <summary>FROM・JOIN の表値関数（3.9.0）。</summary>
    TableValuedFunction,
    /// <summary>関数引数内の ORDER BY（3.44.0）。</summary>
    AggregateOrderBy,
    /// <summary>WITH による共通表式（3.8.3）。</summary>
    CommonTableExpression,
    /// <summary>OVER・FILTER・WINDOW によるウィンドウ関数・集約フィルター（3.25.0・3.30.0）。</summary>
    WindowFunction,
    /// <summary>UPSERT の <c>ON CONFLICT (</c>・<c>ON CONFLICT DO</c>・<c>DO</c>（3.24.0）。</summary>
    Upsert,
    /// <summary><c>RETURNING</c>（3.35.0）。</summary>
    Returning,
    /// <summary>行値（3.15.0）と <c>UPDATE ... SET (</c>。</summary>
    RowValue,
    /// <summary>複数行の <c>VALUES (...),(...)</c>（3.7.11）。</summary>
    MultiRowValues,
    /// <summary><c>NULLS FIRST/LAST</c>（3.30.0）。</summary>
    NullsOrdering,
    /// <summary>RIGHT・FULL JOIN（3.39.0）。</summary>
    RightOrFullJoin,
    /// <summary><c>-&gt;</c>・<c>-&gt;&gt;</c> 演算子（3.38.0）。</summary>
    JsonOperator,
    /// <summary>NULL 以外を右辺に取る <c>IS [NOT]</c>（3.6.19）。</summary>
    IsComparison,
    /// <summary><c>UPDATE ... FROM</c>（3.33.0）。</summary>
    UpdateFrom,
    /// <summary><c>CREATE VIEW 名 (列, ...)</c> の列名リスト（3.9.0）。</summary>
    ViewColumnList,
}

/// <summary>SQLite 3.6.7 の互換範囲を超えるスキーマ定義の一件です。</summary>
/// <param name="Type">sqlite_master の type。</param>
/// <param name="Name">オブジェクト名。</param>
/// <param name="Category">違反の分類。</param>
/// <param name="Sql">検査した定義文。構造 PRAGMA 由来の違反では sqlite_master の sql。</param>
/// <param name="Detail">関数名・照合順序名など、違反した要素。</param>
internal sealed record Sqlite367Violation(string Type, string Name, Sqlite367ViolationCategory Category, string Sql, string Detail);

/// <summary>
/// DB に残る表・索引・ビュー・トリガーの定義が、LR2 の SQLite 3.6.7 で解析できる構文と関数だけかを検査します。
/// </summary>
/// <remarks>
/// <para>
/// 文字列・引用符付き識別子・コメントを字句として処理し、キーワード・関数名は大小文字を区別しません。
/// 式の位置で識別子（引用符付きを含む）の直後に <c>(</c> が続けば関数呼出しとし、閉じた許可一覧にない名前は未知の関数も含めて不合格にします。
/// 表名・列名・型名の位置と、CAST・RAISE・EXISTS・IN・VALUES・CHECK・UNIQUE・PRIMARY KEY・FOREIGN KEY・REFERENCES の後の表名・DEFAULT・USING は関数として扱いません。
/// </para>
/// <para>
/// 接続版は sqlite_master の sql 非 NULL の全行を文字列規則で検査し、さらに <c>PRAGMA index_xinfo</c>・<c>index_list</c>・<c>table_xinfo</c>・<c>table_list</c>
/// でエンジンが認識した式索引・部分索引・生成列・WITHOUT ROWID・STRICT を検出します。sql が NULL の自動索引は対象外です。
/// </para>
/// </remarks>
internal static class Sqlite367SchemaCompatibility
{
    /// <summary>
    /// SQLite 3.6.7 で使える組込み関数の閉じた許可一覧です。
    /// 出典は SQLite 公式文書の core functions（https://www.sqlite.org/lang_corefunc.html）、
    /// aggregate functions（https://www.sqlite.org/lang_aggfunc.html）、date and time functions（https://www.sqlite.org/lang_datefunc.html）と、
    /// 各関数の追加版を記した release history（https://www.sqlite.org/changes.html）です。
    /// 3.6.7 より後に追加された char・unicode（3.7.16）、instr（3.7.15）、printf（3.8.3）、likelihood 系（3.8.x）、iif（3.32.0）、JSON 関数などは含めません。
    /// 拡張読込みの load_extension と、既定のビルドで無効な soundex は、存在しても互換範囲として扱わないため含めません。
    /// </summary>
    private static readonly HashSet<string> AllowedFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "abs", "changes", "coalesce", "glob", "hex", "ifnull", "last_insert_rowid", "length", "like", "lower", "ltrim",
        "max", "min", "nullif", "quote", "random", "randomblob", "replace", "round", "rtrim", "sqlite_version", "substr",
        "total_changes", "trim", "typeof", "upper", "zeroblob",
        "avg", "count", "group_concat", "sum", "total",
        "date", "time", "datetime", "julianday", "strftime",
    };

    private static readonly HashSet<string> AllowedCollations = new(StringComparer.OrdinalIgnoreCase) { "BINARY", "NOCASE", "RTRIM" };

    private static readonly HashSet<string> ConflictResolutions = new(StringComparer.OrdinalIgnoreCase) { "ROLLBACK", "ABORT", "FAIL", "IGNORE", "REPLACE" };

    /// <summary>
    /// 識別子の直後の <c>(</c> を関数呼出しとみなさない SQLite のキーワードです（https://www.sqlite.org/lang_keywords.html）。
    /// 関数名でもある LIKE・GLOB・REPLACE は含めず、許可一覧の関数として扱います。
    /// </summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ABORT", "ACTION", "ADD", "AFTER", "ALL", "ALTER", "ALWAYS", "ANALYZE", "AND", "AS", "ASC", "ATTACH", "AUTOINCREMENT",
        "BEFORE", "BEGIN", "BETWEEN", "BY", "CASCADE", "CASE", "CAST", "CHECK", "COLLATE", "COLUMN", "COMMIT", "CONFLICT",
        "CONSTRAINT", "CREATE", "CROSS", "CURRENT", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "DATABASE", "DEFAULT",
        "DEFERRABLE", "DEFERRED", "DELETE", "DESC", "DETACH", "DISTINCT", "DO", "DROP", "EACH", "ELSE", "END", "ESCAPE", "EXCEPT",
        "EXCLUDE", "EXCLUSIVE", "EXISTS", "EXPLAIN", "FAIL", "FILTER", "FIRST", "FOLLOWING", "FOR", "FOREIGN", "FROM", "FULL",
        "GENERATED", "GROUP", "GROUPS", "HAVING", "IF", "IGNORE", "IMMEDIATE", "IN", "INDEX", "INDEXED", "INITIALLY", "INNER",
        "INSERT", "INSTEAD", "INTERSECT", "INTO", "IS", "ISNULL", "JOIN", "KEY", "LAST", "LEFT", "LIMIT", "MATCH", "MATERIALIZED",
        "NATURAL", "NO", "NOT", "NOTHING", "NOTNULL", "NULL", "NULLS", "OF", "OFFSET", "ON", "OR", "ORDER", "OTHERS", "OUTER",
        "OVER", "PARTITION", "PLAN", "PRAGMA", "PRECEDING", "PRIMARY", "QUERY", "RAISE", "RANGE", "RECURSIVE", "REFERENCES",
        "REGEXP", "REINDEX", "RELEASE", "RENAME", "RESTRICT", "RETURNING", "RIGHT", "ROLLBACK", "ROW", "ROWS", "SAVEPOINT",
        "SELECT", "SET", "TABLE", "TEMP", "TEMPORARY", "THEN", "TIES", "TO", "TRANSACTION", "TRIGGER", "UNBOUNDED", "UNION",
        "UNIQUE", "UPDATE", "USING", "VACUUM", "VALUES", "VIEW", "VIRTUAL", "WHEN", "WHERE", "WINDOW", "WITH", "WITHOUT",
    };

    /// <summary>
    /// <see cref="Keywords"/> に含まれるが、式の先頭で <c>(</c> が続けば関数呼出しになる名前です。
    /// 二項演算子の位置（直前が被演算子の終わり）では演算子として扱います。
    /// </summary>
    private static readonly HashSet<string> CallableKeywords = new(StringComparer.OrdinalIgnoreCase) { "MATCH", "REGEXP" };

    /// <summary>単独の語でも列名ではなく式の値になるキーワードです。索引の項目では式として扱います。</summary>
    private static readonly HashSet<string> LiteralKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "NULL", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "TRUE", "FALSE",
    };

    /// <summary>列定義で型名の終わりと列制約の始まりを示すキーワードです。</summary>
    private static readonly HashSet<string> ColumnConstraintStarts = new(StringComparer.OrdinalIgnoreCase)
    {
        "CONSTRAINT", "PRIMARY", "NOT", "NULL", "UNIQUE", "CHECK", "DEFAULT", "COLLATE", "REFERENCES", "GENERATED", "AS",
    };

    /// <summary>定義文一つを文字列規則だけで検査します。</summary>
    /// <param name="type">sqlite_master の type（table・index・view・trigger）。</param>
    /// <param name="sql">CREATE 文。</param>
    /// <returns>違反の一覧。互換なら空です。</returns>
    internal static IReadOnlyList<Sqlite367Violation> FindViolations(string type, string sql)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(sql);
        var checker = new DefinitionChecker(type, null, sql);
        checker.Check();
        return checker.Violations;
    }

    /// <summary>接続先 DB の sqlite_master の sql 非 NULL の全行と、構造 PRAGMA を検査します。</summary>
    /// <param name="connection">検査対象の接続。読取りだけを行います。</param>
    /// <returns>重複を除いた違反の一覧。互換なら空です。</returns>
    internal static IReadOnlyList<Sqlite367Violation> FindViolations(SQLiteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var violations = new List<Sqlite367Violation>();
        List<SchemaRow> rows = connection.Query<SchemaRow>("SELECT type AS Type, name AS Name, tbl_name AS TableName, sql AS Sql FROM sqlite_master;");
        foreach (SchemaRow row in rows.Where(row => row.Sql != null))
        {
            var checker = new DefinitionChecker(row.Type ?? string.Empty, row.Name, row.Sql!);
            checker.Check();
            violations.AddRange(checker.Violations);
        }
        foreach (SchemaRow index in rows.Where(row => string.Equals(row.Type, "index", StringComparison.OrdinalIgnoreCase) && row.Sql != null))
        {
            if (connection.Query<IndexXInfoRow>("PRAGMA index_xinfo(" + QuoteLiteral(index.Name) + ");").Any(column => column.cid == -2))
            {
                violations.Add(new(index.Type!, index.Name!, Sqlite367ViolationCategory.ExpressionIndex, index.Sql!, "index_xinfo cid=-2"));
            }
        }
        foreach (SchemaRow table in rows.Where(row => string.Equals(row.Type, "table", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (IndexListRow index in connection.Query<IndexListRow>("PRAGMA index_list(" + QuoteLiteral(table.Name) + ");").Where(index => index.partial == 1))
            {
                string sql = rows.FirstOrDefault(row => string.Equals(row.Name, index.name, StringComparison.OrdinalIgnoreCase))?.Sql ?? string.Empty;
                violations.Add(new("index", index.name ?? string.Empty, Sqlite367ViolationCategory.PartialIndex, sql, "index_list partial=1"));
            }
            foreach (TableXInfoRow column in connection.Query<TableXInfoRow>("PRAGMA table_xinfo(" + QuoteLiteral(table.Name) + ");").Where(column => column.hidden is 2 or 3))
            {
                violations.Add(new(table.Type!, table.Name!, Sqlite367ViolationCategory.GeneratedColumn, table.Sql ?? string.Empty, "table_xinfo hidden=" + column.hidden + " " + column.name));
            }
        }
        foreach (TableListRow table in connection.Query<TableListRow>("PRAGMA main.table_list;"))
        {
            string sql = rows.FirstOrDefault(row => string.Equals(row.Name, table.name, StringComparison.OrdinalIgnoreCase))?.Sql ?? string.Empty;
            if (table.wr == 1)
            {
                violations.Add(new("table", table.name ?? string.Empty, Sqlite367ViolationCategory.WithoutRowid, sql, "table_list wr=1"));
            }
            if (table.strict == 1)
            {
                violations.Add(new("table", table.name ?? string.Empty, Sqlite367ViolationCategory.Strict, sql, "table_list strict=1"));
            }
        }
        return [.. violations.DistinctBy(violation => (violation.Type.ToLowerInvariant(), violation.Name.ToLowerInvariant(), violation.Category, violation.Detail))];
    }

    /// <summary>接続先 DB に SQLite 3.6.7 の互換範囲を超える定義がないことを表明します。</summary>
    /// <param name="connection">本番入口の後に開いた検査用の接続。</param>
    internal static void AssertCompatible(SQLiteConnection connection)
    {
        IReadOnlyList<Sqlite367Violation> violations = FindViolations(connection);
        if (violations.Count > 0)
        {
            Assert.Fail("SQLite 3.6.7 の互換範囲を超える定義があります:" + Environment.NewLine
                + string.Join(Environment.NewLine, violations.Select(violation =>
                    violation.Type + " " + violation.Name + ": " + violation.Category + " (" + violation.Detail + ") " + violation.Sql)));
        }
    }

    private static string QuoteLiteral(string? value) => "'" + (value ?? string.Empty).Replace("'", "''") + "'";

    private sealed class SchemaRow
    {
        public string? Type { get; set; }
        public string? Name { get; set; }
        public string? TableName { get; set; }
        public string? Sql { get; set; }
    }

    private sealed class IndexXInfoRow
    {
        public int cid { get; set; }
    }

    private sealed class IndexListRow
    {
        public string? name { get; set; }
        public int partial { get; set; }
    }

    private sealed class TableXInfoRow
    {
        public string? name { get; set; }
        public int hidden { get; set; }
    }

    private sealed class TableListRow
    {
        public string? name { get; set; }
        public int wr { get; set; }
        public int strict { get; set; }
    }

    private enum TokenKind { None, Word, QuotedIdentifier, String, Blob, Number, Variable, Punct }

    private readonly record struct Token(TokenKind Kind, string Text)
    {
        internal bool IsWord(string keyword) => Kind == TokenKind.Word && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);

        internal bool IsPunct(string punct) => Kind == TokenKind.Punct && Text == punct;

        internal bool IsIdentifier => Kind is TokenKind.Word or TokenKind.QuotedIdentifier;
    }

    /// <summary>一つの定義文の字句と検査結果を保持し、種別ごとの構文と式の規則を適用します。</summary>
    private sealed class DefinitionChecker
    {
        private readonly string type;
        private readonly string sql;
        private readonly List<Token> tokens;
        private string name;

        internal DefinitionChecker(string type, string? name, string sql)
        {
            this.type = type.ToLowerInvariant();
            this.name = name ?? string.Empty;
            this.sql = sql;
            tokens = Tokenize(sql, out bool lexFailed);
            while (tokens.Count > 0 && tokens[^1].IsPunct(";"))
            {
                tokens.RemoveAt(tokens.Count - 1);
            }
            if (lexFailed)
            {
                Add(Sqlite367ViolationCategory.Unparsable, "unterminated literal");
            }
        }

        internal List<Sqlite367Violation> Violations { get; } = [];

        internal void Check()
        {
            int index = 0;
            if (!At(index).IsWord("CREATE"))
            {
                Add(Sqlite367ViolationCategory.Unparsable, "not CREATE");
                return;
            }
            index++;
            if (At(index).IsWord("TEMP") || At(index).IsWord("TEMPORARY"))
            {
                index++;
            }
            if (At(index).IsWord("VIRTUAL"))
            {
                Add(Sqlite367ViolationCategory.VirtualTable, "CREATE VIRTUAL TABLE");
                return;
            }
            bool unique = false;
            if (At(index).IsWord("UNIQUE"))
            {
                unique = true;
                index++;
            }
            string keyword = At(index).Kind == TokenKind.Word ? At(index).Text.ToUpperInvariant() : string.Empty;
            string expectedType = keyword switch
            {
                "TABLE" => "table",
                "INDEX" => "index",
                "VIEW" => "view",
                "TRIGGER" => "trigger",
                _ => string.Empty,
            };
            if (type is not ("table" or "index" or "view" or "trigger"))
            {
                Add(Sqlite367ViolationCategory.UnsupportedObjectType, type);
                return;
            }
            if (expectedType != type || (unique && expectedType != "index"))
            {
                Add(Sqlite367ViolationCategory.Unparsable, "type mismatch: " + keyword);
                return;
            }
            index++;
            if (At(index).IsWord("IF") && At(index + 1).IsWord("NOT") && At(index + 2).IsWord("EXISTS"))
            {
                index += 3;
            }
            if (!TryReadQualifiedName(ref index, out string parsedName))
            {
                Add(Sqlite367ViolationCategory.Unparsable, "missing name");
                return;
            }
            if (name.Length == 0)
            {
                name = parsedName;
            }
            switch (type)
            {
                case "table":
                    CheckTable(index);
                    break;
                case "index":
                    CheckIndex(index);
                    break;
                case "view":
                    CheckView(index);
                    break;
                default:
                    CheckTrigger(index);
                    break;
            }
        }

        private void CheckTable(int index)
        {
            if (At(index).IsWord("AS"))
            {
                CheckStatements(index + 1, tokens.Count);
                return;
            }
            if (!At(index).IsPunct("("))
            {
                Add(Sqlite367ViolationCategory.Unparsable, "missing column list");
                return;
            }
            int close = FindClose(index);
            if (close < 0)
            {
                return;
            }
            for (int tail = close + 1; tail < tokens.Count; tail++)
            {
                Token token = tokens[tail];
                if (token.IsWord("WITHOUT"))
                {
                    Add(Sqlite367ViolationCategory.WithoutRowid, "WITHOUT ROWID");
                }
                else if (token.IsWord("STRICT"))
                {
                    Add(Sqlite367ViolationCategory.Strict, "STRICT");
                }
                else if (!token.IsWord("ROWID") && !token.IsPunct(","))
                {
                    Add(Sqlite367ViolationCategory.TableOption, token.Text);
                }
            }
            foreach ((int start, int end) in SplitTopLevel(index + 1, close))
            {
                if (start >= end)
                {
                    Add(Sqlite367ViolationCategory.Unparsable, "empty table element");
                    continue;
                }
                Token first = tokens[start];
                if (first.IsWord("CONSTRAINT") || first.IsWord("PRIMARY") || first.IsWord("UNIQUE") || first.IsWord("CHECK") || first.IsWord("FOREIGN"))
                {
                    CheckTableConstraint(start, end);
                }
                else
                {
                    CheckColumnDefinition(start, end);
                }
            }
        }

        private void CheckColumnDefinition(int start, int end)
        {
            if (!(tokens[start].IsIdentifier || tokens[start].Kind == TokenKind.String))
            {
                Add(Sqlite367ViolationCategory.Unparsable, "column name: " + tokens[start].Text);
                return;
            }
            int index = start + 1;
            while (index < end && !(tokens[index].Kind == TokenKind.Word && ColumnConstraintStarts.Contains(tokens[index].Text)))
            {
                if (tokens[index].IsPunct("("))
                {
                    int close = FindClose(index, end);
                    if (close < 0)
                    {
                        return;
                    }
                    // 型の桁数（varchar(140)、DECIMAL(10,2)）は式ではなく、符号付き数値だけを許します。
                    for (int inner = index + 1; inner < close; inner++)
                    {
                        Token token = tokens[inner];
                        if (!(token.Kind == TokenKind.Number && !IsHex(token)) && !token.IsPunct(",") && !token.IsPunct("+") && !token.IsPunct("-"))
                        {
                            Add(Sqlite367ViolationCategory.Unparsable, "type size: " + token.Text);
                        }
                    }
                    index = close + 1;
                    continue;
                }
                if (!tokens[index].IsIdentifier)
                {
                    Add(Sqlite367ViolationCategory.Unparsable, "type name: " + tokens[index].Text);
                    return;
                }
                index++;
            }
            CheckColumnConstraints(index, end);
        }

        private void CheckColumnConstraints(int index, int end)
        {
            while (index < end)
            {
                Token token = tokens[index];
                if (token.IsWord("CONSTRAINT"))
                {
                    index += 2;
                }
                else if (token.IsWord("PRIMARY") && At(index + 1).IsWord("KEY"))
                {
                    index += 2;
                    if (At(index).IsWord("ASC") || At(index).IsWord("DESC"))
                    {
                        index++;
                    }
                    index = SkipConflictClause(index);
                    if (At(index).IsWord("AUTOINCREMENT"))
                    {
                        index++;
                    }
                }
                else if (token.IsWord("NOT") && At(index + 1).IsWord("NULL"))
                {
                    index = SkipConflictClause(index + 2);
                }
                else if (token.IsWord("NULL"))
                {
                    index = SkipConflictClause(index + 1);
                }
                else if (token.IsWord("UNIQUE"))
                {
                    index = SkipConflictClause(index + 1);
                }
                else if (token.IsWord("CHECK") && At(index + 1).IsPunct("("))
                {
                    int close = FindClose(index + 1, end);
                    if (close < 0)
                    {
                        return;
                    }
                    CheckExpression(index + 2, close);
                    index = close + 1;
                }
                else if (token.IsWord("DEFAULT"))
                {
                    index = CheckDefault(index + 1, end);
                }
                else if (token.IsWord("COLLATE"))
                {
                    CheckCollation(index + 1);
                    index += 2;
                }
                else if (token.IsWord("REFERENCES"))
                {
                    index = SkipForeignKeyClause(index, end);
                }
                else if (token.IsWord("GENERATED") || token.IsWord("AS"))
                {
                    Add(Sqlite367ViolationCategory.GeneratedColumn, token.Text);
                    while (index < end && !At(index).IsPunct("("))
                    {
                        index++;
                    }
                    int close = FindClose(index, end);
                    if (close < 0)
                    {
                        return;
                    }
                    CheckExpression(index + 1, close);
                    index = close + 1;
                    if (At(index).IsWord("STORED") || At(index).IsWord("VIRTUAL"))
                    {
                        index++;
                    }
                }
                else
                {
                    Add(Sqlite367ViolationCategory.Unparsable, "column constraint: " + token.Text);
                    return;
                }
            }
        }

        private int CheckDefault(int index, int end)
        {
            Token token = At(index);
            if (token.IsPunct("("))
            {
                int close = FindClose(index, end);
                if (close < 0)
                {
                    return end;
                }
                CheckExpression(index + 1, close);
                return close + 1;
            }
            if ((token.IsPunct("+") || token.IsPunct("-")) && At(index + 1).Kind == TokenKind.Number)
            {
                CheckNumber(At(index + 1));
                return index + 2;
            }
            if (token.Kind == TokenKind.Number)
            {
                CheckNumber(token);
                return index + 1;
            }
            if (token.Kind is TokenKind.String or TokenKind.Blob
                || token.IsWord("NULL") || token.IsWord("CURRENT_DATE") || token.IsWord("CURRENT_TIME") || token.IsWord("CURRENT_TIMESTAMP"))
            {
                return index + 1;
            }
            Add(Sqlite367ViolationCategory.UnsupportedDefault, token.Text);
            return index + 1;
        }

        private void CheckTableConstraint(int index, int end)
        {
            if (At(index).IsWord("CONSTRAINT"))
            {
                index += 2;
            }
            Token token = At(index);
            if ((token.IsWord("PRIMARY") && At(index + 1).IsWord("KEY")) || token.IsWord("UNIQUE"))
            {
                index += token.IsWord("PRIMARY") ? 2 : 1;
                if (!At(index).IsPunct("("))
                {
                    Add(Sqlite367ViolationCategory.Unparsable, "constraint columns");
                    return;
                }
                int close = FindClose(index, end);
                if (close < 0)
                {
                    return;
                }
                CheckIndexedColumns(index + 1, close);
                index = SkipConflictClause(close + 1);
            }
            else if (token.IsWord("CHECK") && At(index + 1).IsPunct("("))
            {
                int close = FindClose(index + 1, end);
                if (close < 0)
                {
                    return;
                }
                CheckExpression(index + 2, close);
                index = close + 1;
            }
            else if (token.IsWord("FOREIGN") && At(index + 1).IsWord("KEY") && At(index + 2).IsPunct("("))
            {
                int close = FindClose(index + 2, end);
                if (close < 0)
                {
                    return;
                }
                CheckNameList(index + 3, close);
                index = SkipForeignKeyClause(close + 1, end);
            }
            else
            {
                Add(Sqlite367ViolationCategory.Unparsable, "table constraint: " + token.Text);
                return;
            }
            if (index < end)
            {
                Add(Sqlite367ViolationCategory.Unparsable, "after table constraint: " + At(index).Text);
            }
        }

        private int SkipConflictClause(int index)
        {
            if (!(At(index).IsWord("ON") && At(index + 1).IsWord("CONFLICT")))
            {
                return index;
            }
            Token resolution = At(index + 2);
            if (resolution.Kind == TokenKind.Word && ConflictResolutions.Contains(resolution.Text))
            {
                return index + 3;
            }
            Add(Sqlite367ViolationCategory.Upsert, "ON CONFLICT " + resolution.Text);
            return index + 3;
        }

        private int SkipForeignKeyClause(int index, int end)
        {
            if (!At(index).IsWord("REFERENCES"))
            {
                Add(Sqlite367ViolationCategory.Unparsable, "missing REFERENCES");
                return end;
            }
            index++;
            if (!TryReadQualifiedName(ref index, out _))
            {
                Add(Sqlite367ViolationCategory.Unparsable, "REFERENCES table");
                return end;
            }
            if (At(index).IsPunct("("))
            {
                int close = FindClose(index, end);
                if (close < 0)
                {
                    return end;
                }
                CheckNameList(index + 1, close);
                index = close + 1;
            }
            while (index < end)
            {
                if (At(index).IsWord("ON") && (At(index + 1).IsWord("DELETE") || At(index + 1).IsWord("UPDATE")))
                {
                    index += 2;
                    if (At(index).IsWord("SET") && (At(index + 1).IsWord("NULL") || At(index + 1).IsWord("DEFAULT")))
                    {
                        index += 2;
                    }
                    else if (At(index).IsWord("NO") && At(index + 1).IsWord("ACTION"))
                    {
                        index += 2;
                    }
                    else if (At(index).IsWord("CASCADE") || At(index).IsWord("RESTRICT"))
                    {
                        index++;
                    }
                    else
                    {
                        Add(Sqlite367ViolationCategory.Unparsable, "foreign key action: " + At(index).Text);
                        return end;
                    }
                }
                else if (At(index).IsWord("MATCH"))
                {
                    index += 2;
                }
                else if (At(index).IsWord("DEFERRABLE") || (At(index).IsWord("NOT") && At(index + 1).IsWord("DEFERRABLE")))
                {
                    index += At(index).IsWord("NOT") ? 2 : 1;
                    if (At(index).IsWord("INITIALLY"))
                    {
                        index += 2;
                    }
                }
                else
                {
                    break;
                }
            }
            return index;
        }

        private void CheckIndex(int index)
        {
            if (!At(index).IsWord("ON"))
            {
                Add(Sqlite367ViolationCategory.Unparsable, "missing ON");
                return;
            }
            index++;
            if (!TryReadQualifiedName(ref index, out _) || !At(index).IsPunct("("))
            {
                Add(Sqlite367ViolationCategory.Unparsable, "index target");
                return;
            }
            int close = FindClose(index);
            if (close < 0)
            {
                return;
            }
            CheckIndexedColumns(index + 1, close);
            if (close + 1 < tokens.Count)
            {
                if (tokens[close + 1].IsWord("WHERE"))
                {
                    Add(Sqlite367ViolationCategory.PartialIndex, "WHERE");
                    CheckExpression(close + 2, tokens.Count);
                }
                else
                {
                    Add(Sqlite367ViolationCategory.Unparsable, "after index columns: " + tokens[close + 1].Text);
                }
            }
        }

        /// <summary>各項目が <c>識別子 [COLLATE 名] [ASC|DESC]</c> だけであることを確認し、式の項目は式規則でも検査します。</summary>
        private void CheckIndexedColumns(int start, int end)
        {
            foreach ((int itemStart, int itemEnd) in SplitTopLevel(start, end))
            {
                int index = itemStart;
                // 索引の項目の先頭の一語は列名か式なので、KEY・FIRST・ROWS などキーワード集合にある未引用の列名も列として受理します。
                bool simple = index < itemEnd && tokens[index].IsIdentifier && !(tokens[index].Kind == TokenKind.Word && LiteralKeywords.Contains(tokens[index].Text));
                index++;
                if (simple && index < itemEnd && tokens[index].IsWord("COLLATE"))
                {
                    if (index + 1 < itemEnd)
                    {
                        CheckCollation(index + 1);
                    }
                    index += 2;
                }
                if (simple && index < itemEnd && (tokens[index].IsWord("ASC") || tokens[index].IsWord("DESC")))
                {
                    index++;
                }
                if (!simple || index != itemEnd)
                {
                    Add(Sqlite367ViolationCategory.ExpressionIndex, Text(itemStart, itemEnd));
                    CheckExpression(itemStart, itemEnd);
                }
            }
        }

        private void CheckView(int index)
        {
            if (At(index).IsPunct("("))
            {
                Add(Sqlite367ViolationCategory.ViewColumnList, "column list");
                int close = FindClose(index);
                if (close < 0)
                {
                    return;
                }
                index = close + 1;
            }
            if (!At(index).IsWord("AS"))
            {
                Add(Sqlite367ViolationCategory.Unparsable, "missing AS");
                return;
            }
            CheckStatements(index + 1, tokens.Count);
        }

        private void CheckTrigger(int index)
        {
            if (At(index).IsWord("BEFORE") || At(index).IsWord("AFTER"))
            {
                index++;
            }
            else if (At(index).IsWord("INSTEAD") && At(index + 1).IsWord("OF"))
            {
                index += 2;
            }
            if (At(index).IsWord("DELETE") || At(index).IsWord("INSERT"))
            {
                index++;
            }
            else if (At(index).IsWord("UPDATE"))
            {
                index++;
                if (At(index).IsWord("OF"))
                {
                    index++;
                    while (At(index).IsIdentifier && !At(index).IsWord("ON"))
                    {
                        index++;
                        if (!At(index).IsPunct(","))
                        {
                            break;
                        }
                        index++;
                    }
                }
            }
            else
            {
                Add(Sqlite367ViolationCategory.Unparsable, "trigger event: " + At(index).Text);
                return;
            }
            if (!At(index).IsWord("ON"))
            {
                Add(Sqlite367ViolationCategory.Unparsable, "trigger ON");
                return;
            }
            index++;
            if (!TryReadQualifiedName(ref index, out _))
            {
                Add(Sqlite367ViolationCategory.Unparsable, "trigger table");
                return;
            }
            if (At(index).IsWord("FOR") && At(index + 1).IsWord("EACH") && At(index + 2).IsWord("ROW"))
            {
                index += 3;
            }
            int begin = -1;
            for (int search = index; search < tokens.Count; search++)
            {
                if (tokens[search].IsWord("BEGIN"))
                {
                    begin = search;
                    break;
                }
            }
            if (begin < 0 || tokens.Count == 0 || !tokens[^1].IsWord("END"))
            {
                Add(Sqlite367ViolationCategory.Unparsable, "trigger body");
                return;
            }
            if (At(index).IsWord("WHEN"))
            {
                CheckExpression(index + 1, begin);
            }
            else if (index != begin)
            {
                Add(Sqlite367ViolationCategory.Unparsable, "trigger header: " + At(index).Text);
            }
            CheckStatements(begin + 1, tokens.Count - 1);
        }

        /// <summary>トリガー本体・ビューの文を最上位の <c>;</c> で分け、文単位の規則と式規則を適用します。</summary>
        private void CheckStatements(int start, int end)
        {
            int statementStart = start;
            int depth = 0;
            for (int index = start; index <= end; index++)
            {
                if (index < end && tokens[index].IsPunct("("))
                {
                    depth++;
                    continue;
                }
                if (index < end && tokens[index].IsPunct(")"))
                {
                    depth--;
                    continue;
                }
                if (index == end || (depth == 0 && tokens[index].IsPunct(";")))
                {
                    if (statementStart < index)
                    {
                        CheckStatement(statementStart, index);
                    }
                    statementStart = index + 1;
                }
            }
        }

        private void CheckStatement(int start, int end)
        {
            if (tokens[start].IsWord("UPDATE"))
            {
                int depth = 0;
                for (int index = start; index < end; index++)
                {
                    if (tokens[index].IsPunct("("))
                    {
                        depth++;
                    }
                    else if (tokens[index].IsPunct(")"))
                    {
                        depth--;
                    }
                    else if (depth == 0 && tokens[index].IsWord("FROM"))
                    {
                        Add(Sqlite367ViolationCategory.UpdateFrom, "UPDATE ... FROM");
                    }
                }
            }
            CheckExpression(start, end);
        }

        /// <summary>式・文の字句範囲へ、関数・キーワード・行値などの規則を再帰的に適用します。</summary>
        private void CheckExpression(int start, int end)
        {
            int index = start;
            while (index < end)
            {
                Token token = tokens[index];
                Token next = index + 1 < end ? tokens[index + 1] : default;
                if (token.Kind == TokenKind.Number)
                {
                    CheckNumber(token);
                    index++;
                    continue;
                }
                if (token.IsPunct("->") || token.IsPunct("->>"))
                {
                    Add(Sqlite367ViolationCategory.JsonOperator, token.Text);
                    index++;
                    continue;
                }
                if (token.Kind == TokenKind.Word)
                {
                    string upper = token.Text.ToUpperInvariant();
                    switch (upper)
                    {
                        case "WITH":
                            Add(Sqlite367ViolationCategory.CommonTableExpression, token.Text);
                            break;
                        case "RETURNING":
                            Add(Sqlite367ViolationCategory.Returning, token.Text);
                            break;
                        case "OVER":
                        case "WINDOW":
                            Add(Sqlite367ViolationCategory.WindowFunction, token.Text);
                            break;
                        case "FILTER" when next.IsPunct("("):
                            Add(Sqlite367ViolationCategory.WindowFunction, token.Text);
                            break;
                        case "DO":
                            Add(Sqlite367ViolationCategory.Upsert, token.Text);
                            break;
                        case "NULLS":
                            Add(Sqlite367ViolationCategory.NullsOrdering, token.Text);
                            break;
                        case "RIGHT":
                        case "FULL":
                            if (next.IsWord("JOIN") || (next.IsWord("OUTER") && index + 2 < end && tokens[index + 2].IsWord("JOIN")))
                            {
                                Add(Sqlite367ViolationCategory.RightOrFullJoin, token.Text);
                            }
                            break;
                        case "ON" when next.IsWord("CONFLICT"):
                            {
                                Token afterConflict = index + 2 < end ? tokens[index + 2] : default;
                                if (afterConflict.IsPunct("(") || afterConflict.IsWord("DO"))
                                {
                                    Add(Sqlite367ViolationCategory.Upsert, "ON CONFLICT " + afterConflict.Text);
                                }
                                break;
                            }
                        case "IS":
                            {
                                Token operand = next.IsWord("NOT") ? (index + 2 < end ? tokens[index + 2] : default) : next;
                                if (!operand.IsWord("NULL"))
                                {
                                    Add(Sqlite367ViolationCategory.IsComparison, "IS " + operand.Text);
                                }
                                break;
                            }
                        case "COLLATE":
                            if (index + 1 < end)
                            {
                                CheckCollation(index + 1);
                            }
                            index += 2;
                            continue;
                        case "SET" when next.IsPunct("("):
                            Add(Sqlite367ViolationCategory.RowValue, "SET (");
                            break;
                        case "CAST" when next.IsPunct("("):
                            index = CheckCast(index + 1, end);
                            continue;
                        case "VALUES" when next.IsPunct("("):
                            index = CheckValues(index + 1, end);
                            continue;
                        case "FROM":
                        case "JOIN":
                            {
                                int nameIndex = index + 1;
                                if (nameIndex < end && tokens[nameIndex].IsIdentifier && !(tokens[nameIndex].Kind == TokenKind.Word && Keywords.Contains(tokens[nameIndex].Text)))
                                {
                                    int afterName = nameIndex;
                                    TryReadQualifiedName(ref afterName, out string tableName);
                                    if (afterName < end && tokens[afterName].IsPunct("("))
                                    {
                                        Add(Sqlite367ViolationCategory.TableValuedFunction, tableName);
                                        int close = FindClose(afterName, end);
                                        if (close < 0)
                                        {
                                            return;
                                        }
                                        CheckExpression(afterName + 1, close);
                                        index = close + 1;
                                        continue;
                                    }
                                    index = afterName;
                                    continue;
                                }
                                break;
                            }
                        case "INTO":
                            {
                                int columnsIndex = index + 1;
                                if (TryReadQualifiedName(ref columnsIndex, out _) && columnsIndex < end && tokens[columnsIndex].IsPunct("("))
                                {
                                    int close = FindClose(columnsIndex, end);
                                    if (close < 0)
                                    {
                                        return;
                                    }
                                    CheckNameList(columnsIndex + 1, close);
                                    index = close + 1;
                                    continue;
                                }
                                index = columnsIndex;
                                continue;
                            }
                    }
                }
                if (token.IsIdentifier && next.IsPunct("(") && !IsAfterDot(index)
                    && (!(token.Kind == TokenKind.Word && Keywords.Contains(token.Text))
                        || (CallableKeywords.Contains(token.Text) && !PreviousEndsOperand(index))))
                {
                    index = CheckFunctionCall(index, end);
                    continue;
                }
                if (token.IsPunct("("))
                {
                    index = CheckGroup(index, end);
                    continue;
                }
                index++;
            }
        }

        private int CheckFunctionCall(int nameIndex, int end)
        {
            Token function = tokens[nameIndex];
            if (!AllowedFunctions.Contains(function.Text))
            {
                Add(Sqlite367ViolationCategory.UnsupportedFunction, function.Text.ToLowerInvariant());
            }
            int open = nameIndex + 1;
            int close = FindClose(open, end);
            if (close < 0)
            {
                return end;
            }
            int depth = 0;
            for (int index = open + 1; index < close; index++)
            {
                if (tokens[index].IsPunct("("))
                {
                    depth++;
                }
                else if (tokens[index].IsPunct(")"))
                {
                    depth--;
                }
                else if (depth == 0 && tokens[index].IsWord("ORDER"))
                {
                    Add(Sqlite367ViolationCategory.AggregateOrderBy, function.Text);
                }
            }
            CheckExpression(open + 1, close);
            return close + 1;
        }

        private int CheckCast(int open, int end)
        {
            int close = FindClose(open, end);
            if (close < 0)
            {
                return end;
            }
            int depth = 0;
            int asIndex = close;
            for (int index = open + 1; index < close; index++)
            {
                if (tokens[index].IsPunct("("))
                {
                    depth++;
                }
                else if (tokens[index].IsPunct(")"))
                {
                    depth--;
                }
                else if (depth == 0 && tokens[index].IsWord("AS"))
                {
                    asIndex = index;
                    break;
                }
            }
            CheckExpression(open + 1, asIndex);
            return close + 1;
        }

        private int CheckValues(int open, int end)
        {
            int close = FindClose(open, end);
            if (close < 0)
            {
                return end;
            }
            CheckExpression(open + 1, close);
            if (close + 2 < end && tokens[close + 1].IsPunct(",") && tokens[close + 2].IsPunct("("))
            {
                Add(Sqlite367ViolationCategory.MultiRowValues, "VALUES (...),(");
            }
            return close + 1;
        }

        /// <summary>関数呼出し以外の括弧です。副問合せ、IN・USING・RAISE・EXISTS の後以外で最上位にカンマがあれば行値とします。</summary>
        private int CheckGroup(int open, int end)
        {
            int close = FindClose(open, end);
            if (close < 0)
            {
                return end;
            }
            Token previous = open > 0 ? tokens[open - 1] : default;
            Token first = open + 1 < close ? tokens[open + 1] : default;
            if (first.IsWord("SELECT") || first.IsWord("WITH") || first.IsWord("VALUES"))
            {
                CheckStatements(open + 1, close);
                return close + 1;
            }
            bool list = previous.IsWord("IN") || previous.IsWord("USING") || previous.IsWord("RAISE") || previous.IsWord("EXISTS");
            if (!list && SplitTopLevel(open + 1, close).Count > 1)
            {
                Add(Sqlite367ViolationCategory.RowValue, Text(open, close + 1));
            }
            if (previous.IsWord("USING"))
            {
                CheckNameList(open + 1, close);
            }
            else
            {
                CheckExpression(open + 1, close);
            }
            return close + 1;
        }

        private void CheckNameList(int start, int end)
        {
            foreach ((int itemStart, int itemEnd) in SplitTopLevel(start, end))
            {
                if (itemEnd - itemStart != 1 || !tokens[itemStart].IsIdentifier)
                {
                    Add(Sqlite367ViolationCategory.RowValue, Text(itemStart, itemEnd));
                    CheckExpression(itemStart, itemEnd);
                }
            }
        }

        private void CheckCollation(int index)
        {
            Token collation = At(index);
            if (!collation.IsIdentifier || !AllowedCollations.Contains(collation.Text))
            {
                Add(Sqlite367ViolationCategory.UnsupportedCollation, collation.Text);
            }
        }

        private void CheckNumber(Token token)
        {
            if (IsHex(token))
            {
                Add(Sqlite367ViolationCategory.HexLiteral, token.Text);
            }
        }

        private static bool IsHex(Token token)
            => token.Kind == TokenKind.Number && token.Text.Length > 1 && token.Text[0] == '0' && (token.Text[1] == 'x' || token.Text[1] == 'X');

        private bool IsAfterDot(int index) => index > 0 && tokens[index - 1].IsPunct(".");

        /// <summary>直前の字句が被演算子の終わりで、<paramref name="index"/> の語が二項演算子の位置にあるかを返します。</summary>
        private bool PreviousEndsOperand(int index)
        {
            if (index == 0)
            {
                return false;
            }
            Token previous = tokens[index - 1];
            return previous.Kind switch
            {
                TokenKind.String or TokenKind.Blob or TokenKind.Number or TokenKind.Variable or TokenKind.QuotedIdentifier => true,
                TokenKind.Punct => previous.IsPunct(")"),
                TokenKind.Word => !Keywords.Contains(previous.Text) || LiteralKeywords.Contains(previous.Text) || previous.IsWord("END"),
                _ => false,
            };
        }

        private bool TryReadQualifiedName(ref int index, out string qualifiedName)
        {
            qualifiedName = string.Empty;
            if (!(At(index).IsIdentifier || At(index).Kind == TokenKind.String))
            {
                return false;
            }
            qualifiedName = At(index).Text;
            index++;
            if (At(index).IsPunct(".") && (At(index + 1).IsIdentifier || At(index + 1).Kind == TokenKind.String))
            {
                qualifiedName = At(index + 1).Text;
                index += 2;
            }
            return true;
        }

        private List<(int Start, int End)> SplitTopLevel(int start, int end)
        {
            var parts = new List<(int, int)>();
            int depth = 0;
            int partStart = start;
            for (int index = start; index < end; index++)
            {
                if (tokens[index].IsPunct("("))
                {
                    depth++;
                }
                else if (tokens[index].IsPunct(")"))
                {
                    depth--;
                }
                else if (depth == 0 && tokens[index].IsPunct(","))
                {
                    parts.Add((partStart, index));
                    partStart = index + 1;
                }
            }
            parts.Add((partStart, end));
            return parts;
        }

        private int FindClose(int open, int limit = -1)
        {
            int end = limit < 0 ? tokens.Count : limit;
            int depth = 0;
            for (int index = open; index < end; index++)
            {
                if (tokens[index].IsPunct("("))
                {
                    depth++;
                }
                else if (tokens[index].IsPunct(")"))
                {
                    depth--;
                    if (depth == 0)
                    {
                        return index;
                    }
                }
            }
            Add(Sqlite367ViolationCategory.Unparsable, "unbalanced parentheses");
            return -1;
        }

        private Token At(int index) => index >= 0 && index < tokens.Count ? tokens[index] : default;

        private string Text(int start, int end)
            => string.Join(" ", tokens.Skip(start).Take(Math.Max(0, end - start)).Select(token => token.Text));

        private void Add(Sqlite367ViolationCategory category, string detail) => Violations.Add(new(type, name, category, sql, detail));
    }

    /// <summary>文字列・引用符付き識別子・BLOB・コメントを字句として処理します。コメントは捨て、識別子の引用符は外します。</summary>
    private static List<Token> Tokenize(string sql, out bool failed)
    {
        failed = false;
        var tokens = new List<Token>();
        int index = 0;
        while (index < sql.Length)
        {
            char c = sql[index];
            if (char.IsWhiteSpace(c))
            {
                index++;
                continue;
            }
            if (c == '-' && Peek(sql, index + 1) == '-')
            {
                while (index < sql.Length && sql[index] != '\n')
                {
                    index++;
                }
                continue;
            }
            if (c == '/' && Peek(sql, index + 1) == '*')
            {
                int close = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = close < 0 ? sql.Length : close + 2;
                continue;
            }
            if ((c == 'x' || c == 'X') && Peek(sql, index + 1) == '\'')
            {
                index++;
                tokens.Add(new(TokenKind.Blob, "X" + ReadQuoted(sql, ref index, '\'', '\'', ref failed)));
                continue;
            }
            if (c == '\'')
            {
                tokens.Add(new(TokenKind.String, ReadQuoted(sql, ref index, '\'', '\'', ref failed)));
                continue;
            }
            if (c == '"' || c == '`')
            {
                tokens.Add(new(TokenKind.QuotedIdentifier, Unquote(ReadQuoted(sql, ref index, c, c, ref failed))));
                continue;
            }
            if (c == '[')
            {
                int close = sql.IndexOf(']', index + 1);
                if (close < 0)
                {
                    failed = true;
                    close = sql.Length - 1;
                }
                tokens.Add(new(TokenKind.QuotedIdentifier, sql[(index + 1)..close]));
                index = close + 1;
                continue;
            }
            if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(Peek(sql, index + 1))))
            {
                int start = index;
                bool hex = c == '0' && (Peek(sql, index + 1) == 'x' || Peek(sql, index + 1) == 'X');
                index += hex ? 2 : 0;
                while (index < sql.Length)
                {
                    char current = sql[index];
                    bool exponentSign = !hex && (current == '+' || current == '-') && index > start && (sql[index - 1] == 'e' || sql[index - 1] == 'E');
                    if (char.IsAsciiLetterOrDigit(current) || current == '.' || current == '_' || exponentSign)
                    {
                        index++;
                        continue;
                    }
                    break;
                }
                tokens.Add(new(TokenKind.Number, sql[start..index]));
                continue;
            }
            if (IsIdentifierStart(c))
            {
                int start = index;
                while (index < sql.Length && IsIdentifierPart(sql[index]))
                {
                    index++;
                }
                tokens.Add(new(TokenKind.Word, sql[start..index]));
                continue;
            }
            if (c == '?' || c == ':' || c == '@' || c == '$')
            {
                int start = index++;
                while (index < sql.Length && IsIdentifierPart(sql[index]))
                {
                    index++;
                }
                tokens.Add(new(TokenKind.Variable, sql[start..index]));
                continue;
            }
            string punct = sql.AsSpan(index).StartsWith("->>") ? "->>"
                : index + 1 < sql.Length && IsTwoCharOperator(sql.Substring(index, 2)) ? sql.Substring(index, 2)
                : c.ToString();
            tokens.Add(new(TokenKind.Punct, punct));
            index += punct.Length;
        }
        return tokens;
    }

    private static bool IsTwoCharOperator(string text) => text is "->" or "||" or "<=" or ">=" or "==" or "!=" or "<>" or "<<" or ">>";

    private static bool IsIdentifierStart(char c) => char.IsAsciiLetter(c) || c == '_' || c >= 0x80;

    private static bool IsIdentifierPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '$' || c >= 0x80;

    private static char Peek(string sql, int index) => index < sql.Length ? sql[index] : '\0';

    private static string ReadQuoted(string sql, ref int index, char open, char close, ref bool failed)
    {
        var builder = new StringBuilder();
        builder.Append(sql[index]);
        index++;
        while (index < sql.Length)
        {
            char c = sql[index];
            builder.Append(c);
            index++;
            if (c == close)
            {
                if (Peek(sql, index) == close)
                {
                    builder.Append(close);
                    index++;
                    continue;
                }
                return builder.ToString();
            }
        }
        failed = true;
        return builder.ToString();
    }

    private static string Unquote(string quoted)
    {
        if (quoted.Length < 2)
        {
            return quoted;
        }
        char quote = quoted[0];
        return quoted[1..^1].Replace(new string(quote, 2), quote.ToString());
    }
}
