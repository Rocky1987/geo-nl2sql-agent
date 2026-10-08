using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace GeoNl2Sql.Core.Guardrails;

/// <summary>驗證結果。</summary>
/// <param name="IsValid">是否放行。</param>
/// <param name="Reason">拒絕原因（固定的中文短句，可回饋給模型修正；放行時為 null）。</param>
public sealed record SqlValidationResult(bool IsValid, string? Reason)
{
    /// <summary>放行的結果。</summary>
    public static readonly SqlValidationResult Ok = new(true, null);

    /// <summary>建立拒絕的結果。</summary>
    /// <param name="reason">固定的拒絕原因。</param>
    public static SqlValidationResult Reject(string reason) => new(false, reason);
}

/// <summary>
/// 第一道防線：以 T-SQL 語法樹（ScriptDom）做白名單驗證，只放行「單一 SELECT、只讀白名單資料表、只用白名單函式」的查詢。
/// 規則 V1–V6 見 docs/m2-implementation-plan.md §4.1。第二道防線是唯讀 login，兩者獨立。
/// 用法：<c>new SqlValidator(tables).Validate(sql)</c>；實例無狀態，可共用。
/// </summary>
public sealed class SqlValidator
{
    /// <summary>
    /// 函式白名單（不分大小寫）。來源：30 題標準 SQL 實際用到的，加上常見且無副作用的聚合、日期、字串、數學、視窗函式
    /// 與 geography 方法。增減都要寫理由。刻意不列入：CHAR（題目用不到，且是 CHAR() 拼接攻擊的素材）。
    /// </summary>
    private static readonly HashSet<string> AllowedFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        // 聚合
        "COUNT", "SUM", "AVG", "MIN", "MAX",
        // 數學
        "ROUND", "ABS", "CEILING", "FLOOR", "POWER", "SQRT",
        // 日期
        "YEAR", "MONTH", "DAY", "DATEADD", "DATEDIFF", "DATEPART", "DATENAME", "EOMONTH", "GETDATE",
        // 字串
        "LEN", "LOWER", "UPPER", "LTRIM", "RTRIM", "TRIM", "SUBSTRING", "REPLACE", "CONCAT", "LEFT", "RIGHT", "CHARINDEX",
        // 空值與條件
        "COALESCE", "ISNULL", "NULLIF", "IIF",
        // 視窗
        "ROW_NUMBER", "RANK", "DENSE_RANK", "NTILE", "LAG", "LEAD",
        // geography 方法與靜態方法（geography::Point）
        "STDistance", "STIntersects", "STArea", "STContains", "STBuffer", "STAsText", "STLength", "Point",
    };

    /// <summary>可作為查詢來源的資料表參考型別；其餘（OPENROWSET、資料表值函式、變數、PIVOT 等）一律拒絕（V5）。</summary>
    private static readonly Type[] AllowedTableReferenceTypes =
    [
        typeof(NamedTableReference), typeof(QueryDerivedTable), typeof(QualifiedJoin),
        typeof(UnqualifiedJoin), typeof(JoinParenthesisTableReference),
    ];

    /// <summary>允許查詢的資料表名稱（不含結構描述，不分大小寫）。</summary>
    private readonly IReadOnlySet<string> _allowedTables;

    /// <summary>建立驗證器。</summary>
    /// <param name="allowedTables">允許查詢的資料表名稱（不含結構描述，例如 "Customer"）；比對不分大小寫。</param>
    public SqlValidator(IReadOnlySet<string> allowedTables)
    {
        _allowedTables = new HashSet<string>(allowedTables, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>驗證一段 SQL。</summary>
    /// <param name="sql">模型產生的 SQL。</param>
    /// <returns>放行，或附固定原因的拒絕。</returns>
    public SqlValidationResult Validate(string sql)
    {
        // V1：解析，任何錯誤即拒絕
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        TSqlFragment fragment;
        IList<ParseError> errors;
        using (var reader = new StringReader(sql))
            fragment = parser.Parse(reader, out errors);
        if (errors.Count > 0 || fragment is not TSqlScript script)
            return SqlValidationResult.Reject("SQL 語法無法解析");

        // V2：恰好 1 個批次、1 個陳述式，且必須是 SELECT
        if (script.Batches.Count != 1 || script.Batches[0].Statements.Count != 1
            || script.Batches[0].Statements[0] is not SelectStatement select)
            return SqlValidationResult.Reject("只允許單一 SELECT 語句");

        // V3：不得 SELECT ... INTO
        if (select.Into is not null)
            return SqlValidationResult.Reject("不允許 SELECT INTO");

        var walker = new Walker();
        select.Accept(walker);
        if (walker.Reason is not null)
            return SqlValidationResult.Reject(walker.Reason);

        // V4：資料表只能是白名單內的資料表，或本陳述式定義的 CTE（CTE 名稱要等走完整棵樹才收集齊）
        foreach (var table in walker.NamedTables)
        {
            var name = table.SchemaObject;
            if (name.ServerIdentifier is not null || name.DatabaseIdentifier is not null)
                return SqlValidationResult.Reject("不允許的資料表或來源");

            var baseName = name.BaseIdentifier.Value;
            var isCte = name.SchemaIdentifier is null && walker.CteNames.Contains(baseName);
            var isTable = (name.SchemaIdentifier is null
                           || name.SchemaIdentifier.Value.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                          && _allowedTables.Contains(baseName);
            if (!isCte && !isTable)
                return SqlValidationResult.Reject("不允許的資料表或來源");
        }

        return SqlValidationResult.Ok;
    }

    /// <summary>走訪整棵語法樹，檢查 V5、V6，並收集 V4 需要的資料表參考與 CTE 名稱。只記錄第一個違規。</summary>
    private sealed class Walker : TSqlFragmentVisitor
    {
        /// <summary>第一個違規的原因；null 表示目前沒有違規。</summary>
        public string? Reason { get; private set; }

        /// <summary>查詢中出現的具名資料表參考（交給呼叫端比對白名單）。</summary>
        public List<NamedTableReference> NamedTables { get; } = [];

        /// <summary>本陳述式定義的 CTE 名稱。</summary>
        public HashSet<string> CteNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>每個節點都會先經過這裡：檢查資料表參考型別（V5），並收集具名資料表與 CTE 名稱。</summary>
        /// <param name="node">目前走訪到的節點。</param>
        public override void Visit(TSqlFragment node)
        {
            switch (node)
            {
                case TableReference tr when !AllowedTableReferenceTypes.Contains(tr.GetType()):
                    Reason ??= "不允許的資料表或來源";
                    break;
                case NamedTableReference named:
                    NamedTables.Add(named);
                    break;
                case CommonTableExpression cte:
                    CteNames.Add(cte.ExpressionName.Value);
                    break;
            }
        }

        /// <summary>V6：函式呼叫必須在白名單內。</summary>
        /// <param name="node">函式呼叫節點。</param>
        public override void Visit(FunctionCall node)
        {
            if (!AllowedFunctions.Contains(node.FunctionName.Value))
                Reason ??= "不允許的函式";
        }
    }
}
