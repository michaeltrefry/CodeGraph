using CodeGraph.Data;
using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>
/// sc-26200: file_hotspot complexity for C/C++, Go, PHP (brace languages, per-language lexing) and
/// Ruby and Bash (keyword-delimited blocks). Functions are function/method definitions; branches
/// are control-flow tokens inside function bodies; nesting depth comes from blocks. Braces and
/// keywords inside strings, comments, heredocs and raw strings are never code.
/// </summary>
public class VitalsLanguageComplexityTests
{
    [Theory]
    [InlineData(".c", """
        #include <stdio.h>
        #define BLOCK { {
        /* { not code */
        static int
        max_of(int a, int b)
        {
            if (a > b && a != 0) {
                return a;
            }
            return b;
        }

        int classify(const char *s) {
            const char *open = "{{{";
            char brace = '{';
            for (int i = 0; s[i]; i++) {
                switch (s[i]) {
                case 'a':
                    while (i > 0) {
                        i--;
                    }
                    break;
                case '}':
                    return 1;
                }
            }
            return 0; // }}}
        }

        int prototype(int x);
        """, 4, 2, 16, 7)]
    [InlineData(".hpp", """
        namespace app {
        class Counter : public Base {
        public:
            int next() const {
                return count_ > 0 ? count_ : 0;
            }
            Counter() : count_(0) {}
            void reset();
        private:
            int count_;
        };
        }
        """, 3, 2, 3, 1)]
    [InlineData(".go", """
        package main

        import "fmt"

        // Shape { is a comment
        type Shape interface {
            Area() float64
        }

        func (b Box) Area() float64 {
            label := "{{"
            raw := `
            } }
            `
            r := '{'
            if b.w > 0 && b.h > 0 {
                for i := 0; i < 2; i++ {
                    switch {
                    case i == 0:
                        fmt.Println(label, raw, r)
                    }
                }
            }
            return 1
        }

        func helper() int { return 0 }
        """, 4, 2, 16, 5)]
    [InlineData(".php", """
        <html><body>{ {
        <?php
        namespace App;
        use function App\helper;

        # a { comment
        #[Attribute]
        abstract class Greeter {
            public function greet(array $names): string {
                $out = "Hi {$names[0]} }";
                $tpl = <<<EOT
                { {
                EOT;
                foreach ($names as $n) {
                    if ($n === '}' || $n === null) {
                        $out .= $n ?? '';
                    }
                }
                return $this->match($out); // }
            }

            abstract function size(): int;
        }
        ?>
        <p>{</p>
        """, 4, 2, 12, 4)]
    [InlineData(".rb", """
        # frozen_string_literal: true
        =begin
        def not_code
          if x
        =end
        module Shapes
          class Box < Base
            def name = "box"

            def area(items)
              label = "#{items.size} {"
              pattern = /\d{2}/
              words = %w[if end {]
              total = 0 if items.empty?
              items.each do |item|
                if item.ok? && item.size > 0
                  total += 1 unless item.skip?
                end
              end
              text = <<~EOS
                def fake
                end
              EOS
              total
            end
          end
        end
        """, 5, 2, 16, 4)]
    [InlineData(".sh", """
        #!/usr/bin/env bash
        # deploy { script
        set -euo pipefail

        usage() {
          echo "usage: ${0##*/} {start|stop}"
        }

        function deploy {
          local count=$#
          local len=${#1}
          for host in "$@"; do
            if [[ -n "$host" && "$host" != "{" ]]; then
              case "$host" in
                prod-*) echo "prod }" ;;
                *) echo 'other {' ;;
              esac
            elif [ "$count" -gt 0 ] || true; then
              cat <<'EOF'
        if { then
        EOF
            fi
          done
          echo {a,b} # }
        }
        """, 4, 2, 17, 8)]
    public void ComputeFileComplexity_MeasuresEachLanguagesBlocksFunctionsAndBranches(
        string extension, string source, int maxDepth, int functions, int longestFunction, int branches)
    {
        var data = VitalsAnalyzer.ComputeFileComplexity(extension, source);

        data.MaxNestingDepth.ShouldBe(maxDepth);
        data.FunctionCount.ShouldBe(functions);
        data.LongestFunction.ShouldBe(longestFunction);
        data.BranchCount.ShouldBe(branches);
        data.Score.ShouldBeGreaterThan(0);
    }

