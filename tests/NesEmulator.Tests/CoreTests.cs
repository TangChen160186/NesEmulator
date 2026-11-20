using NesEmulator.Core.Apu;
using NesEmulator.Core.Cartridges;
using NesEmulator.Core.Emulation;
using NesEmulator.Core.Input;
using NesEmulator.Core.Ppu;

namespace NesEmulator.Tests;

/// <summary>不依赖外部 ROM 的核心测试。</summary>
internal static class CoreTests
{
    public static void Run(TestRunner runner)
    {
        runner.Section("iNES 解析与卡带");
        runner.Run("16 KB NROM 在 $C000 正确镜像", NromMirroring);
        runner.Run("MMC1 串行移位寄存器切换 PRG bank", Mmc1SerialBank);
        runner.Run("UxROM 固定最后一块 PRG bank", UxRomBank);
        runner.Run("CNROM 切换 CHR bank", CnRomChrBank);
        runner.Run("MMC3 切换 PRG bank 并改写镜像", Mmc3BankAndMirroring);
        runner.Run("MMC3 的 CHR 模式与扫描线 IRQ", Mmc3ChrAndIrq);

        runner.Section("手柄与总线");
        runner.Run("标准手柄选通/串行位顺序", ControllerSequence);
        runner.Run("CPU RAM 镜像与 PPU 寄存器镜像", BusMirroring);

        runner.Section("APU");
        runner.Run("方波声道产生非零 PCM 样本", ApuProducesSamples);
        runner.Run("APU 帧 IRQ 可读并应答", ApuFrameIrq);

        runner.Section("PPU 与整机");
        runner.Run("名称表镜像与调色板镜像", PpuMirroring);
        runner.Run("背景与 sprite 0 hit", SpriteZeroHit);
        runner.Run("自制 ROM 运行、NMI、精灵与画面", DemoToml);
    }

    private static void NromMirroring()
    {
        Cartridge cart = SyntheticCartridge.Create(mapper: 0, prgBanks: 1, chrBanks: 1);
        cart.PrgRom[0x0000] = 0x11;
        cart.PrgRom[0x3FFF] = 0x22;

        TestRunner.Equal(cart.CpuRead(0x8000), (byte)0x11, "$8000 应读取 PRG 起始");
        TestRunner.Equal(cart.CpuRead(0xC000), (byte)0x11, "16 KB PRG 的 $C000 应镜像 $8000");
        TestRunner.Equal(cart.CpuRead(0xBFFF), (byte)0x22, "$BFFF 应读取 PRG 末尾");
        TestRunner.Equal(cart.CpuRead(0xFFFF), (byte)0x22, "$FFFF 应镜像 PRG 末尾");
    }

    private static void Mmc1SerialBank()
    {
        Cartridge cart = SyntheticCartridge.Create(mapper: 1, prgBanks: 8, chrBanks: 1);
        FillPrgBanks(cart.PrgRom);

        // 目标：PRG bank 寄存器 = 3（二进制 00011）。
        // MMC1 每次只接收 bit0，最低位先写，5 次后提交。
        byte[] bits = [1, 1, 0, 0, 0];
        for (int i = 0; i < bits.Length; i++)
            cart.CpuWrite(0xE000, bits[i], cpuCycle: 100 + i * 4);

        TestRunner.Equal(cart.CpuRead(0x8000), (byte)3, "MMC1 控制模式 3 下 $8000 应切换到 bank 3");
        TestRunner.Equal(cart.CpuRead(0xC000), (byte)7, "$C000 应固定为最后一个 bank");
    }

    private static void UxRomBank()
    {
        Cartridge cart = SyntheticCartridge.Create(mapper: 2, prgBanks: 8, chrBanks: 1);
        FillPrgBanks(cart.PrgRom);

        // 模拟 UxROM 的 bank 表：写地址上的 ROM 字节为 $FF，允许任意 bank 通过
        // 总线冲突的 AND。真实游戏也会把这类表放在固定 ROM 区。
        cart.PrgRom[0x0000] = 0xFF;

        cart.CpuWrite(0x8000, 3, 10);
        TestRunner.Equal(cart.CpuRead(0x8000), (byte)3, "$8000 应切换到 bank 3");
        TestRunner.Equal(cart.CpuRead(0xC000), (byte)7, "$C000 固定最后一块");
    }

    private static void CnRomChrBank()
    {
        Cartridge cart = SyntheticCartridge.Create(mapper: 3, prgBanks: 1, chrBanks: 4);
        for (int bank = 0; bank < 4; bank++)
            cart.ChrRom[bank * 0x2000] = (byte)(0xA0 + bank);

        cart.CpuWrite(0x8000, 2, 10);
        TestRunner.Equal(cart.PpuRead(0x0000), (byte)0xA2, "CNROM 应选择 CHR bank 2");
    }

