using GeoNl2Sql.Core.Agent;
using GeoNl2Sql.Core.Guardrails;
using GeoNl2Sql.Core.Nl2Sql;
using GeoNl2Sql.Core.Spatial;
using Microsoft.Extensions.AI;

namespace GeoNl2Sql.Tests.Database;

/// <summary>
/// 三個工具對真實資料庫的測試（不呼叫模型）：回給模型的文字、旁路的地圖資料，以及該地圖資料通過 GeoJSON 檢查。
/// 需要本機資料庫與 <c>ConnectionStrings:Reader</c>。
/// </summary>
[Trait("Category", "Database")]
public class GeoToolsDatabaseTests
{
    /// <summary>永遠不會被呼叫的模型（這些測試只呼叫工具，不經過模型）。</summary>
    private sealed class NoModel : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("不應呼叫模型");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private static GeoTools Tools()
    {
        var executor = new ReadOnlySqlExecutor(DbConfig.Reader, new QueryLimits());
        var pipeline = new Nl2SqlPipeline(new NoModel(), new SqlValidator(DemoSchema.Tables), executor.ExecuteAsync, "資料庫為測試用。\n## 維護注意\n");
        return new GeoTools(pipeline, new SpatialQueries(executor, new SpatialOptions()), new MapResult());
    }

    /// <summary>質心工具：文字含座標，地圖上多一個質心點，且 GeoJSON 通過檢查。</summary>
    [Fact]
    public async Task CentroidTool_ReturnsText_AndMapPoint()
    {
        var tools = Tools();

        var text = await tools.GetDistrictCentroidAsync("中央區");

        Assert.Contains("中央區", text);
        Assert.Contains("緯度", text);
        var map = tools.Map.ToFeatureCollection()!;
        Assert.Single(map);
        Assert.Empty(GeoJsonValidator.Validate(GeoJsonBuilder.Serialize(map)));
    }

    /// <summary>找不到的行政區：固定訊息，地圖不變。</summary>
    [Fact]
    public async Task CentroidTool_UnknownDistrict_ReturnsFixedMessage()
    {
        var tools = Tools();

        var text = await tools.GetDistrictCentroidAsync("不存在的區");

        Assert.Equal("找不到名為「不存在的區」的行政區。", text);
        Assert.Equal(0, tools.Map.Count);
    }

    /// <summary>緩衝區工具：文字含基地台數量；地圖有一個緩衝區加上列出的基地台點，GeoJSON 通過檢查。</summary>
    [Fact]
    public async Task BufferTool_ReturnsCount_AndMapFeatures()
    {
        var tools = Tools();

        var text = await tools.BufferAroundPointAsync(25.0478, 121.5170, 2000);

        Assert.Contains("共有", text);
        var map = tools.Map.ToFeatureCollection()!;
        Assert.True(map.Count >= 2, "至少有緩衝區與一座基地台");
        Assert.Empty(GeoJsonValidator.Validate(GeoJsonBuilder.Serialize(map)));
    }
}
