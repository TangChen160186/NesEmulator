namespace NesEmulator.Core.Cartridges.Mappers;

/// <summary>
/// Mapper 3：CNROM。
///
/// PRG-ROM 固定（通常 16/32 KB），没有 PRG banking；
/// 写入 $8000-$FFFF 的低 2 位选择 8 KB CHR-ROM 银行。
/// 适合程序小、但图形容量较大的游戏，例如《功夫》《光神话》早期版本。
///
/// 部分后期 CNROM 兼容板使用更多 CHR bank 位；这里取低 2 位（4 个 8 KB 窗口），
/// 并用实际 CHR-ROM 大小取模，避免越界。
/// </summary>
public sealed class Mapper003 : Mapper
{
    private int _chrBank;

    public Mapper003(Cartridge cartridge) : base(cartridge)
    {
    }

    /// <inheritdoc />
    public override string Name => "CNROM";

    /// <inheritdoc />
    public override byte ReadPrg(ushort address)
    {
        // CNROM 的 PRG 通常为 16 KB 或 32 KB。16 KB 时高位镜像。
        int offset = address - 0x8000;
        if (Cartridge.PrgRom.Length == 0x4000)
            offset &= 0x3FFF;

        return Cartridge.PrgRom[WrapPrg(offset)];
    }

    /// <inheritdoc />
    public override void WritePrg(ushort address, byte value, long cpuCycle)
    {
        _chrBank = value & 0x03;
    }

    /// <inheritdoc />
    public override byte ReadChr(ushort address)
    {
        int bankCount = Math.Max(1, Cartridge.ChrRom.Length / 0x2000);
        int bank = _chrBank % bankCount;
        int offset = bank * 0x2000 + address;
        return Cartridge.ChrRom[WrapChrRom(offset)];
    }
}
