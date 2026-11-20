using static NesEmulator.Core.Cpu.AddressingMode;
using static NesEmulator.Core.Cpu.CpuOperation;

namespace NesEmulator.Core.Cpu;

/// <summary>
/// 完整的 256 项 6502 指令表。
///
/// 为什么用表驱动？
///   256 个操作码如果写成 256 个 switch 分支，既难检查又难扩展。
///   6502 的设计其实非常规整：高 4 位和低 4 位分别编码操作类别与寻址方式。
///   把“操作码 → 助记符/寻址/周期”独立成表，执行器只写一次通用逻辑，
///   教学时也能一眼对照著名的 16×16 操作码矩阵。
///
/// 周期数说明：
///   BaseCycles 是“不跨页、分支不跳”的基准值。
///   读指令跨页时额外 +1；分支“跳了”额外 +1，跨页再 +1。
///   这些额外周期由 Cpu6502.Execute 负责计算。
///
/// 非官方指令：
///   6502 的设计中有些操作码没有文档定义，但硬件实际执行组合操作，
///   例如 LAX = LDA + LDX，DCP = DEC + CMP。部分 NES 游戏/测试 ROM 会使用它们。
///   本表把 256 项全部填满；KIL/JAM 会让 CPU 停止，属于故意保留的行为。
/// </summary>
public static class OpcodeTable
{
    /// <summary>256 项指令描述，索引即操作码。</summary>
    public static OpcodeInfo[] Entries { get; } = Build();

    /// <summary>按操作码查询。CPU 热路径也可直接使用 Entries[opcode]。</summary>
    public static OpcodeInfo Get(byte opcode) => Entries[opcode];

    private static OpcodeInfo O(
        byte code,
        string mnemonic,
        AddressingMode mode,
        CpuOperation operation,
        int cycles,
        bool pageCross = false,
        bool official = true) =>
        new(code, mnemonic, mode, operation, cycles, pageCross, official);

