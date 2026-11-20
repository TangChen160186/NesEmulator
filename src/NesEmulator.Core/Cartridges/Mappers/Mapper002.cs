namespace NesEmulator.Core.Cartridges.Mappers;

/// <summary>
/// Mapper 2：UxROM（UNROM / UOROM）。
///
/// 硬件布局（16 KB 窗口）：
///   CPU $8000-$BFFF：可切换的 16 KB PRG-ROM 银行（由写入值选择）
///   CPU $C000-$FFFF：固定为最后一个 16 KB 银行（复位向量总在这里）
///   PPU：通常使用 8 KB CHR-RAM，因此游戏会把图形解压后写进 CHR-RAM。
///
/// 换页操作：向 $8000-$FFFF 任意地址写入银行号，低 4 位有效。
/// 代表游戏：《魂斗罗》《恶魔城》《洛克人》等。
///
/// 备注：真实 UNROM 常有“总线冲突”，写入值会与 ROM 当前字节按位与。
/// 这里为兼容性采用宽松处理，不模拟冲突；绝大多数商业 ROM 不依赖它。
/// </summary>
public sealed class Mapper002 : Mapper
{
    private int _bank;

    public Mapper002(Cartridge cartridge) : base(cartridge)
    {
        _bank = 0;
    }

    /// <inheritdoc />
    public override string Name => "UxROM";

    /// <inheritdoc />
    public override byte ReadPrg(ushort address)
    {
        int bankCount = Math.Max(1, Cartridge.PrgRom.Length / 0x4000);

        if (address < 0xC000)
        {
            // $8000-$BFFF：当前选中的 16 KB bank。
            int bank = _bank % bankCount;
            int offset = bank * 0x4000 + (address - 0x8000);
            return Cartridge.PrgRom[WrapPrg(offset)];
        }

        // $C000-$FFFF：最后一个 bank，保证复位/中断向量始终可访问。
        int fixedOffset = (bankCount - 1) * 0x4000 + (address - 0xC000);
        return Cartridge.PrgRom[WrapPrg(fixedOffset)];
    }

    /// <inheritdoc />
    public override void WritePrg(ushort address, byte value, long cpuCycle)
    {
        // 标准 UxROM（≤256 KB）有总线冲突：ROM 在写周期仍驱动数据总线，
        // 写入值会与当前地址的 ROM 字节按位与。正规游戏把 bank 表放在
        // 固定 ROM 区，使写地址处的字节正好允许目标 bank 通过。
        // 512 KB 的 UNROM-512 使用 Flash/额外锁存器，常被视为无冲突变体，
        // 因此在更大的 ROM 上跳过 AND，并允许 bit4 参与 bank 选择。
        bool hasBusConflicts = Cartridge.PrgRom.Length <= 0x40000;
        if (hasBusConflicts)
        {
            int bankCount = Math.Max(1, Cartridge.PrgRom.Length / 0x4000);
            int currentBank = address < 0xC000 ? _bank % bankCount : bankCount - 1;
            int romOffset = currentBank * 0x4000 + (address & 0x3FFF);
            value &= Cartridge.PrgRom[WrapPrg(romOffset)];
        }

        _bank = value & 0x1F;
    }

    /// <inheritdoc />
    public override byte ReadChr(ushort address) => Cartridge.ChrRom[WrapChrRom(address)];
}
