using GeoNl2Sql.Core;
using GeoNl2Sql.Core.Agent;
using GeoNl2Sql.Core.Audit;
using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Nl2Sql;
using GeoNl2Sql.Core.Spatial;
using GeoNl2Sql.Eval.Attack;
using Microsoft.Extensions.AI;

namespace GeoNl2Sql.Tests.Database;

/// <summary>
/// H3 的資料庫層驗證（docs/m4-implementation-plan.md §7.4，假模型、真資料庫、不花費用）：
/// 模型請求（每一輪，含 SQL 自我修正）裡不出現任何客戶的原始個資。
/// 腳本：第 1 次寫 <c>dbo.Customer</c> 的個資欄位被資料庫拒絕（錯誤 230），第 2 次改寫成不加前綴取得遮蔽值。
/// admin 路徑另外確認：表格拿到原始值，但送往模型的請求仍不含。
/// </summary>
[Trait("Category", "Database")]
public class ModelRequestPiiTests
{
    private const string Schema = "資料庫為測試用。\n## 維護注意\n";
    private const string BadSql = "SELECT FullName, NationalId, Phone, Email FROM dbo.Customer";
    private const string GoodSql = "SELECT FullName, NationalId, Phone, Email FROM Customer";

    private sealed class FakeWriter : IAuditWriter
    {
        public Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// 腳本化的假模型：NL2SQL 管線（有 system 訊息）第 1 次回帶 dbo 前綴的 SQL、收到修正要求後第 2 次回不加前綴的；
    /// Agent 先要求呼叫 query_database，拿到工具結果後回答。
    /// </summary>
    private sealed class ScriptedModel : IChatClient
    {
        private ChatMessage Next(IEnumerable<ChatMessage> messages)
        {
            var list = messages.ToList();
            if (list.Any(m => m.Role == ChatRole.System))
                return new ChatMessage(ChatRole.Assistant, $"```sql\n{(list.Count <= 2 ? BadSql : GoodSql)}\n```");
            if (list.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any()) return new ChatMessage(ChatRole.Assistant, "完成。");
            return new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("c1", "query_database", new Dictionary<string, object?> { ["question"] = "列出客戶的姓名、身分證、電話與電子郵件" })]);
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(Next(messages)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>以擁有者身分讀出全部客戶的原始個資值。</summary>
    private static async Task<List<string>> RawValuesAsync()
    {
        var all = await new ReadOnlySqlExecutor(DbConfig.Demo, new QueryLimits { MaxRows = 5000 })
            .ExecuteAsync("SELECT FullName, NationalId, Phone, Email FROM dbo.Customer");
        return all.Rows.SelectMany(r => r).Select(v => ((string)v!).Trim()).Distinct().ToList();
    }

    private static (QueryService Service, RecordingChatClient Recorder) Build()
    {
        var limits = new QueryLimits();
        var recorder = new RecordingChatClient(new ScriptedModel());
        var client = new UsageTrackingChatClient(recorder);
        var reader = new ReadOnlySqlExecutor(DbConfig.Reader, limits);
        var pii = new ReadOnlySqlExecutor(DbConfig.ReaderPii, limits);
        var pipeline = new Nl2SqlPipeline(client, new SqlValidator(DemoSchema.Tables), reader.ExecuteAsync, Schema);
        var agent = new GeoAgent(client, pipeline, new SpatialQueries(reader, new SpatialOptions()), new AgentOptions());
        return (new QueryService(agent, (sql, ct) => pii.ExecuteAsync(sql, ct), new FakeWriter(), new ModelOptions()), recorder);
    }

    /// <summary>analyst：自我修正後取得遮蔽值，所有模型請求都不含原始個資，但看得到遮蔽值。</summary>
    [Fact]
    public async Task Analyst_ModelRequestsNeverContainRawPii_EvenAcrossSelfCorrection()
    {
        var raw = await RawValuesAsync();
        var (service, recorder) = Build();

        var result = await service.RunAsync("列出客戶的姓名、身分證、電話與電子郵件", UserRole.Analyst);

        Assert.True(result.Query!.Success);
        Assert.Equal(2, result.Query.Attempts.Count);           // 第 1 次被資料庫拒絕，第 2 次成功
        Assert.Equal("exec_error", result.Query.Attempts[0].Failure);
        Assert.Equal(1000, result.Query.Data!.Rows.Count);
        var requests = recorder.Requests;
        Assert.True(requests.Count >= 4, $"預期至少 4 次模型請求，實際 {requests.Count}");
        foreach (var request in requests)
            foreach (var value in raw)
                Assert.False(request.Contains(value, StringComparison.OrdinalIgnoreCase), "模型請求出現原始個資");
        Assert.Contains(requests, r => r.Contains("**"));       // 模型看到的是遮蔽值
    }

    /// <summary>admin：表格換成原始值，但送往模型的請求仍然不含任何原始個資。</summary>
    [Fact]
    public async Task Admin_TableHasRawValues_ButModelRequestsDoNot()
    {
        var raw = await RawValuesAsync();
        var (service, recorder) = Build();

        var result = await service.RunAsync("列出客戶的姓名、身分證、電話與電子郵件", UserRole.Admin);

        var tableValues = result.Query!.Data!.Rows.SelectMany(r => r).Select(v => ((string)v!).Trim()).ToHashSet();
        Assert.Contains(raw[0], tableValues);                   // 表格拿到原始值
        foreach (var request in recorder.Requests)
            foreach (var value in raw)
                Assert.False(request.Contains(value, StringComparison.OrdinalIgnoreCase), "admin 路徑的模型請求出現原始個資");
    }
}
