using NesEmulator.Core.Cartridges;

namespace NesEmulator.Core.Ppu;

/// <summary>
/// Ricoh 2C02 PPU（Picture Processing Unit）。NES 的画面、滚动、精灵、
/// 调色板和 NMI 都由它产生。
///
/// 一帧的几何结构（NTSC）：
///   - 262 条扫描线，每条 341 个 PPU dot；
///   - 扫描线 0-239：可见画面（每行 256 个像素）；
///   - 扫描线 240：post-render，不输出；
///   - 扫描线 241：进入 vblank，可产生 NMI；
///   - 扫描线 261：pre-render，为下一帧准备滚动寄存器。
///
/// PPU 每个 dot 做一件小事，CPU 每 1 个周期对应 PPU 3 个 dot。
/// 本类提供逐 dot 的 <see cref="Step()"/>，由 NesBus 根据 CPU 周期批量推进。
///
/// 教学取舍：
///   - 背景按扫描线预取一条“像素调色板索引行”，再逐点与精灵合成。
///     这能正确处理细滚动和逐行 raster 效果，但不支持在同一扫描线中途
///     修改 $2005/$2006 后立即生效的极端 trick。
///   - 精灵溢出使用简化模型（第 9 个精灵即置位），不模拟真实 2C02 的
///     精灵溢出 bug。
///   - MMC3 的扫描线 IRQ 用 dot 260 近似 A12 上升沿。
/// 这些取舍在文档中都有说明，便于继续深入。
/// </summary>
public sealed partial class Ppu2C02
{
    // ==================== 常量 ====================

    /// <summary>每帧扫描线数（NTSC）。</summary>
    public const int ScanlinesPerFrame = 262;

    /// <summary>每条扫描线的 PPU dot 数。</summary>
    public const int DotsPerScanline = 341;

    /// <summary>可见扫描线数。</summary>
    public const int VisibleScanlines = 240;

    // ==================== 硬件连接 ====================

    private readonly Cartridge _cartridge;

    /// <summary>2 KB 或 4 KB 名称表 RAM（四屏卡带需要 4 KB）。</summary>
    private readonly byte[] _nameTableRam;

    /// <summary>32 字节调色板 RAM。</summary>
    private readonly byte[] _paletteRam = new byte[32];

    /// <summary>256 字节 OAM（64 个精灵 × 4 字节）。</summary>
    private readonly byte[] _oam = new byte[256];

    // ==================== 寄存器与内部状态 ====================

    private byte _control;       // $2000 PPUCTRL
    private byte _mask;          // $2001 PPUMASK
    private byte _status;        // $2002 PPUSTATUS（只实现 bit 5-7）
    private byte _oamAddress;    // $2003 OAMADDR

    /// <summary>Loopy 滚动寄存器 v：当前 VRAM 地址/渲染地址，15 位。</summary>
    private ushort _v;

    /// <summary>Loopy 临时寄存器 t：滚动写入的暂存值。</summary>
    private ushort _t;

    /// <summary>细 X 滚动（0-7）。</summary>
    private int _fineX;

    /// <summary>$2005/$2006 的双写翻转标志。</summary>
    private bool _writeToggle;

    /// <summary>$2007 的读缓冲。PPU 读取不是立即返回。</summary>
    private byte _readBuffer;

    /// <summary>开总线（open bus）上最近一次读到的值，用于未实现/只写寄存器。</summary>
    private byte _openBus;

    /// <summary>当前扫描线（0-261）。</summary>
    public int Scanline { get; private set; }

    /// <summary>当前扫描线内的 dot（0-340）。</summary>
    public int Cycle { get; private set; }

    /// <summary>已完成的帧数。前端可用它判断“又输出了一帧”。</summary>
    public long FrameCount { get; private set; }

    /// <summary>当前帧是否为奇数帧，用于 NTSC 的隔帧跳 dot。</summary>
    private bool _oddFrame;

    /// <summary>NMI 输出线。vblank 且 PPUCTRL bit7 时拉高。</summary>
    public bool NmiOutput { get; private set; }

    /// <summary>一帧完成时触发。注意：事件在模拟线程上同步调用。</summary>
    public event Action? FrameCompleted;

    // ==================== 帧缓冲与行缓存 ====================

    /// <summary>对外输出的 256×240 帧缓冲。</summary>
    public PpuFrameBuffer FrameBuffer { get; } = new();

    /// <summary>背景一行 33 个 tile 的颜色索引（264 像素），0xFF 表示透明。</summary>
    private readonly byte[] _backgroundLine = new byte[264];

    /// <summary>精灵层每个像素的调色板索引，0xFF 表示透明。</summary>
    private readonly byte[] _spriteLine = new byte[PpuFrameBuffer.Width];

