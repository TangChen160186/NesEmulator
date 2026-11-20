namespace NesEmulator.Core.Ppu;

public sealed partial class Ppu2C02
{
    // ==================== 背景扫描线 ====================

    /// <summary>
    /// 用当前 loopy v 寄存器为一条可见扫描线预取背景。
    ///
    /// 名称表中的一个 tile 是 8×8 像素，由三部分组成：
    ///   - 1 字节 tile 编号（指向 pattern table 的 16 字节图形块）；
    ///   - 1 字节属性（2×2 个 tile 共用，决定使用哪个 4 色背景调色板）；
    ///   - pattern table 中该 tile 的 2 个位平面（low/high），组合出 2 位颜色号。
    ///
    /// 一条 256 像素的扫描线在细滚动下可能跨 33 个 tile，所以缓存 264 像素。
    /// </summary>
    private void BuildBackgroundLine()
    {
        Array.Fill(_backgroundLine, (byte)0xFF);

        if (!IsBackgroundEnabled)
            return;

        int coarseX = _v & 0x1F;
        int coarseY = (_v >> 5) & 0x1F;
        int nameTable = (_v >> 10) & 0x03;
        int fineY = (_v >> 12) & 0x07;
        int patternBase = (_control & 0x10) != 0 ? 0x1000 : 0x0000;

        // 33 个 tile：32 个覆盖 256 像素，另 1 个用于 fine X 的跨界。
        for (int tile = 0; tile < 33; tile++)
        {
            int nameTableAddress = 0x2000 | (nameTable << 10) | (coarseY << 5) | coarseX;
            byte tileIndex = ReadVram((ushort)nameTableAddress);

            // 属性表从名称表 $23C0 开始，每个字节覆盖 4×4 个 tile。
            int attributeAddress = 0x23C0
                                   | (nameTable << 10)
                                   | ((coarseY >> 2) << 3)
                                   | (coarseX >> 2);
            byte attribute = ReadVram((ushort)attributeAddress);

            // 属性字节分成四个 2×2 象限，每个象限 2 位，选择 4 个背景调色板之一。
            int shift = ((coarseY & 0x02) << 1) | (coarseX & 0x02);
            int paletteBase = (attribute >> shift) & 0x03;

            // pattern table 中每个 tile 占 16 字节：前 8 字节是低位平面，后 8 字节是高位平面。
            int patternAddress = patternBase + tileIndex * 16 + fineY;
            byte lowPlane = _cartridge.PpuRead((ushort)patternAddress);
            byte highPlane = _cartridge.PpuRead((ushort)(patternAddress + 8));

            for (int pixel = 0; pixel < 8; pixel++)
            {
                int bit = 7 - pixel; // pattern 字节的 MSB 是最左边的像素
                int color = ((lowPlane >> bit) & 1) | (((highPlane >> bit) & 1) << 1);

                _backgroundLine[tile * 8 + pixel] = color == 0
                    ? (byte)0xFF // 颜色 0 对背景透明，最终显示 backdrop
                    : (byte)(paletteBase * 4 + color);
            }

            // 一行内 coarse X 递增；从 31 回到 0 时横向名称表翻转。
            coarseX++;
            if (coarseX == 32)
            {
                coarseX = 0;
                nameTable ^= 1;
            }
        }
    }

    /// <summary>渲染关闭时，整条可见扫描线输出全局背景色 $3F00。</summary>
    private void FillBackdropLine()
    {
        uint backdrop = PpuPalette.ToArgb(_paletteRam[0], _mask);
        for (int x = 0; x < PpuFrameBuffer.Width; x++)
            FrameBuffer.SetPixel(x, Scanline, backdrop);
    }

    // ==================== 精灵 ====================

    /// <summary>
    /// 对当前扫描线做精灵评估（sprite evaluation）。
    ///
    /// OAM 中每个精灵 4 字节：
    ///   byte 0：Y 坐标（实际显示从 Y+1 开始）
    ///   byte 1：tile 编号
    ///   byte 2：属性（调色板、优先级、水平/垂直翻转）
    ///   byte 3：X 坐标
    ///
    /// 真实 PPU 在扫描线中分阶段评估，最多保留 8 个精灵；第 9 个会置 overflow。
    /// 本实现直接在行首一次性评估，结果等价于常见情况。
    /// </summary>
    private void EvaluateSpritesForScanline()
    {
        Array.Fill(_spriteLine, (byte)0xFF);
        Array.Clear(_spriteInFront);
        Array.Clear(_spriteZeroOpaque);
        _spriteCount = 0;

        if (!IsSpriteEnabled)
            return;

        int spriteHeight = (_control & 0x20) != 0 ? 16 : 8;
        int found = 0;

        for (int i = 0; i < 64; i++)
        {
            int oamOffset = i * 4;
            int spriteY = _oam[oamOffset] + 1;
            if (Scanline < spriteY || Scanline >= spriteY + spriteHeight)
                continue;

            if (found < 8)
            {
                _spritesOnLine[found++] = new SpriteSlot(
                    _oam[oamOffset + 3],               // X
                    spriteY,                           // Y（已 +1）
                    _oam[oamOffset + 1],               // tile
                    _oam[oamOffset + 2],               // attributes
                    i == 0);                           // 是否为 sprite 0
            }
            else
            {
                // 简化版 overflow：超过 8 个精灵就置位，不模拟 2C02 的 bug。
                _status |= 0x20;
            }
        }

        _spriteCount = found;

        // 按 OAM 顺序绘制；同一像素由更靠前的精灵优先。
        for (int s = 0; s < _spriteCount; s++)
        {
            DrawSprite(_spritesOnLine[s], spriteHeight);
        }
    }

