using System.Diagnostics;
using System.Drawing.Imaging;
using NesEmulator.Core.Cartridges;
using NesEmulator.Core.Emulation;
using NesEmulator.Core.Input;

namespace NesEmulator.Desktop;

/// <summary>
/// 桌面主窗口：菜单、画面、状态栏和模拟线程。
///
/// 线程模型：
///   - UI 线程：处理窗口、菜单、键盘、截图；
///   - 模拟线程：以约 60.0988 Hz 调用 NesConsole.RunFrame()；
///   - 音频线程：WaveOutAudio 内部线程，从 APU 环形缓冲取 PCM。
///
/// 帧画面通过 NesScreenControl.SubmitFrame 线程安全地复制，UI 只负责绘制，
/// 避免在模拟线程直接操作 GDI+ 控件。
/// </summary>
public sealed class MainForm : Form
{
    private const double FrameRate = 60.0988;

    private readonly NesScreenControl _screen = new();
    private readonly MenuStrip _menu = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusText = new("就绪：请打开一个 .nes ROM");
    private readonly ToolStripStatusLabel _fpsText = new("FPS --");
    private readonly ToolStripMenuItem _pauseItem = new("暂停");
    private readonly ToolStripMenuItem _soundItem = new("声音");

    private readonly object _consoleLock = new();

    private NesConsole? _console;
    private string? _romPath;
    private Thread? _emulationThread;
    private WaveOutAudio? _audio;
    private volatile bool _running;
    private volatile bool _paused;
    private bool _soundEnabled = true;
    private int _presentPending;
    private bool _fullscreen;
    private FormBorderStyle _oldBorderStyle;
    private FormWindowState _oldWindowState;
    private Rectangle _oldBounds;

    private readonly string? _initialRom;

