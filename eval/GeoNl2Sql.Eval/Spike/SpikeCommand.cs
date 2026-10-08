using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using GeoNl2Sql.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace GeoNl2Sql.Eval.Spike;

/// <summary>
/// M1 的 <c>spike</c> 子命令（丟棄式腳本，見 docs/m1-implementation-plan.md §5）：
/// 逐題請模型生成 SQL，以 spike_reader 身分執行，再用 execution accuracy 與標準 SQL 的結果集比對，
/// 每輪寫一份 JSON 到 eval/GeoNl2Sql.Eval/Results/。直接呼叫 <see cref="IChatClient"/>，不用 Agent、不用工具。
/// 注意：前置檢查與 spike_reader 都不是安全邊界，完整防護屬 M2。
/// </summary>
public static class SpikeCommand
{
    /// <summary>一題的標準答案，對應 questions.json 的一個物件。</summary>
    /// <param name="Id">題號，例如 J03。</param>
    /// <param name="Category">類別：single、join、aggregate、spatial。</param>
    /// <param name="Question">題目文字（使用者訊息）。</param>
    /// <param name="GoldSql">標準 SQL。</param>
    /// <param name="Ordered">true 表示列順序是題意的一部分，比對時須逐列同序。</param>
    private sealed record Gold(string Id, string Category, string Question, string GoldSql, bool Ordered);

    /// <summary>單次生成的結果。</summary>
    /// <param name="Response">模型原始回應文字。</param>
    /// <param name="Sql">抽出的 SQL；抽不到為 null。</param>
    /// <param name="Failure">null 表示答對；否則為 no_sql、precheck、exec_error、wrong_result 之一。</param>
    /// <param name="Error">錯誤訊息（前置檢查原因或 SQL Server 錯誤）。</param>
    /// <param name="Ms">模型呼叫耗時（毫秒）。</param>
    /// <param name="InputTokens">輸入 Token 數（供應商有回報時）。</param>
    /// <param name="OutputTokens">輸出 Token 數（供應商有回報時）。</param>
    private sealed record Attempt(string Response, string? Sql, string? Failure, string? Error, long Ms, long? InputTokens, long? OutputTokens);

