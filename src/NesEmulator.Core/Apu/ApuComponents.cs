namespace NesEmulator.Core.Apu;

/// <summary>
/// APU 的长度计数器（length counter）。它决定一个音符在收到“停止”后还能响多久。
/// 半帧（每约 14913 CPU 周期）递减一次；减到 0 时声道静音。
/// </summary>
public sealed class LengthCounter
{
    /// <summary>32 个长度值，单位是半帧。数值来自 2A03 硬件表。</summary>
    private static readonly byte[] LengthTable =
    [
        10, 254, 20, 2, 40, 4, 80, 6,
        160, 8, 60, 10, 14, 12, 26, 14,
        12, 16, 24, 18, 48, 20, 96, 22,
        192, 24, 72, 26, 16, 28, 32, 30,
    ];

    /// <summary>当前剩余长度。</summary>
    public int Value { get; private set; }

    /// <summary>声道是否启用。$4015 对应位为 0 时长度计数器被清零。</summary>
    public bool Enabled { get; set; }

    /// <summary>halt 标志（通常与包络 loop 共用寄存器位）。为 true 时停止递减。</summary>
    public bool Halt { get; set; }

    public bool IsActive => Value > 0;

    /// <summary>由 $4003/$4007/$400B/$400F 的最高 5 位装载。</summary>
    public void Load(int index)
    {
        if (Enabled)
            Value = LengthTable[index & 0x1F];
    }

    /// <summary>半帧时钟：未 halt 时递减。</summary>
    public void Clock()
    {
        if (!Halt && Value > 0)
            Value--;
    }

    public void Clear() => Value = 0;
}

/// <summary>
/// APU 包络发生器。它让音量自动衰减，或输出固定音量。
/// $4000/$4004/$400C 的 bit5 是 loop，bit4 是 constant volume，bit3-0 是周期/音量。
/// </summary>
public sealed class Envelope
{
    private int _divider;
    private int _decayLevel;
    private bool _start;
    private bool _loop;
    private bool _constantVolume;
    private int _period;
    private int _volumeOrPeriod;

    /// <summary>当前输出音量 0-15。</summary>
    public int Output => _constantVolume ? _volumeOrPeriod : _decayLevel;

    /// <summary>配置包络寄存器（写入时不立即重新开始）。</summary>
    public void Configure(byte value)
    {
        _loop = (value & 0x20) != 0;
        _constantVolume = (value & 0x10) != 0;
        _period = value & 0x0F;
        _volumeOrPeriod = value & 0x0F;
    }

    /// <summary>下一次时钟从最大音量重新开始（$4003 等写长度寄存器时触发）。</summary>
    public void Start() => _start = true;

    /// <summary>四分之一帧时钟。</summary>
    public void Clock()
    {
        if (_start)
        {
            _start = false;
            _decayLevel = 15;
            _divider = _period;
            return;
        }

        if (_divider > 0)
        {
            _divider--;
            return;
        }

        _divider = _period;
        if (_decayLevel > 0)
        {
            _decayLevel--;
        }
        else if (_loop)
        {
            _decayLevel = 15;
        }
    }

    public void Reset()
    {
        _divider = 0;
        _decayLevel = 0;
        _start = false;
        _loop = false;
        _constantVolume = false;
        _period = 0;
        _volumeOrPeriod = 0;
    }
}
