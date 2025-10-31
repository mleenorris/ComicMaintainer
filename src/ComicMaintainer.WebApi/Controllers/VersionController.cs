using Microsoft.AspNetCore.Mvc;
using System.Reflection;

namespace ComicMaintainer.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class VersionController : ControllerBase
{
    [HttpGet]
    public ActionResult<object> GetVersion()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetName()
            .Version?
            .ToString() ?? "1.0.0";

        return Ok(new
        {
            version = version,
            platform = ".NET"
        });
    }
}
