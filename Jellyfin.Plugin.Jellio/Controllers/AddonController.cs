using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Jellio.Helpers;
using Jellyfin.Plugin.Jellio.Models;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Jellio.Controllers;

[ApiController]
[ConfigAuthorize]
[Route("jellio/{config}")]
[Produces(MediaTypeNames.Application.Json)]
public class AddonController : ControllerBase
{
    private readonly IUserManager _userManager;
    private readonly IUserViewManager _userViewManager;
    private readonly IDtoService _dtoService;
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private static readonly HttpClient _httpClient = new();

    public AddonController(
        IUserManager userManager,
        IUserViewManager userViewManager,
        IDtoService dtoService,
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory)
    {
        _userManager = userManager;
        _userViewManager = userViewManager;
        _dtoService = dtoService;
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
    }

    private async Task<string?> GetTitleFromCinemeta(string imdbId, string type)
    {
        try
        {
            var stremioType = type == "movie" ? "movie" : "series";
            var response = await _httpClient.GetAsync($"https://v3-cinemeta.strem.io/meta/{stremioType}/tt{imdbId}.json");
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
                if (doc.RootElement.TryGetProperty("meta", out var meta) &&
                    meta.TryGetProperty("name", out var name))
                {
                    return name.GetString();
                }
            }
        }
        catch
        {
            // If Cinemeta fails, we'll just return null
        }

