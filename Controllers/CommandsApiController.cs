using Microsoft.AspNetCore.Mvc;
using TinyGenerator.Services;

namespace TinyGenerator.Controllers
{
    [ApiController]
    [Route("api/commands")]
    public class CommandsApiController : ControllerBase
    {
        private readonly ICommandDispatcher _dispatcher;

        public CommandsApiController(ICommandDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        [HttpGet]
        public IActionResult GetActiveCommands()
        {
            var commands = _dispatcher.GetActiveCommands();
            return Ok(commands);
        }
    }
}
