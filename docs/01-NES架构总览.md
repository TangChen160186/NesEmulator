# 01 · NES 架构总览：一台游戏机如何协同工作

> 这一篇建立全局地图。先知道“有哪些芯片、它们如何连在一起、一帧怎样过去”，
> 再去看 CPU/PPU/APU 的细节会容易得多。

## 1. 历史与设计背景

NES（北美版）于 1985 年发售，日本版名为 Famicom（Family Computer，1983）。
它属于 8 位主机世代，设计目标是：

- 用尽可能便宜、成熟的芯片做出彩色卷轴游戏；
- 卡带承担“扩展硬件”的角色，主机只提供基础总线；
- 画面由专门芯片（PPU）产生，CPU 不需要逐像素画图。

主要芯片：

| 芯片 | 作用 | 时钟 |
|---|---|---|
| Ricoh 2A03 | CPU（6502 内核）+ APU（音频）+ 手柄接口 | NTSC 1.789773 MHz |
| Ricoh 2C02 | PPU：背景、精灵、调色板、滚动、NMI | 5.369318 MHz（CPU×3） |
| 2 KB SRAM | CPU 工作内存 | 与 CPU 同速 |
| 2 KB VRAM | PPU 名称表 RAM（主机内） | 与 PPU 同速 |
| 卡带 | PRG-ROM、CHR-ROM/RAM、mapper、存档 RAM | — |

一个经常被忽略的事实：**NES 没有现代意义上的帧缓冲**。PPU 在扫描线输出的同时
即时读取图块、调色板和精灵，边算边输出模拟视频信号。因此修改滚动/调色板的时机
（在 vblank 还是扫描线中途）会直接改变画面。

## 2. 三个处理单元的分工

可以把 NES 想成一个三人小组：

```text
             ┌─────────────────────────────────────────────┐
             │                  卡带 Cartridge              │
             │  PRG-ROM  $8000-$FFFF   CHR-ROM/RAM $0000-$1FFF│
             │  mapper 寄存器、IRQ、镜像控制、电池 RAM       │
             └───────────▲──────────────────▲──────────────┘
                         │                  │
        CPU 地址/数据总线│                  │ PPU 地址/数据总线
                         │                  │
┌────────────────────────┴───────┐   ┌──────┴──────────────────────┐
│ CPU 2A03（6502 @1.79 MHz）     │   │ PPU 2C02（5.37 MHz）        │
│ - 执行游戏逻辑                 │   │ - 每帧 262 条扫描线         │
│ - 读写 $2000-$2007 控制 PPU    │   │ - 背景/精灵/滚动/调色板     │
│ - 每帧处理 NMI/IRQ             │   │ - 输出 vblank 与 NMI 信号   │
│ - 写 $4014 做 OAM DMA          │   │ - 驱动 RGB 视频信号         │
└───────────────┬────────────────┘   └─────────────────────────────┘
                │
┌───────────────┴────────────────┐
│ APU（集成在 2A03 内）           │
│ 方波×2、三角波、噪声、DMC       │
│ 输出模拟音频到电视              │
└────────────────────────────────┘
```

- **CPU 是导演**：执行 6502 机器码，读取手柄，更新游戏状态，写 PPU 寄存器。
- **PPU 是摄影师**：按自己的节奏逐行拍画面，CPU 只能在 vblank 等空隙“改布景”。
- **APU 是配乐师**：独立按帧序列器推进声道，CPU 只负责写音符寄存器。
- **卡带是扩展坞**：把更多 ROM、mapper 逻辑、存档 RAM 接进总线。

## 3. 地址空间

### 3.1 CPU 的 64 KB 空间

6502 只有 16 根地址线，因此 CPU 看到 64 KB：

| 地址范围 | 内容 | 说明 |
|---|---|---|
| `$0000-$07FF` | 2 KB CPU RAM | 零页 `$00-$FF` 访问更快 |
| `$0800-$1FFF` | RAM 镜像 | 每 `$0800` 重复一次 |
| `$2000-$3FFF` | PPU 寄存器 | 8 个寄存器，每 8 字节镜像 |
| `$4000-$4013` | APU 寄存器 | 方波/三角/噪声/DMC |
| `$4014` | OAM DMA | 写页号，复制 256 字节到 PPU OAM |
| `$4015` | APU 状态 | 读声道状态/IRQ |
| `$4016-$4017` | 手柄 1/2 | 选通与串行数据 |
| `$4020-$FFFF` | 卡带空间 | PRG-RAM、mapper 寄存器、PRG-ROM |

CPU 总线解复用在 [`NesBus.cs`](../src/NesEmulator.Core/Bus/NesBus.cs) 中完成。
真实主板的地址译码由 74 系列逻辑芯片完成；模拟器用一个 `switch` 等价实现。

### 3.2 PPU 的 14 位地址空间（16 KB）

PPU 有自己的地址总线，CPU 不能直接访问，只能通过 `$2006/$2007` 间接读写：

| PPU 地址 | 内容 |
|---|---|
| `$0000-$1FFF` | pattern table（图块像素），通常来自 CHR-ROM/RAM |
| `$2000-$2FFF` | 四个逻辑名称表（每个 1 KB：960 字节 tile 索引 + 64 字节属性） |
| `$3000-$3EFF` | 名称表镜像 |
| `$3F00-$3F1F` | 调色板 RAM（32 字节） |

卡带上的 CHR 数据通过 PPU 总线直连，因此 PPU 读取图块不需要 CPU 参与。
这也是为什么“换 CHR bank”能瞬间改变画面，而不用复制大量数据。

### 3.3 APU 寄存器

