namespace NesEmulator.Core.Cpu;

/// <summary>
/// CPU 看到的总线。CPU 本身不关心地址背后是内存、PPU 寄存器、APU 还是卡带，
/// 只负责在正确的时间读写地址；解复用工作由 <c>NesBus</c> 完成。
//
// 这是一个非常小的依赖倒置接口，测试时可以用几百字节的假总线替代整个主机。
/// </summary>
public interface ICpuBus
{
    /// <summary>读取一个字节。</summary>
    byte Read(ushort address);

    /// <summary>写入一个字节。</summary>
    void Write(ushort address, byte value);
}
