using Shouldly;
using TC.CodeGraphApi.Extractors.CSharp;

namespace TC.CodeGraphApi.Tests.Extractors;

public class NuGetReferenceExtractorTests
{
    private readonly NuGetReferenceExtractor _extractor = new();

    [Fact]
    public void Extracts_PackageReferences()
    {
        var csproj = CreateTempCsproj("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="TC.OrdersApi.Models" Version="1.2.3" />
                <PackageReference Include="Dapper" Version="2.1.0" />
              </ItemGroup>
            </Project>
            """);

        try
        {
            var refs = _extractor.ExtractFromProject(csproj);

            refs.Count.ShouldBe(2);
            refs.ShouldContain(r => r.PackageName == "TC.OrdersApi.Models" && r.Version == "1.2.3");
            refs.ShouldContain(r => r.PackageName == "Dapper" && r.Version == "2.1.0");
        }
        finally
        {
            File.Delete(csproj);
        }
    }

    [Fact]
    public void Handles_EmptyProject()
    {
        var csproj = CreateTempCsproj("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        try
        {
            var refs = _extractor.ExtractFromProject(csproj);
            refs.Count.ShouldBe(0);
        }
        finally
        {
            File.Delete(csproj);
        }
    }

    [Fact]
    public void Handles_MissingVersion()
    {
        var csproj = CreateTempCsproj("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="SomePackage" />
              </ItemGroup>
            </Project>
            """);

        try
        {
            var refs = _extractor.ExtractFromProject(csproj);
            refs.Count.ShouldBe(1);
            refs[0].PackageName.ShouldBe("SomePackage");
            refs[0].Version.ShouldBe("");
        }
        finally
        {
            File.Delete(csproj);
        }
    }

    [Fact]
    public void Skips_Empty_Include()
    {
        var csproj = CreateTempCsproj("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="" Version="1.0.0" />
                <PackageReference Include="ValidPackage" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """);

        try
        {
            var refs = _extractor.ExtractFromProject(csproj);
            refs.Count.ShouldBe(1);
            refs[0].PackageName.ShouldBe("ValidPackage");
        }
        finally
        {
            File.Delete(csproj);
        }
    }

    [Fact]
    public void Extracts_MultipleItemGroups()
    {
        var csproj = CreateTempCsproj("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="PackageA" Version="1.0.0" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Include="PackageB" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """);

        try
        {
            var refs = _extractor.ExtractFromProject(csproj);
            refs.Count.ShouldBe(2);
        }
        finally
        {
            File.Delete(csproj);
        }
    }

    private static string CreateTempCsproj(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.csproj");
        File.WriteAllText(path, content);
        return path;
    }
}
