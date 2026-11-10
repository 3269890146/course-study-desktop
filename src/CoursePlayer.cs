/*
 * 文件说明：CoursePlayer.cs
 * 职责角色：本文件实现学习通课程的连续播放、暂停/继续、当前章节识别、平台任务点完成状态核对以及自动切换下一节。
 *           核心原则：
 *           1. 播放结束不等于平台确认完成！必须在视频结束后读取到平台显示的任务完成状态（如 .ans-job-finished、.icon_Completed 等），
 *              确认完成后才自动切换到下一小节；
 *           2. 遇到登录过期、身份核验/验证码弹窗、或视频播完但长时间未出现平台完成标记时，立即停在原页并在状态栏显示明确原因。
 */
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace XuexitongDesktop
{
    /// <summary>
    /// 单次巡检课程页面的返回状态快照。
    /// </summary>
    internal sealed class CourseStepSnapshot
    {
        public bool IsChaoxingPage { get; set; }
        public bool GuardBlocked { get; set; }
        public string GuardReason { get; set; }
        public string ChapterTitle { get; set; }
        public string StepTabTitle { get; set; }
        public bool SwitchedToVideoTab { get; set; }
        public int VideoCount { get; set; }
        public bool IsPaused { get; set; }
        public bool IsEnded { get; set; }
        public double CurrentTime { get; set; }
        public double Duration { get; set; }
        public bool JobFinished { get; set; }
        public bool SwitchedNextChapter { get; set; }
        public string NextChapterTitle { get; set; }
        public bool CourseAllCompleted { get; set; }
        public string StatusMessage { get; set; }

        public CourseStepSnapshot()
        {
            GuardReason = "";
            ChapterTitle = "";
            StepTabTitle = "";
            NextChapterTitle = "";
            StatusMessage = "";
        }
    }

    /// <summary>
    /// 课程连续播放与自动切章控制器。
    /// </summary>
    internal sealed class CoursePlayer
    {
        private readonly WebView2 browser;
        private readonly ToolStripStatusLabel statusLabel;
        private readonly ToolStripLabel chapterLabel;
        private readonly Timer monitorTimer;
        private readonly System.Threading.SemaphoreSlim playbackGate = new System.Threading.SemaphoreSlim(1);
        private bool isRunning;
        private bool disposed;
        private int playbackVersion;
        private int endedWithoutJobFinishTicks;
        private string readingProbeKey;
        private string lastReadingAdvanceKey;
        private const int MaxWaitJobFinishTicks = 4;

        internal bool IsRunning
        {
            get { return isRunning; }
        }

        internal CoursePlayer(WebView2 browser, ToolStripStatusLabel statusLabel, ToolStripLabel chapterLabel)
        {
            this.browser = browser;
            this.statusLabel = statusLabel;
            this.chapterLabel = chapterLabel;
            this.monitorTimer = new Timer { Interval = 1200 };
            this.monitorTimer.Tick += async delegate { await OnMonitorTickAsync(); };
            browser.Disposed += delegate
            {
                disposed = true;
                isRunning = false;
                playbackVersion++;
                monitorTimer.Dispose();
            };
        }

        /// <summary>
        /// 启动或继续连续播放模式。
        /// </summary>
        internal async Task StartOrResumeAsync()
        {
            if (disposed) return;
            playbackVersion++;
            isRunning = true;
            endedWithoutJobFinishTicks = 0;
            readingProbeKey = null;
            lastReadingAdvanceKey = null;
            monitorTimer.Start();
            await StepOnceAsync(true);
        }

        /// <summary>
        /// 暂停连续播放，并向当前页面视频发送暂停指令。
        /// </summary>
        internal async Task PauseAsync()
        {
            if (disposed) return;
            int version = ++playbackVersion;
            isRunning = false;
            monitorTimer.Stop();
            if (statusLabel != null) statusLabel.Text = "正在暂停视频……";
            // 等当前巡检退出，再暂停播放器，避免上一轮播放指令晚到。
            await playbackGate.WaitAsync();
            try
            {
                if (version != playbackVersion || disposed) return;
                if (!IsValidChaoxingUrl(browser))
                {
                    UpdateUi("", "请先打开学习通课程页面。");
                    return;
                }
                var result = await PauseVideosInPageAsync(browser);
                if (version != playbackVersion || disposed) return;
                UpdateUi(result.Item1, result.Item2
                    ? "已暂停播放（点击「连续播放」可继续）。"
                    : "连续播放已停止，未能确认网页视频已暂停。请检查播放器。");
            }
            catch (Exception ex)
            {
                if (version == playbackVersion && !disposed) UpdateUi("", "暂停视频失败：" + ex.Message);
            }
            finally { playbackGate.Release(); }
        }

        private async Task OnMonitorTickAsync()
        {
            if (disposed || !isRunning || playbackGate.CurrentCount == 0) return;
            await StepOnceAsync(true);
        }

        /// <summary>
        /// 执行单步巡检与控制：识别章节、检测拦截状态、恢复播放、核对平台完成标记并在确认完成后自动切章。
        /// </summary>
        internal async Task<CourseStepSnapshot> StepOnceAsync(bool autoPlayAndAdvance)
        {
            int version = playbackVersion;
            await playbackGate.WaitAsync();
            try
            {
                if (version != playbackVersion || disposed) return new CourseStepSnapshot();
                return await InspectAndPlayAsync(autoPlayAndAdvance, version);
            }
            finally { playbackGate.Release(); }
        }

        private async Task<CourseStepSnapshot> InspectAndPlayAsync(bool autoPlayAndAdvance, int version)
        {
            var snap = new CourseStepSnapshot();
            if (!IsValidChaoxingUrl(browser))
            {
                snap.IsChaoxingPage = false;
                snap.StatusMessage = "请先打开学习通课程页面。";
                UpdateUi("", snap.StatusMessage);
                return snap;
            }
            snap.IsChaoxingPage = true;

            try
            {
                string inspectScript = BuildInspectScript(autoPlayAndAdvance);
                string rawJson = await browser.ExecuteScriptAsync(inspectScript);
                if (version != playbackVersion || disposed) return snap;
                var serializer = new JavaScriptSerializer();
                string json = serializer.Deserialize<string>(rawJson);
                var data = serializer.Deserialize<Dictionary<string, object>>(json);
                bool canAdvanceReading = GetBool(data, "canAdvanceReading");
                bool hasVideoOrQuiz = GetBool(data, "hasVideoOrQuiz");

                snap.GuardBlocked = GetBool(data, "guardBlocked");
                snap.GuardReason = GetString(data, "guardReason");
                snap.ChapterTitle = GetString(data, "chapterTitle");
                snap.StepTabTitle = GetString(data, "stepTabTitle");
                snap.SwitchedToVideoTab = GetBool(data, "switchedToVideoTab");
                snap.VideoCount = GetInt(data, "videoCount");
                snap.IsPaused = GetBool(data, "paused");
                snap.IsEnded = GetBool(data, "ended");
                snap.CurrentTime = GetDouble(data, "currentTime");
                snap.Duration = GetDouble(data, "duration");
                snap.JobFinished = GetBool(data, "jobFinished");

                // 同时巡检所有子 CoreWebView2Frame（兼容跨域/跨子域嵌套的课程卡片与视频播放器 iframe）
                var frames = WebViewFrameRegistry.GetAliveFrames(browser);
                foreach (var frame in frames)
                {
                    try
                    {
                        string fRaw = await WebViewFrameRegistry.ExecuteCourseFrameScriptAsync(frame, inspectScript);
                        if (version != playbackVersion || disposed) return snap;
                        string fJson = serializer.Deserialize<string>(fRaw);
                        if (String.IsNullOrEmpty(fJson)) continue;
                        var fData = serializer.Deserialize<Dictionary<string, object>>(fJson);
                        if (fData == null) continue;
                        hasVideoOrQuiz |= GetBool(fData, "hasVideoOrQuiz");

                        if (GetBool(fData, "guardBlocked") && !snap.GuardBlocked)
                        {
                            snap.GuardBlocked = true;
                            snap.GuardReason = GetString(fData, "guardReason");
                        }
                        if (GetBool(fData, "jobFinished"))
                        {
                            snap.JobFinished = true;
                        }
                        if (GetBool(fData, "switchedToVideoTab"))
                        {
                            snap.SwitchedToVideoTab = true;
                        }
                        int fVideos = GetInt(fData, "videoCount");
                        if (fVideos > 0 && snap.VideoCount == 0)
                        {
                            snap.VideoCount = fVideos;
                            snap.IsPaused = GetBool(fData, "paused");
                            snap.IsEnded = GetBool(fData, "ended");
                            snap.CurrentTime = GetDouble(fData, "currentTime");
                            snap.Duration = GetDouble(fData, "duration");
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
                if (version != playbackVersion || disposed) return snap;

                if (snap.GuardBlocked || snap.SwitchedToVideoTab || snap.VideoCount > 0 || hasVideoOrQuiz || !canAdvanceReading)
                    readingProbeKey = null;

                // 1. 若检测到登录过期或身份核验/验证码弹窗，立即停在原页并显示原因
                if (snap.GuardBlocked)
                {
                    isRunning = false;
                    monitorTimer.Stop();
                    snap.StatusMessage = "已停在原页：" + snap.GuardReason;
                    UpdateUi(snap.ChapterTitle, snap.StatusMessage);
                    return snap;
                }

                // 2. 如果刚刚自动点击切换到“视频”页签，等待下一轮加载
                if (snap.SwitchedToVideoTab)
                {
                    snap.StatusMessage = "已自动切换到「视频」页签，正在等待视频加载……";
                    UpdateUi(snap.ChapterTitle, snap.StatusMessage);
                    return snap;
                }

                // 3. 连续两次确认是教材或课件页，再滚动并点击页面的「下一节」。
                if (snap.VideoCount == 0)
                {
                    if (autoPlayAndAdvance && canAdvanceReading && !hasVideoOrQuiz)
                    {
                        string key = browser.Source + "\n" + snap.ChapterTitle + "\n" + snap.StepTabTitle;
                        if (lastReadingAdvanceKey == key)
                        {
                            snap.StatusMessage = "已点击「下一节」，正在等待章节切换。";
                        }
                        else if (readingProbeKey != key)
                        {
                            readingProbeKey = key;
                            lastReadingAdvanceKey = null;
                            snap.StatusMessage = "检测到教材或课件页，正在确认无需播放视频……";
                        }
                        else
                        {
                            string nextRaw = await browser.ExecuteScriptAsync(BuildInspectScript(false, true));
                            if (version != playbackVersion || disposed) return snap;
                            var nextData = serializer.Deserialize<Dictionary<string, object>>(serializer.Deserialize<string>(nextRaw));
                            snap.SwitchedNextChapter = GetBool(nextData, "clickedReadingNext");
                            if (snap.SwitchedNextChapter) lastReadingAdvanceKey = key;
                            else readingProbeKey = null;
                            snap.StatusMessage = snap.SwitchedNextChapter
                                ? "已滚到教材或课件页底部，并点击「下一节」。"
                                : "页面已变化，正在重新检查。";
                        }
                    }
                    else
                    {
                        snap.StatusMessage = "未找到可访问的视频，当前页没有可自动前进的教材或课件。";
                    }
                    UpdateUi(snap.ChapterTitle, snap.StatusMessage);
                    return snap;
                }
                lastReadingAdvanceKey = null;

                // 4. 视频播放结束时的处理：必须核对平台任务完成状态才允许切章！
                bool videoReachedEnd = snap.IsEnded || (snap.Duration > 0.5 && snap.CurrentTime >= snap.Duration - 0.15);
                if (videoReachedEnd)
                {
                    if (snap.JobFinished)
                    {
                        endedWithoutJobFinishTicks = 0;
                        if (autoPlayAndAdvance)
                        {
                            var advanceResult = await AdvanceToNextChapterAsync(browser);
                            if (version != playbackVersion || disposed) return snap;
                            snap.SwitchedNextChapter = advanceResult.Item1;
                            snap.NextChapterTitle = advanceResult.Item2;
                            snap.CourseAllCompleted = advanceResult.Item3;

                            if (snap.SwitchedNextChapter)
                            {
                                snap.ChapterTitle = !String.IsNullOrEmpty(snap.NextChapterTitle) ? snap.NextChapterTitle : snap.ChapterTitle;
                                snap.StatusMessage = "平台已确认本节完成，已自动切换到下一节：" + snap.ChapterTitle;
                            }
                            else if (snap.CourseAllCompleted)
                            {
                                isRunning = false;
                                monitorTimer.Stop();
                                snap.StatusMessage = "平台已确认本节完成，当前课程全部章节已播放完毕。";
                            }
                            else
                            {
                                isRunning = false;
                                monitorTimer.Stop();
                                snap.StatusMessage = "平台已确认完成，但未找到下一节入口，已停在当前页。";
                            }
                        }
                        else
                        {
                            snap.StatusMessage = "当前视频已播放完成，且平台任务状态已确认为完成。";
                        }
                    }
                    else
                    {
                        // 视频结束了，但平台还没有显示“已完成”状态
                        endedWithoutJobFinishTicks++;
                        if (endedWithoutJobFinishTicks >= MaxWaitJobFinishTicks)
                        {
                            isRunning = false;
                            monitorTimer.Stop();
                            snap.StatusMessage = "视频已结束，但无法确认平台任务完成状态，已停在原页避免漏任务点。";
                        }
                        else
                        {
                            snap.StatusMessage = String.Format("视频播放完毕，正在等待平台确认任务完成状态（{0}/{1}）……", endedWithoutJobFinishTicks, MaxWaitJobFinishTicks);
                        }
                    }
                    UpdateUi(snap.ChapterTitle, snap.StatusMessage);
                    return snap;
                }

                // 5. 正在播放或暂停中
                endedWithoutJobFinishTicks = 0;
                if (snap.IsPaused)
                {
                    snap.StatusMessage = autoPlayAndAdvance
                        ? "已向当前章节视频发送播放指令……"
                        : "当前视频已暂停。";
                }
                else
                {
                    string progress = snap.Duration > 0
                        ? String.Format("{0:F1}s / {1:F1}s", snap.CurrentTime, snap.Duration)
                        : String.Format("{0:F1}s", snap.CurrentTime);
                    snap.StatusMessage = String.Format("正在连续播放（{0}）{1}",
                        progress,
                        snap.JobFinished ? " [平台标记：已完成]" : " [平台标记：学习中]");
                }

                if (version == playbackVersion && !disposed) UpdateUi(snap.ChapterTitle, snap.StatusMessage);
                return snap;
            }
            catch (Exception ex)
            {
                snap.StatusMessage = "课程播放巡检异常：" + ex.Message;
                if (version == playbackVersion && !disposed) UpdateUi(snap.ChapterTitle, snap.StatusMessage);
                return snap;
            }
        }

        private void UpdateUi(string chapterTitle, string statusText)
        {
            if (chapterLabel != null)
            {
                chapterLabel.Text = String.IsNullOrWhiteSpace(chapterTitle)
                    ? "当前章节：未识别"
                    : "当前章节：" + chapterTitle;
            }
            if (statusLabel != null && !String.IsNullOrWhiteSpace(statusText))
            {
                statusLabel.Text = statusText;
            }
        }

        internal static bool IsValidChaoxingUrl(WebView2 view)
        {
            if (view == null || view.CoreWebView2 == null || view.Source == null) return false;
            Uri current;
            if (!Uri.TryCreate(view.Source.ToString(), UriKind.Absolute, out current)) return false;
            if (current.Scheme != Uri.UriSchemeHttps) return false;
            return current.Host.Equals("chaoxing.com", StringComparison.OrdinalIgnoreCase) ||
                   current.Host.EndsWith(".chaoxing.com", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 暂停页面及其同源子框架中的所有视频，并返回当前章节标题。
        /// </summary>
        private static async Task<Tuple<string, bool>> PauseVideosInPageAsync(WebView2 view)
        {
            string script = @"(function(){
                function clean(s){return String(s||'').replace(/\s+/g,' ').trim();}
                var found=0;
                var stillPlaying=0;
                function scanPause(d,depth){
                    if(!d||depth>5)return;
                    try{
                        d.querySelectorAll('video').forEach(function(v){
                            found++;
                            v.setAttribute('data-csd-paused','');
                            try{v.pause();}catch(e){}
                            if(!v.paused) stillPlaying++;
                        });
                        d.querySelectorAll('iframe').forEach(function(f){try{if(f.contentDocument)scanPause(f.contentDocument,depth+1);}catch(e){}});
                    }catch(e){}
                }
                scanPause(document,0);
                var active=document.querySelector('#coursetree .posCatalog_active .posCatalog_name, .posCatalog_active .posCatalog_name, .posCatalog_active');
                return JSON.stringify({chapter:active?clean(active.getAttribute('title')||active.innerText):'',found:found,stillPlaying:stillPlaying});
            })()";
            var serializer = new JavaScriptSerializer();
            string raw = await view.ExecuteScriptAsync(script);
            var top = serializer.Deserialize<Dictionary<string, object>>(serializer.Deserialize<string>(raw));
            int found = GetInt(top, "found");
            int stillPlaying = GetInt(top, "stillPlaying");
            foreach (var frame in WebViewFrameRegistry.GetAliveFrames(view))
            {
                try
                {
                    string frameRaw = await WebViewFrameRegistry.ExecuteCourseFrameScriptAsync(frame, script);
                    string frameJson = serializer.Deserialize<string>(frameRaw);
                    if (String.IsNullOrEmpty(frameJson)) continue;
                    var frameStatus = serializer.Deserialize<Dictionary<string, object>>(frameJson);
                    found += GetInt(frameStatus, "found");
                    stillPlaying += GetInt(frameStatus, "stillPlaying");
                }
                catch (Exception) { }
            }
            return Tuple.Create(GetString(top, "chapter"), found > 0 && stillPlaying == 0);
        }

        /// <summary>
        /// 在平台确认当前任务完成后，切换到下一小节。
        /// 返回 Tuple(是否成功触发切换, 下一节标题, 是否已学完全部章节)。
        /// </summary>
        private static async Task<Tuple<bool, string, bool>> AdvanceToNextChapterAsync(WebView2 view)
        {
            string script = @"(function(){
                function clean(s){return String(s||'').replace(/\s+/g,' ').trim();}
                function isVisible(el){
                    if(!el||!el.isConnected)return false;
                    var st=el.ownerDocument.defaultView.getComputedStyle(el);
                    return st.display!=='none'&&st.visibility!=='hidden'&&el.getClientRects().length>0;
                }
                // 优先通过左侧课程目录树 #coursetree 切换到下一小节
                var nodes=Array.from(document.querySelectorAll('#coursetree .posCatalog_select:not(.firstLayer), .coursetree .posCatalog_select:not(.firstLayer), .chapter-item'));
                if(nodes.length>0){
                    var activeIdx=nodes.findIndex(function(el){return el.classList.contains('posCatalog_active')||el.classList.contains('active');});
                    if(activeIdx>=0){
                        if(activeIdx+1>=nodes.length){
                            return JSON.stringify({switched:false,nextTitle:'',allCompleted:true});
                        }
                        var nextNode=nodes[activeIdx+1];
                        var clickTarget=nextNode.querySelector('.posCatalog_name')||nextNode;
                        var nextTitle=clean(clickTarget.getAttribute('title')||clickTarget.innerText);
                        clickTarget.click();
                        return JSON.stringify({switched:true,nextTitle:nextTitle,allCompleted:false});
                    }
                }
                // 备选：点击页面下方的“下一节”按钮
                var nextBtn=document.querySelector('#prevNextFocusNext, #right1, .nextChapter');
                if(nextBtn&&isVisible(nextBtn)){
                    nextBtn.click();
                    var activeAfter=document.querySelector('#coursetree .posCatalog_active .posCatalog_name, .posCatalog_active .posCatalog_name, .posCatalog_active');
                    var titleAfter=activeAfter?clean(activeAfter.getAttribute('title')||activeAfter.innerText):'';
                    return JSON.stringify({switched:true,nextTitle:titleAfter,allCompleted:false});
                }
                return JSON.stringify({switched:false,nextTitle:'',allCompleted:false});
            })()";
            string rawJson = await view.ExecuteScriptAsync(script);
            var serializer = new JavaScriptSerializer();
            string json = serializer.Deserialize<string>(rawJson);
            var data = serializer.Deserialize<Dictionary<string, object>>(json);
            return Tuple.Create(
                GetBool(data, "switched"),
                GetString(data, "nextTitle"),
                GetBool(data, "allCompleted"));
        }

        // 在每个学习通网页和视频子框架监听播放事件。切章或换视频后，播放器也会使用相同的默认速度。
        internal const string DefaultVideoSpeedScript = @"(function(){
            if(location.protocol !== 'https:' || !/(^|\.)chaoxing\.com$/i.test(location.hostname)) return;
            document.addEventListener('play', function(event){
                var video=event.target;
                if(video && video.tagName==='VIDEO'){
                    try{video.playbackRate=2;}catch(e){}
                }
            }, true);
        })()";

        /// <summary>
        /// 生成在浏览器内执行的单步探测与控制 JS 脚本。
        /// </summary>
        private static string BuildInspectScript(bool shouldPlay, bool advanceReading = false)
        {
            string playFlag = shouldPlay ? "true" : "false";
            return @"(function(){
                var shouldPlay = " + playFlag + @";
                var advanceReading = " + (advanceReading ? "true" : "false") + @";
                function clean(s){return String(s||'').replace(/\s+/g,' ').trim();}
                function isVisible(el){
                    if(!el||!el.isConnected)return false;
                    var st=el.ownerDocument.defaultView.getComputedStyle(el);
                    return st.display!=='none'&&st.visibility!=='hidden'&&el.getClientRects().length>0;
                }
                var docs=[];
                function collectDocs(d,depth){
                    if(!d||depth>5||docs.indexOf(d)>=0)return;
                    docs.push(d);
                    try{
                        d.querySelectorAll('iframe').forEach(function(f){
                            try{if(f.contentDocument)collectDocs(f.contentDocument,depth+1);}catch(e){}
                        });
                    }catch(e){}
                }
                collectDocs(document,0);

                // 1. 检测登录过期或身份核验/人脸识别/验证码拦截
                var href=location.href||'';
                var guardBlocked=false;
                var guardReason='';
                if(/passport2\.chaoxing\.com|\/login/i.test(href)){
                    guardBlocked=true;
                    guardReason='登录状态已过期，请重新登录。';
                }
                if(!guardBlocked){
                    for(var i=0;i<docs.length;i++){
                        var doc=docs[i];
                        var verifyEl=doc.querySelector('#validate, .verify-wrap, .face-recognition, .captcha-modal, [data-guard-block=""true""]');
                        if(verifyEl&&isVisible(verifyEl)){
                            guardBlocked=true;
                            guardReason='检测到身份核验或验证码弹窗，请人工完成验证后再继续。';
                            break;
                        }
                        var bodyText=clean(doc.body?doc.body.innerText:'');
                        if(/请完成安全验证|人脸识别验证|拖动滑块完成拼图|登录超时/.test(bodyText)){
                            guardBlocked=true;
                            guardReason='页面提示需要安全验证或重新登录，已停在原页。';
                            break;
                        }
                    }
                }

                // 2. 识别当前章节名称与当前步骤页签
                var activeNode=document.querySelector('#coursetree .posCatalog_active .posCatalog_name, .posCatalog_active .posCatalog_name, .posCatalog_active, .chapter-item.active');
                var chapterTitle=activeNode?clean(activeNode.getAttribute('title')||activeNode.innerText):clean(document.title);
                var prevTitleEl=document.querySelector('.tabtags .active, .tabtags [aria-selected=""true""]')||document.querySelector('.prev_title');
                var stepTabTitle=prevTitleEl?clean(prevTitleEl.getAttribute('title')||prevTitleEl.innerText):'';

                // 3. 查找所有可访问的视频元素
                var videos=[];
                var videoFrames=[];
                function scanVideos(d,frameEl,depth){
                    if(!d||depth>5)return;
                    try{
                        d.querySelectorAll('video').forEach(function(v){
                            videos.push(v);
                            videoFrames.push(frameEl);
                        });
                        d.querySelectorAll('iframe').forEach(function(f){
                            try{if(f.contentDocument)scanVideos(f.contentDocument,f,depth+1);}catch(e){}
                        });
                    }catch(e){}
                }
                scanVideos(document,null,0);

                // 若当前没有视频且存在“视频”页签，自动点击切换到视频页签
                var switchedToVideoTab=false;
                var tabs=Array.from(document.querySelectorAll('.prev_white, .tabtags span, .tabtags [role=""tab""]'));
                var videoTabs=tabs.filter(function(tab){return /^(?:\d+\s*)?视频$/.test(clean(tab.innerText)) && isVisible(tab);});
                if(!guardBlocked && videos.length===0 && shouldPlay){
                    for(var t=0;t<videoTabs.length;t++){
                        if(!videoTabs[t].classList.contains('active') && videoTabs[t].getAttribute('aria-selected')!=='true'){
                            videoTabs[t].click();
                            switchedToVideoTab=true;
                            break;
                        }
                    }
                }

                var hasVideoOrQuiz=videos.length===0 && (videoTabs.length>0 || /视频|测验|考试|作业|讨论/.test(stepTabTitle) ||
                    /测验|考试|作业/.test(chapterTitle) || docs.some(function(doc){
                        return Array.from(doc.querySelectorAll('iframe[src*=""video"" i], [data-type=""video"" i], .singleQuesId, .TiMu, form.answer-box, input[type=""radio""], input[type=""checkbox""]')).some(isVisible);
                    }));
                function isNextButton(el){
                    return isVisible(el) && !el.disabled && el.getAttribute('aria-disabled')!=='true' &&
                        /^下一[节章]$/.test(clean(el.innerText||el.value||el.getAttribute('aria-label')));
                }
                var readingTab=/^(?:\d+\s*)?(?:教材|课件|图文|阅读|资料|讲义|文档|PPT)$/i.test(stepTabTitle);
                // 无页签的教材正文在平台课程卡片内，翻页按钮在外层页面。
                var readingPreview=/\/mycourse\/studentstudy(?:\.html)?\/?$/i.test(location.pathname) && docs.some(function(doc){
                    if(doc===document || doc.readyState!=='complete' ||
                        !/\/knowledge\/cards(?:\.html)?\/?$/i.test(doc.location.pathname) ||
                        !isVisible(doc.defaultView.frameElement)) return false;
                    var content=doc.querySelector('#pageDiv');
                    if(!isVisible(content)) return false;
                    var title=clean(doc.title);
                    if(/^(教材|导读)$/.test(title)) return true;
                    if(title || !clean(content.innerText) || content.querySelector('iframe,video,audio,object,embed,form,input,textarea,button,.ans-attach-ct,[data-type]'))
                        return false;

                    // 无标题时，只接受已加载的文字卡片。同节其他可见页签必须明确标为阅读类。
                    var cardIndex=new URLSearchParams(doc.location.search).get('num');
                    if(!/^\d+$/.test(cardIndex || '')) return false;
                    var currentTabId='dct'+(Number(cardIndex)+1);
                    var cardTabs=Array.from(document.querySelectorAll('[id^=""dct""]')).filter(function(tab){
                        return /^dct\d+$/.test(tab.id) && isVisible(tab);
                    });
                    return cardTabs.some(function(tab){return tab.id===currentTabId;}) && cardTabs.every(function(tab){
                        var label=clean(tab.getAttribute('title') || tab.getAttribute('aria-label') || tab.innerText);
                        return /^(?:\d+\s*)?(?:教材|导读|课件|图文|阅读|资料|讲义|文档|PPT)$/i.test(label) ||
                            (tab.id===currentTabId && /^\d*$/.test(label));
                    });
                });
                var nextButton=[];
                if(document.readyState==='complete' && !guardBlocked && !hasVideoOrQuiz && videos.length===0 && (readingTab || readingPreview))
                    nextButton=Array.from(document.querySelectorAll('#prevNextFocusNext, #right1, .nextChapter, button, a, [role=""button""]')).filter(isNextButton);
                var canAdvanceReading=nextButton.length===1;
                var clickedReadingNext=false;
                if(advanceReading && canAdvanceReading){
                    nextButton[0].scrollIntoView({block:'end',behavior:'instant'});
                    nextButton[0].click();
                    clickedReadingNext=true;
                }

                var paused=true;
                var ended=false;
                var currentTime=0;
                var duration=0;
                if(videos.length>0){
                    var v=videos[0];
                    ended=Boolean(v.ended);
                    currentTime=Number(v.currentTime||0);
                    duration=Number(v.duration||0);
                    if(!guardBlocked && shouldPlay && !ended && v.paused){
                        v.removeAttribute('data-csd-paused');
                        try{v.playbackRate=2;}catch(e){}
                        try{
                            var p=v.play();
                            if(p&&typeof p.catch==='function'){
                                p.catch(function(error){
                                    if(!error || error.name!=='NotAllowedError' || v.hasAttribute('data-csd-paused')) return;
                                    try{v.muted=true;v.play().catch(function(){});}catch(e2){}
                                });
                            }
                        }catch(e){}
                    }
                    paused=Boolean(v.paused);
                }

                // 4. 读取平台显示的任务完成状态（绝不单靠 video.ended 判断）
                var jobFinished=false;
                for(var dIdx=0;dIdx<docs.length;dIdx++){
                    var cd=docs[dIdx];
                    if(cd.querySelector('.ans-job-finished, .icon_Completed, .job-status-completed, [data-job-finished=""true""]')){
                        jobFinished=true;
                        break;
                    }
                    var jobIcons=Array.from(cd.querySelectorAll('.ans-job-icon, .task-status'));
                    for(var j=0;j<jobIcons.length;j++){
                        var jt=clean(jobIcons[j].getAttribute('title')||jobIcons[j].innerText);
                        if(jt.indexOf('已完成')>=0||jt.indexOf('任务点已完成')>=0){
                            jobFinished=true;
                            break;
                        }
                    }
                    if(jobFinished)break;
                }
                // 同时检查左侧课程树当前激活节点是否有完成标志
                if(activeNode){
                    var activeLi=activeNode.closest('.posCatalog_select, .chapter-item')||activeNode;
                    if(activeLi.querySelector('.icon_Completed, .ans-job-finished') || activeLi.getAttribute('data-job-finished')==='true'){
                        jobFinished=true;
                    }
                }

                return JSON.stringify({
                    guardBlocked: guardBlocked,
                    guardReason: guardReason,
                    chapterTitle: chapterTitle,
                    stepTabTitle: stepTabTitle,
                    switchedToVideoTab: switchedToVideoTab,
                    hasVideoOrQuiz: hasVideoOrQuiz,
                    canAdvanceReading: canAdvanceReading,
                    clickedReadingNext: clickedReadingNext,
                    videoCount: videos.length,
                    paused: paused,
                    ended: ended,
                    currentTime: currentTime,
                    duration: duration,
                    jobFinished: jobFinished
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

        private static double GetDouble(Dictionary<string, object> dict, string key)
        {
            object val;
            if (dict == null || !dict.TryGetValue(key, out val) || val == null) return 0;
            double d;
            return Double.TryParse(Convert.ToString(val), out d) ? d : 0;
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            object val;
            return dict != null && dict.TryGetValue(key, out val) && val != null ? Convert.ToString(val) : "";
        }
    }
}
