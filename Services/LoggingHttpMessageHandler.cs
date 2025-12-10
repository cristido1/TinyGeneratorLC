using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TinyGenerator.Services
{
    /// <summary>
    /// HTTP message handler that logs raw requests and responses for debugging
    /// </summary>
    public class LoggingHttpMessageHandler : DelegatingHandler
    {
        private readonly ICustomLogger? _logger;
        private readonly string _agentId;

        public LoggingHttpMessageHandler(HttpMessageHandler innerHandler, ICustomLogger? logger, string agentId)
            : base(innerHandler)
        {
            _logger = logger;
            _agentId = agentId;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Log request
            try
            {
                _logger?.Append(_agentId, "=== RAW HTTP REQUEST ===");
                _logger?.Append(_agentId, $"Method: {request.Method}");
                _logger?.Append(_agentId, $"URI: {request.RequestUri}");
                
                if (request.Content != null)
                {
                    var requestBody = await request.Content.ReadAsStringAsync();
                    _logger?.Append(_agentId, $"Body length: {requestBody.Length} chars");
                    
                    // Split body into chunks for display
                    const int chunkSize = 1000;
                    if (requestBody.Length > chunkSize)
                    {
                        for (int i = 0; i < requestBody.Length; i += chunkSize)
                        {
                            var chunk = requestBody.Substring(i, Math.Min(chunkSize, requestBody.Length - i));
                        _logger?.Append(_agentId, chunk);
                        }
                    }
                    else
                    {
                    _logger?.Append(_agentId, requestBody);
                    }
                }
                
                _logger?.Append(_agentId, "=== END HTTP REQUEST ===");
            }
            catch (Exception ex)
            {
                _logger?.Append(_agentId, $"Error logging request: {ex.Message}");
            }

            // Send actual request
            var response = await base.SendAsync(request, cancellationToken);

            // Log response
            try
            {
                _logger?.Append(_agentId, "=== RAW HTTP RESPONSE ===");
                _logger?.Append(_agentId, $"Status: {(int)response.StatusCode} {response.StatusCode}");
                
                if (response.Content != null)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.Append(_agentId, $"Body length: {responseBody.Length} chars");
                    
                    // Split body into chunks for display
                    const int chunkSize = 1000;
                    if (responseBody.Length > chunkSize)
                    {
                        for (int i = 0; i < responseBody.Length; i += chunkSize)
                        {
                            var chunk = responseBody.Substring(i, Math.Min(chunkSize, responseBody.Length - i));
                            _logger?.Append(_agentId, chunk);
                        }
                    }
                    else
                    {
                        _logger?.Append(_agentId, responseBody);
                    }
                }
                
                _logger?.Append(_agentId, "=== END HTTP RESPONSE ===");
            }
            catch (Exception ex)
            {
                _logger?.Append(_agentId, $"Error logging response: {ex.Message}");
            }

            return response;
        }
    }
}
