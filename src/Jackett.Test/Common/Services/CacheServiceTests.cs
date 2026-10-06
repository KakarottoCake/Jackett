using System;
using System.Collections.Generic;
using System.Linq;
using Jackett.Common.Models;
using Jackett.Common.Models.Config;
using Jackett.Common.Services;
using Jackett.Test.TestHelpers;
using NLog;
using NUnit.Framework;

namespace Jackett.Test.Common.Services
{
    [TestFixture]
    public class CacheServiceTests
    {
        private ServerConfig _config;
        private CacheService _cache;
        private TestWebIndexer _indexer;

        [SetUp]
        public void Setup()
        {
            _config = new ServerConfig(new RuntimeSettings()) { CacheMaxResultsPerIndexer = 3 };
            _cache = new CacheService(LogManager.GetCurrentClassLogger(), _config);
            _indexer = new TestWebIndexer(LogManager.GetCurrentClassLogger());
        }

        private static TorznabQuery Query(string term = "test") => new TorznabQuery { QueryType = "search", SearchTerm = term };
        private static List<ReleaseInfo> Releases(int count = 1) => Enumerable.Range(0, count).Select(i => new ReleaseInfo
        {
            Guid = new Uri("https://example.org/" + i), Link = new Uri("https://example.org/download/" + i),
            Title = "original", Category = new List<int> { 5000 }
        }).ToList();

        [Test]
        public void CacheCopiesInputAndReturnsIndependentResponses()
        {
            var releases = Releases();
            _cache.CacheResults(_indexer, Query(), releases);
            releases[0].Title = "changed input";
            releases[0].Category.Add(2000);
            var response = _cache.Search(_indexer, Query());
            response[0].Link = new Uri("https://proxy.example/modified");
            response[0].Category.Clear();
            response.Clear();
            var next = _cache.Search(_indexer, Query());
            Assert.That(next.Single().Title, Is.EqualTo("original"));
            Assert.That(next.Single().Link.AbsoluteUri, Is.EqualTo("https://example.org/download/0"));
            Assert.That(next.Single().Category, Is.EqualTo(new[] { 5000 }));
        }

        [Test]
        public void TtlChangesAreHonoredImmediately()
        {
            _cache.CacheResults(_indexer, Query(), Releases());
            _config.CacheTtl = -1;
            Assert.That(_cache.Search(_indexer, Query()), Is.Null);
        }

        [Test]
        public void DisablingCacheClearsOldEntries()
        {
            _cache.CacheResults(_indexer, Query(), Releases());
            _config.CacheEnabled = false;
            Assert.That(_cache.Search(_indexer, Query()), Is.Null);
            _config.CacheEnabled = true;
            Assert.That(_cache.Search(_indexer, Query()), Is.Null);
        }

        [Test]
        public void TestQueriesAreNeverCached()
        {
            var query = Query();
            query.IsTest = true;
            _cache.CacheResults(_indexer, query, Releases());
            Assert.That(_cache.Search(_indexer, query), Is.Null);
        }

        [Test]
        public void NullAndEmptySearchTermsShareCacheButCategoriesAndOffsetsDoNot()
        {
            _cache.CacheResults(_indexer, Query(null), Releases());
            Assert.That(_cache.Search(_indexer, Query("")), Has.Count.EqualTo(1));
            var different = Query("");
            different.Offset = 10;
            Assert.That(_cache.Search(_indexer, different), Is.Null);
            different.Offset = 0;
            different.Categories = new[] { 5000 };
            Assert.That(_cache.Search(_indexer, different), Is.Null);
        }

        [Test]
        public void EvictionRespectsReleaseBudgetAndKeepsNewerQuery()
        {
            _cache.CacheResults(_indexer, Query("first"), Releases(2));
            _cache.CacheResults(_indexer, Query("second"), Releases(2));
            Assert.That(_cache.Search(_indexer, Query("first")), Is.Null);
            Assert.That(_cache.Search(_indexer, Query("second")), Has.Count.EqualTo(2));
        }

        [Test]
        public void EmptyQueriesCannotGrowCacheWithoutBound()
        {
            for (var i = 0; i < 20; i++)
                _cache.CacheResults(_indexer, Query(i.ToString()), new List<ReleaseInfo>());
            var hits = Enumerable.Range(0, 20).Count(i => _cache.Search(_indexer, Query(i.ToString())) != null);
            Assert.That(hits, Is.EqualTo(3));
        }

        [Test]
        public void ClearingIndexerAndGlobalCacheInvalidatesResults()
        {
            _cache.CacheResults(_indexer, Query(), Releases());
            _cache.CleanIndexerCache(_indexer);
            Assert.That(_cache.Search(_indexer, Query()), Is.Null);
            _cache.CacheResults(_indexer, Query(), Releases());
            _cache.CleanCache();
            Assert.That(_cache.Search(_indexer, Query()), Is.Null);
        }
    }
}
