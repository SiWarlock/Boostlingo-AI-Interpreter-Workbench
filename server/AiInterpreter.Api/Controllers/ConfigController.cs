using AiInterpreter.Api.Config;
using Microsoft.AspNetCore.Mvc;

namespace AiInterpreter.Api.Controllers;

/// <summary>
/// <c>GET /api/config</c> (ARCH-009) — reports provider capability flags from key presence only
/// (never values; safety invariant #1). Thin (ARCH-008): all assembly is in <see cref="IConfigService"/>.
/// </summary>
[ApiController]
[Route("api/config")]
public sealed class ConfigController : ControllerBase
{
    private readonly IConfigService _config;

    public ConfigController(IConfigService config) => _config = config;

    [HttpGet]
    public ActionResult<ConfigResponse> Get() => Ok(_config.GetConfig());
}
