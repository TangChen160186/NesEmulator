namespace NesEmulator.Core.Ppu;

/// <summary>
/// PPU 输出的 256×240 ARGB 帧缓冲。
///
/// 每帧约 245,760 个像素。桌面前端可以直接通过 <see cref="Pixels"/> 读取
/// 并转换成 Bitmap；不要在这里做 GDI 缩放，保持核心库与 UI 技术无关。
/// </summary>
public sealed class PpuFrameBuffer
{
    /// <summary>NES 可见画面宽度（像素）。</summary>
    public const int Width = 256;

    /// <summary>NES 可见画面高度（扫描线）。</summary>
    public const int Height = 240;

    private readonly uint[] _pixels = new uint[Width * Height];

    /// <summary>只读像素视图，布局为逐行、每像素 ARGB（0xAARRGGBB）。</summary>
    public ReadOnlyMemory<uint> Pixels => _pixels;

    /// <summary>取指定像素。</summary>
    public uint this[int x, int y] => _pixels[y * Width + x];

    /// <summary>写入指定像素，越界时忽略。</summary>
    internal void SetPixel(int x, int y, uint argb)
    {
        if ((uint)x < Width && (uint)y < Height)
            _pixels[y * Width + x] = argb;
    }

    /// <summary>用背景色填充整个画面，通常在复位或关闭渲染时使用。</summary>
    internal void Clear(uint argb)
    {
        Array.Fill(_pixels, argb);
    }

    /// <summary>把整帧复制到外部缓冲区，供 UI 双缓冲。</summary>
    public void CopyTo(Span<uint> destination)
    {
        _pixels.AsSpan().CopyTo(destination);
    }
}
