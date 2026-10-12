using Lib;

namespace Lib.Tests;

public class TextToolsTests
{
    public void WordCount_counts_words()
    {
        var count = TextTools.WordCount("hello world");
        System.Diagnostics.Debug.Assert(count == 2);
    }

    public bool WordCount_namespace_qualified() => Lib.TextTools.WordCount("a") == 1;

    public int Local_value_named_like_the_type()
    {
        var TextTools = new Shadow();
        return TextTools.WordCount("a");
    }
}

public class Shadow
{
    public int WordCount(string text) => text.Length;
}
