# 09 · NES 游戏是怎样开发的：从零写一个演示 ROM

> 这一篇从“做游戏”而不是“做模拟器”的视角，走一遍 NES 程序从源码到画面的流程。
> 配合本项目自制的 `hello-nes.nes` 及其生成器阅读效果最好。

## 1. 与现代游戏开发最大的不同

| 现代 | NES |
|---|---|
| 大内存、虚拟内存 | 2 KB CPU RAM，卡带 ROM 分 bank |
| 任意分辨率帧缓冲 | 256×240，PPU 实时生成扫描线 |
| 浮点、GPU shader | 8 位整数、查表、定点数 |
| 资源文件随意加载 | CHR/名称表/palette 要手动上传和切换 |
| 每帧想画多少画多少 | 每行最多 8 个精灵，需要闪烁/复用 |
| 音频文件 | APU 实时合成，写寄存器 |
| 高级语言 | 主要是 6502 汇编，或 cc65 C |

NES 游戏的本质是：**在 vblank 的短暂窗口里，把下一帧要用的 PPU 状态改好**，
其余时间 CPU 都在算游戏逻辑。

## 2. 工具链

| 工具 | 作用 |
|---|---|
| [ca65](https://cc65.github.io/doc/ca65.html) | 成熟 6502 汇编器，支持宏/段/链接脚本 |
| [asm6f](https://github.com/foo12/asm6f) | 轻量汇编器，语法简单 |
| [cc65](https://cc65.github.io/) | C 编译器，适合逻辑复杂但性能要求不极端的游戏 |
| [NESmaker](https://www.thenew8bitheroes.com/) | 可视化制作，适合不写汇编的入门 |
| [FamiStudio](https://famistudio.org/) | NES 音乐编辑器，可导出汇编/数据 |
| [Mesen2](https://github.com/SourMesen/Mesen2) | 高精度模拟器 + 调试器（看 PPU/APU/内存） |
| YY-CHR 等 | 画 8×8 CHR tile、名称表 |
| 烧录器/复刻板 | 在真机上运行 |

本项目用 C# 写了一个**迷你汇编器**，仅实现演示 ROM 需要的指令：
[`MiniAssembler.cs`](../tools/HelloNes.Builder/MiniAssembler.cs)。
它不是通用工具，但能让你在没有 ca65 的机器上直接生成一个可运行 ROM。

## 3. 一个 NES 程序的基本结构

```text
复位向量 -> reset:
    关中断/初始化栈
    等 vblank
    关渲染
    上传调色板/名称表/CHR
    初始化 OAM
    初始化 APU
    设置滚动
    开 NMI/渲染
main:
    游戏逻辑
    jmp main

nmi:
    保存寄存器
    更新 OAM（DMA）
    更新滚动
    更新音乐
    恢复寄存器
    rti

irq:
    rti
```

关键是：**主循环不做 PPU 写入**（除非在 vblank 内），所有 PPU 更新集中在 NMI。
这样不会在可见扫描线中途改寄存器导致画面撕裂/抖动。

## 4. 复位初始化

一个典型的启动顺序：

```asm
reset:
    sei             ; 关 IRQ
    cld             ; 清十进制（NES 无 BCD）
    ldx #$FF
    txs             ; 栈顶

wait_vblank:
    bit $2002
    bpl wait_vblank ; 等 vblank

    lda #$00
    sta $2000       ; 关 NMI
    sta $2001       ; 关渲染

    ; 1. 写调色板
    ; 2. 清名称表/属性表
    ; 3. 如果 CHR-RAM：上传 tile 数据
    ; 4. 初始化 OAM 影子页
    ; 5. 初始化 APU
    ; 6. 设置滚动
    lda #$80
    sta $2000       ; 开 NMI
    lda #$1E
    sta $2001       ; 开背景+精灵
```

真实游戏通常等待两次 vblank（PPU 上电稳定），并在关渲染时完成大量 VRAM 传输。

## 5. 主循环与 NMI 的分工

### 5.1 主循环

```asm
main:
    jsr update_player
    jsr update_enemies
    jsr check_collision
    jmp main
```

主循环可以随时读写 CPU RAM，但不要直接写 `$2007`/`$2004`（除非确定在 vblank）。

### 5.2 NMI

```asm
nmi:
    pha
    txa
    pha
    tya
    pha

    ; 1. 把 OAM 影子页 DMA 到 PPU
    lda #$00
    sta $2003
    lda #$02
    sta $4014

    ; 2. 更新滚动
    lda scroll_x
    sta $2005
    lda #$00
    sta $2005

    ; 3. 音乐
    jsr play_music

    pla
    tay
    pla
    tax
    pla
    rti
```

### 5.3 帧同步

主循环和 NMI 共享变量时要注意原子性：

- 8 位变量在 6502 上读写是原子的；
- 16 位变量需要关 NMI 或使用双缓冲；
- 不要在 NMI 里做耗时太长的逻辑，vblank 大约只有 2270 个 CPU 周期。

## 6. 手柄读取

标准流程：

```asm
read_controller:
    lda #$01
    sta $4016   ; 选通高：锁存
    lda #$00
    sta $4016   ; 选通低：开始移位
    ldx #$08
    lda #$00
    sta buttons
loop:
    lda $4016   ; 依次返回 A/B/Select/Start/Up/Down/Left/Right
    lsr a       ; bit0 -> carry
    ror buttons ; carry -> bit7，已读位逐步右移
    dex
    bne loop
    rts
```

读完后用一个字节保存：
`A=bit0, B=bit1, Select=bit2, Start=bit3, Up=bit4, Down=bit5, Left=bit6, Right=bit7`。
之所以用 `ROR`，是因为第一位 A 会被后续 7 次右移推到 bit0；如果用 `ROL`，
位序会反过来。

本模拟器的手柄实现见
[`Controller.cs`](../src/NesEmulator.Core/Input/Controller.cs)。

## 7. 图形资源

### 7.1 CHR tile

- 8×8 像素，每个像素 2 位（4 色）；
- 每个 tile 16 字节：8 字节低位平面 + 8 字节高位平面；
- 颜色号 0-3 对应调色板的 0-3 项；
- 256 个 tile 组成一个 pattern table。

### 7.2 名称表

- 32×30 个 tile 编号；
- 64 字节属性表，每 16×16 像素区域选调色板；
- 一屏 256×240。

### 7.3 调色板

- 4 个背景调色板 + 4 个精灵调色板；
- 每个 4 色；
- `$3F00` 是全局背景色；
- 精灵颜色 0 透明。

### 7.4 精灵限制

- 每行最多 8 个精灵；
- 超过会闪烁/丢失；
- 64 个精灵总数；
- 8×8 或 8×16；
- 每个精灵 4 字节 OAM。

这些限制决定了 NES 游戏的美术风格：大量复用 tile、闪烁、扫描线分屏。

## 8. 滚动

NES 的滚动是“世界坐标”而不是“相机矩阵”：

- 名称表 32×30 tile；
- 水平滚动超过一屏时切换名称表；
- 垂直滚动同理；
- `$2005` 写 X/Y，`$2000` 的低 2 位选名称表；
- 分屏/状态栏需要在扫描线中途改滚动，通常配合 sprite 0 hit 或 MMC3 IRQ。

一个简单的每帧水平滚动：

```asm
nmi:
    inc scroll_x
    lda scroll_x
    sta $2005
    lda #$00
    sta $2005
    rti
```

但真实游戏还要处理“滚动到名称表边界时更新 tile 列”，否则会看到空白/重复。

## 9. 音乐

NES 音乐不是播放音频文件，而是写 APU 寄存器：

- Pulse 1/2：旋律、和声；
- Triangle：低音；
- Noise：鼓；
- DMC：采样。

音乐驱动通常在每个 NMI 中运行一次，按帧计数更新音符/包络。FamiStudio 可以导出
数据表和播放器代码。

## 10. 演示 ROM 逐段解读

源码：[`tools/HelloNes.Builder/Program.cs`](../tools/HelloNes.Builder/Program.cs)

### 10.1 生成机器码

```csharp
var asm = new MiniAssembler();
asm.Label("reset");
asm.Sei();
asm.Cld();
asm.LdxImm(0xFF);
asm.Txs();
```

`MiniAssembler` 记录标签和绝对地址 fixup，最后把代码放到 `$C000` 的 16 KB NROM 中。

### 10.2 等 vblank

```csharp
asm.Label("wait_vblank");
asm.BitAbs(0x2002);
asm.Bpl("wait_vblank");
```

`BIT $2002` 把 bit7（vblank）送入 N 标志；`BPL` 在 N=0 时循环。读取 `$2002`
同时会清 vblank 和写锁存。

### 10.3 写调色板

```csharp
asm.LdaImm(0x3F);
asm.StaAbs(0x2006);
asm.LdaImm(0x00);
asm.StaAbs(0x2006);

asm.LdxImm(0);
asm.Label("palette_loop");
asm.LdaAbsX("palette");
asm.StaAbs(0x2007);
asm.Inx();
asm.CpxImm(32);
asm.Bne("palette_loop");
```

向 `$2006` 写两次设置 VRAM 地址，然后循环写 `$2007`。`PPUCTRL` 的 bit2=0，
所以地址每次 +1。

### 10.4 清名称表

4 × 256 = 1024 字节，覆盖 960 字节 tile + 64 字节属性表。因为写入的是 tile 0，
且 tile 0 全透明，所以背景显示全局背景色。

### 10.5 写标题和彩色条

- 标题地址 `$20CA`（第 6 行第 10 列），写 9 个 tile 编号；
- 彩色条地址 `$2280`，写 128 个 tile 编号；
- 属性表保持 0，全部使用背景调色板 0。

### 10.6 精灵与 OAM DMA

```csharp
asm.LdaImm(0);
asm.StaAbs(0x2003);
asm.LdaImm(2);
asm.StaAbs(0x4014);
```

把 CPU 页 `$0200-$02FF` 复制到 OAM。NMI 中每两帧把精灵 X 坐标 +1，然后再次 DMA。

### 10.7 NMI

```csharp
asm.Label("nmi");
asm.Pha();
asm.IncZp(0x00);
asm.LdaZp(0x00);
asm.AndImm(0x01);
asm.Bne("nmi_skip_move");
asm.IncAbs(0x0203);
asm.Label("nmi_skip_move");
// OAM DMA + 滚动归零
asm.Pla();
asm.Rti();
```

`$00` 是 NMI 计数器，`$0203` 是 OAM 影子页中精灵 0 的 X 坐标。测试正是检查这两个值。

### 10.8 中断向量

```csharp
asm.PadTo(0x3FFA);
asm.WordLabel("nmi");
asm.WordLabel("reset");
asm.WordLabel("irq");
```

CPU 从 `$FFFA/$FFFC/$FFFE` 读取 NMI/复位/IRQ 向量。由于 16 KB NROM 镜像，
`$C000` 与 `$8000` 指向同一块 PRG。

## 11. 从演示 ROM 到完整游戏

可以按以下顺序扩展：

1. **手柄控制**：读 `$4016`，按左右移动精灵；
2. **背景卷轴**：滚动变量 + 名称表列更新；
3. **碰撞**：AABB 或 tile 碰撞；
4. **敌人/动画**：OAM 影子页 + 帧计数器；
5. **状态栏**：sprite 0 hit 或 MMC3 IRQ 分屏；
6. **音乐**：FamiStudio 导出 + NMI 驱动；
7. **多关卡**：mapper bank 切换、压缩数据；
8. **存档**：PRG-RAM + 电池。

## 12. 在真机上运行

需要：

- 一张兼容的卡带板（NROM/UNROM/MMC1/MMC3 等）；
- 烧录器（如 Kazzo、INL Retro Prog 等）；
- 或者使用 Flash 卡带/everdrive。

注意：

- 不同板子的 mapper/镜像/电池必须匹配；
- 16 KB NROM 的 ROM 要按板子要求填充/镜像；
- 真机对时序更敏感，模拟器能跑不代表真机能跑；
- 先用 Mesen 的高精度模式验证。

## 13. 外部资料

- [NESdev: Programming guide](https://www.nesdev.org/wiki/Programming_guide)
- [NESdev: 6502 assembly optimizations](https://www.nesdev.org/wiki/6502_assembly_optimizations)
- [NESdev: PPU](https://www.nesdev.org/wiki/PPU)
- [NESdev: APU](https://www.nesdev.org/wiki/APU)
- [ca65 文档](https://cc65.github.io/doc/ca65.html)
- [asm6f](https://github.com/foo12/asm6f)
- [FamiStudio](https://famistudio.org/)
- [nesdoug 教程](https://nesdoug.com/)
- [Nerdy Nights 教程（存档）](https://nerdy-nights.nes.science/)
- [NESmaker](https://www.thenew8bitheroes.com/)
- [Mesen2](https://github.com/SourMesen/Mesen2)
