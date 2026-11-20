namespace NesEmulator.Core.Cartridges;

/// <summary>ROM 容器格式。iNES 1.0 是 1980 年代模拟器社区的事实标准，NES 2.0 是其向后兼容扩展。</summary>
public enum RomFormat
{
    /// <summary>传统 16 字节 iNES 头。</summary>
    INes,

    /// <summary>NES 2.0：复用 iNES 头中的保留位保存更多 mapper/尺寸信息。</summary>
    Nes20,
}

/// <summary>
/// iNES / NES 2.0 文件头（前 16 字节）的解析结果。
///
/// 一个 .nes 文件通常长这样：
///   [16 字节文件头][可选 512 字节 trainer][PRG-ROM][CHR-ROM]
///
/// 文件头字段（偏移量从 0 开始）：
///   0-3   "NES" + 0x1A 魔数
///   4     PRG-ROM 大小，单位 16 KB（低 8 位）
///   5     CHR-ROM 大小，单位 8 KB（低 8 位）
///   6     flags 6：镜像、电池存档、trainer、四屏、mapper 低 4 位
///   7     flags 7：主机类型、NES 2.0 标识、mapper 中 4 位
///   8     flags 8：mapper 高 4 位（NES 2.0 中还包含 submapper）
///   9     flags 9：PRG/CHR 大小的高 4 位（NES 2.0 专用）
///   10    flags 10：PRG-RAM / PRG-NVRAM 大小（NES 2.0 专用）
///   11-15 保留（通常填 0）
///
/// 注意：iNES 文件头没有校验和，也没有统一的版权字段；解析时必须对尺寸和
/// 文件实际长度做严格检查，否则畸形 ROM 会让模拟器越界崩溃。
/// </summary>
public sealed class InesHeader
{
    /// <summary>iNES 固定文件头长度。</summary>
    public const int HeaderSize = 16;

    /// <summary>可选 trainer 的长度。</summary>
    public const int TrainerSize = 512;

    /// <summary>PRG-ROM 总字节数（CPU 执行代码所在的只读区）。</summary>
    public int PrgRomSize { get; private init; }

    /// <summary>CHR-ROM 总字节数（PPU 图块数据）；为 0 表示卡带使用 CHR-RAM。</summary>
    public int ChrRomSize { get; private init; }

    /// <summary>mapper 编号。常见：0=NROM，1=MMC1，2=UxROM，3=CNROM，4=MMC3。</summary>
    public int MapperNumber { get; private init; }

    /// <summary>NES 2.0 submapper；iNES 1.0 中恒为 0。</summary>
    public int SubMapper { get; private init; }

    /// <summary>容器格式。</summary>
    public RomFormat Format { get; private init; }

    /// <summary>头中声明的镜像方式（mapper 之后还可以动态改写它）。</summary>
    public Mirroring Mirroring { get; private init; }

    /// <summary>是否使用四屏 VRAM。四屏 ROM 需要 PPU 额外 2 KB 名称表 RAM。</summary>
    public bool FourScreen { get; private init; }

    /// <summary>是否有电池供电的 PRG-RAM（存档）。</summary>
    public bool HasBattery { get; private init; }

    /// <summary>是否带 512 字节 trainer。模拟器会跳过它，不执行。</summary>
    public bool HasTrainer { get; private init; }

    /// <summary>PRG-RAM 分配大小。没有特殊声明时给 8 KB，足以兼容绝大多数卡带。</summary>
    public int PrgRamSize { get; private init; }

    /// <summary>CHR-RAM 分配大小。CHR-ROM 为 0 时使用。</summary>
    public int ChrRamSize { get; private init; }

    /// <summary>解析 16 字节文件头。此方法不读取 ROM 正文。</summary>
    /// <exception cref="InvalidDataException">魔数、版本或尺寸字段非法。</exception>
    public static InesHeader Parse(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize)
            throw new InvalidDataException($"iNES 文件头至少需要 {HeaderSize} 字节，实际只有 {header.Length} 字节。");

        // 魔数 "NES\x1A"，最后一个是 DOS 文本结束符。
        if (header[0] != (byte)'N' || header[1] != (byte)'E' || header[2] != (byte)'S' || header[3] != 0x1A)
            throw new InvalidDataException("不是有效的 iNES 文件：文件头缺少 NES\\x1A 魔数。");

