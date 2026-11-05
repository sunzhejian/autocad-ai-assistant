using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Web.Script.Serialization;
using AutoCAD.AI.Assistant.Models;

namespace AutoCAD.AI.Assistant.Services
{
    internal sealed class OpenAiCompatibleClient
    {
        internal const string DrawingProtocol =
            "绘图协议：画图时先用≤5行中文说明，末尾输出唯一 ```cad 代码块，内容为严格 JSON：{\"commands\":[...]}。" +
            "带图片先读取单位、比例、可见视图和尺寸。根对象增加 sourceUnit: mm/cm/m（源图非高程尺寸单位）、coordinateUnit:\"source\"、views:[可见视图名]。坐标/半径/字高直接使用源图单位，勿自行乘10/1000；执行器统一换算为毫米。每条标注value也用源图原始读数。高程为m的混合单位仅作文字保留。比例只影响图框、字高和线型，不缩小实体。只复刻可见内容；不臆造未给出的视图；重叠高清块勿重复绘制。单位无法确定时询问，不输出计划。" +
            "纯文字绘图默认毫米；每条可加layer/color/lineweight（纸面mm，不随单位换算）。命令 type：" +
            "layer{name,color,linetype,lineweight}；line{points}；polyline{points,closed,width}；rect{p1,p2}；" +
            "ngon{center,sides,radius}；circle{center,radius}；arc{center,radius,start,end}；ellipse{center,major,ratio}；" +
            "spline{points}；point{position}；text{position,height,content}；mtext{position,height,width,content}；" +
            "hatch{points,holes,pattern,scale,angle}（holes为内孔多边形数组，空心剖面不能填满孔洞）；dimlinear{p1,p2,position,rotation}；dimaligned{p1,p2,position}；" +
            "dimradius{center,point}；dimdiameter{center,point}；block{name,commands}；insert{name,position,rotation,scale}；setvar{name,value}；" +
            "standard{}（建标准图层+仿宋+SL73标注）；frame{size,title,scale,number,company,date,origin}（A0–A4 图框+标题栏，原点0,0；scale 为出图比例如 1:100，执行器按该比例整体放大图框/标题栏/字高，并自动设置 DIMSCALE 与 LTSCALE）。" +
            "规则：含块内部共≤400条；连续折线优先polyline，重复构件用block+insert，勿凑指令数。完整图纸先standard再frame；局部截图不加图框。frame固定按纸面mm生成；源单位坐标布局时，图框宽高/内框边距/字高需除以源单位毫米系数（cm为10，m为1000），避开右下标题栏。尺寸优先于像素比例；从基准和尺寸链计算坐标，保证投影对应和对称；剖面用hatch、图名说明用text。每条图片尺寸标注附value（源图读数）、confidence:read/inferred/unknown；模糊尺寸不得冒充read。source坐标模式下，dimlinear的value须等于端点在rotation方向投影长度，dimaligned为实际距离；dimdiameter的point是圆周点。text只可用<>或<>（估），不可覆盖测量值。无需复述题面。1:100字高为350mm，若sourceUnit=cm则height=35。" +
            "粗线0.5、图框0.7、细线0.25；图层名含轮廓自动粗线、含中心/轴线自动CENTER、含虚线自动DASHED；" +
            "填充scale省略=自动（1=自动，>1更疏，<1更密）；frame 已按比例设 DIMSCALE/LTSCALE，不要再手动设；未要求画图不输出代码块；不要声称已执行。";

        private static readonly HttpClient Http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };

        public Task<string> CompleteAsync(ModelConfiguration model, IReadOnlyList<ChatItem> history, string drawingContext, string imagePath)
        {
            return CompleteAsync(model, history, drawingContext, imagePath, null);
        }

        public async Task<string> CompleteAsync(ModelConfiguration model, IReadOnlyList<ChatItem> history, string drawingContext, string imagePath, Action<string> onDelta)
        {
            return await CompleteAsync(model, history, drawingContext, imagePath, onDelta, CancellationToken.None, null).ConfigureAwait(false);
        }

