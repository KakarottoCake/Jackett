using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Jackett.Common.Indexers.Definitions;
using Jackett.Common.Models;
using Jackett.Common;
using Jackett.Common.Utils.Clients;
using Jackett.Test.TestHelpers;
using NLog;
using NUnit.Framework;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using WebRequest = Jackett.Common.Utils.Clients.WebRequest;

// todo: test download block
// todo: test login block
// todo: test settings block
// todo: test other search modes
// todo: review coverage, too many things missing (headers, encoding, ...)
namespace Jackett.Test.Common.Indexers
{
    [TestFixture]
    public class CardigannIndexerJsonTests
    {
        private readonly TestWebClient _webClient = new TestWebClient();
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private readonly TestCacheService _cacheService = new TestCacheService();

        private class UnauthorizedWebClient : TestWebClient
        {
            private readonly string _body;
            public UnauthorizedWebClient(string body) => _body = body;
            public override Task<WebResult> GetResultAsync(WebRequest request) => Task.FromResult(new WebResult
            {
                Request = request,
                Status = HttpStatusCode.Unauthorized,
                ContentBytes = Encoding.UTF8.GetBytes(_body)
            });
        }

        [TestCase("<!doctype html><html><style>large error page</style><body>Unauthorized</body></html>")]
        [TestCase("{\"message\":\"Unauthenticated.\",\"private_detail\":\"do not expose this response\"}")]
        public void UnauthorizedResponseReportsAuthenticationFailureWithoutDumpingResponseBody(string body)
        {
            var indexer = new CardigannIndexer(null, new UnauthorizedWebClient(body), _logger, null,
                _cacheService, LoadTestDefinition("json-definition1.yml"));
            var exception = Assert.ThrowsAsync<IndexerException>(async () =>
                await indexer.ResultsForQuery(new TorznabQuery { QueryType = "search", SearchTerm = "1080p" }, false));
            Assert.That(exception.Message, Does.Contain("Authentication failed (HTTP 401 Unauthorized)"));
            Assert.That(exception.Message, Does.Contain("Configure"));
            Assert.That(exception.Message, Does.Not.Contain(body));
            Assert.That(exception.Message.Length, Is.LessThan(350));
        }

        [Test]
        public async Task TestCardigannJsonAsync()
        {
            _webClient.RegisterRequestCallback("https://jsondefinition1.com/api/torrents/filter?api_token=&name=1080p&sortField=created_at&sortDirection=desc&perPage=100&page=1",
                                               "json-response1.json");
            var definition = LoadTestDefinition("json-definition1.yml");
            var indexer = new CardigannIndexer(null, _webClient, _logger, null, _cacheService, definition);

            var query = new TorznabQuery
            {
                QueryType = "search",
                SearchTerm = "1080p",
            };

            var result = await indexer.ResultsForQuery(query, false);
            Assert.AreEqual(false, result.IsFromCache);

            var releases = result.Releases.ToList();
            Assert.AreEqual(78, releases.Count);

            var firstRelease = releases.First();
            Assert.AreEqual(2, firstRelease.Category.Count);
            Assert.AreEqual(2000, firstRelease.Category.First());
            Assert.AreEqual(100001, firstRelease.Category.Last());
            Assert.AreEqual("The Eyes of Tammy Faye (2021)  BDRip 1080p AVC ES DD+ 5.1 EN DTSSS 5.1 Subs] HDO", firstRelease.Title);
            Assert.AreEqual("https://jsondefinition1.com/torrents/24804", firstRelease.Details.ToString());
            Assert.AreEqual("https://jsondefinition1.com/torrent/download/24804.01c887e14d0845f195bc12b31ea27d38", firstRelease.Link.ToString());
            Assert.AreEqual("https://jsondefinition1.com/torrent/download/24804.01c887e14d0845f195bc12b31ea27d38", firstRelease.Guid.ToString());
            Assert.AreEqual(null, firstRelease.MagnetUri);
            Assert.AreEqual(null, firstRelease.InfoHash);
            Assert.AreEqual("https://image.tmdb.org/t/p/w92/iBjkm6oxTPrvNkzr63cmnrpsQPR.jpg", firstRelease.Poster.ToString());
            Assert.AreEqual(2021, firstRelease.PublishDate.Year);
            Assert.AreEqual(17964744495L, firstRelease.Size);
            Assert.AreEqual(27, firstRelease.Seeders);
            Assert.AreEqual(30, firstRelease.Peers);
            Assert.AreEqual(1, firstRelease.Files);
            Assert.AreEqual(29, firstRelease.Grabs);
            Assert.AreEqual(1, firstRelease.DownloadVolumeFactor);
            Assert.AreEqual(1, firstRelease.UploadVolumeFactor);
            Assert.AreEqual(null, firstRelease.MinimumRatio);
            Assert.AreEqual(345600, firstRelease.MinimumSeedTime);
            Assert.AreEqual(451.7362465756014d, firstRelease.Gain);
            Assert.AreEqual(9115530, firstRelease.Imdb);
            Assert.AreEqual(null, firstRelease.RageID);
            Assert.AreEqual(601470, firstRelease.TMDb);
            Assert.AreEqual(0, firstRelease.TVDBId);
        }

        private static IndexerDefinition LoadTestDefinition(string fileName)
        {
            var definitionString = TestUtil.LoadTestFile(fileName);
            var deserializer = new DeserializerBuilder()
                               .WithNamingConvention(CamelCaseNamingConvention.Instance)
                               .Build();
            return deserializer.Deserialize<IndexerDefinition>(definitionString);
        }
    }
}
