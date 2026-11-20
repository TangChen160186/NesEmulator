namespace NesEmulator.Core.Cpu;

/// <summary>6502 状态寄存器中的位。B 位只存在于压栈副本中，不是真实寄存器位。</summary>
[Flags]
public enum CpuFlags : byte
{
    Carry = 0x01,             // C：加法进位 / 减法无借位 / 移位溢出位
    Zero = 0x02,              // Z：上一条结果是否为 0
    InterruptDisable = 0x04,  // I：为 1 时屏蔽 IRQ（NMI 不受影响）
    Decimal = 0x08,           // D：十进制模式；NES 的 2A03 实际忽略它
    Break = 0x10,             // B：仅用于 PHP/BRK 压栈的软件标志
    Unused = 0x20,            // 恒为 1
    Overflow = 0x40,          // V：有符号溢出
    Negative = 0x80,          // N：结果 bit 7
}

/// <summary>
/// 可在测试、存档和调试器中保存的 CPU 快照。
/// P 字段按 6502 状态寄存器布局编码；B 位会被规范化。
/// </summary>
public readonly record struct CpuState(
    byte A,
    byte X,
    byte Y,
    byte S,
    byte P,
    ushort PC,
    long Cycles,
    bool Jammed);

/// <summary>
/// NES 使用的 Ricoh 2A03 CPU 核心（MOS 6502 变种）。
///
/// 与标准 6502 的差异：
///   - 2A03 去掉了十进制模式，ADC/SBC 在 D=1 时也按二进制运算；
///   - 时钟为 NTSC 1.789773 MHz（PAL 为 1.662607 MHz）；
///   - NMI/IRQ 由主板上的 PPU、APU、mapper 提供。
///
/// 本类负责三件事：
///   1. 寄存器与内存访问；
///   2. 中断/复位时序；
///   3. 查 <see cref="OpcodeTable"/> 并执行指令，返回精确的 CPU 周期数。
///
/// 设计边界：CPU 以“整条指令”为粒度执行，PPU/APU 由 NesConsole 在指令结束后
/// 统一推进对应的周期数。这是学习型模拟器常用的周期近似；若要做逐 dot 级
/// PPU 时序（例如通过所有 blargg 时序测试），可进一步把每次总线访问拆成时钟。
/// </summary>
public sealed partial class Cpu6502
{
    /// <summary>复位向量地址。</summary>
    public const ushort ResetVector = 0xFFFC;

    /// <summary>NMI 向量地址。</summary>
    public const ushort NmiVector = 0xFFFA;

    /// <summary>IRQ/BRK 向量地址。</summary>
    public const ushort IrqVector = 0xFFFE;

    private readonly ICpuBus _bus;
    private byte _status;
    private bool _previousNmiLine;

    public Cpu6502(ICpuBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        Reset();
    }

    /// <summary>累加器 A。</summary>
    public byte A { get; private set; }

    /// <summary>X 变址寄存器。</summary>
    public byte X { get; private set; }

    /// <summary>Y 变址寄存器。</summary>
    public byte Y { get; private set; }

    /// <summary>栈指针，指向 $0100-$01FF 栈页。压栈后递减。</summary>
    public byte SP { get; private set; }

    /// <summary>程序计数器。</summary>
    public ushort PC { get; private set; }

    /// <summary>状态寄存器（bit 5 恒为 1；bit 4 不作为实际状态保存）。</summary>
    public byte P => (byte)(_status | (byte)CpuFlags.Unused);

    /// <summary>累计执行的 CPU 周期数，复位后从 7 开始（与 nestest 约定一致）。</summary>
    public long TotalCycles { get; private set; }

    /// <summary>PPU 拉高的 NMI 线。CPU 在指令边界检测上升沿。</summary>
    public bool NmiLine { get; set; }

    /// <summary>IRQ 线（低有效；这里 true 表示有中断源在请求）。</summary>
    public bool IrqLine { get; set; }

    /// <summary>是否执行到 KIL/JAM 非法指令。真实 CPU 会停止取指。</summary>
    public bool Jammed { get; private set; }

    /// <summary>把 CPU 复位到上电状态，并从 $FFFC 读取复位向量。</summary>
    public void Reset()
    {
        A = 0;
        X = 0;
        Y = 0;
        SP = 0xFD;
        _status = (byte)(CpuFlags.InterruptDisable | CpuFlags.Unused);
        PC = Read16(ResetVector);
        TotalCycles = 7;
        NmiLine = false;
        IrqLine = false;
        _previousNmiLine = false;
        Jammed = false;
    }

