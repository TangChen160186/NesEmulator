# 05 · 卡带与 Mapper：iNES、换页与扫描线 IRQ

> NES 主机是固定的，卡带却在不断“进化”。Mapper 是卡带上的地址翻译硬件，
> 也是后期游戏突破 32 KB 程序空间的根本手段。

## 1. 一张卡带里有什么

```text
iNES 文件
├─ 16 字节文件头
├─ 可选 512 字节 trainer
├─ PRG-ROM（CPU 执行/读取）
└─ CHR-ROM（PPU 图块；可以为空，改用 CHR-RAM）
```

卡带可能还包含：

- PRG-RAM（`$6000-$7FFF`），用于工作区或电池存档；
- CHR-RAM（8 KB 或更多），游戏启动时把压缩图形解压进去；
- mapper 芯片（74 系列逻辑、自定义 ASIC）；
- 电池（保存 PRG-RAM）；
- 额外 VRAM（四屏卡带）。

## 2. iNES / NES 2.0 文件头

前 16 字节：

| 偏移 | 内容 |
|---|---|
| 0-3 | `NE S` + `0x1A` 魔数 |
| 4 | PRG-ROM 大小，单位 16 KB（低 8 位） |
| 5 | CHR-ROM 大小，单位 8 KB（低 8 位） |
| 6 | 镜像、电池、trainer、四屏、mapper 低 4 位 |
| 7 | 主机类型、NES 2.0 标识、mapper 中 4 位 |
| 8 | mapper 高 4 位 / submapper（NES 2.0） |
| 9 | PRG/CHR 大小高 4 位（NES 2.0） |
| 10 | PRG-RAM/NVRAM 大小（NES 2.0） |
| 11-15 | 保留，通常为 0 |

### 2.1 flags 6

```text
bit 0     镜像：0=Horizontal，1=Vertical
bit 1     电池供电 PRG-RAM
bit 2     是否有 512 字节 trainer
bit 3     四屏 VRAM
bit 4-7   mapper 低 4 位
```

### 2.2 flags 7

```text
bit 0-1   主机类型（通常 0=NTSC）
bit 2-3   NES 2.0 标识：10 表示 NES 2.0
bit 4-7   mapper 中 4 位
```

### 2.3 mapper 编号

iNES 1.0：

```text
mapper = (flags7 & 0xF0) | (flags6 >> 4)
```

NES 2.0：

```text
mapper = ((flags8 & 0x0F) << 8) | (flags7 & 0xF0) | (flags6 >> 4)
```

本项目实现见
[`InesHeader.cs`](../src/NesEmulator.Core/Cartridges/InesHeader.cs)。

### 2.4 训练器 Trainer

头中 bit2 为 1 时，文件头后还有 512 字节 trainer，原本会映射到 `$7000`。
本模拟器跳过 trainer，不执行它。少数老 ROM/修改版依赖 trainer，会不兼容。

### 2.5 NES 2.0

NES 2.0 向后兼容 iNES，但增加了：

- 更大的 mapper 编号；
- submapper；
- 指数-乘数尺寸写法；
- 更细的 RAM 大小；
- 主机类型/扩展设备。

本项目支持常见 NES 2.0 头、mapper 0-4 和标准尺寸；对超大 ROM 的指数-乘数
尺寸写法会明确抛出“不支持”。见 `InesHeader.Parse`。

## 3. 卡带访问路径

CPU 侧：

```text
$6000-$7FFF  PRG-RAM（如果 mapper 启用）
$8000-$FFFF  mapper.ReadPrg / WritePrg
```

PPU 侧：

```text
$0000-$1FFF  CHR-ROM（mapper.ReadChr）或 CHR-RAM
```

`Cartridge` 负责统一转发：

- [`Cartridge.CpuRead/CpuWrite`](../src/NesEmulator.Core/Cartridges/Cartridge.cs)
- [`Cartridge.PpuRead/PpuWrite`](../src/NesEmulator.Core/Cartridges/Cartridge.cs)
- [`Mapper`](../src/NesEmulator.Core/Cartridges/Mapper.cs) 抽象基类

新增 mapper 只需实现 `ReadPrg/ReadChr/WritePrg` 和需要的 `OnScanline`，
无需改 CPU/PPU。

## 4. Mapper 0：NROM

最简单的卡带：

```text
CPU $8000-$FFFF：32 KB PRG；若只有 16 KB，则 $C000-$FFFF 镜像 $8000-$BFFF
PPU $0000-$1FFF：8 KB CHR-ROM 或 CHR-RAM
```