        public async Task<string> CompleteAsync(ModelConfiguration model, IReadOnlyList<ChatItem> history, string drawingContext,
            string imagePath, Action<string> onDelta, CancellationToken cancellation, Action<string> progress, bool detailedImages = false)
        {
            // One budget covers preprocessing, retries, streaming fallback and the optional repair.
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(imagePath != null ? 240 : 120));
                var token = deadline.Token;
                var url = model.UseFullUrl
                    ? model.RequestUrl.Trim()
                    : model.RequestUrl.Trim().TrimEnd('/') + "/chat/completions";
                var key = SecretProtector.Unprotect(model.ApiKeyProtected);
                var messages = imagePath == null
                    ? BuildMessages(model, history, drawingContext, null)
                    : await Task.Run(() => BuildMessages(model, history, drawingContext, imagePath, token, detailedImages), token).ConfigureAwait(false);
                var timeoutSeconds = imagePath != null ? 240 : 120;
                var started = DateTime.Now;

                if (onDelta != null && imagePath == null)
                {
                    var streamed = await TryStreamAsync(url, key, model.ModelId, messages, onDelta, timeoutSeconds, token).ConfigureAwait(false);
                    if (streamed != null)
                    {
                        DiagnosticsLog.Write("请求完成 流式 状态=200 " + Elapsed(started) + "ms");
                        return await ValidateAndRepairAsync(streamed, url, key, model.ModelId, messages, imagePath != null, token, progress).ConfigureAwait(false);
                    }
                }

                var body = new ChatRequest { model = model.ModelId, messages = messages, temperature = 0.2, stream = false };
                if (progress != null) progress(imagePath == null ? "正在生成…" : "正在读取高清图纸并生成几何计划…");
                var result = await PostWithRetryAsync(url, key, body, timeoutSeconds, token).ConfigureAwait(false);
                DiagnosticsLog.Write("请求完成 状态=" + result.status + " " + Elapsed(started) + "ms 模式=" + (imagePath != null ? "图片" : "文本"));

