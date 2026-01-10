using Microsoft.AspNetCore.Mvc;
using TinyGenerator.Data;

namespace TinyGenerator.Controllers
{
    [ApiController]
    [Route("api/agents")]
    public class AgentsApiController : ControllerBase
    {
        private readonly TinyGeneratorDbContext _db;

        public AgentsApiController(TinyGeneratorDbContext db)
        {
            _db = db;
        }

        [HttpGet("details/{id}")]
        public async Task<IActionResult> GetDetails(int id)
        {
            var agent = await _db.Agents.FindAsync(id);
            if (agent == null) return NotFound();

            // Compose a simple description from available fields
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(agent.Prompt)) parts.Add("Prompt: " + agent.Prompt);
            if (!string.IsNullOrWhiteSpace(agent.Instructions)) parts.Add("Instructions: " + agent.Instructions);
            if (!string.IsNullOrWhiteSpace(agent.Config)) parts.Add("Config: " + agent.Config);
            if (!string.IsNullOrWhiteSpace(agent.ExecutionPlan)) parts.Add("Execution Plan: " + agent.ExecutionPlan);
            if (!string.IsNullOrWhiteSpace(agent.Notes)) parts.Add("Notes: " + agent.Notes);

            var description = string.Join("\n\n", parts);

            return Ok(new { title = agent.Name, description });
        }
    }
}
