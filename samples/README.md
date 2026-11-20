# samples/hello-nes.nes

这是本项目**自制**的 NROM 演示 ROM，用于验证和演示模拟器：

- 6502 复位、循环、分支；
- PPU 调色板、名称表、pattern table；
- vblank NMI；
- OAM DMA 与精灵；
- 逐帧移动精灵。

画面内容全部由代码生成：没有使用任何商业游戏的图形、代码或音乐。
ROM 与源码采用 MIT 许可证，可随本项目自由分发。

## 重新生成

```powershell
dotnet run --project tools/HelloNes.Builder
```

生成器源码：[`tools/HelloNes.Builder/Program.cs`](../tools/HelloNes.Builder/Program.cs)
迷你汇编器：[`tools/HelloNes.Builder/MiniAssembler.cs`](../tools/HelloNes.Builder/MiniAssembler.cs)

## 文件信息

| 字段 | 值 |
|---|---|
| 格式 | iNES 1.0 |
| Mapper | 0（NROM） |
| PRG | 16 KB |
| CHR | 8 KB CHR-ROM |
| 镜像 | Horizontal |
| 大小 | 24,592 字节（16 字节头 + 16 KB + 8 KB） |

## 控制

演示本身自动移动精灵，不读取手柄；键盘/手柄的读取实现见
[`Controller.cs`](../src/NesEmulator.Core/Input/Controller.cs)。
