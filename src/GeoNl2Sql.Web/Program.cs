using GeoNl2Sql.Core;
using GeoNl2Sql.Core.Agent;
using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Nl2Sql;
using GeoNl2Sql.Core.Spatial;
using GeoNl2Sql.Web.Models;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// 連線字串與金鑰來自 user-secrets／環境變數（與 Eval 共用），不寫進 appsettings.json。
var reader = builder.Configuration.GetConnectionString("Reader")
    ?? throw new InvalidOperationException("找不到設定 ConnectionStrings:Reader；請先設定 user-secrets（見 README）。");
var modelOptions = builder.Configuration.GetSection(ModelOptions.SectionName).Get<ModelOptions>() ?? new ModelOptions();
var agentOptions = builder.Configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions();
var spatialOptions = builder.Configuration.GetSection(SpatialOptions.SectionName).Get<SpatialOptions>() ?? new SpatialOptions();
var limits = builder.Configuration.GetSection(QueryLimits.SectionName).Get<QueryLimits>() ?? new QueryLimits();

// 這些物件都沒有請求間共用的狀態（每次請求在 GeoAgent 內建立新的 GeoTools 與 MapResult），所以可以是單例。
builder.Services.AddSingleton(_ => ChatClientFactory.Create(modelOptions));
builder.Services.AddSingleton<GeoAgent>(sp =>
{
    var executor = new ReadOnlySqlExecutor(reader, limits);
    var schema = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "db", "schema-description.md"));
    var client = sp.GetRequiredService<IChatClient>();
    var pipeline = new Nl2SqlPipeline(client, new SqlValidator(DemoSchema.Tables), executor.ExecuteAsync, schema);
    return new GeoAgent(client, pipeline, new SpatialQueries(executor, spatialOptions), agentOptions);
});
// admin 旁路：只用來重跑已通過驗證的 SQL 給表格，結果不回給模型（docs/m4-implementation-plan.md §4.2）。
builder.Services.AddSingleton<QueryService>(sp =>
{
    var readerPii = builder.Configuration.GetConnectionString("ReaderPii")
        ?? throw new InvalidOperationException("找不到設定 ConnectionStrings:ReaderPii；請先設定 user-secrets 並重新執行 seed（見 docs/m4-implementation-plan.md §3.1）。");
    var piiExecutor = new ReadOnlySqlExecutor(readerPii, limits);
    return new QueryService(sp.GetRequiredService<GeoAgent>(), (sql, ct) => piiExecutor.ExecuteAsync(sql, ct),
        sp.GetRequiredService<ILogger<QueryService>>());
});
builder.Services.AddSingleton<AgentRunner>(sp => sp.GetRequiredService<QueryService>().RunAsync);
builder.Services.AddSingleton<AgentStreamRunner>(sp => sp.GetRequiredService<QueryService>().RunStreamingAsync);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseRouting();
app.MapStaticAssets();

app.MapControllers();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();

/// <summary>讓整合測試能以 <c>WebApplicationFactory</c> 參照入口。</summary>
public partial class Program;
