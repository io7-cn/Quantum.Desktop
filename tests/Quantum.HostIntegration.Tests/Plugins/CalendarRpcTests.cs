using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NOF.Hosting;
using NOF.Infrastructure;
using NOF.Infrastructure.EntityFrameworkCore;
using Quantum.OfficialPlugins.Calendar;
using Quantum.OfficialPlugins.Calendar.Application;
using Quantum.OfficialPlugins.Calendar.Domain;
using Quantum.OfficialPlugins.Ledger.Rpc;
using Xunit;

namespace Quantum.HostIntegration.Tests.Plugins;

public sealed class CalendarRpcTests
{
    [Fact]
    public async Task ResourceCalendarCatalogAndOperationsWorkWithShippedMigrations()
    {
        await using var connection = new SqliteConnection($"Data Source=calendar-rpc-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await connection.OpenAsync();
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "calendar-migrations"), "*.sql").Order(StringComparer.Ordinal))
        {
            await using var migration = connection.CreateCommand();
            migration.CommandText = await File.ReadAllTextAsync(file);
            await migration.ExecuteNonQueryAsync();
        }

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.Clear();
        builder.Services.AddNOFApplication();
        builder.AddNOFEntityFrameworkCore();
        builder.UseDbContext<LedgerTestDb>().WithTenantMode(TenantMode.SharedDatabase).WithConnectionString(connection.ConnectionString).WithOptions((options, cs) => options.UseSqlite(cs).EnableServiceProviderCaching(false));
        CalendarPluginInitializer.Initialize(builder.Services);
        builder.Services.AddLogging();
        builder.Services.AddSingleton(TimeProvider.System);
        await using var host = new OfficialRpcHarness(builder.Services, "quantum.plugin.calendar", typeof(ICalendarRpcService).Assembly);
        await host.AssertCatalog("quantum.plugin.calendar", "calendar", ["entry.list", "entry.get", "event.list", "event.get", "event.create", "event.update", "event.delete", "task.list", "task.get", "task.create", "task.update", "task.delete", "task.set-completed", "mobile-page.refresh"]);
        var request = new SaveCalendarTaskRequest
        {
            Title = "RPC task",
            Notes = "note",
            Date = new(2026, 10, 19),
            StartTime = new(12, 0),
            Style = "green"
        };
        var created = OfficialRpcHarness.Value(await host.Call("calendar.task.create", request));
        var id = created.GetProperty("id").GetGuid();
        var range = new ListCalendarEntriesRequest
        {
            StartDate = new(2026, 10, 1),
            EndDate = new(2026, 10, 31)
        };
        Assert.Equal(1, OfficialRpcHarness.Value(await host.Call("calendar.entry.list", range)).GetArrayLength());
        Assert.Equal(1, OfficialRpcHarness.Value(await host.Call("calendar.task.list", new ListCalendarTasksRequest { IncludeCompleted = false })).GetArrayLength());
        OfficialRpcHarness.Value(await host.Call("calendar.task.update", new UpdateCalendarTaskRequest { Id = id, Entry = request with { Title = "Updated" } }));
        var read = OfficialRpcHarness.Value(await host.Call("calendar.entry.get", new CalendarEntryRequest { Id = id }));
        await using var ledgerConnection = new SqliteConnection($"Data Source=calendar-ledger-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await ledgerConnection.OpenAsync();
        await LedgerPersistenceTests.Migrate(ledgerConnection);
        var ledgerBuilder = LedgerPersistenceTests.Configure(ledgerConnection.ConnectionString);
        await using var ledgerHost = new OfficialRpcHarness(ledgerBuilder.Services, "quantum.plugin.ledger", typeof(ILedgerRpcService).Assembly);
        await ledgerHost.AssertCatalog("quantum.plugin.ledger", "ledger", ["by-calendar-entry", "create", "delete", "get", "list", "mobile-create", "mobile-refresh", "related", "summary", "update"]);
        var calendarEntryId = read.GetProperty("entry").GetProperty("id").GetGuid();
        var expense = OfficialRpcHarness.Value(await ledgerHost.Call("ledger.create", new { date = "2026-10-19", kind = "Expense", amount = 12.50m, category = "Travel", calendarEntryIds = new[] { calendarEntryId } }));
        var linked = OfficialRpcHarness.Value(await ledgerHost.Call("ledger.by-calendar-entry", new { calendarEntryId }));
        Assert.Equal(expense.GetProperty("id").GetGuid(), Assert.Single(linked.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal("Updated", read.GetProperty("entry").GetProperty("title").GetString());
        OfficialRpcHarness.Value(await host.Call("calendar.task.set-completed", new SetCalendarEntryCompletedRequest { Id = id, IsCompleted = true }));
        Assert.Equal(0, OfficialRpcHarness.Value(await host.Call("calendar.task.list", new ListCalendarTasksRequest { IncludeCompleted = false })).GetArrayLength());
        Assert.True((await host.Call("calendar.task.delete", new CalendarEntryRequest { Id = id }, false)).GetProperty("isSuccess").GetBoolean());
        var missing = await host.RequestAsync("calendar.entry.get", new CalendarEntryRequest { Id = id });
        Assert.Equal(QuantumRequestStatus.Pass, Assert.Single(missing.Value!.Outcomes).Status);
        var invalid = await host.Call("calendar.task.create", request with { Title = " " });
        Assert.Equal("calendar_invalid_request", invalid.GetProperty("errorCode").GetString());
        var originalCulture = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("zh-CN");
            var englishFailure = await host.Call("calendar.task.create", request with { Title = new string('x', 121) });
            Assert.Equal("calendar_invalid_request", englishFailure.GetProperty("errorCode").GetString());
            Assert.Equal("Titles cannot exceed 120 characters.", englishFailure.GetProperty("message").GetString());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = originalCulture;
        }

        var notFound = await host.RequestAsync("calendar.task.update", new UpdateCalendarTaskRequest { Id = id, Entry = request });
        Assert.Equal(QuantumRequestStatus.Pass, Assert.Single(notFound.Value!.Outcomes).Status);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Call("calendar.entry.list", range, cancellationToken: cancelled.Token));
        await host.UseClient<ICalendarRpcServiceClient>(async client =>
        {
            var context = NOF.Contract.Context.Empty;
            var local = await client.CreateTaskAsync(request, context);
            Assert.True(local.IsSuccess, local.Message);
            var localId = local.Value!.Id;
            var invalidUpdate = await client.UpdateTaskAsync(new() { Id = localId, Entry = request with { Title = " " } }, context);
            Assert.False(invalidUpdate.IsSuccess);
            Assert.Equal(request.Title, (await client.GetEntryAsync(new() { Id = localId }, context)).Value!.Entry!.Title);
            Assert.True((await client.SetTaskCompletedAsync(new() { Id = localId, IsCompleted = true }, context)).IsSuccess);
            Assert.Empty((await client.ListTasksAsync(new() { IncludeCompleted = false }, context)).Value!);
            Assert.True((await client.GetEntryAsync(new() { Id = localId }, context)).Value!.Entry!.IsCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DeleteTaskAsync(new() { Id = localId }, context, cancelled.Token));
            Assert.True((await client.DeleteTaskAsync(new() { Id = localId }, context)).IsSuccess);
            Assert.Null((await client.GetEntryAsync(new() { Id = localId }, context)).Value!.Entry);
        });
    }
    [Fact]
    public async Task RepeatingEventsExpandAcrossHostJsonBoundaryAndRejectWrongResourceWrites()
    {
        await using var connection = new SqliteConnection($"Data Source=calendar-repeat-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await connection.OpenAsync();
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "calendar-migrations"), "*.sql").Order(StringComparer.Ordinal))
        {
            await using var migration = connection.CreateCommand();
            migration.CommandText = await File.ReadAllTextAsync(file);
            await migration.ExecuteNonQueryAsync();
        }

        var builder = LedgerPersistenceTests.Configure(connection.ConnectionString);
        CalendarPluginInitializer.Initialize(builder.Services);
        await using var host = new OfficialRpcHarness(builder.Services, "quantum.plugin.calendar", typeof(ICalendarRpcService).Assembly);
        var created = OfficialRpcHarness.Value(await host.Call("calendar.event.create", new
        {
            title = "Planning",
            notes = "",
            date = "2026-10-30",
            startTime = "09:00:00",
            endTime = "09:30:00",
            style = "blue",
            repeatPattern = "Weekly",
            repeatInterval = 2,
            repeatWeekdays = "Monday, Friday",
            repeatCount = 5
        }));
        var id = created.GetProperty("id").GetGuid();
        var occurrences = OfficialRpcHarness.Value(await host.Call("calendar.event.list", new { startDate = "2026-11-01", endDate = "2026-11-30" }));
        Assert.Equal(new[] { "2026-11-09", "2026-11-13", "2026-11-23", "2026-11-27" }, occurrences.EnumerateArray().Select(item => item.GetProperty("date").GetString()));
        Assert.All(occurrences.EnumerateArray(), item =>
        {
            Assert.Equal(id, item.GetProperty("id").GetGuid());
            Assert.Equal("2026-10-30", item.GetProperty("seriesStartDate").GetString());
        });
        var wrongResource = await host.RequestAsync("calendar.task.delete", new { id });
        Assert.Equal(QuantumRequestStatus.Pass, Assert.Single(wrongResource.Value!.Outcomes).Status);
        var wrongCompletion = await host.RequestAsync("calendar.task.set-completed", new { id, isCompleted = true });
        Assert.Equal(QuantumRequestStatus.Pass, Assert.Single(wrongCompletion.Value!.Outcomes).Status);
        Assert.Equal("2026-10-30", OfficialRpcHarness.Value(await host.Call("calendar.event.get", new { id })).GetProperty("entry").GetProperty("date").GetString());
        Assert.True((await host.Call("calendar.event.delete", new { id }, false)).GetProperty("isSuccess").GetBoolean());
        Assert.Empty(OfficialRpcHarness.Value(await host.Call("calendar.event.list", new { startDate = "2026-11-01", endDate = "2026-11-30" })).EnumerateArray());
        var oldName = await host.RequestAsync("calendar.create", new { });
        Assert.True(oldName.IsSuccess);
        Assert.Empty(oldName.Value!.Outcomes);
    }
}
