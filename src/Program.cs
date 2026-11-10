/*
 * 文件说明：Program.cs
 * 职责角色：本文件是学习通 Windows 桌面助手的程序入口与主窗口（MainWindow）。
 *           承载 WebView2 浏览器与独立持久化的用户登录目录（自动回退保护），并在主窗口与课程弹出子窗口中
 *           统一提供：
 *           1. 课程连续播放（自动播放、暂停、继续、显示当前章节、核对平台完成状态后自动切下一节）；
 *           2. 网页自动答题（适配章节测验、作业、考试，点击「开始答题」后读取整卷、作答、填写并逐题核对，供用户检查）；
 *           3. 截图框选识题与粘贴文字查题（本地 Tesseract 中英文 OCR + DeepSeek 答案与解析）；
 *           4. Windows 当前用户 DPAPI 加密的 DeepSeek API 密钥设置。
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: InternalsVisibleTo("全流程验收检查")]
[assembly: InternalsVisibleTo("启动检查")]
[assembly: InternalsVisibleTo("弹窗检查")]
[assembly: InternalsVisibleTo("真实视频联调")]
[assembly: InternalsVisibleTo("网页提取联调")]
[assembly: InternalsVisibleTo("接口检查")]
[assembly: InternalsVisibleTo("框选检查")]

namespace XuexitongDesktop
{
    /// <summary>
    /// 软件自身目录解析辅助类：无论直接双击启动、通过快捷方式启动还是由测试宿主加载，均精准定位程序集所在目录。
    /// </summary>
    internal static class AppPaths
    {
        internal static string BaseDir
        {
            get
            {
                try
                {
                    string loc = typeof(AppPaths).Assembly.Location;
                    if (!String.IsNullOrEmpty(loc))
                    {
                        string dir = Path.GetDirectoryName(loc);
                        if (!String.IsNullOrEmpty(dir)) return dir;
                    }
                }
                catch (Exception)
                {
                }
                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }
    }

    /// <summary>
    /// 跟踪每个 WebView2 实例下的所有子框架（包括跨子域/跨域 iframe），
    /// 使得自动答题与视频控制在 mooc2-ans.chaoxing.com 嵌套 mooc1.chaoxing.com 等跨域场景下也能直接执行脚本。
    /// </summary>
    internal static class WebViewFrameRegistry
    {
        private static readonly ConditionalWeakTable<CoreWebView2, List<CoreWebView2Frame>> FrameMap =
            new ConditionalWeakTable<CoreWebView2, List<CoreWebView2Frame>>();

        internal static void Attach(WebView2 browser)
        {
            if (browser == null || browser.CoreWebView2 == null) return;
            Attach(browser.CoreWebView2);
        }

        internal static void Attach(CoreWebView2 core)
        {
            if (core == null) return;
            List<CoreWebView2Frame> list;
            if (FrameMap.TryGetValue(core, out list)) return;
            list = new List<CoreWebView2Frame>();
            FrameMap.Add(core, list);
            core.FrameCreated += delegate(object sender, CoreWebView2FrameCreatedEventArgs e)
            {
                if (e == null || e.Frame == null) return;
                lock (list)
                {
                    list.RemoveAll(IsFrameDestroyed);
                    if (!list.Contains(e.Frame))
                    {
                        list.Add(e.Frame);
                    }
                }
            };
        }

        internal static List<CoreWebView2Frame> GetAliveFrames(WebView2 browser)
        {
            var result = new List<CoreWebView2Frame>();
            if (browser == null || browser.CoreWebView2 == null) return result;
            Attach(browser.CoreWebView2);
            List<CoreWebView2Frame> list;
            if (!FrameMap.TryGetValue(browser.CoreWebView2, out list)) return result;
            lock (list)
            {
                list.RemoveAll(IsFrameDestroyed);
                result.AddRange(list);
            }
            return result;
        }

        /// <summary>在框架当前页面内检查来源，避免向第三方嵌入页面注入学习通脚本。</summary>
        internal static Task<string> ExecuteCourseFrameScriptAsync(CoreWebView2Frame frame, string script)
        {
            return frame.ExecuteScriptAsync(@"(function(){
                if (location.protocol !== 'https:' || !/(^|\.)chaoxing\.com$/i.test(location.hostname)) return null;
                return (" + script + @");
            })()");
        }

        private static bool IsFrameDestroyed(CoreWebView2Frame frame)
        {
            if (frame == null) return true;
            try
            {
                return frame.IsDestroyed() != 0;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainWindow());
        }
    }

    internal sealed class MainWindow : Form
    {
        private readonly WebView2 browser = new WebView2();
        private readonly ToolStripStatusLabel status = new ToolStripStatusLabel("正在启动浏览器……");
        private readonly ToolStripLabel chapterLabel = new ToolStripLabel("当前章节：未进入课程");
        private readonly CoursePlayer player;

        internal MainWindow()
        {
            Text = "学习通桌面助手";
            Width = 1280;
            Height = 840;
            StartPosition = FormStartPosition.CenterScreen;

            player = new CoursePlayer(browser, status, chapterLabel);

            var toolbar = BuildToolbar(browser, this, status, chapterLabel, player, true);
            var bar = new StatusStrip();
            bar.Items.Add(status);

            browser.Dock = DockStyle.Fill;
            Controls.Add(browser);
            Controls.Add(bar);
            Controls.Add(toolbar);
            Shown += InitializeBrowser;
        }

        /// <summary>
        /// 构建统一的功能工具栏（主窗口与弹出的作业/考试子窗口共用同一套能力）。
        /// </summary>
        private ToolStrip BuildToolbar(
            WebView2 targetBrowser,
            Form ownerForm,
            ToolStripStatusLabel targetStatus,
            ToolStripLabel targetChapterLabel,
            CoursePlayer targetPlayer,
            bool includeHomeButton)
        {
            var toolbar = new ToolStrip();

            var back = new ToolStripButton("后退");
            back.Click += delegate
            {
                if (targetBrowser.CoreWebView2 != null && targetBrowser.CoreWebView2.CanGoBack)
                    targetBrowser.CoreWebView2.GoBack();
            };
            toolbar.Items.Add(back);

            if (includeHomeButton)
            {
                var home = new ToolStripButton("学习通首页");
                home.Click += delegate
                {
                    if (targetBrowser.CoreWebView2 != null)
                        targetBrowser.CoreWebView2.Navigate("https://i.chaoxing.com/");
                };
                toolbar.Items.Add(home);
            }

            var refresh = new ToolStripButton("刷新");
            refresh.Click += delegate
            {
                if (targetBrowser.CoreWebView2 != null)
                    targetBrowser.CoreWebView2.Reload();
            };
            toolbar.Items.Add(refresh);
            toolbar.Items.Add(new ToolStripSeparator());

            var play = new ToolStripButton("连续播放");
            play.ToolTipText = "开始或继续连续播放当前课程视频，平台确认完成后自动切换到下一节";
            play.Click += async delegate { await targetPlayer.StartOrResumeAsync(); };

            var pause = new ToolStripButton("暂停视频");
            pause.ToolTipText = "暂停当前视频并暂停自动切章巡检";
            pause.Click += async delegate { await targetPlayer.PauseAsync(); };

            toolbar.Items.Add(play);
            toolbar.Items.Add(pause);
            toolbar.Items.Add(new ToolStripSeparator());

            var startAutoAnswer = new ToolStripButton("开始答题");
            startAutoAnswer.ToolTipText = "调用 DeepSeek 作答并填写，核对后停在页面，未提交";
            startAutoAnswer.Click += async delegate
            {
                startAutoAnswer.Enabled = false;
                try
                {
                    await RunInteractiveAutoAnswerAsync(targetBrowser, ownerForm, targetStatus);
                }
                finally
                {
                    startAutoAnswer.Enabled = true;
                }
            };
            toolbar.Items.Add(startAutoAnswer);

            var readQuestions = new ToolStripButton("读取页面题目");
            readQuestions.ToolTipText = "仅读取当前页面的题目文字到查题窗口供人工核对";
            readQuestions.Click += async delegate { await ReadQuestions(targetBrowser, ownerForm, targetStatus); };
            toolbar.Items.Add(readQuestions);

            var diagnoseQuestions = new ToolStripButton("诊断漏题");
            diagnoseQuestions.Click += async delegate
            {
                if (targetBrowser.CoreWebView2 == null || targetBrowser.Source == null ||
                    targetBrowser.Source.Scheme != Uri.UriSchemeHttps ||
                    !(targetBrowser.Source.Host == "chaoxing.com" || targetBrowser.Source.Host.EndsWith(".chaoxing.com", StringComparison.OrdinalIgnoreCase)))
                {
                    targetStatus.Text = "请先打开学习通章节测验页面。";
                    return;
                }

                diagnoseQuestions.Enabled = false;
                try
                {
                    string diagnostic = await WebAutoAnswerer.CollectQuestionLayoutDiagnosticAsync(targetBrowser);
                    using (var dialog = new Form { Text = "漏题诊断（仅网页结构）", Width = 820, Height = 540, StartPosition = FormStartPosition.CenterParent })
                    {
                        var report = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
                            ScrollBars = ScrollBars.Both, WordWrap = false, Text = diagnostic };
                        var copy = new Button { Text = "复制诊断结果", Dock = DockStyle.Bottom, Height = 36 };
                        copy.Click += delegate { Clipboard.SetText(diagnostic); copy.Text = "已复制，可以粘贴到聊天"; };
                        dialog.Controls.Add(report);
                        dialog.Controls.Add(copy);
                        dialog.ShowDialog(ownerForm);
                    }
                }
                catch (Exception)
                {
                    targetStatus.Text = "未能读取网页结构，请等测验加载完成后再试。";
                }
                finally { diagnoseQuestions.Enabled = true; }
            };
            toolbar.Items.Add(diagnoseQuestions);

            var questions = new ToolStripButton("截图/文字查题与设置");
            questions.ToolTipText = "框选电脑任意窗口识题、粘贴题目文字查答案解析，或设置加密的 DeepSeek API 密钥";
            questions.Click += delegate { new QuestionWindow().Show(ownerForm); };
            toolbar.Items.Add(questions);

            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(targetChapterLabel);

            return toolbar;
        }

        /// <summary>
        /// 用户在页面点击「开始答题」时触发的完整自动答题流程。
        /// </summary>
        private async Task RunInteractiveAutoAnswerAsync(WebView2 view, Form owner, ToolStripStatusLabel targetStatus)
        {
            string apiKey = KeyStore.LoadKey();
            if (String.IsNullOrWhiteSpace(apiKey))
            {
                targetStatus.Text = "请先填写并保存 DeepSeek API 密钥。";
                MessageBox.Show(owner, "尚未配置 DeepSeek API 密钥。\n请在接下来打开的设置窗口中填写并点击「保存密钥」，然后再点击「开始答题」。", "学习通桌面助手");
                new QuestionWindow().Show(owner);
                return;
            }

            using (var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) })
            {
                AutoAnswerReport report = await WebAutoAnswerer.ExecuteFullWorkflowAsync(
                    view,
                    httpClient,
                    apiKey,
                    msg => targetStatus.Text = msg);

                targetStatus.Text = report.SummaryMessage;
                MessageBox.Show(owner,
                    report.SummaryMessage + "\n\n执行明细：\n- " + String.Join("\n- ", report.Details),
                    "网页答题结果 - " + (String.IsNullOrEmpty(report.PageTypeCn) ? "学习通桌面助手" : report.PageTypeCn));
            }
        }

        // 读取已知题目容器交给用户确认，不自动发送，也不自动提交（保留此方法以兼容本地提取检查）。
        private async Task ReadQuestions(WebView2 view, Form owner, ToolStripStatusLabel result)
        {
            Uri current;
            if (view.CoreWebView2 == null || view.Source == null ||
                !Uri.TryCreate(view.Source.ToString(), UriKind.Absolute, out current) ||
                current.Scheme != Uri.UriSchemeHttps ||
                !(current.Host == "chaoxing.com" || current.Host.EndsWith(".chaoxing.com", StringComparison.OrdinalIgnoreCase)))
            {
                result.Text = "请先打开学习通页面。";
                return;
            }
            try
            {
                // 先读取整卷。旧版只要命中一个已知容器就停止搜索，会漏掉同卷的其他题型。
                PaperExtractionResult paper = null;
                try { paper = await WebAutoAnswerer.ExtractWholePaperAsync(view); }
                catch (InvalidOperationException) { }
                if (paper != null && paper.Questions != null && paper.Questions.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var q in paper.Questions)
                    {
                        sb.AppendFormat("{0}. [{1}] {2}\r\n", q.Id, q.TypeCn, q.Stem);
                        if (q.Options != null)
                        {
                            foreach (var opt in q.Options)
                            {
                                sb.AppendFormat("   {0}. {1}\r\n", opt.Key, opt.Text);
                            }
                        }
                        sb.AppendLine();
                    }
                    new QuestionWindow(sb.ToString().Trim()).Show(owner);
                    result.Text = paper.Questions.Count < paper.ExpectedTotalCount
                        ? String.Format("读取不完整：已读 {0} 题，页面标明共 {1} 题。", paper.Questions.Count, paper.ExpectedTotalCount)
                        : String.Format("已读取 {0} 题，请检查题目后再查询。", paper.Questions.Count);
                    return;
                }

                // 保留没有可识别题卡的旧版答题表单文字预览。
                string script = @"(function(){const found=[];const seen=new Set();function scan(d,n){if(!d||n>5)return;try{d.querySelectorAll('div.Py-mian1.singleQuesId,form.answer-box').forEach(function(e){if(!seen.has(e)){seen.add(e);if(e.querySelector('div.Py-mian1.singleQuesId,form.answer-box'))return;let t=(e.innerText||'').trim();if(t)found.push(t);}});d.querySelectorAll('iframe').forEach(function(f){try{scan(f.contentDocument,n+1);}catch(e){}});}catch(e){}}scan(document,0);return found.join('\n\n');})()";
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                string encoded = await view.ExecuteScriptAsync(script);
                string text = serializer.Deserialize<string>(encoded);
                if (String.IsNullOrWhiteSpace(text))
                {
                    result.Text = "没有找到可读取的题目，请检查当前页面或使用截图识别。";
                    return;
                }
                new QuestionWindow(text).Show(owner);
                result.Text = "已读取页面文字，请检查题目后再查询。";
            }
            catch (Exception ex)
            {
                result.Text = "读取页面失败：" + ex.Message;
            }
        }

        // 只在学习通 HTTPS 页面操作可访问的视频，遇到跨域框架时安全跳过（保留此方法以兼容本地视频控制联调）。
        private async Task ControlVideo(WebView2 targetBrowser, ToolStripStatusLabel targetStatus, bool pause)
        {
            if (targetBrowser.CoreWebView2 == null) return;
            Uri current;
            if (!Uri.TryCreate(targetBrowser.Source.ToString(), UriKind.Absolute, out current) ||
                current.Scheme != Uri.UriSchemeHttps ||
                !(current.Host == "chaoxing.com" || current.Host.EndsWith(".chaoxing.com", StringComparison.OrdinalIgnoreCase)))
            {
                targetStatus.Text = "请先打开学习通课程页面。";
                return;
            }
            try
            {
                string action = pause ? "v.pause()" : "v.play().catch(function(){})";
                string script = @"(function(){let n=0;function scan(d,depth){if(depth>5)return;try{d.querySelectorAll('video').forEach(function(v){" + action + @";n++;});d.querySelectorAll('iframe').forEach(function(f){try{if(f.contentDocument)scan(f.contentDocument,depth+1);}catch(e){}});}catch(e){}}scan(document,0);return n;})()";
                string count = await targetBrowser.ExecuteScriptAsync(script);
                targetStatus.Text = count == "0" ? "当前页面未找到可访问的视频。" : (pause ? "已向视频发送暂停指令。" : "已向视频发送播放指令。");
            }
            catch (Exception ex)
            {
                targetStatus.Text = "视频操作失败：" + ex.Message;
            }
        }

        // 将浏览器状态放在当前用户目录或软件目录，确保重启后保持同一份登录状态。
        private async void InitializeBrowser(object sender, EventArgs e)
        {
            try
            {
                try
                {
                    CoreWebView2Environment.SetLoaderDllFolderPath(AppPaths.BaseDir);
                }
                catch (Exception)
                {
                    // 若当前进程已加载过 WebView2Loader.dll，忽略重复设置异常
                }

                string dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XuexitongDesktop", "WebView2");
                string localDataPath = Path.Combine(AppPaths.BaseDir, "用户数据", "WebView2");
                string fallbackMarker = Path.Combine(AppPaths.BaseDir, "用户数据", "使用软件目录.txt");
                CoreWebView2Environment environment = null;
                bool needsFallback = File.Exists(fallbackMarker);
                if (!needsFallback)
                {
                    try
                    {
                        Directory.CreateDirectory(dataPath);
                        environment = await CoreWebView2Environment.CreateAsync(null, dataPath);
                        await browser.EnsureCoreWebView2Async(environment);
                    }
                    catch (Exception)
                    {
                        needsFallback = true;
                    }
                }
                if (needsFallback)
                {
                    // 某些环境无法使用系统用户目录，回退时保存在软件目录下的「用户数据/WebView2」中。
                    Directory.CreateDirectory(localDataPath);
                    environment = await CoreWebView2Environment.CreateAsync(null, localDataPath);
                    await browser.EnsureCoreWebView2Async(environment);
                    // 记住成功使用的目录，避免下次启动切换到另一份登录状态。
                    File.WriteAllText(fallbackMarker, "浏览器使用本目录下的 WebView2 数据。", System.Text.Encoding.UTF8);
                }

                await browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(CoursePlayer.DefaultVideoSpeedScript);
                AttachPopupHandler(browser, environment);
                browser.CoreWebView2.NavigationCompleted += async delegate(object s, CoreWebView2NavigationCompletedEventArgs args)
                {
                    if (!args.IsSuccess)
                    {
                        status.Text = "页面加载失败，请检查网络连接。";
                        return;
                    }
                    // 页面加载完成后自动识别一次当前章节标题
                    await player.StepOnceAsync(false);
                };
                browser.CoreWebView2.Navigate("https://i.chaoxing.com/");
            }
            catch (Exception ex)
            {
                status.Text = "浏览器启动失败";
                MessageBox.Show("无法启动浏览器：" + ex.Message, "学习通桌面助手");
            }
        }

        // 新窗口共享登录环境，同时具备完整的连续播放、开始答题和查题工具栏。
        private void AttachPopupHandler(WebView2 source, CoreWebView2Environment environment)
        {
            WebViewFrameRegistry.Attach(source);
            source.CoreWebView2.NewWindowRequested += async delegate(object sender, CoreWebView2NewWindowRequestedEventArgs args)
            {
                var deferral = args.GetDeferral();
                Form popup = null;
                try
                {
                    popup = new Form { Text = "学习通 - 课程/测验/考试窗口", Width = 1180, Height = 780, StartPosition = FormStartPosition.CenterParent };
                    var child = new WebView2 { Dock = DockStyle.Fill };
                    var popupStatus = new ToolStripStatusLabel("就绪");
                    var popupChapter = new ToolStripLabel("当前章节：子窗口");
                    var popupPlayer = new CoursePlayer(child, popupStatus, popupChapter);
                    var toolbar = BuildToolbar(child, popup, popupStatus, popupChapter, popupPlayer, false);
                    var popupStatusBar = new StatusStrip();
                    popupStatusBar.Items.Add(popupStatus);

                    popup.Controls.Add(child);
                    popup.Controls.Add(popupStatusBar);
                    popup.Controls.Add(toolbar);
                    popup.Show(this);

                    await child.EnsureCoreWebView2Async(environment);
                    await child.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(CoursePlayer.DefaultVideoSpeedScript);
                    WebViewFrameRegistry.Attach(child);
                    child.CoreWebView2.NavigationCompleted += async delegate(object s2, CoreWebView2NavigationCompletedEventArgs a2)
                    {
                        if (a2.IsSuccess)
                        {
                            await popupPlayer.StepOnceAsync(false);
                        }
                    };
                    AttachPopupHandler(child, environment);
                    args.NewWindow = child.CoreWebView2;
                    args.Handled = true;
                }
                catch (Exception ex)
                {
                    args.Handled = true;
                    if (popup != null) popup.Close();
                    status.Text = "新窗口打开失败：" + ex.Message;
                }
                finally
                {
                    deferral.Complete();
                }
            };
        }
    }
}
