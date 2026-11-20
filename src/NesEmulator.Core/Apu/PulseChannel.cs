namespace NesEmulator.Core.Apu;

/// <summary>
/// 方波声道（Pulse 1 / Pulse 2）。
///
/// 组成：
///   - 8 步占空比序列（12.5%/25%/50%/75% 四种音色）；
///   - 11 位定时器，决定音高；
///   - 长度计数器，决定音符时长；
///   - 包络，决定音量衰减；
///   - 扫频器，让音高自动滑音（也是《超级马里奥兄弟》金币音的来源）。
///
/// Pulse 1 与 Pulse 2 几乎相同，唯一区别是扫频取负时：
///   Pulse 1 使用反码（再减 1），Pulse 2 使用补码。
/// 这会让两者在滑音时产生细微但真实的差异。
/// </summary>
public sealed class PulseChannel
{
    private static readonly byte[][] DutySequences =
    [
        [0, 1, 0, 0, 0, 0, 0, 0], // 12.5%
        [0, 1, 1, 0, 0, 0, 0, 0], // 25%
        [0, 1, 1, 1, 1, 0, 0, 0], // 50%
        [1, 0, 0, 1, 1, 1, 1, 0], // 75%
    ];

    private readonly bool _isPulse1;
    private int _duty;
    private int _phase;
    private int _timerPeriod;
    private int _timerCounter;

    // 扫频器状态
    private bool _sweepEnabled;
    private bool _sweepNegate;
    private bool _sweepReload;
    private int _sweepPeriod;
    private int _sweepShift;
    private int _sweepDivider;

    public PulseChannel(bool isPulse1)
    {
        _isPulse1 = isPulse1;
    }

    /// <summary>长度计数器。</summary>
    public LengthCounter Length { get; } = new();

    /// <summary>包络发生器。</summary>
    public Envelope Envelope { get; } = new();

    /// <summary>当前输出 0-15。0 表示静音。</summary>
    public int Output
    {
        get
        {
            if (!Length.IsActive || _timerPeriod < 8 || IsSweepMuted)
                return 0;

            return DutySequences[_duty][_phase] == 0 ? 0 : Envelope.Output;
        }
    }

    /// <summary>由 APU 每个 CPU 周期调用，推进方波定时器。</summary>
    public void StepTimer()
    {
        if (_timerCounter > 0)
        {
            _timerCounter--;
            return;
        }

        // 方波定时器每 2 个 CPU 周期计一次，因此周期是 (timer+1)*2。
        _timerCounter = (_timerPeriod + 1) * 2;
        _phase = (_phase + 1) & 0x07;
    }

    /// <summary>四分之一帧：包络时钟。</summary>
    public void ClockQuarter() => Envelope.Clock();

    /// <summary>二分之一帧：长度计数器与扫频器。</summary>
    public void ClockHalf()
    {
        Length.Clock();

        if (_sweepDivider == 0)
        {
            int target = SweepTarget();
            if (_sweepEnabled && _sweepShift > 0 && target is >= 0 and <= 0x7FF)
                _timerPeriod = target;

            _sweepDivider = _sweepPeriod;
        }
        else
        {
            _sweepDivider--;
        }

        if (_sweepReload)
        {
            _sweepDivider = _sweepPeriod;
            _sweepReload = false;
        }
    }

    /// <summary>处理器访问 $4000-$4003（Pulse 2 是 $4004-$4007）。</summary>
    public void WriteRegister(int index, byte value)
    {
        switch (index)
        {
            case 0: // duty / envelope
                _duty = (value >> 6) & 0x03;
                Length.Halt = (value & 0x20) != 0;
                Envelope.Configure(value);
                break;

            case 1: // sweep
                _sweepEnabled = (value & 0x80) != 0;
                _sweepPeriod = (value >> 4) & 0x07;
                _sweepNegate = (value & 0x08) != 0;
                _sweepShift = value & 0x07;
                _sweepReload = true;
                break;

            case 2: // timer low
                _timerPeriod = (_timerPeriod & 0x0700) | value;
                break;

            case 3: // length / timer high
                _timerPeriod = (_timerPeriod & 0x00FF) | ((value & 0x07) << 8);
                Length.Load(value >> 3);
                Envelope.Start();
                _phase = 0; // 写高字节会重置占空比相位
                break;
        }
    }

    /// <summary>扫频目标频率；超出 11 位范围时静音。</summary>
    private int SweepTarget()
    {
        int change = _timerPeriod >> _sweepShift;
        return _sweepNegate
            ? _timerPeriod - change - (_isPulse1 ? 1 : 0)
            : _timerPeriod + change;
    }

    private bool IsSweepMuted =>
        _timerPeriod < 8 ||
        (_sweepShift > 0 && SweepTarget() > 0x7FF) ||
        SweepTarget() < 0;

    public void Reset()
    {
        _duty = 0;
        _phase = 0;
        _timerPeriod = 0;
        _timerCounter = 0;
        _sweepEnabled = false;
        _sweepNegate = false;
        _sweepReload = false;
        _sweepPeriod = 0;
        _sweepShift = 0;
        _sweepDivider = 0;
        Length.Clear();
        Length.Halt = false;
        Envelope.Reset();
    }
}