    /// <summary>
    /// 设置 CPU 状态。仅用于单元测试、nestest 自动对拍和未来存档功能；
    /// 正常模拟流程绝不应调用。
    /// </summary>
    public void RestoreState(CpuState state)
    {
        A = state.A;
        X = state.X;
        Y = state.Y;
        SP = state.S;
        PC = state.PC;
        // B 位不存储；保留 bit 5，忽略 bit 4。
        _status = (byte)((state.P & 0xCF) | (byte)CpuFlags.Unused);
        TotalCycles = state.Cycles;
        Jammed = state.Jammed;
        _previousNmiLine = false;
    }

    /// <summary>获取当前状态快照。</summary>
    public CpuState CaptureState() => new(A, X, Y, SP, P, PC, TotalCycles, Jammed);

    /// <summary>
    /// 执行一条指令（或响应一次中断），返回消耗的 CPU 周期。
    ///
    /// 中断检查顺序：
    ///   1. NMI 上升沿（不可屏蔽）；
    ///   2. IRQ 且 I=0；
    ///   3. 否则取指执行。
    ///
    /// 真实 6502 的中断有 7 周期响应序列，这里在 ServiceInterrupt 中完整模拟。
    /// </summary>
    public int Step()
    {
        if (Jammed)
            return 2;

        // NMI 是边沿触发：vblank 期间 NMI 线保持高电平，只应响应一次。
        bool nmiEdge = NmiLine && !_previousNmiLine;
        _previousNmiLine = NmiLine;

        if (nmiEdge)
        {
            int interruptCycles = ServiceInterrupt(NmiVector, isBreak: false);
            TotalCycles += interruptCycles;
            return interruptCycles;
        }

        // IRQ 是电平触发：只要中断源未清除且 I=0，就会重复进入。
        if (IrqLine && !GetFlag(CpuFlags.InterruptDisable))
        {
            int interruptCycles = ServiceInterrupt(IrqVector, isBreak: false);
            TotalCycles += interruptCycles;
            return interruptCycles;
        }

        byte opcode = Read(PC++);
        OpcodeInfo info = OpcodeTable.Entries[opcode];
        int cycles = Execute(info);
        TotalCycles += cycles;
        return cycles;
    }

    /// <summary>增加外部 DMA 等造成的 CPU 停顿周期，并推进累计时钟。</summary>
    public void AddStallCycles(int cycles)
    {
        if (cycles > 0)
            TotalCycles += cycles;
    }

    /// <summary>读取一个字节。</summary>
    private byte Read(ushort address) => _bus.Read(address);

    /// <summary>写入一个字节。</summary>
    private void Write(ushort address, byte value) => _bus.Write(address, value);

    /// <summary>读取小端 16 位字。地址 +1 在 16 位空间内回绕。</summary>
    private ushort Read16(ushort address)
    {
        byte lo = Read(address);
        byte hi = Read((ushort)(address + 1));
        return (ushort)(lo | (hi << 8));
    }

    /// <summary>从 PC 取指并前进。</summary>
    private byte FetchByte() => Read(PC++);

    /// <summary>读取状态位。</summary>
    private bool GetFlag(CpuFlags flag) => (_status & (byte)flag) != 0;

    /// <summary>设置或清除状态位。</summary>
    private void SetFlag(CpuFlags flag, bool value)
    {
        if (value)
            _status |= (byte)flag;
        else
            _status &= (byte)~flag;
        _status |= (byte)CpuFlags.Unused;
    }

    /// <summary>根据结果设置 N、Z 标志。</summary>
    private void SetNZ(byte value)
    {
        SetFlag(CpuFlags.Zero, value == 0);
        SetFlag(CpuFlags.Negative, (value & 0x80) != 0);
    }

    /// <summary>压栈：先写 $0100+SP，再递减 SP。</summary>
    private void Push(byte value) => Write((ushort)(0x0100 | SP--), value);

    /// <summary>出栈：先递增 SP，再读 $0100+SP。</summary>
    private byte Pull() => Read((ushort)(0x0100 | ++SP));

    /// <summary>
    /// 执行中断/BRK 的 7 周期序列：
    ///   压入 PC 高、低字节，再压入状态寄存器，设置 I，最后读中断向量。
    /// BRK 与硬件 IRQ 共用 $FFFE 向量，区别只在压栈的 B 位。
    /// </summary>
    private int ServiceInterrupt(ushort vector, bool isBreak)
    {
        Push((byte)(PC >> 8));
        Push((byte)(PC & 0x00FF));

        byte pushedStatus = (byte)(_status | (byte)CpuFlags.Unused);
        if (isBreak)
            pushedStatus |= (byte)CpuFlags.Break;
        else
            pushedStatus &= (byte)~CpuFlags.Break;

        Push(pushedStatus);
        SetFlag(CpuFlags.InterruptDisable, true);

        PC = Read16(vector);
        return 7;
    }
}
