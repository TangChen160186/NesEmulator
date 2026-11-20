using NesEmulator.Core.Apu;
using NesEmulator.Core.Bus;
using NesEmulator.Core.Cartridges;
using NesEmulator.Core.Cpu;
using NesEmulator.Core.Input;
using NesEmulator.Core.Ppu;

namespace NesEmulator.Core.Emulation;

/// <summary>
/// 一台完整的 NES 主机。它把 CPU、PPU、APU、总线、卡带和两个手柄组装起来，
/// 并负责最重要的“时钟同步”：
///
///   CPU 执行 1 个周期  →  PPU 前进 3 个 dot  →  APU 前进 1 个周期
///
/// 因为 CPU 以整条指令为粒度执行，本类在一条指令结束后按它消耗的周期数
/// 批量推进 PPU/APU。输入、mapper IRQ、NMI 都在指令边界采样。
///
/// 对外只需三个操作：
///   - <see cref="RunFrame"/>：跑到下一帧完成；
///   - <see cref="StepInstruction"/>：单步，供调试器/测试；
///   - <see cref="Reset"/>：复位。
/// </summary>
public sealed class NesConsole
{
    public NesConsole(Cartridge cartridge)
    {
        Cartridge = cartridge ?? throw new ArgumentNullException(nameof(cartridge));
        Controller1 = new Controller();
        Controller2 = new Controller();
        Bus = new NesBus(cartridge, Controller1, Controller2);
        Ppu = Bus.Ppu;
        Apu = Bus.Apu;
        Cpu = new Cpu6502(Bus);

        Reset();
    }

    public Cartridge Cartridge { get; }
    public NesBus Bus { get; }
    public Cpu6502 Cpu { get; }
    public Ppu2C02 Ppu { get; }
    public Apu2A03 Apu { get; }
    public Controller Controller1 { get; }
    public Controller Controller2 { get; }

    /// <summary>已完成的帧数。</summary>
    public long FrameCount => Ppu.FrameCount;

    /// <summary>PPU 帧缓冲。</summary>
    public PpuFrameBuffer FrameBuffer => Ppu.FrameBuffer;

    /// <summary>复位主机。CPU RAM 与电池 RAM 保留，寄存器与 PPU/APU 状态复位。</summary>
    public void Reset()
    {
        Bus.Reset();
        Cartridge.Reset();
        Ppu.Reset();
        Apu.Reset();
        Cpu.Reset();

        Cpu.NmiLine = Ppu.NmiOutput;
        Cpu.IrqLine = Bus.IrqLine;
    }

    /// <summary>
    /// 执行一条 CPU 指令，并把相应的 PPU/APU 时间推进。
    /// 返回实际经过的 CPU 周期（包含 OAM DMA 停顿）。
    /// </summary>
    public int StepInstruction()
    {
        Bus.CpuCycle = Cpu.TotalCycles;

        int instructionCycles = Cpu.Step();
        int dmaCycles = Bus.TakeDmaStallCycles();
        if (dmaCycles > 0)
            Cpu.AddStallCycles(dmaCycles);

        int totalCycles = instructionCycles + dmaCycles;
        Bus.Tick(totalCycles);

        // 总线时钟推进后，重新采样中断线，供下一条指令开始时检测。
        Cpu.NmiLine = Ppu.NmiOutput;
        Cpu.IrqLine = Bus.IrqLine;

        return totalCycles;
    }

    /// <summary>
    /// 运行到下一帧完成。通常一帧约 29,780 个 CPU 周期 / 89,342 个 PPU dot。
    /// </summary>
    public void RunFrame()
    {
        long targetFrame = Ppu.FrameCount + 1;
        int guard = 0;

        while (Ppu.FrameCount < targetFrame)
        {
            StepInstruction();

            // 防御性上限：如果 ROM 卡死或 mapper 行为异常，避免 UI 永久冻结。
            if (++guard > 200_000)
                throw new InvalidOperationException("单帧执行超过 20 万条指令，CPU 可能已经跑飞。");
        }
    }

    /// <summary>连续运行多帧，适用于基准测试/无头模式。</summary>
    public void RunFrames(int count)
    {
        for (int i = 0; i < count; i++)
            RunFrame();
    }

    /// <summary>保存电池存档（.sav）。</summary>
    public void SaveBattery(string path) => Cartridge.SaveBattery(path);

    /// <summary>读取电池存档（.sav）。</summary>
    public void LoadBattery(string path) => Cartridge.LoadBattery(path);
}
