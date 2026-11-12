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
            "layer{name,color,linetype,lineweight}；line{points}；polyline{points,closed,width}；rect{p1,p2}（也接受points:[两个对角点]，不与p1/p2混用；矩形轴对齐，旋转轮廓用polyline）；" +
            "ngon{center,sides,radius}；circle{center,radius}；arc{center,radius,start,end}；ellipse{center,major,ratio}；" +
            "donut{center,innerDiameter,outerDiameter}（原生带宽圆环，0≤内径<外径）；solid{points}（按边界顺序3点三角形或4点凸四边形的二维实心区域）；" +
            "leader{points,position,height,content,arrowSize,material,targetLayer}（原生多重引线，points依次为箭头尖、折点，position为文字位置，不能用几条散线冒充）；" +
            "材料剖面必须画实际封闭轮廓、材料hatch及内孔；混凝土等材料引线须同时给material与targetLayer，箭头尖必须落在该层的真实材料填充内，不能指向空白或孔洞。混凝土不可用孤立黑色solid三角形/矩形代替；无源图时可用material:concrete，源图已给纹理时优先照源图。仅做引线语法测试时用“引线测试”等通用文字，不冒充材料构件。" +
            "dimangular{center,p1,p2,position,value}（真实原生小角标注，center为顶点、p1/p2为射线上点，position在小角扇区内；value始终为度，不随cm/m换算，角度须0<值<180）；" +
            "polyline可带bulges数组，与points等长；每值为该顶点至下一顶点圆弧的tan(扫角/4)，正值逆时针，负值顺时针，0为直线，半圆为±1；开放折线末值为0，弧不能用很多短线代替。" +
            "line/polyline可带dashLength（实际最长实段的模型长度，按源坐标单位给出）；执行器按真实线型定义和当前LTSCALE计算该新实体的比例，不改已有图元或线型定义。读取源图中心线/虚线长短节奏时优先指定dashLength，不能只给CENTER名字就认为外观正确；连续线/纯点线不能使用此字段。" +
            "spline{points}；point{position}；text{position,height,content}；mtext{position,height,width,content}；" +
            "hatch{points,holes,material,spacing,pattern,scale,angle}（holes为内孔多边形数组。材料优先material:riprap块石/gravel碎石/sand砂/earth土/clay粘土/concrete混凝土，自动按当前DIMSCALE控制纸面疏密；spacing可选，指定源坐标单位下的纹理大小；不要用几块孤立多边形代替整个石材区域填充。材料填充与文字、引线分开，保留层间边界与内孔）；dimlinear{p1,p2,position,rotation}；dimaligned{p1,p2,position}；" +
            "填充拓扑：U形、槽形或向外敞开的空白是外轮廓的凹缺口，应沿真实边界顺序写成一个凹多边形points；holes仅用于完全位于外环内部且不接触外环的封闭孔洞。多视图、相邻构件分别创建填充，不用相互接触或重叠的内孔拼接。保留源尺寸和实际开口，不删除孔洞、不缩小孔洞来规避校验。" +
            "dimradius{center,point}；dimdiameter{center,point}；block{name,commands}；insert{name,position,rotation,scale}；setvar{name,value}；" +
            "尺寸几何引用：可给line/rect/polyline（直边）/circle增加稳定id；dimlinear/dimaligned可加reference引用同一作用域内实际被测轮廓的id，p1/p2必须在该轮廓上，合法边中点可用。图片中能定位轮廓的线性尺寸优先声明reference；内孔尺寸引用内孔轮廓，不挂在外墙上。id在块内局部唯一；不跨块、不引用含bulges圆弧多段线或hatch，不为通过检查虚构辅助轮廓。只验证本次落图，非持久关联约束。" +
            "矩形整体边长标注优先简写dimlinear{reference,side,position,value,confidence}，side为top/bottom/left/right（世界XY上下左右），省略p1/p2；rotation可省略，若提供须与side一致（上下为0、左右为90）。本地从该rect/rectangle精确计算端点并核对value，不修改原图读数。仅测完整矩形边长；局部长度、斜边和其他曲线仍明确给p1/p2。内孔必须引用实际内孔矩形，不用外框尺寸猜测。" +
            "standard{}（建标准图层+仿宋+SL73标注）；frame{size,title,scale,number,company,date,origin}（A0–A4 图框+标题栏，原点0,0；scale 为出图比例如 1:100，执行器按该比例整体放大图框/标题栏/字高，并自动设置 DIMSCALE 与 LTSCALE）。" +
            "规则：含块内部共≤400条；预计新建图元≤5000、输入点≤20000、填充边界合计≤8192、文字合计≤40000字符；块展开受独立工作量上限控制，不用层层插入制造海量重复。连续折线优先polyline，重复构件用block+insert，勿凑指令数。完整图纸先standard再frame；局部截图不加图框。frame固定按纸面mm生成；源单位坐标布局时，图框宽高/内框边距/字高需除以源单位毫米系数（cm为10，m为1000），避开右下标题栏。尺寸优先于像素比例；从基准和尺寸链计算坐标，保证投影对应和对称；剖面用hatch、图名说明用text。每条图片尺寸标注附value（源图读数）、confidence:read/inferred/unknown；模糊尺寸不得冒充read。source坐标模式下，dimlinear的value须等于端点在rotation方向投影长度，dimaligned为实际距离；dimdiameter的point是圆周点。text只可用<>或<>（估），不可覆盖测量值。无需复述题面。1:100字高为350mm，若sourceUnit=cm则height=35。" +
            "复刻以源图外观为准，不以通用规范替换图中实际符号。先在内部核对每个视图的外形/内孔/尺寸端点/中心线/图名和下划线数量，再输出完整计划。图名按相对图幅大小取字高，不要把图名放大到接近内孔高度，不自行添加源图没有的双下划线。" +
            "复刻填充先看实际纹理：原图若是斜线加颗粒，不用只有三角碎石的AR-CONC替代；可用同一points和holes的两个hatch叠加ANSI31和DOTS/AR-SAND。清楚可辨时只指定pattern、spacing及angle，material是纹理辨不清时的近似预设。spacing为源坐标单位下相邻纹理的特征间隔，按图中线距与已读实体尺寸的比值估算。ANSI31自带45度斜线，angle=0保持该角度，angle是额外旋转。单色原图默认color=7，不用灰色弱化其填充。" +
            "setvar仅支持DIMSCALE/LTSCALE/CELTSCALE/TEXTSIZE/DIMTXT/DIMASZ/DIMEXO/DIMEXE/DIMGAP/DIMDEC/LWDISPLAY/DIMLFAC/DIMTAD/DIMLUNIT/DIMZIN/DIMTIH/DIMTOH/DIMTSZ。竖向尺寸文字需沿尺寸线时DIMTIH=0、DIMTOH=0。尺寸端点按源图区分短斜线与箭头：DIMTSZ>0设置短斜线大小（源单位），DIMTSZ=0使用箭头。" +
            "source坐标模式的DIMTXT/DIMASZ/DIMEXO/DIMEXE/DIMGAP/DIMTSZ也是模型空间源参数长度，本地换算为mm后除以DIMSCALE，比例只应用一次。注意DIMTSZ不是完整tick长度：标准线性/对齐标注的完整45度斜线约为该模型参数的2*sqrt(2)倍，须从原图实测完整长度再换算，勿套用通用样例。text.height等普通几何长度仍按源单位给出。" +
            "粗线0.5、图框0.7、细线0.25；图层名含轮廓自动粗线、含中心/轴线自动CENTER、含虚线自动DASHED；" +
            "填充scale省略=自动（1=自动，>1更疏，<1更密）；frame 已按比例设 DIMSCALE/LTSCALE，不要再手动设；未要求画图不输出代码块；不要声称已执行。";

        private static readonly HttpClient Http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        internal const string PlanPatchProtocol =
            "你正在复核已有CAD计划，不是重新绘图。只输出唯一```cadpatch代码块，内容严格JSON："
            + "{\"base_sha256\":\"给定的原计划哈希\",\"updates\":[{\"command\":\"原计划1基序号，如12或2.3\",\"fields\":{\"position\":[x,y]}}],\"setvars\":{\"LTSCALE\":数值}}。"
            + "原计划全文由本地保存，只给出可编辑目标摘要和几何基准；哈希由本地生成，必须原样复制，不能自行重算。"
            + "updates最多16项，同一command只出现一次；坐标和长度使用摘要所示原始坐标单位，不能自行换算mm。"
            + "只修复复核指出的问题，保留已正确的图形。不得输出完整commands，不改轮廓坐标、内孔、value、单位、reference、side、文字内容或指令类型。"
            + "dim/dimlinear/dimaligned只可改position；text/mtext可改position/height/rotation，mtext另可改width；"
            + "leader可改position/height/arrowSize；hatch可改spacing/scale/angle/lineweight；line/polyline只可改dashLength（实际最长实段）。"
            + "lineweight始终为纸面mm；角度为度；LTSCALE/DIMSCALE无量纲，不能把它们当作源长度。"
            + "setvars只可含LTSCALE、DIMSCALE、DIMTXT、DIMASZ、DIMTSZ、DIMEXO、DIMEXE、DIMGAP、DIMTAD、DIMTIH、DIMTOH；"
            + "有图框/块/多视图/中途改变样式时不提供全局setvars。不要修改DIMLFAC或安全设置，不添加源图没有的内容。";
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        // Optional diagnostics for controlled benchmarks; the normal UI does not
        // persist response bodies or install an observer.
        public Action<string, string> ResponseObserver { get; set; }
        public Action<Dictionary<string, object>> RequestObserver { get; set; }
        public string LastCompatibilityWarning { get; private set; }
        private static readonly object CapabilitySync = new object();
        private static readonly HashSet<string> NoTemperature = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> NoStreaming = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> NoReasoningEffort = new HashSet<string>(StringComparer.Ordinal);
        private sealed class ModelResponseException : InvalidOperationException
        { public ModelResponseException(string message) : base(message) { } }
        private static bool SupportsStreaming(string url, string model)
        { lock (CapabilitySync) return !NoStreaming.Contains(url + "\n" + model); }

        private static bool IncludeTemperature(string url, string model)
        {
            if (String.Equals((model ?? "").Trim(), "auto", StringComparison.OrdinalIgnoreCase)) return false;
            lock (CapabilitySync) return !NoTemperature.Contains(url + "\n" + model);
        }
        private static bool SupportsReasoningEffort(string url, string model)
        { lock (CapabilitySync) return !NoReasoningEffort.Contains(url + "\n" + model); }

        private string SerializeRequest(string url, ChatRequest body, out bool temperatureSent, out bool reasoningSent)
        {
            var payload = new Dictionary<string, object> { { "model", body.model }, { "messages", body.messages }, { "stream", body.stream } };
            temperatureSent = IncludeTemperature(url, body.model);
            if (temperatureSent) payload["temperature"] = body.temperature;
            reasoningSent = !String.IsNullOrEmpty(body.reasoning_effort) && SupportsReasoningEffort(url, body.model);
            if (reasoningSent) payload["reasoning_effort"] = body.reasoning_effort;
            return _serializer.Serialize(payload);
        }

        private void ObserveRequest(ChatRequest body, bool temperatureSent, bool reasoningSent)
        {
            var observer = RequestObserver;
            if (observer == null) return;
            try { observer(new Dictionary<string, object> { { "model", body.model }, { "stream", body.stream },
                { "temperature_sent", temperatureSent }, { "reasoning_effort_sent", reasoningSent },
                { "reasoning_effort", reasoningSent ? body.reasoning_effort : null } }); }
            catch (Exception ex) { throw new ModelResponseException("记录请求选项失败，未自动重复请求：" + ex.Message); }
        }

        private bool LearnReasoningRejection(string url, string model, int status, string json, bool sent)
        {
            if (!sent || (status != 400 && status != 422)) return false;
            var detail = ErrorMessage(json).ToLowerInvariant();
            if (!detail.Contains("reasoning_effort") || detail.Contains("unsupported value") || detail.Contains("supported values")
                || detail.Contains("must be one of") || detail.Contains("not support value") || detail.Contains("does not support the value")) return false;
            var fieldRejected = detail.Contains("unsupported parameter") || detail.Contains("unsupported_parameter")
                || detail.Contains("not support parameter") || detail.Contains("not support the parameter")
                || detail.Contains("reasoning_effort is not supported") || detail.Contains("'reasoning_effort' is not supported")
                || detail.Contains("\"reasoning_effort\" is not supported") || detail.Contains("reasoning_effort not supported")
                || detail.Contains("not support reasoning_effort");
            if (!fieldRejected) return false;
            lock (CapabilitySync)
            {
                if (NoReasoningEffort.Count >= 128) NoReasoningEffort.Clear();
                NoReasoningEffort.Add(url + "\n" + model);
            }
            LastCompatibilityWarning = "当前服务不支持所选推理预算，本次使用服务默认设置。";
            DiagnosticsLog.Write("请求参数兼容：服务明确拒绝 reasoning_effort，省略该参数并保持模型和消息重试一次。");
            return true;
        }
        private bool LearnTemperatureRejection(string url, string model, int status, string json, bool temperatureSent)
        {
            if (!temperatureSent || (status != 400 && status != 422)) return false;
            var detail = ErrorMessage(json).ToLowerInvariant();
            if (!detail.Contains("temperature") || !(detail.Contains("not support") || detail.Contains("unsupported") || detail.Contains("does not support"))) return false;
            lock (CapabilitySync)
            {
                if (NoTemperature.Count >= 128) NoTemperature.Clear();
                NoTemperature.Add(url + "\n" + model);
            }
            DiagnosticsLog.Write("请求参数兼容：服务拒绝 temperature，已省略该参数并保留原模型与消息重试一次。");
            return true;
        }

        public Task<string> CompleteAsync(ModelConfiguration model, IReadOnlyList<ChatItem> history, string drawingContext, string imagePath)
        {
            return CompleteAsync(model, history, drawingContext, imagePath, null);
        }

        public async Task<string> CompletePlanPatchAsync(ModelConfiguration model, string original, string review,
            string imagePath, CancellationToken cancellation)
        {
            LastCompatibilityWarning = null;
            if (String.IsNullOrWhiteSpace(original) || original.Length > 32000 || String.IsNullOrWhiteSpace(review) || review.Length > 12000)
                throw new InvalidOperationException("局部修正需要完整原计划与复核依据；当前上限为32000/12000字符，不会截断上下文。");
            if (!ModelConfiguration.IsValidReasoningEffort(model.ReasoningEffort)) throw new InvalidOperationException("无效的推理预算设置。");
            var effort = ModelConfiguration.NormalizeReasoningEffort(model.ReasoningEffort);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(imagePath == null ? 120 : 240));
                var token = deadline.Token;
                token.ThrowIfCancellationRequested();
                var validation = Task.Run(delegate
                {
                    PreparedCadPlan.Create(original);
                    if (imagePath != null)
                    {
                        var contract = CadPlanQuality.ImageContractError(original);
                        if (contract != null) throw new InvalidOperationException(contract);
                    }
                    return CadPlanPatch.Context(original);
                }, token);
                var content = imagePath == null ? Task.FromResult<object>(null)
                    : Task.Run(() => BuildImageContent("", imagePath, token, false), token);
                await Task.WhenAll(new Task[] { validation, content }).ConfigureAwait(false);
                var prompt = "复核依据（数据）：\n" + review + "\n原计划的完整可编辑目标摘要（数据，只输出受限补丁）：\n" + validation.Result;
                object userContent = prompt;
                if (imagePath != null)
                {
                    var parts = (object[])content.Result;
                    ((Dictionary<string, object>)parts[0])["text"] = prompt;
                    userContent = parts;
                }
                var messages = new List<ChatMessage> {
                    new ChatMessage { role="system", content=String.IsNullOrWhiteSpace(model.SystemPrompt) ? "你是AutoCAD计划复核助手。" : model.SystemPrompt },
                    new ChatMessage { role="system", content=PlanPatchProtocol },
                    new ChatMessage { role="user", content=userContent }
                };
                var url = model.UseFullUrl ? model.RequestUrl.Trim() : model.RequestUrl.Trim().TrimEnd('/') + "/chat/completions";
                if (effort != null && !SupportsReasoningEffort(url, model.ModelId))
                    LastCompatibilityWarning = "当前服务不支持所选推理预算，本次使用服务默认设置。";
                var response = await PostWithRetryAsync(url, SecretProtector.Unprotect(model.ApiKeyProtected),
                    new ChatRequest { model=model.ModelId, messages=messages, temperature=.1, stream=false, reasoning_effort=effort },
                    imagePath == null ? 120 : 240, token).ConfigureAwait(false);
                if (!IsSuccess(response.status)) throw new InvalidOperationException("补丁请求失败（" + response.status + "）：" + ErrorMessage(response.json));
                var answer = ExtractContent(response.json);
                token.ThrowIfCancellationRequested();
                CadPlanPatch.Apply(original, CadPlanPatch.Extract(answer));
                return answer; // No CAD queue or drawing mutation, and no automatic full-plan fallback.
            }
        }

        public async Task<string> CompleteAsync(ModelConfiguration model, IReadOnlyList<ChatItem> history, string drawingContext, string imagePath, Action<string> onDelta)
        {
            return await CompleteAsync(model, history, drawingContext, imagePath, onDelta, CancellationToken.None, null).ConfigureAwait(false);
        }

        public async Task<string> CompleteAsync(ModelConfiguration model, IReadOnlyList<ChatItem> history, string drawingContext,
            string imagePath, Action<string> onDelta, CancellationToken cancellation, Action<string> progress, bool detailedImages = false)
        {
            // One budget covers preprocessing, retries, streaming fallback and the optional repair.
            LastCompatibilityWarning = null;
            if (!ModelConfiguration.IsValidReasoningEffort(model.ReasoningEffort)) throw new InvalidOperationException("无效的推理预算设置。");
            var effort = ModelConfiguration.NormalizeReasoningEffort(model.ReasoningEffort);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(imagePath != null ? 240 : 120));
                var token = deadline.Token;
                var url = model.UseFullUrl
                    ? model.RequestUrl.Trim()
                    : model.RequestUrl.Trim().TrimEnd('/') + "/chat/completions";
                var key = SecretProtector.Unprotect(model.ApiKeyProtected);
                if (effort != null && !SupportsReasoningEffort(url, model.ModelId))
                    LastCompatibilityWarning = "当前服务不支持所选推理预算，本次使用服务默认设置。";
                var messages = imagePath == null
                    ? BuildMessages(model, history, drawingContext, null)
                    : await Task.Run(() => BuildMessages(model, history, drawingContext, imagePath, token, detailedImages), token).ConfigureAwait(false);
                var timeoutSeconds = imagePath != null ? 240 : 120;
                var started = DateTime.Now;
                if (progress != null) progress(imagePath == null ? "正在生成…" : "正在读取高清图纸并生成几何计划…");

                if (onDelta != null && imagePath == null && SupportsStreaming(url, model.ModelId))
                {
                    var streamed = await TryStreamAsync(url, key, model.ModelId, messages, onDelta, timeoutSeconds, token, effort).ConfigureAwait(false);
                    if (streamed != null)
                    {
                        DiagnosticsLog.Write("请求完成 文本通道 状态=200 " + Elapsed(started) + "ms");
                        return await ValidateAndRepairAsync(streamed, url, key, model.ModelId, messages, imagePath != null, token, progress, effort).ConfigureAwait(false);
                    }
                }

                var body = new ChatRequest { model = model.ModelId, messages = messages, temperature = 0.2, stream = false, reasoning_effort = effort };
                var result = await PostWithRetryAsync(url, key, body, timeoutSeconds, token).ConfigureAwait(false);
                DiagnosticsLog.Write("请求完成 状态=" + result.status + " " + Elapsed(started) + "ms 模式=" + (imagePath != null ? "图片" : "文本"));

                if (!IsSuccess(result.status))
                {
                    var detail = ErrorMessage(result.json);
                    if (imagePath != null && (result.status == 400 || result.status == 415 || result.status == 422))
                        throw new InvalidOperationException("图片请求被拒绝（" + result.status + "）：" + detail + "。未执行绘图，也未改成纯文字请求。");
                    DiagnosticsLog.Write("请求失败 状态=" + result.status + " " + detail);
                    throw new InvalidOperationException("模型请求失败（" + result.status + "）：" + detail);
                }
                return await ValidateAndRepairAsync(ExtractContent(result.json), url, key, model.ModelId, messages, imagePath != null, token, progress, effort).ConfigureAwait(false);
            }
        }

        private async Task<string> ValidateAndRepairAsync(string answer, string url, string key, string model,
            List<ChatMessage> messages, bool image, CancellationToken token, Action<string> progress, string effort)
        {
            string plan = null;
            string error;
            try
            {
                plan = CadPlanText.Extract(answer);
                error = plan == null ? null : ValidatePlan(plan, image);
            }
            catch (Exception ex) { error = ex.Message; }
            if (error == null) return answer;
            var originalPlan = plan;
            if (plan != null) DiagnosticsLog.SavePlan(plan);
            if (progress != null) progress("尺寸或计划校验未通过，正在修正一次…");
            DiagnosticsLog.Write("计划自动修正：" + error);
            messages.Add(new ChatMessage { role = "assistant", content = answer });
            messages.Add(new ChatMessage { role = "user", content = "本地校验未通过，尚未执行。一次修正全部问题并返回完整唯一cad计划，勿删除尺寸或伪造文字规避校验。"
                + (image ? "图片计划须同时补全顶层sourceUnit(mm/cm/m)、coordinateUnit(source)、views(实际可见视图名)，按源单位给出坐标；单位依据图片和用户说明，不得猜测。" : "") + "\n" + error });
            var response = await PostWithRetryAsync(url, key, new ChatRequest { model = model, messages = messages, temperature = 0.1, reasoning_effort = effort }, 240, token).ConfigureAwait(false);
            if (!IsSuccess(response.status)) throw new InvalidOperationException("计划修正失败（" + response.status + "），未执行绘图。");
            var repaired = ExtractContent(response.json);
            plan = CadPlanText.Extract(repaired);
            error = plan == null ? "修正后缺少绘图计划。" : ValidatePlan(plan, image);
            if (error == null && originalPlan != null) error = CadGeometryReferences.RepairCoverageError(originalPlan, plan);
            if (error != null) { if (plan != null) DiagnosticsLog.SavePlan(plan); throw new InvalidOperationException("修正后校验仍未通过，未执行：" + error); }
            return repaired;
        }

        private static string ValidatePlan(string plan, bool image)
        {
            var error = CadPlanValidator.Validate(plan);
            if (!image) return error;
            // A geometric error must not hide missing source units/views from
            // the single permitted repair; report both on that same request.
            try
            {
                var contract = CadPlanQuality.ImageContractError(plan);
                if (contract != null) error = error == null ? contract : error + "\n" + contract;
            }
            catch { if (error == null) throw; }
            return error;
        }

        private async Task<PostResult> PostWithRetryAsync(string url, string key, ChatRequest body, int timeoutSeconds, CancellationToken token)
        {
            var compatibilityRetried = false; var reasoningRetried = false; var transientRetried = false;
            while (true)
            {
                var result = await PostAsync(url, key, body, timeoutSeconds, token).ConfigureAwait(false);
                var learned = false;
                if (!compatibilityRetried && LearnTemperatureRejection(url, body.model, result.status, result.json, result.temperatureSent))
                { compatibilityRetried = true; learned = true; }
                if (!reasoningRetried && LearnReasoningRejection(url, body.model, result.status, result.json, result.reasoningSent))
                { reasoningRetried = true; learned = true; }
                if (learned) continue;
                if (transientRetried || (result.status != 429 && result.status < 500)) return result;
                transientRetried = true;
                DiagnosticsLog.Write("请求重试 状态=" + result.status);
                await Task.Delay(1500, token).ConfigureAwait(false);
            }
        }

        private async Task<PostResult> PostAsync(string url, string key, ChatRequest body, int timeoutSeconds, CancellationToken token)
        {
            using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                cancellation.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                bool temperatureSent, reasoningSent;
                request.Content = new StringContent(SerializeRequest(url, body, out temperatureSent, out reasoningSent), Encoding.UTF8, "application/json");
                ObserveRequest(body, temperatureSent, reasoningSent);
                using (var response = await Http.SendAsync(request, cancellation.Token).ConfigureAwait(false))
                {
                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (ResponseObserver != null) ResponseObserver("http-" + (int)response.StatusCode, json);
                    return new PostResult { status = (int)response.StatusCode, json = json, temperatureSent = temperatureSent, reasoningSent = reasoningSent };
                }
            }
        }

        /// <summary>Accept SSE or JSON from this same response; never retry a partial answer.</summary>
        private async Task<string> TryStreamAsync(string url, string key, string modelId, List<ChatMessage> messages, Action<string> onDelta, int timeoutSeconds, CancellationToken token, string effort)
        {
            var emitted = false;
            using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cancellation.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                try
                {
                    var temperatureRetried = false; var reasoningRetried = false;
                    for (var attempt = 0; attempt < 3; attempt++)
                    using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                        var body = new ChatRequest { model = modelId, messages = messages, temperature = 0.2, stream = true, reasoning_effort = effort };
                        bool temperatureSent, reasoningSent;
                        request.Content = new StringContent(SerializeRequest(url, body, out temperatureSent, out reasoningSent), Encoding.UTF8, "application/json");
                        ObserveRequest(body, temperatureSent, reasoningSent);
                        using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token).ConfigureAwait(false))
                        {
                            var status = (int)response.StatusCode;
                            if (!IsSuccess(status))
                            {
                                var error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (ResponseObserver != null) ResponseObserver("stream-http-" + status, error);
                                var learned = false;
                                if (!temperatureRetried && LearnTemperatureRejection(url, modelId, status, error, temperatureSent)) { temperatureRetried = true; learned = true; }
                                if (!reasoningRetried && LearnReasoningRejection(url, modelId, status, error, reasoningSent)) { reasoningRetried = true; learned = true; }
                                if (learned) continue;
                                var detail = ErrorMessage(error);
                                var lower = detail.ToLowerInvariant();
                                if ((status == 400 || status == 406 || status == 415 || status == 422 || status == 501)
                                    && lower.Contains("stream") && (lower.Contains("not support") || lower.Contains("unsupported")))
                                {
                                    lock (CapabilitySync)
                                    {
                                        if (NoStreaming.Count >= 128) NoStreaming.Clear();
                                        NoStreaming.Add(url + "\n" + modelId);
                                    }
                                    DiagnosticsLog.Write("模型明确不支持流式：本次改为普通请求，后续同一模型直接使用普通请求。");
                                    return null;
                                }
                                if (status != 429 && status < 500)
                                    throw new ModelResponseException("模型请求失败（" + status + "）：" + detail);
                                return null;
                            }
                            var builder = new StringBuilder();
                            var complete = false; var sawData = false;
                            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (cancellation.Token.Register(() => stream.Dispose()))
                            using (var reader = new System.IO.StreamReader(stream))
                            {
                                while (true)
                                {
                                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                                    if (line == null) break;
                                    var trimmed = line.TrimStart();
                                    // Some compatible services ignore stream=true and return a full JSON reply.
                                    if (!sawData && (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal)))
                                    {
                                        var json = line + "\n" + await reader.ReadToEndAsync().ConfigureAwait(false);
                                        if (ResponseObserver != null) ResponseObserver("stream-json", json);
                                        string answer;
                                        try { answer = ExtractContent(json); }
                                        catch (Exception ex) { throw new ModelResponseException(ex.Message); }
                                        cancellation.Token.ThrowIfCancellationRequested();
                                        emitted = true; onDelta(answer);
                                        DiagnosticsLog.Write("流式请求收到普通JSON，直接使用本次结果，未重复请求。");
                                        return answer;
                                    }
                                    if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                                    var data = trimmed.Substring(5).Trim();
                                    if (data.Length == 0) continue;
                                    sawData = true;
                                    if (data == "[DONE]") { complete = true; break; }
                                    if (ResponseObserver != null) ResponseObserver("sse", data);
                                    string finish;
                                    var delta = ExtractDelta(data, out finish);
                                    ValidateFinishReason(finish);
                                    if (finish == "stop") complete = true;
                                    if (String.IsNullOrEmpty(delta)) continue;
                                    if (builder.Length + delta.Length > 8 * 1024 * 1024)
                                        throw new ModelResponseException("模型输出超过本地长度上限，未执行。");
                                    builder.Append(delta); emitted = true; onDelta(delta);
                                }
                            }
                            if (sawData && !complete) throw new ModelResponseException("流式连接提前结束，未执行不完整的绘图计划。请重新发送。");
                            var text = builder.ToString().Trim();
                            if (text.Length == 0 && sawData) throw new ModelResponseException("模型返回了空内容。");
                            return text.Length == 0 ? null : text;
                        }
                    }
                    return null;
                }
                catch (OperationCanceledException) { throw; }
                catch (ModelResponseException) { throw; }
                catch (Exception ex)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (emitted) throw new ModelResponseException("流式输出中断，未执行，也未自动重复请求：" + ex.Message);
                    DiagnosticsLog.Write("流式连接失败，回退非流式：" + ex.Message);
                    return null;
                }
            }
        }

        private string ExtractDelta(string json, out string finish)
        {
            finish = null;
            Dictionary<string, object> map;
            try { map = _serializer.DeserializeObject(json) as Dictionary<string, object>; }
            catch { throw new ModelResponseException("流式数据格式错误，未执行。"); }
            if (map == null) throw new ModelResponseException("流式数据格式错误，未执行。");
            if (map.ContainsKey("error")) throw new ModelResponseException("模型流式请求失败：" + ErrorMessage(json));
            var choices = map.ContainsKey("choices") ? map["choices"] as object[] : null;
            if (choices == null || choices.Length == 0) return null;
            var choice = choices[0] as Dictionary<string, object>;
            if (choice == null) return null;
            if (choice.ContainsKey("finish_reason") && choice["finish_reason"] != null) finish = Convert.ToString(choice["finish_reason"]);
            var delta = choice.ContainsKey("delta") ? choice["delta"] as Dictionary<string, object> : null;
            return delta != null && delta.ContainsKey("content") && delta["content"] != null ? Convert.ToString(delta["content"]) : null;
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

        private static void ValidateFinishReason(string finish)
        {
            if (finish == "length") throw new ModelResponseException("模型输出被截断，未执行。请缩小绘图范围或提高服务输出上限。");
            if (!String.IsNullOrEmpty(finish) && finish != "stop")
                throw new ModelResponseException("模型未正常完成输出（" + finish + "），未执行。");
        }

        private string ExtractContent(string json)
        {
            var result = _serializer.Deserialize<ChatResponse>(json);
            if (result == null || result.choices == null || result.choices.Count == 0 || result.choices[0].message == null)
            {
                throw new InvalidOperationException("模型返回了空内容。");
            }
            ValidateFinishReason(result.choices[0].finish_reason);
            var raw = result.choices[0].message.content;
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
            public string reasoning_effort { get; set; }
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
            public bool temperatureSent { get; set; }
            public bool reasoningSent { get; set; }
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
