namespace NesEmulator.Core.Apu;

/// <summary>
/// NES 的音频处理单元（APU），集成在 Ricoh 2A03 CPU 芯片内部。
///
/// 五个声道：
///   - Pulse 1 / Pulse 2：方波，负责旋律、和声、音效；
///   - Triangle：三角波，负责低音；
///   - Noise：噪声，负责鼓和爆炸；
///   - DMC：1 位 delta 采样，播放 PCM。
///
/// 两个时钟域需要理解：
///   1. CPU 时钟：1.789773 MHz（NTSC），声道定时器按 CPU 周期递减；
///   2. 帧序列器：约 240 Hz，分成 quarter/half 帧，统一推进包络、长度、
///      扫频和线性计数器。$4017 可以选择 4 步或 5 步模式。
///
/// 音频输出以 44100 Hz 单声道 16 位 PCM 写入 <see cref="Audio"/> 环形缓冲。
/// 混音采用 NESdev 推荐的线性近似公式，而不是精确的非线性 DAC 模型；
/// 对学习和大多数游戏试听已经足够。
/// </summary>
public sealed class Apu2A03
{
    /// <summary>NTSC CPU 时钟频率。</summary>
    public const double NtscCpuClock = 1_789_773.0;

    /// <summary>输出采样率。</summary>
    public const int SampleRate = 44_100;

    /// <summary>4 步帧序列器的周期边界（CPU 周期）。</summary>
    private const int FrameQuarter1 = 7457;
    private const int FrameHalf1 = 14913;
    private const int FrameQuarter2 = 22371;
    private const int FrameEnd4 = 29829;
    private const int FrameEnd5 = 37281;

    private readonly DmcChannel _dmc;
    private int _frameCycle;
    private bool _fiveStepMode;
    private bool _irqInhibit;
    private bool _frameIrq;
    private double _sampleAccumulator;
    private double _dcPreviousInput;
    private double _dcPreviousOutput;

    public Apu2A03(Func<ushort, byte> dmcReadMemory)
    {
        _dmc = new DmcChannel(dmcReadMemory);
        Pulse1 = new PulseChannel(isPulse1: true);
        Pulse2 = new PulseChannel(isPulse1: false);
        Triangle = new TriangleChannel();
        Noise = new NoiseChannel();
        Reset();
    }

    public PulseChannel Pulse1 { get; }
    public PulseChannel Pulse2 { get; }
    public TriangleChannel Triangle { get; }
    public NoiseChannel Noise { get; }

    /// <summary>APU 输出的 44.1 kHz 样本缓冲，由前端音频线程消费。</summary>
    public AudioBuffer Audio { get; } = new(16384);

    /// <summary>是否有 APU 中断请求（帧 IRQ 或 DMC IRQ）。</summary>
    public bool IrqPending => _frameIrq || _dmc.IrqPending;

    /// <summary>复位 APU：关闭所有声道，清空帧序列器与音频缓冲。</summary>
    public void Reset()
    {
        Pulse1.Reset();
        Pulse2.Reset();
        Triangle.Reset();
        Noise.Reset();
        _dmc.Reset();

        Pulse1.Length.Enabled = false;
        Pulse2.Length.Enabled = false;
        Triangle.Length.Enabled = false;
        Noise.Length.Enabled = false;

        _frameCycle = 0;
        _fiveStepMode = false;
        _irqInhibit = false;
        _frameIrq = false;
        _sampleAccumulator = 0;
        _dcPreviousInput = 0;
        _dcPreviousOutput = 0;
        Audio.Clear();
    }

    /// <summary>CPU 读取 $4015 的 APU 状态，并清除帧 IRQ/DMC IRQ。</summary>
    public byte ReadStatus()
    {
        byte status = 0;

        if (Pulse1.Length.IsActive) status |= 0x01;
        if (Pulse2.Length.IsActive) status |= 0x02;
        if (Triangle.Length.IsActive) status |= 0x04;
        if (Noise.Length.IsActive) status |= 0x08;
        if (_dmc.BytesRemaining > 0) status |= 0x10;
        if (_frameIrq) status |= 0x40;
        if (_dmc.IrqPending) status |= 0x80;

        // 读取状态寄存器会应答并清除两个 IRQ 标志。
        _frameIrq = false;
        _dmc.AcknowledgeIrq();
        return status;
    }

    /// <summary>CPU 写 $4000-$4013、$4015、$4017。其他 APU 地址忽略。</summary>
    public void WriteRegister(ushort address, byte value)
    {
        switch (address)
        {
            case >= 0x4000 and <= 0x4003:
                Pulse1.WriteRegister(address - 0x4000, value);
                break;
            case >= 0x4004 and <= 0x4007:
                Pulse2.WriteRegister(address - 0x4004, value);
                break;
            case >= 0x4008 and <= 0x400B:
                Triangle.WriteRegister(address - 0x4008, value);
                break;
            case >= 0x400C and <= 0x400F:
                Noise.WriteRegister(address - 0x400C, value);
                break;
            case >= 0x4010 and <= 0x4013:
                _dmc.WriteRegister(address - 0x4010, value);
                break;

            case 0x4015:
                WriteStatus(value);
                break;

            case 0x4017:
                WriteFrameCounter(value);
                break;
        }
    }

