using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeGraph.Tests.Extractors;

/// <summary>
/// sc-26145 (LensWorks re-run): callable qualified names used to drop every whitespace character
/// of the parameter list, gluing type keywords to their operands (<c>encode(to:anyEncoder)</c>,
/// <c>*constu8</c>). Whitespace between two words now survives as a single space; whitespace next
/// to punctuation is still layout and is dropped.
/// </summary>
public class TreeSitterSignatureNormalizationTests
{
    private static readonly ExtractorContext TestContext = new()
    {
        RepositoryName = "P",
        RootPath = "/test"
    };

    [Theory]
    [InlineData("(to encoder: any Encoder)", "(to encoder:any Encoder)")]
    [InlineData("(content: some View)", "(content:some View)")]
    [InlineData("(_ value: inout T)", "(_ value:inout T)")]
    [InlineData("(_ value: borrowing T)", "(_ value:borrowing T)")]
    [InlineData("(p: *const u8, len: usize)", "(p:*const u8,len:usize)")]
    [InlineData("(p: *mut u8)", "(p:*mut u8)")]
    [InlineData("(v: &mut T)", "(v:&mut T)")]
    [InlineData("(s: &'a str)", "(s:&'a str)")]
    [InlineData("(h: Box<dyn Trait>)", "(h:Box<dyn Trait>)")]
    [InlineData("(f: impl Fn(i32) -> i32)", "(f:impl Fn(i32)->i32)")]
    [InlineData("( a ,  b\n\t)", "(a,b)")]
    [InlineData("(int a, char * b)", "(int a,char*b)")]
    [InlineData("(int a, char *b)", "(int a,char*b)")]
    [InlineData("(Map < String , List < Int > > m)", "(Map<String,List<Int>>m)")]
    [InlineData("(xs: [ Int ])", "(xs:[Int])")]
    [InlineData("(@escaping  Handler)", "(@escaping Handler)")]
    [InlineData("   ", "()")]
    [InlineData(null, "()")]
    public void NormalizeSignature_KeepsOneSpaceBetweenWordsAndDropsLayout(string? parameters, string expected)
    {
        TreeSitterExtractor.NormalizeSignature(parameters).ShouldBe(expected);
    }

    [Fact]
    public async Task Swift_ExistentialParameter_KeepsTheAnyKeywordSeparate()
    {
        var result = await Extractor().ExtractAsync("/test/Sources/Codec.swift",
            "struct Box {\n    func encode(to encoder: any Encoder) {}\n    func body(content: some View) {}\n" +
            "    func swap(_ value: inout Int) {}\n}\n",
            TestContext);

        var methods = result.Nodes.Select(node => node.QualifiedName)
            .Where(qn => qn.Contains("#method:", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        methods.ShouldContain(qn => qn.EndsWith("#method:encode(to:any Encoder)", StringComparison.Ordinal));
        methods.ShouldContain(qn => qn.EndsWith("#method:body(content:some View)", StringComparison.Ordinal));
        methods.ShouldContain(qn => qn.EndsWith("#method:swap(_:inout Int)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rust_PointerAndReferenceParameters_KeepTheirKeywordsSeparate()
    {
        var result = await Extractor().ExtractAsync("/test/src/lib.rs",
            "pub fn hash(data: *const u8, out: &mut Vec<u8>, sink: &dyn Write, f: impl Fn(u8)) {}\n",
            TestContext);

        result.Nodes.Select(node => node.QualifiedName)
            .ShouldContain("P:src/lib.rs#function:hash(data:*const u8,out:&mut Vec<u8>,sink:&dyn Write,f:impl Fn(u8))");
    }

    [Fact]
    public async Task Go_ParametersWithDifferentLayout_ShareOneQualifiedNameWithoutGluedWords()
    {
        var spaced = await Extractor().ExtractAsync("/test/a.go",
            "package a\nfunc F(a int, b * string) {}\n", TestContext);
        var tight = await Extractor().ExtractAsync("/test/a.go",
            "package a\nfunc F(a  int,b *string) {}\n", TestContext);

        // Previously `F(aint,b*string)`: the parameter name was glued to its type.
        var qn = spaced.Nodes.Single(node => node.Name == "F").QualifiedName;
        qn.ShouldEndWith("#function:F(a int,b*string)");
        tight.Nodes.Single(node => node.Name == "F").QualifiedName.ShouldBe(qn);
    }

    [Fact]
    public void WalkerVersion_IsBumpedForTheQualifiedNameChange()
    {
        // Callable QNs of every tree-sitter language changed for unchanged source.
        TreeSitterExtractor.WalkerVersion.ShouldBe(2);
    }

    private static TreeSitterExtractor Extractor() => new(NullLogger<TreeSitterExtractor>.Instance);
}
