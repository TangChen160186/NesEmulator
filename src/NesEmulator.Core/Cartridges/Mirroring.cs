namespace NesEmulator.Core.Cartridges;

/// <summary>
/// 卡带控制的两个 CIRAM（PPU 内部的 2 KB 名称表 RAM）如何映射到四个逻辑名称表。
///
/// NES 的 PPU 地址空间里有四个逻辑名称表：
///   $2000-$23FF、$2400-$27FF、$2800-$2BFF、$2C00-$2FFF
/// 但主机内部通常只有 2 KB 物理 CIRAM，因此必须“镜像”出四个窗口。
///
/// 术语容易混淆，这里按 iNES 头 flag 6 的 bit 0 定义：
///   Horizontal（水平镜像，header bit=0）：逻辑表 0/1 指向同一块，2/3 指向另一块。
///   Vertical  （垂直镜像，header bit=1）：逻辑表 0/1 分别指向两块，2/3 与它们重复。
/// 只有 Vertical 镜像才允许横向滚动时同时看到左右两张不同的名称表；
/// 超级马里奥兄弟等横向卷轴游戏通常使用 Vertical 镜像。
/// </summary>
public enum Mirroring
{
    /// <summary>水平镜像：A A B B（适合纵向滚动）。</summary>
    Horizontal = 0,

    /// <summary>垂直镜像：A B A B（适合横向滚动）。</summary>
    Vertical = 1,

    /// <summary>四屏 VRAM：四张名称表各自独立，卡带必须带额外 2 KB VRAM。</summary>
    FourScreen = 2,

    /// <summary>单屏低：四个逻辑名称表全部指向物理表 0。</summary>
    SingleScreenLower = 3,

    /// <summary>单屏高：四个逻辑名称表全部指向物理表 1。</summary>
    SingleScreenUpper = 4,
}
