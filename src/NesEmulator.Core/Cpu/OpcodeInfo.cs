namespace NesEmulator.Core.Cpu;

/// <summary>6502 的寻址方式。理解它们比背 256 条指令更重要。</summary>
public enum AddressingMode
{
    /// <summary>指令隐含操作数，例如 CLC、INX。</summary>
    Implied,

    /// <summary>操作累加器 A，例如 ASL A。</summary>
    Accumulator,

    /// <summary>立即数：操作数就在指令后一字节。</summary>
    Immediate,

    /// <summary>零页：8 位地址，$00-$FF，访问更快也更短。</summary>
    ZeroPage,

    /// <summary>零页 + X（8 位回绕）。</summary>
    ZeroPageX,

    /// <summary>零页 + Y（8 位回绕）。</summary>
    ZeroPageY,

    /// <summary>16 位绝对地址。</summary>
    Absolute,

    /// <summary>绝对地址 + X，可能跨页，读指令会多 1 周期。</summary>
    AbsoluteX,

    /// <summary>绝对地址 + Y，可能跨页。</summary>
    AbsoluteY,

    /// <summary>间接：读取地址处存放的 16 位指针（仅 JMP 使用）。</summary>
    Indirect,

    /// <summary>(zp,X)：零页指针加 X 后间接寻址。</summary>
    IndirectX,

    /// <summary>(zp),Y：零页指针间接寻址后加 Y。</summary>
    IndirectY,

    /// <summary>相对寻址：8 位有符号偏移，用于分支。</summary>
    Relative,
}

/// <summary>
/// 指令执行类别。寻址方式决定“从哪里取数”，这个枚举决定“对数据做什么”。
/// 非官方指令（undocumented opcodes）也列在这里，以便完整覆盖 256 个字节值。
/// </summary>
public enum CpuOperation
{
    ADC, AND, ASL, BCC, BCS, BEQ, BIT, BMI, BNE, BPL, BRK, BVC, BVS,
    CLC, CLD, CLI, CLV, CMP, CPX, CPY, DEC, DEX, DEY, EOR, INC, INX,
    INY, JMP, JSR, LDA, LDX, LDY, LSR, NOP, ORA, PHA, PHP, PLA, PLP,
    ROL, ROR, RTI, RTS, SBC, SEC, SED, SEI, STA, STX, STY, TAX, TAY,
    TSX, TXA, TXS, TYA,

    // ---- 非官方（未公开）指令 ----
    SLO, RLA, SRE, RRA, SAX, LAX, DCP, ISC, ANC, ALR, ARR, AXS,
    XAA, AHX, SHY, SHX, TAS, LAS, KIL,
}

/// <summary>
/// 一条 6502 指令的静态描述。CPU 执行时先查这张表，再根据寻址方式取值。
/// </summary>
/// <param name="Code">操作码字节。</param>
/// <param name="Mnemonic">汇编助记符。</param>
/// <param name="Mode">寻址方式。</param>
/// <param name="Operation">操作类型。</param>
/// <param name="BaseCycles">不计算额外周期时的基础周期数。</param>
/// <param name="PageCrossPenalty">读操作跨页时是否 +1 周期。</param>
/// <param name="IsOfficial">是否为 MOS/任天堂文档中公开的官方指令。</param>
public readonly record struct OpcodeInfo(
    byte Code,
    string Mnemonic,
    AddressingMode Mode,
    CpuOperation Operation,
    int BaseCycles,
    bool PageCrossPenalty = false,
    bool IsOfficial = true);
