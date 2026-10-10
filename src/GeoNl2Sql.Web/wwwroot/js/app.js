// 首頁腳本：送出問題到 POST /query/stream，邊接收邊顯示進度與回答，最後把回答、SQL、結果表與地圖畫出來。
// 所有來自伺服器的文字一律用 textContent 寫入（不拼 innerHTML），避免資料表內容變成頁面腳本。
(() => {
    const $ = (id) => document.getElementById(id);
    const form = $('ask-form');
    const input = $('question');
    const submit = $('submit');
    let map = null;
    let layer = null;
    let featureLayers = []; // 與 geoJson.features 同順序的 Leaflet 圖層，供表格的「定位」按鈕使用。
    let selected = null;    // 目前被標示的圖層。

    // 伺服器把空間欄位換成這段固定文字（QueryController.SpatialPlaceholder）；表格靠它找出哪些儲存格是地圖要素。
    const spatialPlaceholder = '<空間資料，已顯示在地圖上>';
    const highlightStyle = { color: '#dc2626', fillColor: '#facc15', fillOpacity: 1, weight: 3, radius: 11 };

    // 要素類型 → 地圖樣式（kind 由 GeoTools 寫入 properties；一般查詢結果的要素沒有 kind）。
    const styles = {
        buffer: { color: '#2563eb', weight: 2, fillColor: '#2563eb', fillOpacity: 0.12 },
        centroid: { color: '#dc2626', fillColor: '#dc2626', fillOpacity: 0.9, radius: 8, weight: 2 },
        station: { color: '#18181b', fillColor: '#18181b', fillOpacity: 0.85, radius: 5, weight: 1 },
        default: { color: '#2563eb', weight: 2, fillColor: '#2563eb', fillOpacity: 0.2, radius: 6 },
    };

    // 工具名稱 → 給一般使用者看的中文名稱。
    const toolLabels = {
        query_database: '資料庫查詢',
        get_district_centroid: '區域中心點',
        buffer_around_point: '周邊範圍分析',
    };

    // 資料表欄位名稱（小寫）→ 中文欄位名；沒列到的別名維持原樣顯示。
    const columnLabels = {
        districtid: '行政區編號', districtname: '行政區', population: '人口數',
        stationid: '基地台編號', stationname: '基地台名稱', band: '頻段', status: '狀態',
        installeddate: '建置日期', stationcount: '基地台數量', longitude: '經度', latitude: '緯度',
        planid: '方案編號', planname: '方案名稱', monthlyfee: '月租費', datacapgb: '每月流量上限（GB）',
        customerid: '客戶編號', subscriptionid: '訂閱編號', startdate: '開始日期',
        outageid: '中斷事件編號', startedat: '開始時間', durationminutes: '持續時間（分鐘）', cause: '原因',
        kind: '類型',
    };
    const columnLabel = (name) => columnLabels[String(name).toLowerCase()] || name;

    // 把 properties 組成彈出視窗的 DOM（textContent，不用 innerHTML）。
    function popupFor(properties) {
        const box = document.createElement('div');
        for (const [key, value] of Object.entries(properties || {})) {
            const line = document.createElement('div');
            const name = document.createElement('strong');
            name.textContent = columnLabel(key) + '：';
            line.append(name, document.createTextNode(String(value)));
            box.append(line);
        }
        return box;
    }

    // 地圖在載入頁面時就建立（預設看展示資料所在的城區），沒有空間資料的回答只是清掉圖層並顯示提示。
    function initMap() {
        map = L.map('map', { scrollWheelZoom: true }).setView([25.05, 121.5], 12);
        L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 19,
            attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors',
        }).addTo(map);
    }

    const styleFor = (feature) => styles[feature.properties?.kind] || styles.default;

    // 定位到第 index 個要素：標示、移動地圖並打開彈出視窗。
    function locate(index) {
        const target = featureLayers[index];
        if (!target) return;
        if (selected) selected.setStyle(styleFor(selected.feature));
        selected = target;
        target.setStyle(highlightStyle);
        target.bringToFront();
        $('map').scrollIntoView({ behavior: 'smooth', block: 'center' });
        if (target.getBounds) map.fitBounds(target.getBounds(), { padding: [40, 40], maxZoom: 15 });
        else map.flyTo(target.getLatLng(), Math.max(map.getZoom(), 15));
        target.openPopup();
    }

    function drawMap(geoJson) {
        if (layer) { layer.remove(); layer = null; }
        featureLayers = [];
        selected = null;
        const empty = $('map-empty');
        empty.textContent = '這個問題的答案沒有位置資料，所以地圖沒有標記。試試「中心點」、「附近範圍」或「地圖」範例。';
        empty.classList.toggle('hidden', !!geoJson);
        if (!geoJson) return;
        layer = L.geoJSON(geoJson, {
            style: styleFor,
            pointToLayer: (feature, latlng) => L.circleMarker(latlng, styleFor(feature)),
            onEachFeature: (feature, l) => {
                featureLayers.push(l);
                l.bindPopup(() => popupFor(feature.properties));
            },
        }).addTo(map);
        const bounds = layer.getBounds();
        if (bounds.isValid()) map.fitBounds(bounds, { padding: [24, 24], maxZoom: 15 });
    }

    function drawTable(columns, rows, truncated) {
        const card = $('table-card');
        if (columns.length === 0) { card.classList.add('hidden'); return; }
        card.classList.remove('hidden');
        const head = document.createElement('tr');
        for (const column of columns) {
            const th = document.createElement('th');
            th.textContent = columnLabel(column);
            head.append(th);
        }
        $('thead').replaceChildren(head);
        let featureIndex = 0;
        $('tbody').replaceChildren(...rows.map((row) => {
            const tr = document.createElement('tr');
            for (const value of row) {
                const td = document.createElement('td');
                if (value === spatialPlaceholder) {
                    const button = document.createElement('button');
                    button.type = 'button';
                    button.className = 'locate-btn';
                    button.textContent = '📍 定位';
                    button.dataset.feature = String(featureIndex++);
                    td.append(button);
                } else {
                    td.textContent = value === null ? 'NULL' : String(value);
                }
                tr.append(td);
            }
            return tr;
        }));
        // 表格的空間儲存格數量要與地圖要素數量一致才能對應；不一致（例如同一次回答還用了其他工具畫圖）就不提供定位。
        if (featureIndex !== featureLayers.length) {
            for (const button of $('tbody').querySelectorAll('.locate-btn')) button.parentElement.textContent = '（地圖上）';
        }
        $('row-count').textContent = `${rows.length} 列${truncated ? '（結果過多，已截斷）' : ''}`;
    }

    // 表格的「定位」按鈕（事件委派，表格每次重畫都不必重新綁定）。
    $('tbody').addEventListener('click', (event) => {
        const button = event.target.closest('.locate-btn');
        if (button) locate(Number(button.dataset.feature));
    });

    function setAnswer(text, muted) {
        const answer = $('answer');
        answer.textContent = text;
        answer.classList.toggle('text-muted-foreground', muted);
    }

    // 送出新問題時先清掉上一題的表格與地圖，避免新結果出來前殘留舊內容。
    function clearResults() {
        $('results').classList.add('hidden');
        $('results').classList.remove('flex');
        $('table-card').classList.add('hidden');
        $('thead').replaceChildren();
        $('tbody').replaceChildren();
        drawMap(null);
    }

    function render(data) {
        setAnswer(data.answer || '', false);
        const tools = $('tools');
        tools.replaceChildren(...(data.toolsCalled || []).map((name) => {
            const badge = document.createElement('span');
            badge.className = 'badge';
            badge.textContent = toolLabels[name] || name;
            return badge;
        }));
        if (data.error) {
            const badge = document.createElement('span');
            badge.className = 'badge-error';
            badge.textContent = '失敗';
            tools.append(badge);
        }
        drawMap(data.geoJson); // 先畫地圖：表格的「定位」按鈕要對照地圖要素的數量。
        drawTable(data.columns || [], data.rows || [], data.truncated);
        const hasDetails = (data.columns || []).length > 0;
        $('results').classList.toggle('hidden', !hasDetails);
        $('results').classList.toggle('flex', hasDetails);
    }

    // 工具名稱 → 執行中的進度文字。
    const stepLabels = {
        query_database: '正在查詢資料庫…',
        get_district_centroid: '正在找出區域中心點…',
        buffer_around_point: '正在計算周邊範圍…',
    };

    // 打字機效果：模型輸出很快，這裡用計時器以固定節奏顯示；積壓太多時加快，避免最後等太久。
    function createTypewriter() {
        let pending = '';
        let shown = '';
        let timer = null;
        let onDrained = null;
        const tick = () => {
            const count = Math.max(1, Math.ceil(pending.length / 60));
            shown += pending.slice(0, count);
            pending = pending.slice(count);
            setAnswer(shown, false);
            if (pending.length === 0) {
                clearInterval(timer);
                timer = null;
                if (onDrained) { onDrained(); onDrained = null; }
            }
        };
        return {
            push(text) {
                pending += text;
                if (!timer) timer = setInterval(tick, 45);
            },
            reset() {
                pending = ''; shown = '';
                if (timer) { clearInterval(timer); timer = null; }
                if (onDrained) { onDrained(); onDrained = null; }
            },
            hasText: () => shown.length + pending.length > 0,
            // 等已收到的文字都顯示完。
            drain() {
                return pending.length === 0 ? Promise.resolve() : new Promise((resolve) => { onDrained = resolve; });
            },
        };
    }

    // 處理串流中的一個事件（POST /query/stream 每行一個 JSON）。回傳 true 表示已收到最後結果或錯誤。
    async function handleEvent(event, typer) {
        if (event.type === 'step') {
            // 呼叫工具前的旁白不算回答，後端的最後結果也不含它；但要先讓它完整顯示並停留片刻，再切到下一階段。
            if (typer.hasText()) {
                await typer.drain();
                await new Promise((resolve) => setTimeout(resolve, 800));
            }
            typer.reset();
            setAnswer(stepLabels[event.tool] || '處理中…', true);
            const badge = document.createElement('span');
            badge.className = 'badge';
            badge.textContent = toolLabels[event.tool] || event.tool;
            $('tools').append(badge);
        } else if (event.type === 'answer') {
            typer.push(event.delta);
        } else if (event.type === 'result') {
            await typer.drain();
            render(event.data);
            return true;
        } else if (event.type === 'error') {
            typer.reset();
            setAnswer(event.message, true);
            return true;
        }
        return false;
    }

    async function ask(question) {
        submit.disabled = true;
        $('tools').replaceChildren();
        clearResults();
        setAnswer('思考中…', true);
        const typer = createTypewriter();
        let finished = false;
        try {
            const response = await fetch('/query/stream', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ question }),
            });
            // 問題不合法時是一般的 400 JSON（success=false 與固定的錯誤訊息）。
            if (!response.ok) {
                render(await response.json());
                return;
            }
            const reader = response.body.getReader();
            const decoder = new TextDecoder();
            let buffer = '';
            for (;;) {
                const { value, done } = await reader.read();
                if (done) break;
                buffer += decoder.decode(value, { stream: true });
                const lines = buffer.split('\n');
                buffer = lines.pop();
                for (const line of lines) {
                    if (line.trim() && await handleEvent(JSON.parse(line), typer)) finished = true;
                }
            }
            if (!finished) typer.reset();
            if (!finished) setAnswer('連線中斷，沒有收到完整的回答，請再試一次。', true);
        } catch {
            typer.reset();
            setAnswer('無法連線到伺服器，請確認服務是否在執行。', true);
        } finally {
            submit.disabled = false;
        }
    }

    initMap();

    form.addEventListener('submit', (event) => {
        event.preventDefault();
        const question = input.value.trim();
        if (question) ask(question);
    });
    for (const button of document.querySelectorAll('.example')) {
        button.addEventListener('click', () => {
            const text = button.lastElementChild.textContent;
            input.value = text;
            ask(text);
        });
    }
})();