    /// <summary>JSON 輸出用的命名規則（camelCase、縮排、中文不跳脫）。</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 執行 spike。用法：<c>spike [plain|described] [--limit N] [--runs N] [--provider P] [--model M] [--fake 文字]</c>。
    /// </summary>
    /// <param name="config">已載入的設定；讀取 <c>Model</c> 區段與 <c>ConnectionStrings:Demo</c>。</param>
    /// <param name="args">子命令之後的參數。第一個非選項參數是 schema 版本（預設 described）；
    /// <c>--limit</c> 只跑前 N 題；<c>--runs</c> 重複輪數（預設 1）；<c>--provider</c>／<c>--model</c> 覆寫模型設定；
    /// <c>--fake</c> 不呼叫模型，改用固定回應（其中 <c>{gold}</c> 會換成該題標準 SQL），用來驗證比對器與前置檢查。</param>
    /// <exception cref="InvalidOperationException">缺少連線字串，或選 Anthropic 但未設定金鑰。</exception>
    public static async Task RunAsync(IConfiguration config, string[] args)
    {
        string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        var schemaVersion = args.FirstOrDefault(a => a is "plain" or "described") ?? "described";
        var fake = Opt("--fake");
        var runs = int.Parse(Opt("--runs") ?? "1");
        var demo = config.GetConnectionString("Demo") ?? throw new InvalidOperationException("找不到設定 ConnectionStrings:Demo。");
        var options = config.GetSection(ModelOptions.SectionName).Get<ModelOptions>() ?? new ModelOptions();
        if (Opt("--provider") is { } p) options.Provider = Enum.Parse<ModelProvider>(p, true);
        if (Opt("--model") is { } m) options.ModelId = m;

        var questionsPath = Path.Combine(AppContext.BaseDirectory, "Questions", "questions.json");
        var questionsBytes = await File.ReadAllBytesAsync(questionsPath);
        var questions = JsonSerializer.Deserialize<List<Gold>>(questionsBytes, Json)!
            .Take(int.Parse(Opt("--limit") ?? "30")).ToList();
        var system = BuildSystemPrompt(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", "schema-description.md")), schemaVersion);
        using var client = fake is null ? ChatClientFactory.Create(options) : null;
        // 輸出目錄是專案原始碼下的 Results/（bin/<組態>/<TFM>/ 往上三層），不是執行時的工作目錄。
        var resultsDir = Directory.CreateDirectory(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Results"))).FullName;
        var modelLabel = fake is null ? $"{options.Provider}/{options.ModelId}" : "fake";
        Console.WriteLine($"Model={modelLabel} Schema={schemaVersion} 題數={questions.Count} 輪數={runs}");

        for (var run = 1; run <= runs; run++)
        {
            var items = new List<object>();
            int correct = 0, correctAfterRetry = 0;
            var byCategory = questions.GroupBy(q => q.Category).ToDictionary(g => g.Key, _ => 0);
            foreach (var q in questions)
            {
                var gold = await QueryAsync(demo, q.GoldSql, asReader: false);
                List<ChatMessage> messages = [new(ChatRole.System, system), new(ChatRole.User, q.Question)];
                var first = await AttemptAsync(client, messages, fake?.Replace("{gold}", q.GoldSql), demo, gold, q.Ordered);
                Attempt? retry = null;
                if (first.Failure == "exec_error" && fake is null)
                {
                    // 一次修正：只回饋錯誤訊息，不回饋任何結果資料（§5.4）。
                    messages.Add(new(ChatRole.Assistant, first.Response));
                    messages.Add(new(ChatRole.User, $"這個 SQL 執行失敗，錯誤訊息：{first.Error}\n請修正，同樣只輸出一個 ```sql 區塊。"));
                    retry = await AttemptAsync(client, messages, null, demo, gold, q.Ordered);
                }
                if (first.Failure is null) { correct++; byCategory[q.Category]++; }
                if (first.Failure is null || retry is { Failure: null }) correctAfterRetry++;
                Console.WriteLine($"[{run}] {q.Id} {first.Failure ?? "ok"}{(retry is null ? "" : $" → 修正後 {retry.Failure ?? "ok"}")} {first.Ms}ms");
                items.Add(new { q.Id, q.Category, First = first, Retry = retry });
            }

            var summary = new { correct, correctAfterRetry, total = questions.Count, byCategory };
            Console.WriteLine($"第 {run} 輪：單次 {correct}/{questions.Count}，一次修正後 {correctAfterRetry}/{questions.Count}，" +
                              string.Join("、", byCategory.Select(kv => $"{kv.Key} {kv.Value}")));
            var file = Path.Combine(resultsDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Regex.Replace(modelLabel, @"[^\w.-]", "_")}-{schemaVersion}-r{run}.json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
            {
                model = modelLabel, schemaVersion, run,
                questionsSha256 = Convert.ToHexString(SHA256.HashData(questionsBytes)).ToLowerInvariant(),
                startedAt = DateTimeOffset.Now, summary, systemPrompt = system, items,
            }, Json));
            Console.WriteLine($"已寫入 {file}");
        }
    }

