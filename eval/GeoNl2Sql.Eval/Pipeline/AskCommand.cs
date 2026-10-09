using GeoNl2Sql.Core;
using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Nl2Sql;
using Microsoft.Extensions.Configuration;

namespace GeoNl2Sql.Eval.Pipeline;

/// <summary>
/// M2 的 <c>ask</c> 子命令：手動問一個問題，印出生成的 SQL、每次嘗試的結果與前 20 列。
/// 用來人工確認端到端流程，也是之後串接 Web 前的示範入口。
/// </summary>
public static class AskCommand
{
    /// <summary>最多印出的資料列數。</summary>
    private const int PreviewRows = 20;

    /// <summary>
    /// 執行 ask。用法：<c>ask "問題" [--provider P] [--model M]</c>。
    /// </summary>
    /// <param name="config">已載入的設定；讀取 <c>Model</c> 區段與 <c>ConnectionStrings:Reader</c>。</param>
    /// <param name="args">子命令之後的參數；第一個參數是問題。</param>
    /// <exception cref="InvalidOperationException">缺少連線字串。</exception>
    public static async Task RunAsync(IConfiguration config, string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--"))
        {
            Console.WriteLine("用法：ask \"問題\" [--provider P] [--model M]");
            return;
        }
        string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        var reader = config.GetConnectionString("Reader") ?? throw new InvalidOperationException("找不到設定 ConnectionStrings:Reader。");
        var executor = new ReadOnlySqlExecutor(reader, config.GetSection(QueryLimits.SectionName).Get<QueryLimits>() ?? new QueryLimits());
        var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", "schema-description.md"));
        using var client = ChatClientFactory.Create(PipelineCommand.ReadModelOptions(config, Opt));
        var pipeline = new Nl2SqlPipeline(client, new SqlValidator(DemoSchema.Tables), executor.ExecuteAsync, schema);

        var result = await pipeline.AskAsync(args[0]);

        for (var i = 0; i < result.Attempts.Count; i++)
        {
            var a = result.Attempts[i];
            Console.WriteLine($"--- 第 {i + 1} 次嘗試：{a.Failure ?? "成功"}{(a.Message is null ? "" : $"（{a.Message}）")}");
            Console.WriteLine(a.Sql ?? "(抽不到 SQL)");
        }
        if (!result.Success)
        {
            Console.WriteLine($"失敗：{result.FailureReason}");
            return;
        }
        var data = result.Data!;
        Console.WriteLine($"=== 結果（{string.Join(", ", data.Columns)}）共 {data.Rows.Count} 列{(data.Truncated ? "（已截斷）" : "")}");
        foreach (var row in data.Rows.Take(PreviewRows))
            Console.WriteLine(string.Join(" | ", row.Select(v => v switch { null => "NULL", byte[] b => $"<{b.Length} bytes>", _ => v.ToString() })));
        if (data.Rows.Count > PreviewRows) Console.WriteLine($"…（其餘 {data.Rows.Count - PreviewRows} 列未顯示）");
    }
}
