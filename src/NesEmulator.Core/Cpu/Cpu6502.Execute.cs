using static NesEmulator.Core.Cpu.CpuOperation;

namespace NesEmulator.Core.Cpu;

public sealed partial class Cpu6502
{
    /// <summary>
    /// 执行一条已经取指的操作码，返回周期数。
    ///
    /// 执行被分成几类，避免 256 个重复分支：
    ///   1. 控制流 / 中断 / 栈操作；
    ///   2. 隐含寻址的状态与寄存器操作；
    ///   3. 读操作（LDA/ADC/...），可能因跨页 +1 周期；
    ///   4. 写操作（STA/STX/...），不读目标地址，避免误触发 PPU/APU 读副作用；
    ///   5. 读-改-写操作（ASL/INC/DCP/...）。
    /// </summary>
    private int Execute(in OpcodeInfo info)
    {
        int cycles = info.BaseCycles;

        // ---------- 控制流与特殊指令 ----------
        switch (info.Operation)
        {
            case BRK:
                // BRK 是 2 字节指令：操作码 + 一个被忽略的填充字节。
                PC++;
                return ServiceInterrupt(IrqVector, isBreak: true);

            case JSR:
            {
                ushort target = FetchWord();
                // JSR 压入的是“JSR 最后一个字节”的地址，即当前 PC - 1。
                ushort returnAddress = (ushort)(PC - 1);
                Push((byte)(returnAddress >> 8));
                Push((byte)(returnAddress & 0xFF));
                PC = target;
                return cycles;
            }

            case RTS:
            {
                byte lo = Pull();
                byte hi = Pull();
                PC = (ushort)(((hi << 8) | lo) + 1);
                return cycles;
            }

            case RTI:
            {
                byte status = Pull();
                // RTI 会恢复 I 等标志，但 B 位不是真实状态位，忽略。
                _status = (byte)((status & 0xCF) | (byte)CpuFlags.Unused);
                byte lo = Pull();
                byte hi = Pull();
                PC = (ushort)((hi << 8) | lo);
                return cycles;
            }

            case JMP:
                return ExecuteJump(info.Mode);

            case BPL: return Branch(!GetFlag(CpuFlags.Negative));
            case BMI: return Branch(GetFlag(CpuFlags.Negative));
            case BVC: return Branch(!GetFlag(CpuFlags.Overflow));
            case BVS: return Branch(GetFlag(CpuFlags.Overflow));
            case BCC: return Branch(!GetFlag(CpuFlags.Carry));
            case BCS: return Branch(GetFlag(CpuFlags.Carry));
            case BNE: return Branch(!GetFlag(CpuFlags.Zero));
            case BEQ: return Branch(GetFlag(CpuFlags.Zero));

            case KIL:
                // 真实 6502 遇到 JAM 会停止取指，总线进入不确定状态。
                // 模拟器记录状态并原地停住，方便调试器观察。
                Jammed = true;
                return cycles;

            case NOP:
                return ExecuteNop(info);

            // ---------- 隐含寻址的状态/寄存器操作 ----------
            case CLC: SetFlag(CpuFlags.Carry, false); return cycles;
            case SEC: SetFlag(CpuFlags.Carry, true); return cycles;
            case CLI: SetFlag(CpuFlags.InterruptDisable, false); return cycles;
            case SEI: SetFlag(CpuFlags.InterruptDisable, true); return cycles;
            case CLV: SetFlag(CpuFlags.Overflow, false); return cycles;
            case CLD: SetFlag(CpuFlags.Decimal, false); return cycles;
            case SED: SetFlag(CpuFlags.Decimal, true); return cycles;

            case DEX: X--; SetNZ(X); return cycles;
            case DEY: Y--; SetNZ(Y); return cycles;
            case INX: X++; SetNZ(X); return cycles;
            case INY: Y++; SetNZ(Y); return cycles;

            case TAX: X = A; SetNZ(X); return cycles;
            case TAY: Y = A; SetNZ(Y); return cycles;
            case TXA: A = X; SetNZ(A); return cycles;
            case TYA: A = Y; SetNZ(A); return cycles;
            case TSX: X = SP; SetNZ(X); return cycles;
            case TXS: SP = X; return cycles; // TXS 不更新 N/Z

            case PHA: Push(A); return cycles;
            case PHP: Push((byte)(P | (byte)CpuFlags.Break)); return cycles;
            case PLA: A = Pull(); SetNZ(A); return cycles;
            case PLP:
            {
                byte value = Pull();
                _status = (byte)((value & 0xCF) | (byte)CpuFlags.Unused);
                return cycles;
            }
        }

        // ---------- 累加器寻址的读-改-写（ASL A / ROL A ...） ----------
        if (info.Mode == AddressingMode.Accumulator)
        {
            A = info.Operation switch
            {
                ASL => Asl(A),
                LSR => Lsr(A),
                ROL => Rol(A),
                ROR => Ror(A),
                _ => throw new InvalidOperationException($"{info.Mnemonic} 不支持累加器寻址。"),
            };
            return cycles;
        }

        // ---------- 读操作 ----------
        if (IsReadOperation(info.Operation))
        {
            var (_, value, pageCrossed) = ReadOperand(info.Mode);
            if (pageCrossed && info.PageCrossPenalty)
                cycles++; // 6502 的读指令跨页时多一个“修正周期”

            ExecuteRead(info.Operation, value);
            return cycles;
        }

        // ---------- 写操作 ----------
        if (IsStoreOperation(info.Operation))
        {
            ushort address = ResolveAddress(info.Mode);
            ExecuteStore(info.Operation, address);
            return cycles;
        }

        // ---------- 读-改-写操作 ----------
        if (IsReadModifyWrite(info.Operation))
        {
            ushort address = ResolveAddress(info.Mode);
            byte value = Read(address);
            value = ExecuteReadModifyWrite(info.Operation, value);
            Write(address, value);
            return cycles;
        }

        throw new InvalidOperationException($"未实现的操作 {info.Mnemonic} / {info.Mode}。");
    }