    /// <summary>
    /// 組 system 訊息：輸出規則與 T-SQL 方言提醒，加上 schema 文字。
    /// schema 文字取自 schema-description.md 從「資料庫為」到「## 維護注意」之前的內容；
    /// plain 版再去掉說明欄、表名後的括號說明與資料表段落內的說明文字，只留表名、欄名、型別、關聯與空間慣例，
    /// 所以兩個版本的其餘文字完全相同（§6.1）。
    /// </summary>
    /// <param name="markdown">schema-description.md 的全文。</param>
    /// <param name="schemaVersion">plain 或 described。</param>
    /// <returns>完整的 system 訊息。</returns>
    private static string BuildSystemPrompt(string markdown, string schemaVersion)
    {
        var start = markdown.IndexOf("資料庫為", StringComparison.Ordinal);
        var lines = markdown[start..markdown.IndexOf("## 維護注意", StringComparison.Ordinal)].TrimEnd().Split('\n').Select(l => l.TrimEnd('\r'));
        if (schemaVersion == "plain")
        {
            var section = "";
            lines = lines.Select(l =>
            {
                if (l.StartsWith("## ")) section = l;
                if (l.StartsWith('|')) return string.Join('|', l.Split('|').Take(3)) + "|";
                if (l.StartsWith("### ")) return Regex.Replace(l, "（.*）", "");
                return section == "## 資料表" && l.Length > 0 && !l.StartsWith('#') ? null : l;
            }).OfType<string>();
        }
        return $"""
            你是 SQL Server 的 NL2SQL 助手。請依下方 schema，把使用者的問題轉成一個 T-SQL 查詢。
            規則：
            - 只輸出一個 SELECT 語句（可用 WITH 開頭），放在 ```sql 區塊內，不要解釋。
            - 方言是 T-SQL：取前 N 筆用 TOP，不用 LIMIT。
            - 空間欄位是 SQL Server geography，使用方法呼叫語法，例如 a.Location.STDistance(b.Location)；距離單位是公尺。

            # Schema

            {string.Join('\n', lines)}
            """;
    }

