using System.Runtime.InteropServices;
using NesEmulator.Core.Apu;

namespace NesEmulator.Desktop;

/// <summary>
/// 用 Windows 自带的 winmm.dll / waveOut API 播放 APU 的 16 位单声道 PCM。
///
/// 为什么不用 NAudio 等库？
///   学习项目希望零第三方依赖。waveOut 是 Windows 上最古老、最直接的
///   音频输出接口，正好适合演示“环形缓冲 + 多缓冲排队”的基本原理。
///
/// 工作方式：
///   - 分配 4 个 1024 样本的缓冲（约 93 ms 总延迟）；
///   - 音频线程不断找已经播放完（WHDR_DONE）的缓冲，从 APU 环形缓冲读样本；
///   - 不足部分补 0（欠载时输出静音），然后重新 waveOutWrite 排队。
/// </summary>
public sealed class WaveOutAudio : IDisposable
{
    private const int BufferCount = 4;
    private const int SamplesPerBuffer = 1024;

    private const uint WHDR_DONE = 0x00000001;
    private const uint WHDR_PREPARED = 0x00000002;
    private const int WAVE_MAPPER = -1;
    private const int CALLBACK_NULL = 0;
    private const short WAVE_FORMAT_PCM = 1;

    private readonly AudioBuffer _source;
    private readonly short[] _scratch = new short[SamplesPerBuffer];
    private readonly bool[] _everQueued = new bool[BufferCount];

    private IntPtr _waveOut;
    private IntPtr _headers;
    private IntPtr[] _buffers = [];
    private Thread? _audioThread;
    private volatile bool _running;
    private bool _disposed;

    public WaveOutAudio(AudioBuffer source)
    {
        _source = source;

        try
        {
            OpenDevice();
            _running = true;
            _audioThread = new Thread(AudioLoop)
            {
                IsBackground = true,
                Name = "NES Audio (waveOut)",
            };
            _audioThread.Start();
        }
        catch
        {
            CloseDevice();
            throw;
        }
    }

    private void OpenDevice()
    {
        var format = new WAVEFORMATEX
        {
            wFormatTag = WAVE_FORMAT_PCM,
            nChannels = 1,
            nSamplesPerSec = Apu2A03.SampleRate,
            wBitsPerSample = 16,
            nBlockAlign = 2,
            nAvgBytesPerSec = Apu2A03.SampleRate * 2,
            cbSize = 0,
        };

        int result = waveOutOpen(out _waveOut, WAVE_MAPPER, ref format, IntPtr.Zero, IntPtr.Zero, CALLBACK_NULL);
        if (result != 0)
            throw new InvalidOperationException($"无法打开音频设备（waveOutOpen 错误码 {result}）。");

        int headerSize = Marshal.SizeOf<WAVEHDR>();
        _headers = Marshal.AllocHGlobal(headerSize * BufferCount);
        _buffers = new IntPtr[BufferCount];

        for (int i = 0; i < BufferCount; i++)
        {
            IntPtr data = Marshal.AllocHGlobal(SamplesPerBuffer * sizeof(short));
            _buffers[i] = data;

            var header = new WAVEHDR
            {
                lpData = data,
                dwBufferLength = SamplesPerBuffer * sizeof(short),
            };

            IntPtr headerPtr = HeaderPointer(i);
            Marshal.StructureToPtr(header, headerPtr, false);

            result = waveOutPrepareHeader(_waveOut, headerPtr, headerSize);
            if (result != 0)
                throw new InvalidOperationException($"waveOutPrepareHeader 失败（错误码 {result}）。");
        }
    }

    private void AudioLoop()
    {
        while (_running)
        {
            bool wroteAny = false;

            for (int i = 0; i < BufferCount && _running; i++)
            {
                IntPtr headerPtr = HeaderPointer(i);
                WAVEHDR header = Marshal.PtrToStructure<WAVEHDR>(headerPtr);

                bool free = !_everQueued[i] || (header.dwFlags & WHDR_DONE) != 0;
                if (!free)
                    continue;

                // 读取 APU 样本；不够的部分补静音，避免爆音。
                Array.Clear(_scratch);
                _source.Read(_scratch);
                Marshal.Copy(_scratch, 0, _buffers[i], SamplesPerBuffer);

                int result = waveOutWrite(_waveOut, headerPtr, Marshal.SizeOf<WAVEHDR>());
                if (result == 0)
                {
                    _everQueued[i] = true;
                    wroteAny = true;
                }
            }

            Thread.Sleep(wroteAny ? 2 : 5);
        }
    }

    private IntPtr HeaderPointer(int index) =>
        IntPtr.Add(_headers, index * Marshal.SizeOf<WAVEHDR>());

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _running = false;

        if (_audioThread is not null)
        {
            _audioThread.Join(500);
            _audioThread = null;
        }

        CloseDevice();
    }

    private void CloseDevice()
    {
        if (_waveOut != IntPtr.Zero)
        {
            waveOutReset(_waveOut);

            if (_headers != IntPtr.Zero)
            {
                int headerSize = Marshal.SizeOf<WAVEHDR>();
                for (int i = 0; i < BufferCount; i++)
                {
                    IntPtr headerPtr = HeaderPointer(i);
                    waveOutUnprepareHeader(_waveOut, headerPtr, headerSize);
                }
            }

            waveOutClose(_waveOut);
            _waveOut = IntPtr.Zero;
        }

        if (_headers != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_headers);
            _headers = IntPtr.Zero;
        }

        foreach (IntPtr buffer in _buffers)
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
        }

        _buffers = [];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public short wFormatTag;
        public short nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public short nBlockAlign;
        public short wBitsPerSample;
        public short cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public int dwBufferLength;
        public int dwBytesRecorded;
        public IntPtr dwUser;
        public int dwFlags;
        public int dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutOpen(
        out IntPtr hWaveOut,
        int uDeviceID,
        ref WAVEFORMATEX lpFormat,
        IntPtr dwCallback,
        IntPtr dwInstance,
        int dwFlags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr hWaveOut);
}