APU 占用 CPU 的 `$4000-$4017`。CPU 只写参数（频率、音量、长度），真正的波形合成
由 APU 内部的定时器和帧序列器完成。

## 4. 三个时钟与一帧的长度

NTSC 制式下：

```text
CPU 时钟   = 1.789773 MHz
PPU 时钟   = 3 × CPU = 5.369318 MHz
APU 帧序列 = 约 240 Hz（每 7457 个 CPU 周期一个 quarter frame）
```

PPU 的一帧是：

```text
262 条扫描线 × 341 个 dot = 89,342 个 PPU dot
89,342 / 3 ≈ 29,780.7 个 CPU 周期
1,789,773 / 29,780.5 ≈ 60.0988 帧/秒
```

由于 NTSC 彩色副载波的关系，实际帧率不是整 60 Hz。PPU 在奇数帧的 pre-render
扫描线少输出一个 dot，使长期平均帧率落在 60.0988 Hz 附近。

一帧的时间轴：

```text
扫描线 0-239    可见画面，每行 256 个像素（dot 1-256）
扫描线 240      post-render，不输出像素
扫描线 241      vblank 开始；dot 1 置 vblank 标志，可触发 NMI
扫描线 241-260  vblank，CPU 可以安全写 VRAM/OAM/调色板
扫描线 261      pre-render：清 vblank，准备下一帧的滚动寄存器
```

扫描线内部的简化时间轴：

```text
dot 0-255    背景/精灵像素输出
dot 256      增加 coarse Y
dot 257      复制水平滚动位（t -> v）
dot 257-320  取下一行精灵
dot 280-304  pre-render 时复制垂直滚动位（t -> v）
dot 321-336  取下一条扫描线的前两个 tile
dot 337-340  取两个名称表字节
```

本模拟器把背景按扫描线整体预取，再逐点输出；详细取舍见
[`10-已知限制与后续路线.md`](10-已知限制与后续路线.md)。

## 5. 启动过程

上电/复位时：

1. CPU 从 `$FFFC/$FFFD` 读取复位向量，跳到卡带中的启动代码。
2. PPU 还没有稳定，游戏通常等待两次 vblank。
3. 关闭 NMI/渲染，写调色板、清名称表、上传 CHR-RAM（如果卡带使用 CHR-RAM）。
4. 初始化 OAM（影子页 + DMA）与 APU。
5. 设置滚动，打开 NMI/背景/精灵。
6. 进入主循环；NMI 处理程序每帧更新滚动、OAM、音乐。

自制演示 ROM 的启动流程就是按这个顺序写的，见
[`tools/HelloNes.Builder/Program.cs`](../tools/HelloNes.Builder/Program.cs)。

## 6. 卡带为什么是“扩展硬件”

早期 NROM 只有 16/32 KB PRG 和 8 KB CHR，没有换页。随着游戏变大，卡带里加入：

- **mapper**：把 CPU/PPU 地址窗口切换到不同 ROM bank；
- **额外 RAM**：PRG-RAM（存档/工作区）、CHR-RAM（动态图形）；
- **IRQ 计数器**：MMC3 的扫描线计数器，让 CPU 在屏幕某一行中断，实现分屏滚动；
- **镜像控制**：改变名称表如何映射到 2 KB VRAM。

因此同一个“NES”主机可以运行从 24 KB 到数 MB 的卡带，而 CPU/PPU 本身不变。
这也是学习 NES 最有趣的部分之一：**主机是固定的，卡带是不断进化的**。

## 7. 本项目如何把三者装起来

核心对象关系：

```text
NesConsole
 ├─ Cpu6502 ──使用──> NesBus（ICpuBus）
 │                      ├─ CPU RAM 2 KB
 │                      ├─ Ppu2C02 ──> PpuFrameBuffer（256×240 ARGB）
 │                      ├─ Apu2A03 ──> AudioBuffer（44.1 kHz PCM）
 │                      ├─ Controller ×2
 │                      └─ Cartridge ──> Mapper（0/1/2/3/4）
 └─ 时钟推进：CPU 指令周期 → Bus.Tick → PPU ×3、APU ×1
```

关键入口：

- [`NesConsole.StepInstruction()`](../src/NesEmulator.Core/Emulation/NesConsole.cs)
- [`NesConsole.RunFrame()`](../src/NesEmulator.Core/Emulation/NesConsole.cs)
- [`NesBus.Read/Write/Tick`](../src/NesEmulator.Core/Bus/NesBus.cs)

真实硬件是三个时钟同时运行、总线每个周期都在工作；本模拟器以“一条 CPU 指令”
为原子单位推进 PPU/APU。这个近似让代码容易读，也能正确运行大量游戏，但不是
逐 dot 级精确。后续如何提升精度，见
[`10-已知限制与后续路线.md`](10-已知限制与后续路线.md)。

## 8. 继续阅读

- CPU 细节：[`02-6502-CPU.md`](02-6502-CPU.md)
- PPU 细节：[`03-2C02-PPU.md`](03-2C02-PPU.md)
- APU 细节：[`04-2A03-APU.md`](04-2A03-APU.md)
- 卡带与 mapper：[`05-卡带与Mapper.md`](05-卡带与Mapper.md)

外部总览资料：

- [NESdev Wiki](https://www.nesdev.org/wiki/)：最权威的 NES 技术百科
- [NESdev: CPU](https://www.nesdev.org/wiki/CPU)
- [NESdev: PPU](https://www.nesdev.org/wiki/PPU)
- [NESdev: APU](https://www.nesdev.org/wiki/APU)
- [Visual6502](http://www.visual6502.org/)：在晶体管级观察 6502