    /// <summary>
    /// 构造操作码矩阵。每行 16 个，行号是高 4 位、列号是低 4 位，
    /// 与 NESdev 上的 6502 opcode matrix 完全对应。
    /// </summary>
    private static OpcodeInfo[] Build()
    {
        var table = new OpcodeInfo[256];

        // ===== 0x00 - 0x0F =====
        table[0x00] = O(0x00, "BRK", Implied, BRK, 7);
        table[0x01] = O(0x01, "ORA", IndirectX, ORA, 6);
        table[0x02] = O(0x02, "KIL", Implied, KIL, 2, official: false);
        table[0x03] = O(0x03, "SLO", IndirectX, SLO, 8, official: false);
        table[0x04] = O(0x04, "NOP", ZeroPage, NOP, 3, official: false);
        table[0x05] = O(0x05, "ORA", ZeroPage, ORA, 3);
        table[0x06] = O(0x06, "ASL", ZeroPage, ASL, 5);
        table[0x07] = O(0x07, "SLO", ZeroPage, SLO, 5, official: false);
        table[0x08] = O(0x08, "PHP", Implied, PHP, 3);
        table[0x09] = O(0x09, "ORA", Immediate, ORA, 2);
        table[0x0A] = O(0x0A, "ASL", Accumulator, ASL, 2);
        table[0x0B] = O(0x0B, "ANC", Immediate, ANC, 2, official: false);
        table[0x0C] = O(0x0C, "NOP", Absolute, NOP, 4, official: false);
        table[0x0D] = O(0x0D, "ORA", Absolute, ORA, 4);
        table[0x0E] = O(0x0E, "ASL", Absolute, ASL, 6);
        table[0x0F] = O(0x0F, "SLO", Absolute, SLO, 6, official: false);

        // ===== 0x10 - 0x1F =====
        table[0x10] = O(0x10, "BPL", Relative, BPL, 2);
        table[0x11] = O(0x11, "ORA", IndirectY, ORA, 5, pageCross: true);
        table[0x12] = O(0x12, "KIL", Implied, KIL, 2, official: false);
        table[0x13] = O(0x13, "SLO", IndirectY, SLO, 8, official: false);
        table[0x14] = O(0x14, "NOP", ZeroPageX, NOP, 4, official: false);
        table[0x15] = O(0x15, "ORA", ZeroPageX, ORA, 4);
        table[0x16] = O(0x16, "ASL", ZeroPageX, ASL, 6);
        table[0x17] = O(0x17, "SLO", ZeroPageX, SLO, 6, official: false);
        table[0x18] = O(0x18, "CLC", Implied, CLC, 2);
        table[0x19] = O(0x19, "ORA", AbsoluteY, ORA, 4, pageCross: true);
        table[0x1A] = O(0x1A, "NOP", Implied, NOP, 2, official: false);
        table[0x1B] = O(0x1B, "SLO", AbsoluteY, SLO, 7, official: false);
        table[0x1C] = O(0x1C, "NOP", AbsoluteX, NOP, 4, pageCross: true, official: false);
        table[0x1D] = O(0x1D, "ORA", AbsoluteX, ORA, 4, pageCross: true);
        table[0x1E] = O(0x1E, "ASL", AbsoluteX, ASL, 7);
        table[0x1F] = O(0x1F, "SLO", AbsoluteX, SLO, 7, official: false);

        // ===== 0x20 - 0x2F =====
        table[0x20] = O(0x20, "JSR", Absolute, JSR, 6);
        table[0x21] = O(0x21, "AND", IndirectX, AND, 6);
        table[0x22] = O(0x22, "KIL", Implied, KIL, 2, official: false);
        table[0x23] = O(0x23, "RLA", IndirectX, RLA, 8, official: false);
        table[0x24] = O(0x24, "BIT", ZeroPage, BIT, 3);
        table[0x25] = O(0x25, "AND", ZeroPage, AND, 3);
        table[0x26] = O(0x26, "ROL", ZeroPage, ROL, 5);
        table[0x27] = O(0x27, "RLA", ZeroPage, RLA, 5, official: false);
        table[0x28] = O(0x28, "PLP", Implied, PLP, 4);
        table[0x29] = O(0x29, "AND", Immediate, AND, 2);
        table[0x2A] = O(0x2A, "ROL", Accumulator, ROL, 2);
        table[0x2B] = O(0x2B, "ANC", Immediate, ANC, 2, official: false);
        table[0x2C] = O(0x2C, "BIT", Absolute, BIT, 4);
        table[0x2D] = O(0x2D, "AND", Absolute, AND, 4);
        table[0x2E] = O(0x2E, "ROL", Absolute, ROL, 6);
        table[0x2F] = O(0x2F, "RLA", Absolute, RLA, 6, official: false);

        // ===== 0x30 - 0x3F =====
        table[0x30] = O(0x30, "BMI", Relative, BMI, 2);
        table[0x31] = O(0x31, "AND", IndirectY, AND, 5, pageCross: true);
        table[0x32] = O(0x32, "KIL", Implied, KIL, 2, official: false);
        table[0x33] = O(0x33, "RLA", IndirectY, RLA, 8, official: false);
        table[0x34] = O(0x34, "NOP", ZeroPageX, NOP, 4, official: false);
        table[0x35] = O(0x35, "AND", ZeroPageX, AND, 4);
        table[0x36] = O(0x36, "ROL", ZeroPageX, ROL, 6);
        table[0x37] = O(0x37, "RLA", ZeroPageX, RLA, 6, official: false);
        table[0x38] = O(0x38, "SEC", Implied, SEC, 2);
        table[0x39] = O(0x39, "AND", AbsoluteY, AND, 4, pageCross: true);
        table[0x3A] = O(0x3A, "NOP", Implied, NOP, 2, official: false);
        table[0x3B] = O(0x3B, "RLA", AbsoluteY, RLA, 7, official: false);
        table[0x3C] = O(0x3C, "NOP", AbsoluteX, NOP, 4, pageCross: true, official: false);
        table[0x3D] = O(0x3D, "AND", AbsoluteX, AND, 4, pageCross: true);
        table[0x3E] = O(0x3E, "ROL", AbsoluteX, ROL, 7);
        table[0x3F] = O(0x3F, "RLA", AbsoluteX, RLA, 7, official: false);

        // ===== 0x40 - 0x4F =====
        table[0x40] = O(0x40, "RTI", Implied, RTI, 6);
        table[0x41] = O(0x41, "EOR", IndirectX, EOR, 6);
        table[0x42] = O(0x42, "KIL", Implied, KIL, 2, official: false);
        table[0x43] = O(0x43, "SRE", IndirectX, SRE, 8, official: false);
        table[0x44] = O(0x44, "NOP", ZeroPage, NOP, 3, official: false);
        table[0x45] = O(0x45, "EOR", ZeroPage, EOR, 3);
        table[0x46] = O(0x46, "LSR", ZeroPage, LSR, 5);
        table[0x47] = O(0x47, "SRE", ZeroPage, SRE, 5, official: false);
        table[0x48] = O(0x48, "PHA", Implied, PHA, 3);
        table[0x49] = O(0x49, "EOR", Immediate, EOR, 2);
        table[0x4A] = O(0x4A, "LSR", Accumulator, LSR, 2);
        table[0x4B] = O(0x4B, "ALR", Immediate, ALR, 2, official: false);
        table[0x4C] = O(0x4C, "JMP", Absolute, JMP, 3);
        table[0x4D] = O(0x4D, "EOR", Absolute, EOR, 4);
        table[0x4E] = O(0x4E, "LSR", Absolute, LSR, 6);
        table[0x4F] = O(0x4F, "SRE", Absolute, SRE, 6, official: false);

        // ===== 0x50 - 0x5F =====
        table[0x50] = O(0x50, "BVC", Relative, BVC, 2);
        table[0x51] = O(0x51, "EOR", IndirectY, EOR, 5, pageCross: true);
        table[0x52] = O(0x52, "KIL", Implied, KIL, 2, official: false);
        table[0x53] = O(0x53, "SRE", IndirectY, SRE, 8, official: false);
        table[0x54] = O(0x54, "NOP", ZeroPageX, NOP, 4, official: false);
        table[0x55] = O(0x55, "EOR", ZeroPageX, EOR, 4);
        table[0x56] = O(0x56, "LSR", ZeroPageX, LSR, 6);
        table[0x57] = O(0x57, "SRE", ZeroPageX, SRE, 6, official: false);
        table[0x58] = O(0x58, "CLI", Implied, CLI, 2);
        table[0x59] = O(0x59, "EOR", AbsoluteY, EOR, 4, pageCross: true);
        table[0x5A] = O(0x5A, "NOP", Implied, NOP, 2, official: false);
        table[0x5B] = O(0x5B, "SRE", AbsoluteY, SRE, 7, official: false);
        table[0x5C] = O(0x5C, "NOP", AbsoluteX, NOP, 4, pageCross: true, official: false);
        table[0x5D] = O(0x5D, "EOR", AbsoluteX, EOR, 4, pageCross: true);
        table[0x5E] = O(0x5E, "LSR", AbsoluteX, LSR, 7);
        table[0x5F] = O(0x5F, "SRE", AbsoluteX, SRE, 7, official: false);

        // ===== 0x60 - 0x6F =====
        table[0x60] = O(0x60, "RTS", Implied, RTS, 6);
        table[0x61] = O(0x61, "ADC", IndirectX, ADC, 6);
        table[0x62] = O(0x62, "KIL", Implied, KIL, 2, official: false);
        table[0x63] = O(0x63, "RRA", IndirectX, RRA, 8, official: false);
        table[0x64] = O(0x64, "NOP", ZeroPage, NOP, 3, official: false);
        table[0x65] = O(0x65, "ADC", ZeroPage, ADC, 3);
        table[0x66] = O(0x66, "ROR", ZeroPage, ROR, 5);
        table[0x67] = O(0x67, "RRA", ZeroPage, RRA, 5, official: false);
        table[0x68] = O(0x68, "PLA", Implied, PLA, 4);
        table[0x69] = O(0x69, "ADC", Immediate, ADC, 2);
        table[0x6A] = O(0x6A, "ROR", Accumulator, ROR, 2);
        table[0x6B] = O(0x6B, "ARR", Immediate, ARR, 2, official: false);
        table[0x6C] = O(0x6C, "JMP", Indirect, JMP, 5);
        table[0x6D] = O(0x6D, "ADC", Absolute, ADC, 4);
        table[0x6E] = O(0x6E, "ROR", Absolute, ROR, 6);
        table[0x6F] = O(0x6F, "RRA", Absolute, RRA, 6, official: false);

        // ===== 0x70 - 0x7F =====
        table[0x70] = O(0x70, "BVS", Relative, BVS, 2);
        table[0x71] = O(0x71, "ADC", IndirectY, ADC, 5, pageCross: true);
        table[0x72] = O(0x72, "KIL", Implied, KIL, 2, official: false);
        table[0x73] = O(0x73, "RRA", IndirectY, RRA, 8, official: false);
        table[0x74] = O(0x74, "NOP", ZeroPageX, NOP, 4, official: false);
        table[0x75] = O(0x75, "ADC", ZeroPageX, ADC, 4);
        table[0x76] = O(0x76, "ROR", ZeroPageX, ROR, 6);
        table[0x77] = O(0x77, "RRA", ZeroPageX, RRA, 6, official: false);
        table[0x78] = O(0x78, "SEI", Implied, SEI, 2);
        table[0x79] = O(0x79, "ADC", AbsoluteY, ADC, 4, pageCross: true);
        table[0x7A] = O(0x7A, "NOP", Implied, NOP, 2, official: false);
        table[0x7B] = O(0x7B, "RRA", AbsoluteY, RRA, 7, official: false);
        table[0x7C] = O(0x7C, "NOP", AbsoluteX, NOP, 4, pageCross: true, official: false);
        table[0x7D] = O(0x7D, "ADC", AbsoluteX, ADC, 4, pageCross: true);
        table[0x7E] = O(0x7E, "ROR", AbsoluteX, ROR, 7);
        table[0x7F] = O(0x7F, "RRA", AbsoluteX, RRA, 7, official: false);

        // ===== 0x80 - 0x8F =====
        table[0x80] = O(0x80, "NOP", Immediate, NOP, 2, official: false);
        table[0x81] = O(0x81, "STA", IndirectX, STA, 6);
        table[0x82] = O(0x82, "NOP", Immediate, NOP, 2, official: false);
        table[0x83] = O(0x83, "SAX", IndirectX, SAX, 6, official: false);
        table[0x84] = O(0x84, "STY", ZeroPage, STY, 3);
        table[0x85] = O(0x85, "STA", ZeroPage, STA, 3);
        table[0x86] = O(0x86, "STX", ZeroPage, STX, 3);
        table[0x87] = O(0x87, "SAX", ZeroPage, SAX, 3, official: false);
        table[0x88] = O(0x88, "DEY", Implied, DEY, 2);
        table[0x89] = O(0x89, "NOP", Immediate, NOP, 2, official: false);
        table[0x8A] = O(0x8A, "TXA", Implied, TXA, 2);
        table[0x8B] = O(0x8B, "XAA", Immediate, XAA, 2, official: false);
        table[0x8C] = O(0x8C, "STY", Absolute, STY, 4);
        table[0x8D] = O(0x8D, "STA", Absolute, STA, 4);
        table[0x8E] = O(0x8E, "STX", Absolute, STX, 4);
        table[0x8F] = O(0x8F, "SAX", Absolute, SAX, 4, official: false);

        // ===== 0x90 - 0x9F =====
        table[0x90] = O(0x90, "BCC", Relative, BCC, 2);
        table[0x91] = O(0x91, "STA", IndirectY, STA, 6);
        table[0x92] = O(0x92, "KIL", Implied, KIL, 2, official: false);
        table[0x93] = O(0x93, "AHX", IndirectY, AHX, 6, official: false);
        table[0x94] = O(0x94, "STY", ZeroPageX, STY, 4);
        table[0x95] = O(0x95, "STA", ZeroPageX, STA, 4);
        table[0x96] = O(0x96, "STX", ZeroPageY, STX, 4);
        table[0x97] = O(0x97, "SAX", ZeroPageY, SAX, 4, official: false);
        table[0x98] = O(0x98, "TYA", Implied, TYA, 2);
        table[0x99] = O(0x99, "STA", AbsoluteY, STA, 5);
        table[0x9A] = O(0x9A, "TXS", Implied, TXS, 2);
        table[0x9B] = O(0x9B, "TAS", AbsoluteY, TAS, 5, official: false);
        table[0x9C] = O(0x9C, "SHY", AbsoluteX, SHY, 5, official: false);
        table[0x9D] = O(0x9D, "STA", AbsoluteX, STA, 5);
        table[0x9E] = O(0x9E, "SHX", AbsoluteY, SHX, 5, official: false);
        table[0x9F] = O(0x9F, "AHX", AbsoluteY, AHX, 5, official: false);

        // ===== 0xA0 - 0xAF =====
        table[0xA0] = O(0xA0, "LDY", Immediate, LDY, 2);
        table[0xA1] = O(0xA1, "LDA", IndirectX, LDA, 6);
        table[0xA2] = O(0xA2, "LDX", Immediate, LDX, 2);
        table[0xA3] = O(0xA3, "LAX", IndirectX, LAX, 6, official: false);
        table[0xA4] = O(0xA4, "LDY", ZeroPage, LDY, 3);
        table[0xA5] = O(0xA5, "LDA", ZeroPage, LDA, 3);
        table[0xA6] = O(0xA6, "LDX", ZeroPage, LDX, 3);
        table[0xA7] = O(0xA7, "LAX", ZeroPage, LAX, 3, official: false);
        table[0xA8] = O(0xA8, "TAY", Implied, TAY, 2);
        table[0xA9] = O(0xA9, "LDA", Immediate, LDA, 2);
        table[0xAA] = O(0xAA, "TAX", Implied, TAX, 2);
        table[0xAB] = O(0xAB, "LAX", Immediate, LAX, 2, official: false);
        table[0xAC] = O(0xAC, "LDY", Absolute, LDY, 4);
        table[0xAD] = O(0xAD, "LDA", Absolute, LDA, 4);
        table[0xAE] = O(0xAE, "LDX", Absolute, LDX, 4);
        table[0xAF] = O(0xAF, "LAX", Absolute, LAX, 4, official: false);

        // ===== 0xB0 - 0xBF =====
        table[0xB0] = O(0xB0, "BCS", Relative, BCS, 2);
        table[0xB1] = O(0xB1, "LDA", IndirectY, LDA, 5, pageCross: true);
        table[0xB2] = O(0xB2, "KIL", Implied, KIL, 2, official: false);
        table[0xB3] = O(0xB3, "LAX", IndirectY, LAX, 5, pageCross: true, official: false);
        table[0xB4] = O(0xB4, "LDY", ZeroPageX, LDY, 4);
        table[0xB5] = O(0xB5, "LDA", ZeroPageX, LDA, 4);
        table[0xB6] = O(0xB6, "LDX", ZeroPageY, LDX, 4);
        table[0xB7] = O(0xB7, "LAX", ZeroPageY, LAX, 4, official: false);
        table[0xB8] = O(0xB8, "CLV", Implied, CLV, 2);
        table[0xB9] = O(0xB9, "LDA", AbsoluteY, LDA, 4, pageCross: true);
        table[0xBA] = O(0xBA, "TSX", Implied, TSX, 2);
        table[0xBB] = O(0xBB, "LAS", AbsoluteY, LAS, 4, pageCross: true, official: false);
        table[0xBC] = O(0xBC, "LDY", AbsoluteX, LDY, 4, pageCross: true);
        table[0xBD] = O(0xBD, "LDA", AbsoluteX, LDA, 4, pageCross: true);
        table[0xBE] = O(0xBE, "LDX", AbsoluteY, LDX, 4, pageCross: true);
        table[0xBF] = O(0xBF, "LAX", AbsoluteY, LAX, 4, pageCross: true, official: false);

        // ===== 0xC0 - 0xCF =====
        table[0xC0] = O(0xC0, "CPY", Immediate, CPY, 2);
        table[0xC1] = O(0xC1, "CMP", IndirectX, CMP, 6);
        table[0xC2] = O(0xC2, "NOP", Immediate, NOP, 2, official: false);
        table[0xC3] = O(0xC3, "DCP", IndirectX, DCP, 8, official: false);
        table[0xC4] = O(0xC4, "CPY", ZeroPage, CPY, 3);
        table[0xC5] = O(0xC5, "CMP", ZeroPage, CMP, 3);
        table[0xC6] = O(0xC6, "DEC", ZeroPage, DEC, 5);
        table[0xC7] = O(0xC7, "DCP", ZeroPage, DCP, 5, official: false);
        table[0xC8] = O(0xC8, "INY", Implied, INY, 2);
        table[0xC9] = O(0xC9, "CMP", Immediate, CMP, 2);
        table[0xCA] = O(0xCA, "DEX", Implied, DEX, 2);
        table[0xCB] = O(0xCB, "AXS", Immediate, AXS, 2, official: false);
        table[0xCC] = O(0xCC, "CPY", Absolute, CPY, 4);
        table[0xCD] = O(0xCD, "CMP", Absolute, CMP, 4);
        table[0xCE] = O(0xCE, "DEC", Absolute, DEC, 6);
        table[0xCF] = O(0xCF, "DCP", Absolute, DCP, 6, official: false);

        // ===== 0xD0 - 0xDF =====
        table[0xD0] = O(0xD0, "BNE", Relative, BNE, 2);
        table[0xD1] = O(0xD1, "CMP", IndirectY, CMP, 5, pageCross: true);
        table[0xD2] = O(0xD2, "KIL", Implied, KIL, 2, official: false);
        table[0xD3] = O(0xD3, "DCP", IndirectY, DCP, 8, official: false);
        table[0xD4] = O(0xD4, "NOP", ZeroPageX, NOP, 4, official: false);
        table[0xD5] = O(0xD5, "CMP", ZeroPageX, CMP, 4);
        table[0xD6] = O(0xD6, "DEC", ZeroPageX, DEC, 6);
        table[0xD7] = O(0xD7, "DCP", ZeroPageX, DCP, 6, official: false);
        table[0xD8] = O(0xD8, "CLD", Implied, CLD, 2);
        table[0xD9] = O(0xD9, "CMP", AbsoluteY, CMP, 4, pageCross: true);
        table[0xDA] = O(0xDA, "NOP", Implied, NOP, 2, official: false);
        table[0xDB] = O(0xDB, "DCP", AbsoluteY, DCP, 7, official: false);
        table[0xDC] = O(0xDC, "NOP", AbsoluteX, NOP, 4, pageCross: true, official: false);
        table[0xDD] = O(0xDD, "CMP", AbsoluteX, CMP, 4, pageCross: true);
        table[0xDE] = O(0xDE, "DEC", AbsoluteX, DEC, 7);
        table[0xDF] = O(0xDF, "DCP", AbsoluteX, DCP, 7, official: false);

        // ===== 0xE0 - 0xEF =====
        table[0xE0] = O(0xE0, "CPX", Immediate, CPX, 2);
        table[0xE1] = O(0xE1, "SBC", IndirectX, SBC, 6);
        table[0xE2] = O(0xE2, "NOP", Immediate, NOP, 2, official: false);
        table[0xE3] = O(0xE3, "ISC", IndirectX, ISC, 8, official: false);
        table[0xE4] = O(0xE4, "CPX", ZeroPage, CPX, 3);
        table[0xE5] = O(0xE5, "SBC", ZeroPage, SBC, 3);
        table[0xE6] = O(0xE6, "INC", ZeroPage, INC, 5);
        table[0xE7] = O(0xE7, "ISC", ZeroPage, ISC, 5, official: false);
        table[0xE8] = O(0xE8, "INX", Implied, INX, 2);
        table[0xE9] = O(0xE9, "SBC", Immediate, SBC, 2);
        table[0xEA] = O(0xEA, "NOP", Implied, NOP, 2);
        table[0xEB] = O(0xEB, "SBC", Immediate, SBC, 2, official: false);
        table[0xEC] = O(0xEC, "CPX", Absolute, CPX, 4);
        table[0xED] = O(0xED, "SBC", Absolute, SBC, 4);
        table[0xEE] = O(0xEE, "INC", Absolute, INC, 6);
        table[0xEF] = O(0xEF, "ISC", Absolute, ISC, 6, official: false);

        // ===== 0xF0 - 0xFF =====
        table[0xF0] = O(0xF0, "BEQ", Relative, BEQ, 2);
        table[0xF1] = O(0xF1, "SBC", IndirectY, SBC, 5, pageCross: true);
        table[0xF2] = O(0xF2, "KIL", Implied, KIL, 2, official: false);
        table[0xF3] = O(0xF3, "ISC", IndirectY, ISC, 8, official: false);
        table[0xF4] = O(0xF4, "NOP", ZeroPageX, NOP, 4, official: false);
        table[0xF5] = O(0xF5, "SBC", ZeroPageX, SBC, 4);
        table[0xF6] = O(0xF6, "INC", ZeroPageX, INC, 6);
        table[0xF7] = O(0xF7, "ISC", ZeroPageX, ISC, 6, official: false);
        table[0xF8] = O(0xF8, "SED", Implied, SED, 2);
        table[0xF9] = O(0xF9, "SBC", AbsoluteY, SBC, 4, pageCross: true);
        table[0xFA] = O(0xFA, "NOP", Implied, NOP, 2, official: false);
        table[0xFB] = O(0xFB, "ISC", AbsoluteY, ISC, 7, official: false);
        table[0xFC] = O(0xFC, "NOP", AbsoluteX, NOP, 4, pageCross: true, official: false);
        table[0xFD] = O(0xFD, "SBC", AbsoluteX, SBC, 4, pageCross: true);
        table[0xFE] = O(0xFE, "INC", AbsoluteX, INC, 7);
        table[0xFF] = O(0xFF, "ISC", AbsoluteX, ISC, 7, official: false);

        // 自检：漏填/错位会立刻在类型初始化时暴露，而不是运行到某个冷门指令才崩溃。
        if (table.Length != 256)
            throw new InvalidOperationException("操作码表必须恰好有 256 项。");
        for (int i = 0; i < table.Length; i++)
        {
            if (table[i].Code != i)
                throw new InvalidOperationException($"操作码表第 {i:X2} 项编码错误：{table[i].Code:X2}。");
        }

        return table;
    }
}
