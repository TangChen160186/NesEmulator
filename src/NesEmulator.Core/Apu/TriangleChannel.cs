namespace NesEmulator.Core.Apu;

/// <summary>
/// 三角波声道。NES 的三角波没有音量控制，只有开/关；它产生 32 级阶梯波，
/// 常用于低音和旋律。线性计数器（linear counter）负责控制音符释放，
/// 与长度计数器共同决定发声条件。
///
/// 寄存器：
///   $4008：bit7 控制/长度 halt，bit6-0 线性计数器重载值
///   $400A：定时器低 8 位
///   $400B：长度索引（bit7-3）+ 定时器高 3 位
/// </summary>
public sealed class TriangleChannel
{
    /// <summary>32 步三角波序列，15..0..15。</summary>
    private static readonly byte[] Waveform =
    [
        15, 14, 13, 12, 11, 10, 9, 8,
        7, 6, 5, 4, 3, 2, 1, 0,
        0, 1, 2, 3, 4, 5, 6, 7,
        8, 9, 10, 11, 12, 13, 14, 15,
    ];

    private int _timerPeriod;
    private int _timerCounter;
    private int _phase;
    private int _linearCounter;
    private int _linearReloadValue;
    private bool _linearReload;
    private bool _control;

    public LengthCounter Length { get; } = new();

    /// <summary>当前输出 0-15。</summary>
    public int Output => Length.IsActive && _linearCounter > 0 && _timerPeriod >= 2
        ? Waveform[_phase]
        : 0;

    /// <summary>每个 CPU 周期推进三角波定时器。</summary>
    public void StepTimer()
    {
        if (_timerCounter > 0)
        {
            _timerCounter--;
            return;
        }

        _timerCounter = _timerPeriod + 1;
        _phase = (_phase + 1) & 0x1F;
    }

    /// <summary>四分之一帧：线性计数器。</summary>
    public void ClockQuarter()
    {
        if (_linearReload)
        {
            _linearCounter = _linearReloadValue;
        }
        else if (_linearCounter > 0)
        {
            _linearCounter--;
        }

        if (!_control)
            _linearReload = false;
    }

    /// <summary>二分之一帧：长度计数器。</summary>
    public void ClockHalf() => Length.Clock();

    /// <summary>写 $4008-$400B。</summary>
    public void WriteRegister(int index, byte value)
    {
        switch (index)
        {
            case 0: // control / linear reload
                _control = (value & 0x80) != 0;
                Length.Halt = _control;
                _linearReloadValue = value & 0x7F;
                break;

            case 1: // timer low
                _timerPeriod = (_timerPeriod & 0x0700) | value;
                break;

            case 2: // length / timer high
                _timerPeriod = (_timerPeriod & 0x00FF) | ((value & 0x07) << 8);
                Length.Load(value >> 3);
                _linearReload = true;
                break;
        }
    }

    public void Reset()
    {
        _timerPeriod = 0;
        _timerCounter = 0;
        _phase = 0;
        _linearCounter = 0;
        _linearReloadValue = 0;
        _linearReload = false;
        _control = false;
        Length.Clear();
        Length.Halt = false;
    }
}