        return null;
    }

    private string GetBaseUrl(string? overrideBaseUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideBaseUrl))
        {
            return overrideBaseUrl!.TrimEnd('/');
        }

        return $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
    }

    /// <summary>
    /// Checks if a file is a STRM file and reads the URL from it
    /// </summary>
    /// <param name="filePath">Path to the file to check</param>
    /// <returns>The URL from the STRM file, or null if not a STRM file or error occurs</returns>
    private string? ReadStrmUrl(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        // Check if file has .strm extension
        if (!filePath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            // Read the first line of the STRM file which contains the URL
            if (System.IO.File.Exists(filePath))
            {
                var url = System.IO.File.ReadAllText(filePath).Trim();
                
                // Basic validation that it's a URL
                if (!string.IsNullOrWhiteSpace(url) && 
                    (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                     url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    return url;
                }
            }
        }
        catch (Exception ex)
        {
            // Log the error but don't throw - fall back to normal streaming
            Console.WriteLine($"[Jellio] Error reading STRM file {filePath}: {ex.Message}");
        }

        return null;
    }

    private static MetaDto MapToMeta(
        BaseItemDto dto,
        StremioType stremioType,
        string baseUrl,
        bool includeDetails = false
    )
    {
        string? releaseInfo = null;
        if (dto.PremiereDate.HasValue)
        {
            var premiereYear = dto.PremiereDate.Value.Year.ToString(CultureInfo.InvariantCulture);
            releaseInfo = premiereYear;
            if (stremioType == StremioType.Series)
            {
                releaseInfo += "-";
                if (dto.Status != "Continuing" && dto.EndDate.HasValue)
                {
                    var endYear = dto.EndDate.Value.Year.ToString(CultureInfo.InvariantCulture);
                    if (premiereYear != endYear)
                    {
                        releaseInfo += endYear;
                    }
                }
            }
        }

        var meta = new MetaDto
        {
            Id = dto.ProviderIds.TryGetValue("Imdb", out var idVal) ? idVal : $"jellio:{dto.Id}",
            Type = stremioType.ToString().ToLower(CultureInfo.InvariantCulture),
            Name = dto.Name,
            Poster = $"{baseUrl}/Items/{dto.Id}/Images/Primary",
            PosterShape = "poster",
            Genres = dto.Genres,
            Description = dto.Overview,
            ImdbRating = dto.CommunityRating?.ToString("F1", CultureInfo.InvariantCulture),
            ReleaseInfo = releaseInfo,
        };

        if (includeDetails)
        {
            meta.Runtime =
                dto.RunTimeTicks.HasValue && dto.RunTimeTicks.Value != 0
                    ? $"{dto.RunTimeTicks.Value / 600000000} min"
                    : null;
            meta.Logo = dto.ImageTags.ContainsKey(ImageType.Logo)
                ? $"{baseUrl}/Items/{dto.Id}/Images/Logo"
                : null;
            meta.Background =
                dto.BackdropImageTags.Length != 0
                    ? $"{baseUrl}/Items/{dto.Id}/Images/Backdrop/0"
                    : null;
            meta.Released = dto.PremiereDate?.ToString("o");
        }

        return meta;
    }

    private OkObjectResult GetStreamsResult(Guid userId, IReadOnlyList<BaseItem> items)
    {
        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            return Ok(new { streams = Array.Empty<object>() });
        }

        var baseUrl = GetBaseUrl();
        var dtoOptions = new DtoOptions(true);
        var dtos = _dtoService.GetBaseItemDtos(items, dtoOptions, user);
        
        var streams = dtos.SelectMany(dto =>
            dto.MediaSources.Select(source =>
            {
                // Check if this is a STRM file and try to read the URL from it
                var strmUrl = ReadStrmUrl(source.Path);
                
                if (!string.IsNullOrWhiteSpace(strmUrl))
                {
                    // This is a STRM file - use the URL from inside the file
                    return new StreamDto
                    {
                        Url = strmUrl,
                        Name = "Jellio (STRM)",
                        Description = source.Name ?? "STRM Source",
                    };
                }
                else
                {
                    // Normal file - use Jellyfin streaming endpoint
                    return new StreamDto
                    {
                        Url = $"{baseUrl}/videos/{dto.Id}/stream?mediaSourceId={source.Id}&static=true",
                        Name = "Jellio",
                        Description = source.Name,
                    };
                }
            })
        );
        
        return Ok(new { streams });
    }

    [HttpGet("manifest.json")]
    public IActionResult GetManifest([ConfigFromBase64Json] ConfigModel config)
    {
        var userId = (Guid)HttpContext.Items["JellioUserId"]!;

        var userLibraries = LibraryHelper.GetUserLibraries(userId, _userManager, _userViewManager, _dtoService);
        userLibraries = Array.FindAll(userLibraries, l => config.LibrariesGuids.Contains(l.Id));
        if (userLibraries.Length != config.LibrariesGuids.Count)
        {
            return NotFound();
        }

        var catalogs = userLibraries.Select(lib =>
        {
            return new
            {
                type = lib.CollectionType switch
                {
                    CollectionType.movies => "movie",
                    CollectionType.tvshows => "series",
                    _ => null,
                },
                id = lib.Id.ToString(),
                name = $"{lib.Name} | {config.ServerName}",
                extra = new[]
                {
                    new { name = "skip", isRequired = false },
                    new { name = "search", isRequired = false },
                },
            };
        });

        var catalogNames = userLibraries.Select(l => l.Name).ToList();
        var descriptionText = $"Play movies and series from {config.ServerName}: {string.Join(", ", catalogNames)}";
        var manifest = new
        {
            id = "com.stremio.jellio",
            version = "0.0.1",
            name = "Jellio",
            description = descriptionText,
            resources = new object[]
            {
                "catalog",
                "stream",
                new
                {
                    name = "meta",
                    types = new[] { "movie", "series" },
                    idPrefixes = new[] { "jellio" },
                },
            },
            types = new[] { "movie", "series" },
            idPrefixes = new[] { "tt", "jellio" },
            contactEmail = "support@jellio.stream",
            behaviorHints = new { configurable = true },
            catalogs,
        };

        return Ok(manifest);
    }

    [HttpGet("catalog/{stremioType}/{catalogId:guid}/{extra}.json")]
    [HttpGet("catalog/{stremioType}/{catalogId:guid}.json")]
    public IActionResult GetCatalog(
        [ConfigFromBase64Json] ConfigModel config,
        StremioType stremioType,
        Guid catalogId,
        string? extra = null
    )
    {
        var userId = (Guid)HttpContext.Items["JellioUserId"]!;

        var userLibraries = LibraryHelper.GetUserLibraries(userId, _userManager, _userViewManager, _dtoService);
        var catalogLibrary = Array.Find(userLibraries, l => l.Id == catalogId);
        if (catalogLibrary == null)
        {
            return NotFound();
        }

        var item = _libraryManager.GetParentItem(catalogLibrary.Id, userId);
        if (item is not Folder folder)
        {
            folder = _libraryManager.GetUserRootFolder();
        }

        var extras =
            extra
                ?.Split('&')
                .Select(e => e.Split('='))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1])
            ?? new Dictionary<string, string>();

        int startIndex =
            extras.TryGetValue("skip", out var skipValue)
            && int.TryParse(skipValue, out var parsedSkip)
                ? parsedSkip
                : 0;
        extras.TryGetValue("search", out var searchTerm);

        var dtoOptions = new DtoOptions
        {
            Fields = [ItemFields.ProviderIds, ItemFields.Overview, ItemFields.Genres],
        };

        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            return Unauthorized();
        }

        var query = new InternalItemsQuery(user)
        {
            Recursive = true, // need this for search to work
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Limit = 100,
            StartIndex = startIndex,
            SearchTerm = searchTerm,
            ParentId = catalogLibrary.Id,
            DtoOptions = dtoOptions,
        };
        var result = folder.GetItems(query);
        var dtos = _dtoService.GetBaseItemDtos(result.Items, dtoOptions, user);
        var baseUrl = GetBaseUrl();
        var metas = dtos.Select(dto => MapToMeta(dto, stremioType, baseUrl));

        return Ok(new { metas });
    }

    [HttpGet("meta/{stremioType}/jellio:{mediaId:guid}.json")]
    public IActionResult GetMeta(
        [ConfigFromBase64Json] ConfigModel config,
        StremioType stremioType,
        Guid mediaId
    )
    {
        var userId = (Guid)HttpContext.Items["JellioUserId"]!;

        var item = _libraryManager.GetItemById<BaseItem>(mediaId, userId);
        if (item == null)
        {
            return NotFound();
        }

        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            return Unauthorized();
        }

        var dtoOptions = new DtoOptions
        {
            Fields = [ItemFields.ProviderIds, ItemFields.Overview, ItemFields.Genres],
        };
        var dto = _dtoService.GetBaseItemDto(item, dtoOptions, user);
        var baseUrl = GetBaseUrl();
        var meta = MapToMeta(dto, stremioType, baseUrl, includeDetails: true);

        if (stremioType is StremioType.Series)
        {
            if (item is not Series series)
            {
                return BadRequest();
            }

            var episodes = series.GetEpisodes(user, dtoOptions, false).ToList();
            var seriesItemOptions = new DtoOptions { Fields = [ItemFields.Overview] };
            var dtos = _dtoService.GetBaseItemDtos(episodes, seriesItemOptions, user);
            var videos = dtos.Select(episode => new VideoDto
            {
                Id = $"jellio:{episode.Id}",
                Title = episode.Name,
                Thumbnail = $"{baseUrl}/Items/{episode.Id}/Images/Primary",
                Available = true,
                Episode = episode.IndexNumber ?? 0,
                Season = episode.ParentIndexNumber ?? 0,
                Overview = episode.Overview,
                Released = episode.PremiereDate?.ToString("o"),
            });
            meta.Videos = videos;
        }

        return Ok(new { meta });
    }

    [HttpGet("stream/{stremioType}/jellio:{mediaId:guid}.json")]
    public IActionResult GetStream(
        [ConfigFromBase64Json] ConfigModel config,
        StremioType stremioType,
        Guid mediaId
    )
    {
        var userId = (Guid)HttpContext.Items["JellioUserId"]!;

        var item = _libraryManager.GetItemById<BaseItem>(mediaId, userId);
        if (item == null)
        {
            // If the item isn't in the library, we can't resolve provider IDs here.
            // Let Stremio fall back to IMDB-based stream routes which include IDs for request flow.
            return Ok(new { streams = Array.Empty<object>() });
        }

        return GetStreamsResult(userId, [item]);
    }

    [HttpGet("stream/movie/tt{imdbId}.json")]
    public async Task<IActionResult> GetStreamImdbMovie(
        [ConfigFromBase64Json] ConfigModel config,
        string imdbId
    )
    {
        var userId = (Guid)HttpContext.Items["JellioUserId"]!;

        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            return Unauthorized();
        }

        var query = new InternalItemsQuery(user)
        {
            HasAnyProviderId = new Dictionary<string, string> { ["Imdb"] = $"tt{imdbId}" },
            IncludeItemTypes = [BaseItemKind.Movie],
        };
        var items = _libraryManager.GetItemList(query);

        if (items.Count == 0)
        {
            // AUTO-REQUEST: Automatically send request to Jellyseerr if configured
            if (config.JellyseerrEnabled && !string.IsNullOrWhiteSpace(config.JellyseerrUrl))
            {
                var title = await GetTitleFromCinemeta(imdbId, "movie");
                if (!string.IsNullOrWhiteSpace(title))
                {
                    // Send request in background
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await AutoRequestToJellyseerr(config, userId, "movie", imdbId, title, null, null);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Jellio] Auto-request failed: {ex.Message}");
                        }
                    });

                    // Return a notification stream
                    var streams = new[]
                    {
                        new {
                            url = "about:blank",
                            name = "📥 Auto-requested via Jellyseerr",
                            description = $"{title} has been automatically requested. Check Jellyseerr for status."
                        }
                    };
                    return Ok(new { streams });
                }
            }

            return Ok(new { streams = Array.Empty<object>() });
        }

        return GetStreamsResult(userId, items);
    }

    [HttpGet("stream/series/tt{imdbId}:{seasonNum:int}:{episodeNum:int}.json")]
    public async Task<IActionResult> GetStreamImdbTv(
        [ConfigFromBase64Json] ConfigModel config,
        string imdbId,
        int seasonNum,
        int episodeNum
    )
    {
        var userId = (Guid)HttpContext.Items["JellioUserId"]!;

        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            return Unauthorized();
        }

        var seriesQuery = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Series],
            HasAnyProviderId = new Dictionary<string, string> { ["Imdb"] = $"tt{imdbId}" },
        };
        var seriesItems = _libraryManager.GetItemList(seriesQuery);

        if (seriesItems.Count == 0)
        {
            // AUTO-REQUEST: Series not found - auto-request if enabled
            if (config.JellyseerrEnabled && !string.IsNullOrWhiteSpace(config.JellyseerrUrl))
            {
                var title = await GetTitleFromCinemeta(imdbId, "tv");
                if (!string.IsNullOrWhiteSpace(title))
                {
                    // Send request in background
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await AutoRequestToJellyseerr(config, userId, "tv", imdbId, title, seasonNum, episodeNum);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Jellio] Auto-request failed: {ex.Message}");
                        }
                    });

                    var streams = new[]
                    {
                        new {
                            url = "about:blank",
                            name = "📥 Auto-requested via Jellyseerr",
                            description = $"{title} S{seasonNum}E{episodeNum} has been automatically requested. Check Jellyseerr for status."
                        }
                    };
                    return Ok(new { streams });
                }
            }

            return Ok(new { streams = Array.Empty<object>() });
        }

        if (seriesItems[0] is not Series series)
        {
            return BadRequest();
        }

        var episodeQuery = new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            ParentIndexNumber = seasonNum,
            IndexNumber = episodeNum,
            AncestorIds = [series.Id],
        };
        var episodeItems = _libraryManager.GetItemList(episodeQuery);

        if (episodeItems.Count == 0)
        {
            return Ok(new { streams = Array.Empty<object>() });
        }

        return GetStreamsResult(userId, episodeItems);
    }

    // AUTO-REQUEST HELPER METHOD
    private async Task AutoRequestToJellyseerr(
        ConfigModel config,
        Guid userId,
        string type,
        string imdbId,
        string title,
        int? season,
        int? episode)
    {
        Console.WriteLine($"[Jellio] Auto-requesting: {title} ({type}) - IMDB: tt{imdbId}");

        using var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(config.JellyseerrUrl!.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(10);

        if (!string.IsNullOrWhiteSpace(config.JellyseerrApiKey))
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", config.JellyseerrApiKey);
        }

        try
        {
            // Search for TMDB ID
            var searchUri = $"api/v1/search?query={Uri.EscapeDataString(title)}";
            using var searchResp = await client.GetAsync(searchUri);

            int? tmdbId = null;

            if (searchResp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await searchResp.Content.ReadAsStreamAsync());
                if (doc.RootElement.TryGetProperty("results", out var results))
                {
                    foreach (var el in results.EnumerateArray())
                    {
                        var mediaType = el.TryGetProperty("mediaType", out var mt) ? mt.GetString() : null;
                        if (mediaType == type)
                        {
                            tmdbId = el.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : (int?)null;
                            break;
                        }
                    }
                }
            }

            if (!tmdbId.HasValue)
            {
                Console.WriteLine($"[Jellio] Could not find TMDB ID for {title}");
                return;
            }

            // Make request
            var requestUri = "api/v1/request";
            var requestBody = new
            {
                mediaType = type,
                mediaId = tmdbId.Value,
                seasons = type == "tv" && season.HasValue ? new[] { season.Value } : null,
            };

            var jsonContent = JsonContent.Create(requestBody);
            using var requestResp = await client.PostAsync(requestUri, jsonContent);

            if (requestResp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Jellio] Successfully requested {title} in Jellyseerr");
            }
            else
            {
                var errorBody = await requestResp.Content.ReadAsStringAsync();
                Console.WriteLine($"[Jellio] Jellyseerr request failed: {requestResp.StatusCode} - {errorBody}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Jellio] Jellyseerr auto-request error: {ex.Message}");
            throw;
        }
    }
}
