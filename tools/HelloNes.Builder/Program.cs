using System.Text;
using HelloNes.Builder;

// ============================================================================
// Hello NES：为本项目自制的最小 NROM 演示 ROM。
//
// 它不依赖任何第三方 ROM，用于验证：
//   - CPU 复位、循环、分支；
//   - PPU 调色板/名称表/pattern table；
//   - vblank NMI；
//   - OAM DMA 与精灵；
//   - 手柄（代码里保留读取接口，演示主要靠视觉）。
//
// 画面：黑色背景 + "HELLO NES" + 四行彩色条 + 一个左右移动的笑脸精灵。
// ============================================================================

Console.OutputEncoding = Encoding.UTF8;

var asm = new MiniAssembler();

// ---------- 复位入口 ----------
asm.Label("reset");
asm.Sei();
asm.Cld();
asm.LdxImm(0xFF);
asm.Txs();

// 等待 PPU 进入第一次 vblank，确保可以安全访问 VRAM。
asm.Label("wait_vblank");
asm.BitAbs(0x2002);
asm.Bpl("wait_vblank");

// 初始化期间关闭渲染和 NMI。
asm.LdaImm(0x00);
asm.StaAbs(0x2000);
asm.StaAbs(0x2001);

// 把 OAM 影子页 $0200-$02FF 全部填 $FF（Y=$FF 的精灵在屏幕外）。
asm.LdaImm(0xFF);
asm.LdxImm(0x00);
asm.Label("oam_fill");
asm.StaAbsX(0x0200);
asm.Inx();
asm.Bne("oam_fill");

// 在 $0200 放一个精灵：Y=80, tile=4, attr=0, X=120。
asm.LdaImm(80);
asm.StaAbs(0x0200);
asm.LdaImm(4);
asm.StaAbs(0x0201);
asm.LdaImm(0);
asm.StaAbs(0x0202);
asm.LdaImm(120);
asm.StaAbs(0x0203);

// 通过 OAM DMA 把影子页送进 PPU。
asm.LdaImm(0);
asm.StaAbs(0x2003);
asm.LdaImm(2);
asm.StaAbs(0x4014);

// 写 32 字节调色板：$3F00 起。
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

// 清空名称表 0（960 字节 tile + 64 字节属性），用 tile 0（透明/背景色）。
asm.LdaImm(0x20);
asm.StaAbs(0x2006);
asm.LdaImm(0x00);
asm.StaAbs(0x2006);
asm.LdaImm(0x00);
asm.LdyImm(4); // 4 × 256 = 1024 字节
asm.Label("fill_outer");
asm.LdxImm(0);
asm.Label("fill_inner");
asm.StaAbs(0x2007);
asm.Inx();
asm.Bne("fill_inner");
asm.Dey();
asm.Bne("fill_outer");

// 在名称表第 6 行、第 10 列写 "HELLO NES"。
// 地址 = $2000 + 6*32 + 10 = $20CA。
asm.LdaImm(0x20);
asm.StaAbs(0x2006);
asm.LdaImm(0xCA);
asm.StaAbs(0x2006);
foreach (byte tile in new byte[] { 8, 9, 10, 10, 11, 14, 12, 9, 13 })
{
    asm.LdaImm(tile);
    asm.StaAbs(0x2007);
}

// 第 20-23 行画彩色条：地址 $2280，共 128 个 tile。
asm.LdaImm(0x22);
asm.StaAbs(0x2006);
asm.LdaImm(0x80);
asm.StaAbs(0x2006);
asm.LdxImm(0);
asm.Label("bar_loop");
asm.LdaAbsX("color_bar_tiles");
asm.StaAbs(0x2007);
asm.Inx();
asm.CpxImm(128);
asm.Bne("bar_loop");

// 滚动归零。
asm.LdaImm(0);
asm.StaAbs(0x2005);
asm.StaAbs(0x2005);

// 打开 NMI + 背景 + 精灵，并显示左 8 像素。
asm.LdaImm(0x80);
asm.StaAbs(0x2000);
asm.LdaImm(0x1E);
asm.StaAbs(0x2001);

asm.Label("main");
asm.Jmp("main");

// ---------- NMI：每帧移动一次精灵 ----------
asm.Label("nmi");
asm.Pha();
asm.IncZp(0x00);
asm.LdaZp(0x00);
asm.AndImm(0x01);
asm.Bne("nmi_skip_move");
asm.IncAbs(0x0203); // 精灵 X 坐标 +1
asm.Label("nmi_skip_move");
asm.LdaImm(0);
asm.StaAbs(0x2003); // OAMADDR = 0，DMA 从 OAM[0] 开始写
asm.LdaImm(2);
asm.StaAbs(0x4014);
asm.LdaImm(0);
asm.StaAbs(0x2005);
asm.StaAbs(0x2005);
asm.Pla();
asm.Rti();

// ---------- IRQ：本演示不使用 ----------
asm.Label("irq");
asm.Rti();

