using System.Text;
using NesEmulator.Tests;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("NES 模拟器自检");
Console.WriteLine("===============");

var runner = new TestRunner();

runner.Section("CPU：nestest 逐指令对拍");
string root = TestWorkspace.Root;
string nestestRom = Path.Combine(root, ".testdata", "nestest.nes");
string nestestLog = Path.Combine(root, ".testdata", "nestest.log");

if (File.Exists(nestestRom) && File.Exists(nestestLog))
{
    runner.Run("nestest 全 8991 条指令（含周期）", () =>
    {
        if (!NestestHarness.Run(nestestRom, nestestLog))
            throw new InvalidOperationException("存在寄存器/周期不一致，详见上方输出。");
    });
}
else
{
    runner.Run("nestest 全 8991 条指令（含周期）", () =>
        throw new SkipTestException("未找到 .testdata/nestest.nes / nestest.log；下载方法见 docs/07-测试与验证.md。"));
}

CoreTests.Run(runner);
return runner.Finish();
