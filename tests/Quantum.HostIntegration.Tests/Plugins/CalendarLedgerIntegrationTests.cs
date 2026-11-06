using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NOF.Contract;
using Quantum.OfficialPlugins.Ledger;
using Quantum.OfficialPlugins.Ledger.Rpc;
using System.Reflection;
using System.Text.Json;
using Xunit;
using CalendarPage = Quantum.OfficialPlugins.Calendar.UI.Pages.Calendar;
using LedgerPage = Quantum.OfficialPlugins.Ledger.UI.Pages.Ledger;

namespace Quantum.HostIntegration.Tests.Plugins;

public sealed class CalendarLedgerIntegrationTests
{
    [Fact]
    public async Task LinksPersistAcrossMonthsValidateAtomicallyAndWorkWithoutCalendar()
    {
        await using var connection = new SqliteConnection($"Data Source=event-links-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await connection.OpenAsync();
        await LedgerPersistenceTests.Migrate(connection);
        var builder = LedgerPersistenceTests.Configure(connection.ConnectionString);
        await using var host = new OfficialRpcHarness(builder.Services, "quantum.plugin.ledger", typeof(ILedgerRpcService).Assembly);
        var eventId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        Guid id = default;
        await host.UseClient<ILedgerRpcServiceClient>(async client =>
        {
            var saved = await client.CreateEntryAsync(new() { Date = new(2026, 9, 1), Kind = EntryKind.Expense, Amount = 12m, Category = "Food", CalendarEntryIds = [eventId, taskId, eventId] }, Context.Empty);
            Assert.True(saved.IsSuccess);
            id = saved.Value!.Id;
            Assert.Equal(2, saved.Value.CalendarEntryIds!.Length);
            var invalid = await client.UpdateEntryAsync(new() { Id = id, Entry = new() { Date = new(2026, 10, 1), Kind = EntryKind.Income, Amount = 30m, Category = "Changed", CalendarEntryIds = [Guid.Empty] } }, Context.Empty);
            Assert.False(invalid.IsSuccess);
            var unchanged = (await client.GetEntryAsync(new() { Id = id }, Context.Empty)).Value!.Entry!;
            Assert.Equal(12m, unchanged.Amount);
            Assert.Equal(2, unchanged.CalendarEntryIds!.Length);
        });
        var found = OfficialRpcHarness.Value(await host.Call("ledger.by-calendar-entry", new { calendarEntryId = eventId }));
        Assert.Equal(id, Assert.Single(found.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(2, found[0].GetProperty("calendarEntryIds").GetArrayLength());
        Assert.Empty(OfficialRpcHarness.Value(await host.Call("ledger.by-calendar-entry", new { calendarEntryId = Guid.NewGuid() })).EnumerateArray());
        var bad = await host.Call("ledger.by-calendar-entry", new { calendarEntryId = Guid.Empty });
        Assert.False(bad.GetProperty("isSuccess").GetBoolean());
        await host.UseClient<ILedgerRpcServiceClient>(async client =>
        {
            // Old clients omitting the optional field preserve links.
            Assert.True((await client.UpdateEntryAsync(new() { Id = id, Entry = new() { Date = new(2026, 11, 1), Kind = EntryKind.Income, Amount = 15m, Category = "Salary" } }, Context.Empty)).IsSuccess);
            Assert.Single((await client.ListByCalendarEntryAsync(new() { CalendarEntryId = taskId }, Context.Empty)).Value!);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.UpdateEntryAsync(new() { Id = id, Entry = new() { Date = new(2026, 11, 1), Kind = EntryKind.Income, Amount = 15m, Category = "Salary", CalendarEntryIds = [] } }, Context.Empty, cancelled.Token));
            Assert.Single((await client.ListByCalendarEntryAsync(new() { CalendarEntryId = taskId }, Context.Empty)).Value!);
        });
        OfficialRpcHarness.Value(await host.Call("ledger.update", new { id, entry = new { date = "2026-11-01", kind = "Income", amount = 15m, category = "Salary", calendarEntryIds = Array.Empty<Guid>() } }));
        Assert.Empty(OfficialRpcHarness.Value(await host.Call("ledger.by-calendar-entry", new { calendarEntryId = eventId })).EnumerateArray());
        await host.UseClient<ILedgerRpcServiceClient>(async client =>
        {
            await client.UpdateEntryAsync(new() { Id = id, Entry = new() { Date = new(2026, 11, 1), Kind = EntryKind.Income, Amount = 15m, Category = "Salary", CalendarEntryIds = [eventId] } }, Context.Empty);
            await client.DeleteEntryAsync(new() { Id = id }, Context.Empty);
            Assert.Empty((await client.ListByCalendarEntryAsync(new() { CalendarEntryId = eventId }, Context.Empty)).Value!);
        });
    }

    [Fact]
    public async Task UpgradePreservesExistingLedgerRowsWithEmptyCalendarLinks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await LedgerPersistenceTests.Migrate(connection, 3);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO OfficialLedgerEntries (Id, Date, Kind, AmountInFen, Category, Note, TenantId) VALUES ($id, '2026-09-01', 0, 1234, 'Food', 'Existing', 'tenant')";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        await command.ExecuteNonQueryAsync();
        command.CommandText = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "004_calendar_links.sql"));
        await command.ExecuteNonQueryAsync();
        command.CommandText = "SELECT AmountInFen, Note, CalendarEntryIdsJson FROM OfficialLedgerEntries";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1234, reader.GetInt64(0));
        Assert.Equal("Existing", reader.GetString(1));
        Assert.Equal("[]", reader.GetString(2));
    }

    [Theory]
    [InlineData("calendar.entry.list", true, false)]
    [InlineData("calendar.entry.get", false, true)]
    public async Task OptionalCapabilitiesAreIndependentAndDisappearWhenProviderStops(string method, bool canList, bool canGet)
    {
        var rpc = new IntegrationInvoker("partial.calendar", method);
        var page = new LedgerPage(null!, rpc);
        await Invoke(page, "RefreshCalendarCapabilitiesAsync");
        Assert.Equal(canList, Field(page, "_canListCalendar"));
        Assert.Equal(canGet, Field(page, "_canGetCalendar"));
        rpc.Available = false;
        await Invoke(page, "RefreshCalendarCapabilitiesAsync");
        Assert.Equal(false, Field(page, "_canListCalendar"));
        Assert.Equal(false, Field(page, "_canGetCalendar"));
        await Invoke(new LedgerPage(null!), "RefreshCalendarCapabilitiesAsync");
    }

    [Fact]
    public async Task OptionalCatalogFailureHidesActionsButCancellationPropagates()
    {
        var rpc = new IntegrationInvoker("partial.calendar", "calendar.entry.list")
        {
            CatalogException = new InvalidOperationException("Offline")
        };
        var page = new LedgerPage(null!, rpc);
        await Invoke(page, "RefreshCalendarCapabilitiesAsync");
        Assert.Equal(false, Field(page, "_canListCalendar"));
        rpc.CatalogException = new OperationCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(page, "RefreshCalendarCapabilitiesAsync"));
        var calendar = new CalendarPage(null!, rpc);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(calendar, "RefreshLedgerCapabilityAsync"));
    }

    [Fact]
    public async Task CalendarReadsStringLedgerKindsThroughActualHostInvoker()
    {
        await using var connection = new SqliteConnection($"Data Source=host-enums-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await connection.OpenAsync();
        await LedgerPersistenceTests.Migrate(connection);
        var builder = LedgerPersistenceTests.Configure(connection.ConnectionString);
        await using var host = new OfficialRpcHarness(builder.Services, "replacement.ledger", typeof(ILedgerRpcService).Assembly);
        var eventId = Guid.NewGuid();
        await host.UseClient<ILedgerRpcServiceClient>(async client =>
        {
            await client.CreateEntryAsync(new() { Date = new(2026, 10, 20), Kind = EntryKind.Expense, Amount = 12m, Category = "Food", CalendarEntryIds = [eventId] }, Context.Empty);
            await client.CreateEntryAsync(new() { Date = new(2026, 10, 20), Kind = EntryKind.Income, Amount = 30m, Category = "Refund", CalendarEntryIds = [eventId] }, Context.Empty);
        });
        var invoker = host.CreateInvoker();
        var wire = await invoker.RequestSingleAsync<JsonElement>("ledger.by-calendar-entry", new { calendarEntryId = eventId }, Context.Empty);
        Assert.True(wire.IsSuccess);
        Assert.Equal(new[] { "Expense", "Income" }, wire.Value.EnumerateArray().Select(row => row.GetProperty("kind").GetString()).Order().ToArray());
        var incompatible = await invoker.RequestSingleAsync<NumericLedgerProjection[]>("ledger.by-calendar-entry", new { calendarEntryId = eventId }, Context.Empty);
        Assert.False(incompatible.IsSuccess);
        Assert.Equal("rpc_invalid_result", incompatible.ErrorCode);
        Assert.Equal("The provider reply does not match the requested response type.", incompatible.Message);
        var page = new CalendarPage(null!, invoker);
        SetLocalizer<OfficialPlugins.Calendar.UI.Localization.PluginStrings>(page);
        var item = new OfficialPlugins.Calendar.Application.CalendarEntryDetails
        {
            Id = eventId,
            Title = "Trip",
            Notes = "",
            Kind = OfficialPlugins.Calendar.Domain.CalendarEntryKind.Event,
            Date = new(2026, 10, 20),
            StartTime = new(9, 0),
            EndTime = null,
            IsCompleted = false,
            Style = "blue"
        };
        await Invoke(page, "ViewLedgerAsync", item);
        Assert.Null(Field(page, "_errorMessage"));
        Assert.NotNull(Field(page, "_ledgerViewing"));
        var entries = JsonSerializer.SerializeToElement(Field(page, "_linkedLedgerEntries"));
        Assert.Equal(2, entries.GetArrayLength());
        Assert.Equal(new[] { 12m, 30m }, entries.EnumerateArray().Select(entry => entry.GetProperty("Amount").GetDecimal()).Order().ToArray());
    }

    [Fact]
    public async Task CalendarUsesLedgerCapabilityAndHandlesProviderRemoval()
    {
        var rpc = new IntegrationInvoker("alternative.finance", "ledger.by-calendar-entry");
        var page = new CalendarPage(null!, rpc);
        SetLocalizer<OfficialPlugins.Calendar.UI.Localization.PluginStrings>(page);
        var item = new OfficialPlugins.Calendar.Application.CalendarEntryDetails
        {
            Id = Guid.NewGuid(),
            Title = "Trip",
            Notes = "",
            Kind = OfficialPlugins.Calendar.Domain.CalendarEntryKind.Event,
            Date = new(2026, 10, 20),
            StartTime = new(9, 0),
            EndTime = null,
            IsCompleted = false,
            Style = "blue"
        };
        await Invoke(page, "ViewLedgerAsync", item);
        Assert.Equal("ledger.by-calendar-entry", rpc.LastCall);
        Assert.NotNull(Field(page, "_ledgerViewing"));
        var linked = JsonSerializer.SerializeToElement(Field(page, "_linkedLedgerEntries"), JsonSerializerOptions.NOF);
        Assert.Equal(12m, Assert.Single(linked.EnumerateArray()).GetProperty("amount").GetDecimal());
        rpc.Available = false;
        await Invoke(page, "ViewLedgerAsync", item);
        Assert.Equal(false, Field(page, "_canViewLedger"));
        Assert.NotNull(Field(page, "_errorMessage"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LedgerReadsCalendarCapabilitiesFromSameOrSeparateProviders(bool separateProviders)
    {
        var rpc = new IntegrationInvoker("alternative.calendar", "calendar.entry.list", "calendar.entry.get")
        {
            SeparateProviders = separateProviders
        };
        var page = new LedgerPage(null!, rpc);
        SetLocalizer<OfficialPlugins.Ledger.UI.Localization.PluginStrings>(page);
        SetField(page, "_candidateMonth", new DateOnly(2026, 10, 1));
        await Invoke(page, "LoadCalendarCandidates");
        Assert.Equal("calendar.entry.list", rpc.LastCall);
        Assert.Equal(true, Field(page, "_canListCalendar"));
        Assert.Equal(true, Field(page, "_canGetCalendar"));
        Assert.Equal("2026-10-01", rpc.LastPayload.GetProperty("startDate").GetString());
        Assert.Equal("2026-10-31", rpc.LastPayload.GetProperty("endDate").GetString());
        Assert.Single((Array)Field(page, "_calendarCandidates")!);
        var entry = new LedgerEntryDetails
        {
            Id = Guid.NewGuid(),
            Date = new(2026, 10, 1),
            Kind = EntryKind.Expense,
            Amount = 5,
            Category = "Food",
            Note = "",
            RelatedEntryIds = [],
            CalendarEntryIds = [Guid.NewGuid()]
        };
        await Invoke(page, "ViewCalendarLinks", entry);
        Assert.Equal("calendar.entry.get", rpc.LastCall);
        Assert.NotNull(Field(page, "_calendarViewing"));
        Assert.Single((System.Collections.IEnumerable)Field(page, "_calendarLinks")!);
    }

    [Fact]
    public async Task LedgerGroupsOccurrencesWithinAProviderAndRejectsConflictingProviderIds()
    {
        var rpc = new IntegrationInvoker("alternative.calendar", "calendar.entry.list") { RepeatOccurrences = true };
        var page = new LedgerPage(null!, rpc);
        SetLocalizer<OfficialPlugins.Ledger.UI.Localization.PluginStrings>(page);
        SetField(page, "_candidateMonth", new DateOnly(2026, 10, 1));
        await Invoke(page, "LoadCalendarCandidates");
        Assert.Single((Array)Field(page, "_calendarCandidates")!);
        rpc.ConflictingProvider = true;
        await Invoke(page, "LoadCalendarCandidates");
        Assert.Empty((Array)Field(page, "_calendarCandidates")!);
    }

    private sealed record NumericLedgerProjection
    {
        public required int Kind { get; set; }
    }

    private static object? Field(object page, string name) => page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page);
    private static void SetField(object page, string name, object value) => page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
    private static Task Invoke(object page, string name, params object[] args) => (Task)page.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args)!;
    private static void SetLocalizer<T>(object page)
    {
        var factory = new ResourceManagerStringLocalizerFactory(Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance);
        page.GetType().GetProperty("L", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, new StringLocalizer<T>(factory));
    }

    private sealed class IntegrationInvoker : IQuantumRequester
    {
        private readonly string _provider;
        private readonly string[] _methods;
        public IntegrationInvoker(string provider, params string[] methods)
        {
            _provider = provider;
            _methods = methods;
        }

        public bool Available { get; set; } = true;
        public bool RepeatOccurrences { get; set; }
        public bool ConflictingProvider { get; set; }
        public bool SeparateProviders { get; set; }
        public Exception? CatalogException { get; set; }
        public string? LastCall { get; private set; }
        public JsonElement LastPayload { get; private set; }

        public Task<Result<QuantumRequestReport<T>>> RequestAsync<T>(string rpcName, object payload, Context context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rpcName == QuantumCapabilityCatalog.CapabilityName && CatalogException is not null)
            {
                throw CatalogException;
            }

            if (!Available)
            {
                return Task.FromResult<Result<QuantumRequestReport<T>>>(Result.Fail("unavailable", "Provider stopped"));
            }

            object response;
            if (rpcName == QuantumCapabilityCatalog.CapabilityName)
            {
                response = new QuantumCapabilityCatalog(2, _methods.Select((name, index) => new QuantumCapabilityDescription(name, QuantumCapabilityKind.Request, 1, null, [SeparateProviders ? _provider + index : _provider], null, null, null)).ToArray());
            }
            else
            {
                LastCall = rpcName;
                LastPayload = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.NOF);
                response = rpcName.EndsWith("calendar.entry.list", StringComparison.Ordinal) ? new[]
                {
                    new
                    {
                        id = Guid.NewGuid(),
                        title = "Task",
                        date = "2026-10-20",
                        kind = "Task",
                        notes = "",
                        startTime = "09:00:00",
                        endTime = (string? )null,
                        isCompleted = false
                    }
                }

                : rpcName.EndsWith("calendar.entry.get", StringComparison.Ordinal) ? new
                {
                    entry = (object?)null
                }

                : new[]
                {
                    new
                    {
                        id = Guid.NewGuid(),
                        date = "2026-09-01",
                        kind = JsonSerializer.SerializeToElement("Expense"),
                        amount = 12m,
                        category = "Food",
                        note = "Lunch"
                    }
                };
            }

            if (RepeatOccurrences && rpcName == "calendar.entry.list")
            {
                var occurrence = JsonSerializer.SerializeToElement(response, JsonSerializerOptions.NOF)[0];
                response = new[] { occurrence, occurrence };
            }

            var result = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(response, JsonSerializerOptions.NOF), JsonSerializerOptions.NOF);
            var outcomes = new List<QuantumRequestOutcome<T>> { new(_provider, QuantumRequestStatus.Handled, result, null, null) { HasValue = true } };
            if (ConflictingProvider && rpcName == "calendar.entry.list")
            {
                outcomes.Add(new("conflicting.calendar", QuantumRequestStatus.Handled, result, null, null) { HasValue = true });
            }

            return Task.FromResult(Result.Success(new QuantumRequestReport<T>(outcomes, false)));
        }
    }
}
