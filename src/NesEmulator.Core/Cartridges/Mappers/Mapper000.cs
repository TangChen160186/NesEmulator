namespace NesEmulator.Core.Cartridges.Mappers;

/// <summary>
/// Mapper 0：NROM。最早期、最简单的任天堂卡带，没有 banking。
///
/// 硬件布局：
///   CPU $8000-$FFFF：32 KB PRG-ROM；若只有 16 KB，则 $C000-$FFFF 镜像 $8000-$BFFF。
///   PPU $0000-$1FFF：8 KB CHR-ROM（或 8 KB CHR-RAM）。
///
/// 代表游戏：《超级马里奥兄弟》《大金刚》《坦克大战》等早期作品。
/// </summary>
public sealed class Mapper000 : Mapper
{
    public Mapper000(Cartridge cartridge) : base(cartridge)
    {
    }

    /// <inheritdoc />
    public override string Name => "NROM";

    /// <inheritdoc />
    public override byte ReadPrg(ushort address)
    {
        int offset = address - 0x8000;

        // 16 KB 卡带只有 0x4000 字节，最高位地址被硬件忽略，形成镜像。
        if (Cartridge.PrgRom.Length == 0x4000)
            offset &= 0x3FFF;

        // 32 KB 卡带直接用 offset；更大的 NROM 不存在，但 WrapPrg 保证安全。
        return Cartridge.PrgRom[WrapPrg(offset)];
    }

    /// <inheritdoc />
    public override byte ReadChr(ushort address)
    {
        // NROM 的 CHR 是 8 KB，地址 $0000-$1FFF 一一对应。
        return Cartridge.ChrRom[WrapChrRom(address)];
    }

    /// <inheritdoc />
    public override void WriteChr(ushort address, byte value)
    {
        // CHR-ROM 物理上不可写；CHR-RAM 已经在 Cartridge 层处理。
    }
}
