using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;

using Google.Apis.Services;
using Google.Apis.YouTube.v3;

namespace Jellyfin.Plugin.YoutubeMetadata.Providers
{
    public class YoutubeMetadataProvider : IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
    {
        private readonly IServerConfigurationManager _config;
        private readonly IFileSystem _fileSystem;
        private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;
        private readonly ILibraryManager _libmanager;

        public static YoutubeMetadataProvider Current;

        public const string BaseUrl = "https://m.youtube.com/";
        public const string YTID_RE = @"(?<=\[)[a-zA-Z0-9\-_]{11}(?=\])";

        public YoutubeMetadataProvider(IServerConfigurationManager config, IFileSystem fileSystem, IHttpClient httpClient, IJsonSerializer json, ILogger logger, ILibraryManager libmanager)
        {
            _config = config;
            _fileSystem = fileSystem;
            _httpClient = httpClient;
            _json = json;
            _logger = logger;
            _libmanager = libmanager;
            Current = this;
        }

        /// <inheritdoc />
        public string Name => "YoutubeMetadata";

        /// <inheritdoc />
        public int Order => 1;

        /// <inheritdoc />
        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
            => Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

        private string GetPathByTitle(string title)
        {
            var query = new MediaBrowser.Controller.Entities.InternalItemsQuery { Name = title };
            var results = _libmanager.GetItemsResult(query);
            return results.Items[0].Path;
        }

        /// <summary>
        ///  Returns the Youtube ID from the file path. Matches last 11 character field inside square brackets.
        /// </summary>
        /// <param name="title"></param>
        /// <returns></returns>
        internal string GetYTID(string title)
        {
            var filename = GetPathByTitle(title);
            var match = Regex.Match(filename, YTID_RE);
            return match.Value;

        }

        /// <inheritdoc />
        public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Movie>();
            var id = GetYTID(info.Name);

            _logger.LogInformation(id);

            if (!string.IsNullOrWhiteSpace(id))
            {
                await EnsureInfo(id, cancellationToken).ConfigureAwait(false);

                var path = GetVideoInfoPath(_config.ApplicationPaths, id);

                var video = _json.DeserializeFromFile<Google.Apis.YouTube.v3.Data.Video>(path);
                if (video != null)
                {
                    result.Item = new Movie();
                    result.HasMetadata = true;
                    result.Item.OriginalTitle = info.Name;
                    ProcessResult(result.Item, video);
                    result.AddPerson(CreatePerson(video.Snippet.ChannelTitle));
                }
            }
            else
            {
                _logger.LogInformation("Youtube ID not found in filename of title: " + info.Name);
            }

            return result;
        }

        /// <summary>
        /// Creates a person object of type director for the provided name.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        private PersonInfo CreatePerson(string name)
        {

            return new PersonInfo
            {
                Name = name,
                Type = PersonType.Director,
            };
        }
        /// <summary>
        /// Processes the found metadata into the Movie entity.
        /// </summary>
        /// <param name="item"></param>
        /// <param name="result"></param>
        /// <param name="preferredLanguage"></param>
        private void ProcessResult(Movie item, Google.Apis.YouTube.v3.Data.Video result)
        {
            _logger.LogInformation(item.ToString());
            _logger.LogInformation(result.ToString());
            item.Name = result.Snippet.Title;
            item.Overview = result.Snippet.Description;
            var date = DateTime.Parse(result.Snippet.PublishedAtRaw);
            item.ProductionYear = date.Year;
            item.PremiereDate = date;
        }

        /// <summary>
        /// Checks and returns data in local cache, downloads and returns if not present.
        /// </summary>
        /// <param name="youtubeID"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        internal Task EnsureInfo(string youtubeID, CancellationToken cancellationToken)
        {
            var ytPath = GetVideoInfoPath(_config.ApplicationPaths, youtubeID);

            var fileInfo = _fileSystem.GetFileSystemInfo(ytPath);

            if (fileInfo.Exists)
            {
                if ((DateTime.UtcNow - _fileSystem.GetLastWriteTimeUtc(fileInfo)).TotalDays <= 2)
                {
                    return Task.CompletedTask;
                }
            }
            return DownloadInfo(youtubeID, cancellationToken);
        }

        /// <summary>
        /// Downloads metadata from Youtube API asyncronously and stores it as a json to cache.
        /// </summary>
        /// <param name="youtubeId"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        internal async Task DownloadInfo(string youtubeId, CancellationToken cancellationToken)
        {
            _logger.LogInformation("In DownloadInfo");
            cancellationToken.ThrowIfCancellationRequested();
            var youtubeService = new YouTubeService(new BaseClientService.Initializer()
            {
                ApiKey = Plugin.Instance.Configuration.apikey,
                ApplicationName = this.GetType().ToString()
            });
            var vreq = youtubeService.Videos.List("snippet");
            vreq.Id = youtubeId;
            Google.Apis.YouTube.v3.Data.VideoListResponse response = null;
            var attempts = 0;
            do {
                try
                {
                    attempts++;
                    response = await vreq.ExecuteAsync();
                    break;
                }
                catch (Google.GoogleApiException ex)
                {
                    if (attempts == 2)
                    {
                        _logger.LogError(ex, "Tried to get metadata multiple times. ");
                        throw ex;
                    }
                    var now = DateTime.Now;
                    var tomorrow = new DateTime(now.Year, now.Month, now.Day + 1);
                    var timetosub = tomorrow.Subtract(now);
                    var inttimetosub = Convert.ToInt32(timetosub.TotalMilliseconds) + 60000;
                    _logger.LogInformation("Quota exceeded, waiting for {0} seconds", timetosub.TotalSeconds);
                    await Task.Delay(inttimetosub);
                    
                }
            } while (true);
            _logger.LogInformation(response.ToString());
            var path = GetVideoInfoPath(_config.ApplicationPaths, youtubeId);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var foo = response.Items[0];
            _json.SerializeToFile(foo, path);
        }

        /// <summary>
        /// Gets the data path of a video provided the youtube ID.
        /// </summary>
        /// <param name="appPaths"></param>
        /// <param name="youtubeId"></param>
        /// <returns></returns>
        private static string GetVideoDataPath(IApplicationPaths appPaths, string youtubeId)
        {
            var dataPath = Path.Combine(GetVideoDataPath(appPaths), youtubeId);

            return dataPath;
        }

        /// <summary>
        /// Gets the Youtube Metadata root cache path.
        /// </summary>
        /// <param name="appPaths"></param>
        /// <returns></returns>
        private static string GetVideoDataPath(IApplicationPaths appPaths)
        {
            var dataPath = Path.Combine(appPaths.CachePath, "youtubemetadata");

            return dataPath;
        }

        /// <summary>
        /// Gets the path to information on a specific video in the cache.
        /// </summary>
        /// <param name="appPaths"></param>
        /// <param name="youtubeID"></param>
        /// <returns></returns>
        internal static string GetVideoInfoPath(IApplicationPaths appPaths, string youtubeID)
        {
            var dataPath = GetVideoDataPath(appPaths, youtubeID);

            return Path.Combine(dataPath, "ytvideo.json");
        }

        public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}