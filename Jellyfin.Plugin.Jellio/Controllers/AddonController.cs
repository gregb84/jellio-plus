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

    private string GetBaseUrl(string? publicBaseUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(publicBaseUrl))
        {
            return publicBaseUrl.TrimEnd('/');
        }

        var request = HttpContext.Request;
        var scheme = request.Scheme;
        var host = request.Host.ToUriComponent();
        return $"{scheme}://{host}";
    }

    private MetaDto MapToMeta(BaseItemDto dto, StremioType stremioType, string baseUrl, bool includeDetails = false)
    {
        var releaseInfo = dto.ProductionYear?.ToString(CultureInfo.InvariantCulture);
        if (!dto.ProviderIds.TryGetValue("Imdb", out var imdbId))
        {
            imdbId = null;
        }

        var idVal = !string.IsNullOrEmpty(imdbId) ? imdbId : $"jellio:{dto.Id}";
        var meta = new MetaDto
        {
            Id = !string.IsNullOrEmpty(imdbId) && imdbId.StartsWith("tt", StringComparison.Ordinal)
                ? imdbId : $"jellio:{dto.Id}",
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

    private async Task<bool> IsUrlAccessibleAsync(string url)
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            var request = new HttpRequestMessage(HttpMethod.Head, url);
            var response = await _httpClient.SendAsync(request, cts.Token);
            
            return response.IsSuccessStatusCode || 
                   response.StatusCode == System.Net.HttpStatusCode.Redirect ||
                   response.StatusCode == System.Net.HttpStatusCode.MovedPermanently;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> ReadStrmUrlAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            var content = await File.ReadAllTextAsync(filePath);
            var url = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            
            return string.IsNullOrWhiteSpace(url) ? null : url.Trim();
        }
        catch
        {
            return null;
        }
    }

    private async Task<OkObjectResult> GetStreamsResultAsync(Guid userId, IReadOnlyList<BaseItem> items, string authToken, bool directDownloadOnly = false)
    {
        var user = _userManager.GetUserById(userId);
        if (user == null)
        {
            return Ok(new { streams = Array.Empty<object>() });
        }

        var baseUrl = GetBaseUrl();
        var dtoOptions = new DtoOptions(true);
        var dtos = _dtoService.GetBaseItemDtos(items, dtoOptions, user);
        
        var streams = new List<StreamDto>();
        
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var dto = dtos[i];
            
            foreach (var source in dto.MediaSources)
            {
                string? filePath = null;
                if (source.Path != null && File.Exists(source.Path))
                {
                    filePath = source.Path;
                }
                else if (item.Path != null && File.Exists(item.Path))
                {
                    filePath = item.Path;
                }

                bool isStrm = filePath != null && filePath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);
                
                if (isStrm)
                {
                    var strmUrl = await ReadStrmUrlAsync(filePath!);
                    if (!string.IsNullOrWhiteSpace(strmUrl) && await IsUrlAccessibleAsync(strmUrl))
                    {
                        streams.Add(new StreamDto
                        {
                            Url = strmUrl,
                            Name = directDownloadOnly ? "Jellio" : "STRM Source",
                            Description = directDownloadOnly ? source.Name : $"{source.Name} - STRM Source",
                            BehaviorHints = new BehaviorHints
                            {
                                NotWebReady = true
                            }
                        });
                    }
                    else
                    {
                        Console.WriteLine($"[Jellio] Skipping inaccessible STRM URL: {strmUrl}");
                    }
                }
                else
                {
                    if (!directDownloadOnly)
                    {
                        streams.Add(new StreamDto
                        {
                            Url = $"{baseUrl}/videos/{dto.Id}/stream?mediaSourceId={source.Id}&static=true&api_key={authToken}",
                            Name = "Jellio",
                            Description = source.Name,
                            BehaviorHints = new BehaviorHints
                            {
                                NotWebReady = true
                            }
                        });
                    }
                    
                    streams.Add(new StreamDto
                    {
                        Url = $"{baseUrl}/Items/{dto.Id}/Download?mediaSourceId={source.Id}&api_key={authToken}",
                        Name = directDownloadOnly ? "Jellio" : "Jellio (Direct)",
                        Description = directDownloadOnly ? source.Name : $"{source.Name} - Direct Download",
                        BehaviorHints = new BehaviorHints
                        {
                            NotWebReady = true
                        }
                    });
                }
            }
        }
        
        return Ok(new { streams });
    }

    [HttpGet("manifest.json")]
    public IActionResult GetManifest([ConfigFromBase64Json] ConfigModel config)
    {
        var userId = (Guid)HttpContext.Items["JellioUserId"]!;

        var userLibraries = LibraryHelper.GetUserLibraries(userId, _userManager, _userViewManager, _dtoService);
        userLibraries = Array.FindAll(userLibraries, l => config.LibrariesGuids.Contains(l.Id));
        
        // Build catalogs only if libraries are selected
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
        }).ToArray();

        // Description based on whether catalogs are present
        var descriptionText = catalogs.Length > 0
            ? $"Play movies and series from {config.ServerName}: {string.Join(", ", userLibraries.Select(l => l.Name))}"
            : $"Search and play movies and series from {config.ServerName}";

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
            catalogs, // This will be an empty array if no libraries selected
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
            Recursive = true,
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
    public async Task<IActionResult> GetStream(
        [ConfigFromBase64Json] ConfigModel config,
        StremioType stremioType,
        Guid mediaId
    )
    {
        var userId = (Guid)HttpContext.Items["JellioUserId"]!;

        var item = _libraryManager.GetItemById<BaseItem>(mediaId, userId);
        if (item == null)
        {
            return Ok(new { streams = Array.Empty<object>() });
        }

        return await GetStreamsResultAsync(userId, [item], config.AuthToken, config.DirectDownloadOnly);
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
            if (config.JellyseerrEnabled && !string.IsNullOrWhiteSpace(config.JellyseerrUrl))
            {
                var title = await GetTitleFromCinemeta(imdbId, "movie");
                if (!string.IsNullOrWhiteSpace(title))
                {
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

        return await GetStreamsResultAsync(userId, items, config.AuthToken, config.DirectDownloadOnly);
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
            if (config.JellyseerrEnabled && !string.IsNullOrWhiteSpace(config.JellyseerrUrl))
            {
                var title = await GetTitleFromCinemeta(imdbId, "tv");
                if (!string.IsNullOrWhiteSpace(title))
                {
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

        return await GetStreamsResultAsync(userId, episodeItems, config.AuthToken, config.DirectDownloadOnly);
    }

    private async Task AutoRequestToJellyseerr(
        ConfigModel config,
        Guid userId,
        string type,
        string imdbId,
        string title,
        int? season,
        int? episode)
    {
        if (string.IsNullOrWhiteSpace(config.JellyseerrUrl) || 
            string.IsNullOrWhiteSpace(config.JellyseerrApiKey))
        {
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var baseUrl = config.JellyseerrUrl.TrimEnd('/');
            
            var requestBody = new
            {
                mediaType = type,
                mediaId = int.Parse(imdbId.Replace("tt", "")),
                tvdbId = (int?)null,
                seasons = type == "tv" && season.HasValue ? new[] { season.Value } : null
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/request")
            {
                Content = JsonContent.Create(requestBody)
            };
            
            request.Headers.Add("X-Api-Key", config.JellyseerrApiKey);
            
            var response = await client.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Jellio] Successfully requested {title} via Jellyseerr");
            }
            else
            {
                Console.WriteLine($"[Jellio] Jellyseerr request failed: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Jellio] Error sending request to Jellyseerr: {ex.Message}");
        }
    }
}
