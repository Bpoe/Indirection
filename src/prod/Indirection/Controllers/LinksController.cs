using System.ComponentModel.DataAnnotations;
using Indirection.Data;
using Indirection.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace Indirection.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/links")]
public sealed class LinksController(IndirectionDbContext db, HybridCache cache) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<ShortUrl>>> GetAll(CancellationToken cancellationToken)
        => await db.ShortUrls.AsNoTracking().ToListAsync(cancellationToken);

    [HttpGet("{**code}")]
    public async Task<ActionResult<ShortUrl>> Get(string code, CancellationToken cancellationToken)
    {
        var normalized = code.ToLowerInvariant();
        var link = await db.ShortUrls.AsNoTracking()
            .SingleOrDefaultAsync(url => url.Code == normalized, cancellationToken);
        return link is null ? NotFound() : Ok(link);
    }

    [HttpPut("{**code}")]
    public async Task<IActionResult> Put(string code, PutLink request, CancellationToken cancellationToken)
    {
        var normalized = code.ToLowerInvariant();
        var link = await db.ShortUrls.FindAsync([normalized], cancellationToken);
        var created = link is null;
        if (link is null)
        {
            link = new ShortUrl
            {
                Code = normalized,
                Destination = request.Destination,
                CreatedUtc = DateTime.UtcNow
            };
            db.ShortUrls.Add(link);
        }
        else
        {
            link.Destination = request.Destination;
        }

        await db.SaveChangesAsync(cancellationToken);
        await cache.RemoveAsync(normalized, cancellationToken);
        return created ? CreatedAtAction(nameof(Get), new { code = normalized }, link) : NoContent();
    }

    [HttpDelete("{**code}")]
    public async Task<IActionResult> Delete(string code, CancellationToken cancellationToken)
    {
        var normalized = code.ToLowerInvariant();
        var link = await db.ShortUrls.FindAsync([normalized], cancellationToken);
        if (link is null)
        {
            return NotFound();
        }

        db.ShortUrls.Remove(link);
        await db.SaveChangesAsync(cancellationToken);
        await cache.RemoveAsync(normalized, cancellationToken);
        return NoContent();
    }
}

public sealed record PutLink([Required] string Destination);
