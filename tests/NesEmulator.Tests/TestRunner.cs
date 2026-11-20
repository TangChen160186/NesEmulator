namespace NesEmulator.Tests;

/// <summary>
/// 极简测试运行器，避免为了“能跑测试”再引入第三方测试框架。
/// 每个测试是一个普通方法，失败直接抛异常，输出统一汇总。
/// </summary>
internal sealed class TestRunner
{
    private int _passed;
    private int _failed;
    private int _skipped;

    public void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"  [PASS] {name}");
        }
        catch (SkipTestException ex)
        {
            _skipped++;
            Console.WriteLine($"  [SKIP] {name}：{ex.Message}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"  [FAIL] {name}");
            Console.WriteLine($"         {ex.Message}");
        }
    }

    public void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"■ {title}");
    }

    public int Finish()
    {
        Console.WriteLine();
        Console.WriteLine($"测试汇总：通过 {_passed}，失败 {_failed}，跳过 {_skipped}。");
        return _failed == 0 ? 0 : 1;
    }

    public static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T actual, T expected, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
            throw new InvalidOperationException($"{message}：期望 {expected}，实际 {actual}。");
    }
}

internal sealed class SkipTestException(string message) : Exception(message);
