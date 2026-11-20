using NesEmulator.Core.Apu;
using NesEmulator.Core.Cartridges;
using NesEmulator.Core.Cpu;
using NesEmulator.Core.Input;
using NesEmulator.Core.Ppu;

namespace NesEmulator.Core.Bus;

/// <summary>
/// NES 主板总线（CPU 视角）。
///
/// 6502 有 16 位地址空间，不同地址段被硬连线到不同芯片：
///
///   $0000-$07FF  2 KB CPU RAM
///   $0800-$1FFF  RAM 镜像（每 $0800 重复一次，共 4 个镜像）
///   $2000-$3FFF  PPU 寄存器（每 8 字节重复）
///   $4000-$4013  APU 寄存器
///   $4014        OAM DMA
///   $4015        APU 状态
///   $4016        手柄 1 / 选通
///   $4017        手柄 2
///   $4020-$FFFF  卡带（PRG-RAM、mapper 寄存器、PRG-ROM）
///
/// 总线还负责把 CPU 周期转换成 PPU dot（×3）和 APU 周期（×1），
/// 并汇总 NMI/IRQ 线供 CPU 在指令边界采样。
/// </summary>
public sealed class NesBus : ICpuBus
{
    private readonly byte[] _cpuRam = new byte[0x0800];
    private readonly Controller _controller1;
    private readonly Controller _controller2;
    private readonly byte[] _dmaPage = new byte[256];

    private byte _openBus;
    private int _dmaStallCycles;

    public NesBus(Cartridge cartridge, Controller controller1, Controller controller2)
    {
        Cartridge = cartridge;
        _controller1 = controller1;
        _controller2 = controller2;

        Ppu = new Ppu2C02(cartridge);
        // DMC 会主动从 CPU 总线读取采样数据，所以需要把 Read 交给 APU。
        Apu = new Apu2A03(Read);
    }

    public Cartridge Cartridge { get; }
    public Ppu2C02 Ppu { get; }
    public Apu2A03 Apu { get; }

    /// <summary>当前 CPU 周期，供 mapper 识别连续周期写入等时序。</summary>
    public long CpuCycle { get; set; }

    /// <summary>NMI 线电平（由 PPU 驱动）。</summary>
    public bool NmiLine => Ppu.NmiOutput;

    /// <summary>IRQ 线：APU 帧/DMC IRQ 与 mapper IRQ 的“或”。</summary>
    public bool IrqLine => Apu.IrqPending || Cartridge.IrqPending;

    /// <summary>CPU 读取任意地址。</summary>
    public byte Read(ushort address)
    {
        byte value;

        if (address < 0x2000)
        {
            // 2 KB RAM 在 $0000-$1FFF 内镜像 4 次。
            value = _cpuRam[address & 0x07FF];
        }
        else if (address < 0x4000)
        {
            // $2000-$3FFF：PPU 的 8 个寄存器每 8 字节镜像。
            value = Ppu.ReadRegister(address);
        }
        else if (address < 0x4020)
        {
            value = ReadIoRegister(address);
        }
        else
        {
            value = Cartridge.CpuRead(address);
        }

        _openBus = value;
        return value;
    }

    /// <summary>CPU 写任意地址。</summary>
    public void Write(ushort address, byte value)
    {
        if (address < 0x2000)
        {
            _cpuRam[address & 0x07FF] = value;
            return;
        }

        if (address < 0x4000)
        {
            Ppu.WriteRegister(address, value);
            return;
        }

        if (address < 0x4020)
        {
            WriteIoRegister(address, value);
            return;
        }

        Cartridge.CpuWrite(address, value, CpuCycle);
    }

    private byte ReadIoRegister(ushort address)
    {
        switch (address)
        {
            case 0x4015:
                return Apu.ReadStatus();

            case 0x4016:
                // 手柄只驱动 D0，D1-D7 是开总线。
                return (byte)((_openBus & 0xFE) | _controller1.Read());

            case 0x4017:
                return (byte)((_openBus & 0xFE) | _controller2.Read());

            default:
                // APU 的 $4000-$4013 在 2A03 上通常是只写的；
                // $4014 也是只写。读回开总线值。
                return _openBus;
        }
    }

    private void WriteIoRegister(ushort address, byte value)
    {
        switch (address)
        {
            case >= 0x4000 and <= 0x4013:
                Apu.WriteRegister(address, value);
                break;

            case 0x4014:
                StartOamDma(value);
                break;

            case 0x4015:
                Apu.WriteRegister(address, value);
                break;

            case 0x4016:
                // 两个手柄共享同一条选通信号。
                _controller1.WriteStrobe(value);
                _controller2.WriteStrobe(value);
                break;

            case 0x4017:
                Apu.WriteRegister(address, value);
                break;
        }
    }

    /// <summary>
    /// 执行 OAM DMA：从 CPU 页 $XX00-$XXFF 复制 256 字节到 PPU OAM。
    /// DMA 会暂停 CPU 513 或 514 周期，取决于起始周期对齐。
    /// </summary>
    private void StartOamDma(byte page)
    {
        ushort baseAddress = (ushort)(page << 8);
        for (int i = 0; i < 256; i++)
            _dmaPage[i] = Read((ushort)(baseAddress + i));

        Ppu.WriteOamDma(_dmaPage);
        _dmaStallCycles = (CpuCycle & 1) != 0 ? 514 : 513;
    }

    /// <summary>取出并清除待处理的 DMA 停顿周期。</summary>
    public int TakeDmaStallCycles()
    {
        int cycles = _dmaStallCycles;
        _dmaStallCycles = 0;
        return cycles;
    }

    /// <summary>推进 PPU（3 倍速）和 APU（1 倍速）。</summary>
    public void Tick(int cpuCycles)
    {
        if (cpuCycles <= 0)
            return;

        Ppu.Step(cpuCycles * 3);
        Apu.Step(cpuCycles);
    }

    /// <summary>清空总线的临时状态（不断电复位时保留 RAM 内容）。</summary>
    public void Reset()
    {
        _openBus = 0;
        _dmaStallCycles = 0;
    }
}
