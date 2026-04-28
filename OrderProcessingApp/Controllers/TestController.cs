using Microsoft.AspNetCore.Mvc;

namespace OrderProcessingApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TestController : ControllerBase
    {
        [HttpGet]
        public IActionResult Get()
        {
            return Ok("API Working");
        }
    }
}