    private void DrawSprite(SpriteSlot sprite, int spriteHeight)
    {
        int row = Scanline - sprite.Y;

        // 属性 bit7：垂直翻转。
        if ((sprite.Attributes & 0x80) != 0)
            row = spriteHeight - 1 - row;

        int tile = sprite.Tile;
        int patternBase;

        if (spriteHeight == 16)
        {
            // 8×16 精灵：tile 的 bit0 选 pattern table，编号的低位被忽略。
            patternBase = (tile & 0x01) != 0 ? 0x1000 : 0x0000;
            tile &= 0xFE;

            if (row >= 8)
            {
                tile++;
                row -= 8;
            }
        }
        else
        {
            // 8×8 精灵：PPUCTRL bit3 选 pattern table。
            patternBase = (_control & 0x08) != 0 ? 0x1000 : 0x0000;
        }

        int patternAddress = patternBase + tile * 16 + row;
        byte lowPlane = _cartridge.PpuRead((ushort)patternAddress);
        byte highPlane = _cartridge.PpuRead((ushort)(patternAddress + 8));

        int spritePalette = 0x10 + (sprite.Attributes & 0x03) * 4;
        bool flipHorizontal = (sprite.Attributes & 0x40) != 0;
        bool inFront = (sprite.Attributes & 0x20) == 0;

        for (int column = 0; column < 8; column++)
        {
            int screenX = sprite.X + column;
            if ((uint)screenX >= PpuFrameBuffer.Width)
                continue;

            // 左 8 像素裁剪（PPUMASK bit2）。
            if (screenX < 8 && (_mask & 0x04) == 0)
                continue;

            int bit = flipHorizontal ? column : 7 - column;
            int color = ((lowPlane >> bit) & 1) | (((highPlane >> bit) & 1) << 1);
            if (color == 0)
                continue; // 精灵颜色 0 透明

            if (_spriteLine[screenX] == 0xFF)
            {
                _spriteLine[screenX] = (byte)(spritePalette + color);
                _spriteInFront[screenX] = inFront;
            }

            // sprite 0 hit 与优先级无关：只要 sprite 0 在该像素不透明且背景也不透明，
            // 并且不是屏幕最右列，就会置位。
            if (sprite.IsSpriteZero)
                _spriteZeroOpaque[screenX] = true;
        }
    }

    // ==================== 像素合成 ====================

    /// <summary>把一个像素与背景、精灵、backdrop 合成后写入帧缓冲。</summary>
    private void RenderPixel(int x)
    {
        // ---- 背景层 ----
        byte background = 0xFF; // 0xFF = 透明
        if (IsBackgroundEnabled && !(x < 8 && (_mask & 0x02) == 0))
            background = _backgroundLine[x + _fineX];

        // ---- 精灵层 ----
        byte sprite = 0xFF;
        bool spriteInFront = false;
        if (IsSpriteEnabled && !(x < 8 && (_mask & 0x04) == 0))
        {
            sprite = _spriteLine[x];
            spriteInFront = _spriteInFront[x];
        }

        // ---- Sprite 0 hit ----
        // 条件：sprite 0 与背景同时不透明、x != 255、都未被左裁剪隐藏。
        if (background != 0xFF && sprite != 0xFF && _spriteZeroOpaque[x] && x != 255)
            _status |= 0x40;

        // ---- 合成优先级 ----
        // background/sprite 中保存的是“调色板 RAM 地址”（0-31），
        // 还需再查一次 32 字节调色板，才能得到 6 位 NES 颜色号。
        int paletteAddress;
        if (sprite != 0xFF && (background == 0xFF || spriteInFront))
            paletteAddress = sprite;
        else if (background != 0xFF)
            paletteAddress = background;
        else
            paletteAddress = 0; // 全局背景色 $3F00

        byte nesColor = _paletteRam[paletteAddress & 0x1F];
        FrameBuffer.SetPixel(x, Scanline, PpuPalette.ToArgb(nesColor, _mask));
    }
}