                if (!IsSuccess(result.status))
                {
                    var detail = ErrorMessage(result.json);
                    if (imagePath != null && (result.status == 400 || result.status == 415 || result.status == 422))
                        throw new InvalidOperationException("图片请求被拒绝（" + result.status + "），请检查模型是否支持视觉输入。未进行纯文字复刻。");
                    DiagnosticsLog.Write("请求失败 状态=" + result.status + " " + detail);
                    throw new InvalidOperationException("模型请求失败（" + result.status + "）：" + detail);
                }
                return await ValidateAndRepairAsync(ExtractContent(result.json), url, key, model.ModelId, messages, imagePath != null, token, progress).ConfigureAwait(false);
            }
        }

        private async Task<string> ValidateAndRepairAsync(string answer, string url, string key, string model,
            List<ChatMessage> messages, bool image, CancellationToken token, Action<string> progress)
        {
            string plan = null;
            string error;
            try
            {
                plan = CadPlanText.Extract(answer);
                error = plan == null ? null : CadPlanValidator.Validate(plan);
                if (error == null && image && plan != null) error = CadPlanQuality.ImageContractError(plan);
            }
            catch (Exception ex) { error = ex.Message; }
            if (error == null) return answer;
            if (plan != null) DiagnosticsLog.SavePlan(plan);
            if (progress != null) progress("尺寸或计划校验未通过，正在修正一次…");
            DiagnosticsLog.Write("计划自动修正：" + error);
            messages.Add(new ChatMessage { role = "assistant", content = answer });
            messages.Add(new ChatMessage { role = "user", content = "本地校验未通过，尚未执行。只修正以下问题并返回完整唯一cad计划，勿删除尺寸或伪造文字规避校验：\n" + error });
            var response = await PostWithRetryAsync(url, key, new ChatRequest { model = model, messages = messages, temperature = 0.1 }, 240, token).ConfigureAwait(false);
            if (!IsSuccess(response.status)) throw new InvalidOperationException("计划修正失败（" + response.status + "），未执行绘图。");
            var repaired = ExtractContent(response.json);
            plan = CadPlanText.Extract(repaired);
            error = plan == null ? "修正后缺少绘图计划。" : CadPlanValidator.Validate(plan);
            if (error == null && image) error = CadPlanQuality.ImageContractError(plan);
            if (error != null) { if (plan != null) DiagnosticsLog.SavePlan(plan); throw new InvalidOperationException("修正后校验仍未通过，未执行：" + error); }
            return repaired;
        }

        private async Task<PostResult> PostWithRetryAsync(string url, string key, ChatRequest body, int timeoutSeconds, CancellationToken token)
        {
            var result = await PostAsync(url, key, body, timeoutSeconds, token).ConfigureAwait(false);
            if (result.status != 429 && result.status < 500) return result;
            DiagnosticsLog.Write("请求重试 状态=" + result.status);
            await Task.Delay(1500, token).ConfigureAwait(false);
            return await PostAsync(url, key, body, timeoutSeconds, token).ConfigureAwait(false);
        }

        private async Task<PostResult> PostAsync(string url, string key, ChatRequest body, int timeoutSeconds, CancellationToken token)
        {
            using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                cancellation.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Content = new StringContent(_serializer.Serialize(body), Encoding.UTF8, "application/json");
                using (var response = await Http.SendAsync(request, cancellation.Token).ConfigureAwait(false))
                {
                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return new PostResult { status = (int)response.StatusCode, json = json };
                }
            }
        }

        /// <summary>流式请求；失败或服务端不支持流式时返回 null，由调用方回退非流式。</summary>
        private async Task<string> TryStreamAsync(string url, string key, string modelId, List<ChatMessage> messages, Action<string> onDelta, int timeoutSeconds, CancellationToken token)
        {
            using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cancellation.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                        var body = new ChatRequest { model = modelId, messages = messages, temperature = 0.2, stream = true };
                        request.Content = new StringContent(_serializer.Serialize(body), Encoding.UTF8, "application/json");
                        using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token).ConfigureAwait(false))
                        {
                            if (!IsSuccess((int)response.StatusCode)) return null;
                            var builder = new StringBuilder();
                            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (cancellation.Token.Register(() => stream.Dispose()))
                            using (var reader = new System.IO.StreamReader(stream))
                            {
                                while (true)
                                {
                                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                                    if (line == null) break;
                                    if (line.Length == 0 || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                                    var data = line.Substring(5).Trim();
                                    if (data == "[DONE]") break;
                                    var delta = ExtractDelta(data);
                                    if (string.IsNullOrEmpty(delta)) continue;
                                    builder.Append(delta);
                                    onDelta(delta);
                                }
                            }
                            var text = builder.ToString().Trim();
                            return text.Length == 0 ? null : text;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    DiagnosticsLog.Write("流式失败，回退非流式：" + ex.Message);
                    return null;
                }
            }
        }

        private string ExtractDelta(string json)
        {
            try
            {
                var map = _serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (map == null || !map.ContainsKey("choices")) return null;
                var choices = map["choices"] as object[];
                if (choices == null || choices.Length == 0) return null;
                var choice = choices[0] as Dictionary<string, object>;
                if (choice == null || !choice.ContainsKey("delta")) return null;
                var delta = choice["delta"] as Dictionary<string, object>;
                if (delta == null || !delta.ContainsKey("content") || delta["content"] == null) return null;
                return Convert.ToString(delta["content"]);
            }
            catch
            {
                return null;
            }
        }

        private static long Elapsed(DateTime started)
        {
            return (long)(DateTime.Now - started).TotalMilliseconds;
        }

        private static bool IsSuccess(int status)
        {
            return status >= 200 && status < 300;
        }

        private string ErrorMessage(string json)
        {
            try
            {
                var result = _serializer.Deserialize<ChatResponse>(json);
                if (result != null && result.error != null && !string.IsNullOrEmpty(result.error.message)) return result.error.message;
            }
            catch
            {
            }
            return json;
        }

        private string ExtractContent(string json)
        {
            var result = _serializer.Deserialize<ChatResponse>(json);
            if (result == null || result.choices == null || result.choices.Count == 0 || result.choices[0].message == null)
            {
                throw new InvalidOperationException("模型返回了空内容。");
            }
            var raw = result.choices[0].message.content;
            if (result.choices[0].finish_reason == "length") throw new InvalidOperationException("模型输出被截断，未执行。请框选单个视图或提高服务输出上限。");
            var text = string.Empty;
            if (raw is string)
            {
                text = (string)raw;
            }
            else if (raw is object[])
            {
                var parts = new List<string>();
                foreach (var part in (object[])raw)
                {
                    var map = part as Dictionary<string, object>;
                    if (map != null && map.ContainsKey("text") && map["text"] != null) parts.Add(Convert.ToString(map["text"]));
                }
                text = string.Join("\n", parts.ToArray());
            }
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("模型返回了空内容。");
            return text.Trim();
        }

        private List<ChatMessage> BuildMessages(ModelConfiguration model, IReadOnlyList<ChatItem> history, string drawingContext, string imagePath, CancellationToken token = default(CancellationToken), bool detailedImages = false)
        {
            var messages = new List<ChatMessage>();
            var systemPrompt = string.IsNullOrWhiteSpace(model.SystemPrompt)
                ? "你是 AutoCAD 2024 中文助手。回答简洁，直接给可执行的做法。"
                : model.SystemPrompt;
            messages.Add(new ChatMessage { role = "system", content = systemPrompt });
            messages.Add(new ChatMessage { role = "system", content = DrawingProtocol });
            if (imagePath == null && !string.IsNullOrWhiteSpace(drawingContext))
            {
                messages.Add(new ChatMessage { role = "system", content = "当前图形上下文（仅用于判断现状，勿原样复述）：" + drawingContext });
            }

            // Image reconstruction is a new drawing task: old plans/drawing extents contaminate units and view layout.
            var start = imagePath != null ? Math.Max(0, history.Count - 1) : Math.Max(0, history.Count - 8);
            while (start < history.Count - 1 && !history[start].IsUser) start++;
            for (var index = start; index < history.Count; index++)
            {
                var item = history[index];
                var isLast = index == history.Count - 1;
                if (isLast && item.IsUser && imagePath != null)
                {
                    messages.Add(new ChatMessage { role = "user", content = BuildImageContent(item.Content, imagePath, token, detailedImages) });
                    continue;
                }
                messages.Add(new ChatMessage { role = item.IsUser ? "user" : "assistant", content = item.Content });
            }
            return messages;
        }

        private object BuildImageContent(string text, string imagePath, CancellationToken token, bool detailedImages)
        {
            var caption = string.IsNullOrWhiteSpace(text) ? "复刻这张工程图纸。" : text;
            var parts = new List<object> { new Dictionary<string, object> { { "type", "text" }, { "text", caption } } };
            foreach (var image in ImagePreprocessor.PrepareViews(imagePath, token, detailedImages))
            {
                parts.Add(new Dictionary<string, object> { { "type", "text" }, { "text", image.Label } });
                parts.Add(new Dictionary<string, object> { { "type", "image_url" },
                    { "image_url", new Dictionary<string, object> { { "url", image.DataUrl } } } });
            }
            return parts.ToArray();
        }

        private static string EncodeImage(string path)
        {
            return ImagePreprocessor.EncodeForVision(path);
        }

        private sealed class ChatRequest
        {
            public string model { get; set; }
            public List<ChatMessage> messages { get; set; }
            public double temperature { get; set; }
            public bool stream { get; set; }
        }

        private sealed class ChatMessage
        {
            public string role { get; set; }
            public object content { get; set; }
        }

        private sealed class PostResult
        {
            public int status { get; set; }
            public string json { get; set; }
        }

        private sealed class ChatResponse
        {
            public List<ChatChoice> choices { get; set; }
            public ApiError error { get; set; }
        }

        private sealed class ChatChoice
        {
            public ChatMessage message { get; set; }
            public string finish_reason { get; set; }
        }

        private sealed class ApiError
        {
            public string message { get; set; }
        }
    }
}
