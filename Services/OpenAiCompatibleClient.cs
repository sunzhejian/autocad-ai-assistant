using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using AutoCAD.AI.Assistant.Models;

namespace AutoCAD.AI.Assistant.Services
{
    internal sealed class OpenAiCompatibleClient
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();

        public async Task<string> CompleteAsync(ModelConfiguration model, IReadOnlyList<ChatItem> history)
        {
            var messages = new List<ChatMessage>();
            var systemPrompt = string.IsNullOrWhiteSpace(model.SystemPrompt)
                ? "你是 AutoCAD 2024 中文助手。给出清晰、可操作的绘图步骤和 CAD 命令；不要声称已经执行了命令。"
                : model.SystemPrompt;
            messages.Add(new ChatMessage { role = "system", content = systemPrompt });

            foreach (var item in history)
            {
                messages.Add(new ChatMessage
                {
                    role = item.IsUser ? "user" : "assistant",
                    content = item.Content
                });
            }

            var requestBody = new ChatRequest
            {
                model = model.ModelId,
                messages = messages,
                temperature = 0.2,
                stream = false
            };
            var url = model.UseFullUrl
                ? model.RequestUrl.Trim()
                : model.RequestUrl.Trim().TrimEnd('/') + "/chat/completions";

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", SecretProtector.Unprotect(model.ApiKeyProtected));
                request.Content = new StringContent(_serializer.Serialize(requestBody), Encoding.UTF8, "application/json");

                using (var response = await Http.SendAsync(request).ConfigureAwait(false))
                {
                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var result = _serializer.Deserialize<ChatResponse>(json);
                    if (!response.IsSuccessStatusCode)
                    {
                        var detail = result != null && result.error != null ? result.error.message : json;
                        throw new InvalidOperationException("模型请求失败（" + (int)response.StatusCode + "）：" + detail);
                    }

                    string content = null;
                    if (result != null && result.choices != null && result.choices.Count > 0 && result.choices[0].message != null)
                    {
                        content = result.choices[0].message.content;
                    }
                    if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("模型返回了空内容。");
                    return content.Trim();
                }
            }
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
            public string content { get; set; }
        }

        private sealed class ChatResponse
        {
            public List<ChatChoice> choices { get; set; }
            public ApiError error { get; set; }
        }

        private sealed class ChatChoice
        {
            public ChatMessage message { get; set; }
        }

        private sealed class ApiError
        {
            public string message { get; set; }
        }
    }
}
