namespace NesEmulator.Core.Cartridges;

/// <summary>
/// 把磁盘上的 .nes 文件读成 <see cref="Cartridge"/>。
///
/// 解析步骤：
///   1. 读前 16 字节并交给 <see cref="InesHeader"/> 校验/解释；
///   2. 若 header 声明 trainer，则跳过 512 字节；
///   3. 按头中尺寸截取 PRG-ROM 和 CHR-ROM；
///   4. 检查文件是否被截断，构造卡带与 mapper。
///
/// 安全原则：绝不根据未校验的长度直接分配大数组；任何越界都抛出
/// 带中文说明的 InvalidDataException，让前端可以友好地提示用户。
/// </summary>
public static class InesRomLoader
{
    /// <summary>从文件加载 ROM。</summary>
    public static Cartridge Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        return Load(data);
    }

    /// <summary>从内存字节加载 ROM，方便单元测试和嵌入式资源。</summary>
    public static Cartridge Load(ReadOnlySpan<byte> data)
    {
        if (data.Length < InesHeader.HeaderSize)
            throw new InvalidDataException("文件太短，不足 16 字节的 iNES 头。");

        InesHeader header = InesHeader.Parse(data[..InesHeader.HeaderSize]);

        int offset = InesHeader.HeaderSize;
        if (header.HasTrainer)
        {
            // trainer 是 512 字节、原本加载到 $7000 的补丁数据。
            // 现代 ROM 极少使用；本模拟器跳过它，并在 README 中说明限制。
            offset += InesHeader.TrainerSize;
            if (offset > data.Length)
                throw new InvalidDataException("文件头声明了 trainer，但文件在 trainer 结束前就截断了。");
        }

        long required = (long)offset + header.PrgRomSize + header.ChrRomSize;
        if (data.Length < required)
        {
            throw new InvalidDataException(
                $"ROM 文件被截断：头声明需要 {required} 字节，实际只有 {data.Length} 字节。");
        }

        byte[] prgRom = data.Slice(offset, header.PrgRomSize).ToArray();
        offset += header.PrgRomSize;

        byte[] chrRom = header.ChrRomSize > 0
            ? data.Slice(offset, header.ChrRomSize).ToArray()
            : [];

        // 只保留头声明的数据，忽略文件尾部可能有也可能没有的附加字节。
        return new Cartridge(header, prgRom, chrRom);
    }
}
