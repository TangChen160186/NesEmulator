using System.Text.RegularExpressions;
using NesEmulator.Core.Cartridges;
using NesEmulator.Core.Cpu;

namespace NesEmulator.Tests;

/// <summary>
/// nestest 是 6502 社区最经典的 CPU 指令/周期对拍测试 ROM。
/// 它从 $C000 开始执行，每执行一条指令就在日志中记录寄存器与累计周期。
/// 本测试读取官方日志，逐条比较 PC/A/X/Y/P/SP/CYC。
/// </summary>
internal static partial class NestestHarness
{
    private sealed record ExpectedLine(
        ushort Pc, byte A, byte X, byte Y, byte P, byte Sp, long Cycles);

    [GeneratedRegex(
        @"^([0-9A-F]{4})\s+[0-9A-F]{2}(?: [0-9A-F]{2})?(?: [0-9A-F]{2})?\s+.*?\bA:([0-9A-F]{2}) X:([0-9A-F]{2}) Y:([0-9A-F]{2}) P:([0-9A-F]{2}) SP:([0-9A-F]{2}).*CYC:(\d+)",
        RegexOptions.Compiled)]
    private static partial Regex LogLineRegex();

    public static bool Run(string romPath, string logPath)
    {
        Cartridge cart = InesRomLoader.Load(romPath);
        var bus = new SimpleCpuBus();

        // nestest 是 16 KB NROM，$C000-$FFFF 镜像同一块 PRG。
        for (int address = 0x8000; address <= 0xFFFF; address++)
            bus.Write((ushort)address, cart.PrgRom[(address - 0x8000) % cart.PrgRom.Length]);

        var cpu = new Cpu6502(bus);
        var lines = new List<ExpectedLine>();

        foreach (string raw in File.ReadLines(logPath))
        {
            Match match = LogLineRegex().Match(raw);
            if (!match.Success)
                continue;

            lines.Add(new ExpectedLine(
                Convert.ToUInt16(match.Groups[1].Value, 16),
                Convert.ToByte(match.Groups[2].Value, 16),
                Convert.ToByte(match.Groups[3].Value, 16),
                Convert.ToByte(match.Groups[4].Value, 16),
                Convert.ToByte(match.Groups[5].Value, 16),
                Convert.ToByte(match.Groups[6].Value, 16),
                long.Parse(match.Groups[7].Value)));
        }

        if (lines.Count == 0)
        {
            Console.WriteLine("  [FAIL] nestest.log 中没有解析到任何行。");
            return false;
        }

        ExpectedLine first = lines[0];
        cpu.RestoreState(new CpuState(
            first.A, first.X, first.Y, first.Sp, first.P, first.Pc, first.Cycles, false));

        int failures = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            ExpectedLine expected = lines[i];
            CpuState state = cpu.CaptureState();

            // nestest 日志记录的是“执行本条指令之前”的状态，CYC 也是本行起始周期。
            // 因此先比较当前状态，再执行；下一条日志会校验本指令消耗的周期。
            string? mismatch = null;
            if (state.PC != expected.Pc) mismatch += $" PC={state.PC:X4}≠{expected.Pc:X4}";
            if (state.A != expected.A) mismatch += $" A={state.A:X2}≠{expected.A:X2}";
            if (state.X != expected.X) mismatch += $" X={state.X:X2}≠{expected.X:X2}";
            if (state.Y != expected.Y) mismatch += $" Y={state.Y:X2}≠{expected.Y:X2}";
            if (state.P != expected.P) mismatch += $" P={state.P:X2}≠{expected.P:X2}";
            if (state.S != expected.Sp) mismatch += $" SP={state.S:X2}≠{expected.Sp:X2}";
            if (state.Cycles != expected.Cycles) mismatch += $" CYC={state.Cycles}≠{expected.Cycles}";

            if (mismatch is not null)
            {
                failures++;
                Console.WriteLine($"  [FAIL] 第 {i + 1} 行 {expected.Pc:X4}:{mismatch}");
                if (failures >= 20)
                {
                    Console.WriteLine("  ... 后续错误已省略");
                    break;
                }
            }

            if (i + 1 < lines.Count)
                cpu.Step();
        }

        Console.WriteLine($"  nestest：共 {lines.Count} 条指令，失败 {failures} 条。");
        return failures == 0;
    }

    /// <summary>仅用于 CPU 对拍的 64 KB 线性内存总线。</summary>
    private sealed class SimpleCpuBus : ICpuBus
    {
        private readonly byte[] _memory = new byte[0x10000];

        public byte Read(ushort address) => _memory[address];

        public void Write(ushort address, byte value) => _memory[address] = value;
    }
}
