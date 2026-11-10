/*
 * 文件说明：DeepSeekClient.cs
 * 职责角色：本文件负责封装与 DeepSeek 官方接口（https://api.deepseek.com/chat/completions）的全部通信逻辑，
 *           以及基于 Windows 当前用户凭据（DPAPI）的 API 密钥安全存取服务（KeyStore）。
 *           支持两类核心场景：
 *           1. 截图/粘贴文字查题：返回带【答案】与【解析】的文本结果；
 *           2. 网页整卷结构化自动答题：按题号、题型、选项与填空数发送整份试卷，解析并校验结构化 JSON 答案。
 */
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace XuexitongDesktop
{
    /// <summary>
    /// 试卷单题的数据结构，供网页提取器与 AI 答题客户端传输整份试卷使用。
    /// </summary>
    internal sealed class PaperQuestion
    {
        public int Id { get; set; }
        public string PageIndex { get; set; }
        public string QuestionType { get; set; } // single_choice, multi_choice, true_false, fill_blank, short_answer, unknown
        public string TypeCn { get; set; }
        public string Stem { get; set; }
        public List<PaperOption> Options { get; set; }
        public int BlankCount { get; set; }
        public bool HasUnsupportedMediaOrAttachment { get; set; }
        public string UnsupportedReason { get; set; }

        public PaperQuestion()
        {
            PageIndex = "0";
            QuestionType = "";
            TypeCn = "";
            Stem = "";
            Options = new List<PaperOption>();
            UnsupportedReason = "";
        }
    }

    /// <summary>
    /// 选择题/判断题的单个选项数据结构。
    /// </summary>
    internal sealed class PaperOption
    {
        public string Key { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// AI 返回的单题结构化答案。
    /// </summary>
    internal sealed class PaperAnswer
    {
        public int Id { get; set; }
        public string Type { get; set; }
        public List<string> Choices { get; set; }
        public List<string> Blanks { get; set; }
        public string Text { get; set; }
        public string Analysis { get; set; }

        public PaperAnswer()
        {
            Type = "";
            Choices = new List<string>();
            Blanks = new List<string>();
            Text = "";
            Analysis = "";
        }
    }

    /// <summary>
    /// 使用 Windows DPAPI（DataProtectionScope.CurrentUser）加密保存与读取用户的 DeepSeek API 密钥。
    /// 同时兼容软件目录下的「用户数据/key.dat」与系统 LocalAppData 目录，避免目录权限异常导致密钥丢失。
    /// </summary>
    internal static class KeyStore
    {
        private static string GetLocalAppDataKeyPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XuexitongDesktop", "key.dat");
        }

        private static string GetAppDirKeyPath()
        {
            return Path.Combine(AppPaths.BaseDir, "用户数据", "key.dat");
        }

        /// <summary>
        /// 读取已保存的 API 密钥；若未保存则返回空字符串。
        /// </summary>
        internal static string LoadKey()
        {
            string[] candidates = new[] { GetAppDirKeyPath(), GetLocalAppDataKeyPath() };
            foreach (string path in candidates)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        byte[] encrypted = File.ReadAllBytes(path);
                        byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                        string key = Encoding.UTF8.GetString(plain).Trim();
                        if (!String.IsNullOrWhiteSpace(key))
                        {
                            return key;
                        }
                    }
                }
                catch (Exception)
                {
                    // 单个路径解密或读取失败时继续尝试备用路径
                }
            }
            return "";
        }

        /// <summary>
        /// 使用当前 Windows 用户凭据加密保存 API 密钥。
        /// </summary>
        internal static void SaveKey(string rawKey)
        {
            if (String.IsNullOrWhiteSpace(rawKey)) return;
            byte[] encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(rawKey.Trim()),
                null,
                DataProtectionScope.CurrentUser);

            bool savedAny = false;
            Exception lastError = null;
            foreach (string path in new[] { GetAppDirKeyPath(), GetLocalAppDataKeyPath() })
            {
                try
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!String.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllBytes(path, encrypted);
                    savedAny = true;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }
            if (!savedAny && lastError != null)
            {
                throw new InvalidOperationException("无法保存加密密钥：" + lastError.Message);
            }
        }
    }

    /// <summary>
    /// DeepSeek API 客户端，支持普通查题与整卷结构化 JSON 答题。
    /// </summary>
    internal static class DeepSeekClient
    {
        /// <summary>
        /// 基础文本提问方法。保持与离线模拟处理器（接口检查.cs）100% 兼容。
        /// </summary>
        internal static async Task<string> Ask(HttpClient client, string key, string question)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var body = json.Serialize(new
            {
                model = "deepseek-flash",
                thinking = new { type = "disabled" },
                messages = new[] { new { role = "user", content = question } }
            });
            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/chat/completions"))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (var response = await client.SendAsync(request))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("服务返回 HTTP " + (int)response.StatusCode);

                    // 结构异常时给出明确提示，避免把不完整返回当成有效答案。
                    Dictionary<string, object> data;
                    try
                    {
                        data = json.Deserialize<Dictionary<string, object>>(await response.Content.ReadAsStringAsync());
                    }
                    catch (ArgumentException)
                    {
                        throw new InvalidOperationException("服务返回的数据格式不正确");
                    }

                    object value;
                    if (data == null || !data.TryGetValue("choices", out value))
                        throw new InvalidOperationException("服务未返回答案列表");
                    var choices = value as IList;
                    if (choices == null || choices.Count == 0)
                        throw new InvalidOperationException("服务未返回答案列表");
                    var choice = choices[0] as Dictionary<string, object>;
                    if (choice != null && choice.TryGetValue("finish_reason", out value) && Convert.ToString(value) != "stop")
                        throw new InvalidOperationException("答案未正常生成完成，请重试");
                    if (choice == null || !choice.TryGetValue("message", out value))
                        throw new InvalidOperationException("服务未返回答案内容");
                    var message = value as Dictionary<string, object>;
                    if (message == null || !message.TryGetValue("content", out value))
                        throw new InvalidOperationException("服务未返回答案内容");
                    var content = Convert.ToString(value);
                    if (String.IsNullOrWhiteSpace(content))
                        throw new InvalidOperationException("服务返回空答案");
                    return content;
                }
            }
        }

        /// <summary>
        /// 截图或手动粘贴查题专用：要求 DeepSeek 同时返回清晰的【答案】与【解析】。
        /// </summary>
        internal static Task<string> AskWithAnalysis(HttpClient client, string key, string rawQuestion)
        {
            string prompt = "请解答以下题目，并严格按照如下格式输出：\n【答案】：给出明确答案\n【解析】：给出简明扼要的解题分析\n\n题目内容：\n" + rawQuestion;
            return Ask(client, key, prompt);
        }

        /// <summary>
        /// 整卷结构化答题请求：将读取到的整份试卷发送给 DeepSeek（当题目超过 15 道时自动分批请求以防超长截断），要求按题号返回严格 JSON 格式的答案。
        /// </summary>
        internal static async Task<List<PaperAnswer>> AskStructuredPaper(HttpClient client, string key, List<PaperQuestion> questions, Action<string> onProgress = null)
        {
            if (questions == null || questions.Count == 0)
                throw new InvalidOperationException("没有可发送给 AI 的题目。");

            const int batchSize = 15;
            if (questions.Count <= batchSize)
            {
                return await AskSingleBatchStructuredPaper(client, key, questions);
            }

            var allAnswers = new List<PaperAnswer>();
            int totalBatches = (questions.Count + batchSize - 1) / batchSize;
            for (int b = 0; b < totalBatches; b++)
            {
                int start = b * batchSize;
                int count = Math.Min(batchSize, questions.Count - start);
                var batch = questions.GetRange(start, count);
                if (onProgress != null)
                {
                    onProgress(String.Format(
                        "正在请求 DeepSeek AI 作答（第 {0}/{1} 批，题号 {2}~{3}）……",
                        b + 1, totalBatches, batch[0].Id, batch[batch.Count - 1].Id));
                }
                var part = await AskSingleBatchStructuredPaper(client, key, batch);
                allAnswers.AddRange(part);
            }
            return allAnswers;
        }

        private static async Task<List<PaperAnswer>> AskSingleBatchStructuredPaper(HttpClient client, string key, List<PaperQuestion> questions)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var items = new List<object>();
            foreach (var q in questions)
            {
                var opts = new List<object>();
                if (q.Options != null)
                {
                    foreach (var o in q.Options)
                    {
                        opts.Add(new { key = o.Key, text = o.Text });
                    }
                }
                items.Add(new
                {
                    id = q.Id,
                    type = q.QuestionType,
                    type_cn = q.TypeCn,
                    stem = q.Stem,
                    blank_count = q.BlankCount,
                    options = opts
                });
            }

            string questionsJson = serializer.Serialize(items);
            string prompt =
                "你是一个严谨的在线课程测试答题助手。请根据下方提供的 JSON 题目列表作答，并只输出一个合法的 JSON 对象（不要使用 Markdown 代码块包裹，不要输出任何额外解释文字）。\n" +
                "输出格式要求：\n" +
                "{\"answers\":[{\"id\":1,\"type\":\"single_choice\",\"choices\":[\"A\"],\"blanks\":[],\"text\":\"\"}]}\n" +
                "规则：\n" +
                "1. answers 数组长度必须与输入题目数量完全一致，id 必须与输入题号一一对应。\n" +
                "2. single_choice（单选题）和 true_false（判断题）：choices 数组必须且只能包含 1 个选项字母（如 [\"A\"] 或 [\"B\"]）。\n" +
                "3. multi_choice（多选题）：choices 数组包含所有正确选项字母（如 [\"A\",\"C\"]）。\n" +
                "4. fill_blank（填空题）：blanks 数组长度必须严格等于该题的 blank_count，按顺序给出每一空的答案文字（如 [\"第一空答案\",\"第二空答案\"]）。\n" +
                "5. short_answer（简答题）：text 字段填写完整、通顺的简答正文。\n\n" +
                "题目列表 JSON：\n" + questionsJson;

            string rawContent = await Ask(client, key, prompt);
            return ParseStructuredAnswers(rawContent);
        }

        /// <summary>
        /// 解析 AI 返回的结构化 JSON 字符串为答案列表，支持自动剥离可能附带的 Markdown 代码块标记。
        /// </summary>
        internal static List<PaperAnswer> ParseStructuredAnswers(string rawContent)
        {
            if (String.IsNullOrWhiteSpace(rawContent))
                throw new InvalidOperationException("AI 返回内容为空。");

            string cleaned = rawContent.Trim();
            if (cleaned.StartsWith("```"))
            {
                int firstNewLine = cleaned.IndexOf('\n');
                if (firstNewLine >= 0) cleaned = cleaned.Substring(firstNewLine + 1).Trim();
                if (cleaned.EndsWith("```")) cleaned = cleaned.Substring(0, cleaned.Length - 3).Trim();
            }
            int firstBrace = cleaned.IndexOf('{');
            int lastBrace = cleaned.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                cleaned = cleaned.Substring(firstBrace, lastBrace - firstBrace + 1);
            }

            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Dictionary<string, object> root;
            try
            {
                root = serializer.Deserialize<Dictionary<string, object>>(cleaned);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("AI 返回的 JSON 格式无效：" + ex.Message);
            }

            object answersObj;
            if (root == null || !root.TryGetValue("answers", out answersObj))
                throw new InvalidOperationException("AI 返回 JSON 缺少 answers 字段。");

            var list = answersObj as IList;
            if (list == null)
                throw new InvalidOperationException("AI 返回的 answers 字段不是数组。");

            var result = new List<PaperAnswer>();
            foreach (object itemObj in list)
            {
                var item = itemObj as Dictionary<string, object>;
                if (item == null) throw new InvalidOperationException("AI 返回的答案项格式不正确。");

                var ans = new PaperAnswer();
                object val;
                if (!item.TryGetValue("id", out val) || val == null)
                    throw new InvalidOperationException("AI 返回的答案缺少题号 id。");
                ans.Id = Convert.ToInt32(val);

                if (item.TryGetValue("type", out val) && val != null)
                    ans.Type = Convert.ToString(val).Trim();

                if (item.TryGetValue("choices", out val) && val is IList)
                {
                    foreach (object c in (IList)val)
                    {
                        string letter = Convert.ToString(c).Trim().ToUpperInvariant();
                        if (!String.IsNullOrEmpty(letter)) ans.Choices.Add(letter);
                    }
                }
                else if (item.TryGetValue("letters", out val) && val is IList)
                {
                    foreach (object c in (IList)val)
                    {
                        string letter = Convert.ToString(c).Trim().ToUpperInvariant();
                        if (!String.IsNullOrEmpty(letter)) ans.Choices.Add(letter);
                    }
                }

                if (item.TryGetValue("blanks", out val) && val is IList)
                {
                    foreach (object b in (IList)val)
                    {
                        ans.Blanks.Add(Convert.ToString(b ?? "").Trim());
                    }
                }

                if (item.TryGetValue("text", out val) && val != null)
                    ans.Text = Convert.ToString(val).Trim();

                if (item.TryGetValue("analysis", out val) && val != null)
                    ans.Analysis = Convert.ToString(val).Trim();

                result.Add(ans);
            }
            return result;
        }
    }
}
