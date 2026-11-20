namespace NesEmulator.Core.Cartridges.Mappers;

/// <summary>
/// Mapper 4：MMC3 / TxROM。功能最丰富、也最值得学习的经典 mapper：
///   - 8 个 1 KB CHR bank 寄存器；
///   - 2 个可切换的 8 KB PRG bank；
///   - 四种 PRG / CHR 组合模式；
///   - 可编程扫描线计数器 IRQ，用于制作分屏卷轴、状态栏、光栅效果。
///
/// 寄存器地址（注意：A0 参与选择，偶数/奇数地址行为不同）：
///   $8000 偶：bank select（bit 0-2 选寄存器，bit 6 PRG 模式，bit 7 CHR 模式）
///   $8001 奇：写入被选中寄存器的值
///   $A000 偶：镜像（bit 0：0=垂直，1=水平）
///   $A001 奇：PRG-RAM 保护（本模拟器忽略）
///   $C000 偶：IRQ 重载值（latch）
///   $C001 奇：请求在下一个扫描线重载计数器
///   $E000 偶：关闭 IRQ 并清除 pending
///   $E001 奇：打开 IRQ
///
/// MMC3 的 IRQ 由 PPU 的 A12 上升沿驱动。本模拟器采用常见的“扫描线近似”：
/// 渲染开启时，每进入一条扫描线的 dot 260 调用一次 OnScanline。
/// 这对绝大多数游戏足够，但不覆盖依赖 A12 精确时序的极端情况。
///
/// 代表游戏：《超级马里奥兄弟 3》《星之卡比》《忍者神龟》等。
/// </summary>
public sealed class Mapper004 : Mapper
{
    private readonly byte[] _registers = new byte[8];
    private byte _bankSelect;
    private byte _irqLatch;
    private byte _irqCounter;
    private bool _irqReload;
    private bool _irqEnabled;
    private bool _irqPending;

    public Mapper004(Cartridge cartridge) : base(cartridge)
    {
        Reset();
    }

    /// <inheritdoc />
    public override string Name => "MMC3";

    /// <inheritdoc />
    public override bool IrqPending => _irqPending;

    /// <inheritdoc />
    public override void Reset()
    {
        Array.Clear(_registers);
        _bankSelect = 0;
        _irqLatch = 0;
        _irqCounter = 0;
        _irqReload = false;
        _irqEnabled = false;
        _irqPending = false;
        Cartridge.Mirroring = Cartridge.Header.Mirroring;
    }

    /// <inheritdoc />
    public override void WritePrg(ushort address, byte value, long cpuCycle)
    {
        bool odd = (address & 1) != 0;

        switch (address & 0xE000)
        {
            case 0x8000: // $8000-$9FFF：bank select / bank data
                if (!odd)
                {
                    _bankSelect = value;
                }
                else
                {
                    _registers[_bankSelect & 0x07] = value;
                }
                break;

            case 0xA000: // $A000-$BFFF：镜像 / PRG-RAM 保护
                if (!odd && !Cartridge.Header.FourScreen)
                {
                    // MMC3 的 bit 0：0 = 垂直镜像，1 = 水平镜像。
                    Cartridge.Mirroring = (value & 0x01) != 0
                        ? Mirroring.Horizontal
                        : Mirroring.Vertical;
                }
                break;

            case 0xC000: // $C000-$DFFF：IRQ latch / reload
                if (!odd)
                    _irqLatch = value;
                else
                    _irqReload = true; // 下一个扫描线把 latch 装入 counter
                break;

            case 0xE000: // $E000-$FFFF：IRQ 开关 / acknowledge
                if (!odd)
                {
                    _irqEnabled = false;
                    _irqPending = false;
                }
                else
                {
                    _irqEnabled = true;
                }
                break;
        }
    }

    /// <inheritdoc />
    public override byte ReadPrg(ushort address)
    {
        int bankCount = Math.Max(1, Cartridge.PrgRom.Length / 0x2000);
        bool prgMode = (_bankSelect & 0x40) != 0;

        // 四个 8 KB 槽位：
        //   模式 0：R6, R7, second-last, last
        //   模式 1：second-last, R7, R6, last
        // 注意模式下 R7 始终在 $A000；R6 在模式 0 时位于 $8000。
        int slot = (address - 0x8000) / 0x2000;
        int bank = slot switch
        {
            0 => prgMode ? bankCount - 2 : _registers[6],
            1 => _registers[7],
            2 => prgMode ? _registers[6] : bankCount - 2,
            _ => bankCount - 1,
        };

        bank = ((bank % bankCount) + bankCount) % bankCount;
        int offset = bank * 0x2000 + (address & 0x1FFF);
        return Cartridge.PrgRom[WrapPrg(offset)];
    }

    /// <inheritdoc />
    public override byte ReadChr(ushort address)
    {
        int bankCount = Math.Max(1, Cartridge.ChrRom.Length / 0x0400);
        bool chrMode = (_bankSelect & 0x80) != 0;
        int slot = address / 0x0400; // 0..7，每个槽 1 KB

        // MMC3 的 CHR 槽位：
        //   模式 0：[R0,R1 组成 2KB] [R1 同样参与] [R2,R3] [R4,R5]
        // 更准确地说，R0/R1 是 2 KB 粒度的两个寄存器，低 1 位在 2 KB 映射中被忽略。
        int bank;
        if (!chrMode)
        {
            bank = slot switch
            {
                0 or 1 => _registers[0] & 0xFE | (slot & 1),
                2 or 3 => _registers[1] & 0xFE | (slot & 1),
                4 => _registers[2],
                5 => _registers[3],
                6 => _registers[4],
                _ => _registers[5],
            };
        }
        else
        {
            bank = slot switch
            {
                0 => _registers[2],
                1 => _registers[3],
                2 => _registers[4],
                3 => _registers[5],
                4 or 5 => _registers[0] & 0xFE | (slot & 1),
                _ => _registers[1] & 0xFE | (slot & 1),
            };
        }

        bank = ((bank % bankCount) + bankCount) % bankCount;
        int offset = bank * 0x0400 + (address & 0x03FF);
        return Cartridge.ChrRom[WrapChrRom(offset)];
    }

    /// <inheritdoc />
    public override void OnScanline()
    {
        // 计数器逻辑：
        //   - counter 为 0 或收到 reload 请求时，装入 latch；
        //   - 否则递减；
        //   - 递减到 0 且 IRQ 使能时，拉低 IRQ 线。
        if (_irqCounter == 0 || _irqReload)
        {
            _irqCounter = _irqLatch;
            _irqReload = false;
        }
        else
        {
            _irqCounter--;
        }

        if (_irqCounter == 0 && _irqEnabled)
            _irqPending = true;
    }
}
