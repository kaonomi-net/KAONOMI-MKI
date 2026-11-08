using System.Collections.Concurrent;

namespace KAONOMI.Agent.WebScrapper.Scraping
{
    /// <summary>
    /// The rules of the robots.txt of the sites, read once per site and kept: the Allow / Disallow lines of the groups
    /// for every robot (<c>User-agent: *</c>) or for this scraper; the longest matching rule wins, Allow on a tie. A site
    /// whose robots.txt cannot be read allows everything.
    /// </summary>
    public sealed class RobotsTxt
    {
        private readonly HttpClient _http;
        private readonly string _agentToken;
        private readonly ConcurrentDictionary<string, List<(bool Allow, string Path)>> _rules = new(StringComparer.OrdinalIgnoreCase);

        public RobotsTxt(HttpClient http, string agentToken = "kaonomiwebscrapper")
        {
            _http = http;
            _agentToken = agentToken.ToLowerInvariant();
        }

        public async Task<bool> IsAllowedAsync(Uri url, CancellationToken cancellationToken)
        {
            var site = url.GetLeftPart(UriPartial.Authority);
            if (!_rules.TryGetValue(site, out var rules))
            {
                rules = await LoadAsync(site, cancellationToken);
                _rules[site] = rules;
            }

            var path = url.PathAndQuery;
            (bool Allow, string Path)? best = null;
            foreach (var rule in rules)
            {
                if (rule.Path.Length == 0 || !path.StartsWith(rule.Path, StringComparison.Ordinal))
                    continue;
                if (best is null || rule.Path.Length > best.Value.Path.Length || (rule.Path.Length == best.Value.Path.Length && rule.Allow))
                    best = rule;
            }
            return best?.Allow ?? true;
        }

        private async Task<List<(bool, string)>> LoadAsync(string site, CancellationToken cancellationToken)
        {
            var rules = new List<(bool, string)>();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var response = await _http.GetAsync(site + "/robots.txt", timeout.Token);
                if (!response.IsSuccessStatusCode)
                    return rules;
                var text = await response.Content.ReadAsStringAsync(timeout.Token);

                var agents = new List<string>();
                var inRules = false;
                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.Split('#')[0].Trim();
                    var colon = line.IndexOf(':');
                    if (colon <= 0)
                        continue;
                    var key = line[..colon].Trim().ToLowerInvariant();
                    var value = line[(colon + 1)..].Trim();
                    if (key == "user-agent")
                    {
                        if (inRules)
                        {
                            agents.Clear();
                            inRules = false;
                        }
                        agents.Add(value.ToLowerInvariant());
                    }
                    else if (key is "allow" or "disallow")
                    {
                        inRules = true;
                        if (agents.Any(a => a == "*" || _agentToken.Contains(a)))
                            rules.Add((key == "allow", value));
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                // Unreadable robots.txt: everything is allowed.
            }
            return rules;
        }
    }
}