    /// <summary>$4015 写：分别启用/禁用声道，禁用时清空长度计数器。</summary>
    private void WriteStatus(byte value)
    {
        Pulse1.Length.Enabled = (value & 0x01) != 0;
        Pulse2.Length.Enabled = (value & 0x02) != 0;
        Triangle.Length.Enabled = (value & 0x04) != 0;
        Noise.Length.Enabled = (value & 0x08) != 0;
        _dmc.SetEnabled((value & 0x10) != 0);

        if ((value & 0x01) == 0) Pulse1.Length.Clear();
        if ((value & 0x02) == 0) Pulse2.Length.Clear();
        if ((value & 0x04) == 0) Triangle.Length.Clear();
        if ((value & 0x08) == 0) Noise.Length.Clear();
    }

    /// <summary>$4017 写：选择 4/5 步帧序列器、抑制 IRQ、复位计数器。</summary>
    private void WriteFrameCounter(byte value)
    {
        _fiveStepMode = (value & 0x80) != 0;
        _irqInhibit = (value & 0x40) != 0;
        if (_irqInhibit)
            _frameIrq = false;

        _frameCycle = 0;

        // 5 步模式写入后，硬件会立即时钟一次 quarter/half 帧。
        // 这是常见的近似；精确时序还有 3-4 个 CPU 周期的延迟。
        if (_fiveStepMode)
        {
            ClockQuarterFrame();
            ClockHalfFrame();
        }
    }

    /// <summary>
    /// 推进 APU 若干 CPU 周期。每个周期：
    ///   1. 推进五个声道的定时器；
    ///   2. 推进帧序列器；
    ///   3. 按采样率抽取一个混音样本。
    /// </summary>
    public void Step(int cpuCycles)
    {
        for (int i = 0; i < cpuCycles; i++)
        {
            Pulse1.StepTimer();
            Pulse2.StepTimer();
            Triangle.StepTimer();
            Noise.StepTimer();
            _dmc.StepTimer();

            StepFrameSequencer();

            _sampleAccumulator += SampleRate / NtscCpuClock;
            if (_sampleAccumulator >= 1.0)
            {
                _sampleAccumulator -= 1.0;
                WriteAudioSample();
            }
        }
    }

    private void StepFrameSequencer()
    {
        _frameCycle++;

        if (_fiveStepMode)
        {
            switch (_frameCycle)
            {
                case FrameQuarter1:
                    ClockQuarterFrame();
                    break;
                case FrameHalf1:
                    ClockQuarterFrame();
                    ClockHalfFrame();
                    break;
                case FrameQuarter2:
                    ClockQuarterFrame();
                    break;
                case FrameEnd5:
                    ClockQuarterFrame();
                    ClockHalfFrame();
                    _frameCycle = 0;
                    break;
            }
        }
        else
        {
            switch (_frameCycle)
            {
                case FrameQuarter1:
                    ClockQuarterFrame();
                    break;
                case FrameHalf1:
                    ClockQuarterFrame();
                    ClockHalfFrame();
                    break;
                case FrameQuarter2:
                    ClockQuarterFrame();
                    break;
                case FrameEnd4:
                    ClockQuarterFrame();
                    ClockHalfFrame();
                    if (!_irqInhibit)
                        _frameIrq = true;
                    break;
                case FrameEnd4 + 1:
                    _frameCycle = 0;
                    break;
            }
        }
    }

    private void ClockQuarterFrame()
    {
        Pulse1.ClockQuarter();
        Pulse2.ClockQuarter();
        Triangle.ClockQuarter();
        Noise.ClockQuarter();
    }

    private void ClockHalfFrame()
    {
        Pulse1.ClockHalf();
        Pulse2.ClockHalf();
        Triangle.ClockHalf();
        Noise.ClockHalf();
    }

    /// <summary>
    /// 五个声道混合。NES 的 DAC 是非线性的，但线性近似公式在 0-1 范围内
    /// 已经比较接近听感：
    ///   pulse = 0.00752 * (P1 + P2)
    ///   tnd   = 0.00851*T + 0.00494*N + 0.00335*D
    /// </summary>
    private void WriteAudioSample()
    {
        double pulse = 0.00752 * (Pulse1.Output + Pulse2.Output);
        double tnd = 0.00851 * Triangle.Output
                     + 0.00494 * Noise.Output
                     + 0.00335 * _dmc.Output;

        double mixed = Math.Clamp(pulse + tnd, 0.0, 1.5);

        // 简单一阶高通，去掉混音中的直流偏置，避免音频设备产生持续偏移。
        double filtered = mixed - _dcPreviousInput + 0.995 * _dcPreviousOutput;
        _dcPreviousInput = mixed;
        _dcPreviousOutput = filtered;

        double scaled = Math.Clamp(filtered * 0.8, -1.0, 1.0);
        short sample = (short)(scaled * short.MaxValue);
        Audio.Write(sample);
    }
}
