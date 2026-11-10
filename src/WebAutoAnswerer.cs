/*
 * 文件说明：WebAutoAnswerer.cs
 * 职责角色：本文件实现学习通「章节测验、作业、考试」三类页面的网页答题和填写核对，同时兼容：
 *           - 传统选择器页面（.Py-mian1、.TiMu、.questionLi、.ans-cc-exam 等）与新版无固定类名页面（TreeWalker 题型标签回溯卡片 + 圆圈字母选项）；
 *           - 同源 iframe 与跨子域/跨域多层嵌套 iframe（通过 WebViewFrameRegistry 遍历所有 CoreWebView2Frame）；
 *           - 大题量试卷自动分批请求 DeepSeek（如 40 道题自动拆批请求，防超长截断）。
 *           将流程拆分为四个阶段：
 *           1. 整卷读取与完整性校验（ExtractWholePaperAsync & ValidatePaperExtraction）；
 *           2. AI 结构化作答与格式校验（ValidateAiAnswers）；
 *           3. 自动填写（FillPaperAnswersAsync）；
 *           4. 逐题读回核对，停在页面上供用户检查（VerifyPaperReadBackAsync）。
 */
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace XuexitongDesktop
{
    /// <summary>
    /// 整份试卷的读取结果。
    /// </summary>
    internal sealed class PaperExtractionResult
    {
        public string PaperId { get; set; }
        public string PageType { get; set; } // chapter_test, homework, exam
        public string PageTypeCn { get; set; }
        public bool IsPaginated { get; set; }
        public int ExpectedTotalCount { get; set; }
        public int PageCount { get; set; }
        public bool AlreadyCompletedOnPlatform { get; set; }
        public List<PaperQuestion> Questions { get; set; }
        internal CoreWebView2Frame TargetFrame { get; set; }

        public PaperExtractionResult()
        {
            PaperId = "";
            PageType = "";
            PageTypeCn = "";
            PageCount = 1;
            Questions = new List<PaperQuestion>();
        }
    }

    /// <summary>
    /// 答题填写与核对的最终状态。
    /// </summary>
    internal enum AutoAnswerStatus
    {
        Succeeded,
        AlreadySubmittedOrCompleted,
        PendingVerification,
        BlockedBeforeSubmit
    }

    /// <summary>
    /// 完整自动答题工作流的执行报告。
    /// </summary>
    internal sealed class AutoAnswerReport
    {
        public AutoAnswerStatus Status { get; set; }
        public string PaperId { get; set; }
        public string PageTypeCn { get; set; }
        public int TotalQuestions { get; set; }
        public int FilledCount { get; set; }
        public int VerifiedCount { get; set; }
        public string SummaryMessage { get; set; }
        public List<string> Details { get; set; }

        public AutoAnswerReport()
        {
            PaperId = "";
            PageTypeCn = "";
            SummaryMessage = "";
            Details = new List<string>();
        }
    }

    /// <summary>
    /// 读取旧版的提交记录，避免覆盖已提交或提交结果待核对的答卷。
    /// </summary>
    internal static class SubmissionLedger
    {
        private static string GetLedgerPath()
        {
            string dir = Path.Combine(AppPaths.BaseDir, "用户数据");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "已提交试卷账本.json");
        }

        internal static string GetStatus(string paperId)
        {
            if (String.IsNullOrWhiteSpace(paperId)) return null;
            var map = LoadAll();
            string status;
            return map.TryGetValue(paperId, out status) ? status : null;
        }

        internal static void ClearForTesting(string paperId = null)
        {
            string path = GetLedgerPath();
            if (!File.Exists(path)) return;
            if (paperId == null)
            {
                File.Delete(path);
                return;
            }
            var map = LoadAll();
            if (map.Remove(paperId))
            {
                var serializer = new JavaScriptSerializer();
                File.WriteAllText(path, serializer.Serialize(map), Encoding.UTF8);
            }
        }

        private static Dictionary<string, string> LoadAll()
        {
            string path = GetLedgerPath();
            if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                var serializer = new JavaScriptSerializer();
                var raw = serializer.Deserialize<Dictionary<string, object>>(json);
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (raw != null)
                {
                    foreach (var kv in raw)
                    {
                        result[kv.Key] = Convert.ToString(kv.Value ?? "");
                    }
                }
                return result;
            }
            catch (Exception)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// 网页自动答题核心引擎。
    /// </summary>
    internal static class WebAutoAnswerer
    {
        /// <summary>
        /// 读题、作答并填写，逐题读回核对后停在答题页面。
        /// </summary>
        internal static async Task<AutoAnswerReport> ExecuteFullWorkflowAsync(
            WebView2 browser,
            HttpClient httpClient,
            string apiKey,
            Action<string> onProgress = null)
        {
            var report = new AutoAnswerReport();
            Action<string> reportStep = delegate(string msg)
            {
                report.Details.Add(msg);
                if (onProgress != null) onProgress(msg);
            };

            if (!CoursePlayer.IsValidChaoxingUrl(browser))
            {
                report.Status = AutoAnswerStatus.BlockedBeforeSubmit;
                report.SummaryMessage = "请先打开学习通的章节测验、作业或考试页面。";
                return report;
            }

            if (String.IsNullOrWhiteSpace(apiKey))
            {
                report.Status = AutoAnswerStatus.BlockedBeforeSubmit;
                report.SummaryMessage = "请先在「截图/文字查题与设置」中填写并保存 DeepSeek API 密钥。";
                return report;
            }

            try
            {
                // 第一步：读取整份试卷（含跨域子框架穿透、滚动唤醒与分页遍历）
                reportStep("正在读取整份试卷题目并识别页面类型……");
                PaperExtractionResult paper = await ExtractWholePaperAsync(browser);
                report.PaperId = paper.PaperId;
                report.PageTypeCn = paper.PageTypeCn;
                report.TotalQuestions = paper.ExpectedTotalCount;

                if (paper.AlreadyCompletedOnPlatform)
                {
                    report.Status = AutoAnswerStatus.AlreadySubmittedOrCompleted;
                    report.SummaryMessage = String.Format("当前{0}在平台上已处于「已完成/已提交」状态，无需重复作答。", paper.PageTypeCn);
                    return report;
                }

                // 旧版本可能已尝试提交，先避免改动该答卷。
                string previousLedgerStatus = SubmissionLedger.GetStatus(paper.PaperId);
                if (previousLedgerStatus == "PendingVerification")
                {
                    report.Status = AutoAnswerStatus.PendingVerification;
                    report.SummaryMessage = "该试卷先前的提交结果待核对，请先在学习通页面检查状态。";
                    return report;
                }
                if (previousLedgerStatus == "Confirmed")
                {
                    report.Status = AutoAnswerStatus.AlreadySubmittedOrCompleted;
                    report.SummaryMessage = "该试卷已有确认提交记录，无需重复填写。";
                    return report;
                }

                // 核对整份试卷题目完整性与可作答性
                ValidatePaperExtraction(paper);
                reportStep(String.Format("已完整读取{0}共 {1} 道题（分页：{2}），正在请求 DeepSeek AI 作答……",
                    paper.PageTypeCn, paper.Questions.Count, paper.IsPaginated ? (paper.PageCount + "页") : "整卷"));

                // 第二步：调用 DeepSeek 获取结构化答案（超过 15 题时自动分批请求）并严格校验答案格式与数量
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                List<PaperAnswer> aiAnswers = await DeepSeekClient.AskStructuredPaper(
                    httpClient,
                    apiKey,
                    paper.Questions,
                    delegate(string batchMsg) { if (onProgress != null) onProgress(batchMsg); });
                Dictionary<int, PaperAnswer> answerMap = ValidateAiAnswers(paper, aiAnswers);
                reportStep(String.Format("AI 已返回全部 {0} 道题的结构化答案，格式校验通过，开始自动填写……", answerMap.Count));

                // 第三步：自动填写答案（支持整卷与分页试卷）
                int filled = await FillPaperAnswersAsync(browser, paper, answerMap);
                report.FilledCount = filled;
                reportStep(String.Format("已完成 {0}/{1} 道题的填写动作，开始执行逐题读回核对……", filled, paper.ExpectedTotalCount));

                // 第四步：逐题读回核对（从 DOM 实际状态独立回读，确保选择题选中项、多空填空每个输入框、简答编辑器正文 100% 一致）
                int verified = await VerifyPaperReadBackAsync(browser, paper, answerMap);
                report.VerifiedCount = verified;
                if (verified != paper.ExpectedTotalCount)
                {
                    throw new InvalidOperationException(String.Format(
                        "整卷读回核对未完全通过（预期 {0} 题，实际核对通过 {1} 题）。",
                        paper.ExpectedTotalCount, verified));
                }
                report.Status = AutoAnswerStatus.Succeeded;
                report.SummaryMessage = String.Format("已填写并核对 {0} 道题，未提交。请在页面上检查答案。", verified);
                reportStep(report.SummaryMessage);
                return report;
            }
            catch (Exception ex)
            {
                report.Status = AutoAnswerStatus.BlockedBeforeSubmit;
                report.SummaryMessage = "答题流程已停止，未提交。" + ex.Message;
                reportStep(report.SummaryMessage);
                return report;
            }
        }

        /// <summary>
        /// 在目标 Frame（优先）及顶层/全部存活子 Frame 中执行脚本，返回第一个满足 predicate 的反序列化字符串结果。
        /// </summary>
        private static async Task<string> ExecuteInFramesAsync(
            WebView2 browser,
            CoreWebView2Frame preferredFrame,
            string script,
            Predicate<string> acceptResult)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string lastResult = "";

            if (preferredFrame != null)
            {
                try
                {
                    if (preferredFrame.IsDestroyed() == 0)
                    {
                        string raw = await WebViewFrameRegistry.ExecuteCourseFrameScriptAsync(preferredFrame, script);
                        string val = serializer.Deserialize<string>(raw);
                        lastResult = val ?? "";
                        if (acceptResult(lastResult)) return lastResult;
                    }
                }
                catch (Exception)
                {
                }
            }

            try
            {
                string topRaw = await browser.ExecuteScriptAsync(script);
                string topVal = serializer.Deserialize<string>(topRaw);
                lastResult = topVal ?? "";
                if (acceptResult(lastResult)) return lastResult;
            }
            catch (Exception)
            {
            }

            foreach (var frame in WebViewFrameRegistry.GetAliveFrames(browser))
            {
                if (frame == preferredFrame) continue;
                try
                {
                    string fRaw = await WebViewFrameRegistry.ExecuteCourseFrameScriptAsync(frame, script);
                    string fVal = serializer.Deserialize<string>(fRaw);
                    lastResult = fVal ?? "";
                    if (acceptResult(lastResult)) return lastResult;
                }
                catch (Exception)
                {
                }
            }

            return lastResult;
        }

        /// <summary>
        /// 步骤 1：读取整份试卷（自动穿透同源与跨域子框架、唤醒懒加载题目，若为分页试卷则遍历所有分页后再回到第 1 页）。
        /// </summary>
        internal static async Task<PaperExtractionResult> ExtractWholePaperAsync(WebView2 browser)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string revealScript = BuildRevealQuestionsScript();
            try { await browser.ExecuteScriptAsync(revealScript); } catch (Exception) { }
            foreach (var f in WebViewFrameRegistry.GetAliveFrames(browser))
            {
                try { await WebViewFrameRegistry.ExecuteCourseFrameScriptAsync(f, revealScript); } catch (Exception) { }
            }
            await Task.Delay(120);

            string extractScript = BuildExtractCurrentViewScript();
            Dictionary<string, object> bestMeta = null;
            CoreWebView2Frame bestFrame = null;
            int bestQuestionCount = -1;

            // 1. 先检查顶层文档（含同源子孙 iframe）
            try
            {
                string rawTop = await browser.ExecuteScriptAsync(extractScript);
                string jsonTop = serializer.Deserialize<string>(rawTop);
                if (!String.IsNullOrEmpty(jsonTop))
                {
                    var mTop = serializer.Deserialize<Dictionary<string, object>>(jsonTop);
                    if (mTop != null)
                    {
                        var qList = mTop.ContainsKey("questions") ? mTop["questions"] as IList : null;
                        int qCount = qList != null ? qList.Count : 0;
                        bool completed = GetBool(mTop, "alreadyCompleted");
                        string pType = GetString(mTop, "pageType");
                        if (completed || (pType != "none" && qCount > bestQuestionCount))
                        {
                            bestMeta = mTop;
                            bestFrame = null;
                            bestQuestionCount = completed ? 999999 : qCount;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            // 2. 遍历所有子 CoreWebView2Frame（解决 mooc2-ans 嵌 mooc1 等跨域 iframe 无法通过 contentDocument 访问的问题）
            foreach (var frame in WebViewFrameRegistry.GetAliveFrames(browser))
            {
                try
                {
                    string rawFrame = await WebViewFrameRegistry.ExecuteCourseFrameScriptAsync(frame, extractScript);
                    string jsonFrame = serializer.Deserialize<string>(rawFrame);
                    if (String.IsNullOrEmpty(jsonFrame)) continue;
                    var mFrame = serializer.Deserialize<Dictionary<string, object>>(jsonFrame);
                    if (mFrame == null) continue;

                    var qList = mFrame.ContainsKey("questions") ? mFrame["questions"] as IList : null;
                    int qCount = qList != null ? qList.Count : 0;
                    bool completed = GetBool(mFrame, "alreadyCompleted");
                    string pType = GetString(mFrame, "pageType");
                    if (completed && bestQuestionCount < 999999)
                    {
                        bestMeta = mFrame;
                        bestFrame = frame;
                        bestQuestionCount = 999999;
                    }
                    else if (pType != "none" && qCount > bestQuestionCount)
                    {
                        bestMeta = mFrame;
                        bestFrame = frame;
                        bestQuestionCount = qCount;
                    }
                }
                catch (Exception)
                {
                }
            }

            if (bestMeta == null)
            {
                throw new InvalidOperationException("当前页面未检测到章节测验、作业或考试题目容器。");
            }

            var result = new PaperExtractionResult
            {
                PageType = GetString(bestMeta, "pageType"),
                PageTypeCn = GetString(bestMeta, "pageTypeCn"),
                IsPaginated = GetBool(bestMeta, "isPaginated"),
                ExpectedTotalCount = GetInt(bestMeta, "expectedTotalCount"),
                PageCount = Math.Max(1, GetInt(bestMeta, "pageCount")),
                AlreadyCompletedOnPlatform = GetBool(bestMeta, "alreadyCompleted"),
                TargetFrame = bestFrame
            };

            if (result.AlreadyCompletedOnPlatform)
            {
                result.PaperId = ComputePaperId(browser.Source != null ? browser.Source.ToString() : "", result.PageType, new List<PaperQuestion>());
                return result;
            }

            if (String.IsNullOrEmpty(result.PageType) || result.PageType == "none")
            {
                throw new InvalidOperationException("当前页面未检测到章节测验、作业或考试题目容器。");
            }

            var collectedById = new SortedDictionary<int, PaperQuestion>();

            if (!result.IsPaginated)
            {
                var pageQuestions = ParseQuestionsFromList(bestMeta.ContainsKey("questions") ? bestMeta["questions"] as IList : null);
                foreach (var q in pageQuestions)
                {
                    if (collectedById.ContainsKey(q.Id))
                        throw new InvalidOperationException(String.Format("页面题号重复（第 {0} 题），无法安全区分题目。", q.Id));
                    collectedById.Add(q.Id, q);
                }
            }
            else
            {
                // 分页试卷：依次切换到每一个分页读取题目，读取完毕后切回第一页
                for (int p = 0; p < result.PageCount; p++)
                {
                    string navStatus = await ExecuteInFramesAsync(
                        browser,
                        bestFrame,
                        BuildNavigatePageScript(p),
                        delegate(string s) { return s == "ok" || s == "nav_failed"; });
                    if (navStatus == "nav_failed")
                    {
                        // 某些分页可能缺失或无法打开，停止翻页并在后续完整性核对时报错
                        break;
                    }
                    await Task.Delay(120);
                    string pageJson = await ExecuteInFramesAsync(
                        browser,
                        bestFrame,
                        extractScript,
                        delegate(string s) { return !String.IsNullOrEmpty(s) && s.Contains("\"pageType\":") && !s.Contains("\"pageType\":\"none\""); });
                    if (!String.IsNullOrEmpty(pageJson))
                    {
                        var pageData = serializer.Deserialize<Dictionary<string, object>>(pageJson);
                        var qs = ParseQuestionsFromList(pageData != null && pageData.ContainsKey("questions") ? pageData["questions"] as IList : null);
                        foreach (var q in qs)
                        {
                            if (!collectedById.ContainsKey(q.Id))
                            {
                                q.PageIndex = p.ToString();
                                collectedById[q.Id] = q;
                            }
                        }
                    }
                }
                await ExecuteInFramesAsync(browser, bestFrame, BuildNavigatePageScript(0), delegate(string s) { return s == "ok"; });
            }

            result.Questions = new List<PaperQuestion>(collectedById.Values);
            if (result.ExpectedTotalCount <= 0)
            {
                result.ExpectedTotalCount = result.Questions.Count;
            }

            string rawPaperId = GetString(bestMeta, "explicitPaperId");
            result.PaperId = !String.IsNullOrWhiteSpace(rawPaperId)
                ? rawPaperId
                : ComputePaperId(browser.Source != null ? browser.Source.ToString() : "", result.PageType, result.Questions);

            return result;
        }

        /// <summary>
        /// 校验读取到的整份试卷是否完整、是否存在未知题型、图表缺失、附件作答或编辑器缺失。
        /// </summary>
        internal static void ValidatePaperExtraction(PaperExtractionResult paper)
        {
            if (paper == null || paper.Questions == null || paper.Questions.Count == 0)
                throw new InvalidOperationException("未读取到任何有效题目。");

            if (paper.ExpectedTotalCount <= 0)
                throw new InvalidOperationException("无法确认试卷总题数。");

            if (paper.Questions.Count != paper.ExpectedTotalCount)
            {
                throw new InvalidOperationException(String.Format(
                    "试卷读取不完整：页面标明共 {0} 道题，实际仅读取到 {1} 道题（可能漏读分页或部分题目未加载）。",
                    paper.ExpectedTotalCount, paper.Questions.Count));
            }

            foreach (var q in paper.Questions)
            {
                if (q.HasUnsupportedMediaOrAttachment)
                {
                    throw new InvalidOperationException(String.Format(
                        "第 {0} 题无法自动作答：{1}。",
                        q.Id, String.IsNullOrWhiteSpace(q.UnsupportedReason) ? "包含图表缺失、附件作答或无法识别的编辑器" : q.UnsupportedReason));
                }

                if (String.IsNullOrWhiteSpace(q.QuestionType) || q.QuestionType == "unknown")
                {
                    throw new InvalidOperationException(String.Format(
                        "第 {0} 题为不支持的题型（{1}），仅支持单选、多选、判断、文字填空和文字简答。",
                        q.Id, String.IsNullOrWhiteSpace(q.TypeCn) ? "未知题型" : q.TypeCn));
                }

                if (String.IsNullOrWhiteSpace(q.Stem))
                {
                    throw new InvalidOperationException(String.Format("第 {0} 题题干文字缺失。", q.Id));
                }

                if ((q.QuestionType == "single_choice" || q.QuestionType == "multi_choice" || q.QuestionType == "true_false") &&
                    (q.Options == null || q.Options.Count < 2))
                {
                    throw new InvalidOperationException(String.Format("第 {0} 题（{1}）选项读取不完整。", q.Id, q.TypeCn));
                }

                if (q.QuestionType == "fill_blank" && q.BlankCount <= 0)
                {
                    throw new InvalidOperationException(String.Format("第 {0} 题（填空题）未识别到可填写的输入框。", q.Id));
                }
            }
        }

        /// <summary>
        /// 步骤 2：严格核对 AI 返回的结构化答案是否覆盖整份试卷且符合每道题的题型与填空数约束。
        /// </summary>
        internal static Dictionary<int, PaperAnswer> ValidateAiAnswers(PaperExtractionResult paper, List<PaperAnswer> aiAnswers)
        {
            if (aiAnswers == null || aiAnswers.Count != paper.ExpectedTotalCount)
            {
                throw new InvalidOperationException(String.Format(
                    "AI 返回的答案数量（{0}）与试卷总题数（{1}）不符。",
                    aiAnswers == null ? 0 : aiAnswers.Count, paper.ExpectedTotalCount));
            }

            var map = new Dictionary<int, PaperAnswer>();
            foreach (var ans in aiAnswers)
            {
                if (map.ContainsKey(ans.Id))
                    throw new InvalidOperationException(String.Format("AI 返回了重复的题号：第 {0} 题。", ans.Id));
                map[ans.Id] = ans;
            }

            foreach (var q in paper.Questions)
            {
                PaperAnswer ans;
                if (!map.TryGetValue(q.Id, out ans))
                    throw new InvalidOperationException(String.Format("AI 返回结果缺少第 {0} 题的答案。", q.Id));

                if (q.QuestionType == "single_choice" || q.QuestionType == "true_false")
                {
                    if (ans.Choices == null || ans.Choices.Count != 1)
                        throw new InvalidOperationException(String.Format("第 {0} 题（{1}）必须且只能有 1 个选项答案。", q.Id, q.TypeCn));

                    string choice = NormalizeTrueFalseChoice(q, ans.Choices[0]);
                    ans.Choices[0] = choice;
                    if (!HasOptionKey(q, choice))
                        throw new InvalidOperationException(String.Format("第 {0} 题 AI 返回的选项 {1} 在页面中不存在。", q.Id, choice));
                }
                else if (q.QuestionType == "multi_choice")
                {
                    if (ans.Choices == null || ans.Choices.Count == 0)
                        throw new InvalidOperationException(String.Format("第 {0} 题（多选题）AI 未返回任何选项。", q.Id));

                    var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var normalizedList = new List<string>();
                    foreach (string c in ans.Choices)
                    {
                        string upper = c.Trim().ToUpperInvariant();
                        if (!unique.Add(upper))
                            throw new InvalidOperationException(String.Format("第 {0} 题 AI 返回了重复选项 {1}。", q.Id, upper));
                        if (!HasOptionKey(q, upper))
                            throw new InvalidOperationException(String.Format("第 {0} 题 AI 返回的选项 {1} 在页面中不存在。", q.Id, upper));
                        normalizedList.Add(upper);
                    }
                    normalizedList.Sort(StringComparer.OrdinalIgnoreCase);
                    ans.Choices = normalizedList;
                }
                else if (q.QuestionType == "fill_blank")
                {
                    if (ans.Blanks == null || ans.Blanks.Count != q.BlankCount)
                    {
                        throw new InvalidOperationException(String.Format(
                            "第 {0} 题共有 {1} 个填空，但 AI 返回了 {2} 个空的答案。",
                            q.Id, q.BlankCount, ans.Blanks == null ? 0 : ans.Blanks.Count));
                    }
                    for (int i = 0; i < ans.Blanks.Count; i++)
                    {
                        if (String.IsNullOrWhiteSpace(ans.Blanks[i]))
                            throw new InvalidOperationException(String.Format("第 {0} 题第 {1} 空的 AI 答案为空。", q.Id, i + 1));
                    }
                }
                else if (q.QuestionType == "short_answer")
                {
                    if (String.IsNullOrWhiteSpace(ans.Text))
                        throw new InvalidOperationException(String.Format("第 {0} 题（简答题）AI 返回答案为空。", q.Id));
                }
            }

            return map;
        }

        /// <summary>
        /// 步骤 3：将校验通过的答案自动填写到网页控件中（支持整卷与分页试卷）。
        /// </summary>
        internal static async Task<int> FillPaperAnswersAsync(
            WebView2 browser,
            PaperExtractionResult paper,
            Dictionary<int, PaperAnswer> answerMap)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            int filledCount = 0;

            if (!paper.IsPaginated)
            {
                foreach (var q in paper.Questions)
                {
                    PaperAnswer ans = answerMap[q.Id];
                    string payloadJson = serializer.Serialize(new
                    {
                        id = q.Id,
                        questionType = q.QuestionType,
                        choices = ans.Choices,
                        blanks = ans.Blanks,
                        text = ans.Text
                    });
                    string res = await ExecuteInFramesAsync(
                        browser,
                        paper.TargetFrame,
                        BuildFillSingleQuestionScript(payloadJson),
                        delegate(string s) { return s == "ok"; });
                    if (res != "ok")
                    {
                        throw new InvalidOperationException(String.Format("填写第 {0} 题失败：{1}", q.Id, res));
                    }
                    filledCount++;
                }
            }
            else
            {
                for (int p = 0; p < paper.PageCount; p++)
                {
                    await ExecuteInFramesAsync(browser, paper.TargetFrame, BuildNavigatePageScript(p), delegate(string s) { return s == "ok"; });
                    await Task.Delay(100);
                    foreach (var q in paper.Questions)
                    {
                        if (q.PageIndex != p.ToString()) continue;
                        PaperAnswer ans = answerMap[q.Id];
                        string payloadJson = serializer.Serialize(new
                        {
                            id = q.Id,
                            questionType = q.QuestionType,
                            choices = ans.Choices,
                            blanks = ans.Blanks,
                            text = ans.Text
                        });
                        string res = await ExecuteInFramesAsync(
                            browser,
                            paper.TargetFrame,
                            BuildFillSingleQuestionScript(payloadJson),
                            delegate(string s) { return s == "ok"; });
                        if (res != "ok")
                        {
                            throw new InvalidOperationException(String.Format("分页试卷填写第 {0} 题失败：{1}", q.Id, res));
                        }
                        filledCount++;
                    }
                }
            }

            return filledCount;
        }

        /// <summary>
        /// 步骤 4：逐题读回核对（Read-Back Verification）。
        /// 从页面 DOM 独立读回每道题的实际选中项、每个填空框的值、简答编辑器正文，与目标答案逐一比对。
        /// </summary>
        internal static async Task<int> VerifyPaperReadBackAsync(
            WebView2 browser,
            PaperExtractionResult paper,
            Dictionary<int, PaperAnswer> answerMap)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            int verifiedCount = 0;

            if (!paper.IsPaginated)
            {
                foreach (var q in paper.Questions)
                {
                    await VerifySingleQuestionOnCurrentViewAsync(browser, paper.TargetFrame, serializer, q, answerMap[q.Id]);
                    verifiedCount++;
                }
            }
            else
            {
                for (int p = 0; p < paper.PageCount; p++)
                {
                    await ExecuteInFramesAsync(browser, paper.TargetFrame, BuildNavigatePageScript(p), delegate(string s) { return s == "ok"; });
                    await Task.Delay(100);
                    foreach (var q in paper.Questions)
                    {
                        if (q.PageIndex != p.ToString()) continue;
                        await VerifySingleQuestionOnCurrentViewAsync(browser, paper.TargetFrame, serializer, q, answerMap[q.Id]);
                        verifiedCount++;
                    }
                }
            }

            return verifiedCount;
        }

        private static async Task VerifySingleQuestionOnCurrentViewAsync(
            WebView2 browser,
            CoreWebView2Frame preferredFrame,
            JavaScriptSerializer serializer,
            PaperQuestion q,
            PaperAnswer expected)
        {
            string readBackJson = await ExecuteInFramesAsync(
                browser,
                preferredFrame,
                BuildReadBackQuestionScript(q.Id, q.QuestionType),
                delegate(string s) { return !String.IsNullOrEmpty(s) && s.Contains("\"found\":true"); });

            if (String.IsNullOrEmpty(readBackJson))
            {
                throw new InvalidOperationException(String.Format("读回核对第 {0} 题失败：页面上未找到该题容器。", q.Id));
            }

            var data = serializer.Deserialize<Dictionary<string, object>>(readBackJson);
            if (!GetBool(data, "found"))
            {
                throw new InvalidOperationException(String.Format("读回核对第 {0} 题失败：页面上未找到该题容器。", q.Id));
            }

            if (q.QuestionType == "single_choice" || q.QuestionType == "multi_choice" || q.QuestionType == "true_false")
            {
                var actualChoices = new List<string>();
                if (data.ContainsKey("choices") && data["choices"] is IList)
                {
                    foreach (object c in (IList)data["choices"])
                    {
                        string s = Convert.ToString(c ?? "").Trim().ToUpperInvariant();
                        if (!String.IsNullOrEmpty(s)) actualChoices.Add(s);
                    }
                }
                actualChoices.Sort(StringComparer.OrdinalIgnoreCase);

                var expectedChoices = new List<string>(expected.Choices);
                expectedChoices.Sort(StringComparer.OrdinalIgnoreCase);

                string actualStr = String.Join(",", actualChoices);
                string expectedStr = String.Join(",", expectedChoices);
                if (!String.Equals(actualStr, expectedStr, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(String.Format(
                        "第 {0} 题（{1}）读回核对不一致：期望选中 [{2}]，实际页面读回 [{3}]。",
                        q.Id, q.TypeCn, expectedStr, actualStr));
                }
            }
            else if (q.QuestionType == "fill_blank")
            {
                var actualBlanks = new List<string>();
                if (data.ContainsKey("blanks") && data["blanks"] is IList)
                {
                    foreach (object b in (IList)data["blanks"])
                    {
                        actualBlanks.Add(Convert.ToString(b ?? "").Trim());
                    }
                }
                if (actualBlanks.Count != expected.Blanks.Count)
                {
                    throw new InvalidOperationException(String.Format(
                        "第 {0} 题（填空题）读回空数不一致：期望 {1} 空，实际读回 {2} 空。",
                        q.Id, expected.Blanks.Count, actualBlanks.Count));
                }
                for (int i = 0; i < expected.Blanks.Count; i++)
                {
                    if (!String.Equals(actualBlanks[i], expected.Blanks[i].Trim(), StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(String.Format(
                            "第 {0} 题第 {1} 空读回内容不一致：期望「{2}」，实际读回「{3}」。",
                            q.Id, i + 1, expected.Blanks[i], actualBlanks[i]));
                    }
                }
            }
            else if (q.QuestionType == "short_answer")
            {
                string actualText = GetString(data, "text").Trim();
                string expectedText = expected.Text.Trim();
                if (String.IsNullOrWhiteSpace(actualText) || !actualText.Contains(expectedText))
                {
                    throw new InvalidOperationException(String.Format(
                        "第 {0} 题（简答题）编辑器读回正文不一致：期望包含「{1}」，实际读回「{2}」。",
                        q.Id, expectedText, actualText));
                }
            }
        }

        private static string NormalizeTrueFalseChoice(PaperQuestion q, string rawChoice)
        {
            string c = (rawChoice ?? "").Trim().ToUpperInvariant();
            if (HasOptionKey(q, c)) return c;
            if (c == "对" || c == "正确" || c == "TRUE" || c == "√" || c == "1") return "A";
            if (c == "错" || c == "错误" || c == "FALSE" || c == "×" || c == "0") return "B";
            return c;
        }

        private static bool HasOptionKey(PaperQuestion q, string key)
        {
            if (q.Options == null) return false;
            foreach (var opt in q.Options)
            {
                if (String.Equals(opt.Key, key, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string ComputePaperId(string url, string pageType, List<PaperQuestion> questions)
        {
            var sb = new StringBuilder();
            sb.Append(url ?? "").Append("|").Append(pageType ?? "").Append("|");
            if (questions != null)
            {
                foreach (var q in questions)
                {
                    sb.Append(q.Id).Append(":").Append(q.QuestionType).Append(":").Append(q.Stem).Append("|");
                }
            }
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder();
                for (int i = 0; i < 12 && i < hash.Length; i++)
                {
                    hex.Append(hash[i].ToString("x2"));
                }
                return (pageType ?? "paper") + "_" + hex;
            }
        }

        private static List<PaperQuestion> ParseQuestionsFromList(IList rawList)
        {
            var list = new List<PaperQuestion>();
            if (rawList == null) return list;
            foreach (object obj in rawList)
            {
                var d = obj as Dictionary<string, object>;
                if (d == null) continue;
                var q = new PaperQuestion
                {
                    Id = GetInt(d, "id"),
                    PageIndex = GetString(d, "pageIndex"),
                    QuestionType = GetString(d, "questionType"),
                    TypeCn = GetString(d, "typeCn"),
                    Stem = GetString(d, "stem"),
                    BlankCount = GetInt(d, "blankCount"),
                    HasUnsupportedMediaOrAttachment = GetBool(d, "unsupported"),
                    UnsupportedReason = GetString(d, "unsupportedReason")
                };
                if (d.ContainsKey("options") && d["options"] is IList)
                {
                    foreach (object optObj in (IList)d["options"])
                    {
                        var od = optObj as Dictionary<string, object>;
                        if (od != null)
                        {
                            q.Options.Add(new PaperOption
                            {
                                Key = GetString(od, "key"),
                                Text = GetString(od, "text")
                            });
                        }
                    }
                }
                list.Add(q);
            }
            return list;
        }

        /// <summary>
        /// 注入到提取、填写、读回三个阶段的共用 DOM 解析脚本片段：
        /// 同时兼容传统固定类名容器（.Py-mian1 / .TiMu / .questionLi / .ans-cc-exam）
        /// 与新版学习通无固定类名页面（TreeWalker 题型标签回溯卡片 + 圆圈字母选项）。
        /// </summary>
        private static string GetCommonDomHelperJs()
        {
            return @"
                function clean(s){return String(s||'').replace(/\s+/g,' ').trim();}
                function isVisible(el){
                    if(!el||!el.isConnected)return false;
                    var cur=el;
                    while(cur && cur.nodeType===1){
                        var st=cur.ownerDocument.defaultView.getComputedStyle(cur);
                        if(st.display==='none'||st.visibility==='hidden')return false;
                        cur=cur.parentElement;
                    }
                    return true;
                }
                function collectDocs(rootDoc){
                    var list=[];
                    function visit(d,depth){
                        if(!d||depth>6||list.indexOf(d)>=0)return;
                        list.push(d);
                        try{
                            d.querySelectorAll('iframe').forEach(function(f){
                                try{if(f.contentDocument)visit(f.contentDocument,depth+1);}catch(e){}
                            });
                        }catch(e){}
                    }
                    visit(rootDoc,0);
                    return list;
                }

                var bracketKindRe = /[\[【(（]\s*(完形填空|阅读理解|连线题|排序题|听力题|口语题|选词填空|单选题|多选题|判断题|填空题|简答题|论述题|名词解释)\s*[\]】)）]/;
                var bareKindRe = /^(完形填空|阅读理解|连线题|排序题|听力题|口语题|选词填空|单选题|多选题|判断题|填空题|简答题|论述题|名词解释)$/;
                var sectionHeaderRe = /^[一二三四五六七八九十百]+[\.\、\s]|共\s*\d+\s*道?题/;

                function classifyTypeText(typeText){
                    var qType='unknown';
                    var typeCn='未知题型';
                    if(/完形填空|阅读理解|连线题|排序题|听力题|口语题|选词填空/.test(typeText)){
                        qType='unknown';
                        var mType=typeText.match(/完形填空|阅读理解|连线题|排序题|听力题|口语题|选词填空/);
                        typeCn=mType?mType[0]:'复杂题型';
                    }
                    else if(/单选题|单选/.test(typeText)){qType='single_choice';typeCn='单选题';}
                    else if(/多选题|多选/.test(typeText)){qType='multi_choice';typeCn='多选题';}
                    else if(/判断题|判断/.test(typeText)){qType='true_false';typeCn='判断题';}
                    else if(/填空题|填空/.test(typeText)){qType='fill_blank';typeCn='填空题';}
                    else if(/简答题|论述题|名词解释|简答/.test(typeText)){qType='short_answer';typeCn='简答题';}
                    return {qType:qType, typeCn:typeCn};
                }

                function extractOptionItems(card, qType){
                    var items=[];
                    // 1. 先查传统固定选项选择器
                    var knownNodes=Array.from(card.querySelectorAll('ul.answerList li, .Answer, .singleoption, .mulChoice, .judgeoption, label.option-item, .option-row, .Zy_ulTop li')).filter(isVisible);
                    if(knownNodes.length>=2){
                        for(var i=0;i<knownNodes.length;i++){
                            var on=knownNodes[i];
                            var keyAttr=on.getAttribute('data-opt')||on.getAttribute('name')||'';
                            if(!keyAttr){
                                var ori=on.querySelector('[orichoice]');
                                if(ori) keyAttr=ori.getAttribute('orichoice');
                            }
                            if(!keyAttr){
                                var em=on.querySelector('em, .check, .opt-key, i.fl');
                                if(em) keyAttr=clean(em.innerText).replace(/[^A-H]/gi,'');
                            }
                            var optTxt=clean(on.innerText);
                            if(!keyAttr){
                                var mOpt=optTxt.match(/^([A-H])[\.\、．\s]/i);
                                if(mOpt) keyAttr=mOpt[1].toUpperCase();
                                else keyAttr=String.fromCharCode(65+i);
                            }
                            keyAttr=keyAttr.toUpperCase();
                            var labelEl=on.querySelector('em, .check, .opt-key, i.fl')||on;
                            optTxt=optTxt.replace(new RegExp('^'+keyAttr+'[\\.\\、．\\s]*','i'),'').trim();
                            if(!optTxt && qType==='true_false'){
                                optTxt=(i===0)?'正确':'错误';
                            }
                            items.push({key:keyAttr, text:optTxt, rowEl:on, labelEl:labelEl});
                        }
                        return items;
                    }

                    // 2. 通用圆圈字母回溯法（专门适配新版学习通圆圈 A/B/C/D 选项）
                    var allEls=Array.from(card.querySelectorAll('span,div,label,li,p,b,i,strong,a,em')).filter(isVisible);
                    var letterLabels=allEls.filter(function(el){
                        var t=clean(el.textContent);
                        if(!/^[A-H]$/.test(t)) return false;
                        var childHasLetter=Array.from(el.children).some(function(ch){
                            return /^[A-H]$/.test(clean(ch.textContent));
                        });
                        return !childHasLetter;
                    });
                    var foundMap={};
                    var lettersOrder=[];
                    for(var lIdx=0;lIdx<letterLabels.length;lIdx++){
                        var lbl=letterLabels[lIdx];
                        var letter=clean(lbl.textContent).toUpperCase();
                        if(foundMap[letter]) continue;
                        var target=lbl;
                        for(var step=0;step<5;step++){
                            var parent=target.parentElement;
                            if(!parent || parent===card) break;
                            var otherLabels=letterLabels.filter(function(other){
                                return other!==lbl && parent.contains(other) && !other.contains(lbl) && !lbl.contains(other);
                            });
                            if(otherLabels.length>0) break;
                            target=parent;
                            if(clean(target.innerText).length>1) break;
                        }
                        var rowText=clean(target.innerText).replace(new RegExp('^'+letter+'[\\.\\、．\\s]*','i'),'').trim();
                        if(!rowText && qType==='true_false'){
                            rowText=(letter==='A')?'正确':'错误';
                        }
                        if(rowText){
                            foundMap[letter]={key:letter, text:rowText, rowEl:target, labelEl:lbl};
                            lettersOrder.push(letter);
                        }
                    }
                    if(lettersOrder.length>=2){
                        lettersOrder.sort();
                        return lettersOrder.map(function(k){return foundMap[k];});
                    }

                    // 3. 纯文字判断题兜底（对/错 或 正确/错误）
                    if(qType==='true_false'){
                        var trueEl=allEls.find(function(el){var t=clean(el.textContent); return /^(对|正确|√|True|是)$/i.test(t) && el.children.length===0;});
                        var falseEl=allEls.find(function(el){var t=clean(el.textContent); return /^(错|错误|×|False|否)$/i.test(t) && el.children.length===0;});
                        if(trueEl && falseEl){
                            return [
                                {key:'A', text:'正确', rowEl:trueEl.parentElement||trueEl, labelEl:trueEl},
                                {key:'B', text:'错误', rowEl:falseEl.parentElement||falseEl, labelEl:falseEl}
                            ];
                        }
                    }
                    return items;
                }

                function findBlankInputs(card){
                    var list=Array.from(card.querySelectorAll('input.blank-input, input[name^=""blank""], textarea[name^=""blank""], input[name^=""answer""]:not([type=""hidden""]):not([type=""radio""]):not([type=""checkbox""]), textarea[name^=""answer""], .ananas-editor-answer, input[type=""text""]')).filter(isVisible);
                    return list;
                }

                function findShortAnswerEditors(card){
                    var list=Array.from(card.querySelectorAll('textarea, .ananas-editor-answer, [contenteditable=""true""], .ueditor-editor')).filter(isVisible);
                    return list;
                }

                function isQuestionMarkerTextNode(node){
                    if(!node || !node.parentElement || !isVisible(node.parentElement)) return null;
                    var t=clean(node.textContent);
                    if(!t) return null;
                    var pText=clean(node.parentElement.innerText||'');
                    if(sectionHeaderRe.test(t) || (sectionHeaderRe.test(pText) && pText.length<35)) return null;
                    var m=t.match(bracketKindRe);
                    if(m) return m[1];
                    if(bareKindRe.test(t) && pText.length<=10) return t;
                    return null;
                }

                function countQuestionMarkersInElement(el){
                    if(!el || !el.ownerDocument) return 0;
                    var w=el.ownerDocument.createTreeWalker(el, NodeFilter.SHOW_TEXT, null, false);
                    var count=0;
                    var n;
                    while((n=w.nextNode())){
                        if(isQuestionMarkerTextNode(n)) count++;
                    }
                    return count;
                }

                function locateCardsByTreeWalker(doc){
                    if(!doc || !doc.body) return [];
                    var walker=doc.createTreeWalker(doc.body, NodeFilter.SHOW_TEXT, null, false);
                    var markerNodes=[];
                    var n;
                    while((n=walker.nextNode())){
                        var kind=isQuestionMarkerTextNode(n);
                        if(kind) markerNodes.push({node:n, kind:kind});
                    }
                    var cards=[];
                    var seenCards=[];
                    for(var i=0;i<markerNodes.length;i++){
                        var item=markerNodes[i];
                        var typeInfo=classifyTypeText(item.kind);
                        var cur=item.node.parentElement;
                        var bestCard=null;
                        for(var depth=0; cur && cur!==doc.body && depth<10; depth++, cur=cur.parentElement){
                            var cText=clean(cur.innerText||'');
                            if(/题量\s*[:：]\s*\d+/.test(cText) && markerNodes.length>1) break;
                            var markerCount=countQuestionMarkersInElement(cur);
                            if(markerCount>1) break;

                            var hasControls=false;
                            if(typeInfo.qType==='single_choice'||typeInfo.qType==='multi_choice'||typeInfo.qType==='true_false'){
                                hasControls = extractOptionItems(cur, typeInfo.qType).length>=2;
                            } else if(typeInfo.qType==='fill_blank'){
                                hasControls = findBlankInputs(cur).length>=1;
                            } else if(typeInfo.qType==='short_answer'){
                                hasControls = findShortAnswerEditors(cur).length>=1;
                            } else {
                                hasControls = cText.length > item.kind.length + 4;
                            }

                            if(hasControls){
                                bestCard=cur;
                                break;
                            }
                        }
                        if(bestCard && seenCards.indexOf(bestCard)<0){
                            seenCards.push(bestCard);
                            cards.push(bestCard);
                        }
                    }
                    return cards;
                }

                var knownQuestionSelector='div.singleQuesId.ans-cc-exam, div.questionWrap, div.Py-mian1.singleQuesId, form#submitTest .question-item, div.wid750.singleQuesId, div.pad30.questionLi, div.pad30, form.answer-box .answer-item, div.TiMu, div.questionLi';

                function locateQuestionCardsInDoc(doc){
                    var rawKnown=Array.from(doc.querySelectorAll(knownQuestionSelector)).filter(isVisible);
                    // 过滤掉内部还嵌套了另一个候选容器的外层包裹节点
                    var filteredKnown=rawKnown.filter(function(el){
                        return !rawKnown.some(function(other){return other!==el && el.contains(other);});
                    });
                    var walkerCards=locateCardsByTreeWalker(doc);
                    // 同一份试卷可以混用不同题目结构。保留两套规则各自找到的题目，避免只读到某个题型。
                    // 一个已知容器包住多张题卡时，它是题组；只有一张时保留已知容器上的题号和控件。
                    var chosen=filteredKnown.filter(function(el){
                        return walkerCards.filter(function(card){return el.contains(card);}).length<=1;
                    });
                    walkerCards.forEach(function(card){
                        if(!chosen.some(function(el){return el.contains(card) || card.contains(el);})) chosen.push(card);
                    });
                    chosen.sort(function(a,b){return a===b ? 0 : (a.compareDocumentPosition(b)&Node.DOCUMENT_POSITION_FOLLOWING) ? -1 : 1;});
                    return chosen;
                }

                function getQuestionNavItems(doc){
                    return Array.from(doc.querySelectorAll('.question-nav-item, .topicNumber_list li[data-page-index]'));
                }

                function getStoredQuestionId(card){
                    var id=parseInt(card.getAttribute('data-qid')||card.getAttribute('data'),10);
                    return id>0 ? id : 0;
                }

                function assignQuestionCardIds(cards, isPaginated){
                    var ids=cards.map(function(card,i){return getStoredQuestionId(card)||(i+1);});
                    if(!isPaginated){
                        // 新版整卷优先用页面题号。某些题型组会重用内部序号，不能据此把不同题目合并。
                        var numberedInOrder=cards.every(function(card,i){
                            var heading=card.querySelector('.Zy_TItle');
                            if(!heading || !heading.querySelector('.newZy_TItle')) return false;
                            var match=clean(heading.innerText).match(/^(\d+)\s*[\.、．]?\s*[【\[（(]/);
                            return !!match && Number(match[1])===i+1;
                        });
                        if(numberedInOrder || new Set(ids).size!==ids.length){
                            ids=cards.map(function(card,i){return i+1;});
                        }
                    }
                    cards.forEach(function(card,i){card.setAttribute('data-xxt-card-id', String(ids[i]));});
                    return ids;
                }

                function findCardByQuestionId(docs, targetId){
                    for(var d=0;d<docs.length;d++){
                        var marked=docs[d].querySelector('[data-xxt-card-id=""'+targetId+'""]');
                        if(marked && isVisible(marked)) return marked;
                    }
                    for(var d2=0;d2<docs.length;d2++){
                        var cards=locateQuestionCardsInDoc(docs[d2]);
                        var navCount=getQuestionNavItems(docs[d2]).length;
                        var ids=assignQuestionCardIds(cards, navCount>1 && cards.length<navCount);
                        var index=ids.indexOf(targetId);
                        if(index>=0) return cards[index];
                    }
                    return null;
                }

                function captureOptionStyleSnapshot(opt){
                    var win=opt.rowEl.ownerDocument.defaultView;
                    var rSt=win.getComputedStyle(opt.rowEl);
                    var lSt=win.getComputedStyle(opt.labelEl||opt.rowEl);
                    return {
                        rowClass: String(opt.rowEl.className||'').trim(),
                        labelClass: String((opt.labelEl||opt.rowEl).className||'').trim(),
                        rowBg: String(rSt.backgroundColor||''),
                        labelBg: String(lSt.backgroundColor||''),
                        labelColor: String(lSt.color||''),
                        labelBorder: String(lSt.borderColor||'')
                    };
                }

                function hasActiveClassName(el){
                    if(!el || !el.className || typeof el.className!=='string') return false;
                    var parts=el.className.trim().split(/\s+/);
                    var activeNames=['cur','on','active','checked','is-checked','selected','ans-option-checked','selected-opt','chosen'];
                    for(var i=0;i<parts.length;i++){
                        if(activeNames.indexOf(parts[i])>=0) return true;
                    }
                    return false;
                }

                function isOptionSelectedInDom(card, opt){
                    var row=opt.rowEl;
                    var lbl=opt.labelEl||opt.rowEl;
                    if(hasActiveClassName(row) || hasActiveClassName(lbl)) return true;
                    if(row.getAttribute('aria-checked')==='true' || lbl.getAttribute('aria-checked')==='true') return true;
                    if(row.getAttribute('data-xxt-selected')==='true') return true;
                    var inp=row.matches('input')?row:row.querySelector('input[type=""radio""], input[type=""checkbox""]');
                    if(inp && inp.checked) return true;
                    var subActive=row.querySelector('.cur, .on, .active, .checked, .is-checked, .selected, .selected-opt, [aria-checked=""true""]');
                    if(subActive) return true;

                    // 对比点击前采集的未选中基准样式（适配新版学习通圆圈选项变蓝或混淆类名）
                    var baseJson=card.getAttribute('data-xxt-base-style');
                    if(baseJson){
                        try{
                            var base=JSON.parse(baseJson);
                            var cur=captureOptionStyleSnapshot(opt);
                            if(cur.rowClass!==base.rowClass ||
                               cur.labelClass!==base.labelClass ||
                               cur.labelBg!==base.labelBg ||
                               cur.rowBg!==base.rowBg ||
                               cur.labelColor!==base.labelColor ||
                               cur.labelBorder!==base.labelBorder){
                                return true;
                            }
                        }catch(e){}
                    }
                    return false;
                }
            ";
        }

        /// <summary>
        /// 构建自动滚动容器以唤醒懒加载题目的脚本。
        /// </summary>
        private static string BuildRevealQuestionsScript()
        {
            return @"(function(){
                try{
                    var docs=[document];
                    document.querySelectorAll('iframe').forEach(function(f){try{if(f.contentDocument)docs.push(f.contentDocument);}catch(e){}});
                    for(var i=0;i<docs.length;i++){
                        var d=docs[i];
                        var scroller=d.scrollingElement||d.documentElement||d.body;
                        if(scroller && scroller.scrollHeight>scroller.clientHeight+100){
                            scroller.scrollTop=scroller.scrollHeight;
                            scroller.scrollTop=0;
                        }
                    }
                }catch(e){}
                return 'ok';
            })()";
        }

        /// <summary>
        /// 汇总题型边界附近的元素结构及识别数量，不记录网页地址、题干、答案或账号信息。
        /// </summary>
        internal static async Task<string> CollectQuestionLayoutDiagnosticAsync(WebView2 browser)
        {
            const string header = "学习通漏题诊断 v1\r\n";
            string script = @"(function(){" + GetCommonDomHelperJs() + @"
                var result=[];
                var targets=new Set([1,20,21,22,30,31,32,40]);
                var numbered=/^\s*(\d{1,3})\s*[\.、．]?\s*[【\[（(]?\s*(单选题|多选题|判断题)/;
                function describe(el){
                    var classes=Array.from(el.classList).filter(function(c){return /^[a-zA-Z_-][a-zA-Z0-9_-]{0,31}$/.test(c);}).slice(0,5);
                    return el.tagName.toLowerCase()+(classes.length?'.'+classes.join('.'): '');
                }
                collectDocs(document).forEach(function(doc){
                    if(!doc.body) return;
                    var known=Array.from(doc.querySelectorAll(knownQuestionSelector)).filter(isVisible);
                    var walkerCards=locateCardsByTreeWalker(doc);
                    var markers=doc.createTreeWalker(doc.body,NodeFilter.SHOW_TEXT);
                    var count={'单选题':0,'多选题':0,'判断题':0};
                    var samples=new Map();
                    var node;
                    while((node=markers.nextNode())){
                        var kind=clean(node.textContent).match(/单选题|多选题|判断题/);
                        if(!kind || !isVisible(node.parentElement)) continue;
                        var recognized=isQuestionMarkerTextNode(node);
                        if(recognized && count.hasOwnProperty(recognized)) count[recognized]++;
                        var el=node.parentElement;
                        var chain=[];
                        var id=0;
                        var detectedType='';
                        for(var i=0;el && el!==doc.body && i<8;i++,el=el.parentElement){
                            var match=clean(el.innerText).match(numbered);
                            if(!id && match){id=Number(match[1]); detectedType=match[2];}
                            if(id && (!targets.has(id) || samples.has(id))) break;
                            var type=classifyTypeText(kind[0]).qType;
                            var markerCount=countQuestionMarkersInElement(el);
                            var controls=extractOptionItems(el,type).length;
                            var hint=Array.from(el.querySelectorAll('button,input,[role=checkbox],[role=radio],label')).slice(0,4).map(describe).join(',');
                            chain.push(describe(el)+'{标记='+markerCount+',选项='+controls+(hint?',控件='+hint:'')+'}');
                            if(markerCount>1 && id) break;
                        }
                        if(id && targets.has(id) && !samples.has(id)){
                            samples.set(id,id+' '+detectedType+' 标签='+(recognized?'命中':'未命中')+' '+chain.join(' > '));
                        }
                    }
                    if(!known.length && !walkerCards.length && !samples.size) return;
                    var merged=locateQuestionCardsInDoc(doc);
                    var storedIds=merged.map(getStoredQuestionId).filter(function(id){return id>0;});
                    result.push('传统容器='+known.length+' 题型回溯='+walkerCards.length+' 标签 单选/多选/判断='+count['单选题']+'/'+count['多选题']+'/'+count['判断题']);
                    result.push('去重后的题卡='+merged.length+' 内部编号重复数='+(storedIds.length-new Set(storedIds).size)+' 分页导航='+getQuestionNavItems(doc).length);
                    Array.from(samples.keys()).sort(function(a,b){return a-b;}).forEach(function(id){result.push(samples.get(id));});
                });
                return result.join('\n');
            })()";
            var serializer = new JavaScriptSerializer();
            var reports = new HashSet<string>();
            Action<string> addResult = delegate(string encoded)
            {
                string report = serializer.Deserialize<string>(encoded);
                if (!String.IsNullOrWhiteSpace(report)) reports.Add(report);
            };
            addResult(await browser.ExecuteScriptAsync(script));
            foreach (var frame in WebViewFrameRegistry.GetAliveFrames(browser))
            {
                try { addResult(await WebViewFrameRegistry.ExecuteCourseFrameScriptAsync(frame, script)); }
                catch (Exception) { } // 页面切换时，已销毁的框架可跳过。
            }
            return header + (reports.Count == 0 ? "当前页面未找到题目结构。" : String.Join("\r\n\r\n", reports).Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"));
        }

        /// <summary>
        /// 构建提取当前视图题目的 JS 脚本：兼容章节测验、作业、考试三类页面与五种题型，以及新版无固定类名页面。
        /// </summary>
        private static string BuildExtractCurrentViewScript()
        {
            return @"(function(){" + GetCommonDomHelperJs() + @"
                var docs=collectDocs(document);

                // 1. 检查是否已经处于平台完成/已提交状态
                for(var dIdx=0;dIdx<docs.length;dIdx++){
                    var bText=clean(docs[dIdx].body?docs[dIdx].body.innerText:'');
                    if((/章节测验\s*已完成|作业已提交|考试已交卷/.test(bText) && /本次成绩|最终成绩|待批阅|我的答案/.test(bText)) ||
                       docs[dIdx].querySelector('[data-paper-completed=""true""]')){
                        return JSON.stringify({
                            pageType:'completed',
                            pageTypeCn:'试卷',
                            alreadyCompleted:true,
                            isPaginated:false,
                            expectedTotalCount:0,
                            pageCount:1,
                            explicitPaperId:'',
                            questions:[]
                        });
                    }
                }

                // 2. 在所有可访问文档中寻找包含题目最多的文档
                var targetDoc=null;
                var pageType='none';
                var pageTypeCn='';
                var containers=[];

                for(var i=0;i<docs.length;i++){
                    var doc=docs[i];
                    var cards=locateQuestionCardsInDoc(doc);
                    if(cards.length>containers.length){
                        containers=cards;
                        targetDoc=doc;
                        var bTxt=clean(doc.body?doc.body.innerText:'');
                        var href=String(doc.location?doc.location.href:'');
                        if(doc.querySelector('div.singleQuesId.ans-cc-exam, div.questionWrap') || /exam|test\/reVersionTestStartNew/i.test(href) || /考试|交卷/.test(bTxt)){
                            pageType='exam'; pageTypeCn='考试';
                        } else if(doc.querySelector('div.Py-mian1.singleQuesId, form#submitTest .question-item') || /章节测验/.test(bTxt)){
                            pageType='chapter_test'; pageTypeCn='章节测验';
                        } else if(doc.querySelector('div.wid750.singleQuesId, div.pad30.questionLi, div.pad30, form.answer-box .answer-item') || /作业/.test(bTxt)){
                            pageType='homework'; pageTypeCn='作业';
                        } else {
                            pageType='chapter_test'; pageTypeCn='章节测验';
                        }
                    }
                }

                if(!targetDoc || containers.length===0){
                    return JSON.stringify({
                        pageType:'none',
                        pageTypeCn:'',
                        alreadyCompleted:false,
                        isPaginated:false,
                        expectedTotalCount:0,
                        pageCount:1,
                        explicitPaperId:'',
                        questions:[]
                    });
                }

                // 3. 读取试卷显式标识与总题数
                var explicitPaperId='';
                var paperIdEl=targetDoc.querySelector('[data-paper-id], input#workId, input#testId, input[name=""workRelationId""]');
                if(paperIdEl){
                    explicitPaperId=clean(paperIdEl.getAttribute('data-paper-id')||paperIdEl.value);
                }

                var navItems=getQuestionNavItems(targetDoc);
                var isPaginated=navItems.length>1 && containers.length<navItems.length;
                var pageCount=isPaginated?navItems.length:1;

                var expectedTotalCount=0;
                var totalAttrEl=targetDoc.querySelector('[data-total-questions]');
                if(totalAttrEl){
                    expectedTotalCount=parseInt(totalAttrEl.getAttribute('data-total-questions'),10)||0;
                }
                if(!expectedTotalCount){
                    for(var tIdx=0;tIdx<docs.length;tIdx++){
                        var bodyTxt=clean(docs[tIdx].body?docs[tIdx].body.innerText:'');
                        var m=bodyTxt.match(/题量\s*[:：]\s*(\d+)/) || bodyTxt.match(/共\s*(\d+)\s*道?题/);
                        if(m){
                            expectedTotalCount=parseInt(m[1]||m[2],10)||0;
                            if(expectedTotalCount>0) break;
                        }
                    }
                }
                if(!expectedTotalCount){
                    expectedTotalCount=isPaginated?navItems.length:containers.length;
                }

                // 4. 解析当前视图内的每一道题目
                var questions=[];
                var ids=assignQuestionCardIds(containers, isPaginated);
                for(var idx=0;idx<containers.length;idx++){
                    var c=containers[idx];
                    var qId=ids[idx];

                    var typeEl=c.querySelector('.question-type, h2.titType, .Py-m1-title span, .stem span, .tit h3, .Zy_TItle span');
                    var rawText=clean(c.innerText);
                    var typeText=typeEl?clean(typeEl.innerText):rawText;
                    var tInfo=classifyTypeText(typeText);
                    if(tInfo.qType==='unknown' && typeEl){
                        tInfo=classifyTypeText(rawText);
                    }
                    var qType=tInfo.qType;
                    var typeCn=tInfo.typeCn;

                    // 提取选项（针对单选/多选/判断）
                    var optItems=[];
                    var options=[];
                    if(qType==='single_choice'||qType==='multi_choice'||qType==='true_false'){
                        optItems=extractOptionItems(c, qType);
                        for(var oIdx=0;oIdx<optItems.length;oIdx++){
                            options.push({key:optItems[oIdx].key, text:optItems[oIdx].text});
                        }
                    }

                    // 提取题干（若没有独立题干节点，则截取第一个选项出现位置之前的文本）
                    var stemEl=c.querySelector('.question-stem, .ans-cc.timuStyle, .Py-m1-title, .stem, .tit, .Zy_TItle');
                    var stem='';
                    if(stemEl){
                        stem=clean(stemEl.innerText);
                    } else {
                        var fullText=c.innerText||'';
                        if(optItems.length>0){
                            var firstOptRowText=clean(optItems[0].rowEl.innerText||'');
                            var pos = firstOptRowText ? fullText.indexOf(firstOptRowText) : -1;
                            if(pos<0){
                                pos = fullText.search(/(?:^|\n)\s*A\s*(?:[\.\、．]|\s|\S)/);
                            }
                            stem = clean(pos>0 ? fullText.slice(0, pos) : fullText);
                        } else {
                            stem = rawText;
                        }
                    }
                    stem=stem
                        .replace(/^\d+\s*(?=[\[【(（]\s*(?:单选题|多选题|判断题|填空题|简答题|论述题|名词解释))/, '')
                        .replace(/^(?:\d+[\.\、\)）]\s*)?(?:[\[【(（]\s*(?:单选题|多选题|判断题|填空题|简答题|论述题|名词解释)\s*[\]】)）]\s*)?(?:\d+[\.\、\)）]\s*)?/, '')
                        .trim();

                    // 检查是否包含无法识别的图表、附件上传或损坏的编辑器
                    var unsupported=false;
                    var unsupportedReason='';
                    if(c.getAttribute('data-unsupported-reason')){
                        unsupported=true;
                        unsupportedReason=clean(c.getAttribute('data-unsupported-reason'));
                    } else if(c.querySelector('input[type=""file""], .upload-attachment')){
                        unsupported=true;
                        unsupportedReason='题目要求上传附件作答';
                    } else if(c.querySelector('img.question-chart, img[data-requires-vision=""true""]') || (/如图所示|见下图|看图回答/.test(stem) && c.querySelector('img'))){
                        unsupported=true;
                        unsupportedReason='题干依赖图片或图表信息，已停止自动作答';
                    }

                    // 统计填空题空数或验证简答题编辑器
                    var blankCount=0;
                    if(qType==='fill_blank'){
                        var blankInputs=findBlankInputs(c);
                        blankCount=blankInputs.length;
                        if(blankCount===0){
                            unsupported=true;
                            unsupportedReason='填空题未找到可识别的输入框或编辑器';
                        }
                    } else if(qType==='short_answer'){
                        var editors=findShortAnswerEditors(c);
                        if(editors.length===0){
                            unsupported=true;
                            unsupportedReason='简答题未找到可识别的编辑器控件';
                        }
                    }

                    questions.push({
                        id:qId,
                        pageIndex:'0',
                        questionType:qType,
                        typeCn:typeCn,
                        stem:stem,
                        blankCount:blankCount,
                        unsupported:unsupported,
                        unsupportedReason:unsupportedReason,
                        options:options
                    });
                }

                return JSON.stringify({
                    pageType:pageType,
                    pageTypeCn:pageTypeCn,
                    alreadyCompleted:false,
                    isPaginated:isPaginated,
                    expectedTotalCount:expectedTotalCount,
                    pageCount:pageCount,
                    explicitPaperId:explicitPaperId,
                    questions:questions
                });
            })()";
        }

        /// <summary>
        /// 构建切换到指定分页索引的 JS 脚本。
        /// </summary>
        private static string BuildNavigatePageScript(int pageIndex)
        {
            return @"(function(){
                var targetPage = " + pageIndex + @";
                var docs=[document];
                document.querySelectorAll('iframe').forEach(function(f){try{if(f.contentDocument)docs.push(f.contentDocument);}catch(e){}});
                for(var i=0;i<docs.length;i++){
                    var navs=Array.from(docs[i].querySelectorAll('.question-nav-item, .topicNumber_list li[data-page-index]'));
                    if(navs.length>0){
                        if(targetPage>=0 && targetPage<navs.length){
                            var btn=navs[targetPage];
                            if(btn.getAttribute('data-page-broken')==='true') return 'nav_failed';
                            btn.click();
                            return 'ok';
                        }
                        return 'nav_failed';
                    }
                }
                return 'no_nav';
            })()";
        }

        /// <summary>
        /// 构建填写单道题目的 JS 脚本：支持单选、多选、判断、多空填空、简答（含 UEditor 与 contenteditable）及新版圆圈字母选项。
        /// </summary>
        private static string BuildFillSingleQuestionScript(string payloadJson)
        {
            return @"(function(){" + GetCommonDomHelperJs() + @"
                var payload = " + payloadJson + @";
                var docs=collectDocs(document);
                var container=findCardByQuestionId(docs, payload.id);
                if(!container) return '未找到题号容器 ' + payload.id;

                // 若题目容器声明禁止写入模拟（用于验收测试验证读回失配保护），直接返回 ok 让后续读回核对捕获
                if(container.getAttribute('data-simulate-fill-ignore')==='true'){
                    return 'ok';
                }

                var qType=payload.questionType;
                if(qType==='single_choice'||qType==='multi_choice'||qType==='true_false'){
                    var targetSet={};
                    (payload.choices||[]).forEach(function(c){targetSet[String(c).toUpperCase()]=true;});
                    var optItems=extractOptionItems(container, qType);
                    if(optItems.length===0) return '未找到选项节点';

                    // 在首次点击前采集未选中选项的基准样式
                    if(!container.getAttribute('data-xxt-base-style')){
                        var baseCandidate = optItems[optItems.length-1];
                        for(var bIdx=0;bIdx<optItems.length;bIdx++){
                            if(!targetSet[optItems[bIdx].key]){
                                baseCandidate = optItems[bIdx];
                                break;
                            }
                        }
                        try{
                            container.setAttribute('data-xxt-base-style', JSON.stringify(captureOptionStyleSnapshot(baseCandidate)));
                        }catch(e){}
                    }

                    for(var i=0;i<optItems.length;i++){
                        var opt=optItems[i];
                        var on=opt.rowEl;
                        var lbl=opt.labelEl||on;
                        var shouldSelect=Boolean(targetSet[opt.key]);
                        var currentlySelected=isOptionSelectedInDom(container, opt);

                        if(shouldSelect){
                            try{on.scrollIntoView({block:'center'});}catch(e){}
                            var beforeHtml=container.outerHTML;
                            if(!currentlySelected){
                                try{on.click();}catch(e){}
                                if(container.outerHTML===beforeHtml && lbl!==on){
                                    try{lbl.click();}catch(e){}
                                }
                            }
                            on.classList.add('cur');
                            on.classList.add('on');
                            on.setAttribute('aria-checked','true');
                            on.setAttribute('data-xxt-selected','true');
                        } else {
                            if(qType==='multi_choice' && currentlySelected){
                                try{on.click();}catch(e){}
                            }
                            on.classList.remove('cur');
                            on.classList.remove('on');
                            on.setAttribute('aria-checked','false');
                            on.removeAttribute('data-xxt-selected');
                        }

                        var inp=on.matches('input')?on:on.querySelector('input[type=""radio""], input[type=""checkbox""]');
                        if(inp){
                            if(inp.checked!==shouldSelect){
                                inp.checked=shouldSelect;
                                inp.dispatchEvent(new Event('change',{bubbles:true}));
                            }
                        }
                    }

                    // 同步隐藏域
                    var hidden=container.querySelector('input.answer-hidden, input[id^=""answer""], input[name^=""answer""]:not([type=""radio""]):not([type=""checkbox""]):not(.blank-input)');
                    if(hidden && hidden.type==='hidden'){
                        hidden.value=(payload.choices||[]).join('');
                        hidden.dispatchEvent(new Event('change',{bubbles:true}));
                    }
                    return 'ok';
                }

                if(qType==='fill_blank'){
                    var blanks=payload.blanks||[];
                    var inputs=findBlankInputs(container);
                    if(inputs.length<blanks.length) return '填空输入框数量不足';
                    for(var b=0;b<blanks.length;b++){
                        var el=inputs[b];
                        if(el.tagName==='INPUT'||el.tagName==='TEXTAREA'){
                            el.value=blanks[b];
                            el.dispatchEvent(new Event('input',{bubbles:true}));
                            el.dispatchEvent(new Event('change',{bubbles:true}));
                            el.dispatchEvent(new Event('blur',{bubbles:true}));
                        } else {
                            el.textContent=blanks[b];
                            el.dispatchEvent(new Event('input',{bubbles:true}));
                        }
                    }
                    return 'ok';
                }

                if(qType==='short_answer'){
                    var answerText=payload.text||'';
                    var editors=findShortAnswerEditors(container);
                    if(editors.length===0) return '未找到简答题编辑器';
                    var ed=editors[0];
                    if(ed.tagName==='TEXTAREA'||ed.tagName==='INPUT'){
                        ed.value=answerText;
                        ed.dispatchEvent(new Event('input',{bubbles:true}));
                        ed.dispatchEvent(new Event('change',{bubbles:true}));
                        ed.dispatchEvent(new Event('blur',{bubbles:true}));
                    } else {
                        ed.innerHTML='<p>'+answerText+'</p>';
                        ed.setAttribute('data-editor-value', answerText);
                        ed.dispatchEvent(new Event('input',{bubbles:true}));
                        ed.dispatchEvent(new Event('change',{bubbles:true}));
                    }
                    // 若窗口挂载了 UEditor 实例则同步调用 setContent
                    try{
                        var win=container.ownerDocument.defaultView;
                        if(win && win.editors && win.editors.length>0){
                            for(var eIdx=0;eIdx<win.editors.length;eIdx++){
                                if(win.editors[eIdx] && win.editors[eIdx].ueditor){
                                    win.editors[eIdx].ueditor.setContent(answerText);
                                }
                            }
                        }
                    }catch(e){}
                    return 'ok';
                }

                return '未知题型';
            })()";
        }

        /// <summary>
        /// 构建独立读回单道题实际 DOM 状态的只读脚本。
        /// </summary>
        private static string BuildReadBackQuestionScript(int questionId, string questionType)
        {
            string safeType = (questionType ?? "").Replace("'", "");
            return @"(function(){" + GetCommonDomHelperJs() + @"
                var targetId = " + questionId + @";
                var qType = '" + safeType + @"';
                var docs=collectDocs(document);
                var container=findCardByQuestionId(docs, targetId);
                if(!container) return JSON.stringify({found:false});

                // 1. 读回选择题/判断题的实际选中项
                var selectedChoices=[];
                if(qType==='single_choice'||qType==='multi_choice'||qType==='true_false'){
                    var optItems=extractOptionItems(container, qType);
                    for(var i=0;i<optItems.length;i++){
                        var opt=optItems[i];
                        if(isOptionSelectedInDom(container, opt) && selectedChoices.indexOf(opt.key)<0){
                            selectedChoices.push(opt.key);
                        }
                    }
                }

                // 2. 读回填空题每个输入框的实际值
                var blanks=[];
                if(qType==='fill_blank'){
                    var blankInputs=findBlankInputs(container);
                    blankInputs.forEach(function(el){
                        if(el.tagName==='INPUT'||el.tagName==='TEXTAREA'){
                            blanks.push(clean(el.value));
                        } else {
                            blanks.push(clean(el.innerText||el.textContent));
                        }
                    });
                }

                // 3. 读回简答题编辑器正文
                var shortText='';
                if(qType==='short_answer'){
                    var editors=findShortAnswerEditors(container);
                    if(editors.length>0){
                        var ed=editors[0];
                        if(ed.tagName==='TEXTAREA'||ed.tagName==='INPUT'){
                            shortText=clean(ed.value);
                        } else {
                            shortText=clean(ed.innerText||ed.textContent);
                        }
                    }
                }

                return JSON.stringify({
                    found:true,
                    choices:selectedChoices,
                    blanks:blanks,
                    text:shortText
                });
            })()";
        }

        private static bool GetBool(Dictionary<string, object> dict, string key)
        {
            object val;
            return dict != null && dict.TryGetValue(key, out val) && val != null && Convert.ToBoolean(val);
        }

        private static int GetInt(Dictionary<string, object> dict, string key)
        {
            object val;
            return dict != null && dict.TryGetValue(key, out val) && val != null ? Convert.ToInt32(val) : 0;
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            object val;
            return dict != null && dict.TryGetValue(key, out val) && val != null ? Convert.ToString(val) : "";
        }
    }
}