    /// <summary>Each trap holds one function whose body nests to depth 2 once literals and comments are skipped.</summary>
    [Theory]
    // C++ raw string spanning lines; ")\"" inside it is not its delimiter.
    [InlineData(".cpp", """
        void f() {
            auto s = R"x(
        { "{" )" }
        )x";
            if (s.empty()) {
                g();
            }
        }
        """, 8, 1)]
    // A directive and a // comment continued onto the next line by a trailing backslash.
    [InlineData(".c", """
        #define BEGIN \
            {
        // a comment \
        {
        int f(void) {
            const char *s = "a {\
        b }";
            if (1) {
                g();
            }
        }
        """, 7, 1)]
    // Digit separators are not char literals; prefixed and escaped literals are.
    [InlineData(".cc", """
        int f() {
            int n = 1'000; char c = '{';
            wchar_t w = L'}'; const char *q = "\"{";
            if (n) {
                g('}');
            }
        }
        """, 7, 1)]
    // Go block comments do not nest.
    [InlineData(".go", """
        func f() {
            /* outer /* inner */
            if true {
                g()
            }
        }
        """, 6, 1)]
    [InlineData(".go", """
        func f() {
            q := '\''
            s := "\"{"
            if q == '{' {
                g(s)
            }
        }
        """, 7, 1)]
    // A PHP line comment ends at ?>; the inline HTML after it is not code.
    [InlineData(".php", """
        <?php
        function f() {
            // x ?>
        <div>{</div>
        <? if (true) { ?><b>}</b><?php
                g();
            }
        }
        """, 7, 1)]
    [InlineData(".php", """
        <?php
        function f() {
            $s = <<<'EOT'
          } {$x["}
          EOT;
            if ($s) {
                g();
            }
        }
        """, 8, 1)]
    // A string inside a {$…} interpolation does not end the enclosing string.
    [InlineData(".php", """
        <?php
        function f($a) {
            $s = "x {$a["}"]} y";
            if ($s) {
                g();
            }
        }
        """, 6, 1)]
    // #[ is an attribute, # a comment; keywords are case-insensitive.
    [InlineData(".php", """
        <?php
        #[Pure] FUNCTION f() { # trailing { comment
            IF (true) {
                g();
            }
        }
        """, 5, 1)]
    // The optional do of while/until/for does not open a second block.
    [InlineData(".rb", """
        def f(q)
          while q.any? do
            q.pop
          end
          q
        end
        """, 6, 1)]
    // Method calls, symbols and hash labels named like keywords are not keywords; a modifier if is a branch.
    [InlineData(".rb", """
        def f(x)
          k = x.class
          opts = { if: 1, unless: :end }
          return :if if x
          k
        end
        """, 6, 1)]
    // Nested delimiters in %Q{}, and strings inside its interpolation.
    [InlineData(".rb", """
        def f(x)
          s = %Q{a {b} #{x ? "}" : "{"} c}
          if s
            g
          end
        end
        """, 6, 2)]
    // Pattern-matching in arms are branches; the in of a for loop is not; and/or are.
    [InlineData(".rb", """
        def f(x)
          for y in x do g(y) end
          case x
          in Integer then 1
          in String then 2
          end
          g(x) or h(x)
        end
        """, 8, 5)]
    // Two heredocs started on one line; their bodies are not code.
    [InlineData(".rb", """
        def f
          g(<<~A, <<-'B')
            if x
          A
            while y
            B
          if h
            k
          end
        end
        """, 10, 1)]
    // Everything after __END__ is data.
    [InlineData(".rb", """
        def f
          if x
            y
          end
        end
        __END__
        def g
          if x
        """, 5, 1)]
    // Division is not a regex; =~ /…/ is; ?{ is a character literal.
    [InlineData(".rb", """
        def f(a, b)
          r = a / b
          m = b =~ /if|{/
          c = ?{
          x = $' if b
          if r
            g
          end
        end
        """, 9, 2)]
    // A <<- heredoc's terminator may be tab-indented.
    [InlineData(".sh", "f() {\n\tcat <<-EOF\n\t\tif x; then\n\tEOF\n\tif true; then\n\t\tg\n\tfi\n}\n", 8, 1)]
    // Quotes nested inside a command substitution inside a double-quoted string.
    [InlineData(".sh", """
        f() {
          x="$(echo " ; fi ; ")"
          if true; then
            g
          fi
        }
        """, 6, 1)]
    // Reserved words are only keywords in command position.
    [InlineData(".sh", """
        f() {
          echo if then fi done
          grep -q while file && echo for
          grep -q x <<< "$y"
          (( n = n << 1 ))
          echo a \
            if then
          if true; then g; fi
        }
        """, 9, 2)]
    // ${#x} and $# are not comments; brace expansion and escaped braces are not blocks.
    [InlineData(".bash", """
        f()
        {
          n=${#1} m=$# # {
          y=${x/a/; fi }
          echo {a,b} \{ x#y; if true; then
          echo $'it\'s {'
            g
          fi
        }
        """, 9, 1)]
    // Case patterns spelled like keywords; each arm is a branch.
    [InlineData(".zsh", """
        f() {
          case "$1" in
            (if|fi) g ;;
            done) h ;&
            *) k
          esac
        }
        """, 7, 4)]
    public void ComputeFileComplexity_LiteralsAndCommentsAreNotCode(
        string extension, string source, int longestFunction, int branches)
    {
        var data = VitalsAnalyzer.ComputeFileComplexity(extension, source);

        data.MaxNestingDepth.ShouldBe(2);
        data.FunctionCount.ShouldBe(1);
        data.LongestFunction.ShouldBe(longestFunction);
        data.BranchCount.ShouldBe(branches);
    }

