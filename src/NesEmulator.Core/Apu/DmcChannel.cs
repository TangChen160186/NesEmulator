namespace NesEmulator.Core.Apu;

/// <summary>
/// DMC（Delta Modulation Channel）采样声道。
///
/// DMC 是 NES 上唯一能播放 PCM 采样的通道：它从 CPU 地址空间（$C000-$FFFF）
/// 读取 1 位 delta 编码样本，累加成一个 7 位 DAC 电平。
/// 因为会自己读内存，DMC 也是 APU 中唯一需要“CPU 总线回调”的声道。
///
/// 寄存器：
///   $4010：bit7 IRQ 使能，bit6 循环，bit3-0 采样率索引
///   $4011：7 位 DAC 直接加载值
///   $4012：采样起始地址 = $C000 + value*64
///   $4013：采样长度 = value*16 + 1 字节
///
/// 本实现不模拟 DMC 抢占总线造成的 CPU 停顿（真实硬件会暂停 CPU 若干周期），
/// 这对音乐播放影响很小，但属于已知的不精确点。
/// </summary>
public sealed class DmcChannel
{
    /// <summary>NTSC DMC 采样率表，单位为 CPU 周期。</summary>
    private static readonly int[] RateTable =
    [
        428, 380, 340, 320, 286, 254, 226, 214,
        190, 160, 142, 128, 106, 84, 72, 54,
    ];

    private readonly Func<ushort, byte> _readMemory;

    private bool _irqEnabled;
    private bool _loop;
    private int _rateIndex;
    private int _timerCounter;

    private int _sampleAddress = 0xC000;
    private int _sampleLength = 1;
    private int _currentAddress = 0xC000;
    private int _bytesRemaining;

    private byte _sampleBuffer;
    private bool _sampleBufferEmpty = true;
    private byte _shiftRegister;
    private int _bitsRemaining;
    private int _outputLevel;
    private bool _irqPending;

    public DmcChannel(Func<ushort, byte> readMemory)
    {
        _readMemory = readMemory ?? throw new ArgumentNullException(nameof(readMemory));
    }

    /// <summary>DMC 是否向 CPU 请求 IRQ。</summary>
    public bool IrqPending => _irqPending;

    /// <summary>剩余字节数；$4015 的 bit4 会报告它是否非零。</summary>
    public int BytesRemaining => _bytesRemaining;

    /// <summary>当前 7 位 DAC 输出 0-127。</summary>
    public int Output => _outputLevel;

    /// <summary>每个 CPU 周期推进 DMC 采样器。</summary>
    public void StepTimer()
    {
        if (_timerCounter > 0)
        {
            _timerCounter--;
            return;
        }

        _timerCounter = RateTable[_rateIndex];

        // 一个字节 8 位，逐位推进 DAC。
        if (_bitsRemaining == 0 && !_sampleBufferEmpty)
        {
            _shiftRegister = _sampleBuffer;
            _sampleBufferEmpty = true;
            _bitsRemaining = 8;
        }

        if (_bitsRemaining == 0 && _sampleBufferEmpty)
            FetchSample();

        if (_bitsRemaining > 0)
        {
            int bit = _shiftRegister & 1;
            _shiftRegister >>= 1;
            _bitsRemaining--;

            // 1 让 DAC 电平 +2，0 让电平 -2，并限制在 0-127。
            _outputLevel = bit != 0
                ? Math.Min(127, _outputLevel + 2)
                : Math.Max(0, _outputLevel - 2);
        }
    }

    private void FetchSample()
    {
        if (_bytesRemaining == 0)
            return;

        int address = _currentAddress;
        _sampleBuffer = _readMemory((ushort)address);

        // DMC 的地址在 $C000-$FFFF 内回绕。
        _currentAddress = (address + 1) & 0xFFFF;
        if (_currentAddress < 0x8000)
            _currentAddress = 0x8000;

        _sampleBufferEmpty = false;
        _bytesRemaining--;

        if (_bytesRemaining != 0)
            return;

        if (_loop)
        {
            _currentAddress = _sampleAddress;
            _bytesRemaining = _sampleLength;
        }
        else if (_irqEnabled)
        {
            _irqPending = true;
        }
    }

    /// <summary>写 $4010/$4011/$4012/$4013。</summary>
    public void WriteRegister(int index, byte value)
    {
        switch (index)
        {
            case 0:
                _irqEnabled = (value & 0x80) != 0;
                _loop = (value & 0x40) != 0;
                _rateIndex = value & 0x0F;
                if (!_irqEnabled)
                    _irqPending = false;
                break;

            case 1:
                _outputLevel = value & 0x7F;
                break;

            case 2:
                _sampleAddress = 0xC000 + value * 64;
                break;

            case 3:
                _sampleLength = value * 16 + 1;
                break;
        }
    }

    /// <summary>$4015 的 bit4 控制 DMC 启停。</summary>
    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            _bytesRemaining = 0;
            return;
        }

        if (_bytesRemaining == 0)
        {
            _currentAddress = _sampleAddress;
            _bytesRemaining = _sampleLength;
        }
    }

    /// <summary>读取 $4015 后清除 DMC IRQ。</summary>
    public void AcknowledgeIrq() => _irqPending = false;

    public void Reset()
    {
        _irqEnabled = false;
        _loop = false;
        _rateIndex = 0;
        _timerCounter = 0;
        _sampleAddress = 0xC000;
        _sampleLength = 1;
        _currentAddress = 0xC000;
        _bytesRemaining = 0;
        _sampleBuffer = 0;
        _sampleBufferEmpty = true;
        _shiftRegister = 0;
        _bitsRemaining = 0;
        _outputLevel = 0;
        _irqPending = false;
    }
}
