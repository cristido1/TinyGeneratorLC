using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;

namespace TinyGenerator.Skills
{
    public class HttpPlugin
    {
    public string? LastCalled { get; set; }
        private static readonly HttpClient _http = new HttpClient();

        [KernelFunction("http_get")]
        public async Task<string> HttpGetAsync(string url)
        {
            LastCalled = nameof(HttpGetAsync);
            var resp = await _http.GetAsync(url);
            return await resp.Content.ReadAsStringAsync();
        }

        [KernelFunction("http_post")]
        public async Task<string> HttpPostAsync(string url, string content)
        {
            LastCalled = nameof(HttpPostAsync);
            var resp = await _http.PostAsync(url, new StringContent(content));
            return await resp.Content.ReadAsStringAsync();
        }
    }
}