没有换页寄存器。代表游戏包括《超级马里奥兄弟》等早期作品。

实现：[`Mapper000.cs`](../src/NesEmulator.Core/Cartridges/Mappers/Mapper000.cs)

## 5. Mapper 1：MMC1 / SxROM

MMC1 使用一个 **5 位串行移位寄存器**：

```text
每次向 $8000-$FFFF 写 1 位（取 value 的 bit0）
连续写 5 次后，5 位整体写入目标寄存器
value bit7=1 时立即复位移位寄存器，并把 PRG 模式强制为 3
```

目标寄存器由地址 bit13-14 选择：

| 地址 | 寄存器 |
|---|---|
| `$8000-$9FFF` | Control |
| `$A000-$BFFF` | CHR bank 0 |
| `$C000-$DFFF` | CHR bank 1 |
| `$E000-$FFFF` | PRG bank |

Control：

```text
bit 0-1 镜像：0=单屏低，1=单屏高，2=Vertical，3=Horizontal
bit 2-3 PRG 模式
bit 4   CHR 模式：0=8 KB，1=两个 4 KB
```

PRG 模式：

```text
0/1：32 KB 切换（忽略 bank bit0）
2  ：$8000 固定第一块，$C000 切换
3  ：$8000 切换，$C000 固定最后一块
```

CHR 模式：

```text
0：8 KB，chrBank0 的 bit0 忽略
1：两个 4 KB，低半区用 chrBank0，高半区用 chrBank1
```

PRG bank bit4=1 时禁用 PRG-RAM。

实现：[`Mapper001.cs`](../src/NesEmulator.Core/Cartridges/Mappers/Mapper001.cs)

## 6. Mapper 2：UxROM

```text
CPU $8000-$BFFF：可切换的 16 KB PRG bank
CPU $C000-$FFFF：固定最后一个 16 KB bank（复位向量）
PPU：通常 8 KB CHR-RAM
```

写 `$8000-$FFFF` 设置 bank。

标准 UxROM（≤256 KB）有**总线冲突**：写周期里 ROM 仍在驱动数据总线，
写入值会与当前地址的 ROM 字节按位与。游戏通常把 bank 表放在固定 ROM 区，
使写地址上的字节正好允许目标 bank 通过。

512 KB 的 UNROM-512 变体使用额外锁存/Flash，常被视为无冲突，并使用 bit4
选择 bank。本实现按 PRG 大小区分这两种情况：

```csharp
bool hasBusConflicts = Cartridge.PrgRom.Length <= 0x40000;
...
_bank = value & 0x1F;
```

实现：[`Mapper002.cs`](../src/NesEmulator.Core/Cartridges/Mappers/Mapper002.cs)

> 512 KB UNROM-512 的完整行为（CHR-RAM bank、镜像/Flash 寄存器）更接近
> mapper 30。部分 homebrew 可能需要单独的 Mapper 30 实现。

## 7. Mapper 3：CNROM

- PRG 固定；
- 写入 `$8000-$FFFF` 的低 2 位选择 8 KB CHR-ROM bank；
- 适合程序小、图形容量较大的游戏。

实现：[`Mapper003.cs`](../src/NesEmulator.Core/Cartridges/Mappers/Mapper003.cs)

## 8. Mapper 4：MMC3 / TxROM

MMC3 是学习价值最高的 mapper 之一：8 个 CHR bank、2 个可切换 PRG bank、
灵活的 2 KB/1 KB 组合，以及**扫描线 IRQ**。

### 8.1 寄存器

地址的 A0 参与选择：

| 地址 | 作用 |
|---|---|
| `$8000` 偶 | bank select：bit0-2 选寄存器，bit6 PRG 模式，bit7 CHR 模式 |
| `$8001` 奇 | 写入被选中寄存器的值 |
| `$A000` 偶 | 镜像：bit0=0 Vertical，bit0=1 Horizontal |
| `$A001` 奇 | PRG-RAM 保护（本实现忽略） |
| `$C000` 偶 | IRQ 重载值 latch |
| `$C001` 奇 | 请求下一个扫描线重载计数器 |
| `$E000` 偶 | 关闭 IRQ 并清除 pending |
| `$E001` 奇 | 打开 IRQ |

### 8.2 PRG 映射

四个 8 KB 槽：

