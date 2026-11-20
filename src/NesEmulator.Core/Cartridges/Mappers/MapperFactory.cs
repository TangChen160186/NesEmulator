namespace NesEmulator.Core.Cartridges.Mappers;

/// <summary>根据 iNES mapper 编号创建对应的硬件行为实现。</summary>
public static class MapperFactory
{
    /// <summary>当前版本支持的 mapper 列表，供 UI/README 自动展示。</summary>
    public static IReadOnlyList<(int Number, string Name)> SupportedMappers { get; } =
    [
        (0, "NROM"),
        (1, "MMC1 / SxROM"),
        (2, "UxROM"),
        (3, "CNROM"),
        (4, "MMC3 / TxROM"),
    ];

    /// <summary>创建 mapper。遇到未实现编号时给出明确的“不支持”错误。</summary>
    public static Mapper Create(Cartridge cartridge, int mapperNumber) => mapperNumber switch
    {
        0 => new Mapper000(cartridge),
        1 => new Mapper001(cartridge),
        2 => new Mapper002(cartridge),
        3 => new Mapper003(cartridge),
        4 => new Mapper004(cartridge),
        _ => throw new NotSupportedException(
            $"暂不支持 mapper {mapperNumber}。当前支持：" +
            string.Join(" / ", SupportedMappers.Select(m => $"{m.Number} {m.Name}"))),
    };
}