    /// <summary>执行 JMP 的绝对/间接寻址。间接 JMP 保留了著名的 6502 页边界 bug。</summary>
    private int ExecuteJump(AddressingMode mode)
    {
        ushort target = FetchWord();

        if (mode != AddressingMode.Indirect)
        {
            PC = target;
            return 3;
        }

        // NMOS 6502 的 JMP ($xxFF) 缺点：
        // 高字节不是从 $xx+1:00 读取，而是仍从 $xx:00 读取，即只在同一页内回绕。
        // 这是硬件 bug，模拟器必须保留，否则依赖它的游戏/测试会失败。
        byte low = Read(target);
        ushort highAddress = (ushort)((target & 0xFF00) | ((target + 1) & 0x00FF));
        byte high = Read(highAddress);
        PC = (ushort)(low | (high << 8));
        return 5;
    }

    /// <summary>
    /// 分支指令：不跳 = 2 周期；跳 = 3；若目标与当前指令下一页不同，再加 1。
    /// </summary>
    private int Branch(bool condition)
    {
        sbyte offset = (sbyte)FetchByte();
        int cycles = 2;

        if (!condition)
            return cycles;

        cycles++; // 分支成立
        ushort target = (ushort)(PC + offset);
        if ((PC & 0xFF00) != (target & 0xFF00))
            cycles++; // 跨页修正

        PC = target;
        return cycles;
    }

    /// <summary>NOP 的多种非官方寻址形式：仍需完成一次哑读，周期数按表计算。</summary>
    private int ExecuteNop(in OpcodeInfo info)
    {
        if (info.Mode == AddressingMode.Implied)
            return info.BaseCycles;

        var (_, _, pageCrossed) = ReadOperand(info.Mode);
        return info.BaseCycles + (pageCrossed && info.PageCrossPenalty ? 1 : 0);
    }

