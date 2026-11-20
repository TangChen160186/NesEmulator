namespace NesEmulator.Core.Ppu;

/// <summary>
/// PPU 的 64 色主调色板到 RGB 的转换。
///
/// NES 不存储 RGB 像素，只存储 6 位调色板索引。最终颜色由 PPU 的模拟视频
/// 信号和电视制式决定。不同模拟器/电视机使用的调色板会有细微差异：
/// 这里采用社区广泛使用的“2C02 NTSC”近似值，便于得到熟悉的画面。
///
/// 重要概念：
///   - 每个背景/精灵调色板有 4 个 6 位颜色索引；
///   - 调色板中的 0 号颜色 `$3F00` 是全局背景色；
///   - 精灵颜色 0 永远是“透明”，不会显示；
///   - 灰度位（PPUMASK bit 0）会把 6 位索引的低 4 位清零，只保留 4 个灰度级。
/// </summary>
public static class PpuPalette
{
    /// <summary>2C02 的 64 个主颜色，ARGB（alpha 固定为 0xFF）。</summary>
    private static readonly uint[] Colors =
    [
        0xFF666666, 0xFF002A88, 0xFF1412A7, 0xFF3B00A4, 0xFF5C007E, 0xFF6E0040, 0xFF6C0600, 0xFF561D00,
        0xFF333500, 0xFF0B4800, 0xFF005200, 0xFF004F08, 0xFF00404D, 0xFF000000, 0xFF000000, 0xFF000000,
        0xFFADADAD, 0xFF155FD9, 0xFF4240FF, 0xFF7527FE, 0xFFA01ACC, 0xFFB71E7B, 0xFFB53120, 0xFF994E00,
        0xFF6B6D00, 0xFF388700, 0xFF0C9300, 0xFF008F32, 0xFF007C8D, 0xFF000000, 0xFF000000, 0xFF000000,
        0xFFFFFEFF, 0xFF64B0FF, 0xFF9290FF, 0xFFC676FF, 0xFFF36AFF, 0xFFFE6ECC, 0xFFFE8170, 0xFFEA9E22,
        0xFFBCBE00, 0xFF88D800, 0xFF5CE430, 0xFF45E082, 0xFF48CDDE, 0xFF4F4F4F, 0xFF000000, 0xFF000000,
        0xFFFFFEFF, 0xFFC0DFFF, 0xFFD3D2FF, 0xFFE8C8FF, 0xFFFBC2FF, 0xFFFEC4EA, 0xFFFECCC5, 0xFFF7D8A5,
        0xFFE4E594, 0xFFCFEF96, 0xFFBDF4AB, 0xFFB3F3CC, 0xFFB5EBF2, 0xFFB8B8B8, 0xFF000000, 0xFF000000,
    ];

    /// <summary>把 PPU 调色板索引转为 ARGB，并应用灰度/强调位。</summary>
    /// <param name="paletteIndex">0-31 的调色板 RAM 索引。</param>
    /// <param name="ppuMask">PPUMASK 寄存器，bit 0=灰度，bit 5-7=红/绿/蓝强调。</param>
    public static uint ToArgb(int paletteIndex, byte ppuMask = 0)
    {
        int colorIndex = paletteIndex & 0x3F;

        // 灰度模式：只保留 bit4-5，即 4 个灰度档（0x00/0x10/0x20/0x30）。
        if ((ppuMask & 0x01) != 0)
            colorIndex &= 0x30;

        uint argb = Colors[colorIndex];
        int r = (int)((argb >> 16) & 0xFF);
        int g = (int)((argb >> 8) & 0xFF);
        int b = (int)(argb & 0xFF);

        // 强调色在真实硬件上是改变色相/亮度，这里做一个简单的亮度近似。
        // 它不是严格准确的 NTSC 信号模型，但足以表达红/绿/蓝色调变化。
        if ((ppuMask & 0x20) != 0) { r = Math.Min(255, r + 32); g = g * 3 / 4; b = b * 3 / 4; } // 红
        if ((ppuMask & 0x40) != 0) { g = Math.Min(255, g + 32); r = r * 3 / 4; b = b * 3 / 4; } // 绿
        if ((ppuMask & 0x80) != 0) { b = Math.Min(255, b + 32); r = r * 3 / 4; g = g * 3 / 4; } // 蓝

        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }
}
