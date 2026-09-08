using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CryptoSmithX.WebApp.Agent.Controllers;

/// <summary>
/// Trading parameters — where a sign-in lands. Bound to nothing yet, by instruction: the figures
/// on it are the design's own and the Save button changes only the word above it. When the source
/// is settled this controller reads and writes it; until then the screen is honest about being a
/// screen and nothing behind it can be broken by it.
/// </summary>
[Authorize]
public sealed class ParametersController : Controller
{
    public IActionResult Index() => View();
}