    /// <summary>执行读操作对寄存器/标志的影响。</summary>
    private void ExecuteRead(CpuOperation operation, byte value)
    {
        switch (operation)
        {
            case LDA: A = value; SetNZ(A); break;
            case LDX: X = value; SetNZ(X); break;
            case LDY: Y = value; SetNZ(Y); break;

            case ORA: A |= value; SetNZ(A); break;
            case AND: A &= value; SetNZ(A); break;
            case EOR: A ^= value; SetNZ(A); break;

            case ADC: Adc(value); break;
            case SBC: Sbc(value); break;

            case CMP: Compare(A, value); break;
            case CPX: Compare(X, value); break;
            case CPY: Compare(Y, value); break;

            case BIT:
                SetFlag(CpuFlags.Zero, (A & value) == 0);
                SetFlag(CpuFlags.Negative, (value & 0x80) != 0);
                SetFlag(CpuFlags.Overflow, (value & 0x40) != 0);
                break;

            // ---- 非官方读指令 ----
            case LAX:
                A = X = value;
                SetNZ(A);
                break;

            case ANC:
                A &= value;
                SetNZ(A);
                SetFlag(CpuFlags.Carry, GetFlag(CpuFlags.Negative));
                break;

            case ALR:
                A &= value;
                A = Lsr(A);
                break;

            case ARR:
            {
                // ARR 的 V/C 计算是 6502 最古怪的细节之一，不同文档略有差异。
                // 这里采用广泛使用的行为：先 AND，再带进位右旋，C=bit6，V=bit6 xor bit5。
                A &= value;
                bool carryIn = GetFlag(CpuFlags.Carry);
                A = (byte)((A >> 1) | (carryIn ? 0x80 : 0x00));
                SetNZ(A);
                SetFlag(CpuFlags.Carry, (A & 0x40) != 0);
                SetFlag(CpuFlags.Overflow, ((A & 0x40) != 0) ^ ((A & 0x20) != 0));
                break;
            }

            case AXS:
            {
                // X = (A & X) - imm，不改变 A；C 表示无借位。
                int difference = (A & X) - value;
                SetFlag(CpuFlags.Carry, difference >= 0);
                X = (byte)difference;
                SetNZ(X);
                break;
            }

            case XAA:
                // 非官方且高度不稳定；常见芯片行为为 (A | magic) & X & imm。
                A = (byte)((A | 0xEE) & X & value);
                SetNZ(A);
                break;

            case LAS:
                // mem & SP，同时写入 A、X、SP。
                A = X = SP = (byte)(value & SP);
                SetNZ(A);
                break;

            default:
                throw new InvalidOperationException($"未实现的读操作 {operation}。");
        }
    }

    /// <summary>执行写操作。AHX/SHY/SHX/TAS 的行为在真实芯片上不稳定，此处采用常见近似。</summary>
    private void ExecuteStore(CpuOperation operation, ushort address)
    {
        byte highPlusOne = (byte)((address >> 8) + 1);

        switch (operation)
        {
            case STA: Write(address, A); break;
            case STX: Write(address, X); break;
            case STY: Write(address, Y); break;
            case SAX: Write(address, (byte)(A & X)); break;
            case AHX: Write(address, (byte)(A & X & highPlusOne)); break;
            case SHY: Write(address, (byte)(Y & highPlusOne)); break;
            case SHX: Write(address, (byte)(X & highPlusOne)); break;
            case TAS:
                SP = (byte)(A & X);
                Write(address, (byte)(SP & highPlusOne));
                break;
            default:
                throw new InvalidOperationException($"未实现的写操作 {operation}。");
        }
    }

