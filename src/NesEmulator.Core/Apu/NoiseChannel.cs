namespace NesEmulator.Core.Apu;

/// <summary>
/// 噪声声道。使用一个 15 位线性反馈移位寄存器（LFSR）产生伪随机方波，
/// 适合鼓、爆炸和风声。
///
/// 寄存器：
///   $400C：长度 halt / 包络配置
///   $400E：bit7 模式（短/长 LFSR），bit3-0 周期索引
///   $400F：长度索引 + 包络重启
/// </summary>
public sealed class NoiseChannel
{
    /// <summary>NTSC 噪声周期表，单位为 CPU 周期。</summary>
    private static readonly int[] PeriodTable =
    [
        4, 8, 16, 32, 64, 96, 128, 160,
        202, 254, 380, 508, 762, 1016, 2034, 4068,
    ];

    private int _periodIndex;
    private bool _mode;
    private int _timerCounter;
    private ushort _shiftRegister = 1;

    public LengthCounter Length { get; } = new();
    public Envelope Envelope { get; } = new();

    /// <summary>当前输出 0-15。LFSR 的 bit0 为 0 时发声。</summary>
    public int Output => Length.IsActive && (_shiftRegister & 0x01) == 0
        ? Envelope.Output
        : 0;

    /// <summary>每个 CPU 周期推进噪声定时器。</summary>
    public void StepTimer()
    {
        if (_timerCounter > 0)
        {
            _timerCounter--;
            return;
        }

        _timerCounter = PeriodTable[_periodIndex];
        StepShiftRegister();
    }

    /// <summary>四分之一帧：包络。</summary>
    public void ClockQuarter() => Envelope.Clock();

    /// <summary>二分之一帧：长度。</summary>
    public void ClockHalf() => Length.Clock();

    /// <summary>写 $400C/$400E/$400F。</summary>
    public void WriteRegister(int index, byte value)
    {
        switch (index)
        {
            case 0:
                Length.Halt = (value & 0x20) != 0;
                Envelope.Configure(value);
                break;

            case 2:
                _mode = (value & 0x80) != 0;
                _periodIndex = value & 0x0F;
                break;

            case 3:
                Length.Load(value >> 3);
                Envelope.Start();
                break;
        }
    }

    /// <summary>
    /// 15 位 LFSR：反馈位 = bit0 xor (mode ? bit6 : bit1)。
    /// 然后整体右移，反馈放回 bit14。
    /// </summary>
    private void StepShiftRegister()
    {
        int feedback = (_shiftRegister & 1) ^ ((_shiftRegister >> (_mode ? 6 : 1)) & 1);
        _shiftRegister = (ushort)((_shiftRegister >> 1) | (feedback << 14));
    }

    public void Reset()
    {
        _periodIndex = 0;
        _mode = false;
        _timerCounter = 0;
        _shiftRegister = 1;
        Length.Clear();
        Length.Halt = false;
        Envelope.Reset();
    }
}