// ---------- 数据 ----------
asm.Label("palette");
asm.Bytes(
    0x0F, 0x21, 0x11, 0x31, // 背景 0：黑、浅蓝、蓝、淡蓝
    0x0F, 0x16, 0x27, 0x18, // 背景 1：红
    0x0F, 0x2A, 0x1A, 0x0A, // 背景 2：绿
    0x0F, 0x30, 0x10, 0x00, // 背景 3：白
    0x0F, 0x28, 0x18, 0x08, // 精灵 0：橙
    0x0F, 0x16, 0x27, 0x18, // 精灵 1
    0x0F, 0x2A, 0x1A, 0x0A, // 精灵 2
    0x0F, 0x30, 0x10, 0x00  // 精灵 3
);

asm.Label("color_bar_tiles");
var barTiles = new byte[128];
for (int i = 0; i < barTiles.Length; i++)
    barTiles[i] = (byte)(i % 3 + 1);
asm.Bytes(barTiles);

// 中断向量必须位于 $FFFA-$FFFF（相对 Origin 的 0x3FFA）。
asm.PadTo(0x3FFA);
asm.WordLabel("nmi");
asm.WordLabel("reset");
asm.WordLabel("irq");

byte[] prg = asm.Build();
if (prg.Length > 0x4000)
    throw new InvalidOperationException($"PRG 超出 16 KB：{prg.Length} 字节。");
Array.Resize(ref prg, 0x4000);

// ---------- 生成 CHR pattern table ----------
byte[] chr = new byte[0x2000];

// tile 0：全 0（透明，显示 backdrop）
WriteTile(chr, 1, SolidRows(), 1);   // tile 1：颜色 1
WriteTile(chr, 2, SolidRows(), 2);   // tile 2：颜色 2
WriteTile(chr, 3, SolidRows(), 3);   // tile 3：颜色 3

// tile 4：精灵笑脸
WriteTile(chr, 4,
[
    "..oooo..",
    ".oooooo.",
    "oo####oo",
    "oo####oo",
    "oooooooo",
    "oo####oo",
    ".oooooo.",
    "..oooo..",
], 0);

// 8-14：5×7 字体 H E L O N S + 空格
var font = new Dictionary<char, string[]>
{
    ['H'] = ["#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"],
    ['E'] = ["#####", "#....", "#....", "####.", "#....", "#....", "#####"],
    ['L'] = ["#....", "#....", "#....", "#....", "#....", "#....", "#####"],
    ['O'] = [".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."],
    ['N'] = ["#...#", "##..#", "##..#", "#.#.#", "#..##", "#..##", "#...#"],
    ['S'] = [".#####", "#....", "#....", ".####", "....#", "....#", "#####"],
    [' '] = ["     ", "     ", "     ", "     ", "     ", "     ", "     "],
};

WriteFontTile(chr, 8, font['H']);
WriteFontTile(chr, 9, font['E']);
WriteFontTile(chr, 10, font['L']);
WriteFontTile(chr, 11, font['O']);
WriteFontTile(chr, 12, font['N']);
WriteFontTile(chr, 13, font['S']);
WriteFontTile(chr, 14, font[' ']);

// ---------- 组装 .nes 文件 ----------
var header = new byte[16];
header[0] = (byte)'N';
header[1] = (byte)'E';
header[2] = (byte)'S';
header[3] = 0x1A;
header[4] = 1; // 1 × 16 KB PRG
header[5] = 1; // 1 × 8 KB CHR
header[6] = 0; // 水平镜像，无电池，无 trainer
header[7] = 0; // mapper 0

string root = FindRoot();
string outputDirectory = Path.Combine(root, "samples");
Directory.CreateDirectory(outputDirectory);
string outputPath = Path.Combine(outputDirectory, "hello-nes.nes");

using (var output = File.Create(outputPath))
{
    output.Write(header);
    output.Write(prg);
    output.Write(chr);
}

Console.WriteLine($"已生成 {outputPath}");
Console.WriteLine($"PRG={prg.Length} 字节，CHR={chr.Length} 字节，构建于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

static string[] SolidRows() =>
[
    "########", "########", "########", "########",
    "########", "########", "########", "########",
];

static void WriteTile(byte[] chr, int index, string[] rows, int color)
{
    for (int y = 0; y < 8; y++)
    {
        int low = 0, high = 0;
        for (int x = 0; x < 8; x++)
        {
            char c = x < rows[y].Length ? rows[y][x] : ' ';
            int pixel = c switch
            {
                '#' => color == 0 ? 3 : color,
                'o' => 1,
                '+' => 2,
                _ => 0,
            };

            int bit = 7 - x;
            if ((pixel & 1) != 0) low |= 1 << bit;
            if ((pixel & 2) != 0) high |= 1 << bit;
        }

        chr[index * 16 + y] = (byte)low;
        chr[index * 16 + 8 + y] = (byte)high;
    }
}

static void WriteFontTile(byte[] chr, int index, string[] glyph)
{
    // 把 5×7 字形放进 8×8 tile 的右下偏移位置。
    var rows = new string[8];
    rows[0] = "        ";
    for (int y = 0; y < 7; y++)
        rows[y + 1] = " " + glyph[y] + "  ";

    WriteTile(chr, index, rows, 3);
}

static string FindRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "NesEmulator.slnx")))
            return directory.FullName;
        directory = directory.Parent;
    }

    return Directory.GetCurrentDirectory();
}
