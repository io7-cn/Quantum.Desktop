using System.Net;

namespace Quantum.Marketplace;

internal sealed class OfflinePlatformHttpHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            RequestMessage = request,
            Content = new StringContent("Quantum Preview is offline. Configure QUANTUM_PREVIEW_PLATFORM_URL for a separate test platform.")
        });
}