    /// <summary>执行内存读-改-写，返回要写回的值。</summary>
    private byte ExecuteReadModifyWrite(CpuOperation operation, byte value)
    {
        switch (operation)
        {
            case ASL: return Asl(value);
            case LSR: return Lsr(value);
            case ROL: return Rol(value);
            case ROR: return Ror(value);
            case INC: value++; SetNZ(value); return value;
            case DEC: value--; SetNZ(value); return value;

            // ---- 非官方复合 RMW ----
            case SLO: // ASL mem; ORA A
            {
                byte result = Asl(value);
                A |= result;
                SetNZ(A);
                return result;
            }
            case RLA: // ROL mem; AND A
            {
                byte result = Rol(value);
                A &= result;
                SetNZ(A);
                return result;
            }
            case SRE: // LSR mem; EOR A
            {
                byte result = Lsr(value);
                A ^= result;
                SetNZ(A);
                return result;
            }
            case RRA: // ROR mem; ADC A
            {
                byte result = Ror(value);
                Adc(result);
                return result;
            }
            case DCP: // DEC mem; CMP A
            {
                byte result = (byte)(value - 1);
                SetNZ(result);
                Compare(A, result);
                return result;
            }
            case ISC: // INC mem; SBC A
            {
                byte result = (byte)(value + 1);
                SetNZ(result);
                Sbc(result);
                return result;
            }
            default:
                throw new InvalidOperationException($"未实现的读-改-写操作 {operation}。");
        }
    }

    // ==================== 算术与标志辅助 ====================

    /// <summary>
    /// 二进制 ADC。NES 的 2A03 不支持十进制模式，因此即使 D=1 也按二进制处理。
    /// V = (~(A ^ M) & (A ^ R) & 0x80) != 0
    /// </summary>
    private void Adc(byte value)
    {
        int carry = GetFlag(CpuFlags.Carry) ? 1 : 0;
        int sum = A + value + carry;
        bool overflow = ((~(A ^ value) & (A ^ sum) & 0x80) != 0);

        SetFlag(CpuFlags.Carry, sum > 0xFF);
        SetFlag(CpuFlags.Overflow, overflow);
        A = (byte)sum;
        SetNZ(A);
    }

    /// <summary>二进制 SBC：A - M - (1-C)，C=1 表示没有借位。</summary>
    private void Sbc(byte value)
    {
        int borrow = GetFlag(CpuFlags.Carry) ? 0 : 1;
        int difference = A - value - borrow;
        bool overflow = (((A ^ value) & (A ^ difference) & 0x80) != 0);

        SetFlag(CpuFlags.Carry, difference >= 0);
        SetFlag(CpuFlags.Overflow, overflow);
        A = (byte)difference;
        SetNZ(A);
    }

    /// <summary>比较：计算寄存器 - 值，C 表示寄存器 >= 值。</summary>
    private void Compare(byte register, byte value)
    {
        SetFlag(CpuFlags.Carry, register >= value);
        SetNZ((byte)(register - value));
    }

    /// <summary>算术左移：bit7 移入 C，低位补 0。</summary>
    private byte Asl(byte value)
    {
        SetFlag(CpuFlags.Carry, (value & 0x80) != 0);
        byte result = (byte)(value << 1);
        SetNZ(result);
        return result;
    }

    /// <summary>逻辑右移：bit0 移入 C，高位补 0。</summary>
    private byte Lsr(byte value)
    {
        SetFlag(CpuFlags.Carry, (value & 0x01) != 0);
        byte result = (byte)(value >> 1);
        SetNZ(result);
        return result;
    }

    /// <summary>带进位循环左移。</summary>
    private byte Rol(byte value)
    {
        bool carryIn = GetFlag(CpuFlags.Carry);
        SetFlag(CpuFlags.Carry, (value & 0x80) != 0);
        byte result = (byte)((value << 1) | (carryIn ? 1 : 0));
        SetNZ(result);
        return result;
    }

    /// <summary>带进位循环右移。</summary>
    private byte Ror(byte value)
    {
        bool carryIn = GetFlag(CpuFlags.Carry);
        SetFlag(CpuFlags.Carry, (value & 0x01) != 0);
        byte result = (byte)((value >> 1) | (carryIn ? 0x80 : 0x00));
        SetNZ(result);
        return result;
    }

    // ==================== 寻址 ====================