    [Theory]
    [InlineData("int main(int argc, char **argv)", true)]
    [InlineData("static int  max_of(int a, int b)", true)]
    [InlineData("Counter() : count_(0)", true)]
    [InlineData("int next() const", true)]
    [InlineData("void move(Foo&& other) noexcept", true)]
    [InlineData("auto size() const -> std::size_t", true)]
    [InlineData("bool operator==(const Foo& other) const", true)]
    [InlineData("Foo::~Foo()", true)]
    [InlineData("if (x)", false)]
    [InlineData("else if (x)", false)]
    [InlineData("while (n--)", false)]
    [InlineData("switch (k)", false)]
    [InlineData("struct Point", false)]
    [InlineData("namespace app", false)]
    [InlineData("auto cb = [](int x)", false)]
    [InlineData("int table[]", false)]
    public void IsFunctionDeclarator_RecognizesCFunctionHeads(string head, bool expected)
        => BraceBlockScanner.IsFunctionDeclarator(head).ShouldBe(expected);

    /// <summary>Every extension the graph indexes for these languages is measured with its language's rules.</summary>
    [Theory]
    [InlineData(".c")]
    [InlineData(".h")]
    [InlineData(".cpp")]
    [InlineData(".cc")]
    [InlineData(".cxx")]
    [InlineData(".hpp")]
    [InlineData(".hxx")]
    [InlineData(".hh")]
    public void ComputeFileComplexity_CFamilyExtensions_UseCRules(string extension)
    {
        var data = VitalsAnalyzer.ComputeFileComplexity(extension, """
            #define OPEN {
            int f(int x)
            {
                if (x) { return '}'; }
                return 0;
            }
            """);

        data.MaxNestingDepth.ShouldBe(2);
        data.FunctionCount.ShouldBe(1);
        data.LongestFunction.ShouldBe(5);
        data.BranchCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(".sh")]
    [InlineData(".bash")]
    [InlineData(".zsh")]
    public void ComputeFileComplexity_ShellExtensions_UseShellRules(string extension)
    {
        var data = VitalsAnalyzer.ComputeFileComplexity(extension, """
            f() {
              if true; then echo "}"; fi
            }
            """);

        data.MaxNestingDepth.ShouldBe(2);
        data.FunctionCount.ShouldBe(1);
        data.LongestFunction.ShouldBe(3);
        data.BranchCount.ShouldBe(1);
    }

    /// <summary>
    /// sc-26200: vitals cover exactly the C/C++, Go, Ruby, PHP and shell files graph discovery
    /// indexes; files under skip patterns get no metrics.
    /// </summary>
    [Fact]
    public async Task ComputeMetricsAsync_CoversTheNewLanguagesGraphDiscoveryIndexes()
    {
        var store = new InMemoryGraphStore();
        var repoPath = Path.Combine(Path.GetTempPath(), "codegraph-vitals-tests", Guid.NewGuid().ToString("N"), "TestProject");
        Directory.CreateDirectory(repoPath);
        try
        {
            string[] expected =
            [
                "native/a.c", "native/a.h", "native/b.cpp", "native/b.cc", "native/b.cxx",
                "native/b.hpp", "native/b.hxx", "native/b.hh", "cmd/main.go", "lib/a.rb",
                "web/index.php", "scripts/a.sh", "scripts/b.bash", "scripts/c.zsh"
            ];
            foreach (var relPath in expected.Concat(["build/gen/gen.c", "web/node_modules/pkg/x.php", "obj/tool.go"]))
            {
                var fullPath = Path.Combine(repoPath, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, "\n");
            }

            var pipeline = new IndexingPipeline(
                store,
                [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
                Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
                new LocalFileSystem(),
                NullLogger<IndexingPipeline>.Instance);
            await pipeline.IndexProjectAsync("TestProject", repoPath, ct: CancellationToken.None);
            store.Nodes.Where(node => node.Label == NodeLabel.File).Select(node => node.FilePath)
                .ShouldBe(expected, ignoreOrder: true);

            var analyzer = new VitalsAnalyzer(
                store,
                new NoAnalysisProviders(),
                Options.Create(new AnalysisOptions()),
                new LocalFileSystem(),
                new NoLint(),
                new LintResultCache(),
                new DiagnosticDetailCache(),
                NullLogger<VitalsAnalyzer>.Instance);
            await analyzer.ComputeMetricsAsync("TestProject", repoPath);

            (await store.GetFileMetricsAsync("TestProject")).Select(metric => metric.FilePath)
                .ShouldBe(expected, ignoreOrder: true);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(repoPath)!, recursive: true);
        }
    }

    private sealed class NoLint : ILintRunner
    {
        public Task<IReadOnlyDictionary<string, LintResult>> LintProjectAsync(string repoPath, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, LintResult>>(new Dictionary<string, LintResult>());
    }

    private sealed class NoAnalysisProviders : IAnalysisProviderRegistry
    {
        public IAnalysisModelProvider GetProvider(string? providerName = null) => throw new NotSupportedException();
    }
}
