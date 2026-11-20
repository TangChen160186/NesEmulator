using NesEmulator.Core.Cartridges.Mappers;

namespace NesEmulator.Core.Cartridges;

/// <summary>
/// 一张已插入主机的卡带。
///
/// 卡带不是“一个 ROM 文件”这么简单，它同时扮演三种角色：
///   1. CPU 的程序存储器：$6000-$7FFF（PRG-RAM）、$8000-$FFFF（PRG-ROM）；
///   2. PPU 的图块存储器：$0000-$1FFF（CHR-ROM 或 CHR-RAM）；
///   3. 主板扩展硬件：mapper 寄存器、扫描线 IRQ、镜像控制。
///
/// Cartridge 负责统一转发这些访问，Mapper 子类负责真正的换页逻辑。
/// CPU 总线（NesBus）和 PPU 都只与 Cartridge 打交道，因此新增 mapper
/// 不需要修改 CPU/PPU 的代码。
/// </summary>
public sealed class Cartridge
{
    private readonly byte[] _prgRam;
    private readonly byte[] _chrRam;

    /// <summary>从已经校验过的文件头和 ROM 数据构造卡带。</summary>
    public Cartridge(InesHeader header, byte[] prgRom, byte[] chrRom)
    {
        Header = header;
        PrgRom = prgRom;
        ChrRom = chrRom;
        _prgRam = new byte[Math.Max(0, header.PrgRamSize)];
        _chrRam = new byte[Math.Max(0, header.ChrRamSize)];

        // 初始镜像由文件头决定；mapper 之后可能通过寄存器改写。
        Mirroring = header.Mirroring;
        Mapper = MapperFactory.Create(this, header.MapperNumber);
        Reset();
    }

    /// <summary>文件头解析结果。</summary>
    public InesHeader Header { get; }

    /// <summary>PRG-ROM 数据（CPU 指令与数据）。</summary>
    public byte[] PrgRom { get; }

    /// <summary>CHR-ROM 数据（PPU 图块）；长度可以为 0，表示使用 CHR-RAM。</summary>
    public byte[] ChrRom { get; }

    /// <summary>PRG-RAM（$6000-$7FFF），也用于电池存档。</summary>
    public byte[] PrgRam => _prgRam;

    /// <summary>CHR-RAM；只有 ChrRom 为空时才存在。</summary>
    public byte[] ChrRam => _chrRam;

    /// <summary>当前 mapper 实例。</summary>
    public Mapper Mapper { get; }

    /// <summary>当前名称表镜像方式，可被 mapper 动态修改。</summary>
    public Mirroring Mirroring { get; set; }

    /// <summary>是否有电池存档。</summary>
    public bool HasBattery => Header.HasBattery;

    /// <summary>当前是否使用 CHR-RAM。</summary>
    public bool UsesChrRam => ChrRom.Length == 0;

    /// <summary>mapper 是否正在请求 IRQ。</summary>
    public bool IrqPending => Mapper.IrqPending;

    /// <summary>复位卡带逻辑（不清除电池 RAM 的内容）。</summary>
    public void Reset()
    {
        // 真实硬件上 PRG-RAM 上电内容是随机的；为了让模拟结果可复现，
        // 新卡带按 0 初始化，读档时再覆盖。不要在这里 Array.Clear，
        // 否则“复位主机”会意外抹掉存档内存。
        Mapper.Reset();
    }

    /// <summary>CPU 读取卡带地址空间（$4020-$FFFF；通常只会有 $6000 以上）。</summary>
    public byte CpuRead(ushort address)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            if (!Mapper.PrgRamEnabled || _prgRam.Length == 0)
                return 0xFF;

            return _prgRam[(address - 0x6000) % _prgRam.Length];
        }

        if (address >= 0x8000)
            return Mapper.ReadPrg(address);

        // $4020-$5FFF 是扩展区，绝大多数卡带不响应。
        return 0xFF;
    }

    /// <summary>CPU 写入卡带地址空间。</summary>
    /// <param name="cpuCycle">CPU 累计周期，用于 MMC1 的连续写保护。</param>
    public void CpuWrite(ushort address, byte value, long cpuCycle)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            if (Mapper.PrgRamEnabled && _prgRam.Length > 0)
                _prgRam[(address - 0x6000) % _prgRam.Length] = value;
            return;
        }

        if (address >= 0x8000)
            Mapper.WritePrg(address, value, cpuCycle);
    }

    /// <summary>PPU 读取 pattern table（$0000-$1FFF）。</summary>
    public byte PpuRead(ushort address)
    {
        if (address > 0x1FFF)
            return 0xFF;

        if (UsesChrRam)
            return _chrRam.Length == 0 ? (byte)0xFF : _chrRam[address % _chrRam.Length];

        return Mapper.ReadChr(address);
    }

    /// <summary>PPU 写入 pattern table。CHR-ROM 不可写，只有 CHR-RAM 会保存。</summary>
    public void PpuWrite(ushort address, byte value)
    {
        if (address > 0x1FFF)
            return;

        if (UsesChrRam)
        {
            if (_chrRam.Length > 0)
                _chrRam[address % _chrRam.Length] = value;
            return;
        }

        Mapper.WriteChr(address, value);
    }

    /// <summary>PPU 每进入一条 render scanline 时通知 mapper（MMC3 IRQ 使用）。</summary>
    public void NotifyScanline() => Mapper.OnScanline();

    /// <summary>把电池存档写入文件；没有电池 RAM 时什么都不做。</summary>
    public void SaveBattery(string path)
    {
        if (!HasBattery || _prgRam.Length == 0)
            return;

        File.WriteAllBytes(path, _prgRam);
    }

    /// <summary>从 .sav 文件恢复电池存档。文件长度不足时只复制可用的部分。</summary>
    public void LoadBattery(string path)
    {
        if (!HasBattery || !File.Exists(path))
            return;

        byte[] data = File.ReadAllBytes(path);
        Array.Copy(data, _prgRam, Math.Min(data.Length, _prgRam.Length));
    }
}
