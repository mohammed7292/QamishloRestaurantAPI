using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/map")]
public sealed class MapTilesController : ControllerBase
{
    private static readonly HttpClient TileHttp = CreateTileClient();
    private static readonly ConcurrentDictionary<string, (byte[] Bytes, string ContentType, DateTime ExpiresUtc)> Cache = new();

    private static HttpClient CreateTileClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "QamishloFastFoodMap/4.0 (Erbil-Iraq; https://qamishlorestaurantapi.miohammedomran.prod.page)");
        return client;
    }

    [HttpGet("tiles/{style}/{z:int}/{x:int}/{y:int}.png")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> GetTile(string style, int z, int x, int y)
    {
        if (z < 10 || z > 20 || x < 0 || y < 0)
            return BadRequest();

        string normStyle = (style ?? "streets").ToLowerInvariant();
        string cacheKey = $"{normStyle}:{z}:{x}:{y}";

        if (Cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
            return File(cached.Bytes, cached.ContentType);

        string[] urls = normStyle switch
        {
            "satellite" => new[]
            {
                $"https://mt{(x + y) % 4}.google.com/vt/lyrs=y&hl=ar&x={x}&y={y}&z={z}",
                $"https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"
            },
            _ => new[]
            {
                $"https://mt{(x + y) % 4}.google.com/vt/lyrs=m&hl=ar&x={x}&y={y}&z={z}",
                $"https://server.arcgisonline.com/ArcGIS/rest/services/World_Street_Map/MapServer/tile/{z}/{y}/{x}",
                $"https://tile.openstreetmap.org/{z}/{x}/{y}.png"
            }
        };

        foreach (string url in urls)
        {
            try
            {
                using var resp = await TileHttp.GetAsync(url);
                if (!resp.IsSuccessStatusCode) continue;
                byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length < 100) continue;
                string ct = resp.Content.Headers.ContentType?.MediaType ?? "image/png";
                if (Cache.Count > 2500) Cache.Clear();
                Cache[cacheKey] = (bytes, ct, DateTime.UtcNow.AddHours(12));
                return File(bytes, ct);
            }
            catch
            {
            }
        }

        return NotFound();
    }
}
