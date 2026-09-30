namespace Indirection.Models;

public sealed class ShortUrl
{
    public required string Code { get; set; }
    public required string Destination { get; set; }
    public DateTime CreatedUtc { get; set; }
}
