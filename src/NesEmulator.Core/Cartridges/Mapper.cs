namespace NesEmulator.Core.Cartridges;

/// <summary>
/// 卡带 mapper（也称为 memory mapper / bank switcher）的抽象基类。
///
/// 为什么需要 mapper？
///   6502 只有 16 位地址线，$8000-$FFFF 这 32 KB 必须容纳程序。
///   早期 NROM 游戏只有 16/32 KB PRG，直接映射即可；
///   后期游戏有 128 KB、256 KB 甚至更多程序，就必须在固定窗口里
///   “换页”（bank switching）：向卡带上的寄存器写值，改变某段地址
///   当前看到的是 ROM 的哪一块。
///
/// 这个抽象类把“内存访问”与“换页策略”分开：
///   - CPU 的 $6000-$7FFF 通常是 PRG-RAM；
///   - CPU 的 $8000-$FFFF 由 ReadPrg 决定映射哪一块 PRG-ROM；
///   - PPU 的 $0000-$1FFF 由 ReadChr 决定映射哪一块 CHR-ROM/CHR-RAM；
///   - 部分 mapper 还会产生扫描线 IRQ（如 MMC3）。
///
/// 学习建议：先读 Mapper000（最简单），再读 Mapper002/003，
/// 然后读 Mapper001（串行移位寄存器），最后读 Mapper004（扫描线 IRQ）。
/// </summary>
public abstract class Mapper
{
    /// <summary>mapper 所属的卡带；子类用它访问 ROM/RAM 数组和当前镜像设置。</summary>
    protected Mapper(Cartridge cartridge)
    {
        Cartridge = cartridge;
    }

    /// <summary>所属卡带。</summary>
    protected Cartridge Cartridge { get; }

    /// <summary>人类可读的名称，例如 "NROM"、"MMC1"。</summary>
    public abstract string Name { get; }

    /// <summary>CPU 读取 $8000-$FFFF 时调用。返回值必须是 PRG-ROM/PRG-RAM 中的字节。</summary>
    public abstract byte ReadPrg(ushort address);

    /// <summary>CPU 写入 $8000-$FFFF 时调用。大多数 mapper 在这里接收换页命令。</summary>
    /// <param name="cpuCycle">当前 CPU 总周期，MMC1 用来识别连续周期写入。</param>
    public virtual void WritePrg(ushort address, byte value, long cpuCycle)
    {
    }

    /// <summary>PPU 读取 $0000-$1FFF（pattern table）时调用。</summary>
    public abstract byte ReadChr(ushort address);

    /// <summary>PPU 写入 $0000-$1FFF 时调用。只有 CHR-RAM 才会真正保存数据。</summary>
    public virtual void WriteChr(ushort address, byte value)
    {
    }

    /// <summary>PRG-RAM（$6000-$7FFF）当前是否可访问。MMC1 可以软件禁用。</summary>
    public virtual bool PrgRamEnabled => true;

    /// <summary>mapper 是否正在向 CPU 拉低 IRQ 线（低有效，这里用 true 表示“有中断请求”）。</summary>
    public virtual bool IrqPending => false;

    /// <summary>
    /// PPU 进入某条扫描线的取指阶段时调用，MMC3 用它递减扫描线计数器。
    /// 本模拟器在 PPU 的 dot 260 调用一次（仅渲染开启时）。
    /// </summary>
    public virtual void OnScanline()
    {
    }

    /// <summary>复位时把 mapper 恢复到上电状态（不会清空电池 RAM）。</summary>
    public virtual void Reset()
    {
    }

    /// <summary>把 16 位地址转换成从 0 开始的 PRG 偏移，并按实际 ROM 大小回绕，避免越界。</summary>
    protected int WrapPrg(int offset)
    {
        int size = Cartridge.PrgRom.Length;
        if (size == 0)
            return 0;
        offset %= size;
        if (offset < 0)
            offset += size;
        return offset;
    }

    /// <summary>PPU 侧 CHR-ROM 偏移辅助方法。</summary>
    protected int WrapChrRom(int offset)
    {
        int size = Cartridge.ChrRom.Length;
        if (size == 0)
            return 0;
        offset %= size;
        if (offset < 0)
            offset += size;
        return offset;
    }
}
