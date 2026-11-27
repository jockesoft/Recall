using AwesomeAssertions;
using Recall.Web.Services.Metrics;

namespace Recall.Tests.Services.Metrics;

[TestFixture]
public sealed class CrawlerUserAgentsTests
{
    [TestCase("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)")]
    [TestCase("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm) Chrome/116.0.1938.76 Safari/537.36")]
    [TestCase("DuckDuckBot/1.1; (+http://duckduckgo.com/duckduckbot.html)")]
    [TestCase("Mozilla/5.0 (compatible; YandexBot/3.0; +http://yandex.com/bots)")]
    [TestCase("facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)")]
    [TestCase("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; GPTBot/1.2; +https://openai.com/gptbot)")]
    [TestCase("Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko; compatible; ClaudeBot/1.0; +claudebot@anthropic.com)")]
    public void AKnownCrawler_Should_BeRecognised(string userAgent) =>
        CrawlerUserAgents.IsCrawler(userAgent).Should().BeTrue();

    [TestCase("Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1")]
    [TestCase("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0")]
    [TestCase("Mozilla/5.0 (X11; Linux x86_64; rv:133.0) Gecko/20100101 Firefox/133.0")]
    [TestCase("")]
    [TestCase(null)]
    public void APersonsBrowser_Should_NotBeACrawler(string? userAgent) =>
        CrawlerUserAgents.IsCrawler(userAgent).Should().BeFalse();
}
