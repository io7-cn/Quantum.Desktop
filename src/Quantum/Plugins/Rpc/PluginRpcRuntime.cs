using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NOF.Application;
using NOF.Contract;
using NOF.Infrastructure;

namespace Quantum.Plugins;

internal sealed class PluginRpcRuntime : IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PluginRpcSerializer _serializer;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private IReadOnlyList<PluginRpcMethodDefinition> _methods = [];
    private TaskCompletionSource? _idle;
    private int _activeInvocations;
    private int _state;

    private PluginRpcRuntime(
        string pluginId,
        Guid runtimeId,
        IServiceScopeFactory scopeFactory,
        PluginRpcSerializer serializer,
        ILogger logger)
    {
        PluginId = pluginId;
        RuntimeId = runtimeId;
        _scopeFactory = scopeFactory;
        _serializer = serializer;
        _logger = logger;
    }

    public string PluginId { get; }

    public Guid RuntimeId { get; }

    public IReadOnlyList<PluginRpcMethodDefinition> Methods => _methods;

    public bool IsActive => Volatile.Read(ref _state) == (int)RpcRuntimeState.Active;

    internal IDisposable AcquireMobileOwnerLease()
    {
        if (!TryBeginInvocation()) throw new InvalidOperationException("The mobile page owner is stopping.");
        return new MobileOwnerLease(this);
    }

    private sealed class MobileOwnerLease(PluginRpcRuntime runtime) : IDisposable
    {
        public void Dispose() => runtime.EndInvocation();
    }

    public static PluginRpcRuntime Create(
        string pluginId,
        Guid runtimeId,
        Assembly assembly,
        IServiceScopeFactory scopeFactory,
        PluginRpcSerializer serializer,
        ILogger logger,
        IEnumerable<string>? providedCapabilities = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(logger);

        var runtime = new PluginRpcRuntime(
            pluginId,
            runtimeId,
            scopeFactory,
            serializer,
            logger);
        runtime._methods = DiscoverMethods(assembly, pluginId, serializer);
        if (providedCapabilities is not null)
        {
            var declared = providedCapabilities.ToHashSet(StringComparer.Ordinal);
            var implemented = runtime._methods.Select(method => method.Capability.Name).ToHashSet(StringComparer.Ordinal);
            if (!declared.SetEquals(implemented))
            {
                throw new InvalidOperationException(
                    $"Plugin '{pluginId}' capability declarations do not match its RPC methods. "
                    + $"Missing methods: {string.Join(", ", declared.Except(implemented).Order(StringComparer.Ordinal))}. "
                    + $"Missing manifest entries: {string.Join(", ", implemented.Except(declared).Order(StringComparer.Ordinal))}.");
            }
        }

        return runtime;
    }

    public void Resume()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state == (int)RpcRuntimeState.Disposed, this);
            _state = (int)RpcRuntimeState.Active;
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken)
    {
        Task wait;
        lock (_gate)
        {
            if (_state == (int)RpcRuntimeState.Disposed)
            {
                return Task.CompletedTask;
            }

            _state = (int)RpcRuntimeState.Paused;
            if (_activeInvocations == 0)
            {
                return Task.CompletedTask;
            }

            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wait = _idle.Task;
        }

        return wait.WaitAsync(cancellationToken);
    }

    public async Task<PluginRpcDispatchResult> InvokeAsync(
        PluginRpcMethodDefinition method,
        JsonElement payload,
        PluginRpcCallContext callContext,
        CancellationToken cancellationToken)
    {
        if (!TryBeginInvocation())
        {
            return PluginRpcDispatchResult.Fail(
                PluginRpcErrors.NotFound,
                "The requested RPC service is not currently available.");
        }

        try
        {
            object? request;
            try
            {
                request = _serializer.Deserialize(payload, method.Mapping.RequestType);
            }
            catch (JsonException exception)
            {
                var path = exception.Path ?? "$";
                return PluginRpcDispatchResult.Fail(
                    PluginRpcErrors.InvalidPayload,
                    $"The payload for RPC '{method.Capability.Name}' is invalid at {path}: {exception.Message}",
                    new Dictionary<string, string>
                    {
                        ["rpc"] = method.Capability.Name,
                        ["path"] = path,
                        ["reason"] = exception.Message
                    });
            }
            catch (NotSupportedException exception)
            {
                return PluginRpcDispatchResult.Fail(
                    PluginRpcErrors.InvalidPayload,
                    $"The request type for RPC '{method.Capability.Name}' does not support this JSON payload: {exception.Message}");
            }

            if (request is null)
            {
                return PluginRpcDispatchResult.Fail(
                    PluginRpcErrors.InvalidPayload,
                    $"The payload for RPC '{method.Capability.Name}' is invalid at $: A non-null request is required.",
                    new Dictionary<string, string>
                    {
                        ["rpc"] = method.Capability.Name,
                        ["path"] = "$",
                        ["reason"] = "A non-null request is required."
                    });
            }

            IResult result;
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                scope.ServiceProvider.ResolveDaemonServices();
                scope.ServiceProvider.GetRequiredService<PluginRpcContextInboundMiddleware>().CallerContext = CreateTargetContext(callContext);
                var execution = await scope.ServiceProvider.GetRequiredService<RequestInboundPipelineExecutor>()
                    .ExecuteAsync(request, method.Mapping.HandlerType, method.Mapping.ReturnType,
                        method.ServiceType, method.OperationName, null, cancellationToken).ConfigureAwait(false);
                result = execution.Response ?? throw new InvalidOperationException("The NOF request pipeline returned no response.");

                try
                {
                    return PluginRpcDispatchResult.Success(
                        _serializer.Serialize(result, result.GetType()));
                }
                catch (Exception exception) when (exception is JsonException or NotSupportedException)
                {
                    _logger.LogError(
                        exception,
                        "RPC result serialization failed for {RpcName} in plugin {PluginId} runtime {RuntimeId}.",
                        method.Capability.Name,
                        PluginId,
                        RuntimeId);
                    return PluginRpcDispatchResult.Fail(
                        PluginRpcErrors.InvalidResult,
                        "The RPC result could not be serialized.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _logger.LogError(
                    exception,
                    "RPC handler {RpcHandler} failed for {RpcName} in plugin {PluginId} runtime {RuntimeId}.",
                    method.Mapping.HandlerType.FullName,
                    method.Capability.Name,
                    PluginId,
                    RuntimeId);
                return PluginRpcDispatchResult.Fail(
                    PluginRpcErrors.InvocationFailed,
                    "The RPC handler failed unexpectedly.");
            }
        }
        finally
        {
            EndInvocation();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await PauseAsync(CancellationToken.None).ConfigureAwait(false);
        lock (_gate)
        {
            _state = (int)RpcRuntimeState.Disposed;
            _methods = [];
        }
    }

    private bool TryBeginInvocation()
    {
        lock (_gate)
        {
            if (_state != (int)RpcRuntimeState.Active)
            {
                return false;
            }

            checked
            {
                _activeInvocations++;
            }

            return true;
        }
    }

    private void EndInvocation()
    {
        TaskCompletionSource? idle = null;
        lock (_gate)
        {
            _activeInvocations--;
            if (_activeInvocations == 0 && _state != (int)RpcRuntimeState.Active)
            {
                idle = _idle;
                _idle = null;
            }
        }

        idle?.TrySetResult();
    }

    private static Context CreateTargetContext(PluginRpcCallContext source)
    {
        var items = source.Items.ToDictionary(
            static item => (object)item.Key,
            static item => (object?)item.Value.Clone());
        items[QuantumRequestContext.Dispatched] = source.IsRequest;
        items[QuantumRpcContextKeys.CallerPluginId] = source.CallerPluginId;
        items[QuantumRpcContextKeys.CallerRuntimeId] = source.CallerRuntimeId;
        return Context.Empty.WithItems(items);
    }

    private static IReadOnlyList<PluginRpcMethodDefinition> DiscoverMethods(Assembly assembly, string pluginId, PluginRpcSerializer serializer)
    {
        var definitions = new List<PluginRpcMethodDefinition>();
        foreach (var serverType in GetLoadableTypes(assembly)
                     .Where(static type => !type.IsAbstract
                         && !type.ContainsGenericParameters
                         && typeof(IRpcServer).IsAssignableFrom(type))
                     .OrderBy(static type => type.FullName, StringComparer.Ordinal))
        {
            var serviceType = serverType
                    .GetProperty(
                        nameof(IRpcServerServiceType.ServiceType),
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                    ?.GetValue(null) as Type
                ?? throw new InvalidOperationException(
                    $"RPC server '{serverType.FullName}' does not expose generated ServiceType metadata.");
            var handlerMappings = serverType
                    .GetProperty(
                        nameof(IRpcServer.HandlerMappings),
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                    ?.GetValue(null) as IReadOnlyDictionary<string, RpcHandlerMapping>
                ?? throw new InvalidOperationException(
                    $"RPC server '{serverType.FullName}' does not expose generated HandlerMappings metadata.");
            if (serviceType.GetCustomAttribute<TransportOverQuantumAttribute>(inherit: false) is null)
            {
                continue;
            }

            foreach (var mapping in handlerMappings.OrderBy(
                         static item => item.Key,
                         StringComparer.Ordinal))
            {
                var contractMethod = serviceType.GetMethods()
                    .SingleOrDefault(method => string.Equals(
                            method.Name,
                            mapping.Key,
                            StringComparison.Ordinal)
                        && method.GetParameters() is [var parameter]
                        && parameter.ParameterType == mapping.Value.RequestType)
                    ?? throw new InvalidOperationException(
                        $"RPC server '{serverType.FullName}' maps unknown contract method "
                        + $"'{serviceType.FullName}.{mapping.Key}'.");
                var returnsValue = IsSupportedResult(contractMethod.ReturnType);
                var policy = contractMethod.GetCustomAttribute<RequestCapabilityAttribute>(inherit: false)
                    ?? throw new InvalidOperationException(
                        $"Quantum method '{serviceType.FullName}.{contractMethod.Name}' needs [RequestCapability].");
                if (!Enum.IsDefined(policy.Dispatch)
                    || (policy.UniqueOwner || policy.AllowStop) && policy.Dispatch != QuantumDispatch.Ordered
                    || policy.RequireSingleProvider && !policy.UniqueOwner)
                {
                    throw new InvalidOperationException($"Invalid request strategy for '{policy.Name}'.");
                }

                var capabilityName = policy.Name;
                var parameter = contractMethod.GetParameters()[0];
                var responseType = returnsValue
                    ? contractMethod.ReturnType.GetGenericArguments()[0]
                    : null;
                var hasRequestResult = contractMethod.ReturnType.IsGenericType
                    && contractMethod.ReturnType.GetGenericTypeDefinition() == typeof(QuantumRequestResult<>);

                definitions.Add(new PluginRpcMethodDefinition(
                    serviceType,
                    contractMethod.Name,
                    mapping.Value,
                    policy,
                    hasRequestResult,
                    PluginRpcCatalogMetadataReader.Describe(
                        capabilityName,
                        (string)pluginId,
                        contractMethod,
                        parameter,
                        responseType,
                        serializer)));
            }
        }

        var duplicate = definitions.GroupBy(method => method.Capability.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Capability '{duplicate.Key}' is implemented more than once in '{assembly.GetName().Name}'.");
        }

        return definitions;
    }

    private static bool IsSupportedResult(Type returnType)
    {
        if (returnType == typeof(Result))
        {
            return false;
        }

        if (returnType.IsGenericType && (returnType.GetGenericTypeDefinition() == typeof(Result<>)
            || returnType.GetGenericTypeDefinition() == typeof(QuantumRequestResult<>)))
        {
            return true;
        }

        throw new InvalidOperationException(
            $"Quantum RPC method return type '{returnType}' must be Result, Result<T>, or QuantumRequestResult<T>.");
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    private enum RpcRuntimeState
    {
        Paused,
        Active,
        Disposed
    }
}

internal sealed record PluginRpcMethodDefinition(
    Type ServiceType,
    string OperationName,
    RpcHandlerMapping Mapping,
    RequestCapabilityAttribute Policy,
    bool HasRequestResult,
    QuantumCapabilityDescription Capability);
