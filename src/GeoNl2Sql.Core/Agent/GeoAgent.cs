using GeoNl2Sql.Core.Nl2Sql;
using GeoNl2Sql.Core.Spatial;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using NetTopologySuite.Features;

namespace GeoNl2Sql.Core.Agent;

/// <summary>Agent 的設定（設定區段 <c>Agent</c>）。</summary>
public sealed class AgentOptions
{
    /// <summary>設定區段名稱。</summary>
    public const string SectionName = "Agent";

    /// <summary>
    /// 單次請求最多執行幾輪工具呼叫（G7）。每輪之後會再問模型一次，所以模型請求最多 <c>MaxToolRounds + 1</c> 次；
    /// 第 <c>MaxToolRounds + 1</c> 次請求若仍要求呼叫工具，就停止並回報無法完成。
    /// </summary>
    public int MaxToolRounds { get; set; } = 6;
}

/// <summary>一次請求的結果。</summary>
/// <param name="Answer">給使用者的回答文字；超過輪數上限時為固定訊息。</param>
/// <param name="Map">要畫在地圖上的 FeatureCollection；沒有任何空間資料時為 <c>null</c>。不經過模型。</param>
/// <param name="ToolCalls">模型呼叫過的工具，依序排列。</param>
/// <param name="Query">最近一次 <c>query_database</c> 的完整結果（含 SQL）；沒呼叫過為 <c>null</c>。</param>
/// <param name="HitLimit">true 表示因超過 <see cref="AgentOptions.MaxToolRounds"/> 而停止。</param>
public sealed record GeoAgentResult(string Answer, FeatureCollection? Map, IReadOnlyList<ToolCall> ToolCalls, Nl2SqlResult? Query, bool HitLimit);

/// <summary>
/// 組裝 Microsoft Agent Framework 的 <see cref="ChatClientAgent"/>、三個工具與工具迴圈上限（docs/m3-implementation-plan.md §6）。
/// 每次 <see cref="RunAsync"/> 都建立新的工具集與 <see cref="MapResult"/>，因此同一個 <see cref="GeoAgent"/> 可同時處理多個請求而不會混入彼此的地圖資料。
/// 用法：<c>var result = await new GeoAgent(client, pipeline, spatial, new AgentOptions()).RunAsync("中央區的質心在哪？")</c>。
/// </summary>
public sealed class GeoAgent
{
    /// <summary>提示詞版本（<see cref="Instructions"/> 與 <see cref="GeoTools"/> 的工具描述）；任一者改動就要遞增並記錄。</summary>
    public const string PromptVersion = "v1";

    /// <summary>超過輪數上限時回給使用者的固定訊息。</summary>
    public const string LimitMessage = "無法在限制內完成這個問題，請換個方式或把問題拆小一點再試。";

    /// <summary>Agent 的系統指示。</summary>
    public const string Instructions = """
        你是電信公司的地理資料助理，回答關於行政區與基地台的問題。你有三個工具：
        - query_database：一般資料查詢（統計、篩選、排序、距離、面積）。
        - get_district_centroid：取得某行政區的質心座標。
        - buffer_around_point：以座標為圓心、半徑（公尺）畫範圍並找出範圍內的基地台。
        規則：
        1. 需要資料時一律呼叫工具，不要憑記憶或猜測回答數字與座標。
        2. 質心與緩衝區要用專用工具，不要用 query_database 自己算。
        3. 座標一律是「緯度在前、經度在後」；半徑單位是公尺（1 公里＝1000 公尺）。
        4. 工具回報失敗或找不到資料時，如實告訴使用者，不要編造結果。
        5. 用使用者的語言簡短回答；資料列與地圖會由系統另外顯示，不必重複列出全部資料。
        """;

    private readonly IChatClient _client;
    private readonly Nl2SqlPipeline _pipeline;
    private readonly SpatialQueries _spatial;
    private readonly AgentOptions _options;

    /// <summary>
    /// 建立 Agent。
    /// </summary>
    /// <param name="client">聊天用戶端；不需自行包工具迴圈，本類別會加上並套用輪數上限。</param>
    /// <param name="pipeline">自然語言轉 SQL 管線。</param>
    /// <param name="spatial">質心與緩衝區查詢。</param>
    /// <param name="options">輪數上限等設定。</param>
    public GeoAgent(IChatClient client, Nl2SqlPipeline pipeline, SpatialQueries spatial, AgentOptions options)
    {
        _client = client;
        _pipeline = pipeline;
        _spatial = spatial;
        _options = options;
    }

    /// <summary>
    /// 回答一個問題：模型可呼叫三個工具，最多 <see cref="AgentOptions.MaxToolRounds"/> 輪。
    /// </summary>
    /// <param name="question">使用者的問題。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>回答、地圖資料與工具呼叫紀錄。模型呼叫本身的例外會往外傳。</returns>
    public async Task<GeoAgentResult> RunAsync(string question, CancellationToken cancellationToken = default)
    {
        var tools = new GeoTools(_pipeline, _spatial, new MapResult());
        var looping = new FunctionInvokingChatClient(_client) { MaximumIterationsPerRequest = _options.MaxToolRounds };
        var agent = new ChatClientAgent(looping, new ChatClientAgentOptions
        {
            Name = "GeoAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = Instructions,
                Tools = tools.CreateFunctions(),
                Temperature = 0,
                MaxOutputTokens = 1024,
            },
        });

        var response = await agent.RunAsync(question, cancellationToken: cancellationToken);

        // 超過上限時迴圈停止，最後一則訊息仍是「要求呼叫工具」而不是答案。
        var hitLimit = response.Messages.LastOrDefault()?.Contents.OfType<FunctionCallContent>().Any() == true;
        return new GeoAgentResult(hitLimit ? LimitMessage : response.Text, tools.Map.ToFeatureCollection(), tools.Calls, tools.LastQuery, hitLimit);
    }
}
