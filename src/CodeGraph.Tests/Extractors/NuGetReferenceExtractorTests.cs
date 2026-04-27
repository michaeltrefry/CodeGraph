using Shouldly;
using CodeGraph.Extractors.CSharp;

namespace CodeGraph.Tests.Extractors;

public class NuGetReferenceExtractorTests
{
    private readonly NuGetReferenceExtractor _extractor = new();

    [Fact]
    public void Extracts_PackageReferences()
    {
        var refs = _extractor.ExtractFromProjectXml("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="OrdersApi.Models" Version="1.2.3" />
                <PackageReference Include="Dapper" Version="2.1.0" />
              </ItemGroup>
            </Project>
            """);

        refs.Count.ShouldBe(2);
        refs.ShouldContain(r => r.PackageName == "OrdersApi.Models" && r.Version == "1.2.3");
        refs.ShouldContain(r => r.PackageName == "Dapper" && r.Version == "2.1.0");
    }

    [Fact]
    public void Handles_EmptyProject()
    {
        var refs = _extractor.ExtractFromProjectXml("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        refs.Count.ShouldBe(0);
    }

    [Fact]
    public void Handles_MissingVersion()
    {
        var refs = _extractor.ExtractFromProjectXml("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="SomePackage" />
              </ItemGroup>
            </Project>
            """);

        refs.Count.ShouldBe(1);
        refs[0].PackageName.ShouldBe("SomePackage");
        refs[0].Version.ShouldBe("");
    }

    [Fact]
    public void Skips_Empty_Include()
    {
        var refs = _extractor.ExtractFromProjectXml("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="" Version="1.0.0" />
                <PackageReference Include="ValidPackage" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """);

        refs.Count.ShouldBe(1);
        refs[0].PackageName.ShouldBe("ValidPackage");
    }

    [Fact]
    public void Extracts_MultipleItemGroups()
    {
        var refs = _extractor.ExtractFromProjectXml("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="PackageA" Version="1.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Include="PackageB" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """);

        refs.Count.ShouldBe(2);
    }
}
