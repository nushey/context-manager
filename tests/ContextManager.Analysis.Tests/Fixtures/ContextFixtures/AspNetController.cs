using Microsoft.AspNetCore.Mvc;

namespace ContextFixtures;

public class AspNetController : ControllerBase
{
    public IActionResult Get(int id, string? filter) => Ok(id);
}