    /// <summary>
    /// 呼叫模型（或使用假回應）一次，抽出 SQL、做前置檢查、以 spike_reader 執行並與標準結果比對。
    /// </summary>
    /// <param name="client">聊天用戶端；使用假回應時為 null。</param>
    /// <param name="messages">要送出的訊息（system＋題目，修正時另含前一次回應與錯誤訊息）。</param>
    /// <param name="fakeResponse">不為 null 時直接當作模型回應，不呼叫模型。</param>
    /// <param name="demo">GeoNl2SqlDemo 的連線字串。</param>
    /// <param name="gold">標準 SQL 的結果集。</param>
    /// <param name="ordered">是否比對列順序。</param>
    /// <returns>這次生成的結果。</returns>
    private static async Task<Attempt> AttemptAsync(IChatClient? client, List<ChatMessage> messages, string? fakeResponse,
        string demo, List<object?[]> gold, bool ordered)
    {
        var sw = Stopwatch.StartNew();
        ChatResponse response;
        try
        {
            response = fakeResponse is null
                ? await client!.GetResponseAsync(messages, new ChatOptions { Temperature = 0, MaxOutputTokens = 1024 })
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, fakeResponse));
        }
        catch (HttpRequestException ex)
        {
            // 本機 Ollama 偶爾對特定請求回 500（非我方程式邏輯問題），記為失敗並讓其餘題目繼續跑，而不是整個輪次中斷。
            return new Attempt("", null, "model_error", ex.Message, sw.ElapsedMilliseconds, null, null);
        }
        var ms = sw.ElapsedMilliseconds;
        Attempt Result(string? sql, string? failure, string? error) =>
            new(response.Text, sql, failure, error, ms, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount);

        // 抽取：優先取 ```sql 區塊，否則取第一個 SELECT／WITH 起始到結尾的文字。
        var match = Regex.Match(response.Text, @"```sql\s*(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!match.Success) match = Regex.Match(response.Text, @"\b(?:SELECT|WITH)\b.*", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!match.Success) return Result(null, "no_sql", null);
        var sql = (match.Groups.Count > 1 ? match.Groups[1].Value : match.Value).Trim().TrimEnd(';').Trim();

        // 前置檢查（便宜的過濾，不是安全邊界）：SELECT／WITH 開頭、單一語句、不含寫入或切換身分的關鍵字。
        if (!Regex.IsMatch(sql, @"^(SELECT|WITH)\b", RegexOptions.IgnoreCase)) return Result(sql, "precheck", "不是以 SELECT 或 WITH 開頭");
        if (sql.Contains(';') || Regex.IsMatch(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            return Result(sql, "precheck", "含多個語句或批次");
        if (Regex.Match(sql, @"\b(INSERT|UPDATE|DELETE|MERGE|DROP|ALTER|CREATE|TRUNCATE|EXEC|EXECUTE|REVERT|GRANT|INTO)\b", RegexOptions.IgnoreCase) is { Success: true } bad)
            return Result(sql, "precheck", $"含禁用關鍵字 {bad.Value}");

        try
        {
            var actual = await QueryAsync(demo, sql, asReader: true);
            return Result(sql, SameResult(gold, actual, ordered) ? null : "wrong_result", null);
        }
        catch (SqlException ex)
        {
            return Result(sql, "exec_error", ex.Message);
        }
    }

    /// <summary>
    /// 執行一段 SQL 並讀回結果集（最多 1,001 列，超過 1,000 列必與標準答案不同）。逾時 10 秒。
    /// 生成的 SQL 用獨立且不進連線池的連線，先切成 spike_reader 再執行，連線關閉即結束身分切換。
    /// </summary>
    /// <param name="demo">GeoNl2SqlDemo 的連線字串。</param>
    /// <param name="sql">要執行的 SQL。</param>
    /// <param name="asReader">true 表示以 spike_reader 身分執行（模型生成的 SQL）；標準 SQL 傳 false。</param>
    /// <returns>每列一個陣列；數值統一轉成 double，空間型別轉成十六進位字串。</returns>
    private static async Task<List<object?[]>> QueryAsync(string demo, string sql, bool asReader)
    {
        await using var conn = new SqlConnection(new SqlConnectionStringBuilder(demo) { Pooling = !asReader }.ConnectionString);
        await conn.OpenAsync();
        if (asReader)
        {
            await using var impersonate = new SqlCommand("EXECUTE AS USER = 'spike_reader';", conn);
            await impersonate.ExecuteNonQueryAsync();
        }
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 10 };
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (rows.Count <= 1000 && await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                // geography／geometry 是 UDT，沒有 Microsoft.SqlServer.Types 時不能 GetValue，改讀原始位元組。
                row[i] = reader.IsDBNull(i) ? null
                    : reader.GetDataTypeName(i).EndsWith("geography") || reader.GetDataTypeName(i).EndsWith("geometry") ? Convert.ToHexString(reader.GetSqlBytes(i).Value)
                    : reader.GetValue(i) switch
                    {
                        byte or short or int or long or decimal or float or double => Convert.ToDouble(reader.GetValue(i), CultureInfo.InvariantCulture),
                        var v => v,
                    };
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// execution accuracy 比對：欄位數相同，且列的多重集合相同（ordered 時順序也須相同）。
    /// 欄位名稱不比；數值容差 1e-6（大於 1 的數值按比例放大）；NULL 等於 NULL。
    /// </summary>
    /// <param name="gold">標準結果。</param>
    /// <param name="actual">生成 SQL 的結果。</param>
    /// <param name="ordered">是否比對列順序。</param>
    /// <returns>兩個結果集相同時為 true。</returns>
    private static bool SameResult(List<object?[]> gold, List<object?[]> actual, bool ordered)
    {
        if (gold.Count != actual.Count) return false;
        if (gold.Count > 0 && gold[0].Length != actual[0].Length) return false;
        if (gold.Count == 0) return true;
        if (!ordered)
        {
            // 以四捨五入到 6 位的文字當排序鍵，讓容差內的數值排在相同位置。
            static string Key(object?[] row) => string.Join('\u001f', row.Select(v => v is double d ? d.ToString("F6", CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture)));
            gold = gold.OrderBy(Key, StringComparer.Ordinal).ToList();
            actual = actual.OrderBy(Key, StringComparer.Ordinal).ToList();
        }
        return gold.Zip(actual).All(pair => pair.First.Zip(pair.Second).All(c => c switch
        {
            (null, null) => true,
            (double a, double b) => Math.Abs(a - b) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b))),
            var (a, b) => Equals(a, b),
        }));
    }
}
