using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jackett.Common;
using Jackett.Common.Exceptions;
using Jackett.Common.Indexers;
using Jackett.Common.Models;
using Jackett.Common.Models.Config;
using Jackett.Common.Models.IndexerConfig;
using Jackett.Common.Services;
using Jackett.Common.Services.Interfaces;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;

namespace Jackett.Test.Common.Indexers
{
    [TestFixture]
    public class ConcurrentQueryTests
    {
        private class ControlledIndexer : BaseIndexer
        {
            public int Calls;
            public TaskCompletionSource<bool> Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Fail;
            public override TorznabCapabilities TorznabCaps { get; protected set; } = new() { SearchAvailable = true };
            public ControlledIndexer(ICacheService cache)
                : base(null, LogManager.GetCurrentClassLogger(), null, null, cache)
            {
                Id = "controlled";
                Name = "Controlled";
            }
            protected override async Task<IEnumerable<ReleaseInfo>> PerformQuery(TorznabQuery query)
            {
                Interlocked.Increment(ref Calls);
                await Gate.Task;
                if (Fail)
                    throw new InvalidOperationException("expected failure");
                return new[] { new ReleaseInfo { Guid = new Uri("https://example.org/release"), Title = query.SearchTerm } };
            }
            protected override IEnumerable<ReleaseInfo> FilterResults(TorznabQuery query, IEnumerable<ReleaseInfo> results) => results;
            protected override IEnumerable<ReleaseInfo> FixResults(TorznabQuery query, IEnumerable<ReleaseInfo> results) => results;
            public override Task<IndexerConfigurationStatus> ApplyConfiguration(JToken configJson) => throw new NotSupportedException();
            public override IIndexerRequestGenerator GetRequestGenerator() => throw new NotSupportedException();
            public override IParseIndexerResponse GetParser() => throw new NotSupportedException();
        }

        private static ControlledIndexer Create() => new(new CacheService(LogManager.GetCurrentClassLogger(), new ServerConfig(new RuntimeSettings())));
        private static TorznabQuery Query(string term = "test") => new() { QueryType = "search", SearchTerm = term };

        [Test]
        public async Task IdenticalSimultaneousRequestsUseOneTrackerCallAndIndependentResponses()
        {
            var indexer = Create();
            var requests = Enumerable.Range(0, 20).Select(_ => indexer.ResultsForQuery(Query(), false)).ToArray();
            Assert.That(indexer.Calls, Is.EqualTo(1));
            indexer.Gate.SetResult(true);
            var responses = await Task.WhenAll(requests);
            responses[0].Releases.First().Title = "modified";
            Assert.That(responses.Skip(1).All(r => r.Releases.First().Title == "test"), Is.True);
            var cached = await indexer.ResultsForQuery(Query(), false);
            Assert.That(cached.IsFromCache, Is.True);
            Assert.That(cached.Releases.First().Title, Is.EqualTo("test"));
            Assert.That(indexer.Calls, Is.EqualTo(1));
        }

        [Test]
        public async Task DifferentQueriesRunIndependently()
        {
            var indexer = Create();
            var first = indexer.ResultsForQuery(Query("first"), false);
            var second = indexer.ResultsForQuery(Query("second"), false);
            Assert.That(indexer.Calls, Is.EqualTo(2));
            indexer.Gate.SetResult(true);
            Assert.That((await first).Releases.First().Title, Is.EqualTo("first"));
            Assert.That((await second).Releases.First().Title, Is.EqualTo("second"));
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task TestAndCacheBypassQueriesAlwaysMakeSeparateRequests(bool test)
        {
            var indexer = Create();
            var query = Query();
            query.IsTest = test;
            query.Cache = test;
            var first = indexer.ResultsForQuery(query, false);
            var second = indexer.ResultsForQuery(query, false);
            Assert.That(indexer.Calls, Is.EqualTo(2));
            indexer.Gate.SetResult(true);
            await Task.WhenAll(first, second);
        }

        [Test]
        public async Task FailedSharedRequestIsRemovedAndCanBeRetried()
        {
            var indexer = Create();
            indexer.Fail = true;
            var first = indexer.ResultsForQuery(Query(), false);
            var second = indexer.ResultsForQuery(Query(), false);
            indexer.Gate.SetResult(true);
            Assert.ThrowsAsync<IndexerException>(async () => await first);
            Assert.ThrowsAsync<IndexerException>(async () => await second);
            Assert.That(indexer.Calls, Is.EqualTo(1));
            indexer.Fail = false;
            var retry = await indexer.ResultsForQuery(Query(), false);
            Assert.That(retry.Releases.Count(), Is.EqualTo(1));
            Assert.That(indexer.Calls, Is.EqualTo(2));
        }
    }
}
