/*
 * 文件说明：ScreenOcr.cs
 * 职责角色：本文件负责电脑任意窗口的屏幕区域鼠标框选（ScreenSelection）与本地离线中英文文字识别（ScreenOcr）。
 *           使用随包附带的 Tesseract 5.2.0 与 tessdata（chi_sim+eng），图片仅在内存中处理，不操作被截图的来源窗口。
 */
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using Tesseract;

namespace XuexitongDesktop
{
    /// <summary>
    /// 全屏半透明鼠标框选窗口，支持正向或反向拖拽框选任意屏幕区域，按 Esc 取消。
    /// </summary>
    internal sealed class ScreenSelection : Form
    {
        private Point start;
        private Point end;
        private bool dragging;
        public Rectangle SelectedArea { get; private set; }

        internal ScreenSelection()
        {
            Bounds = SystemInformation.VirtualScreen;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            Opacity = 0.25;
            TopMost = true;
            DoubleBuffered = true;
            Cursor = Cursors.Cross;
            KeyPreview = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            start = e.Location;
            end = start;
            dragging = true;
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging)
            {
                end = e.Location;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            dragging = false;
            end = e.Location;
            var r = MakeRectangle();
            if (r.Width > 4 && r.Height > 4)
            {
                SelectedArea = new Rectangle(r.X + Left, r.Y + Top, r.Width, r.Height);
                DialogResult = DialogResult.OK;
                Close();
            }
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (dragging)
            {
                using (var pen = new Pen(Color.Lime, 2))
                {
                    e.Graphics.DrawRectangle(pen, MakeRectangle());
                }
            }
        }

        private Rectangle MakeRectangle()
        {
            return Rectangle.FromLTRB(
                Math.Min(start.X, end.X),
                Math.Min(start.Y, end.Y),
                Math.Max(start.X, end.X),
                Math.Max(start.Y, end.Y));
        }
    }

    /// <summary>
    /// 本地 Tesseract 中英文 OCR 识别器。
    /// </summary>
    internal static class ScreenOcr
    {
        /// <summary>
        /// 截取屏幕指定矩形区域并使用本地 chi_sim+eng 模型识别文字。
        /// </summary>
        internal static string Recognize(Rectangle area)
        {
            using (var image = new Bitmap(area.Width, area.Height))
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.CopyFromScreen(area.Location, Point.Empty, area.Size);
                return RecognizeBitmap(image);
            }
        }

        /// <summary>
        /// 对内存中的 Bitmap 图像执行本地 chi_sim+eng 文字识别。
        /// </summary>
        internal static string RecognizeBitmap(Bitmap image)
        {
            using (var stream = new MemoryStream())
            {
                image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                string baseDir = AppPaths.BaseDir;
                TesseractEnviornment.CustomSearchPath = baseDir;
                string tessdataPath = Path.Combine(baseDir, "tessdata");
                using (var pix = Pix.LoadFromMemory(stream.ToArray()))
                using (var engine = new TesseractEngine(tessdataPath, "chi_sim+eng", EngineMode.Default))
                using (var page = engine.Process(pix))
                {
                    return (page.GetText() ?? "").Trim();
                }
            }
        }
    }
}