    private static void Mmc3BankAndMirroring()
    {
        // 初始用垂直镜像，才能验证 $A000 写入后切换为水平镜像。
        Cartridge cart = SyntheticCartridge.Create(mapper: 4, prgBanks: 8, chrBanks: 1, vertical: true);

        // MMC3 以 8 KB 为 bank，因此每个 8 KB 边界写入 bank 编号。
        for (int bank = 0; bank < cart.PrgRom.Length / 0x2000; bank++)
            cart.PrgRom[bank * 0x2000] = (byte)bank;

        // bank select = 6（偶数地址），bank data = 3（奇数地址）。
        cart.CpuWrite(0x8000, 6, 10);
        cart.CpuWrite(0x8001, 3, 12);
        TestRunner.Equal(cart.CpuRead(0x8000), (byte)3, "MMC3 R6 应映射到 $8000");

        cart.CpuWrite(0xA000, 1, 14);
        TestRunner.Equal(cart.Mirroring, Mirroring.Horizontal, "$A000 bit0=1 应切换到水平镜像");
    }

    private static void Mmc3ChrAndIrq()
    {
        Cartridge cart = SyntheticCartridge.Create(mapper: 4, prgBanks: 8, chrBanks: 1);
        for (int bank = 0; bank < 8; bank++)
            cart.ChrRom[bank * 0x400] = (byte)bank;

        // CHR 模式 0：R0 映射到 $0000/$0400（2 KB），R2 映射到 $1000。
        cart.CpuWrite(0x8000, 0, 10);
        cart.CpuWrite(0x8001, 6, 12);
        TestRunner.Equal(cart.PpuRead(0x0000), (byte)6, "R0 的低 2 KB 起始 bank");
        TestRunner.Equal(cart.PpuRead(0x0400), (byte)7, "R0 的第二个 1 KB bank");

        cart.CpuWrite(0x8000, 2, 14);
        cart.CpuWrite(0x8001, 1, 16);
        TestRunner.Equal(cart.PpuRead(0x1000), (byte)1, "R2 应映射到 $1000");

        // CHR 模式 1：R0 移到 $1000。
        cart.CpuWrite(0x8000, 0x80, 18);
        cart.CpuWrite(0x8001, 6, 20);
        TestRunner.Equal(cart.PpuRead(0x1000), (byte)6, "CHR 模式 1 下 R0 应映射到 $1000");

        // IRQ：latch=3，reload，enable；第 4 次扫描线计数到 0。
        cart.CpuWrite(0xC000, 3, 22);
        cart.CpuWrite(0xC001, 0, 24);
        cart.CpuWrite(0xE001, 0, 26);
        for (int i = 0; i < 4; i++)
            cart.NotifyScanline();
        TestRunner.True(cart.IrqPending, "MMC3 计数器到 0 时应置 IRQ");

        cart.CpuWrite(0xE000, 0, 28);
        TestRunner.True(!cart.IrqPending, "写 $E000 应应答并清除 IRQ");
    }

    private static void ControllerSequence()
    {
        var controller = new Controller();
        controller.SetButtons(ControllerButton.A | ControllerButton.Start | ControllerButton.Right);

        controller.WriteStrobe(1);
        controller.WriteStrobe(0);

        bool[] bits = new bool[8];
        for (int i = 0; i < 8; i++)
            bits[i] = controller.Read() != 0;

        TestRunner.True(bits[0], "第 1 位应为 A");
        TestRunner.True(!bits[1], "第 2 位应为 B（未按）");
        TestRunner.True(!bits[2], "第 3 位应为 Select（未按）");
        TestRunner.True(bits[3], "第 4 位应为 Start");
        TestRunner.True(!bits[4] && !bits[5] && !bits[6], "方向键上/下/左应为 0");
        TestRunner.True(bits[7], "第 8 位应为 Right");
        TestRunner.Equal(controller.Read(), (byte)1, "第 9 位应补 1");
    }