    /// <summary>精灵层每个像素是否位于背景前面（属性 bit5=0）。</summary>
    private readonly bool[] _spriteInFront = new bool[PpuFrameBuffer.Width];

    /// <summary>精灵 0 在当前扫描线上是否产生不透明像素（用于 sprite 0 hit）。</summary>
    private readonly bool[] _spriteZeroOpaque = new bool[PpuFrameBuffer.Width];

    private readonly SpriteSlot[] _spritesOnLine = new SpriteSlot[8];
    private int _spriteCount;

    private readonly record struct SpriteSlot(
        int X, int Y, int Tile, byte Attributes, bool IsSpriteZero);

    // ==================== 构造与复位 ====================

    public Ppu2C02(Cartridge cartridge)
    {
        _cartridge = cartridge ?? throw new ArgumentNullException(nameof(cartridge));
        _nameTableRam = new byte[cartridge.Header.FourScreen ? 0x1000 : 0x0800];
        Reset();
    }

    /// <summary>
    /// 复位 PPU 寄存器与内部锁存器。名称表/调色板内容在真实硬件上不会因
    /// CPU 复位而清空，因此这里保留 RAM 内容，只复位控制状态。
    /// </summary>
    public void Reset()
    {
        _control = 0;
        _mask = 0;
        _status = 0;
        _oamAddress = 0;
        _v = 0;
        _t = 0;
        _fineX = 0;
        _writeToggle = false;
        _readBuffer = 0;
        _openBus = 0;
        Scanline = 0;
        Cycle = 0;
        NmiOutput = false;
        _oddFrame = false;
        FrameBuffer.Clear(PpuPalette.ToArgb(_paletteRam[0], _mask));
        Array.Clear(_oam);
        Array.Clear(_spritesOnLine);
        _spriteCount = 0;
    }

    /// <summary>当前是否开启渲染（背景或精灵任一）。</summary>
    public bool IsRenderingEnabled => (_mask & 0x18) != 0;

    /// <summary>背景是否开启（PPUMASK bit 3）。</summary>
    public bool IsBackgroundEnabled => (_mask & 0x08) != 0;

    /// <summary>精灵是否开启（PPUMASK bit 4）。</summary>
    public bool IsSpriteEnabled => (_mask & 0x10) != 0;

    /// <summary>OAM 的只读视图，供调试器/测试查看。</summary>
    public ReadOnlyMemory<byte> Oam => _oam;

    // ==================== CPU 寄存器接口 ====================

    /// <summary>CPU 读取 $2000-$2007（地址已镜像到 8 字节窗口）。</summary>
    public byte ReadRegister(ushort address)
    {
        byte result;

        switch (address & 0x0007)
        {
            case 0: // PPUCTRL：只写
                result = _openBus;
                break;

            case 1: // PPUMASK：只写
                result = _openBus;
                break;

            case 2: // PPUSTATUS
                // 低 5 位是开总线值；读取会清除 vblank 并复位写入锁存。
                result = (byte)((_status & 0xE0) | (_openBus & 0x1F));
                _status &= 0x1F;
                _writeToggle = false;
                UpdateNmiLine();
                break;

            case 3: // OAMADDR：只写
                result = _openBus;
                break;

            case 4: // OAMDATA
                result = _oam[_oamAddress];
                break;

            case 5: // PPUSCROLL：只写
            case 6: // PPUADDR：只写
                result = _openBus;
                break;

            default: // 7 = PPUDATA
                result = ReadData();
                break;
        }

        // 开总线：多数只写寄存器读回最近一次总线值。
        _openBus = result;
        return result;
    }

    /// <summary>CPU 写入 $2000-$2007。</summary>
    public void WriteRegister(ushort address, byte value)
    {
        switch (address & 0x0007)
        {
            case 0: // PPUCTRL
                _control = value;
                // 名称表选择位写入 t 的 bit 10-11。
                _t = (ushort)((_t & 0x73FF) | ((value & 0x03) << 10));
                UpdateNmiLine();
                break;

            case 1: // PPUMASK
                _mask = value;
                break;

            case 2: // PPUSTATUS：只读
                break;

            case 3: // OAMADDR
                _oamAddress = value;
                break;

            case 4: // OAMDATA
                _oam[_oamAddress++] = value;
                break;

            case 5: // PPUSCROLL，两次写入
                if (!_writeToggle)
                {
                    // 第一次：X 滚动。coarse X = value>>3，fine X = value&7。
                    _t = (ushort)((_t & 0x7FE0) | (value >> 3));
                    _fineX = value & 0x07;
                    _writeToggle = true;
                }
                else
                {
                    // 第二次：Y 滚动。coarse Y 在 bit5-9，fine Y 在 bit12-14。
                    _t = (ushort)((_t & 0x0C1F)
                                  | ((value & 0xF8) << 2)
                                  | ((value & 0x07) << 12));
                    _writeToggle = false;
                }
                break;

            case 6: // PPUADDR，两次写入
                if (!_writeToggle)
                {
                    _t = (ushort)((_t & 0x00FF) | ((value & 0x3F) << 8));
                    _writeToggle = true;
                }
                else
                {
                    _t = (ushort)((_t & 0x3F00) | value);
                    _v = _t;
                    _writeToggle = false;
                }
                break;

            case 7: // PPUDATA
                WriteData(value);
                break;
        }
    }

