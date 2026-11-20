using System.Diagnostics;
using System.Text;
using NesEmulator.Cli;
using NesEmulator.Core.Cartridges;
using NesEmulator.Core.Emulation;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0)
{
    PrintUsage();
    return 0;
}

string romPath = args[0];
int frames = 60;
string? screenshotPath = null;

for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--frames" when i + 1 < args.Length:
            frames = int.Parse(args[++i]);
            break;
        case "--screenshot" when i + 1 < args.Length:
            screenshotPath = args[++i];
            break;
        case "--help" or "-h":
            PrintUsage();
            return 0;
        default:
            Console.Error.WriteLine($"未知参数：{args[i]}");
            PrintUsage();
            return 2;
    }
}

try
{
    Cartridge cartridge = InesRomLoader.Load(romPath);
    Console.WriteLine($"ROM   : {Path.GetFileName(romPath)}");
    Console.WriteLine($"卡带  : {cartridge.Header.ToSummary()}");
    Console.WriteLine($"Mapper: {cartridge.Mapper.Name}");
    Console.WriteLine($"PRG   : {cartridge.PrgRom.Length / 1024.0:F1} KB    CHR: " +
                      (cartridge.UsesChrRam
                          ? $"{cartridge.ChrRam.Length / 1024.0:F1} KB RAM"
                          : $"{cartridge.ChrRom.Length / 1024.0:F1} KB ROM"));

    var console = new NesConsole(cartridge);
    var stopwatch = Stopwatch.StartNew();
    console.RunFrames(frames);
    stopwatch.Stop();

    double seconds = stopwatch.Elapsed.TotalSeconds;
    Console.WriteLine($"运行  : {frames} 帧 / {seconds:F3} 秒  ({(seconds > 0 ? frames / seconds : 0):F1} FPS，无头模式)");
    Console.WriteLine($"CPU   : PC=${console.Cpu.PC:X4}  周期={console.Cpu.TotalCycles:N0}  Jammed={console.Cpu.Jammed}");
    Console.WriteLine($"PPU   : frame={console.Ppu.FrameCount}  vblank={(console.Ppu.ReadRegister(0x2002) & 0x80) != 0}");
    Console.WriteLine($"APU   : 可读样本={console.Apu.Audio.Count}");

    if (screenshotPath is not null)
    {
        PngWriter.Save(console.FrameBuffer, screenshotPath);
        Console.WriteLine($"画面  : {screenshotPath}");
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"错误：{ex.Message}");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        NES 模拟器 · 无头运行器

        用法:
          dotnet run --project src/NesEmulator.Cli -- <rom.nes> [选项]

        选项:
          --frames <N>        运行 N 帧后退出（默认 60）
          --screenshot <path> 把最后一帧保存为 PNG
          --help              显示帮助

        示例:
          dotnet run --project src/NesEmulator.Cli -- samples/hello-nes.nes --frames 120 --screenshot hello.png
        """);
}
