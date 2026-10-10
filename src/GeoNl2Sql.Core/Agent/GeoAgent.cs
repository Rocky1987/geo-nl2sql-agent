using System.Runtime.CompilerServices;
using System.Text;
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
/// <param name="QueryFeatures">最近一次 <c>query_database</c> 加進 <paramref name="Map"/> 的要素（同一批物件）；沒有為 <c>null</c>。admin 旁路用它找出要換掉的要素。</param>
public sealed record GeoAgentResult(string Answer, FeatureCollection? Map, IReadOnlyList<ToolCall> ToolCalls, Nl2SqlResult? Query, bool HitLimit,
    IReadOnlyList<IFeature>? QueryFeatures = null);

/// <summary><see cref="GeoAgent.RunStreamingAsync"/> 送出的事件基底型別。</summary>
public abstract record GeoAgentEvent;

/// <summary>模型要求呼叫某個工具（工具即將執行）。</summary>
/// <param name="Name">工具名稱。</param>
public sealed record ToolStarted(string Name) : GeoAgentEvent;

/// <summary>回答文字的一小段。</summary>
/// <param name="Text">這一段的文字。</param>
public sealed record AnswerDelta(string Text) : GeoAgentEvent;

/// <summary>整個請求結束，附完整結果（一定是最後一個事件）。</summary>
/// <param name="Result">與 <see cref="GeoAgent.RunAsync"/> 相同的結果。</param>
public sealed record Completed(GeoAgentResult Result) : GeoAgentEvent;

/// <summary>
/// 組裝 Microsoft Agent Framework 的 <see cref="ChatClientAgent"/>、三個工具與工具迴圈上限（docs/m3-implementation-plan.md §6）。
/// 每次 <see cref="RunAsync"/> 都建立新的工具集與 <see cref="MapResult"/>，因此同一個 <see cref="GeoAgent"/> 可同時處理多個請求而不會混入彼此的地圖資料。
/// 用法：<c>var result = await new GeoAgent(client, pipeline, spatial, new AgentOptions()).RunAsync("中央區的質心在哪？")</c>。
/// </summary>
public sealed class GeoAgent
{
    /// <summary>提示詞版本（<see cref="Instructions"/> 與 <see cref="GeoTools"/> 的工具描述）；任一者改動就要遞增並記錄。</summary>
    public const string PromptVersion = "v2";

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
        6. 回答只用一般文字：不要使用表格、條列符號、粗體或任何 markdown 語法（例如 |、-、*、#）；有多筆資料時用一兩句話概括重點，例如最多與最少的是哪幾個。
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
        var (agent, tools) = CreateAgent();

        var response = await agent.RunAsync(question, cancellationToken: cancellationToken);

        // 超過上限時迴圈停止，最後一則訊息仍是「要求呼叫工具」而不是答案。
        var hitLimit = response.Messages.LastOrDefault()?.Contents.OfType<FunctionCallContent>().Any() == true;
        return new GeoAgentResult(hitLimit ? LimitMessage : response.Text, tools.Map.ToFeatureCollection(), tools.Calls, tools.LastQuery, hitLimit,
            tools.LastQueryFeatures);
    }

    /// <summary>
    /// 串流版的 <see cref="RunAsync"/>：模型要呼叫工具時送出 <see cref="ToolStarted"/>，產生回答文字時逐段送出 <see cref="AnswerDelta"/>，
    /// 最後送出一次 <see cref="Completed"/>（內容與 <see cref="RunAsync"/> 的結果相同，唯一差別是回答只含最後一次工具呼叫之後的文字，不含呼叫前的旁白）。
    /// </summary>
    /// <param name="question">使用者的問題。</param>
    /// <param name="cancellationToken">取消權杖。</param>
    /// <returns>依發生順序排列的事件；模型呼叫本身的例外會往外傳。</returns>
    public async IAsyncEnumerable<GeoAgentEvent> RunStreamingAsync(
        string question, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (agent, tools) = CreateAgent();
        var text = new StringBuilder();
        var lastWasToolCall = false;

        await foreach (var update in agent.RunStreamingAsync(question, cancellationToken: cancellationToken))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        lastWasToolCall = true;
                        text.Clear(); // 呼叫工具前的旁白（例如「我來查資料庫」）不算回答，只保留工具之後的文字。
                        yield return new ToolStarted(call.Name);
                        break;
                    case TextContent { Text.Length: > 0 } piece:
                        lastWasToolCall = false;
                        text.Append(piece.Text);
                        yield return new AnswerDelta(piece.Text);
                        break;
                    case FunctionResultContent:
                        lastWasToolCall = false;
                        break;
                }
            }
        }

        // 與 RunAsync 相同：停在「要求呼叫工具」而沒有後續結果，代表超過輪數上限。
        yield return new Completed(new GeoAgentResult(lastWasToolCall ? LimitMessage : text.ToString(),
            tools.Map.ToFeatureCollection(), tools.Calls, tools.LastQuery, lastWasToolCall, tools.LastQueryFeatures));
    }

    /// <summary>建立本次請求專用的 Agent 與工具集（含獨立的地圖資料與工具輪數上限）。</summary>
    private (ChatClientAgent Agent, GeoTools Tools) CreateAgent()
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
        return (agent, tools);
    }
}
