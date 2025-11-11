using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace TinyGenerator.Skills
{
    [Description("Provides HTTP request functions such as GET and POST.")]
    public class HttpPlugin
    {
        public string? LastCalled { get; set; }
        private static readonly HttpClient _http = new HttpClient();

        [KernelFunction("http_get"),Description("Makes a GET request to a URL.")]
        public async Task<string> HttpGetAsync([Description("The URL to make the GET request to.")] string url)
        {
            LastCalled = nameof(HttpGetAsync);
            var resp = await _http.GetAsync(url);
            return await resp.Content.ReadAsStringAsync();
        }

        [KernelFunction("http_post"), Description("Makes a POST request to a URL.")]
        public async Task<string> HttpPostAsync([Description("The URL to make the POST request to.")] string url, [Description("The content to include in the POST request.")] string content)
        {
            LastCalled = nameof(HttpPostAsync);
            var resp = await _http.PostAsync(url, new StringContent(content));
            return await resp.Content.ReadAsStringAsync();
        }
        [KernelFunction("describe"), Description("Describes the available HTTP functions.")]
        public string Describe() =>
            "Available functions: http_get(url), http_post(url, content). " +
            "Example: http_get('https://api.example.com/data') returns the response from the URL.";
    }
}
