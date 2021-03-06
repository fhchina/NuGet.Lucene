using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using AspNet.WebApi.HtmlMicrodataFormatter;
using Lucene.Net.Linq;
using NuGet.Lucene.Util;
using NuGet.Lucene.Web.Models;
using NuGet.Lucene.Web.Symbols;
using NuGet.Lucene.Web.Util;

namespace NuGet.Lucene.Web.Controllers
{
    /// <summary>
    /// Provides methods to search, get metadata, download, upload and delete packages.
    /// </summary>
    public class PackagesController : ApiControllerBase
    {
        public ILucenePackageRepository LuceneRepository { get; set; }
        public IMirroringPackageRepository MirroringRepository { get; set; }
        public ISymbolSource SymbolSource { get; set; }
        public ITaskRunner TaskRunner { get; set; }

        /// <summary>
        /// Gets metadata about a package from the <c>nuspec</c> files and other
        /// metadata such as package size, date published, download counts, etc.
        /// </summary>
        public object GetPackageInfo(string id, string version="")
        {
            var packageSpec = new PackageSpec(id, version);
            var packages = LuceneRepository
                            .LucenePackages
                            .Where(p => p.Id == packageSpec.Id)
                            .OrderBy(p => p.Version)
                            .ToList();

            var package = packageSpec.Version != null
                              ? packages.Find(p => p.Version.SemanticVersion == packageSpec.Version)
                              : packages.LastOrDefault();

            if (package == null)
            {
                return Request.CreateErrorResponse(HttpStatusCode.NotFound, "Package not found.");
            }

            var versionHistory = packages.Select(pkg => new PackageVersionSummary(pkg, new Link(GetPackageInfoUrl(pkg), pkg.Version.ToString()))).ToList();

            versionHistory.Select(v => v.Link).SetRelationships(packages.IndexOf(package));

            var result = new PackageWithVersionHistory();

            package.ShallowClone(result);

            result.PackageDownloadLink = new Link(Url.Link(RouteNames.Packages.Download, new { id = result.Id, version = result.Version }), "attachment", "Download Package");
            result.VersionHistory = versionHistory.ToArray();
            result.SymbolsAvailable = SymbolSource.AreSymbolsPresentFor(package);

            return result;
        }

        private string GetPackageInfoUrl(LucenePackage pkg)
        {
            return Url.Link(RouteNames.Packages.Info, new { id = pkg.Id, version = pkg.Version });
        }

        /// <summary>
        /// Downloads the complete <c>.nupkg</c> content. The HTTP HEAD method
        /// is also supported for verifying package size, and modification date.
        /// The <c>ETag</c> response header will contain the md5 hash of the
        /// package content.
        /// </summary>
        [HttpGet, HttpHead]
        public HttpResponseMessage DownloadPackage(string id, string version="")
        {
            var packageSpec = new PackageSpec(id, version);
            var package = FindPackage(packageSpec);

            var result = EvaluateCacheHeaders(packageSpec, package);

            if (result != null)
            {
                return result;
            }

            if (Request.Headers.Range != null)
            {
                try
                {
                    HttpResponseMessage partialResponse = Request.CreateResponse(HttpStatusCode.PartialContent);
                    partialResponse.Content = new ByteRangeStreamContent(package.GetStream(), Request.Headers.Range, new MediaTypeWithQualityHeaderValue("application/zip"));
                    return partialResponse;
                }
                catch (InvalidByteRangeException e)
                {
                    return Request.CreateErrorResponse(HttpStatusCode.BadRequest, e);
                }
            }

            result = Request.CreateResponse(HttpStatusCode.OK);
            if (Request.Method == HttpMethod.Get)
            {
                result.Content = new StreamContent(package.GetStream());
                TaskRunner.QueueBackgroundWorkItem(cancellationToken => LuceneRepository.IncrementDownloadCountAsync(package, cancellationToken));
            }
            else
            {
                result.Content = new StringContent(string.Empty);
            }

            result.Headers.ETag = new EntityTagHeaderValue('"' + package.PackageHash + '"');
            result.Content.Headers.ContentType = new MediaTypeWithQualityHeaderValue("application/zip");
            result.Content.Headers.LastModified = package.LastUpdated;
            result.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue(DispositionTypeNames.Attachment)
                {
                    FileName = string.Format("{0}.{1}{2}", package.Id, package.Version, Constants.PackageExtension),
                    Size = package.PackageSize,
                    CreationDate = package.Created,
                    ModificationDate = package.LastUpdated,
                };

            return result;
        }

        private HttpResponseMessage EvaluateCacheHeaders(PackageSpec packageSpec, LucenePackage package)
        {
            if (package == null)
            {
                return Request.CreateErrorResponse(HttpStatusCode.NotFound,
                                                     string.Format("Package {0} version {1} not found.", packageSpec.Id,
                                                                   packageSpec.Version));
            }

            var etagMatch = Request.Headers.IfMatch.Any(etag => !etag.IsWeak && etag.Tag == '"' + package.PackageHash + '"');
            var notModifiedSince = Request.Headers.IfModifiedSince.HasValue &&
                                   Request.Headers.IfModifiedSince >= package.LastUpdated;

            if (etagMatch || notModifiedSince)
            {
                return Request.CreateResponse(HttpStatusCode.NotModified);
            }

            return null;
        }