    /// <summary>读取操作数并返回 (有效地址, 值, 是否跨页)。不用于写指令。</summary>
    private (ushort Address, byte Value, bool PageCrossed) ReadOperand(AddressingMode mode)
    {
        switch (mode)
        {
            case AddressingMode.Immediate:
            {
                ushort address = PC++;
                return (address, Read(address), false);
            }
            case AddressingMode.ZeroPage:
            {
                byte address = FetchByte();
                return (address, Read(address), false);
            }
            case AddressingMode.ZeroPageX:
            {
                // 零页索引在 $00-$FF 内回绕，不会进位到 $0100。
                byte address = (byte)(FetchByte() + X);
                return (address, Read(address), false);
            }
            case AddressingMode.ZeroPageY:
            {
                byte address = (byte)(FetchByte() + Y);
                return (address, Read(address), false);
            }
            case AddressingMode.Absolute:
            {
                ushort address = FetchWord();
                return (address, Read(address), false);
            }
            case AddressingMode.AbsoluteX:
            {
                ushort baseAddress = FetchWord();
                ushort address = (ushort)(baseAddress + X);
                return (address, Read(address), (baseAddress & 0xFF00) != (address & 0xFF00));
            }
            case AddressingMode.AbsoluteY:
            {
                ushort baseAddress = FetchWord();
                ushort address = (ushort)(baseAddress + Y);
                return (address, Read(address), (baseAddress & 0xFF00) != (address & 0xFF00));
            }
            case AddressingMode.IndirectX:
            {
                byte pointer = (byte)(FetchByte() + X);
                ushort address = ReadPointer(pointer);
                return (address, Read(address), false);
            }
            case AddressingMode.IndirectY:
            {
                byte pointer = FetchByte();
                ushort baseAddress = ReadPointer(pointer);
                ushort address = (ushort)(baseAddress + Y);
                return (address, Read(address), (baseAddress & 0xFF00) != (address & 0xFF00));
            }
            default:
                throw new InvalidOperationException($"寻址方式 {mode} 不读取操作数。");
        }
    }

    /// <summary>解析写指令/读-改-写指令的目标地址，不读取该地址的内容。</summary>
    private ushort ResolveAddress(AddressingMode mode)
    {
        switch (mode)
        {
            case AddressingMode.ZeroPage:
                return FetchByte();
            case AddressingMode.ZeroPageX:
                return (byte)(FetchByte() + X);
            case AddressingMode.ZeroPageY:
                return (byte)(FetchByte() + Y);
            case AddressingMode.Absolute:
                return FetchWord();
            case AddressingMode.AbsoluteX:
                return (ushort)(FetchWord() + X);
            case AddressingMode.AbsoluteY:
                return (ushort)(FetchWord() + Y);
            case AddressingMode.IndirectX:
            {
                byte pointer = (byte)(FetchByte() + X);
                return ReadPointer(pointer);
            }
            case AddressingMode.IndirectY:
            {
                byte pointer = FetchByte();
                return (ushort)(ReadPointer(pointer) + Y);
            }
            default:
                throw new InvalidOperationException($"寻址方式 {mode} 不产生可写地址。");
        }
    }

    /// <summary>读取零页指针：低字节在 address，高字节在 (address+1) & 0xFF。</summary>
    private ushort ReadPointer(byte address)
    {
        byte lo = Read(address);
        byte hi = Read((byte)(address + 1)); // 零页内回绕
        return (ushort)(lo | (hi << 8));
    }

    /// <summary>从 PC 读取一个小端 16 位地址。</summary>
    private ushort FetchWord()
    {
        byte lo = FetchByte();
        byte hi = FetchByte();
        return (ushort)(lo | (hi << 8));
    }

    // ==================== 操作分类 ====================

    private static bool IsReadOperation(CpuOperation operation) => operation is
        ADC or AND or BIT or CMP or CPX or CPY or EOR or LDA or LDX or LDY or
        ORA or SBC or LAX or ANC or ALR or ARR or AXS or XAA or LAS;

    private static bool IsStoreOperation(CpuOperation operation) => operation is
        STA or STX or STY or SAX or AHX or SHY or SHX or TAS;

    private static bool IsReadModifyWrite(CpuOperation operation) => operation is
        ASL or LSR or ROL or ROR or INC or DEC or SLO or RLA or SRE or RRA or DCP or ISC;
}
