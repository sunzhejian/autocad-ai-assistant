using System;
using System.Collections.Generic;
using AutoCAD.AI.Assistant.Models;

namespace AutoCAD.AI.Assistant.Services
{
    internal static class ChatRetention
    {
        public const int VisibleLimit = 40;
        public const int HistoryLimit = 12;
        public static void Append(IList<ChatItem> list, ChatItem item, int limit)
        {
            list.Add(item);
            while (list.Count > limit) list.RemoveAt(0);
        }
        public static string Display(string content)
        {
            if (content == null || content.Length <= 8000) return content;
            return content.Substring(0, 8000) + "\n（长内容仅展示前 8000 字符，模型上下文与本地计划保留原文。）";
        }
    }
}