        byte flags6 = header[6];
        byte flags7 = header[7];
        byte flags8 = header[8];
        byte flags9 = header[9];
        byte flags10 = header[10];

        // iNES 2.0 把 flags7 的 bit 2-3 规定为二进制的 10。
        bool isNes20 = (flags7 & 0x0C) == 0x08;
        var format = isNes20 ? RomFormat.Nes20 : RomFormat.INes;

        int mapper = (flags6 >> 4) | (flags7 & 0xF0);
        int submapper = 0;

        if (isNes20)
        {
            // NES 2.0 的 mapper 号由三段拼成：高 4 位 + 中 4 位 + 低 4 位。
            mapper |= (flags8 & 0x0F) << 8;
            submapper = (flags8 >> 4) & 0x0F;
        }

        // NES 2.0 在 flags 9 的高/低半字节保存 PRG/CHR 尺寸的“高 4 位”。
        // 对常见的家用 ROM（尺寸小于 4 MB / 2 MB）这里通常为 0。
        // 指数-乘数记法（高半字节为 0xF）只用于超大 ROM，本学习型模拟器明确拒绝。
        if (isNes20 && (flags9 & 0x0F) == 0x0F)
            throw new NotSupportedException("暂不支持 NES 2.0 的超大 PRG-ROM 指数-乘数尺寸写法。");

        int prgBanks = header[4] | (isNes20 ? (flags9 & 0x0F) << 8 : 0);
        int chrBanks = header[5] | (isNes20 ? (flags9 & 0xF0) << 4 : 0);

        if (prgBanks == 0)
            throw new InvalidDataException("ROM 没有声明的 PRG-ROM 数据，无法执行。");

        // 经典 iNES 的尺寸字段是 8 位，NES 2.0 扩展成 12 位，单位不变。
        int prgRomSize = prgBanks * 16 * 1024;
        int chrRomSize = chrBanks * 8 * 1024;

        // 2 MB 以上的 PRG/CHR 对学习用途没有意义；设上限可防止恶意头造成过大分配。
        const int MaxRomSize = 64 * 1024 * 1024;
        if (prgRomSize is <= 0 or > MaxRomSize || chrRomSize > MaxRomSize)
            throw new InvalidDataException($"ROM 尺寸异常：PRG={prgRomSize} 字节，CHR={chrRomSize} 字节。");

        bool fourScreen = (flags6 & 0x08) != 0;
        var mirroring = fourScreen
            ? Mirroring.FourScreen
            : (flags6 & 0x01) != 0 ? Mirroring.Vertical : Mirroring.Horizontal;

        int prgRamSize = 8 * 1024;
        if (isNes20)
        {
            // NES 2.0 flags 10：低半字节是 NVRAM，高半字节是 PRG-RAM，单位 = 64 << 值。
            int nvram = flags10 & 0x0F;
            int ram = (flags10 >> 4) & 0x0F;
            int shift = Math.Max(nvram, ram);
            if (shift > 0)
            {
                // 最多分配 512 KB，避免畸形头一次分配几 GB。
                prgRamSize = Math.Min(512 * 1024, 64 << Math.Min(shift, 13));
            }
        }

        // CHR-ROM 为 0 表示卡带使用 CHR-RAM（例如 UNROM/MMC1 开发板）。
        // 标准容量 8 KB；NES 2.0 可用 flags 10 表示不同大小，这里用 8 KB 起步。
        int chrRamSize = chrRomSize == 0 ? 8 * 1024 : 0;

        return new InesHeader
        {
            Format = format,
            PrgRomSize = prgRomSize,
            ChrRomSize = chrRomSize,
            MapperNumber = mapper,
            SubMapper = submapper,
            Mirroring = mirroring,
            FourScreen = fourScreen,
            HasBattery = (flags6 & 0x02) != 0,
            HasTrainer = (flags6 & 0x04) != 0,
            PrgRamSize = prgRamSize,
            ChrRamSize = chrRamSize,
        };
    }

    /// <summary>返回一行适合显示在窗口标题栏的摘要。</summary>
    public string ToSummary() =>
        $"{Format} mapper {MapperNumber}" +
        (SubMapper != 0 ? $".{SubMapper}" : string.Empty) +
        $" · PRG {PrgRomSize / 1024}KB · " +
        (ChrRomSize > 0 ? $"CHR {ChrRomSize / 1024}KB" : $"CHR-RAM {ChrRamSize / 1024}KB") +
        $" · {Mirroring}";
}
