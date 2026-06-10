using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentOrion.Core.Configuration;
using AgentOrion.Core.Operations;
using Microsoft.Extensions.Options;

namespace AgentOrion.Infrastructure.Operations;

public sealed class HttpAwbReservationGateway : IAwbReservationGateway
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly AwbApiOptions _options;

    public HttpAwbReservationGateway(HttpClient httpClient, IOptions<AgentOrionOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value.Operations.AwbApi;
        ConfigureClient();
    }

    public Task<AwbReservationOperationResult> CreateReservationAsync(AwbReservationCreateRequest request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, _options.CreatePath, request, ct);

    public Task<AwbReservationOperationResult> GetReservationAsync(string awbNumber, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, ExpandAwbPath(_options.GetPath, awbNumber), body: null, ct);

    public Task<AwbReservationOperationResult> UpdateReservationStatusAsync(AwbReservationStatusUpdateRequest request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, ExpandAwbPath(_options.UpdateStatusPath, request.AwbNumber), new { request.Status }, ct);

    public Task<AwbReservationOperationResult> CancelReservationAsync(AwbReservationCancelRequest request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, ExpandAwbPath(_options.CancelPath, request.AwbNumber), new { request.Reason }, ct);

    private async Task<AwbReservationOperationResult> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        try
        {
            using var message = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                message.Content = JsonContent.Create(body, options: JsonOptions);
            }

            using var response = await _httpClient.SendAsync(message, ct);
            if (!response.IsSuccessStatusCode)
            {
                return MapFailure(response);
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(content))
            {
                return AwbReservationOperationResult.Failed("La API operativa no devolvio contenido.");
            }

            var result = TryDeserializeResult(content);
            if (result is not null)
            {
                return result;
            }

            var reservation = JsonSerializer.Deserialize<AwbReservationDto>(content, JsonOptions);
            return reservation is null
                ? AwbReservationOperationResult.Failed("La API operativa devolvio una respuesta no reconocida.")
                : AwbReservationOperationResult.Ok(reservation);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return AwbReservationOperationResult.Failed("La API operativa no respondio antes del timeout.", "timeout");
        }
        catch (HttpRequestException ex)
        {
            return AwbReservationOperationResult.Failed(ex.Message, "http_error");
        }
    }

    private void ConfigureClient()
    {
        if (!string.IsNullOrWhiteSpace(_options.BaseUrl) && _httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(_options.BaseUrl, UriKind.Absolute);
        }

        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 1, 300));

        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.Equals(_options.AuthMode, "none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(_options.AuthMode, "bearer", StringComparison.OrdinalIgnoreCase))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            return;
        }

        if (string.Equals(_options.AuthMode, "api-key", StringComparison.OrdinalIgnoreCase))
        {
            _httpClient.DefaultRequestHeaders.Remove("X-API-Key");
            _httpClient.DefaultRequestHeaders.Add("X-API-Key", _options.ApiKey);
        }
    }

    private static AwbReservationOperationResult MapFailure(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return AwbReservationOperationResult.Failed("La API operativa no encontro el AWB solicitado.", "not_found");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return AwbReservationOperationResult.Failed("La API operativa rechazo la autenticacion configurada.", "unauthorized");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return AwbReservationOperationResult.Failed("La API operativa rechazo la operacion por conflicto de estado.", "conflict");
        }

        return AwbReservationOperationResult.Failed($"La API operativa respondio {(int)response.StatusCode}.", "http_status");
    }

    private static AwbReservationOperationResult? TryDeserializeResult(string content)
    {
        try
        {
            return JsonSerializer.Deserialize<AwbReservationOperationResult>(content, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ExpandAwbPath(string template, string awbNumber) =>
        template.Replace("{awbNumber}", Uri.EscapeDataString(awbNumber), StringComparison.OrdinalIgnoreCase);
}
