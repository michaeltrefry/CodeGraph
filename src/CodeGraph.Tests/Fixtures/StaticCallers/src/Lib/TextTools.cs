namespace Lib;

public static class TextTools
{
    public static int WordCount(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    public static int DoubleWordCount(string text) => WordCount(text) * 2;
}
