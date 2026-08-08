using CodeGraph.Api.Auth;
using CodeGraph.Api.Controllers;
using CodeGraph.Data;
using CodeGraph.Models.Requests;
using CodeGraph.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeGraph.Tests.Controllers;

public class AdminLogsControllerTests
{
    [Fact]
    public async Task List_UsesFixedHundredRowPageAndNormalizedFilters()
    {
        var store = new RecordingApplicationLogStore
        {
            Result = new ApplicationLogPage(
            [
                new ApplicationLogEntryEntity
                {
                    Id = 7,
                    OccurredAtUtc = new DateTime(2026, 8, 7, 12, 0, 0),
                    Level = "Error",
                    Source = "CodeGraph.Api@host",
                    Category = "CodeGraph.Api.Controllers.SampleController",
                    EventId = 12,
                    Message = "Something failed"
                }
            ],
            201)
        };
        var controller = new AdminLogsController(store);

        var result = await controller.List(new AdminApplicationLogQueryRequest
        {
            Page = 2,
            Level = " Error ",
            Search = " failed ",
            Start = new DateTimeOffset(2026, 8, 7, 8, 0, 0, TimeSpan.FromHours(-4)),
            End = new DateTimeOffset(2026, 8, 7, 13, 0, 0, TimeSpan.Zero)
        }, CancellationToken.None);

        var response = result.Result.ShouldBeOfType<OkObjectResult>()
            .Value.ShouldBeOfType<ApplicationLogPageResponse>();
        response.Page.ShouldBe(2);
        response.PageSize.ShouldBe(100);
        response.TotalPages.ShouldBe(3);
        response.Entries.Single().OccurredAtUtc.Kind.ShouldBe(DateTimeKind.Utc);
        store.Query.ShouldNotBeNull();
        store.Query.PageSize.ShouldBe(100);
        store.Query.Level.ShouldBe("Error");
        store.Query.Search.ShouldBe("failed");
        store.Query.StartUtc.ShouldBe(new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(0, null, null)]
    [InlineData(1, "Verbose", null)]
    public async Task List_RejectsInvalidPageOrLevel(int page, string? level, string? search)
    {
        var controller = new AdminLogsController(new RecordingApplicationLogStore());

        var result = await controller.List(new AdminApplicationLogQueryRequest
        {
            Page = page,
            Level = level,
            Search = search
        }, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task List_RejectsReversedRange()
    {
        var controller = new AdminLogsController(new RecordingApplicationLogStore());

        var result = await controller.List(new AdminApplicationLogQueryRequest
        {
            Start = DateTimeOffset.UtcNow,
            End = DateTimeOffset.UtcNow.AddHours(-1)
        }, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Controller_RequiresAdminPolicy()
    {
        var authorize = typeof(AdminLogsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>()
            .Single();

        authorize.Policy.ShouldBe(CodeGraphAuthenticationDefaults.AdminPolicy);
    }

    private sealed class RecordingApplicationLogStore : IApplicationLogStore
    {
        public ApplicationLogQuery? Query { get; private set; }
        public ApplicationLogPage Result { get; init; } = new([], 0);

        public Task WriteBatchAsync(IReadOnlyList<ApplicationLogEntryEntity> entries, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<ApplicationLogPage> QueryAsync(ApplicationLogQuery query, CancellationToken cancellationToken = default)
        {
            Query = query;
            return Task.FromResult(Result);
        }

        public Task<int> DeleteBeforeAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}

public class ClientErrorsControllerTests
{
    [Fact]
    public void Report_LogsValidBrowserErrorAtErrorLevel()
    {
        var logger = new RecordingLogger<ClientErrorsController>();
        var controller = new ClientErrorsController(logger);

        var result = controller.Report(new ClientErrorReportRequest
        {
            Message = "Cannot read properties of undefined",
            Stack = "at AdminLogsComponent.load",
            Url = "https://codegraph.example/settings/logs",
            UserAgent = "test-browser"
        });

        result.ShouldBeOfType<AcceptedResult>();
        logger.Entries.Single().Level.ShouldBe(LogLevel.Error);
        logger.Entries.Single().Message.ShouldContain("Cannot read properties of undefined");
        logger.Entries.Single().Message.ShouldContain("settings/logs");
    }

    [Fact]
    public void Report_RejectsMissingMessage()
    {
        var controller = new ClientErrorsController(new RecordingLogger<ClientErrorsController>());

        var result = controller.Report(new ClientErrorReportRequest());

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Controller_RequiresAuthenticatedUserPolicy()
    {
        var authorize = typeof(ClientErrorsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>()
            .Single();

        authorize.Policy.ShouldBe(CodeGraphAuthenticationDefaults.UserPolicy);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
