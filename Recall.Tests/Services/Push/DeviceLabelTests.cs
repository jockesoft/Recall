using AwesomeAssertions;
using Recall.Web.Services.Push;

namespace Recall.Tests.Services.Push;

[TestFixture]
public class DeviceLabelTests
{
    [TestCase("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36", "Chrome on Android")]
    [TestCase("Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148", "Safari on iPhone")]
    [TestCase("Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1", "Safari on iPhone")]
    [TestCase("Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1", "Safari on iPad")]
    [TestCase("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15", "Safari on Mac")]
    [TestCase("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0", "Edge on Windows")]
    [TestCase("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:132.0) Gecko/20100101 Firefox/132.0", "Firefox on Windows")]
    [TestCase("Mozilla/5.0 (X11; Linux x86_64; rv:132.0) Gecko/20100101 Firefox/132.0", "Firefox on Linux")]
    [TestCase("Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/26.0 Chrome/122.0.0.0 Mobile Safari/537.36", "Samsung Internet on Android")]
    [TestCase("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 OPR/115.0.0.0", "Opera on Windows")]
    [TestCase("Mozilla/5.0 (X11; CrOS x86_64 14541.0.0) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36", "Chrome on ChromeOS")]
    [TestCase("Mozilla/5.0 (Linux; Android 14) Mystery", "Browser on Android")]
    [TestCase("curl/8.4.0", "Unknown browser")]
    [TestCase("", "Unknown browser")]
    [TestCase(null, "Unknown browser")]
    public void FromUserAgent_Should_NameTheBrowserAndTheSystem(string? userAgent, string label)
    {
        DeviceLabel.FromUserAgent(userAgent).Should().Be(label);
    }
}