    public MainForm(string? initialRom = null)
    {
        _initialRom = initialRom;
        Text = "NES 学习模拟器";
        ClientSize = new Size(780, 760);
        MinimumSize = new Size(520, 480);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.Black;
        KeyPreview = true;
        Icon = SystemIcons.Application;

        BuildMenu();
        BuildLayout();

        _screen.SetOverlay("打开一个 .nes ROM 开始\n\n文件 → 打开 ROM（Ctrl+O）");

        KeyDown += OnGameKeyDown;
        KeyUp += OnGameKeyUp;
        Deactivate += (_, _) => ReleaseController();

        Shown += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_initialRom))
            {
                LoadRom(Path.GetFullPath(_initialRom));
                return;
            }

            string? demo = FindDemoRom();
            if (demo is not null)
                _statusText.Text = "提示：可以从“文件 → 打开示例 ROM”运行自制演示。";
        };

        FormClosing += OnClosing;
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Black,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

        _menu.Dock = DockStyle.Fill;
        _screen.Dock = DockStyle.Fill;
        _status.Dock = DockStyle.Fill;
        _status.SizingGrip = false;
        _status.AutoSize = false;
        _status.BackColor = Color.FromArgb(28, 28, 28);
        _status.ForeColor = Color.Gainsboro;
        _status.Padding = new Padding(6, 1, 6, 1);

        _statusText.Spring = true;
        _statusText.ForeColor = Color.Gainsboro;
        _statusText.TextAlign = ContentAlignment.MiddleLeft;
        _fpsText.ForeColor = Color.Gainsboro;
        _status.Items.Add(_statusText);
        _status.Items.Add(new ToolStripSeparator());
        _status.Items.Add(_fpsText);

        root.Controls.Add(_menu, 0, 0);
        root.Controls.Add(_screen, 0, 1);
        root.Controls.Add(_status, 0, 2);
        Controls.Add(root);
    }

    private void BuildMenu()
    {
        var file = new ToolStripMenuItem("文件(&F)");
        var open = new ToolStripMenuItem("打开 ROM...", null, (_, _) => OpenRom()) { ShortcutKeys = Keys.Control | Keys.O };
        var openDemo = new ToolStripMenuItem("打开示例 ROM", null, (_, _) => OpenDemo());
        var screenshot = new ToolStripMenuItem("保存截图...", null, (_, _) => SaveScreenshot()) { ShortcutKeys = Keys.F12 };
        var exit = new ToolStripMenuItem("退出", null, (_, _) => Close());
        file.DropDownItems.AddRange([open, openDemo, new ToolStripSeparator(), screenshot, new ToolStripSeparator(), exit]);

        var emulation = new ToolStripMenuItem("模拟(&E)");
        _pauseItem.ShortcutKeyDisplayString = "P";
        _pauseItem.Click += (_, _) => TogglePause();
        var reset = new ToolStripMenuItem("复位", null, (_, _) => ResetConsole()) { ShortcutKeys = Keys.F2 };
        var step = new ToolStripMenuItem("单帧步进（暂停时）", null, (_, _) => StepOneFrame()) { ShortcutKeys = Keys.F10 };
        var saveBattery = new ToolStripMenuItem("保存电池存档", null, (_, _) => SaveBatteryNow());
        _soundItem.CheckOnClick = true;
        _soundItem.Checked = true;
        _soundItem.Click += (_, _) => ToggleSound();
        emulation.DropDownItems.AddRange([_pauseItem, reset, step, new ToolStripSeparator(), _soundItem, saveBattery]);

        var view = new ToolStripMenuItem("视图(&V)");
        view.DropDownItems.Add(new ToolStripMenuItem("2× 窗口", null, (_, _) => SetScale(2)));
        view.DropDownItems.Add(new ToolStripMenuItem("3× 窗口", null, (_, _) => SetScale(3)));
        view.DropDownItems.Add(new ToolStripMenuItem("4× 窗口", null, (_, _) => SetScale(4)));
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(new ToolStripMenuItem("全屏切换", null, (_, _) => ToggleFullscreen()) { ShortcutKeys = Keys.F11 });

        var help = new ToolStripMenuItem("帮助(&H)");
        help.DropDownItems.Add(new ToolStripMenuItem("按键说明", null, (_, _) => ShowControls()));
        help.DropDownItems.Add(new ToolStripMenuItem("关于", null, (_, _) => ShowAbout()));

        _menu.Items.AddRange([file, emulation, view, help]);
    }

    // ==================== ROM 生命周期 ====================

    private void OpenRom()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 NES ROM",
            Filter = "NES ROM (*.nes)|*.nes|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            LoadRom(dialog.FileName);
    }

    private void OpenDemo()
    {
        string? path = FindDemoRom();
        if (path is null)
        {
            MessageBox.Show(this, "找不到 samples/hello-nes.nes。\n请先运行 tools/HelloNes.Builder 生成示例 ROM。",
                "示例 ROM", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        LoadRom(path);
    }

    private void LoadRom(string path)
    {
        StopEmulation();
        SaveBatteryForCurrentRom();

        try
        {
            Cartridge cartridge = InesRomLoader.Load(path);
            var console = new NesConsole(cartridge);

            string savePath = Path.ChangeExtension(path, ".sav");
            if (File.Exists(savePath))
                console.LoadBattery(savePath);

            lock (_consoleLock)
                _console = console;

            _romPath = path;
            _paused = false;
            _screen.SetOverlay(null);
            Text = $"NES 学习模拟器 — {Path.GetFileName(path)}";
            _statusText.Text = $"{Path.GetFileName(path)} · mapper {cartridge.Header.MapperNumber} {cartridge.Mapper.Name} · " +
                               $"{cartridge.Header.Mirroring} · PRG {cartridge.PrgRom.Length / 1024} KB";
            _pauseItem.Text = "暂停";
            _pauseItem.Checked = false;

            StartAudio();
            StartEmulation();
        }
        catch (Exception ex)
        {
            _screen.SetOverlay("ROM 加载失败\n" + ex.Message);
            MessageBox.Show(this, ex.Message, "无法加载 ROM", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StartEmulation()
    {
        if (_console is null || _running)
            return;

        _running = true;
        _emulationThread = new Thread(EmulationLoop)
        {
            IsBackground = true,
            Name = "NES Emulation",
        };
        _emulationThread.Start();
    }

    private void StopEmulation()
    {
        _running = false;
        Thread? thread = _emulationThread;
        _emulationThread = null;

        if (thread is not null && thread.IsAlive)
            thread.Join(3000);

        _audio?.Dispose();
        _audio = null;
    }

    private void EmulationLoop()
    {
        double frameTicks = Stopwatch.Frequency / FrameRate;
        double nextFrame = Stopwatch.GetTimestamp();
        long fpsStart = Stopwatch.GetTimestamp();
        long fpsFrames = 0;

        while (_running)
        {
            if (_paused)
            {
                Thread.Sleep(10);
                nextFrame = Stopwatch.GetTimestamp();
                continue;
            }

            NesConsole? console = _console;
            if (console is null)
            {
                Thread.Sleep(10);
                continue;
            }

            try
            {
                lock (_consoleLock)
                {
                    console.RunFrame();
                    _screen.SubmitFrame(console.FrameBuffer.Pixels.Span);
                }
            }
            catch (Exception ex)
            {
                _running = false;
                BeginInvokeSafe(() =>
                {
                    _screen.SetOverlay("模拟器异常停止\n" + ex.Message);
                    _statusText.Text = "模拟异常：" + ex.Message;
                });
                break;
            }

            RequestRepaint();

            fpsFrames++;
            long now = Stopwatch.GetTimestamp();
            if (now - fpsStart >= Stopwatch.Frequency / 2)
            {
                double fps = fpsFrames * Stopwatch.Frequency / (double)(now - fpsStart);
                fpsFrames = 0;
                fpsStart = now;
                BeginInvokeSafe(() => _fpsText.Text = $"FPS {fps:F1}");
            }

            nextFrame += frameTicks;
            double remaining = nextFrame - Stopwatch.GetTimestamp();
            if (remaining > Stopwatch.Frequency * 0.002)
            {
                int sleepMs = Math.Max(1, (int)(remaining * 1000 / Stopwatch.Frequency) - 1);
                Thread.Sleep(sleepMs);
            }
            else if (remaining > 0)
            {
                Thread.SpinWait(200);
            }
            else if (remaining < -frameTicks * 4)
            {
                // 如果机器卡顿超过 4 帧，放弃追帧，避免“快进”。
                nextFrame = Stopwatch.GetTimestamp();
            }
        }
    }

    // ==================== 控制 ====================

    private void TogglePause()
    {
        if (_console is null)
            return;

        _paused = !_paused;
        _pauseItem.Text = _paused ? "继续" : "暂停";
        _pauseItem.Checked = _paused;
        _screen.SetOverlay(_paused ? "已暂停\n按 P 继续 · F10 单帧" : null);
        _statusText.Text = _paused ? "已暂停" : "运行中";
    }

    private void ResetConsole()
    {
        if (_console is null)
            return;

        lock (_consoleLock)
            _console.Reset();

        _screen.SetOverlay(_paused ? "已暂停\n按 P 继续 · F10 单帧" : null);
        PresentOnce();
    }

    private void StepOneFrame()
    {
        if (_console is null)
            return;

        if (!_paused)
            TogglePause();

        lock (_consoleLock)
            _console.RunFrame();

        PresentOnce();
    }

    private void PresentOnce()
    {
        if (_console is null)
            return;

        _screen.SubmitFrame(_console.FrameBuffer.Pixels.Span);
        _screen.Invalidate();
    }

    private void RequestRepaint()
    {
        // UI 线程来不及重绘时，多个模拟帧共享一次 Invalidate 即可。
        if (Interlocked.Exchange(ref _presentPending, 1) != 0)
            return;

        BeginInvokeSafe(() =>
        {
            Interlocked.Exchange(ref _presentPending, 0);
            _screen.Invalidate();
        });
    }

    private void BeginInvokeSafe(Action action)
    {
        if (!IsHandleCreated || IsDisposed)
            return;

        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // 窗口正在销毁。
        }
    }

    // ==================== 键盘 ====================

    private void OnGameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.P)
        {
            TogglePause();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (e.KeyCode == Keys.F2)
        {
            ResetConsole();
            e.Handled = true;
            return;
        }

        if (e.KeyCode == Keys.F10)
        {
            StepOneFrame();
            e.Handled = true;
            return;
        }

        if (e.KeyCode == Keys.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if (e.KeyCode == Keys.Escape && _fullscreen)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        ControllerButton? button = MapKey(e.KeyCode);
        if (button is not null && _console is not null)
        {
            _console.Controller1.SetButton(button.Value, true);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void OnGameKeyUp(object? sender, KeyEventArgs e)
    {
        ControllerButton? button = MapKey(e.KeyCode);
        if (button is not null && _console is not null)
        {
            _console.Controller1.SetButton(button.Value, false);
            e.Handled = true;
        }
    }

    private static ControllerButton? MapKey(Keys key) => key switch
    {
        Keys.Up => ControllerButton.Up,
        Keys.Down => ControllerButton.Down,
        Keys.Left => ControllerButton.Left,
        Keys.Right => ControllerButton.Right,
        Keys.Z => ControllerButton.A,
        Keys.X => ControllerButton.B,
        Keys.Enter => ControllerButton.Start,
        Keys.RShiftKey or Keys.LShiftKey => ControllerButton.Select,
        _ => null,
    };

    private void ReleaseController() => _console?.Controller1.SetButtons(ControllerButton.None);

    // ==================== 声音/视图/截图 ====================

    private void StartAudio()
    {
        if (!_soundEnabled || _console is null)
            return;

        try
        {
            _audio = new WaveOutAudio(_console.Apu.Audio);
            _soundItem.Checked = true;
        }
        catch (Exception ex)
        {
            _soundItem.Checked = false;
            _statusText.Text = "音频不可用：" + ex.Message;
        }
    }

    private void ToggleSound()
    {
        _soundEnabled = _soundItem.Checked;
        if (!_soundEnabled)
        {
            _audio?.Dispose();
            _audio = null;
            return;
        }

        StartAudio();
    }

    private void SaveScreenshot()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "保存当前帧",
            Filter = "PNG 图片 (*.png)|*.png",
            FileName = _romPath is null ? "nes-frame.png" : Path.GetFileNameWithoutExtension(_romPath) + ".png",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        using var bitmap = _screen.CaptureBitmap();
        bitmap.Save(dialog.FileName, ImageFormat.Png);
        _statusText.Text = "截图已保存：" + dialog.FileName;
    }

    private void SetScale(int scale)
    {
        ClientSize = new Size(256 * scale, 240 * scale + _menu.Height + _status.Height);
    }

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _oldBorderStyle = FormBorderStyle;
            _oldWindowState = WindowState;
            _oldBounds = Bounds;
            _fullscreen = true;
            _menu.Visible = false;
            _status.Visible = false;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            _fullscreen = false;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = _oldBorderStyle;
            Bounds = _oldBounds;
            WindowState = _oldWindowState;
            _menu.Visible = true;
            _status.Visible = true;
        }
    }

    // ==================== 存档与退出 ====================

    private void SaveBatteryNow()
    {
        if (_romPath is null || _console is null)
            return;

        string savePath = Path.ChangeExtension(_romPath, ".sav");
        lock (_consoleLock)
            _console.SaveBattery(savePath);
        _statusText.Text = "电池存档已保存：" + Path.GetFileName(savePath);
    }

    private void SaveBatteryForCurrentRom()
    {
        if (_romPath is null || _console is null)
            return;

        try
        {
            lock (_consoleLock)
                _console.SaveBattery(Path.ChangeExtension(_romPath, ".sav"));
        }
        catch
        {
            // 关闭窗口时静默处理存档失败，避免阻塞退出。
        }
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        _running = false;
        Thread? thread = _emulationThread;
        if (thread is not null && thread.IsAlive)
            thread.Join(2000);

        SaveBatteryForCurrentRom();
        _audio?.Dispose();
        _audio = null;
    }

    // ==================== 帮助 ====================

    private void ShowControls()
    {
        MessageBox.Show(this,
            """
            游戏控制（玩家 1）：
              方向键   十字键
              Z        A
              X        B
              Enter    Start
              Shift    Select

            模拟器快捷键：
              P        暂停/继续
              F2       复位主机
              F10      单帧步进（暂停时）
              F12      保存截图
              F11      全屏切换
              Ctrl+O   打开 ROM
            """,
            "按键说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowAbout()
    {
        MessageBox.Show(this,
            """
            NES 学习模拟器（.NET）

            CPU : Ricoh 2A03 / MOS 6502（含非官方指令）
            PPU : 2C02，256×240，背景/精灵/NMI
            APU : 方波×2、三角波、噪声、DMC
            卡带: iNES / NES 2.0，mapper 0/1/2/3/4
            显示: 最近邻缩放 + winmm 音频输出

            这是一个以“看懂 NES 如何工作”为目标的教学项目，
            不追求商业模拟器的 100% 兼容性。
            """,
            "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string? FindDemoRom()
    {
        var directories = new[]
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
        };

        foreach (string start in directories)
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                string candidate = Path.Combine(directory.FullName, "samples", "hello-nes.nes");
                if (File.Exists(candidate))
                    return candidate;
                directory = directory.Parent;
            }
        }

        return null;
    }
}
