// 验证默认浏览器安全设置下的整卷识别和嵌入页面来源限制。
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using XuexitongDesktop;

internal sealed class ReadSmoke : Form
{
    private readonly WebView2 browser = new WebView2 { Dock = DockStyle.Fill };
    private int exitCode = 1;

    private ReadSmoke(string fixtures)
    {
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        Controls.Add(browser);
        var timeout = new Timer { Interval = 20000 };
        timeout.Tick += delegate { timeout.Stop(); Console.Error.WriteLine("本地测试超时"); Close(); };
        Shown += async delegate
        {
            timeout.Start();
            try
            {
                CoreWebView2Environment.SetLoaderDllFolderPath(AppPaths.BaseDir);
                var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.BaseDir, "test-profile"));
                await browser.EnsureCoreWebView2Async(environment);
                WebViewFrameRegistry.Attach(browser);
                foreach (string host in new[] { "fixture.chaoxing.com", "external.example" })
                    browser.CoreWebView2.SetVirtualHostNameToFolderMapping(host, fixtures, CoreWebView2HostResourceAccessKind.DenyCors);
                foreach (string fixture in new[] { "quiz-mixed-layout.html", "quiz-reused-card-ids.html" })
                {
                    await NavigateAsync("https://fixture.chaoxing.com/" + fixture);
                    PaperExtractionResult paper = await WebAutoAnswerer.ExtractWholePaperAsync(browser);
                    WebAutoAnswerer.ValidatePaperExtraction(paper);
                    if (paper.Questions.Count != 40 || paper.Questions[20].Id != 21 ||
                        paper.Questions[20].QuestionType != "multi_choice" || paper.Questions[30].Id != 31 ||
                        paper.Questions[30].QuestionType != "true_false" || paper.Questions[39].Id != 40)
                        throw new InvalidOperationException(fixture + " 未正确读取全部题目");
                    Console.WriteLine("PASS " + fixture + " (40 questions)");
                }
                await NavigateAsync("https://fixture.chaoxing.com/frame-guard.html");
                var frames = WebViewFrameRegistry.GetAliveFrames(browser);
                if (frames.Count != 2) throw new InvalidOperationException("未找到两个测试框架");
                int allowed = 0;
                int blocked = 0;
                foreach (var frame in frames)
                {
                    string result = await WebViewFrameRegistry.ExecuteCourseFrameScriptAsync(frame, "'allowed'");
                    if (result == "\"allowed\"") allowed++;
                    if (result == "null") blocked++;
                }
                if (allowed != 1 || blocked != 1) throw new InvalidOperationException("框架来源检查未生效");
                Console.WriteLine("PASS trusted frame allowed, external frame blocked");
                await VerifyPlaybackPauseAsync();
                await VerifyReadingAdvanceAsync();
                exitCode = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { timeout.Stop(); Close(); }
        };
    }

    private async Task VerifyPlaybackPauseAsync()
    {
        await NavigateAsync("https://fixture.chaoxing.com/frame-empty.html");
        var status = new ToolStripStatusLabel();
        var player = new CoursePlayer(browser, status, new ToolStripLabel());
        await player.PauseAsync();
        if (!status.Text.Contains("未能确认")) throw new InvalidOperationException("没有视频时不应报告已暂停");
        await browser.ExecuteScriptAsync(@"(function(){
            var video=document.createElement('video');
            document.body.appendChild(video);
            window.playCalls=0;
            video.play=function(){
                window.playCalls++;
                return new Promise(function(resolve,reject){
                    setTimeout(function(){reject(new DOMException('test','NotAllowedError'));}, 300);
                });
            };
        })()");
        Task start = player.StartOrResumeAsync();
        Task pause = player.PauseAsync();
        await Task.WhenAll(start, pause);
        await Task.Delay(450);
        string stopped = await browser.ExecuteScriptAsync("document.querySelector('video').paused && window.playCalls === 1");
        if (stopped != "true" || player.IsRunning || !status.Text.StartsWith("已暂停播放", StringComparison.Ordinal))
            throw new InvalidOperationException("暂停后旧播放请求仍在重试或状态不正确：" + stopped + " " + status.Text);
        Console.WriteLine("PASS pause wins over an in-flight play request");
    }

    private async Task VerifyReadingAdvanceAsync()
    {
        string url = "https://fixture.chaoxing.com/course-reading.html";
        var player = new CoursePlayer(browser, new ToolStripStatusLabel(), new ToolStripLabel());
        await NavigateAsync(url);
        await player.StepOnceAsync(false);
        await player.StepOnceAsync(true);
        if (await browser.ExecuteScriptAsync("window.nextClicks") != "0")
            throw new InvalidOperationException("只读检查或首次检查不应跳节");
        CourseStepSnapshot next = await player.StepOnceAsync(true);
        await player.StepOnceAsync(true);
        if (!next.SwitchedNextChapter || await browser.ExecuteScriptAsync("window.nextClicks === 1 && window.wasScrolled") != "true")
            throw new InvalidOperationException("教材页未滚到底部并且仅点击一次下一节");

        await NavigateAsync("https://fixture.chaoxing.com/mycourse/studentstudy.html");
        await player.StepOnceAsync(true);
        if (await browser.ExecuteScriptAsync("window.nextClicks") != "0")
            throw new InvalidOperationException("无页签教材预览需要连续两次确认");
        CourseStepSnapshot preview = await player.StepOnceAsync(true);
        await player.StepOnceAsync(true);
        if (!preview.SwitchedNextChapter || await browser.ExecuteScriptAsync("window.nextClicks === 1 && window.wasScrolled") != "true")
            throw new InvalidOperationException("无页签教材预览未滚动并点击一次下一节");

        await NavigateAsync("https://fixture.chaoxing.com/mycourse/studentstudy.html?case=intro");
        await browser.ExecuteScriptAsync("document.querySelector('iframe').contentDocument.title = '导读'");
        await player.StepOnceAsync(true);
        if (await browser.ExecuteScriptAsync("window.nextClicks") != "0")
            throw new InvalidOperationException("导读页首次检查不应跳节");
        CourseStepSnapshot intro = await player.StepOnceAsync(true);
        await player.StepOnceAsync(true);
        if (!intro.SwitchedNextChapter || await browser.ExecuteScriptAsync("window.nextClicks === 1 && window.wasScrolled") != "true")
            throw new InvalidOperationException("导读页未滚动并点击一次下一节");

        // 模拟实机的编号页签、无标题卡片，以及相邻的导读或测验页签。
        foreach (string otherTab in new[] { "导读", "章节测验" })
        {
            await NavigateAsync("https://fixture.chaoxing.com/mycourse/studentstudy.html?case=untitled");
            await browser.ExecuteScriptAsync(@"(function(){
                var frame = document.querySelector('iframe');
                frame.contentDocument.title = '';
                document.body.insertAdjacentHTML('afterbegin',
                    '<ul><li id=""dct1"">1</li><li id=""dct2"">2 " + otherTab + @"</li></ul>');
            })()");
            await player.StepOnceAsync(true);
            if (await browser.ExecuteScriptAsync("window.nextClicks") != "0")
                throw new InvalidOperationException("无标题卡片首次检查不应跳节");
            await player.StepOnceAsync(true);
            await player.StepOnceAsync(true);
            string expected = otherTab == "导读" ? "window.nextClicks === 1 && window.wasScrolled" : "window.nextClicks === 0";
            if (await browser.ExecuteScriptAsync(expected) != "true")
                throw new InvalidOperationException("无标题卡片处理错误，另一页签为" + otherTab);
        }

        await NavigateAsync(url + "?case=unknown");
        await browser.ExecuteScriptAsync("document.querySelector('.tabtags').remove()");
        await player.StepOnceAsync(true);
        await player.StepOnceAsync(true);
        if (await browser.ExecuteScriptAsync("window.nextClicks") != "0")
            throw new InvalidOperationException("只有下一节按钮的未知页面不能跳节");

        await NavigateAsync(url + "?case=video");
        await browser.ExecuteScriptAsync("document.querySelector('.tabtags').insertAdjacentHTML('beforeend','<span class=\"prev_white\">2 视频</span>')");
        await player.StepOnceAsync(true);
        await player.StepOnceAsync(true);
        if (await browser.ExecuteScriptAsync("window.nextClicks") != "0")
            throw new InvalidOperationException("视频尚未加载时不应跳节");

        await NavigateAsync(url + "?case=quiz");
        await browser.ExecuteScriptAsync("document.body.insertAdjacentHTML('beforeend','<div class=\"TiMu\">章节测验 <input type=\"radio\"></div>')");
        await player.StepOnceAsync(true);
        await player.StepOnceAsync(true);
        if (await browser.ExecuteScriptAsync("window.nextClicks") != "0")
            throw new InvalidOperationException("有测验时不应跳节");
        Console.WriteLine("PASS reading page advances once after scrolling; video and quiz pages stay put");
    }

    private async Task NavigateAsync(string url)
    {
        var complete = new TaskCompletionSource<bool>();
        EventHandler<CoreWebView2NavigationCompletedEventArgs> handler = delegate(object sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            complete.TrySetResult(args.IsSuccess);
        };
        browser.CoreWebView2.NavigationCompleted += handler;
        try
        {
            browser.CoreWebView2.Navigate(url);
            if (!await complete.Task) throw new InvalidOperationException("本地测试页打开失败");
        }
        finally { browser.CoreWebView2.NavigationCompleted -= handler; }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        using (var runner = new ReadSmoke(args[0]))
        {
            Application.Run(runner);
            return runner.exitCode;
        }
    }
}
