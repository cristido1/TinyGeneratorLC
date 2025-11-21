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
        private readonly ProgressService? _progressService;
        private readonly string _agentId;

        public LoggingHttpMessageHandler(HttpMessageHandler innerHandler, ProgressService? progressService, string agentId)
            : base(innerHandler)
        {
            _progressService = progressService;
            _agentId = agentId;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Log request
            try
            {
                _progressService?.Append(_agentId, "=== RAW HTTP REQUEST ===");
                _progressService?.Append(_agentId, $"Method: {request.Method}");
                _progressService?.Append(_agentId, $"URI: {request.RequestUri}");
                
                if (request.Content != null)
                {
                    var requestBody = await request.Content.ReadAsStringAsync();
                    _progressService?.Append(_agentId, $"Body length: {requestBody.Length} chars");
                    
                    // Split body into chunks for display
                    const int chunkSize = 1000;
                    if (requestBody.Length > chunkSize)
                    {
                        for (int i = 0; i < requestBody.Length; i += chunkSize)
                        {
                            var chunk = requestBody.Substring(i, Math.Min(chunkSize, requestBody.Length - i));
                            _progressService?.Append(_agentId, chunk);
                        }
                    }
                    else
                    {
                        _progressService?.Append(_agentId, requestBody);
                    }
                }
                
                _progressService?.Append(_agentId, "=== END HTTP REQUEST ===");
            }
            catch (Exception ex)
            {
                _progressService?.Append(_agentId, $"Error logging request: {ex.Message}");
            }

            // Send actual request
            var response = await base.SendAsync(request, cancellationToken);

            // Log response
            try
            {
                _progressService?.Append(_agentId, "=== RAW HTTP RESPONSE ===");
                _progressService?.Append(_agentId, $"Status: {(int)response.StatusCode} {response.StatusCode}");
                
                if (response.Content != null)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _progressService?.Append(_agentId, $"Body length: {responseBody.Length} chars");
                    
                    // Split body into chunks for display
                    const int chunkSize = 1000;
                    if (responseBody.Length > chunkSize)
                    {
                        for (int i = 0; i < responseBody.Length; i += chunkSize)
                        {
                            var chunk = responseBody.Substring(i, Math.Min(chunkSize, responseBody.Length - i));
                            _progressService?.Append(_agentId, chunk);
                        }
                    }
                    else
                    {
                        _progressService?.Append(_agentId, responseBody);
                    }
                }
                
                _progressService?.Append(_agentId, "=== END HTTP RESPONSE ===");
            }
            catch (Exception ex)
            {
                _progressService?.Append(_agentId, $"Error logging response: {ex.Message}");
            }

            return response;
        }
    }
}
