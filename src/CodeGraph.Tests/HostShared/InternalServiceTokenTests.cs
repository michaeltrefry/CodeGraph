using CodeGraph.Host.Shared.Auth;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.HostShared;

public class InternalServiceTokenTests
{
    [Fact]
    public void CreateToken_RoundTripsThroughValidator()
    {
        var options = Options.Create(new InternalServiceAuthOptions
        {
            HmacKey = "test-key-with-enough-entropy",
            Issuer = "codegraph-tests",
            TokenLifetimeSeconds = 60
        });
        var factory = new InternalServiceTokenFactory(options);
        var validator = new InternalServiceTokenValidator(options);

        var token = factory.CreateToken("Michael", "codegraph-indexer");

        var result = validator.ValidateToken(token, "codegraph-indexer");
        result.IsValid.ShouldBeTrue(result.Error);
        result.Principal!.Identity!.Name.ShouldBe("michael");
        result.Principal.FindFirst("codegraph_internal")!.Value.ShouldBe("true");
    }

    [Fact]
    public void ValidateToken_RejectsWrongAudience()
    {
        var options = Options.Create(new InternalServiceAuthOptions
        {
            HmacKey = "test-key-with-enough-entropy",
            Issuer = "codegraph-tests"
        });
        var factory = new InternalServiceTokenFactory(options);
        var validator = new InternalServiceTokenValidator(options);

        var token = factory.CreateToken("michael", "codegraph-memory");

        var result = validator.ValidateToken(token, "codegraph-indexer");
        result.IsValid.ShouldBeFalse();
        result.Error.ShouldBe("Internal service token audience mismatch.");
    }

    [Fact]
    public void CreateToken_ThrowsWhenEnabledWithoutHmacKey()
    {
        var factory = new InternalServiceTokenFactory(Options.Create(new InternalServiceAuthOptions()));

        var ex = Should.Throw<InvalidOperationException>(() => factory.CreateToken("michael", "codegraph-indexer"));
        ex.Message.ShouldContain("HmacKey");
    }
}