    private static void BusMirroring()
    {
        Cartridge cart = SyntheticCartridge.Create(mapper: 0);
        var console = new NesConsole(cart);

        console.Bus.Write(0x0000, 0x5A);
        TestRunner.Equal(console.Bus.Read(0x0800), (byte)0x5A, "$0800 应镜像 $0000");
        TestRunner.Equal(console.Bus.Read(0x1000), (byte)0x5A, "$1000 应镜像 $0000");

        // 通过 $2006/$2007 写 VRAM，再从镜像地址读回（关渲染时有读缓冲，读两次）。
        console.Bus.Write(0x2006, 0x20);
        console.Bus.Write(0x2006, 0x00);
        console.Bus.Write(0x2007, 0x77);
        console.Bus.Write(0x2006, 0x20);
        console.Bus.Write(0x2006, 0x00);
        console.Bus.Read(0x2007); // 第一次返回旧缓冲
        TestRunner.Equal(console.Bus.Read(0x2007), (byte)0x77, "$2007 读缓冲应返回刚写入的名称表字节");
    }

    private static void ApuProducesSamples()
    {
        var apu = new Apu2A03(_ => 0xFF);
        apu.WriteRegister(0x4015, 0x01);       // 启用 Pulse 1
        apu.WriteRegister(0x4000, 0xBF);       // 50% 占空比、halt、固定音量 15
        apu.WriteRegister(0x4002, 0x40);       // 定时器低
        apu.WriteRegister(0x4003, 0x08);       // 长度 + 定时器高
        apu.Step(30_000);

        var samples = new short[4096];
        int count = apu.Audio.Read(samples);
        TestRunner.True(count > 0, "APU 应产生样本");
        TestRunner.True(samples.AsSpan(0, count).IndexOfAnyExcept((short)0) >= 0, "方波样本不应全为 0");
    }

    private static void ApuFrameIrq()
    {
        var apu = new Apu2A03(_ => 0xFF);
        apu.WriteRegister(0x4015, 0x00);
        apu.WriteRegister(0x4017, 0x00); // 4 步模式，允许 IRQ
        apu.Step(30_000);

        TestRunner.True(apu.IrqPending, "4 步帧序列器应在 29829 周期处请求 IRQ");
        byte status = apu.ReadStatus();
        TestRunner.True((status & 0x40) != 0, "$4015 应报告帧 IRQ");
        TestRunner.True(!apu.IrqPending, "读取 $4015 后帧 IRQ 应被清除");
    }

    private static void PpuMirroring()
    {
        // Vertical：逻辑表 0 和 1 不同，2 镜像 0。
        var verticalCart = SyntheticCartridge.Create(mapper: 0, vertical: true);
        var verticalConsole = new NesConsole(verticalCart);
        WriteNameTableByte(verticalConsole, 0x2000, 0xAA);
        TestRunner.Equal(ReadNameTableByte(verticalConsole, 0x2800), (byte)0xAA,
            "Vertical 镜像下 $2800 应镜像 $2000");
        TestRunner.True(ReadNameTableByte(verticalConsole, 0x2400) != 0xAA,
            "Vertical 镜像下 $2400 应是另一块物理表");

        // Horizontal：逻辑表 0 和 1 相同，2 不同。
        var horizontalCart = SyntheticCartridge.Create(mapper: 0, vertical: false);
        var horizontalConsole = new NesConsole(horizontalCart);
        WriteNameTableByte(horizontalConsole, 0x2000, 0x55);
        TestRunner.Equal(ReadNameTableByte(horizontalConsole, 0x2400), (byte)0x55,
            "Horizontal 镜像下 $2400 应镜像 $2000");

        // 调色板镜像：$3F10 与 $3F00 是同一字节。
        WritePaletteByte(horizontalConsole, 0x3F10, 0x30);
        TestRunner.Equal(ReadPaletteByte(horizontalConsole, 0x3F00), (byte)0x30,
            "$3F10 应镜像到 $3F00");
    }

    private static void SpriteZeroHit()
    {
        Cartridge cart = SyntheticCartridge.Create(mapper: 0, prgBanks: 1, chrBanks: 1);
        var console = new NesConsole(cart);

        // tile 0：纯色 1；tile 1：纯色 2。
        for (int y = 0; y < 8; y++)
        {
            cart.ChrRom[y] = 0xFF;
            cart.ChrRom[16 + y] = 0x00;
            cart.ChrRom[24 + y] = 0xFF;
        }

        // 背景全部用 tile 0。
        SetPpuAddress(console, 0x2000);
        for (int i = 0; i < 960; i++)
            console.Bus.Write(0x2007, 0);
        for (int i = 0; i < 64; i++)
            console.Bus.Write(0x2007, 0);

        WritePaletteByte(console, 0x3F00, 0x0F);
        WritePaletteByte(console, 0x3F01, 0x21);
        WritePaletteByte(console, 0x3F02, 0x11);
        WritePaletteByte(console, 0x3F03, 0x31);

        // OAM sprite 0：Y=50（在第 51 行显示），tile=1，X=50。
        console.Bus.Write(0x2003, 0x00);
        console.Bus.Write(0x2004, 50);
        console.Bus.Write(0x2004, 1);
        console.Bus.Write(0x2004, 0);
        console.Bus.Write(0x2004, 50);

        console.Bus.Write(0x2005, 0);
        console.Bus.Write(0x2005, 0);
        console.Bus.Write(0x2001, 0x1E); // 开背景+精灵，显示左 8 像素

        // 直接推进 PPU 到第 60 行（不经过 CPU），第 51 行应已发生 hit。
        console.Ppu.Step(341 * 60);
        byte status = console.Ppu.ReadRegister(0x2002);
        TestRunner.True((status & 0x40) != 0, "sprite 0 与背景重叠时应置 hit 标志");
    }

