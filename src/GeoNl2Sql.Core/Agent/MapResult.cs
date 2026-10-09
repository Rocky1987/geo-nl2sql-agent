using NetTopologySuite.Features;

namespace GeoNl2Sql.Core.Agent;

/// <summary>
/// 單次請求累積的地圖資料（旁路）：各工具把要畫在地圖上的 Feature 放進來，請求結束後由呼叫端取走，<b>不經過模型</b>
/// （docs/m3-implementation-plan.md §2.3）。每次請求建立一個新實例，所以並行的請求不會互相混入對方的資料。
/// 工具迴圈可能並行呼叫多個工具，因此加入與讀取都有鎖。
/// </summary>
public sealed class MapResult
{
    private readonly object _gate = new();
    private readonly List<IFeature> _features = [];

    /// <summary>
    /// 加入要畫的 Feature。
    /// </summary>
    /// <param name="features">Feature 序列；空序列無作用。</param>
    public void Add(IEnumerable<IFeature> features)
    {
        lock (_gate) _features.AddRange(features);
    }

    /// <summary>目前累積的 Feature 數量。</summary>
    public int Count
    {
        get { lock (_gate) return _features.Count; }
    }

    /// <summary>
    /// 取出目前累積的內容。
    /// </summary>
    /// <returns>沒有任何 Feature 時為 <c>null</c>（前端就不畫地圖）；否則為包含所有 Feature 的 FeatureCollection（複本）。</returns>
    public FeatureCollection? ToFeatureCollection()
    {
        lock (_gate)
        {
            if (_features.Count == 0) return null;
            var collection = new FeatureCollection();
            foreach (var f in _features) collection.Add(f);
            return collection;
        }
    }
}
