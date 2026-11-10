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
                exitCode = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { timeout.Stop(); Close(); }
        };
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
