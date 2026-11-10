/*
 * 文件说明：QuestionWindow.cs
 * 职责角色：本文件提供「截图查题 / 粘贴文字查题 / DeepSeek API 密钥设置」窗口。
 *           支持鼠标框选任意电脑窗口并先在本机用 Tesseract 识别为可编辑文字，用户可手动修正识别结果，
 *           再点击查询获取 DeepSeek 返回的【答案】和【解析】；截图模式仅显示答案与解析，不触碰被截图窗口。
 */
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace XuexitongDesktop
{
    internal sealed class QuestionWindow : Form
    {
        private readonly TextBox key = new TextBox();
        private readonly TextBox question = new TextBox();
        private readonly TextBox answer = new TextBox();
        private readonly Button query = new Button();
        private readonly Button saveKeyBtn = new Button();

        internal QuestionWindow(string initialText = null)
        {
            Text = "截图与文字查题 / API 设置";
            Width = 820;
            Height = 720;
            StartPosition = FormStartPosition.CenterParent;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(8) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));

            layout.Controls.Add(new Label
            {
                Text = "DeepSeek API 密钥（使用当前 Windows 用户凭据加密保存在本机）：",
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft
            }, 0, 0);

            var keyPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            keyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            keyPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            key.Dock = DockStyle.Fill;
            key.UseSystemPasswordChar = true;
            saveKeyBtn.Text = "保存密钥";
            saveKeyBtn.Dock = DockStyle.Fill;
            saveKeyBtn.Click += SaveKeyOnly;
            keyPanel.Controls.Add(key, 0, 0);
            keyPanel.Controls.Add(saveKeyBtn, 1, 0);
            layout.Controls.Add(keyPanel, 0, 1);

            layout.Controls.Add(new Label
            {
                Text = "题目内容（可粘贴文字，或点击下方「框选屏幕识题」后在此手动修改识别出的文字）：",
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft
            }, 0, 2);

            question.Multiline = true;
            question.ScrollBars = ScrollBars.Vertical;
            question.Dock = DockStyle.Fill;
            if (initialText != null) question.Text = initialText;
            layout.Controls.Add(question, 0, 3);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
            var capture = new Button { Text = "框选屏幕识题", AutoSize = true };
            capture.Click += CaptureScreen;
            query.Text = "查询答案与解析";
            query.AutoSize = true;
            query.Click += Ask;
            buttons.Controls.Add(capture);
            buttons.Controls.Add(query);
            layout.Controls.Add(buttons, 0, 4);

            answer.Multiline = true;
            answer.ScrollBars = ScrollBars.Vertical;
            answer.Dock = DockStyle.Fill;
            answer.ReadOnly = true;
            layout.Controls.Add(answer, 0, 5);

            Controls.Add(layout);

            try
            {
                string loaded = KeyStore.LoadKey();
                if (!String.IsNullOrEmpty(loaded)) key.Text = loaded;
            }
            catch (Exception)
            {
                MessageBox.Show("无法读取已保存的密钥，请重新填写。");
            }
        }

        private void SaveKeyOnly(object sender, EventArgs e)
        {
            if (String.IsNullOrWhiteSpace(key.Text))
            {
                MessageBox.Show("请先输入有效的 DeepSeek API 密钥。");
                return;
            }
            try
            {
                KeyStore.SaveKey(key.Text.Trim());
                answer.Text = "密钥已使用当前 Windows 用户凭据加密保存成功。";
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存密钥失败：" + ex.Message);
            }
        }

        // 用户框选后先隐藏窗口，避免截图中包含当前窗口；识别结果填入可编辑文本框供用户核对修改。
        private async void CaptureScreen(object sender, EventArgs e)
        {
            Hide();
            try
            {
                using (var selection = new ScreenSelection())
                {
                    if (selection.ShowDialog() != DialogResult.OK) return;
                    var area = selection.SelectedArea;
                    await Task.Delay(250);
                    string text = await Task.Run(() => ScreenOcr.Recognize(area));
                    if (String.IsNullOrWhiteSpace(text))
                    {
                        MessageBox.Show("没有识别到文字，请重新框选清晰的题目区域。");
                        return;
                    }
                    question.Text = text;
                    answer.Text = "已完成本地截图识别。您可以先在上方文本框修改识别文字，再点击「查询答案与解析」。";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("识别失败：" + ex.Message);
            }
            finally
            {
                Show();
                Activate();
            }
        }

        // 在用户主动点击查询时保存密钥并向 DeepSeek 查询答案与解析。
        private async void Ask(object sender, EventArgs e)
        {
            if (String.IsNullOrWhiteSpace(key.Text) || String.IsNullOrWhiteSpace(question.Text))
            {
                MessageBox.Show("请先填写密钥和题目。");
                return;
            }
            query.Enabled = false;
            answer.Text = "正在向 DeepSeek 查询答案与解析……";
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(90);
                    await QueryWithClientAsync(client);
                }
            }
            catch (Exception ex)
            {
                answer.Text = "查询失败：" + ex.Message;
            }
            finally
            {
                query.Enabled = true;
            }
        }

        /// <summary>
        /// 使用指定的 HttpClient 执行保存密钥与查题，方便端到端验收测试注入模拟客户端验证完整流程。
        /// </summary>
        internal async Task QueryWithClientAsync(HttpClient client)
        {
            KeyStore.SaveKey(key.Text.Trim());
            answer.Text = await DeepSeekClient.AskWithAnalysis(client, key.Text.Trim(), question.Text.Trim());
        }
    }
}
