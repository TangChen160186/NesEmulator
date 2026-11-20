namespace HelloNes.Builder;

/// <summary>
/// 一个只实现演示 ROM 所需指令的迷你 6502 汇编器。
///
/// 它存在的目的不是取代 ca65/asm6，而是让示例 ROM 的源码保持可读：
/// 分支、绝对地址和数据表都通过标签引用，不需要手工计算偏移。
/// </summary>
internal sealed class MiniAssembler
{
    /// <summary>PRG 在 CPU 地址空间的装载地址。16 KB NROM 同时镜像到 $8000 和 $C000。</summary>
    public const ushort Origin = 0xC000;

    private readonly List<byte> _code = [];
    private readonly Dictionary<string, ushort> _labels = new(StringComparer.Ordinal);
    private readonly List<(int OperandOffset, string Label)> _absoluteFixups = [];
    private readonly List<(int OperandOffset, string Label)> _branchFixups = [];

    public int Length => _code.Count;

    /// <summary>定义一个标签，地址为当前 PC。</summary>
    public void Label(string name)
    {
        if (!_labels.TryAdd(name, CurrentPc))
            throw new InvalidOperationException($"标签重复：{name}");
    }

    private ushort CurrentPc => (ushort)(Origin + _code.Count);

    private void Emit(byte value) => _code.Add(value);

    private void Emit(byte opcode, byte operand)
    {
        _code.Add(opcode);
        _code.Add(operand);
    }

    private int EmitAbsolute(byte opcode, string label)
    {
        _code.Add(opcode);
        int offset = _code.Count;
        _code.Add(0);
        _code.Add(0);
        _absoluteFixups.Add((offset, label));
        return offset;
    }

    private void EmitAbsolute(byte opcode, ushort address)
    {
        _code.Add(opcode);
        _code.Add((byte)(address & 0xFF));
        _code.Add((byte)(address >> 8));
    }

    private void EmitBranch(byte opcode, string label)
    {
        _code.Add(opcode);
        int operandOffset = _code.Count;
        _code.Add(0);
        _branchFixups.Add((operandOffset, label));
    }

    // ---- 指令助记符 ----
    public void Sei() => Emit(0x78);
    public void Cld() => Emit(0xD8);
    public void Rti() => Emit(0x40);
    public void Pha() => Emit(0x48);
    public void Pla() => Emit(0x68);
    public void Inx() => Emit(0xE8);
    public void Iny() => Emit(0xC8);
    public void Dex() => Emit(0xCA);
    public void Dey() => Emit(0x88);
    public void Txs() => Emit(0x9A);
    public void Tax() => Emit(0xAA);

    public void LdaImm(byte value) => Emit(0xA9, value);
    public void LdxImm(byte value) => Emit(0xA2, value);
    public void LdyImm(byte value) => Emit(0xA0, value);
    public void CpxImm(byte value) => Emit(0xE0, value);
    public void CpyImm(byte value) => Emit(0xC0, value);
    public void AndImm(byte value) => Emit(0x29, value);

    public void LdaZp(byte address) => Emit(0xA5, address);
    public void IncZp(byte address) => Emit(0xE6, address);

    public void StaAbs(ushort address) => EmitAbsolute(0x8D, address);
    public void LdaAbs(ushort address) => EmitAbsolute(0xAD, address);
    public void BitAbs(ushort address) => EmitAbsolute(0x2C, address);
    public void IncAbs(ushort address) => EmitAbsolute(0xEE, address);
    public void Jmp(ushort address) => EmitAbsolute(0x4C, address);
    public void Jsr(ushort address) => EmitAbsolute(0x20, address);

    public void LdaAbs(string label) => EmitAbsolute(0xAD, label);
    public void LdaAbsX(string label) => EmitAbsolute(0xBD, label);
    public void StaAbsX(ushort address) => EmitAbsolute(0x9D, address);
    public void Jmp(string label) => EmitAbsolute(0x4C, label);
    public void Jsr(string label) => EmitAbsolute(0x20, label);

    public void Bpl(string label) => EmitBranch(0x10, label);
    public void Bmi(string label) => EmitBranch(0x30, label);
    public void Bvc(string label) => EmitBranch(0x50, label);
    public void Bvs(string label) => EmitBranch(0x70, label);
    public void Bcc(string label) => EmitBranch(0x90, label);
    public void Bcs(string label) => EmitBranch(0xB0, label);
    public void Bne(string label) => EmitBranch(0xD0, label);
    public void Beq(string label) => EmitBranch(0xF0, label);

    /// <summary>填充到相对于 Origin 的指定偏移。用于把中断向量放到 $FFFA。</summary>
    public void PadTo(int offset)
    {
        if (offset < _code.Count)
            throw new InvalidOperationException($"代码/数据已越过偏移 {offset:X4}。");

        while (_code.Count < offset)
            _code.Add(0);
    }

    /// <summary>写入一个标签的 16 位地址（小端），用于中断向量。</summary>
    public void WordLabel(string label)
    {
        int offset = _code.Count;
        _code.Add(0);
        _code.Add(0);
        _absoluteFixups.Add((offset, label));
    }

    public void Bytes(params byte[] values) => _code.AddRange(values);

    /// <summary>解析所有标签并返回最终机器码。</summary>
    public byte[] Build()
    {
        foreach ((int operandOffset, string label) in _absoluteFixups)
        {
            if (!_labels.TryGetValue(label, out ushort address))
                throw new InvalidOperationException($"未定义标签：{label}");
            _code[operandOffset] = (byte)(address & 0xFF);
            _code[operandOffset + 1] = (byte)(address >> 8);
        }

        foreach ((int operandOffset, string label) in _branchFixups)
        {
            if (!_labels.TryGetValue(label, out ushort target))
                throw new InvalidOperationException($"未定义标签：{label}");

            int nextInstruction = Origin + operandOffset + 1;
            int delta = target - nextInstruction;
            if (delta is < -128 or > 127)
                throw new InvalidOperationException($"分支 {label} 超出 ±128 字节范围：{delta}");

            _code[operandOffset] = (byte)(sbyte)delta;
        }

        return [.. _code];
    }
}
