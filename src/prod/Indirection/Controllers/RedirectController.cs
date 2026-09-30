using Indirection.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace Indirection.Controllers;

[ApiController]
[AllowAnonymous]
public sealed class RedirectController(IndirectionDbContext db, HybridCache cache) : ControllerBase
{
    [HttpGet("{**code}")]
    public async Task<IActionResult> Get(string? code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(code))
        {
            return NotFound();
        }

        var normalized = code.ToLowerInvariant();
        try
        {
            var destination = await cache.GetOrCreateAsync(normalized, async token =>
                // A failed factory is not cached; returning null would negatively cache a miss.
                await db.ShortUrls.Where(url => url.Code == normalized)
                    .Select(url => url.Destination).SingleOrDefaultAsync(token)
                    ?? throw new KeyNotFoundException(), cancellationToken: cancellationToken);
            return Redirect(destination);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
