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
        private bool isRunning;
        private bool isBusy;
        private int endedWithoutJobFinishTicks;
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
        }

        /// <summary>
        /// 启动或继续连续播放模式。
        /// </summary>
        internal async Task StartOrResumeAsync()
        {
            isRunning = true;
            endedWithoutJobFinishTicks = 0;
            monitorTimer.Start();
            await StepOnceAsync(true);
        }

        /// <summary>
        /// 暂停连续播放，并向当前页面视频发送暂停指令。
        /// </summary>
        internal async Task PauseAsync()
        {
            isRunning = false;
            monitorTimer.Stop();
            if (!IsValidChaoxingUrl(browser))
            {
                UpdateUi("", "请先打开学习通课程页面。");
                return;
            }
            try
            {
                string chapter = await PauseVideosInPageAsync(browser);
                UpdateUi(chapter, "已暂停播放（点击「连续播放」可继续）。");
            }
            catch (Exception ex)
            {
                UpdateUi("", "暂停视频失败：" + ex.Message);
            }
        }

        private async Task OnMonitorTickAsync()
        {
            if (!isRunning || isBusy) return;
            isBusy = true;
            try
            {
                await StepOnceAsync(true);
            }
            finally
            {
                isBusy = false;
            }
        }

        /// <summary>
        /// 执行单步巡检与控制：识别章节、检测拦截状态、恢复播放、核对平台完成标记并在确认完成后自动切章。
        /// </summary>
        internal async Task<CourseStepSnapshot> StepOnceAsync(bool autoPlayAndAdvance)
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
                var serializer = new JavaScriptSerializer();
                string json = serializer.Deserialize<string>(rawJson);
                var data = serializer.Deserialize<Dictionary<string, object>>(json);

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
                        string fJson = serializer.Deserialize<string>(fRaw);
                        if (String.IsNullOrEmpty(fJson)) continue;
                        var fData = serializer.Deserialize<Dictionary<string, object>>(fJson);
                        if (fData == null) continue;

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

                // 3. 未找到视频时停在原页提示
                if (snap.VideoCount == 0)
                {
                    snap.StatusMessage = "当前页面未找到可访问的视频，已停在原页。";
                    UpdateUi(snap.ChapterTitle, snap.StatusMessage);
                    return snap;
                }

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

                UpdateUi(snap.ChapterTitle, snap.StatusMessage);
                return snap;
            }
            catch (Exception ex)
            {
                snap.StatusMessage = "课程播放巡检异常：" + ex.Message;
                UpdateUi(snap.ChapterTitle, snap.StatusMessage);
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
        private static async Task<string> PauseVideosInPageAsync(WebView2 view)
        {
            string script = @"(function(){
                function clean(s){return String(s||'').replace(/\s+/g,' ').trim();}
                function scanPause(d,depth){
                    if(!d||depth>5)return;
                    try{
                        d.querySelectorAll('video').forEach(function(v){try{v.pause();}catch(e){}});
                        d.querySelectorAll('iframe').forEach(function(f){try{if(f.contentDocument)scanPause(f.contentDocument,depth+1);}catch(e){}});
                    }catch(e){}
                }
                scanPause(document,0);
                var active=document.querySelector('#coursetree .posCatalog_active .posCatalog_name, .posCatalog_active .posCatalog_name, .posCatalog_active');
                return active?clean(active.getAttribute('title')||active.innerText):'';
            })()";
            string raw = await view.ExecuteScriptAsync(script);
            foreach (var frame in WebViewFrameRegistry.GetAliveFrames(view))
            {
                try { await WebViewFrameRegistry.ExecuteCourseFrameScriptAsync(frame, script); }
                catch (Exception) { }
            }
            return new JavaScriptSerializer().Deserialize<string>(raw);
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
        private static string BuildInspectScript(bool shouldPlay)
        {
            string playFlag = shouldPlay ? "true" : "false";
            return @"(function(){
                var shouldPlay = " + playFlag + @";
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
                var prevTitleEl=document.querySelector('.prev_title, .tabtags .active');
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
                if(!guardBlocked && videos.length===0 && shouldPlay){
                    var tabs=Array.from(document.querySelectorAll('.prev_white, .tabtags span'));
                    for(var t=0;t<tabs.length;t++){
                        var tabText=clean(tabs[t].innerText);
                        if((tabText==='视频'||tabText==='2视频') && isVisible(tabs[t]) && !tabs[t].classList.contains('active')){
                            tabs[t].click();
                            switchedToVideoTab=true;
                            break;
                        }
                    }
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
                        try{v.playbackRate=2;}catch(e){}
                        try{
                            var p=v.play();
                            if(p&&typeof p.catch==='function'){
                                p.catch(function(){
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
