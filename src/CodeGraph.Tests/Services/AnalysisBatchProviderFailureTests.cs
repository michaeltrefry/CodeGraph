using System.Net;
using System.Text;
using CodeGraph.Services.Analyzers;
using CodeGraph.Services.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public class AnalysisBatchProviderFailureTests
{
    [Fact]
    public async Task OpenAi_TerminalStatusIncludesProviderError()
    {
        var provider = OpenAi(new StubHandler(_ => Json("""{"id":"batch","status":"failed","errors":{"data":[{"message":"invalid batch input"}]}}""")));
        var result = await provider.GetBatchStatusAsync("batch");
        result.IsFailed.ShouldBeTrue();
        result.Error!.ShouldContain("invalid batch input");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OpenAi_ReadsErrorFile_AndPreservesSuccessfulAndMalformedLines(bool hasOutputFile)
    {
        var paths = new List<string>();
        var provider = OpenAi(new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            if (path.EndsWith("/batches/batch"))
                return Json(hasOutputFile
                    ? """{"id":"batch","status":"completed","output_file_id":"good","error_file_id":"bad"}"""
                    : """{"id":"batch","status":"completed","error_file_id":"bad"}""");
            if (path.EndsWith("/good/content"))
                return Json("""{"custom_id":"good","response":{"status_code":200,"body":{"model":"stub","choices":[{"message":{"content":"useful output"}}]}}}""");
            return Json("""
                {"custom_id":"bad","error":{"code":"batch_expired","message":"request expired"}}
                not json
                {"custom_id":"http-bad","response":{"status_code":400,"body":{"error":{"message":"input rejected"}}}}
                """);
        }));

        var results = await provider.GetBatchResultsAsync("batch");

        results.Single(r => r.CustomId == "bad").Error!.ShouldContain("request expired");
        results.Single(r => r.CustomId == "http-bad").Error!.ShouldContain("input rejected");
        results.Single(r => r.CustomId == "").Error!.ShouldContain("Malformed provider result");
        paths.ShouldContain("/v1/files/bad/content");
        if (hasOutputFile)
        {
            results.Single(r => r.CustomId == "good").Text.ShouldBe("useful output");
            results.Single(r => r.CustomId == "good").Status.ShouldBe("succeeded");
        }
    }

    [Fact]
    public async Task Anthropic_ItemErrorSurvivesMalformedNeighbor()
    {
        var provider = Anthropic(new StubHandler(_ => Json("""
            {"custom_id":"bad","result":{"type":"errored","error":{"type":"error","error":{"type":"invalid_request_error","message":"input too large"}}}}
            {broken
            {"custom_id":"good","result":{"type":"succeeded","message":{"model":"stub","content":[{"type":"text","text":"valid output"}]}}}
            """)));

        var results = await provider.GetBatchResultsAsync("batch");

        results.Single(r => r.CustomId == "bad").Error!.ShouldContain("input too large");
        results.Single(r => r.CustomId == "").Error!.ShouldContain("Malformed provider result");
        results.Single(r => r.CustomId == "good").Text.ShouldBe("valid output");
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task HttpStatusError_IncludesProviderResponseBody(string providerName)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("provider validation detail")
        });
        IAnalysisModelProvider provider = providerName == "openai" ? OpenAi(handler) : Anthropic(handler);

        var exception = await Should.ThrowAsync<HttpRequestException>(() => provider.GetBatchStatusAsync("batch"));

        exception.Message.ShouldContain("provider validation detail");
        exception.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static OpenAiAnalysisProvider OpenAi(HttpMessageHandler handler) => new(
        new ClientFactory(new HttpClient(handler)), Options.Create(new AnalysisOptions()), NullLogger<OpenAiAnalysisProvider>.Instance);

    private static AnthropicAnalysisProvider Anthropic(HttpMessageHandler handler) => new(
        new ClientFactory(new HttpClient(handler)), new AnthropicCircuitBreaker(NullLogger<AnthropicCircuitBreaker>.Instance),
        Options.Create(new AnalysisOptions()), NullLogger<AnthropicAnalysisProvider>.Instance);

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name = "") => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