```text
模式 0（bit6=0）：
  $8000 R6
  $A000 R7
  $C000 倒数第二块
  $E000 最后一块

模式 1（bit6=1）：
  $8000 倒数第二块
  $A000 R7
  $C000 R6
  $E000 最后一块
```

### 8.3 CHR 映射

八个 1 KB 槽：

```text
模式 0（bit7=0）：
  $0000-$07FF  R0（2 KB）
  $0800-$0FFF  R1（2 KB）
  $1000-$13FF  R2
  $1400-$17FF  R3
  $1800-$1BFF  R4
  $1C00-$1FFF  R5

模式 1（bit7=1）：
  $0000-$0FFF  R2-R5
  $1000-$17FF  R0/R1
  $1800-$1FFF  R0/R1 的另一半
```

### 8.4 扫描线 IRQ

MMC3 的 IRQ 由 PPU 的 A12 上升沿驱动。简化模型：

- 渲染开启时，每进入一条扫描线的 dot 260 调用一次 `OnScanline()`；
- counter 为 0 或收到 reload 时装入 latch；
- 否则递减；
- 减到 0 且 IRQ 使能时拉低 IRQ 线；
- CPU 写 `$E000` 应答并关闭。

本实现：[`Mapper004.cs`](../src/NesEmulator.Core/Cartridges/Mappers/Mapper004.cs)
以及 [`Ppu2C02.Step`](../src/NesEmulator.Core/Ppu/Ppu2C02.cs) 中的
`_cartridge.NotifyScanline()`。

A12 精确时序、MMC3 rev A/B 差异、IRQ 延迟等未完整建模。对分屏状态栏等常见
用法足够，但专门的 MMC3 时序测试可能失败。

## 9. 镜像与四屏

名称表镜像由卡带控制：

- 文件头 bit0 给出初始值；
- MMC1 的 Control、MMC3 的 `$A000` 可以动态改写；
- 四屏卡带需要 4 KB VRAM，mapper 的镜像位不改变物理连接；
- PPU 的 `MapNameTable` 负责逻辑表到物理表的转换。

## 10. 电池存档

`Cartridge.SaveBattery(path)` / `LoadBattery(path)` 把 PRG-RAM 写入/读出 `.sav`。
桌面端在打开 ROM 时读取同名的 `.sav`，关闭/切换 ROM 时保存。

注意：

- 只有头中声明电池的卡带才会保存；
- PRG-RAM 初始内容在真实硬件上不确定，本项目为了可复现按 0 初始化；
- 复位主机不会清空电池 RAM。

## 11. 已支持与未支持

| Mapper | 状态 |
|---|---|
| 0 NROM | 完整基础支持 |
| 1 MMC1 | 常用功能；未模拟连续写周期之外的所有板级差异 |
| 2 UxROM | 基础支持；512 KB UNROM-512 仅有部分行为 |
| 3 CNROM | 基础支持 |
| 4 MMC3 | 常用 bank + 扫描线 IRQ；IRQ 为近似时序 |
| 其他 | 加载时抛出明确的不支持错误 |

未实现：MMC5、VRC 系列、Namco 163、FDS、扩展音频、Mapper 30 的完整 UNROM-512
寄存器、四屏之外的复杂板级逻辑等。

## 12. 如何新增一个 Mapper

1. 在 `Cartridges/Mappers/` 新建类，继承 `Mapper`；
2. 实现 `ReadPrg`、`WritePrg`、`ReadChr`（如需要 `WriteChr`）；
3. 需要 IRQ 时重写 `IrqPending` 和 `OnScanline`；
4. 需要 PRG-RAM 禁用时重写 `PrgRamEnabled`；
5. 在 `MapperFactory` 注册编号；
6. 在测试中用合成 ROM 验证 bank 边界与镜像；
7. 更新 `SupportedMappers` 和文档。

## 13. 外部资料

- [NESdev: iNES](https://www.nesdev.org/wiki/INES)
- [NESdev: NES 2.0](https://www.nesdev.org/wiki/NES_2.0)
- [NESdev: Mapper](https://www.nesdev.org/wiki/Mapper)
- [NESdev: MMC1](https://www.nesdev.org/wiki/MMC1)
- [NESdev: UxROM](https://www.nesdev.org/wiki/UxROM)
- [NESdev: CNROM](https://www.nesdev.org/wiki/CNROM)
- [NESdev: MMC3](https://www.nesdev.org/wiki/MMC3)
- [NESdev: Mirroring](https://www.nesdev.org/wiki/Mirroring)
