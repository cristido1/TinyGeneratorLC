using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.SemanticKernel;
using System.ComponentModel;
using TinyGenerator.Services;

namespace TinyGenerator.Skills
{
    [Description("Provides HTTP request functions such as GET and POST.")]
    public class HttpSkill : ITinySkill
    {
        private readonly ICustomLogger? _logger;
        private int? _modelId;
        private string? _modelName;
        private DateTime? _lastCalled;
        private string? _lastFunction;
        
        public string? LastCalled { get; set; }
        private static readonly HttpClient _http = new HttpClient();

        // ITinySkill implementation
        int? ITinySkill.ModelId { get => _modelId; set => _modelId = value; }
        string? ITinySkill.ModelName { get => _modelName; set => _modelName = value; }
        int? ITinySkill.AgentId => null;
        string? ITinySkill.AgentName => null;
        DateTime? ITinySkill.LastCalled { get => _lastCalled; set => _lastCalled = value; }
        string? ITinySkill.LastFunction { get => _lastFunction; set => _lastFunction = value; }
        ICustomLogger? ITinySkill.Logger { get => _logger; set { } }

        public HttpSkill(ICustomLogger? logger = null)
        {
            _logger = logger;
        }

        [KernelFunction("http_get"), Description("Makes a GET request to a URL.")]
        public async Task<string> HttpGetAsync([Description("The URL to make the GET request to.")] string url)
        {
            ((ITinySkill)this).LogFunctionCall("http_get");
            LastCalled = nameof(HttpGetAsync);
            var resp = await _http.GetAsync(url);
            return await resp.Content.ReadAsStringAsync();
        }

        [KernelFunction("http_post"), Description("Makes a POST request to a URL.")]
        public async Task<string> HttpPostAsync([Description("The URL to make the POST request to.")] string url, [Description("The content to include in the POST request.")] string content)
        {
            ((ITinySkill)this).LogFunctionCall("http_post");
            LastCalled = nameof(HttpPostAsync);
            var resp = await _http.PostAsync(url, new StringContent(content));
            return await resp.Content.ReadAsStringAsync();
        }

        [KernelFunction("describe"), Description("Describes the available HTTP functions.")]
        public string Describe()
        {
            ((ITinySkill)this).LogFunctionCall("describe");
            return "Available functions: http_get(url), http_post(url, content). " +
                "Example: http_get('https://api.example.com/data') returns the response from the URL.";
        }
    }
}
