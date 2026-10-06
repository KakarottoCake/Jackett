# Jackett Optimized

This fork keeps the existing Jackett API routes and response formats. Its dashboard loads configured indexers first, retrieves the full catalog only when Add indexer is opened, and uses a responsive dark interface with collapsible settings and connection instructions.

## Search and resource changes

- Identical simultaneous cache-enabled searches share one in-flight tracker query per indexer. Different queries still run independently. Tests and explicit cache bypasses make separate live requests.
- Cache lookups check the requested entry's expiration immediately. A global cleanup runs at most once per minute during cache activity, instead of scanning every tracker on every lookup.
- Eviction computes the release total once and sorts only when over budget. Empty searches have a query-count limit as well, preventing unbounded cache growth.
- Responses receive independent release objects and collection fields, so proxy-link rewriting cannot corrupt cached results or another client's response.
- Versioned JavaScript and CSS can be reused from the browser cache. Scripts defer execution until the document is parsed.
- Release labels and tooltips initialize for the displayed table page. Search preset buttons replace their existing click handler instead of accumulating handlers during redraws.

Live searches still depend on tracker response times and use network, CPU and memory. Cached searches avoid tracker requests; this fork does not promise zero resource usage or eliminate delays imposed by remote trackers.

## Verification on the existing home server

October 6, 2026, Ubuntu x86-64, approximately 8 GB RAM, nine configured indexers:

| Measurement | Original | Optimized |
| --- | --- | --- |
| Indexer data fetched when opening dashboard | 1,535,689 bytes | 43,892 bytes |
| Configured-indexer API response, median of five warm requests | 40.52 ms | 6.43 ms |
| Nyaa TV query returning 80 releases | — | 2,697 ms cold; 8.29–8.94 ms cached |
| Idle Docker CPU sample | 0.01% | 0.03% |
| Docker memory sample | 149.8 MiB | 119.6 MiB |

Samples describe this server and workload, not guarantees. The two search timings compare cold and cached requests, rather than proving a speedup over the original cache. Memory samples were taken at different points in the services' lifetimes.

All 263 .NET 9 regression tests passed, including 13 new tests for cache isolation, expiry, eviction, empty-query limits, query separation, concurrent request sharing, explicit bypasses and failure retries. JavaScript syntax checks and Git whitespace checks passed.

The deployed instance passed health, login, dashboard, configured-indexer, capabilities, legacy Torznab route, cache display, catalog and manual search checks. Sonarr Pro parsed 230 releases from its existing integrations. FileList, Nyaa and TorrentLeech indexer tests passed after deployment. Blutopia failed both before and after deployment because its upstream API returned HTTP 401 Unauthorized for its configured credentials; a working Blutopia API token is needed to resolve that failure.

## Docker build and deployment

Build on the Linux x86-64 Docker host. Pin `JACKETT_BASE` to the existing LinuxServer Jackett image or a locally tagged copy. The build runs the regression tests before publishing a self-contained .NET 9 application and overlaying it on that image.

```sh
docker build -f Dockerfile.optimized \
  --build-arg JACKETT_BASE=jackett-original:pre-optimized \
  --build-arg JACKETT_VERSION=0.24.2793 \
  -t jackett-optimized:local .
sudo python3 deploy/reuse-container.py jackett jackett-optimized:local
```

The deployment helper is configured for this home server's backup directory. It backs up the configuration privately, stops and retains the original container, and creates its replacement from the original Docker configuration. Bind mounts, environment settings, published ports, restart policy and network mode carry over. The existing API key and Jackett configuration remain unchanged.

The replacement disables upstream auto-updates and excludes this container from Watchtower so they cannot replace the fork. Update this image by rebuilding the fork and redeploying it. The helper restarts the original container automatically if creation, startup or the health check fails.

For manual rollback, stop the optimized `jackett`, rename it to an unused name, rename the retained `jackett-pre-optimized-<timestamp>` container to `jackett`, and start it. The private backup directory contains the original container inspection and configuration archive. Keep both stopped containers from running together against the shared configuration volume.
