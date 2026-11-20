namespace NesEmulator.Core.Input;

/// <summary>标准 NES 手柄的 8 个按键。枚举值就是 $4016 串行移位输出的位序号。</summary>
[Flags]
public enum ControllerButton : byte
{
    None = 0,
    A = 1 << 0,
    B = 1 << 1,
    Select = 1 << 2,
    Start = 1 << 3,
    Up = 1 << 4,
    Down = 1 << 5,
    Left = 1 << 6,
    Right = 1 << 7,
}

/// <summary>
/// 标准 NES 手柄（2A03 内置并行口 + CD4021 移位寄存器）。
///
/// CPU 通过两个寄存器访问：
///   $4016 写：bit 0 是“选通/锁存”信号（strobe）
///   $4016 读：玩家 1 的串行数据 bit 0
///   $4017 读：玩家 2 的串行数据 bit 0
///
/// 游戏通常这样读取：
///   写 $4016 = 1   （锁存当前所有按键）
///   写 $4016 = 0   （开始移位）
///   连续读 8 次 $4016，依次得到 A、B、Select、Start、Up、Down、Left、Right。
///
/// 这个类把“主机硬件时序”与“前端键盘状态”分开：
/// 前端随时调用 SetButton 设置当前按住的键，模拟器在选通时拍快照。
/// </summary>
public sealed class Controller
{
    private int _buttons;

    /// <summary>当前串行移位寄存器中的值。</summary>
    private byte _shiftRegister;

    /// <summary>选通信号是否保持高电平。</summary>
    private bool _strobe;

    /// <summary>设置一个按键的按下/松开状态，可由输入线程安全调用。</summary>
    public void SetButton(ControllerButton button, bool pressed)
    {
        int mask = (int)button;
        int current;
        do
        {
            current = Volatile.Read(ref _buttons);
            int next = pressed ? current | mask : current & ~mask;
            if (Interlocked.CompareExchange(ref _buttons, next, current) == current)
                break;
        }
        while (true);
    }

    /// <summary>一次性替换全部按键状态（键盘轮询时很方便）。</summary>
    public void SetButtons(ControllerButton buttons) => Volatile.Write(ref _buttons, (int)buttons);

    /// <summary>当前按键快照，主要给 UI/调试器显示。</summary>
    public ControllerButton Buttons => (ControllerButton)Volatile.Read(ref _buttons);

    /// <summary>CPU 写 $4016：bit 0 为 1 时锁存按键，为 0 时允许串行读出。</summary>
    public void WriteStrobe(byte value)
    {
        _strobe = (value & 0x01) != 0;
        if (_strobe)
        {
            // 选通为高：并行装载当前按键。
            _shiftRegister = (byte)Volatile.Read(ref _buttons);
        }
    }

    /// <summary>CPU 读 $4016/$4017：返回串行数据的 bit 0。</summary>
    public byte Read()
    {
        if (_strobe)
        {
            // 选通保持为高时，寄存器不停重装，因此读到的永远是 A 键。
            return (byte)(Volatile.Read(ref _buttons) & 0x01);
        }

        byte result = (byte)(_shiftRegister & 0x01);

        // 右移，并在最高位补 1。真实 CD4021 在第 8 位之后会读到 1。
        _shiftRegister = (byte)((_shiftRegister >> 1) | 0x80);
        return result;
    }
}