    /// <summary>
    /// $4014 OAM DMA：把 256 字节页复制进 OAM。
    /// 真实 DMA 会占用 CPU 513/514 周期，停顿由 NesBus 计算。
    /// </summary>
    public void WriteOamDma(ReadOnlySpan<byte> page)
    {
        for (int i = 0; i < 256; i++)
            _oam[(byte)(_oamAddress + i)] = page[i];
    }

    // ==================== PPUDATA ====================

    /// <summary>读取 $2007。非调色板区域有读缓冲，调色板读取立即返回。</summary>
    private byte ReadData()
    {
        int address = _v & 0x3FFF;
        byte result;

        if (address >= 0x3F00)
        {
            // 调色板读取是即时的；同时用 $2Fxx 的内容填充读缓冲。
            result = ReadPalette(address);
            _readBuffer = ReadVram((ushort)((address - 0x1000) & 0x3FFF));
        }
        else
        {
            // VRAM 读取返回上一次的缓冲值，再装入新值。
            result = _readBuffer;
            _readBuffer = ReadVram((ushort)address);
        }

        IncrementVramAddress();
        return result;
    }

    /// <summary>写入 $2007。</summary>
    private void WriteData(byte value)
    {
        WriteVram((ushort)(_v & 0x3FFF), value);
        IncrementVramAddress();
    }

    /// <summary>
    /// PPUDATA 地址自增。渲染开启时真实硬件会在拷贝数据的同时推进 loopy
    /// 的 coarse X/Y；这里采用最常见的“按 1 或 32 线性增加”近似，
    /// 对初始化 VRAM、传 CHR 数据等常见用途完全够用。
    /// </summary>
    private void IncrementVramAddress()
    {
        int increment = (_control & 0x04) != 0 ? 32 : 1;
        _v = (ushort)((_v + increment) & 0x3FFF);
    }

    // ==================== PPU 地址空间 ====================

    private byte ReadVram(ushort address)
    {
        int addr = address & 0x3FFF;

        if (addr < 0x2000)
            return _cartridge.PpuRead((ushort)addr); // pattern table

        if (addr < 0x3F00)
        {
            // $3000-$3EFF 是 $2000-$2EFF 的镜像。
            if (addr >= 0x3000)
                addr -= 0x1000;

            return ReadNameTable((ushort)addr);
        }

        return ReadPalette(addr);
    }

    private void WriteVram(ushort address, byte value)
    {
        int addr = address & 0x3FFF;

        if (addr < 0x2000)
        {
            _cartridge.PpuWrite((ushort)addr, value); // CHR-RAM 可写
            return;
        }

        if (addr < 0x3F00)
        {
            if (addr >= 0x3000)
                addr -= 0x1000;

            WriteNameTable((ushort)addr, value);
            return;
        }

        WritePalette(addr, value);
    }

    /// <summary>把逻辑名称表号（0-3）按当前镜像方式映射到物理 CIRAM 表号。</summary>
    private int MapNameTable(int logicalIndex)
    {
        return _cartridge.Mirroring switch
        {
            Mirroring.Vertical => logicalIndex & 1,       // A B A B
            Mirroring.Horizontal => logicalIndex >> 1,    // A A B B
            Mirroring.SingleScreenLower => 0,
            Mirroring.SingleScreenUpper => 1,
            Mirroring.FourScreen => logicalIndex & 3,     // 四块独立
            _ => logicalIndex & 1,
        };
    }

    private byte ReadNameTable(ushort address)
    {
        int logical = (address >> 10) & 0x03;
        int offset = address & 0x03FF;
        int physical = MapNameTable(logical);
        return _nameTableRam[(physical * 0x400 + offset) % _nameTableRam.Length];
    }

    private void WriteNameTable(ushort address, byte value)
    {
        int logical = (address >> 10) & 0x03;
        int offset = address & 0x03FF;
        int physical = MapNameTable(logical);
        _nameTableRam[(physical * 0x400 + offset) % _nameTableRam.Length] = value;
    }

    /// <summary>调色板索引镜像：$3F10/$3F14/$3F18/$3F1C 映射到 $3F00/$3F04/$3F08/$3F0C。</summary>
    private static int MirrorPaletteIndex(int address)
    {
        int index = address & 0x1F;
        if ((index & 0x13) == 0x10)
            index &= 0x0F;
        return index;
    }

