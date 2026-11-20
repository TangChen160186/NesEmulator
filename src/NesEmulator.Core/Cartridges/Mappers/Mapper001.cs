namespace NesEmulator.Core.Cartridges.Mappers;

/// <summary>
/// Mapper 1：MMC1 / SxROM。任天堂最经典的可编程 mapper，使用一个
/// **5 位串行移位寄存器**接收命令，因此一次换页需要连续写 5 次。
///
/// 寄存器选择看 CPU 地址的 bit 13-14：
///   $8000-$9FFF  控制寄存器（镜像、PRG 模式、CHR 模式）
///   $A000-$BFFF  CHR bank 0
///   $C000-$DFFF  CHR bank 1
///   $E000-$FFFF  PRG bank（bit 4 还能禁用 PRG-RAM）
///
/// 每次写入只把数据的 bit 0 压入移位寄存器；第 5 次写入后，
/// 5 位值整体写入目标寄存器。若写入值 bit 7 = 1，则立即复位移位寄存器，
/// 并把 PRG 模式强制为 3（$8000 可切换、$C000 固定最后一块）。
///
/// 控制寄存器：
///   bit 0-1 镜像：0=单屏低，1=单屏高，2=垂直，3=水平
///   bit 2-3 PRG 模式：
///       0/1 = 32 KB 切换（$8000-$FFFF 一起换）
///       2   = $8000 固定第一块，$C000 切换
///       3   = $8000 切换，$C000 固定最后一块
///   bit 4   CHR 模式：0=8 KB，1=两个 4 KB
///
/// 代表游戏：《塞尔达传说》《银河战士》《忍者龙剑传》等。
/// </summary>
public sealed class Mapper001 : Mapper
{
    private byte _shift;
    private int _shiftCount;
    private byte _control;
    private byte _chrBank0;
    private byte _chrBank1;
    private byte _prgBank;
    private long _lastWriteCycle = long.MinValue;

    public Mapper001(Cartridge cartridge) : base(cartridge)
    {
        Reset();
    }

    /// <inheritdoc />
    public override string Name => "MMC1";

    /// <summary>PRG-RAM 是否启用（PRG bank 寄存器 bit 4 = 1 表示禁用）。</summary>
    public override bool PrgRamEnabled => (_prgBank & 0x10) == 0;

    /// <inheritdoc />
    public override void Reset()
    {
        _shift = 0;
        _shiftCount = 0;
        _control = 0x0C; // PRG 模式 3；上电时镜像未定义，这里取单屏低
        _chrBank0 = 0;
        _chrBank1 = 0;
        _prgBank = 0;
        _lastWriteCycle = long.MinValue;
        ApplyMirroring();
    }

    /// <inheritdoc />
    public override void WritePrg(ushort address, byte value, long cpuCycle)
    {
        // 硬件在相邻 CPU 周期连续写入时会忽略后一次写。正常 5 次写循环
        // 至少间隔数周期，所以这条规则主要影响极少数时序 hack。
        // 注意首发写入时 _lastWriteCycle 还是哨兵值，直接相减会整数溢出。
        if (_lastWriteCycle != long.MinValue && cpuCycle - _lastWriteCycle <= 1)
            return;
        _lastWriteCycle = cpuCycle;

        // bit 7 = 1：复位移位寄存器，并强制 PRG 模式 3。
        // 这一步不会提交移位寄存器中尚未完成的 5 位数据。
        if ((value & 0x80) != 0)
        {
            _shift = 0;
            _shiftCount = 0;
            _control |= 0x0C;
            ApplyMirroring();
            return;
        }

        // 串行输入：每次只取 bit 0，放在当前已接收位数的位置。
        _shift |= (byte)((value & 0x01) << _shiftCount);
        _shiftCount++;

        if (_shiftCount < 5)
            return;

        // 第 5 位到齐，根据地址选择目标寄存器。
        int register = (address >> 13) & 0x03;
        switch (register)
        {
            case 0:
                _control = (byte)(_shift & 0x1F);
                ApplyMirroring();
                break;
            case 1:
                _chrBank0 = (byte)(_shift & 0x1F);
                break;
            case 2:
                _chrBank1 = (byte)(_shift & 0x1F);
                break;
            case 3:
                _prgBank = (byte)(_shift & 0x1F);
                break;
        }

        _shift = 0;
        _shiftCount = 0;
    }

    /// <inheritdoc />
    public override byte ReadPrg(ushort address)
    {
        int bankCount = Math.Max(1, Cartridge.PrgRom.Length / 0x4000);
        int mode = (_control >> 2) & 0x03;
        int bank;

        if (mode is 0 or 1)
        {
            // 32 KB 模式：bit 0 被忽略，奇偶两个 16 KB bank 一起切换。
            int first = (_prgBank & 0x0E) % bankCount;
            bank = first + (address >= 0xC000 ? 1 : 0);
            bank %= bankCount;
        }
        else if (mode == 2)
        {
            // $8000 固定 bank 0，$C000 切换到指定 bank。
            bank = address < 0xC000 ? 0 : _prgBank % bankCount;
        }
        else
        {
            // $8000 切换，$C000 固定最后一块（复位向量所在）。
            bank = address < 0xC000 ? _prgBank % bankCount : bankCount - 1;
        }

        int offset = bank * 0x4000 + (address & 0x3FFF);
        return Cartridge.PrgRom[WrapPrg(offset)];
    }

    /// <inheritdoc />
    public override byte ReadChr(ushort address)
    {
        int bankCount = Math.Max(1, Cartridge.ChrRom.Length / 0x1000);
        int bank;

        if ((_control & 0x10) == 0)
        {
            // 8 KB 模式：chrBank0 的 bit 0 被忽略，bit 1-4 选 8 KB 块。
            int first = _chrBank0 & 0x1E;
            bank = first + (address >= 0x1000 ? 1 : 0);
        }
        else
        {
            // 4 KB 模式：低 4 KB 用 bank0，高 4 KB 用 bank1。
            bank = address < 0x1000 ? _chrBank0 : _chrBank1;
        }

        bank %= bankCount;
        int offset = bank * 0x1000 + (address & 0x0FFF);
        return Cartridge.ChrRom[WrapChrRom(offset)];
    }

    /// <summary>把控制寄存器的镜像位同步到卡带的 CIRAM 映射。</summary>
    private void ApplyMirroring()
    {
        // 四屏卡带有独立 VRAM，mapper 的镜像位不改变物理连接。
        if (Cartridge.Header.FourScreen)
            return;

        Cartridge.Mirroring = (_control & 0x03) switch
        {
            0 => Mirroring.SingleScreenLower,
            1 => Mirroring.SingleScreenUpper,
            2 => Mirroring.Vertical,
            _ => Mirroring.Horizontal,
        };
    }
}
