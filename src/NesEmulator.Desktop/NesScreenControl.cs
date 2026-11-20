using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using NesEmulator.Core.Ppu;

namespace NesEmulator.Desktop;

/// <summary>
/// 负责显示 256×240 NES 画面的控件。
///
/// 关键点：
///   - 用一张 32bpp 的 Bitmap 作为双缓冲，模拟线程只更新它，不直接画控件；
///   - 缩放使用最近邻插值，保持像素锐利（NES 只有 256×240）；
///   - 画面按 16:15 等比缩放并居中，两侧留黑边。
/// </summary>
public sealed class NesScreenControl : Control
{
    private readonly object _gate = new();
    private readonly Bitmap _bitmap = new(PpuFrameBuffer.Width, PpuFrameBuffer.Height, PixelFormat.Format32bppRgb);
    private readonly int[] _scratch = new int[PpuFrameBuffer.Width * PpuFrameBuffer.Height];
    private string? _overlay;

    public NesScreenControl()
    {
        DoubleBuffered = true;
        BackColor = Color.Black;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>由模拟线程调用：复制一帧并请求重绘。</summary>
    public void SubmitFrame(ReadOnlySpan<uint> pixels)
    {
        if (pixels.Length < _scratch.Length)
            return;

        lock (_gate)
        {
            // uint ARGB -> int，像素格式相同，直接内存转换。
            MemoryMarshal.Cast<uint, int>(pixels[.._scratch.Length]).CopyTo(_scratch);

            BitmapData data = _bitmap.LockBits(
                new Rectangle(0, 0, _bitmap.Width, _bitmap.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppRgb);
            try
            {
                Marshal.Copy(_scratch, 0, data.Scan0, _scratch.Length);
            }
            finally
            {
                _bitmap.UnlockBits(data);
            }
        }
    }

    /// <summary>画一层居中提示文字（暂停、无 ROM 等）。</summary>
    public void SetOverlay(string? text)
    {
        _overlay = text;
        Invalidate();
    }

    /// <summary>复制当前画面，供“保存截图”使用。</summary>
    public Bitmap CaptureBitmap()
    {
        lock (_gate)
            return (Bitmap)_bitmap.Clone();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        graphics.Clear(Color.Black);

        // 计算保持 16:15 宽高比的显示区域。
        float scale = Math.Min(ClientSize.Width / (float)PpuFrameBuffer.Width,
                               ClientSize.Height / (float)PpuFrameBuffer.Height);
        int width = Math.Max(1, (int)(PpuFrameBuffer.Width * scale));
        int height = Math.Max(1, (int)(PpuFrameBuffer.Height * scale));
        int x = (ClientSize.Width - width) / 2;
        int y = (ClientSize.Height - height) / 2;

        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.SmoothingMode = SmoothingMode.None;

        lock (_gate)
        {
            graphics.DrawImage(_bitmap, new Rectangle(x, y, width, height),
                new Rectangle(0, 0, _bitmap.Width, _bitmap.Height), GraphicsUnit.Pixel);
        }

        if (!string.IsNullOrEmpty(_overlay))
        {
            using var font = new Font("Microsoft YaHei UI", Math.Max(12, height / 20f), FontStyle.Bold);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var shadow = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
            using var brush = new SolidBrush(Color.White);

            var rectangle = new RectangleF(0, 0, ClientSize.Width, ClientSize.Height);
            graphics.DrawString(_overlay, font, shadow, new RectangleF(2, 2, ClientSize.Width, ClientSize.Height), format);
            graphics.DrawString(_overlay, font, brush, rectangle, format);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _bitmap.Dispose();

        base.Dispose(disposing);
    }
}