    private static void DemoToml()
    {
        string? rom = TestWorkspace.FindSampleRom();
        if (rom is null)
            throw new SkipTestException("未找到 samples/hello-nes.nes");

        Cartridge cart = InesRomLoader.Load(rom);
        var console = new NesConsole(cart);
        console.RunFrames(60);

        TestRunner.Equal(console.Ppu.FrameCount, 60L, "应完成 60 帧");
        TestRunner.True((byte)console.Bus.Read(0x0000) >= 50, "NMI 计数器应接近 60");
        TestRunner.True((byte)console.Bus.Read(0x0203) > 120, "精灵 X 应被 NMI 移动");

        // 标题区域应有非黑像素，彩色条区域应有较多彩色像素。
        int titlePixels = 0;
        int barPixels = 0;
        ReadOnlySpan<uint> pixels = console.FrameBuffer.Pixels.Span;
        for (int y = 44; y < 72; y++)
        {
            for (int x = 70; x < 190; x++)
                if ((pixels[y * PpuFrameBuffer.Width + x] & 0x00FFFFFF) != 0)
                    titlePixels++;
        }

        for (int y = 160; y < 192; y++)
        {
            for (int x = 0; x < 256; x++)
                if ((pixels[y * PpuFrameBuffer.Width + x] & 0x00FFFFFF) != 0)
                    barPixels++;
        }

        TestRunner.True(titlePixels > 100, $"标题区域应有文字像素，实际 {titlePixels}");
        TestRunner.True(barPixels > 4000, $"彩色条区域应有像素，实际 {barPixels}");
    }

    private static void SetPpuAddress(NesConsole console, ushort address)
    {
        console.Bus.Write(0x2006, (byte)(address >> 8));
        console.Bus.Write(0x2006, (byte)(address & 0xFF));
    }

    private static void WriteNameTableByte(NesConsole console, ushort address, byte value)
    {
        SetPpuAddress(console, address);
        console.Bus.Write(0x2007, value);
    }

    private static byte ReadNameTableByte(NesConsole console, ushort address)
    {
        SetPpuAddress(console, address);
        console.Bus.Read(0x2007); // 丢弃读缓冲
        return console.Bus.Read(0x2007);
    }

    private static void WritePaletteByte(NesConsole console, ushort address, byte value)
    {
        SetPpuAddress(console, address);
        console.Bus.Write(0x2007, value);
    }

    private static byte ReadPaletteByte(NesConsole console, ushort address)
    {
        SetPpuAddress(console, address);
        return console.Bus.Read(0x2007);
    }

    private static void FillPrgBanks(byte[] prg)
    {
        for (int bank = 0; bank < prg.Length / 0x4000; bank++)
            prg[bank * 0x4000] = (byte)bank;
    }
}

/// <summary>构造用于测试的最小 iNES 卡带。</summary>
internal static class SyntheticCartridge
{
    public static Cartridge Create(int mapper, int prgBanks = 2, int chrBanks = 1, bool vertical = false)
    {
        var header = new byte[16];
        header[0] = (byte)'N';
        header[1] = (byte)'E';
        header[2] = (byte)'S';
        header[3] = 0x1A;
        header[4] = (byte)prgBanks;
        header[5] = (byte)chrBanks;
        header[6] = (byte)((vertical ? 1 : 0) | ((mapper & 0x0F) << 4));
        header[7] = (byte)(mapper & 0xF0);

        InesHeader parsed = InesHeader.Parse(header);
        var prg = new byte[parsed.PrgRomSize];
        var chr = new byte[parsed.ChrRomSize];
        return new Cartridge(parsed, prg, chr);
    }
}

internal static class TestWorkspace
{
    public static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "NesEmulator.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }

            return Directory.GetCurrentDirectory();
        }
    }

    public static string? FindSampleRom()
    {
        string path = Path.Combine(Root, "samples", "hello-nes.nes");
        return File.Exists(path) ? path : null;
    }
}