    private byte ReadPalette(int address) => _paletteRam[MirrorPaletteIndex(address)];

    private void WritePalette(int address, byte value)
    {
        _paletteRam[MirrorPaletteIndex(address)] = (byte)(value & 0x3F);
    }

    // ==================== 逐 dot 推进 ====================

    /// <summary>推进若干个 PPU dot。</summary>
    public void Step(int dots)
    {
        for (int i = 0; i < dots; i++)
            Step();
    }

    /// <summary>推进一个 PPU dot，并处理该 dot 上的事件和像素输出。</summary>
    public void Step()
    {
        // ---- 扫描线起点/特殊事件 ----
        if (Scanline == 241 && Cycle == 1)
        {
            // 进入 vblank：置 vblank 标志，若 NMI 使能则拉高 NMI 线。
            _status |= 0x80;
            UpdateNmiLine();
        }
        else if (Scanline == 261 && Cycle == 1)
        {
            // pre-render：清除 vblank、sprite 0、overflow，准备新一帧。
            _status &= 0x1F;
            UpdateNmiLine();
        }

        if (Scanline < VisibleScanlines && Cycle == 1)
        {
            // 在每条可见扫描线开始处准备背景和精灵行缓存。
            if (IsRenderingEnabled)
            {
                BuildBackgroundLine();
                EvaluateSpritesForScanline();
            }
            else
            {
                FillBackdropLine();
            }
        }

        // ---- 可见像素输出 ----
        if (Scanline < VisibleScanlines && Cycle is >= 1 and <= 256 && IsRenderingEnabled)
            RenderPixel(Cycle - 1);

        // ---- 渲染管线在该 dot 上的寄存器操作 ----
        if (IsRenderingEnabled)
        {
            if (Cycle == 256 && Scanline <= 261)
                IncrementY();

            if (Cycle == 257)
                CopyHorizontalScrollBits();

            if (Scanline == 261 && Cycle is >= 280 and <= 304)
                CopyVerticalScrollBits();

            // MMC3 扫描线计数器：真实硬件由 A12 上升沿驱动，这里用 dot 260 近似。
            if (Cycle == 260 && (Scanline < VisibleScanlines || Scanline == 261))
                _cartridge.NotifyScanline();
        }

        AdvanceDot();
    }

    /// <summary>推进 dot，处理扫描线/帧回绕和 NTSC 奇数帧跳 dot。</summary>
    private void AdvanceDot()
    {
        // NTSC 奇数帧在 pre-render 的最后一条扫描线少一个 dot，用于让帧率为
        // 60.0988 Hz 而不是精确的 60 Hz。普通教学模拟可以忽略，此处保留。
        if (Scanline == 261 && _oddFrame && Cycle == 339)
        {
            Cycle = 0;
            Scanline = 0;
            CompleteFrame();
            return;
        }

        Cycle++;
        if (Cycle < DotsPerScanline)
            return;

        Cycle = 0;
        Scanline++;

        if (Scanline >= ScanlinesPerFrame)
        {
            Scanline = 0;
            CompleteFrame();
        }
    }

    private void CompleteFrame()
    {
        FrameCount++;
        _oddFrame = !_oddFrame;
        FrameCompleted?.Invoke();
    }

    /// <summary>根据 vblank 状态和 NMI 使能更新 NMI 输出线。</summary>
    private void UpdateNmiLine()
    {
        NmiOutput = (_status & 0x80) != 0 && (_control & 0x80) != 0;
    }

    // ==================== 滚动寄存器（loopy） ====================

    /// <summary>把 t 的水平滚动位复制到 v。每条扫描线 dot 257 执行。</summary>
    private void CopyHorizontalScrollBits()
    {
        _v = (ushort)((_v & 0x7BE0) | (_t & 0x041F));
    }

    /// <summary>把 t 的垂直滚动位复制到 v。pre-render 的 dot 280-304 执行。</summary>
    private void CopyVerticalScrollBits()
    {
        _v = (ushort)((_v & 0x041F) | (_t & 0x7BE0));
    }

    /// <summary>增加 fine Y；到达 7 后增加 coarse Y，并在 29 处回绕到 0。</summary>
    private void IncrementY()
    {
        if ((_v & 0x7000) != 0x7000)
        {
            _v = (ushort)(_v + 0x1000);
            return;
        }

        _v = (ushort)(_v & 0x0FFF);
        int coarseY = (_v >> 5) & 0x1F;

        if (coarseY == 29)
        {
            coarseY = 0;
            _v ^= 0x0800; // 切换垂直名称表
        }
        else if (coarseY == 31)
        {
            coarseY = 0;
        }
        else
        {
            coarseY++;
        }

        _v = (ushort)((_v & ~0x03E0) | (coarseY << 5));
    }
}
