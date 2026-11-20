namespace NesEmulator.Core.Apu;

/// <summary>
/// 一个简单的单声道 16 位 PCM 环形缓冲区。
///
/// APU 在模拟线程上以 44100 Hz 生产样本，前端音频线程以相近速度消费。
/// 用环形缓冲区解耦两者：
///   - 写满时丢弃最旧样本，避免模拟器被音频卡住或延迟无限增长；
///   - 读空时返回 0，前端可以自行补静音。
///
/// 对学习项目来说，锁带来的开销远小于音频回调的周期；若追求极致性能，
/// 可以改成无锁的单生产者/单消费者队列。
/// </summary>
public sealed class AudioBuffer
{
    private readonly short[] _buffer;
    private readonly object _gate = new();
    private int _readIndex;
    private int _count;

    public AudioBuffer(int capacity = 16384)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _buffer = new short[capacity];
    }

    /// <summary>当前可读样本数。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    /// <summary>写入一个样本；写满时覆盖最旧的一个。</summary>
    public void Write(short sample)
    {
        lock (_gate)
        {
            if (_count == _buffer.Length)
            {
                _buffer[_readIndex] = sample;
                _readIndex = (_readIndex + 1) % _buffer.Length;
                return;
            }

            int writeIndex = (_readIndex + _count) % _buffer.Length;
            _buffer[writeIndex] = sample;
            _count++;
        }
    }

    /// <summary>批量写入。</summary>
    public void Write(ReadOnlySpan<short> samples)
    {
        foreach (short sample in samples)
            Write(sample);
    }

    /// <summary>读取最多 destination.Length 个样本，返回实际读取数。</summary>
    public int Read(Span<short> destination)
    {
        lock (_gate)
        {
            int toRead = Math.Min(destination.Length, _count);
            for (int i = 0; i < toRead; i++)
            {
                destination[i] = _buffer[_readIndex];
                _readIndex = (_readIndex + 1) % _buffer.Length;
            }

            _count -= toRead;
            return toRead;
        }
    }

    /// <summary>清空缓冲（复位/切换 ROM 时使用）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _readIndex = 0;
            _count = 0;
            Array.Clear(_buffer);
        }
    }
}