        /// <summary>
        /// Searches for packages that match <paramref name="query"/>, or if no query
        /// is provided, returns all packages in the repository.
        /// </summary>
        /// <param name="query">Search terms. May include special characters to support prefix,
        /// wildcard or phrase queries.
        /// </param>
        /// <param name="includePrerelease">Specify <c>true</c> to look for pre-release packages.</param>
        /// <param name="latestOnly">Specify <c>true</c> to only search most recent package version or <c>false</c> to search all versions</param>
        /// <param name="offset">Number of results to skip, for pagination.</param>
        /// <param name="count">Number of results to return, for pagination.</param>
        /// <param name="originFilter">Limit result to mirrored or local packages, or both.</param>
        /// <param name="sort">Specify field to sort results on. Score (relevance) is default.</param>
        /// <param name="order">Sort order (default:ascending or descending)</param>
        [HttpGet]
        public dynamic Search(
            string query = "",
            bool includePrerelease = false,
            bool latestOnly = true,
            int offset = 0,
            int count = 20,
            PackageOriginFilter originFilter = PackageOriginFilter.Any,
            SearchSortField sort = SearchSortField.Score,
            SearchSortDirection order = SearchSortDirection.Ascending)
        {
            var criteria = new SearchCriteria(query)
            {
                AllowPrereleaseVersions = includePrerelease,
                PackageOriginFilter = originFilter,
                SortField = sort,
                SortDirection = order
            };

            LuceneQueryStatistics stats = null;
            List<IPackage> hits;

            try
            {
                var queryable = LuceneRepository.Search(criteria).CaptureStatistics(s => stats = s);

                if (latestOnly)
                {
                    queryable = queryable.LatestOnly(includePrerelease);
                }

                hits = queryable.Skip(offset).Take(count).ToList();
            }
            catch (InvalidSearchCriteriaException ex)
            {
                var message = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                return Request.CreateErrorResponse(HttpStatusCode.BadRequest, message);
            }

            dynamic result = new ExpandoObject();

            // criteria
            result.Query = query;
            result.IncludePrerelease = includePrerelease;
            result.TotalHits = stats.TotalHits;
            result.OriginFilter = originFilter;
            result.Sort = sort;
            result.Order = order;

            // statistics
            result.Offset = stats.SkippedHits;
            result.Count = stats.RetrievedDocuments;
            result.ElapsedPreparationTime = stats.ElapsedPreparationTime;
            result.ElapsedSearchTime = stats.ElapsedSearchTime;
            result.ElapsedRetrievalTime = stats.ElapsedRetrievalTime;

            var chars = stats.Query.ToString().Normalize(NormalizationForm.FormD);
            result.ComputedQuery = new string(chars.Where(c => c < 0x7f && !char.IsControl(c)).ToArray());

            // hits
            result.Hits = hits;
            return result;
        }

        /// <summary>
        /// Gets a list of fields that can be searched using the advanced search function.
        /// </summary>
        [HttpGet]
        public IList<string> GetAvailableSearchFieldNames()
        {
            return LuceneRepository.GetAvailableSearchFieldNames().ToList();
        }

        /// <summary>
        /// Permanently delete a package from the repository.
        /// </summary>
        [Authorize(Roles=RoleNames.PackageManager)]
        public async Task<HttpResponseMessage> DeletePackage(string id, string version="")
        {
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
            {
                return Request.CreateErrorResponse(HttpStatusCode.BadRequest, "Must specify package id and version.");
            }

            var package = LuceneRepository.FindPackage(id, new SemanticVersion(version));

            if (package == null)
            {
                var message = string.Format("Package '{0}' version '{1}' not found.", id, version);
                return Request.CreateErrorResponse(HttpStatusCode.NotFound, message);
            }

            Audit("Delete package {0} version {1}", id, version);

            var task1 = LuceneRepository.RemovePackageAsync(package, CancellationToken.None);
            var task2 = SymbolSource.RemoveSymbolsAsync(package);

            await Task.WhenAll(task1, task2);

            return Request.CreateResponse(HttpStatusCode.OK);
        }

        /// <summary>
        /// Upload a package to the repository. If a package already exists
        /// with the same Id and Version, it will be replaced with the new package.
        /// </summary>
        [HttpPut]
        [HttpPost]
        [Authorize(Roles = RoleNames.PackageManager)]
        public async Task<HttpResponseMessage> PutPackage([FromBody]IPackage package)
        {
            if (package == null || string.IsNullOrWhiteSpace(package.Id) || package.Version == null)
            {
                return Request.CreateErrorResponse(HttpStatusCode.BadRequest, "Must provide package with valid id and version.");
            }

            if (package.HasSourceAndSymbols())
            {
                var response = Request.CreateResponse(HttpStatusCode.RedirectKeepVerb);
                response.Headers.Location = new Uri(Url.Link(RouteNames.Symbols.Upload, null), UriKind.RelativeOrAbsolute);
                return response;
            }

            try
            {
                Audit("Add package {0} version {1}", package.Id, package.Version);
                await LuceneRepository.AddPackageAsync(package, CancellationToken.None);
            }
            catch (PackageOverwriteDeniedException ex)
            {
                return Request.CreateErrorResponse(HttpStatusCode.Conflict, ex.Message);
            }

            var location = Url.Link(RouteNames.Packages.Info, new { id = package.Id, version = package.Version });
            var result = Request.CreateResponse(HttpStatusCode.Created);
            result.Headers.Location = new Uri(location);
            return result;
        }

        private LucenePackage FindPackage(PackageSpec packageSpec)
        {
            if (packageSpec.Version == null)
            {
                return FindNewestReleasePackage(packageSpec.Id);
            }

            var package = MirroringRepository.FindPackage(packageSpec.Id, packageSpec.Version);
            return package != null ? LuceneRepository.Convert(package) : null;
        }

        private LucenePackage FindNewestReleasePackage(string packageId)
        {
            return (LucenePackage) LuceneRepository
                    .FindPackagesById(packageId)
                    .Where(p => p.IsReleaseVersion())
                    .OrderByDescending(p => p.Version)
                    .FirstOrDefault();
        }
    }
}
